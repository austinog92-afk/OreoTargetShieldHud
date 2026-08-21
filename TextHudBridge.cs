using System;
using System.Text;
using Sandbox.ModAPI;
using VRage;
using VRageMath;

namespace Oreo.TargetShieldHud
{
    /// <summary>
    /// Minimal bridge for DraygoKorvan's Text HUD API. It intentionally exposes only
    /// the one persistent HUD text object this plugin needs.
    /// </summary>
    internal sealed class TextHudBridge : IDisposable
    {
        private const long Channel = 573804956;
        private readonly Action<object> handler;
        private Func<int, object> factory;
        private Action<object, int, object> setter;
        private Action<object> remover;
        private bool registered;

        public bool IsReady { get { return factory != null && setter != null && remover != null; } }
        public event Action Ready;

        public TextHudBridge()
        {
            handler = HandleRegistration;
            MyAPIGateway.Utilities.RegisterMessageHandler(Channel, handler);
            registered = true;
        }

        public HudText CreateText(StringBuilder text, Vector2D origin, double scale)
        {
            return IsReady ? new HudText(factory, setter, remover, text, origin, scale) : null;
        }

        private void HandleRegistration(object message)
        {
            if (IsReady)
                return;

            if (!(message is MyTuple<Func<int, object>, Action<object, int, object>,
                Func<object, int, object>, Action<object>>))
                return;

            var api = (MyTuple<Func<int, object>, Action<object, int, object>,
                Func<object, int, object>, Action<object>>)message;
            factory = api.Item1;
            setter = api.Item2;
            remover = api.Item4;
            var callback = Ready;
            if (callback != null)
                callback();
        }

        public void Dispose()
        {
            if (registered && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, handler);
            registered = false;
            factory = null;
            setter = null;
            remover = null;
            Ready = null;
        }
    }

    internal sealed class HudText : IDisposable
    {
        private const int MessageMember = 0;
        private const int VisibleMember = 1;
        private const int TimeToLiveMember = 2;
        private const int ScaleMember = 3;
        private const int OffsetMember = 5;
        private const int OriginMember = 10;
        private const int OptionsMember = 11;
        private const int ShadowColorMember = 12;
        private const int FontMember = 13;
        private const int HudMessageType = 0;

        private object backing;
        private readonly Action<object, int, object> setter;
        private Action<object> remover;

        public HudText(Func<int, object> factory, Action<object, int, object> setter,
            Action<object> remover, StringBuilder text, Vector2D origin, double scale)
        {
            this.setter = setter;
            this.remover = remover;
            backing = factory(HudMessageType);
            if (backing == null)
                return;

            setter(backing, TimeToLiveMember, -1);
            setter(backing, OriginMember, origin);
            setter(backing, OptionsMember, (byte)3); // Hide with HUD + draw text shadow.
            setter(backing, ShadowColorMember, new Color(0, 0, 0, 220));
            setter(backing, ScaleMember, scale);
            setter(backing, MessageMember, text);
            setter(backing, OffsetMember, Vector2D.Zero);
            setter(backing, FontMember, "monospace");
            setter(backing, VisibleMember, true);
        }

        public void SetVisible(bool visible)
        {
            if (backing != null)
                setter(backing, VisibleMember, visible);
        }

        public void SetOrigin(Vector2D origin)
        {
            if (backing != null)
                setter(backing, OriginMember, origin);
        }

        public void SetScale(double scale)
        {
            if (backing != null)
                setter(backing, ScaleMember, scale);
        }

        public void Dispose()
        {
            if (backing != null && remover != null)
                remover(backing);
            backing = null;
            remover = null;
        }
    }
}
