using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI;
using VRage;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Plugins;
using VRageMath;

namespace Oreo.TargetShieldHud
{
    /// <summary>
    /// Pulsar client plugin. Shows the local player's WeaponCore focus, live
    /// Defense Shields values, outgoing WC damage, and a capped set of nearby
    /// enemy shield bars. Only maximum observed NPC shield capacity is saved.
    /// </summary>
    public sealed class Plugin : IPlugin, IDisposable
    {
        private const int PollFrames = 12;        // Selected target: about 5 reads/second.
        private const int ThreatPollFrames = 30; // Nearby bars: about 2 reads/second.
        private const int ApiRetryFrames = 300;  // Re-request missing APIs every ~5 seconds.
        private const int SaveFrames = 600;      // At most one local save every ~10 seconds.
        private const int MaxEnemyBars = 8;
        private const int MaxDamageTargets = 128;
        private const double EnemyBarRange = 15000d;
        private const long DamageMonitorId = 675497565490103L;
        private static readonly char[] ShieldBarSteps =
            { '_', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        private readonly StringBuilder hudText = new StringBuilder(480);
        private readonly RecordStore records = new RecordStore();
        private readonly List<MyTuple<MyEntity, float>> threats =
            new List<MyTuple<MyEntity, float>>(32);
        private readonly Dictionary<long, EnemyShieldBar> enemyBars =
            new Dictionary<long, EnemyShieldBar>();
        private readonly Dictionary<long, TargetDamageRecord> damageByTarget =
            new Dictionary<long, TargetDamageRecord>();
        private readonly List<long> staleBarIds = new List<long>(16);
        private readonly List<EnemyShieldSample> shieldSamples =
            new List<EnemyShieldSample>(MaxEnemyBars);
        private readonly Action<ListReader<MyTuple<ulong, long, int, MyEntity, MyEntity,
            ListReader<MyTuple<Vector3D, object, float>>>>> damageCallback;

        private WeaponCoreApi weaponCore;
        private DefenseShieldsApi defenseShields;
        private TextHudBridge textHud;
        private HudText hudLine;
        private bool sessionLoaded;
        private bool damageMonitorActive;
        private int frame;
        private int lastSaveFrame;
        private long shooterGridId;
        private long currentTargetId;
        private string currentTargetName = string.Empty;
        private TargetDamageRecord currentDamage;
        private double currentMaxSeen;

        public Plugin()
        {
            damageCallback = OnDamageEvents;
        }

        public void Init(object gameInstance)
        {
            // Game ModAPI services are not guaranteed to exist here. Session setup is
            // deferred until Update sees a live game session.
        }

        public void Update()
        {
            try
            {
                bool hasSession = MyAPIGateway.Session != null && MyAPIGateway.Utilities != null;
                if (hasSession && !sessionLoaded)
                    StartSession();
                else if (!hasSession && sessionLoaded)
                    StopSession();

                if (!sessionLoaded)
                    return;

                frame++;
                EnsureHudLine();

                if (frame % ApiRetryFrames == 0)
                {
                    weaponCore.Request();
                    defenseShields.Request();
                }

                if (frame % PollFrames == 0)
                    RefreshTarget();

                if (frame % ThreatPollFrames == 0)
                    RefreshEnemyBars();

                if (records.Enabled && records.EnemyBars)
                    UpdateEnemyBarPositions();

                if (records.Dirty && frame - lastSaveFrame >= SaveFrames)
                {
                    records.Save();
                    lastSaveFrame = frame;
                }
            }
            catch (Exception error)
            {
                // Never allow a broken HUD read to interrupt the game's simulation loop.
                if (frame % ApiRetryFrames == 0 && MyAPIGateway.Utilities != null)
                    MyAPIGateway.Utilities.ShowNotification(
                        "Oreo Shield HUD: " + error.GetType().Name, 3000, "Red");
            }
        }

        private void StartSession()
        {
            frame = 0;
            lastSaveFrame = 0;
            records.Load();
            weaponCore = new WeaponCoreApi();
            defenseShields = new DefenseShieldsApi();
            textHud = new TextHudBridge();
            textHud.Ready += EnsureHudLine;
            weaponCore.Load();
            defenseShields.Load();
            MyAPIGateway.Utilities.MessageEntered += OnChatMessage;
            sessionLoaded = true;
        }

        private void StopSession()
        {
            SetDamageMonitoring(false);
            ClearEnemyBars();
            if (records.Dirty && MyAPIGateway.Utilities != null)
                records.Save();
            if (MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.MessageEntered -= OnChatMessage;
            if (hudLine != null) hudLine.Dispose();
            if (textHud != null) textHud.Dispose();
            if (defenseShields != null) defenseShields.Dispose();
            if (weaponCore != null) weaponCore.Dispose();
            hudLine = null;
            textHud = null;
            defenseShields = null;
            weaponCore = null;
            hudText.Clear();
            ClearCurrentTarget(false);
            damageByTarget.Clear();
            sessionLoaded = false;
        }

        private void EnsureHudLine()
        {
            if (hudLine != null || textHud == null || !textHud.IsReady)
                return;
            hudLine = textHud.CreateText(hudText,
                new Vector2D(records.X, records.Y), records.Scale);
            if (hudLine != null)
                hudLine.SetVisible(records.Enabled);
        }

        private void RefreshTarget()
        {
            hudText.Clear();

            if (!records.Enabled)
            {
                ClearCurrentTarget(true);
                if (hudLine != null) hudLine.SetVisible(false);
                return;
            }
            if (hudLine != null) hudLine.SetVisible(true);

            if (!weaponCore.IsReady || !defenseShields.IsReady)
            {
                ClearCurrentTarget(true);
                hudText.Append("<color=0,220,255>OREO TARGET SHIELD<color=255,255,255>\n");
                hudText.Append("Waiting for ");
                if (!weaponCore.IsReady) hudText.Append("WeaponCore ");
                if (!defenseShields.IsReady) hudText.Append("Defense Shields");
                return;
            }

            MyEntity controlled = MyAPIGateway.Session.ControlledObject as MyEntity;
            if (controlled == null)
            {
                ClearCurrentTarget(true);
                hudText.Append("<color=90,90,90>OREO TARGET SHIELD - enter a cockpit<color=255,255,255>");
                return;
            }

            MyEntity shooter = controlled.GetTopMostParent() ?? controlled;
            MyEntity target = weaponCore.GetAiFocus(shooter, 0);
            if (target == null || target.MarkedForClose)
            {
                ClearCurrentTarget(true);
                hudText.Append("<color=90,90,90>OREO TARGET SHIELD - no WC focus<color=255,255,255>");
                return;
            }
            target = target.GetTopMostParent() ?? target;

            string targetName = CleanHudText(target.DisplayName);
            if (string.IsNullOrWhiteSpace(targetName))
                targetName = "Entity " + target.EntityId;

            IMyTerminalBlock shield = defenseShields.FindShield(target);
            SetCurrentTarget(shooter, target, targetName);

            double distance = Vector3D.Distance(shooter.PositionComp.WorldAABB.Center,
                target.PositionComp.WorldAABB.Center);
            hudText.Append("<color=0,220,255>TARGET:<color=255,255,255> ")
                .Append(currentTargetName).Append("  <color=150,150,150>")
                .Append(FormatDistance(distance)).Append("<color=255,255,255>\n");

            if (shield == null)
            {
                hudText.Append("<color=150,150,150>SHIELD: none detected<color=255,255,255>");
                AppendDamageLine();
                return;
            }

            // Defense Shields exposes charge in 1/100 HP units; multiply by 100 to
            // match the HP shown by Defense Shields and existing PB integrations.
            double currentHp = Math.Max(0, defenseShields.GetCurrentCharge(shield) * 100d);
            double maximumHp = Math.Max(0, defenseShields.GetMaximumCharge(shield) * 100d);
            double percent = defenseShields.GetPercent(shield);
            if (maximumHp <= 0)
            {
                hudText.Append("<color=150,150,150>SHIELD: API returned no capacity<color=255,255,255>");
                AppendDamageLine();
                return;
            }
            if (percent < 0 || double.IsNaN(percent) || double.IsInfinity(percent))
                percent = 100d * currentHp / maximumHp;
            percent = ClampPercent(percent);

            currentMaxSeen = records.Observe(currentTargetName, maximumHp);
            string color = PercentColor(percent);
            hudText.Append("<color=0,220,255>SHIELD:<color=255,255,255> ")
                .Append(FormatNumber(currentHp)).Append(" / ")
                .Append(FormatNumber(maximumHp)).Append(" HP  ")
                .Append("<color=").Append(color).Append(">")
                .Append(percent.ToString("0.0", CultureInfo.InvariantCulture))
                .Append("%<color=255,255,255>\n")
                .Append(BuildBar(percent, color));

            if (currentMaxSeen > 0)
                hudText.Append("  <color=0,220,255>MAX SEEN:<color=255,255,255> ")
                    .Append(FormatNumber(currentMaxSeen));

            AppendDamageLine();
        }

        private void SetCurrentTarget(MyEntity shooter, MyEntity target, string name)
        {
            long newShooterId = shooter == null ? 0 : shooter.EntityId;
            long newTargetId = target == null ? 0 : target.EntityId;
            shooterGridId = newShooterId;
            currentTargetId = newTargetId;
            currentTargetName = name ?? string.Empty;
            currentDamage = GetOrCreateDamageRecord(currentTargetId, currentTargetName);
            currentMaxSeen = records.GetMaximumSeen(currentTargetName);
            SetDamageMonitoring(currentTargetId != 0);
        }

        private void ClearCurrentTarget(bool stopDamageMonitor)
        {
            if (stopDamageMonitor)
                SetDamageMonitoring(false);
            shooterGridId = 0;
            currentTargetId = 0;
            currentTargetName = string.Empty;
            currentDamage = null;
            currentMaxSeen = 0;
        }

        private void AppendDamageLine()
        {
            double shieldDamage = currentDamage == null ? 0 : currentDamage.Shield;
            double hullDamage = currentDamage == null ? 0 : currentDamage.Hull;
            double total = shieldDamage + hullDamage;
            hudText.Append("\n<color=0,220,255>DAMAGE DEALT:<color=255,255,255> ")
                .Append(FormatNumber(total))
                .Append("  <color=80,170,255>SHIELD:<color=255,255,255> ")
                .Append(FormatNumber(shieldDamage))
                .Append("  <color=255,170,60>HULL:<color=255,255,255> ")
                .Append(FormatNumber(hullDamage));
        }

        private void SetDamageMonitoring(bool enabled)
        {
            enabled = enabled && records.Enabled && weaponCore != null &&
                weaponCore.CanReadDamage && currentTargetId != 0;
            if (enabled == damageMonitorActive)
                return;

            try
            {
                if (weaponCore != null && weaponCore.CanReadDamage)
                    weaponCore.SetDamageMonitor(DamageMonitorId, enabled, damageCallback);
                damageMonitorActive = enabled;
            }
            catch
            {
                damageMonitorActive = false;
            }
        }

        private void OnDamageEvents(ListReader<MyTuple<ulong, long, int, MyEntity, MyEntity,
            ListReader<MyTuple<Vector3D, object, float>>>> events)
        {
            long shooterId = shooterGridId;
            if (!damageMonitorActive || shooterId == 0)
                return;

            long localPlayerId = 0;
            if (MyAPIGateway.Session != null && MyAPIGateway.Session.Player != null)
                localPlayerId = MyAPIGateway.Session.Player.IdentityId;

            foreach (var projectile in events)
            {
                // WC provides both the firing player's identity and weapon entities.
                // WeaponParent can be WC's construct root rather than the grid entity
                // returned by ControlledObject, so accept either ownership signal.
                bool localPlayerWeapon = localPlayerId != 0 &&
                    projectile.Item2 == localPlayerId;
                bool localGridWeapon = SameTopEntity(projectile.Item5, shooterId) ||
                    SameTopEntity(projectile.Item4, shooterId);
                if (!localPlayerWeapon && !localGridWeapon)
                    continue;

                foreach (var hit in projectile.Item6)
                {
                    float damage = hit.Item3;
                    if (damage <= 0 || float.IsNaN(damage) || float.IsInfinity(damage))
                        continue;

                    var block = hit.Item2 as IMySlimBlock;
                    if (block != null)
                    {
                        MyEntity hitGrid = block.CubeGrid as MyEntity;
                        MyEntity hitTop = hitGrid == null ? null :
                            (hitGrid.GetTopMostParent() ?? hitGrid);
                        TargetDamageRecord record;
                        if (hitTop != null &&
                            damageByTarget.TryGetValue(hitTop.EntityId, out record))
                        {
                            record.Hull += damage;
                            record.LastSeenFrame = frame;
                        }
                        continue;
                    }

                    var entity = hit.Item2 as MyEntity;
                    if (entity == null)
                        continue;
                    MyEntity hitEntity = entity.GetTopMostParent() ?? entity;
                    TargetDamageRecord shieldRecord;
                    if (damageByTarget.TryGetValue(hitEntity.EntityId, out shieldRecord))
                    {
                        // WC reports shield impacts as entity hits and block impacts as
                        // IMySlimBlock, so this remains accurate after focus changes.
                        shieldRecord.Shield += damage;
                        shieldRecord.LastSeenFrame = frame;
                    }
                }
            }
        }

        private TargetDamageRecord GetOrCreateDamageRecord(long entityId, string name)
        {
            if (entityId == 0)
                return null;

            TargetDamageRecord record;
            if (!damageByTarget.TryGetValue(entityId, out record))
            {
                if (damageByTarget.Count >= MaxDamageTargets)
                {
                    long oldestId = 0;
                    int oldestFrame = int.MaxValue;
                    foreach (KeyValuePair<long, TargetDamageRecord> item in damageByTarget)
                    {
                        if (item.Key != currentTargetId && item.Value.LastSeenFrame < oldestFrame)
                        {
                            oldestId = item.Key;
                            oldestFrame = item.Value.LastSeenFrame;
                        }
                    }
                    if (oldestId != 0)
                        damageByTarget.Remove(oldestId);
                }

                record = new TargetDamageRecord();
                damageByTarget[entityId] = record;
            }
            record.Name = name ?? string.Empty;
            record.LastSeenFrame = frame;
            return record;
        }

        private void RefreshEnemyBars()
        {
            if (!records.Enabled || !records.EnemyBars || textHud == null ||
                !textHud.IsReady || !weaponCore.IsReady || !weaponCore.CanReadThreats ||
                !defenseShields.IsReady)
            {
                ClearEnemyBars();
                return;
            }

            MyEntity controlled = MyAPIGateway.Session.ControlledObject as MyEntity;
            if (controlled == null)
            {
                ClearEnemyBars();
                return;
            }

            MyEntity shooter = controlled.GetTopMostParent() ?? controlled;
            Vector3D shooterPosition = shooter.PositionComp.WorldAABB.Center;
            double maxDistanceSquared = EnemyBarRange * EnemyBarRange;
            threats.Clear();
            shieldSamples.Clear();
            weaponCore.GetSortedThreats(shooter, threats);

            foreach (var threat in threats)
            {
                if (shieldSamples.Count >= MaxEnemyBars)
                    break;

                MyEntity target = threat.Item1;
                if (target == null || target.MarkedForClose)
                    continue;
                target = target.GetTopMostParent() ?? target;
                if (target.EntityId == shooter.EntityId ||
                    target.EntityId == currentTargetId ||
                    Vector3D.DistanceSquared(shooterPosition,
                        target.PositionComp.WorldAABB.Center) > maxDistanceSquared)
                    continue;

                IMyTerminalBlock shield = defenseShields.FindShield(target);
                if (shield == null)
                    continue;

                double currentHp = Math.Max(0, defenseShields.GetCurrentCharge(shield) * 100d);
                double maximumHp = Math.Max(0, defenseShields.GetMaximumCharge(shield) * 100d);
                if (maximumHp <= 0)
                    continue;

                double percent = defenseShields.GetPercent(shield);
                if (percent < 0 || double.IsNaN(percent) || double.IsInfinity(percent))
                    percent = 100d * currentHp / maximumHp;
                percent = ClampPercent(percent);

                string name = CleanHudText(target.DisplayName);
                if (string.IsNullOrWhiteSpace(name))
                    name = "Entity " + target.EntityId;
                records.Observe(name, maximumHp);

                shieldSamples.Add(new EnemyShieldSample
                {
                    Entity = target,
                    Name = name,
                    CurrentHp = currentHp,
                    MaximumHp = maximumHp,
                    Percent = percent
                });
            }

            double largestMaximumHp = 0;
            double smallestMaximumHp = double.MaxValue;
            foreach (EnemyShieldSample sample in shieldSamples)
            {
                if (sample.MaximumHp > largestMaximumHp)
                    largestMaximumHp = sample.MaximumHp;
                if (sample.MaximumHp > 0 && sample.MaximumHp < smallestMaximumHp)
                    smallestMaximumHp = sample.MaximumHp;
            }
            if (smallestMaximumHp == double.MaxValue)
                smallestMaximumHp = 0;

            foreach (EnemyShieldSample sample in shieldSamples)
            {
                MyEntity target = sample.Entity;
                string name = sample.Name;
                double currentHp = sample.CurrentHp;
                double maximumHp = sample.MaximumHp;
                double percent = sample.Percent;

                EnemyShieldBar bar;
                if (!enemyBars.TryGetValue(target.EntityId, out bar))
                {
                    bar = new EnemyShieldBar(target);
                    bar.Line = textHud.CreateText(bar.Text, Vector2D.Zero, EnemyBarScale());
                    enemyBars[target.EntityId] = bar;
                }
                else
                {
                    bar.Entity = target;
                    if (bar.Line == null)
                        bar.Line = textHud.CreateText(bar.Text, Vector2D.Zero, EnemyBarScale());
                }

                string color = PercentColor(percent);
                int barWidth = records.MinimalEnemyBars
                    ? LogarithmicBarWidth(maximumHp, smallestMaximumHp,
                        largestMaximumHp, 4, 8)
                    : LogarithmicBarWidth(maximumHp, smallestMaximumHp,
                        largestMaximumHp, 6, 14);
                bar.Text.Clear();
                if (records.MinimalEnemyBars)
                {
                    bar.Text.Append("<color=255,255,255>")
                        .Append(CompactTargetName(name)).Append("  ")
                        .Append(BuildShieldBar(percent, color, barWidth)).Append("  ")
                        .Append("<color=").Append(color).Append(">")
                        .Append(percent.ToString("0", CultureInfo.InvariantCulture))
                        .Append("%<color=255,255,255>");
                }
                else
                {
                    bar.Text.Append("<color=255,255,255>").Append(name).Append("  ")
                        .Append("<color=").Append(color).Append(">")
                        .Append(percent.ToString("0", CultureInfo.InvariantCulture))
                        .Append("%<color=255,255,255>\n")
                        .Append(BuildShieldBar(percent, color, barWidth)).Append("  ")
                        .Append(FormatNumber(currentHp)).Append(" / ")
                        .Append(FormatNumber(maximumHp)).Append(" HP");
                }
                bar.LastSeenFrame = frame;
            }

            staleBarIds.Clear();
            foreach (KeyValuePair<long, EnemyShieldBar> item in enemyBars)
            {
                if (item.Value.LastSeenFrame != frame)
                    staleBarIds.Add(item.Key);
            }
            foreach (long id in staleBarIds)
            {
                EnemyShieldBar bar;
                if (enemyBars.TryGetValue(id, out bar) && bar.Line != null)
                    bar.Line.Dispose();
                enemyBars.Remove(id);
            }

            UpdateEnemyBarPositions();
        }

        private void UpdateEnemyBarPositions()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Camera == null)
                return;

            var camera = MyAPIGateway.Session.Camera;
            foreach (EnemyShieldBar bar in enemyBars.Values)
            {
                bool visible = records.Enabled && records.EnemyBars && bar.Line != null &&
                    bar.Entity != null && !bar.Entity.MarkedForClose;
                if (!visible)
                {
                    if (bar.Line != null) bar.Line.SetVisible(false);
                    continue;
                }

                Vector3D position = bar.Entity.PositionComp.WorldAABB.Center;
                Vector3D viewPosition = Vector3D.Transform(position, camera.ViewMatrix);
                if (viewPosition.Z >= 0)
                {
                    bar.Line.SetVisible(false);
                    continue;
                }

                Vector3D screenPosition = camera.WorldToScreen(ref position);
                if (screenPosition.X < -1.15 || screenPosition.X > 1.15 ||
                    screenPosition.Y < -1.15 || screenPosition.Y > 1.15)
                {
                    bar.Line.SetVisible(false);
                    continue;
                }

                bar.Line.SetOrigin(new Vector2D(screenPosition.X - 0.075,
                    screenPosition.Y + 0.055));
                bar.Line.SetVisible(true);
            }
        }

