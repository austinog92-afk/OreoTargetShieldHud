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
    /// Defense Shields values, selected-grid hull integrity, outgoing WC damage
    /// popups, and a capped set of nearby enemy shield bars. Only maximum observed
    /// NPC shield capacity is saved permanently.
    /// </summary>
    public sealed class Plugin : IPlugin, IDisposable
    {
        private const int PollFrames = 12;        // Selected target: about 5 reads/second.
        private const int ThreatPollFrames = 30; // Nearby bars: about 2 reads/second.
        private const int HullPollFrames = 60;   // Selected hull: one local scan/second.
        private const int ApiRetryFrames = 300;  // Re-request missing APIs every ~5 seconds.
        private const int SaveFrames = 600;      // At most one local save every ~10 seconds.
        private const int MaxEnemyBars = 16;
        private const int MaxDamageTargets = 128;
        private const int MaxHullTargets = 64;
        private const int DamagePopupBatchFrames = 12;
        private const int DamagePopupLifetimeFrames = 72;
        private const int MaxDamagePopups = 10;
        private const int MaxPendingDamageTargets = 16;
        private const double EnemyBarRange = 15000d;
        private const long DamageMonitorId = 675497565490103L;
        private static readonly char[] ShieldBarSteps =
            { '_', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        private readonly StringBuilder hudText = new StringBuilder(480);
        private readonly RecordStore records = new RecordStore();
        private readonly RoachT3Bridge roachT3 = new RoachT3Bridge();
        private readonly List<MyTuple<MyEntity, float>> threats =
            new List<MyTuple<MyEntity, float>>(32);
        private readonly Dictionary<long, EnemyShieldBar> enemyBars =
            new Dictionary<long, EnemyShieldBar>();
        private readonly Dictionary<long, TargetDamageRecord> damageByTarget =
            new Dictionary<long, TargetDamageRecord>();
        private readonly Dictionary<long, TargetHullRecord> hullByTarget =
            new Dictionary<long, TargetHullRecord>();
        private readonly Dictionary<long, PendingDamageBatch> pendingDamagePopups =
            new Dictionary<long, PendingDamageBatch>();
        private readonly List<DamagePopup> damagePopups = new List<DamagePopup>(MaxDamagePopups);
        private readonly List<IMySlimBlock> hullBlocks = new List<IMySlimBlock>(4096);
        private readonly List<long> staleBarIds = new List<long>(16);
        private readonly List<long> readyPopupIds = new List<long>(MaxPendingDamageTargets);
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
        private int damagePopupSequence;
        private long shooterGridId;
        private long currentTargetId;
        private string currentTargetName = string.Empty;
        private TargetDamageRecord currentDamage;
        private IMyCubeGrid shooterGrid;
        private IMyCubeGrid currentTargetGrid;
        private TargetHullRecord currentHull;
        private double currentMaxSeen;
        private double previousShieldHp;
        private double previousShieldMaximum;
        private double shieldRateHpPerSecond;
        private int previousShieldFrame;
        private bool shieldRateReady;

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

                UpdateDamagePopups();

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
            ClearDamagePopups();
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
            hullByTarget.Clear();
            hullBlocks.Clear();
            roachT3.Reset();
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
            if (RecordStore.IsGenericGridName(targetName))
            {
                ClearCurrentTarget(true);
                hudText.Append("<color=90,90,90>OREO TARGET SHIELD - ignored unnamed grid<color=255,255,255>");
                return;
            }

            IMyTerminalBlock shield = defenseShields.FindShield(target);
            SetCurrentTarget(shooter, target, targetName);
            RefreshHullIntegrityIfDue();

            double distance = Vector3D.Distance(shooter.PositionComp.WorldAABB.Center,
                target.PositionComp.WorldAABB.Center);
            hudText.Append("<color=0,220,255>TARGET:<color=255,255,255> ")
                .Append(currentTargetName).Append("  <color=150,150,150>")
                .Append(FormatDistance(distance)).Append("<color=255,255,255>\n");

            if (shield == null)
            {
                ResetShieldRate();
                hudText.Append("<color=150,150,150>SHIELD: none detected<color=255,255,255>");
                AppendHullBar();
                AppendDamageLine();
                AppendRoachT3();
                return;
            }

            // Defense Shields exposes charge in 1/100 HP units; multiply by 100 to
            // match the HP shown by Defense Shields and existing PB integrations.
            double currentHp = Math.Max(0, defenseShields.GetCurrentCharge(shield) * 100d);
            double maximumHp = Math.Max(0, defenseShields.GetMaximumCharge(shield) * 100d);
            double percent = defenseShields.GetPercent(shield);
            if (maximumHp <= 0)
            {
                ResetShieldRate();
                hudText.Append("<color=150,150,150>SHIELD: API returned no capacity<color=255,255,255>");
                AppendHullBar();
                AppendDamageLine();
                AppendRoachT3();
                return;
            }
            if (percent < 0 || double.IsNaN(percent) || double.IsInfinity(percent))
                percent = 100d * currentHp / maximumHp;
            percent = ClampPercent(percent);

            currentMaxSeen = records.Observe(currentTargetName, maximumHp);
            UpdateShieldRate(currentHp, maximumHp);
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

            AppendHullBar();
            AppendShieldRate(currentHp, maximumHp);
            AppendDamageLine();
            AppendRoachT3();
        }

        private void SetCurrentTarget(MyEntity shooter, MyEntity target, string name)
        {
            long newShooterId = shooter == null ? 0 : shooter.EntityId;
            long newTargetId = target == null ? 0 : target.EntityId;
            if (newTargetId != currentTargetId)
                ResetShieldRate();
            shooterGridId = newShooterId;
            shooterGrid = shooter as IMyCubeGrid;
            currentTargetId = newTargetId;
            currentTargetName = name ?? string.Empty;
            currentDamage = GetOrCreateDamageRecord(currentTargetId, currentTargetName);
            currentTargetGrid = target as IMyCubeGrid;
            currentHull = GetOrCreateHullRecord(currentTargetId);
            currentMaxSeen = records.GetMaximumSeen(currentTargetName);
            SetDamageMonitoring(currentTargetId != 0);
        }

        private void ClearCurrentTarget(bool stopDamageMonitor)
        {
            if (stopDamageMonitor)
                SetDamageMonitoring(false);
            shooterGridId = 0;
            shooterGrid = null;
            currentTargetId = 0;
            currentTargetName = string.Empty;
            currentDamage = null;
            currentTargetGrid = null;
            currentHull = null;
            currentMaxSeen = 0;
            hullBlocks.Clear();
            ResetShieldRate();
        }

        private TargetHullRecord GetOrCreateHullRecord(long entityId)
        {
            if (entityId == 0)
                return null;

            TargetHullRecord record;
            if (!hullByTarget.TryGetValue(entityId, out record))
            {
                if (hullByTarget.Count >= MaxHullTargets)
                {
                    long oldestId = 0;
                    int oldestFrame = int.MaxValue;
                    foreach (KeyValuePair<long, TargetHullRecord> item in hullByTarget)
                    {
                        if (item.Key != currentTargetId &&
                            item.Value.LastScanFrame < oldestFrame)
                        {
                            oldestId = item.Key;
                            oldestFrame = item.Value.LastScanFrame;
                        }
                    }
                    if (oldestId != 0)
                        hullByTarget.Remove(oldestId);
                }

                record = new TargetHullRecord();
                hullByTarget[entityId] = record;
            }
            return record;
        }

        private void RefreshHullIntegrityIfDue()
        {
            if (currentTargetGrid == null || currentHull == null ||
                currentTargetGrid.MarkedForClose)
                return;
            if (currentHull.LastScanFrame > 0 &&
                frame - currentHull.LastScanFrame < HullPollFrames)
                return;

            try
            {
                hullBlocks.Clear();
                currentTargetGrid.GetBlocks(hullBlocks);
                double currentHp = 0;
                double existingMaximumHp = 0;
                for (int i = 0; i < hullBlocks.Count; i++)
                {
                    IMySlimBlock block = hullBlocks[i];
                    if (block == null)
                        continue;
                    double maximum = Math.Max(0, block.MaxIntegrity);
                    existingMaximumHp += maximum;
                    currentHp += Math.Max(0, Math.Min(maximum, block.Integrity));
                }

                if (existingMaximumHp <= 0)
                    return;

                // Never reduce the denominator when a destroyed block disappears
                // from the grid. New/rebuilt blocks may increase the observed peak.
                if (existingMaximumHp > currentHull.MaximumHp)
                    currentHull.MaximumHp = existingMaximumHp;
                currentHull.CurrentHp = Math.Min(currentHp, currentHull.MaximumHp);
                currentHull.LastScanFrame = frame;
                currentHull.Ready = true;
            }
            catch
            {
                // A grid can close while blocks are being copied. Keep the previous
                // good reading and try again on the next selected-target scan.
            }
            finally
            {
                hullBlocks.Clear();
            }
        }

        private void AppendHullBar()
        {
            hudText.Append("\n<color=0,220,255>HULL:<color=255,255,255> ");
            if (currentHull == null || !currentHull.Ready ||
                currentHull.MaximumHp <= 0)
            {
                hudText.Append("calculating");
                return;
            }

            double percent = ClampPercent(100d * currentHull.CurrentHp /
                currentHull.MaximumHp);
            string color = PercentColor(percent);
            hudText.Append(FormatNumber(currentHull.CurrentHp)).Append(" / ")
                .Append(FormatNumber(currentHull.MaximumHp)).Append(" HP  ")
                .Append("<color=").Append(color).Append(">")
                .Append(percent.ToString("0.0", CultureInfo.InvariantCulture))
                .Append("%<color=255,255,255>\n")
                .Append(BuildBar(percent, color));
        }

        private void ResetShieldRate()
        {
            previousShieldHp = 0;
            previousShieldMaximum = 0;
            shieldRateHpPerSecond = 0;
            previousShieldFrame = 0;
            shieldRateReady = false;
        }

        private void UpdateShieldRate(double currentHp, double maximumHp)
        {
            if (previousShieldFrame <= 0)
            {
                previousShieldHp = currentHp;
                previousShieldMaximum = maximumHp;
                previousShieldFrame = frame;
                shieldRateReady = false;
                return;
            }

            double elapsed = (frame - previousShieldFrame) / 60d;
            // Capacity should be effectively constant between samples. A fortify,
            // unfortify, or modulation transition changes maximum HP, so start a
            // fresh baseline rather than reporting the capacity change as damage.
            double capacityTolerance = Math.Max(10d,
                Math.Max(previousShieldMaximum, maximumHp) * 0.0001d);
            bool stableCapacity = Math.Abs(maximumHp - previousShieldMaximum) <=
                capacityTolerance;

            if (!stableCapacity || elapsed <= 0 || elapsed > 2d)
            {
                previousShieldHp = currentHp;
                previousShieldMaximum = maximumHp;
                previousShieldFrame = frame;
                shieldRateHpPerSecond = 0;
                shieldRateReady = false;
                return;
            }

            double delta = currentHp - previousShieldHp;
            double noiseFloor = Math.Max(1d, maximumHp * 0.000001d);
            double rawRate = Math.Abs(delta) <= noiseFloor ? 0 : delta / elapsed;
            double rateNoiseFloor = noiseFloor / elapsed;

            if (!shieldRateReady ||
                (rawRate != 0 &&
                    Math.Sign(rawRate) != Math.Sign(shieldRateHpPerSecond)))
                shieldRateHpPerSecond = rawRate;
            else
                shieldRateHpPerSecond = shieldRateHpPerSecond * 0.65d + rawRate * 0.35d;

            if (Math.Abs(shieldRateHpPerSecond) < rateNoiseFloor)
                shieldRateHpPerSecond = 0;

            previousShieldHp = currentHp;
            previousShieldMaximum = maximumHp;
            previousShieldFrame = frame;
            shieldRateReady = true;
        }

        private void AppendShieldRate(double currentHp, double maximumHp)
        {
            hudText.Append("\n<color=0,220,255>SHIELD RATE:<color=255,255,255> ");
            if (!shieldRateReady)
            {
                hudText.Append("calculating");
                return;
            }

            if (shieldRateHpPerSecond < 0)
            {
                double rate = -shieldRateHpPerSecond;
                hudText.Append("<color=255,80,80>DRAIN<color=255,255,255> ")
                    .Append(FormatNumber(rate)).Append("/s  |  BREAK ~")
                    .Append(FormatDuration(currentHp / rate));
            }
            else if (shieldRateHpPerSecond > 0)
            {
                double remaining = Math.Max(0, maximumHp - currentHp);
                hudText.Append("<color=80,255,120>RECHARGE<color=255,255,255> ")
                    .Append(FormatNumber(shieldRateHpPerSecond)).Append("/s  |  FULL ~")
                    .Append(FormatDuration(remaining / shieldRateHpPerSecond));
            }
            else
            {
                hudText.Append("stable");
            }
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

        private void AppendRoachT3()
        {
            T3Snapshot t3 = roachT3.Read(shooterGrid, currentTargetId,
                currentTargetName, frame);
            if (t3 == null || !t3.Available || !t3.MatchesSelectedTarget)
                return;

            hudText.Append("\n<color=0,220,255>ROACH T3:<color=255,255,255> ")
                .Append(t3.Active
                    ? "<color=0,255,80>TRACKING<color=255,255,255> "
                    : "<color=255,220,0>PAUSED<color=255,255,255> ")
                .Append(FormatT3(t3.Current));
            if (t3.ExpectedMaximum > 0)
            {
                hudText.Append(" / ").Append(FormatT3(t3.ExpectedMaximum))
                    .Append(" (").Append((100d * t3.Current / t3.ExpectedMaximum)
                        .ToString("0", CultureInfo.InvariantCulture)).Append("%)");
            }

            hudText.Append("\n<color=0,220,255>AVG<color=255,255,255> ")
                .Append(t3.Tracked > 0 ? FormatT3(t3.Average) : "--")
                .Append("  <color=0,220,255>BEST<color=255,255,255> ")
                .Append(FormatT3(t3.Best))
                .Append("  <color=0,220,255>TOTAL<color=255,255,255> ")
                .Append(FormatT3(t3.Total))
                .Append("  <color=0,220,255>TRACKED<color=255,255,255> ")
                .Append(t3.Tracked.ToString(CultureInfo.InvariantCulture));
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
                        if (hitTop != null)
                            QueueDamagePopup(hitTop, damage, false);
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
                    QueueDamagePopup(hitEntity, damage, true);
                }
            }
        }

        private void QueueDamagePopup(MyEntity entity, double damage, bool shieldDamage)
        {
            if (entity == null || entity.MarkedForClose || damage <= 0 ||
                double.IsNaN(damage) || double.IsInfinity(damage))
                return;

            entity = entity.GetTopMostParent() ?? entity;
            PendingDamageBatch batch;
            if (!pendingDamagePopups.TryGetValue(entity.EntityId, out batch))
            {
                if (pendingDamagePopups.Count >= MaxPendingDamageTargets)
                    return;
                // Check the name once when opening a batch, not once per projectile.
                // A MAC can report hundreds of damage samples for one visible hit.
                string name = CleanHudText(entity.DisplayName);
                if (RecordStore.IsGenericGridName(name))
                    return;
                batch = new PendingDamageBatch
                {
                    Entity = entity,
                    FirstFrame = frame
                };
                pendingDamagePopups[entity.EntityId] = batch;
            }
            else
            {
                batch.Entity = entity;
            }

            if (shieldDamage)
                batch.ShieldDamage += damage;
            else
                batch.HullDamage += damage;
        }

        private void UpdateDamagePopups()
        {
            if (!records.Enabled || textHud == null || !textHud.IsReady)
            {
                ClearDamagePopups();
                return;
            }

            readyPopupIds.Clear();
            foreach (KeyValuePair<long, PendingDamageBatch> item in pendingDamagePopups)
            {
                PendingDamageBatch batch = item.Value;
                if (batch.Entity == null || batch.Entity.MarkedForClose ||
                    frame - batch.FirstFrame >= DamagePopupBatchFrames)
                    readyPopupIds.Add(item.Key);
            }

            for (int i = 0; i < readyPopupIds.Count; i++)
            {
                PendingDamageBatch batch;
                if (!pendingDamagePopups.TryGetValue(readyPopupIds[i], out batch))
                    continue;
                pendingDamagePopups.Remove(readyPopupIds[i]);
                if (batch.Entity == null || batch.Entity.MarkedForClose)
                    continue;
                SpawnDamagePopup(batch.Entity, batch.ShieldDamage, true);
                SpawnDamagePopup(batch.Entity, batch.HullDamage, false);
            }
            readyPopupIds.Clear();

            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Camera == null)
            {
                SetDamagePopupsVisible(false);
                return;
            }

            var camera = MyAPIGateway.Session.Camera;
            for (int i = damagePopups.Count - 1; i >= 0; i--)
            {
                DamagePopup popup = damagePopups[i];
                int ageFrames = frame - popup.StartFrame;
                if (ageFrames >= DamagePopupLifetimeFrames || popup.Entity == null ||
                    popup.Entity.MarkedForClose)
                {
                    if (popup.Line != null) popup.Line.Dispose();
                    damagePopups.RemoveAt(i);
                    continue;
                }

                Vector3D position = popup.Entity.PositionComp.WorldAABB.Center;
                Vector3D viewPosition = Vector3D.Transform(position, camera.ViewMatrix);
                if (viewPosition.Z >= 0)
                {
                    popup.Line.SetVisible(false);
                    continue;
                }

                Vector3D screenPosition = camera.WorldToScreen(ref position);
                if (screenPosition.X < -1.15 || screenPosition.X > 1.15 ||
                    screenPosition.Y < -1.15 || screenPosition.Y > 1.15)
                {
                    popup.Line.SetVisible(false);
                    continue;
                }

                double progress = Math.Max(0, Math.Min(1,
                    ageFrames / (double)DamagePopupLifetimeFrames));
                UpdateDamagePopupText(popup, progress);
                double horizontalOffset = (popup.Lane - 2) * 0.018d;
                double verticalOffset = 0.10d + progress * 0.11d +
                    (popup.Lane % 2) * 0.012d;
                popup.Line.SetOrigin(new Vector2D(screenPosition.X + horizontalOffset,
                    screenPosition.Y + verticalOffset));
                popup.Line.SetVisible(true);
            }
        }

        private void SpawnDamagePopup(MyEntity entity, double damage, bool shieldDamage)
        {
            if (entity == null || damage <= 0 || textHud == null || !textHud.IsReady)
                return;

            while (damagePopups.Count >= MaxDamagePopups)
            {
                DamagePopup oldest = damagePopups[0];
                if (oldest.Line != null) oldest.Line.Dispose();
                damagePopups.RemoveAt(0);
            }

            var popup = new DamagePopup
            {
                Entity = entity,
                Damage = damage,
                ShieldDamage = shieldDamage,
                StartFrame = frame,
                Lane = damagePopupSequence++ % 5
            };
            UpdateDamagePopupText(popup, 0);
            popup.Line = textHud.CreateText(popup.Text, Vector2D.Zero,
                Math.Max(0.55d, Math.Min(1.10d, records.Scale * 0.95d)));
            if (popup.Line != null)
                damagePopups.Add(popup);
        }

        private static void UpdateDamagePopupText(DamagePopup popup, double progress)
        {
            int red;
            int green;
            int blue;
            if (popup.ShieldDamage)
            {
                red = green = blue = (int)Math.Round(255d - 140d * progress);
            }
            else
            {
                red = (int)Math.Round(255d - 110d * progress);
                green = blue = (int)Math.Round(70d - 45d * progress);
            }

            popup.Text.Clear().Append("<color=").Append(red).Append(",")
                .Append(green).Append(",").Append(blue).Append(">-")
                .Append(FormatNumber(popup.Damage));
        }

        private void SetDamagePopupsVisible(bool visible)
        {
            for (int i = 0; i < damagePopups.Count; i++)
            {
                if (damagePopups[i].Line != null)
                    damagePopups[i].Line.SetVisible(visible);
            }
        }

        private void ClearDamagePopups()
        {
            for (int i = 0; i < damagePopups.Count; i++)
            {
                if (damagePopups[i].Line != null)
                    damagePopups[i].Line.Dispose();
            }
            damagePopups.Clear();
            pendingDamagePopups.Clear();
            readyPopupIds.Clear();
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
                if (!IsEntityOnScreen(target))
                    continue;

                string name = CleanHudText(target.DisplayName);
                if (string.IsNullOrWhiteSpace(name))
                    name = "Entity " + target.EntityId;
                if (RecordStore.IsGenericGridName(name))
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
                    bar.Text.Append("<color=255,255,255>");
                    if (records.ShowEnemyBarNames)
                        bar.Text.Append(CompactTargetName(name)).Append("  ");
                    bar.Text.Append(BuildShieldBar(percent, color, barWidth)).Append("  ")
                        .Append("<color=").Append(color).Append(">")
                        .Append(percent.ToString("0", CultureInfo.InvariantCulture))
                        .Append("%<color=255,255,255>");
                }
                else
                {
                    bar.Text.Append("<color=255,255,255>");
                    if (records.ShowEnemyBarNames)
                        bar.Text.Append(name).Append("\n");
                    bar.Text.Append(BuildShieldBar(percent, color, barWidth)).Append("  ")
                        .Append("<color=").Append(color).Append(">")
                        .Append(percent.ToString("0", CultureInfo.InvariantCulture))
                        .Append("%<color=255,255,255>\n")
                        .Append(FormatNumber(currentHp)).Append(" / ")
                        .Append(FormatNumber(maximumHp)).Append(" HP");
                }
                bar.TagCharacters = Math.Max(8, Math.Min(32, name.Length));
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

        private static bool IsEntityOnScreen(MyEntity entity)
        {
            if (entity == null || MyAPIGateway.Session == null ||
                MyAPIGateway.Session.Camera == null)
                return false;

            var camera = MyAPIGateway.Session.Camera;
            Vector3D position = entity.PositionComp.WorldAABB.Center;
            Vector3D viewPosition = Vector3D.Transform(position, camera.ViewMatrix);
            if (viewPosition.Z >= 0)
                return false;

            Vector3D screenPosition = camera.WorldToScreen(ref position);
            return screenPosition.X >= -1.15 && screenPosition.X <= 1.15 &&
                screenPosition.Y >= -1.15 && screenPosition.Y <= 1.15;
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

                double estimatedWidth = records.MinimalEnemyBars ? 0.20 : 0.27;
                double originX;
                if (records.ShowEnemyBarNames)
                {
                    // Name fallback: keep our complete tag close to the entity.
                    estimatedWidth += Math.Min(0.24, bar.TagCharacters * 0.006);
                    originX = screenPosition.X - estimatedWidth * 0.5;
                }
                else
                {
                    // WeaponCore supplies the name, so attach the bar to its right edge.
                    double horizontalOffset = 0.15 + bar.TagCharacters * 0.006;
                    originX = screenPosition.X + horizontalOffset;
                    if (originX + estimatedWidth > 1.0)
                        originX = screenPosition.X - horizontalOffset - estimatedWidth;
                }

                bar.Line.SetOrigin(new Vector2D(originX,
                    screenPosition.Y + (records.MinimalEnemyBars ? 0.095 : 0.125)));
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
            else if (command == "names")
            {
                string state = parts.Length >= 3 ? parts[2].ToLowerInvariant() : "toggle";
                if (state != "on" && state != "off" && state != "toggle")
                {
                    Notify("Use: /oshield names [on|off]", "Red");
                    return;
                }
                records.ShowEnemyBarNames = state == "on" ||
                    (state == "toggle" && !records.ShowEnemyBarNames);
                records.MarkSettingsChanged();
                Notify("Enemy bar names " + (records.ShowEnemyBarNames ? "ON" : "OFF"));
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
            else if (command == "resetpos")
            {
                records.ResetPosition();
                if (hudLine != null)
                    hudLine.SetOrigin(new Vector2D(records.X, records.Y));
                Notify("HUD position reset to default", "Green", 5000);
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
                    " | Roach T3 " + (roachT3.IsLinked ? "linked" : "waiting") +
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
                Notify("/oshield [on|off] | bars [on|off|min|full] | names [on|off] | resetdamage | " +
                    "pos X Y | resetpos | scale N | api | record | top | save | export | " +
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
            return Math.Max(0.30, Math.Min(0.70, records.Scale * 0.55));
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

        private static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                return "--";
            if (seconds < 1) return "<1s";
            if (seconds < 60) return Math.Ceiling(seconds).ToString("0",
                CultureInfo.InvariantCulture) + "s";
            if (seconds < 3600)
            {
                int total = (int)Math.Ceiling(seconds);
                return (total / 60).ToString(CultureInfo.InvariantCulture) + "m " +
                    (total % 60).ToString(CultureInfo.InvariantCulture) + "s";
            }
            int minutes = (int)Math.Ceiling(seconds / 60d);
            return (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h " +
                (minutes % 60).ToString(CultureInfo.InvariantCulture) + "m";
        }

        private static string FormatNumber(double value)
        {
            if (value >= 1000000000) return (value / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (value >= 1000000) return (value / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000) return (value / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string FormatT3(double value)
        {
            return value.ToString("#,##0.##", CultureInfo.InvariantCulture);
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
            public int TagCharacters = 8;

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

        private sealed class TargetHullRecord
        {
            public double CurrentHp;
            public double MaximumHp;
            public int LastScanFrame;
            public bool Ready;
        }

        private sealed class PendingDamageBatch
        {
            public MyEntity Entity;
            public double ShieldDamage;
            public double HullDamage;
            public int FirstFrame;
        }

        private sealed class DamagePopup
        {
            public MyEntity Entity;
            public readonly StringBuilder Text = new StringBuilder(48);
            public HudText Line;
            public double Damage;
            public bool ShieldDamage;
            public int StartFrame;
            public int Lane;
        }
    }
}
