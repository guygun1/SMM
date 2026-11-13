using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Shows every other player on the HUD as a native Signal ping (PingInstance), named and tinted like the player.
    /// The ping object is a child of the remote avatar, so it follows the player and dies with the avatar.
    /// </summary>
    sealed class PlayerBeacons : ISyncFeature
    {
        public string Name { get { return "beacons"; } }

        const string ChildName = "mp_beacon";
        static ConfigEntry<bool> _enabled;
        float _nextTick, _nextScan;
        RemoteAvatar[] _avatars = new RemoteAvatar[0];
        readonly Dictionary<int, Color> _lastTint = new Dictionary<int, Color>();
        readonly Dictionary<int, float> _nextTintPush = new Dictionary<int, float>();

        public void Register()
        {
            try
            {
                if (_enabled == null && Plugin.I != null)
                    _enabled = Plugin.I.Config.Bind("Interface", "PlayerBeacons", true, "Show other players on the HUD as signal pings with their name and colour.");
            }
            catch (Exception e) { SyncKit.Warn("beacons: config: " + e.Message); }
            _nextScan = 0f;
        }

        public void Tick()
        {
            try
            {
                if (Time.unscaledTime < _nextTick) return;
                _nextTick = Time.unscaledTime + 0.2f;

                bool on = _enabled == null || _enabled.Value;
                if (Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + 1f;
                    _avatars = UnityEngine.Object.FindObjectsOfType<RemoteAvatar>();
                }
                foreach (var ra in _avatars)
                {
                    if (ra == null) continue;
                    var child = ra.transform.Find(ChildName);
                    if (!on)
                    {
                        if (child != null) UnityEngine.Object.Destroy(child.gameObject);
                        continue;
                    }
                    PingInstance ping = child != null ? child.GetComponent<PingInstance>() : null;
                    if (ping == null)
                    {
                        if (child != null) UnityEngine.Object.Destroy(child.gameObject);
                        ping = Create(ra);
                        if (ping == null) continue;
                    }
                    Update(ra, ping);
                }
            }
            catch (Exception e) { SyncKit.Warn("beacons: " + e.Message); }
        }

        static PingInstance Create(RemoteAvatar ra)
        {
            try
            {
                var go = new GameObject(ChildName);
                go.SetActive(false);                       // set the fields before Awake/OnEnable registers the ping
                go.transform.SetParent(ra.transform, false);
                go.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                var p = go.AddComponent<PingInstance>();
                p.pingType = PingType.Signal;
                p.origin = go.transform;
                p.displayPingInManager = true;
                p.visible = true;
                p.minDist = 0f;
                p._id = "mp_player_" + ra.OwnerPeer;
                go.SetActive(true);
                return p;
            }
            catch (Exception e) { SyncKit.Warn("beacons: create: " + e.Message); return null; }
        }

        void Update(RemoteAvatar ra, PingInstance p)
        {
            var st = ra.state;
            if (st == null) return;
            string label = string.IsNullOrEmpty(st.playerName) ? "Player " + ra.OwnerPeer : st.playerName;
            if (p.GetLabel() != label) p.SetLabel(label);
            p.minDist = 0f;
            if (!p.visible) p.SetVisible(true);

            // PingInstance only has a palette index, so push the exact colour to the HUD. The HUD resets it to the
            // palette colour whenever it rebuilds the icon, hence the periodic re-push.
            int key = ra.OwnerPeer;
            Color last;
            float next;
            _nextTintPush.TryGetValue(key, out next);
            if (!_lastTint.TryGetValue(key, out last) || last != st.color || Time.unscaledTime >= next)
            {
                if (!_lastTint.ContainsKey(key) || last != st.color) p.SetColor(Closest(st.color));
                _lastTint[key] = st.color;
                _nextTintPush[key] = Time.unscaledTime + 2f;
                try
                {
                    if (PingManager.onColor != null) PingManager.onColor(p.Id, st.color);
                }
                catch (Exception) { }
            }
        }

        static int Closest(Color c)
        {
            var opts = PingManager.colorOptions;
            int best = 0; float bd = float.MaxValue;
            if (opts == null) return 0;
            for (int i = 0; i < opts.Length; i++)
            {
                float d = Mathf.Pow(opts[i].r - c.r, 2) + Mathf.Pow(opts[i].g - c.g, 2) + Mathf.Pow(opts[i].b - c.b, 2);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }
    }
}