        private void SetEnemyBarsVisible(bool visible)
        {
            foreach (EnemyShieldBar bar in enemyBars.Values)
            {
                if (bar.Line != null)
                    bar.Line.SetVisible(visible);
            }
        }

        private void ClearEnemyBars()
        {
            foreach (EnemyShieldBar bar in enemyBars.Values)
            {
                if (bar.Line != null)
                    bar.Line.Dispose();
            }
            enemyBars.Clear();
            threats.Clear();
            shieldSamples.Clear();
            staleBarIds.Clear();
        }

        private void OnChatMessage(string message, ref bool sendToOthers)
        {
            if (string.IsNullOrWhiteSpace(message) ||
                !message.TrimStart().StartsWith("/oshield", StringComparison.OrdinalIgnoreCase))
                return;

            sendToOthers = false;
            string[] parts = message.Trim().Split(new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            string command = parts.Length > 1 ? parts[1].ToLowerInvariant() : "toggle";

            if (command == "toggle" || command == "on" || command == "off")
            {
                records.Enabled = command == "on" ||
                    (command == "toggle" && !records.Enabled);
                records.MarkSettingsChanged();
                if (hudLine != null) hudLine.SetVisible(records.Enabled);
                if (!records.Enabled)
                {
                    SetDamageMonitoring(false);
                    SetEnemyBarsVisible(false);
                }
                Notify("Target shield HUD " + (records.Enabled ? "ON" : "OFF"));
            }
            else if (command == "bars")
            {
                string state = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "toggle";
                if (state != "on" && state != "off" && state != "toggle" &&
                    state != "min" && state != "full")
                {
                    Notify("Use: /oshield bars [on|off|min|full]", "Red");
                    return;
                }
                if (state == "min" || state == "full")
                {
                    records.MinimalEnemyBars = state == "min";
                    records.EnemyBars = true;
                }
                else
                {
                    records.EnemyBars = state == "on" ||
                        (state == "toggle" && !records.EnemyBars);
                }
                records.MarkSettingsChanged();
                if (!records.EnemyBars)
                    ClearEnemyBars();
                Notify("Enemy shield bars " + (records.EnemyBars
                    ? (records.MinimalEnemyBars ? "MIN" : "FULL")
                    : "OFF"));
            }
            else if (command == "resetdamage")
            {
                if (currentDamage == null)
                    Notify("No current WC target", "Red");
                else
                {
                    currentDamage.Shield = 0;
                    currentDamage.Hull = 0;
                    Notify("Current target damage reset");
                }
            }
            else if (command == "pos" && parts.Length >= 4)
            {
                double x;
                double y;
                if (double.TryParse(parts[2], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out x) &&
                    double.TryParse(parts[3], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out y))
                {
                    records.X = Math.Max(-1, Math.Min(1, x));
                    records.Y = Math.Max(-1, Math.Min(1, y));
                    records.MarkSettingsChanged();
                    if (hudLine != null)
                        hudLine.SetOrigin(new Vector2D(records.X, records.Y));
                    Notify("HUD position saved");
                }
                else Notify("Use: /oshield pos -0.34 0.82", "Red");
            }
            else if (command == "scale" && parts.Length >= 3)
            {
                double scale;
                if (double.TryParse(parts[2], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out scale))
                {
                    records.Scale = Math.Max(0.4, Math.Min(2.0, scale));
                    records.MarkSettingsChanged();
                    if (hudLine != null) hudLine.SetScale(records.Scale);
                    foreach (EnemyShieldBar bar in enemyBars.Values)
                    {
                        if (bar.Line != null) bar.Line.SetScale(EnemyBarScale());
                    }
                    Notify("HUD scale: " + records.Scale.ToString("0.00"));
                }
                else Notify("Use: /oshield scale 0.78", "Red");
            }
            else if (command == "api")
            {
                Notify("WC " + (weaponCore.IsReady ? "ready" : "waiting") +
                    " | damage " + (weaponCore.CanReadDamage ? "ready" : "waiting") +
                    " | monitor " + (damageMonitorActive ? "on" : "off") +
                    " | DS " + (defenseShields.IsReady ? "ready" : "waiting") +
                    " | TextHUD " + (textHud.IsReady ? "ready" : "waiting"),
                    "White", 7000);
            }
            else if (command == "record")
            {
                Notify(string.IsNullOrEmpty(currentTargetName)
                    ? "No current WC target"
                    : currentTargetName + " | highest shield " +
                        FormatNumber(currentMaxSeen), "White", 7000);
            }
            else if (command == "top")
            {
                Notify(records.Summary(5), "White", 10000);
            }
            else if (command == "save")
            {
                try
                {
                    records.ForceSave();
                    lastSaveFrame = frame;
                    Notify("Saved " + records.Count + " max-shield records to " +
                        "OreoTargetShieldHud.txt", "Green", 8000);
                }
                catch (Exception error)
                {
                    Notify("Shield record save failed: " + error.GetType().Name,
                        "Red", 8000);
                }
            }
            else if (command == "export")
            {
                try
                {
                    records.ForceSave();
                    lastSaveFrame = frame;
                    int count = records.ExportShieldRecords();
                    Notify(count < 0
                        ? "Shield export unavailable"
                        : "Exported " + count + " max-shield records to " +
                            RecordStore.ExportFileName,
                        count < 0 ? "Red" : "Green", 8000);
                }
                catch (Exception error)
                {
                    Notify("Shield export failed: " + error.GetType().Name, "Red", 8000);
                }
            }
            else if (command == "cleardata")
            {
                if (parts.Length < 3 || !parts[2].Equals("confirm",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Notify("This clears all saved max shields. Run: /oshield cleardata confirm",
                        "Red", 10000);
                }
                else
                {
                    int cleared = records.ClearShieldRecords();
                    records.Save();
                    lastSaveFrame = frame;
                    currentMaxSeen = 0;
                    Notify("Cleared " + cleared + " saved max-shield records", "Green", 8000);
                }
            }
            else
            {
                Notify("/oshield [on|off] | bars [on|off|min|full] | resetdamage | " +
                    "pos X Y | scale N | api | record | top | save | export | " +
                    "cleardata confirm",
                    "White", 10000);
            }
        }

        private static bool SameTopEntity(MyEntity entity, long entityId)
        {
            if (entity == null || entityId == 0)
                return false;
            MyEntity top = entity.GetTopMostParent() ?? entity;
            return top.EntityId == entityId;
        }

        private double EnemyBarScale()
        {
            return Math.Max(0.35, Math.Min(0.85, records.Scale * 0.68));
        }

        private static double ClampPercent(double percent)
        {
            return Math.Max(0, Math.Min(100, percent));
        }

        private static string PercentColor(double percent)
        {
            return percent > 60 ? "0,255,80" : percent > 25 ? "255,220,0" : "255,60,60";
        }

        private static string BuildBar(double percent, string color)
        {
            return BuildShieldBar(percent, color, 24);
        }

        private static int LogarithmicBarWidth(double maximumHp,
            double smallestMaximumHp, double largestMaximumHp,
            int minimumWidth, int maximumWidth)
        {
            if (maximumWidth <= minimumWidth || maximumHp <= 0 ||
                smallestMaximumHp <= 0 || largestMaximumHp <= 0)
                return minimumWidth;
            if (largestMaximumHp <= smallestMaximumHp)
                return maximumWidth;

            double clampedHp = Math.Max(smallestMaximumHp,
                Math.Min(largestMaximumHp, maximumHp));
            double ratio = Math.Log(clampedHp / smallestMaximumHp) /
                Math.Log(largestMaximumHp / smallestMaximumHp);
            int width = minimumWidth + (int)Math.Round(
                (maximumWidth - minimumWidth) * ratio);
            return Math.Max(minimumWidth, Math.Min(maximumWidth, width));
        }

        private static string BuildShieldBar(double percent, string color, int width)
        {
            width = Math.Max(1, width);
            percent = ClampPercent(percent);
            int eighths = (int)Math.Round(width * 8d * percent / 100d);
            eighths = Math.Max(0, Math.Min(width * 8, eighths));
            int full = eighths / 8;
            int partial = eighths % 8;
            int used = full + (partial > 0 ? 1 : 0);

            var bar = new StringBuilder(width + 80);
            bar.Append("<color=150,150,150>[<color=").Append(color).Append(">")
                .Append(new string('█', full));
            if (partial > 0)
                bar.Append(ShieldBarSteps[partial]);
            bar.Append("<color=55,55,55>")
                .Append(new string('_', Math.Max(0, width - used)))
                .Append("<color=150,150,150>]<color=255,255,255>");
            return bar.ToString();
        }

        private static string CompactTargetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";

            string value = name.Trim();
            if (value.StartsWith("(NPC-", StringComparison.OrdinalIgnoreCase))
            {
                int close = value.IndexOf(')');
                if (close >= 0 && close + 1 < value.Length)
                    value = value.Substring(close + 1).Trim();
            }
            return value;
        }

        private static string CleanHudText(string value)
        {
            return (value ?? string.Empty).Replace('<', '[').Replace('>', ']').Trim();
        }

        private static string FormatDistance(double metres)
        {
            return metres >= 1000
                ? (metres / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " km"
                : metres.ToString("0", CultureInfo.InvariantCulture) + " m";
        }

        private static string FormatNumber(double value)
        {
            if (value >= 1000000000) return (value / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (value >= 1000000) return (value / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000) return (value / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        private static void Notify(string message, string color = "Green", int duration = 4000)
        {
            MyAPIGateway.Utilities.ShowNotification(message, duration, color);
        }

        public void OpenConfigDialog()
        {
            if (MyAPIGateway.Utilities != null)
                Notify("Use /oshield help in chat", "White", 6000);
        }

        public void Dispose()
        {
            if (sessionLoaded)
                StopSession();
        }

        private sealed class EnemyShieldBar
        {
            public MyEntity Entity;
            public readonly StringBuilder Text = new StringBuilder(192);
            public HudText Line;
            public int LastSeenFrame;

            public EnemyShieldBar(MyEntity entity)
            {
                Entity = entity;
            }
        }

        private sealed class EnemyShieldSample
        {
            public MyEntity Entity;
            public string Name = string.Empty;
            public double CurrentHp;
            public double MaximumHp;
            public double Percent;
        }

        private sealed class TargetDamageRecord
        {
            public string Name = string.Empty;
            public double Shield;
            public double Hull;
            public int LastSeenFrame;
        }
    }
}
