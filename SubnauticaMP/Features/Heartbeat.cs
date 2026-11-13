using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// The host sends a tiny message every 2 seconds. A joined player's watchdog (Plugin.Update) treats 20 s without anything from the
    /// host as the host being gone, and returns to the main menu. Without a beat a quiet world (nothing moving) sends nothing at all,
    /// and a host that vanished without closing the connection could not be told from a quiet one.
    /// </summary>
    sealed class Heartbeat : ISyncFeature
    {
        public string Name { get { return "heartbeat"; } }
        float _next;

        public void Register() { SyncKit.On("net.hb", (from, r) => { }); _next = 0f; }

        public void Tick()
        {
            if (!SyncKit.IsHost) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 2f;
            SyncKit.Send("net.hb", w => w.U8(1));
        }
    }
}
