using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRage.Collections;
using VRage.Game.Entity;
using VRageMath;

namespace Oreo.TargetShieldHud
{
    /// <summary>Small client-only subset of WeaponCore's public full ModAPI.</summary>
    internal sealed class WeaponCoreApi : IDisposable
    {
        private const long Channel = 67549756549;
        private readonly Action<object> handler;
        private Func<MyEntity, int, MyEntity> getAiFocus;
        private Action<MyEntity, ICollection<MyTuple<MyEntity, float>>> getSortedThreats;
        private Action<long, int,
            Action<ListReader<MyTuple<ulong, long, int, MyEntity, MyEntity,
                ListReader<MyTuple<Vector3D, object, float>>>>>> registerDamageEvent;
        private bool registered;

        public bool IsReady { get { return getAiFocus != null; } }
        public bool CanReadThreats { get { return getSortedThreats != null; } }
        public bool CanReadDamage { get { return registerDamageEvent != null; } }

        public WeaponCoreApi()
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

        public MyEntity GetAiFocus(MyEntity shooter, int priority)
        {
            return getAiFocus == null || shooter == null ? null : getAiFocus(shooter, priority);
        }

        public void GetSortedThreats(MyEntity shooter,
            ICollection<MyTuple<MyEntity, float>> output)
        {
            if (getSortedThreats != null && shooter != null && output != null)
                getSortedThreats(shooter, output);
        }

        public void SetDamageMonitor(long id, bool enabled,
            Action<ListReader<MyTuple<ulong, long, int, MyEntity, MyEntity,
                ListReader<MyTuple<Vector3D, object, float>>>>> callback)
        {
            if (registerDamageEvent != null && callback != null)
                registerDamageEvent(id, enabled ? 1 : 0, callback);
        }

        private void HandleMessage(object message)
        {
            if (message is string)
                return;

            var methods = message as IReadOnlyDictionary<string, Delegate>;
            if (methods == null)
                return;

            Delegate method;
            if (methods.TryGetValue("GetAiFocusBase", out method))
                getAiFocus = method as Func<MyEntity, int, MyEntity>;
            if (methods.TryGetValue("GetSortedThreatsBase", out method))
                getSortedThreats = method as Action<MyEntity,
                    ICollection<MyTuple<MyEntity, float>>>;
            if (methods.TryGetValue("DamageHandler", out method))
                registerDamageEvent = method as Action<long, int,
                    Action<ListReader<MyTuple<ulong, long, int, MyEntity, MyEntity,
                        ListReader<MyTuple<Vector3D, object, float>>>>>>;
        }

        public void Dispose()
        {
            if (registered && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, handler);
            registered = false;
            getAiFocus = null;
            getSortedThreats = null;
            registerDamageEvent = null;
        }
    }
}
