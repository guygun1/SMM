using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Advanced debug logging. Off by default. Turn it on in the BepInEx config ([Debug] Advanced = true), or press Ctrl+F9 in
    /// the game to switch it on and off. While on, everything the mod does is written to the normal BepInEx log with a
    /// "[dbg time frame]" prefix: every network message (rate limited per name, with a summary of what was cut), every sound
    /// that plays and who asked for it, vehicle and Cyclops events, the Cyclops interface objects of the sub the player is in,
    /// and a summary of the world every 10 seconds. Send LogOutput.log after a session with it on.
    /// </summary>
    internal static class DebugLog
    {
        static ConfigEntry<bool> _on;
        static bool _hooked;

        public static bool On { get { return _on != null && _on.Value; } }
        internal static ConfigEntry<bool> Entry { get { return _on; } }

        public static void Init(ConfigFile cfg)
        {
            _on = cfg.Bind("Debug", "Advanced", false, "Write a very detailed log of everything the mod does (messages, sounds, vehicles, Cyclops, state) to the BepInEx log. Slower and noisy: only for finding bugs. Ctrl+F9 in the game toggles it.");
            SoundWatch.StopRepeats = cfg.Bind("Sound", "StopRepeatedAmbience", true, "In a multiplayer session, an ambient sound (environment loops, music, the surface swim sound) is not restarted within 2 seconds of its last start. Turn off if an ambient sound seems to be missing.").Value;
            Apply();
            _on.SettingChanged += (s, e) => Apply();
        }

        static void Apply()
        {
            if (On && !_hooked) { _hooked = true; SyncSession.Trace = OnMsg; Plugin.I?.Log.Info("[dbg] advanced debug logging is ON"); }
            else if (!On && _hooked) { _hooked = false; SyncSession.Trace = null; Plugin.I?.Log.Info("[dbg] advanced debug logging is OFF"); }
        }

        // ------------------------------------------------------------------ logging with a per-key rate limit

        sealed class Gate { public float Window; public int Count, Cut; }
        static readonly Dictionary<string, Gate> _gates = new Dictionary<string, Gate>();
        static float _nextFlush, _nextSnap;

        /// <summary>Log one line. At most perSecond lines per key per second are written; the number cut is reported after.</summary>
        public static void Log(string key, string msg, int perSecond = 8)
        {
            if (!On || Plugin.I == null) return;
            float now = Time.unscaledTime;
            Gate g;
            if (!_gates.TryGetValue(key, out g)) { g = new Gate { Window = now }; _gates[key] = g; }
            if (now - g.Window >= 1f) { if (g.Cut > 0) Emit("(" + key + ": " + g.Cut + " more lines cut in the last second)"); g.Window = now; g.Count = 0; g.Cut = 0; }
            if (g.Count >= perSecond) { g.Cut++; return; }
            g.Count++;
            Emit(key + ": " + msg);
        }

        static void Emit(string line)
        {
            try { Plugin.I.Log.Info("[dbg " + Time.unscaledTime.ToString("F2") + " f" + Time.frameCount + "] " + line); } catch { }
        }

        // ------------------------------------------------------------------ network messages

        sealed class Stat { public int Count; public long Bytes; }
        static readonly Dictionary<string, Stat> _tx = new Dictionary<string, Stat>(), _rx = new Dictionary<string, Stat>();

        static void OnMsg(string name, int peer, int bytes, bool outgoing)
        {
            var d = outgoing ? _tx : _rx;
            Stat s; if (!d.TryGetValue(name, out s)) { s = new Stat(); d[name] = s; }
            s.Count++; s.Bytes += bytes;
            Log((outgoing ? "tx " : "rx ") + name, "peer " + peer + ", " + bytes + " bytes", 5);
        }

        // ------------------------------------------------------------------ every frame

        public static void Tick()
        {
            // Ctrl+F9 switches advanced logging on and off
            try
            {
                if (_on != null && Input.GetKeyDown(KeyCode.F9) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                    _on.Value = !_on.Value;
            }
            catch { }
            if (!On) return;
            float now = Time.unscaledTime;
            if (now >= _nextFlush)
            {
                _nextFlush = now + 10f;
                Summary("tx", _tx); Summary("rx", _rx);
            }
            if (now >= _nextSnap)
            {
                _nextSnap = now + 10f;
                try { Snapshot(); } catch (Exception e) { Emit("snapshot failed: " + e.Message); }
                try { Proximity(); } catch (Exception e) { Emit("proximity probe failed: " + e.Message); }
            }
            try { WatchCyclopsUi(now); } catch (Exception e) { Log("cyc-ui", "watch failed: " + e.Message, 1); }
        }

        static void Summary(string dir, Dictionary<string, Stat> d)
        {
            if (d.Count == 0) return;
            var sb = new StringBuilder("messages " + dir + " in the last 10 s:");
            foreach (var kv in d) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.Count).Append('/').Append(kv.Value.Bytes).Append('B');
            Emit(sb.ToString());
            d.Clear();
        }

        // ------------------------------------------------------------------ world summary

        static void Snapshot()
        {
            var p = Player.main;
            if (p == null) return;
            var sb = new StringBuilder("world: player at " + p.transform.position.ToString("F1"));
            sb.Append(", sub=" + (p.currentSub != null ? p.currentSub.name : "none"));
            sb.Append(", piloting=" + p.isPiloting);
            sb.Append(", host=" + SyncKit.IsHost + ", session=" + SyncKit.InSession);
            int followers = 0, kin = 0, subs = 0;
            foreach (var f in UnityEngine.Object.FindObjectsOfType<EntityFollower>()) { if (f == null || f.Dead) continue; followers++; var rb = f.GetComponent<Rigidbody>(); if (rb != null && rb.isKinematic) kin++; }
            foreach (var s in UnityEngine.Object.FindObjectsOfType<SubRoot>()) if (s != null && !s.isBase) subs++;
            sb.Append(", followers=" + followers + " (kinematic " + kin + "), cyclops/subs=" + subs);
            Emit(sb.ToString());
        }

        // ------------------------------------------------------------------ things that depend on how close the player is

        /// <summary>
        /// Everything the game measures against the player or the camera, side by side: where the camera, the player and every audio listener are,
        /// how far the nearest Cyclops and the ambient sound emitters are, and what the game thinks the player's surroundings are.
        /// A camera or listener that is not where the player is, a second listener, or a duplicated ambient emitter shows up here.
        /// </summary>
        static void Proximity()
        {
            var p = Player.main;
            if (p == null) return;
            var me = p.transform.position;
            var sb = new StringBuilder("prox: ");
            var cam = Camera.main;
            if (cam != null) sb.Append("camera " + Vector3.Distance(cam.transform.position, me).ToString("F2") + " m from the player; ");
            else sb.Append("no main camera; ");
            var ls = UnityEngine.Object.FindObjectsOfType<AudioListener>();
            sb.Append(ls.Length + " audio listener(s)");
            foreach (var l in ls) if (l != null) sb.Append(" ['" + l.gameObject.name + "' " + Vector3.Distance(l.transform.position, me).ToString("F1") + " m, " + (l.isActiveAndEnabled ? "on" : "off") + "]");
            sb.Append("; ");
            try { sb.Append("underwater=" + p.IsUnderwater() + ", "); } catch { }
            try { sb.Append("inside=" + p.IsInside() + ", "); } catch { }
            try { sb.Append("biome=" + p.GetBiomeString() + ", "); } catch { }
            float near = float.MaxValue; string nearName = "none";
            foreach (var sub in UnityEngine.Object.FindObjectsOfType<SubRoot>())
            {
                if (sub == null || sub.isBase) continue;
                float d = Vector3.Distance(sub.transform.position, me);
                if (d < near) { near = d; nearName = sub.name; }
            }
            sb.Append("nearest sub " + nearName + (near < float.MaxValue ? " " + near.ToString("F1") + " m" : "") + "; ");
            var et = AccessTools.TypeByName("FMOD_CustomEmitter");
            if (et != null)
            {
                int total = 0, within10 = 0, within30 = 0, within60 = 0;
                var byName = new Dictionary<string, int>();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(et))
                {
                    var c = o as Behaviour;
                    if (c == null || !c.isActiveAndEnabled) continue;
                    total++;
                    float d = Vector3.Distance(c.transform.position, me);
                    if (d < 10f) within10++; if (d < 30f) within30++; if (d < 60f) within60++;
                    if (c.gameObject.name.IndexOf("mbience", StringComparison.OrdinalIgnoreCase) >= 0)
                    { int n; byName.TryGetValue(c.gameObject.name, out n); byName[c.gameObject.name] = n + 1; }
                }
                sb.Append("emitters active " + total + " (<10 m " + within10 + ", <30 m " + within30 + ", <60 m " + within60 + ")");
                foreach (var kv in byName) sb.Append(" " + kv.Key + "x" + kv.Value);
            }
            Emit(sb.ToString());
        }

        // ------------------------------------------------------------------ the Cyclops interface: what appears and disappears

        static readonly Dictionary<int, bool> _uiState = new Dictionary<int, bool>();
        static readonly List<Behaviour> _ui = new List<Behaviour>();
        static SubRoot _uiFor; static float _nextUiScan, _nextUiCheck;

        static void WatchCyclopsUi(float now)
        {
            var p = Player.main;
            var sub = p != null ? p.currentSub : null;
            if (sub != _uiFor) { _uiFor = sub; _ui.Clear(); _uiState.Clear(); _nextUiScan = 0f; }
            if (sub == null) return;
            if (now >= _nextUiScan)
            {
                _nextUiScan = now + 3f;
                _ui.Clear();
                foreach (var b in sub.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null) continue;
                    string tn = b.GetType().Name;
                    if (tn.StartsWith("Cyclops") || tn.Contains("HUD") || tn.Contains("Hud") || tn.Contains("Panel") || tn.Contains("Button")) _ui.Add(b);
                }
                Log("cyc-ui", "watching " + _ui.Count + " interface components of '" + sub.name + "'", 1);
            }
            if (now < _nextUiCheck) return;
            _nextUiCheck = now + 0.1f;
            foreach (var b in _ui)
            {
                if (b == null) continue;
                int key = b.GetInstanceID();
                bool act = b.isActiveAndEnabled;
                bool prev;
                if (!_uiState.TryGetValue(key, out prev)) { _uiState[key] = act; continue; }
                if (prev == act) continue;
                _uiState[key] = act;
                Log("cyc-ui", b.GetType().Name + " on '" + b.gameObject.name + "' " + (act ? "ENABLED" : "DISABLED") + " (sub moved this frame: " + (sub.transform.hasChanged) + ")", 20);
            }
        }
    }
}
