using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Animations that are started by a command and not by a parameter: hatches, ramps, doors, docking arms, lids, mod machines.
    /// EntitySync already keeps animator PARAMETERS (bool, int, float) of moving things in step; a trigger or a direct Play/CrossFade
    /// leaves no value behind, so nobody else saw it. Here every such call on an animator that belongs to an object of the shared world
    /// (it has a unique id above it) is sent as: which animator (id and path), which call, and its name or hash and numbers. The other
    /// machines make the same call on their copy, inside SyncKit.Apply, so it is not sent back.
    /// Never forwarded: animators of the local player (the player's own arms and body are driven by their own machine and shown to
    /// others by the avatar), and animators without an id. Calls repeated within 0.2 s are dropped.
    /// </summary>
    sealed class AnimTriggerSync : ISyncFeature
    {
        public string Name { get { return "anim-triggers"; } }

        static readonly Dictionary<string, float> _recent = new Dictionary<string, float>();
        static float _nextPrune;
        static int _sent, _got;
        static float _nextStats;
        static readonly HashSet<string> _warned = new HashSet<string>();

        public void Register()
        {
            _recent.Clear(); _sent = _got = 0;
            SyncKit.On("anim.cmd", OnCmd);
        }

        // ------------------------------------------------------------------ send

        internal static void Local(Animator a, string op, object[] args)
        {
            if (a == null || !SyncKit.ShouldSend) return;
            var p = Player.main;
            if (p != null && a.transform.IsChildOf(p.transform)) return;          // our own body
            string id, path;
            if (!SyncKit.IdAndPath(a, out id, out path)) return;
            if (a.GetComponentInParent<EntityFollower>() != null) return;          // driven by the host: it does not decide anything here
            if (EntitySync.InDockingBay(a)) return;                                // the bay reacts to nearby vehicles on every machine by itself
            var w = new ByteWriter(48);
            w.Str(id); w.Str(path ?? ""); w.Str(op);
            w.VarU((uint)args.Length);
            var key = new System.Text.StringBuilder(id).Append('|').Append(path).Append('|').Append(op);
            foreach (var x in args)
            {
                if (x is string) { w.U8(0); w.Str((string)x); key.Append('|').Append((string)x); }
                else if (x is int) { w.U8(1); w.VarI((int)x); key.Append('|').Append((int)x); }
                else if (x is float) { w.U8(2); w.F32((float)x); }
                else return;
            }
            float now = Time.unscaledTime;
            string k = key.ToString();
            float last;
            if (_recent.TryGetValue(k, out last) && now - last < 0.2f) return;
            _recent[k] = now;
            if (now >= _nextPrune)
            {
                _nextPrune = now + 10f;
                var old = _recent.Where(kv => now - kv.Value > 5f).Select(kv => kv.Key).ToList();
                foreach (var o in old) _recent.Remove(o);
            }
            var payload = w.ToArray();
            SyncKit.Send("anim.cmd", x => x.Bytes(payload));
            _sent++;
            DebugLog.Log("anim", "sent " + op + " " + k, 10);
        }

        // ------------------------------------------------------------------ receive

        static void OnCmd(int from, ByteReader r0)
        {
            var b = r0.Bytes(); if (b == null) return;
            var r = new ByteReader(b);
            string id = r.Str(), path = r.Str(), op = r.Str();
            int n = (int)r.VarU();
            if (n < 0 || n > 6) return;
            var args = new object[n];
            for (int i = 0; i < n; i++)
            {
                byte tag = r.U8();
                if (tag == 0) args[i] = r.Str(); else if (tag == 1) args[i] = (int)r.VarI(); else if (tag == 2) args[i] = r.F32(); else return;
            }
            var tr = SyncKit.FindChild(id, path);
            var a = tr != null ? tr.GetComponent<Animator>() : null;
            if (a == null || !a.isActiveAndEnabled) { DebugLog.Log("anim", "no animator for " + op + " on " + id, 5); return; }
            if (EntitySync.InDockingBay(a)) return;                                // an older sender may still forward bay animations: this machine's bay runs by itself
            // While the local player works a hatch of this Cyclops (or a cinematic runs), the sender's trigger calls would restart the
            // hatch animation under them and the entry never finishes. Same hold as for the animator values (PlayerGuard.HoldAnims).
            if (PlayerGuard.HoldAnims(a.GetComponentInParent<SubRoot>(), a.transform)) { DebugLog.Log("anim", "held back " + op + " on " + id + " (local hatch in use)", 5); return; }
            _got++;
            DebugLog.Log("anim", "replaying " + op + " on " + id, 10);
            SyncKit.Apply(() =>
            {
                try
                {
                    switch (op)
                    {
                        case "SetTrigger": if (args[0] is string) a.SetTrigger((string)args[0]); else a.SetTrigger((int)args[0]); break;
                        case "ResetTrigger": if (args[0] is string) a.ResetTrigger((string)args[0]); else a.ResetTrigger((int)args[0]); break;
                        case "Play":
                            {
                                int layer = n > 1 ? (int)args[1] : -1; float t = n > 2 ? (float)args[2] : float.NegativeInfinity;
                                if (args[0] is string) a.Play((string)args[0], layer, t); else a.Play((int)args[0], layer, t);
                                break;
                            }
                        case "CrossFade":
                            {
                                float dur = (float)args[1]; int layer = n > 2 ? (int)args[2] : -1; float t = n > 3 ? (float)args[3] : float.NegativeInfinity;
                                if (args[0] is string) a.CrossFade((string)args[0], dur, layer, t); else a.CrossFade((int)args[0], dur, layer, t);
                                break;
                            }
                    }
                }
                catch (Exception e) { if (_warned.Add(op)) SyncKit.Warn("anim: replaying " + op + " failed: " + e.Message); }
            });
        }

        public void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextStats && (_sent + _got) > 0) { _nextStats = now + 60f; SyncKit.Info("anim-triggers: sent " + _sent + ", replayed " + _got); }
        }
    }

    // One patch class per Animator call. Each is skipped when the overload is not in this Unity version.

    static class AnimM
    {
        internal static MethodBase M(string name, params Type[] ps) { return AccessTools.Method(typeof(Animator), name, ps); }
        internal static object Opt(object o) { return o; }
    }

    [HarmonyPatch] static class P_AnimTriggerS { static bool Prepare() { return AnimM.M("SetTrigger", typeof(string)) != null; } static MethodBase TargetMethod() { return AnimM.M("SetTrigger", typeof(string)); } static void Postfix(Animator __instance, string __0) { try { AnimTriggerSync.Local(__instance, "SetTrigger", new object[] { __0 }); } catch { } } }
    [HarmonyPatch] static class P_AnimTriggerI { static bool Prepare() { return AnimM.M("SetTrigger", typeof(int)) != null; } static MethodBase TargetMethod() { return AnimM.M("SetTrigger", typeof(int)); } static void Postfix(Animator __instance, int __0) { try { AnimTriggerSync.Local(__instance, "SetTrigger", new object[] { __0 }); } catch { } } }
    [HarmonyPatch] static class P_AnimResetS { static bool Prepare() { return AnimM.M("ResetTrigger", typeof(string)) != null; } static MethodBase TargetMethod() { return AnimM.M("ResetTrigger", typeof(string)); } static void Postfix(Animator __instance, string __0) { try { AnimTriggerSync.Local(__instance, "ResetTrigger", new object[] { __0 }); } catch { } } }
    [HarmonyPatch] static class P_AnimResetI { static bool Prepare() { return AnimM.M("ResetTrigger", typeof(int)) != null; } static MethodBase TargetMethod() { return AnimM.M("ResetTrigger", typeof(int)); } static void Postfix(Animator __instance, int __0) { try { AnimTriggerSync.Local(__instance, "ResetTrigger", new object[] { __0 }); } catch { } } }
    [HarmonyPatch] static class P_AnimPlayS { static bool Prepare() { return AnimM.M("Play", typeof(string), typeof(int), typeof(float)) != null; } static MethodBase TargetMethod() { return AnimM.M("Play", typeof(string), typeof(int), typeof(float)); } static void Postfix(Animator __instance, string __0, int __1, float __2) { try { AnimTriggerSync.Local(__instance, "Play", new object[] { __0, __1, __2 }); } catch { } } }
    [HarmonyPatch] static class P_AnimPlayI { static bool Prepare() { return AnimM.M("Play", typeof(int), typeof(int), typeof(float)) != null; } static MethodBase TargetMethod() { return AnimM.M("Play", typeof(int), typeof(int), typeof(float)); } static void Postfix(Animator __instance, int __0, int __1, float __2) { try { AnimTriggerSync.Local(__instance, "Play", new object[] { __0, __1, __2 }); } catch { } } }
    [HarmonyPatch] static class P_AnimFadeS { static bool Prepare() { return AnimM.M("CrossFade", typeof(string), typeof(float), typeof(int), typeof(float), typeof(float)) != null; } static MethodBase TargetMethod() { return AnimM.M("CrossFade", typeof(string), typeof(float), typeof(int), typeof(float), typeof(float)); } static void Postfix(Animator __instance, string __0, float __1, int __2, float __3) { try { AnimTriggerSync.Local(__instance, "CrossFade", new object[] { __0, __1, __2, __3 }); } catch { } } }
}
