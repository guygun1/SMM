using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Watches the game's sound calls (emitters and the static play helpers). Two jobs:
    ///  1. Advanced debug log: which sound, on which object, who called it, and whether it was played while a remote change was
    ///     being replayed. That shows where repeating sounds come from.
    ///  2. While a remote change is replayed, the same sound on the same object is not started again within 0.3 s: a replay that
    ///     runs twice (or in a loop) must not play the sound twice. Sounds of the local player's own actions are never touched.
    /// The sound classes are found by name; one that does not exist in this game build is skipped.
    /// </summary>
    internal static class SoundWatch
    {
        static readonly Dictionary<string, float> _last = new Dictionary<string, float>();
        static float _nextPrune;
        internal static int Suppressed;

        static readonly string[] Types = { "FMOD_CustomEmitter", "FMOD_CustomLoopingEmitter", "FMOD_StudioEventEmitter" };
        static readonly string[] Methods = { "Play", "PlayOneShot", "StartEvent", "Stop", "Release", "OnDisable" };
        internal static bool StopRepeats = true;                      // config Sound/StopRepeatedAmbience
        const float AmbientMinGap = 30f;                               // an ambient emitter is not started again within this many seconds
        static readonly Dictionary<int, float> _started = new Dictionary<int, float>();
        static readonly Dictionary<string, float> _hitAt = new Dictionary<string, float>();
        const float HitMinGap = 0.8f;                                 // the same collision / impact sound is not played again within this many seconds
        internal static int Skipped;
        const float TriggerMinGap = 1.5f;                             // a sound that a trigger callback starts is not started again within this many seconds
        static readonly Dictionary<int, float> _triggerPlay = new Dictionary<int, float>();

        static Type _hatchType; static bool _hatchLooked;
        /// <summary>True for an emitter on a Cyclops entry hatch or in a vehicle docking bay: these sounds are driven by proximity and repeat when anything flickers.</summary>
        static bool OnHatchOrBay(Component c)
        {
            if (c == null) return false;
            if (!_hatchLooked) { _hatchLooked = true; try { _hatchType = AccessTools.TypeByName("CyclopsEntryHatch"); } catch { } }
            return (_hatchType != null && c.GetComponentInParent(_hatchType) != null) || EntitySync.InDockingBay(c);
        }

        /// <summary>True when the current call comes from a physics trigger callback (OnTriggerEnter, OnTriggerExit, OnTriggerStay).</summary>
        static bool FromTrigger()
        {
            try
            {
                var fr = new StackTrace(3, false).GetFrames();
                for (int i = 0; fr != null && i < fr.Length && i < 8; i++)
                {
                    var mm = fr[i].GetMethod();
                    if (mm != null && mm.Name.StartsWith("OnTrigger", StringComparison.Ordinal)) return true;
                }
            }
            catch { }
            return false;
        }

        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (var tn in Types)
            {
                Type t = null;
                try { t = AccessTools.TypeByName(tn); } catch { }
                if (t == null) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (Array.IndexOf(Methods, m.Name) >= 0 && !m.IsAbstract && !m.ContainsGenericParameters) yield return m;
            }
            Type u = null;
            try { u = AccessTools.TypeByName("Utils"); } catch { }
            if (u != null)
                foreach (var m in u.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if ((m.Name == "PlayFMODAsset" || m.Name == "PlayEnvSound") && !m.ContainsGenericParameters) yield return m;
        }

        static string AssetName(object o)
        {
            if (o == null) return "?";
            try
            {
                var t = Traverse.Create(o);
                var asset = t.Field("asset").GetValue();
                if (asset != null) return PathOf(asset);
                string ev = t.Field("eventName").GetValue<string>(); if (!string.IsNullOrEmpty(ev)) return ev;
            }
            catch { }
            return o.GetType().Name;
        }

        static string PathOf(object asset)
        {
            try { var p = Traverse.Create(asset).Field("path").GetValue<string>(); if (!string.IsNullOrEmpty(p)) return p; } catch { }
            try { var n = asset as UnityEngine.Object; if (n != null) return n.name; } catch { }
            return asset.ToString();
        }

        // ---- once-until-away: a hatch sound or a docking-bay sound plays once, and is not started again until every player has been
        // farther than OnceRearm from where it played (a bay sound is also released by a dock or undock event, see RearmBay).
        sealed class OnceLock { public Vector3 Pos; public Transform Anchor; public SubRoot Sub; public bool Bay; }
        static readonly Dictionary<string, OnceLock> _once = new Dictionary<string, OnceLock>();
        static readonly Dictionary<int, int> _kindOf = new Dictionary<int, int>();
        const float OnceRearm = 25f;   // a Cyclops is about 20 m long: standing anywhere in it must stay "near"
        static float _nextOncePoll;
        static Type _bayType; static bool _bayLooked;

        /// <summary>0 = a normal sound, 1 = a Cyclops entry hatch sound, 2 = a vehicle docking bay sound.</summary>
        static int OnceKind(Component comp, string asset)
        {
            if (comp != null)
            {
                if (!_hatchLooked) { _hatchLooked = true; try { _hatchType = AccessTools.TypeByName("CyclopsEntryHatch"); } catch { } }
                if (_hatchType != null && comp.GetComponentInParent(_hatchType) != null) return 1;
                if (EntitySync.InDockingBay(comp)) return 2;
            }
            if (asset.IndexOf("hatch", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (asset.IndexOf("dock", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("/bay", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 0;
        }

        /// <summary>Who the lock belongs to: the hatch or bay the emitter sits in, else the emitter; "S" for a static play call.</summary>
        static string OnceOwner(Component comp)
        {
            if (comp == null) return "S";
            if (_hatchType != null) { var h = comp.GetComponentInParent(_hatchType); if (h != null) return "E" + h.GetInstanceID(); }
            if (!_bayLooked) { _bayLooked = true; try { _bayType = AccessTools.TypeByName("VehicleDockingBay"); } catch { } }
            if (_bayType != null) { var b = comp.GetComponentInParent(_bayType); if (b != null) return "E" + b.GetInstanceID(); }
            return "E" + comp.GetInstanceID();
        }

        /// <summary>Called every frame from PlayerGuard: releases the once-locks of sounds that nobody is near any more.</summary>
        internal static void Poll(float now)
        {
            if (_once.Count == 0 || now < _nextOncePoll) return;
            _nextOncePoll = now + 0.5f;
            var pl = Player.main; if (pl == null) return;
            var who = new List<Vector3> { pl.transform.position };
            try { if (Plugin.I != null) who.AddRange(Plugin.I.RemotePlayerPositions()); } catch { }
            List<string> free = null;
            foreach (var kv in _once)
            {
                bool near = false;
                var lk = kv.Value;
                if (lk.Anchor != null) lk.Pos = lk.Anchor.position;                    // the hatch moves with its Cyclops: follow it
                if (lk.Sub != null && pl.currentSub == lk.Sub) near = true;           // inside that sub is always near
                for (int i = 0; i < who.Count && !near; i++) near = (who[i] - lk.Pos).sqrMagnitude < OnceRearm * OnceRearm;
                if (near) continue;
                if (free == null) free = new List<string>();
                free.Add(kv.Key);
            }
            if (free != null) foreach (var k in free) _once.Remove(k);
        }

        /// <summary>A vehicle docked or undocked near pos: the bay sounds there may play once again.</summary>
        internal static void RearmBay(Vector3 pos)
        {
            List<string> free = null;
            foreach (var kv in _once)
                if (kv.Value.Bay && (kv.Value.Pos - pos).sqrMagnitude < 30f * 30f) { if (free == null) free = new List<string>(); free.Add(kv.Key); }
            if (free != null) foreach (var k in free) _once.Remove(k);
        }

        internal static void ResetLocks() { _once.Clear(); _kindOf.Clear(); }

        static readonly Dictionary<string, float> _logAt = new Dictionary<string, float>();

        /// <summary>
        /// The game calls Stop and Play on a few hundred emitters every frame (music, radiation, engine loops), about 2000 lines a second.
        /// The log's per-second cap then hid the hatch and bay sounds the debug log is for. Stop calls and the same call on the same
        /// emitter more than once a second are not logged, except hatch / door / dock / bay / vehicle sounds, which always are.
        /// </summary>
        static bool ShouldLog(string method, string asset, string key, float now)
        {
            bool interesting = asset.IndexOf("hatch", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0
                || asset.IndexOf("dock", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("bay", StringComparison.OrdinalIgnoreCase) >= 0
                || asset.IndexOf("moonpool", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("enter", StringComparison.OrdinalIgnoreCase) >= 0
                || asset.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("vehicle", StringComparison.OrdinalIgnoreCase) >= 0;
            if (interesting) return true;
            if (method == "Stop" || method == "Release" || method == "OnDisable") return false;
            string k = method + "|" + key;
            float l;
            if (_logAt.TryGetValue(k, out l) && now - l < 1f) return false;
            if (_logAt.Count > 2000) _logAt.Clear();
            _logAt[k] = now;
            return true;
        }

        /// <returns>false to skip the call.</returns>
        internal static bool Before(object inst, MethodBase m, object[] args)
        {
            // nothing to do outside a session unless the debug log is on: skip the asset lookup (reflection) and everything after it
            if (!SyncKit.InSession && !DebugLog.On) return true;
            string asset = null; string who = "-"; int id = 0;
            var comp = inst as Component;
            if (comp != null) { asset = AssetName(inst); who = comp.gameObject.name; id = comp.GetInstanceID(); }
            else if (args != null)
                foreach (var a in args)
                    if (a != null && a.GetType().Name.Contains("FMODAsset")) { asset = PathOf(a); break; }
            if (asset == null) asset = m.Name;
            bool applying = SyncKit.Applying;
            float now = Time.unscaledTime;

            // The log of a joined session showed the game restarting ambient sounds on a fixed beat: the reef music and background loops
            // once a second, the surface swim sound every frame. An ambient emitter (an "env" sound, or an object called ...Ambience)
            // that was started a moment ago is not started again; one that really ended starts again after the gap.
            if (comp != null && (m.Name == "Stop" || m.Name == "Release" || m.Name == "OnDisable")) _started.Remove(id);   // stopped on purpose: a start after that is real
            bool restart = false;
            if (StopRepeats && comp != null && m.Name == "Play" && SyncKit.InSession && inst.GetType().Name == "FMOD_CustomEmitter"
                && (asset.StartsWith("event:/env/") || who.IndexOf("mbience", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                float ls;
                if (_started.TryGetValue(id, out ls) && now - ls < AmbientMinGap) restart = true;
                else _started[id] = now;
            }
            if (restart)
            {
                Skipped++;
                if (DebugLog.On) DebugLog.Log("sound", "REPEAT-SKIPPED " + asset + " on '" + who + "' (started " + (now - _started[id]).ToString("F2") + " s ago)", 6);
                return false;
            }
            // Collision sounds (a Cyclops scraping or bumping something) in a collision loop played every frame: the same one is not played again within a short moment.
            if (StopRepeats && SyncKit.InSession && m.Name != "Stop" && m.Name != "Release" && m.Name != "OnDisable"
                && (asset.IndexOf("collision", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("collide", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("impact", StringComparison.OrdinalIgnoreCase) >= 0 || asset.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                float lh; string hkey = asset + "|" + id;
                if (_hitAt.TryGetValue(hkey, out lh) && now - lh < HitMinGap)
                {
                    Skipped++;
                    if (DebugLog.On) DebugLog.Log("sound", "HIT-SKIPPED " + asset + " on '" + who + "'", 6);
                    return false;
                }
                if (_hitAt.Count > 500) _hitAt.Clear();
                _hitAt[hkey] = now;
            }
            // A hatch or docking-bay sound plays once; it is not started again until everyone has moved away from it.
            if (StopRepeats && SyncKit.InSession && m.Name != "Stop" && m.Name != "Release" && m.Name != "OnDisable")
            {
                int kind;
                if (comp == null) kind = OnceKind(null, asset);
                else if (!_kindOf.TryGetValue(id, out kind)) { kind = OnceKind(comp, asset); if (_kindOf.Count > 6000) _kindOf.Clear(); _kindOf[id] = kind; }
                if (kind != 0)
                {
                    string hk = OnceOwner(comp) + "|" + asset;
                    if (_once.ContainsKey(hk))
                    {
                        Skipped++;
                        if (DebugLog.On) DebugLog.Log("sound", "ONCE-SKIPPED " + asset + " on '" + who + "' (already played, someone is still near)", 6);
                        return false;
                    }
                    var pl0 = Player.main;
                    Transform anc = comp != null ? comp.transform : null;
                    SubRoot sr = null;
                    try { if (comp != null) sr = comp.GetComponentInParent<SubRoot>(); if (sr == null && pl0 != null) sr = pl0.currentSub; } catch { }
                    if (anc == null && sr != null) anc = sr.transform;
                    _once[hk] = new OnceLock { Pos = comp != null ? comp.transform.position : (pl0 != null ? pl0.transform.position : Vector3.zero), Anchor = anc, Sub = sr, Bay = kind == 2 };
                }
            }
            // The log of a joined session showed the Cyclops entry hatch restarting its open and close sounds, from its OnTriggerEnter and
            // OnTriggerExit, over and over while the player stood near it. The trigger itself is what flickers (see the hatch probe);
            // until that is gone, a trigger-driven sound is not started again on the same emitter within a moment.
            if (StopRepeats && comp != null && m.Name == "Play" && SyncKit.InSession && inst.GetType().Name == "FMOD_CustomEmitter")
            {
                float lp; int tk = id ^ asset.GetHashCode();
                if (_triggerPlay.TryGetValue(tk, out lp) && now - lp < TriggerMinGap && (OnHatchOrBay(comp) || FromTrigger()))
                {
                    Skipped++;
                    if (DebugLog.On) DebugLog.Log("sound", "TRIGGER-REPEAT-SKIPPED " + asset + " on '" + who + "' (" + (now - lp).ToString("F2") + " s after the last start)", 6);
                    return false;
                }
                _triggerPlay[tk] = now;
            }
            string key = asset + "#" + id;
            float last;
            bool dup = applying && _last.TryGetValue(key, out last) && now - last < 0.3f;
            if (!dup) _last[key] = now;
            if (now >= _nextPrune) { _nextPrune = now + 20f; var old = new List<string>(); foreach (var kv in _last) if (now - kv.Value > 5f) old.Add(kv.Key); foreach (var k in old) _last.Remove(k); }

            if (DebugLog.On && ShouldLog(m.Name, asset, key, now))
            {
                string stack = "";
                try
                {
                    var fr = new StackTrace(2, false).GetFrames();
                    var sb = new StringBuilder();
                    for (int i = 0; fr != null && i < fr.Length && sb.Length < 200; i++)
                    {
                        var mm = fr[i].GetMethod();
                        if (mm == null || mm.DeclaringType == null) continue;
                        if (sb.Length > 0) sb.Append(" < ");
                        sb.Append(mm.DeclaringType.Name).Append('.').Append(mm.Name);
                        if (i >= 3) break;
                    }
                    stack = sb.ToString();
                }
                catch { }
                DebugLog.Log("sound", (dup ? "SUPPRESSED " : "") + asset + " on '" + who + "' via " + m.DeclaringType.Name + "." + m.Name + (applying ? " [remote replay]" : "") + " <- " + stack, 30);
            }
            if (dup) { Suppressed++; return false; }
            return true;
        }
    }

    [HarmonyPatch]
    static class P_Sound
    {
        static bool Prepare() { foreach (var m in SoundWatch.Targets()) return true; return false; }
        static IEnumerable<MethodBase> TargetMethods() { return SoundWatch.Targets(); }
        static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            try { return SoundWatch.Before(__instance, __originalMethod, __args); }
            catch { return true; }
        }
    }
    /// <summary>
    /// Advanced debug only: who enters and leaves the Cyclops entry hatch trigger. The log showed it fire an exit and an enter in the same
    /// frame, over and over; this names the collider and where it is, so the cause (the player, a vehicle, a copy of a remote player) is visible.
    /// </summary>
    [HarmonyPatch]
    static class P_HatchProbe
    {
        static bool Prepare() { foreach (var m in Targets()) return true; return false; }
        static IEnumerable<MethodBase> TargetMethods() { return Targets(); }
        static IEnumerable<MethodBase> Targets()
        {
            Type t = null;
            try { t = AccessTools.TypeByName("CyclopsEntryHatch"); } catch { }
            if (t == null) yield break;
            foreach (var n in new[] { "OnTriggerEnter", "OnTriggerExit" })
            {
                var m = AccessTools.Method(t, n, new[] { typeof(Collider) });
                if (m != null) yield return m;
            }
        }
        static void Prefix(object __instance, MethodBase __originalMethod, Collider __0)
        {
            try
            {
                if (!DebugLog.On || __0 == null) return;
                var c = __instance as Component;
                var pl = Player.main;
                string d = c != null && pl != null ? Vector3.Distance(c.transform.position, pl.transform.position).ToString("F1") + " m from the player" : "?";
                var rb = __0.attachedRigidbody;
                DebugLog.Log("hatch", __originalMethod.Name + " by collider '" + __0.name + "' of '" + __0.transform.root.name + "'"
                    + (pl != null && __0.transform.IsChildOf(pl.transform) ? " (the local player)" : "")
                    + (__0.GetComponentInParent<EntityFollower>() != null ? " (a followed copy)" : "")
                    + ", hatch " + d + ", body " + (rb == null ? "none" : (rb.isKinematic ? "kinematic" : "dynamic")), 20);
            }
            catch { }
        }
    }
}
