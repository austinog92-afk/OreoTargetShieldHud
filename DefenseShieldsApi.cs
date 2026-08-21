using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace Oreo.TargetShieldHud
{
    /// <summary>Read-only subset of Defense Shields' public full ModAPI.</summary>
    internal sealed class DefenseShieldsApi : IDisposable
    {
        private const long Channel = 1365616918;
        private readonly Action<object> handler;
        private Func<IMyEntity, IMyTerminalBlock> getShieldBlock;
        private Func<IMyEntity, bool, IMyTerminalBlock> matchEntityToShield;
        private Func<IMyTerminalBlock, float> getShieldPercent;
        private Func<IMyTerminalBlock, float> getMaxCharge;
        private Func<IMyTerminalBlock, float> getCharge;
        private bool registered;

        public bool IsReady
        {
            get
            {
                return (getShieldBlock != null || matchEntityToShield != null) &&
                       getShieldPercent != null && getMaxCharge != null && getCharge != null;
            }
        }

        public DefenseShieldsApi()
        {
            handler = HandleMessage;
        }

        public void Load()
        {
            if (!registered)
            {
                MyAPIGateway.Utilities.RegisterMessageHandler(Channel, handler);
                registered = true;
            }
            Request();
        }

        public void Request()
        {
            if (registered && !IsReady)
                MyAPIGateway.Utilities.SendModMessage(Channel, "ApiEndpointRequest");
        }

        public IMyTerminalBlock FindShield(MyEntity target)
        {
            if (target == null)
                return null;

            IMyTerminalBlock block = null;
            if (getShieldBlock != null)
                block = getShieldBlock(target);
            if (block == null && matchEntityToShield != null)
                block = matchEntityToShield(target, true);
            return block;
        }

        public float GetPercent(IMyTerminalBlock shield)
        {
            return shield == null || getShieldPercent == null ? -1f : getShieldPercent(shield);
        }

        public float GetCurrentCharge(IMyTerminalBlock shield)
        {
            return shield == null || getCharge == null ? -1f : getCharge(shield);
        }

        public float GetMaximumCharge(IMyTerminalBlock shield)
        {
            return shield == null || getMaxCharge == null ? -1f : getMaxCharge(shield);
        }

        private void HandleMessage(object message)
        {
            if (IsReady || message is string)
                return;

            var methods = message as IReadOnlyDictionary<string, Delegate>;
            if (methods == null)
                return;

            Assign(methods, "GetShieldBlock", ref getShieldBlock);
            Assign(methods, "MatchEntToShieldFast", ref matchEntityToShield);
            Assign(methods, "GetShieldPercent", ref getShieldPercent);
            Assign(methods, "GetMaxCharge", ref getMaxCharge);
            Assign(methods, "GetCharge", ref getCharge);
        }

        private static void Assign<T>(IReadOnlyDictionary<string, Delegate> methods, string name, ref T field)
            where T : class
        {
            Delegate method;
            if (methods.TryGetValue(name, out method))
                field = method as T;
        }

        public void Dispose()
        {
            if (registered && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, handler);
            registered = false;
            getShieldBlock = null;
            matchEntityToShield = null;
            getShieldPercent = null;
            getMaxCharge = null;
            getCharge = null;
        }
    }
}
