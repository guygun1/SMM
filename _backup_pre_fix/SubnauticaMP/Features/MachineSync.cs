using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Machines: crafting animations on fabricators / modification stations (the crafted item stays with the crafter),
    /// base power levels (host sends, joiners set), and sign / label text.
    /// Not synced: planters and grow beds, Mobile Vehicle Bay build animation (see report).
    /// </summary>
    sealed class MachineSync : ISyncFeature
    {
        const float PowerInterval = 2f;
        const float PowerRange = 200f;
        const int PowerMax = 128;
        const int FullEvery = 5;
        const int DictMax = 4096;

        static MethodInfo _craftBegin, _craftEnd;

        float _nextPower;
        int _cycle;
        readonly Dictionary<string, float> _lastPower = new Dictionary<string, float>();

        public string Name { get { return "machines"; } }

        public void Register()
        {
            SyncKit.On("mach.craft", OnCraft);
            SyncKit.On("mach.pwr", OnPower);
            SyncKit.On("mach.sign", OnSign);
        }

        // ------------------------------------------------------------------ tick (host: power)

        public void Tick()
        {
            if (!SyncKit.Active || !SyncKit.IsHost || !HostSettings.SharedPower) return;
            if (Time.unscaledTime < _nextPower) return;
            _nextPower = Time.unscaledTime + PowerInterval;
            var player = Player.main;
            if (player == null) return;
            try { SendPower(); }
            catch (Exception e) { SyncKit.Warn("machines: power send: " + e.Message); }
        }

        // ------------------------------------------------------------------ crafting

        static MethodInfo CraftBegin()
        {
            if (_craftBegin == null) _craftBegin = AccessTools.Method(typeof(Crafter), "OnCraftingBegin", new[] { typeof(TechType), typeof(float) });
            return _craftBegin;
        }

        static MethodInfo CraftEnd()
        {
            if (_craftEnd == null) _craftEnd = AccessTools.Method(typeof(Crafter), "OnCraftingEnd", new Type[0]);
            return _craftEnd;
        }

        static void OnCraft(int from, ByteReader r)
        {
            string id = r.Str();
            string path = r.Str();
            TechType tech = SyncKit.Tech(r);
            float duration = r.F32();
            var t = SyncKit.FindChild(id, path);
            var crafter = t != null ? t.GetComponent<Crafter>() : null;
            if (crafter == null) return;   // not loaded here, or not a crafter
            try
            {
                var begin = CraftBegin();
                if (begin == null) return;
                SyncKit.Apply(() => begin.Invoke(crafter, new object[] { tech, duration }));
                if (Plugin.I != null) Plugin.I.StartCoroutine(CraftEndLater(crafter, duration));
            }
            catch (Exception e) { SyncKit.Warn("machines: craft visual: " + e.Message); }
        }

        static IEnumerator CraftEndLater(Crafter crafter, float duration)
        {
            yield return new WaitForSeconds(Mathf.Max(0.1f, duration) + 0.2f);
            if (crafter == null) yield break;
            try
            {
                var end = CraftEnd();
                if (end != null) SyncKit.Apply(() => end.Invoke(crafter, new object[0]));
            }
            catch (Exception e) { SyncKit.Warn("machines: craft end visual: " + e.Message); }
        }

        // ------------------------------------------------------------------ power

        // The list of power sources is looked up rarely (scanning the whole scene is the expensive part) and kept with
        // each source's id and path, so the 2-second tick only reads numbers.
        sealed class Src { public PowerSource Ps; public string Id, Path, Key; }
        readonly List<Src> _sources = new List<Src>();
        float _nextRefresh;
        readonly List<Vector3> _origins = new List<Vector3>();

        void RefreshSources()
        {
            _sources.Clear();
            foreach (var ps in UnityEngine.Object.FindObjectsOfType<PowerSource>())
            {
                if (ps == null) continue;
                string id, path;
                if (!SyncKit.IdAndPath(ps, out id, out path)) continue;
                _sources.Add(new Src { Ps = ps, Id = id, Path = path, Key = id + "/" + path });
            }
        }

        void SendPower()
        {
            float now = Time.unscaledTime;
            if (now >= _nextRefresh) { _nextRefresh = now + 10f; RefreshSources(); }
            _cycle++;
            bool full = _cycle % FullEvery == 0;
            if (_lastPower.Count > DictMax) _lastPower.Clear();

            // around the host and around every other player, so a base the joiner works in is covered too
            _origins.Clear();
            if (Player.main != null) _origins.Add(Player.main.transform.position);
            if (Plugin.I != null) _origins.AddRange(Plugin.I.RemotePlayerPositions());
            float r2 = PowerRange * PowerRange;

            var ids = new List<string>();
            var paths = new List<string>();
            var values = new List<float>();
            foreach (var src in _sources)
            {
                var ps = src.Ps;
                if (ps == null || !ps.isActiveAndEnabled) continue;
                var pos = ps.transform.position;
                bool near = false;
                for (int i = 0; i < _origins.Count; i++) if ((pos - _origins[i]).sqrMagnitude <= r2) { near = true; break; }
                if (!near) continue;
                // power cells in a personal inventory are not synced
                var pk = ps.GetComponentInParent<Pickupable>();
                if (pk != null && pk.attached) continue;
                float power = ps.GetPower();
                float last;
                bool changed = !_lastPower.TryGetValue(src.Key, out last) || Mathf.Abs(last - power) > 0.05f;
                if (!changed && !full) continue;
                _lastPower[src.Key] = power;
                ids.Add(src.Id); paths.Add(src.Path); values.Add(power);
                if (ids.Count >= PowerMax) break;
            }
            if (ids.Count == 0) return;

            SyncKit.Send("mach.pwr", w =>
            {
                w.VarI(ids.Count);
                for (int i = 0; i < ids.Count; i++)
                {
                    w.Str(ids[i]);
                    w.Str(paths[i]);
                    w.F32(values[i]);
                }
            });
        }

        static void OnPower(int from, ByteReader r)
        {
            if (!HostSettings.SharedPower) return;
            int count = (int)r.VarI();
            if (count < 0 || count > PowerMax) return;
            var targets = new List<PowerSource>();
            var values = new List<float>();
            for (int i = 0; i < count; i++)
            {
                string id = r.Str();
                string path = r.Str();
                float power = r.F32();
                if (float.IsNaN(power) || power < 0f) continue;
                var t = SyncKit.FindChild(id, path);
                var ps = t != null ? t.GetComponent<PowerSource>() : null;
                if (ps == null) continue;   // not loaded here: fine, skip
                targets.Add(ps); values.Add(power);
            }
            if (targets.Count == 0) return;
            SyncKit.Apply(() =>
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    try { targets[i].SetPower(values[i]); }
                    catch (Exception e) { SyncKit.Warn("machines: set power: " + e.Message); }
                }
            });
        }

        // ------------------------------------------------------------------ signs and labels

        static void OnSign(int from, ByteReader r)
        {
            int kind = r.U8();
            string id = r.Str();
            string path = r.Str();
            string text = r.Str() ?? "";
            int color = (int)r.VarI();
            int scale = (int)r.VarI();
            bool bg = r.Bool();
            bool[] elems = ReadBools(r);
            var t = SyncKit.FindChild(id, path);
            if (t == null) return;

            if (kind == 0)
            {
                var sign = t.GetComponent<Sign>();
                if (sign == null) return;
                SyncKit.Apply(() =>
                {
                    sign.text = text;
                    sign.colorIndex = color;
                    sign.scaleIndex = scale;
                    sign.backgroundEnabled = bg;
                    sign.elements = elems;
                    var input = sign.signInput;
                    if (input != null) ApplyInput(input, text, color, scale, bg, elems);
                });
            }
            else
            {
                var label = t.GetComponent<ColoredLabel>();
                if (label == null) return;
                SyncKit.Apply(() =>
                {
                    label.text = text;
                    label.colorIndex = color;
                    var input = label.signInput;
                    if (input != null)
                    {
                        try { input.text = text; input.colorIndex = color; }   // a label has no size, background or element settings
                        catch (Exception e) { SyncKit.Warn("machines: label input: " + e.Message); }
                    }
                });
            }
        }

        static void ApplyInput(uGUI_SignInput input, string text, int color, int scale, bool bg, bool[] elems)
        {
            try
            {
                input.text = text;
                input.colorIndex = color;
                input.scaleIndex = scale;
                input.elementsState = elems;
                input.SetBackground(bg);
            }
            catch (Exception e) { SyncKit.Warn("machines: sign input: " + e.Message); }
        }

        static IEnumerator SendSignLater(uGUI_SignInput input)
        {
            // let the game store the new text on the sign first
            yield return new WaitForEndOfFrame();
            try { SendSign(input); }
            catch (Exception e) { SyncKit.Warn("machines: sign send: " + e.Message); }
        }

        static void SendSign(uGUI_SignInput input)
        {
            if (input == null || !SyncKit.ShouldSend) return;
            var sign = input.GetComponentInParent<Sign>();
            if (sign != null)
            {
                string id, path;
                if (!SyncKit.IdAndPath(sign, out id, out path)) return;
                string text = sign.text ?? "";
                int color = sign.colorIndex, scale = sign.scaleIndex;
                bool bg = sign.backgroundEnabled;
                bool[] elems = sign.elements ?? new bool[0];
                SyncKit.Send("mach.sign", w => WriteSign(w, 0, id, path, text, color, scale, bg, elems));
                return;
            }
            var label = input.GetComponentInParent<ColoredLabel>();
            if (label != null)
            {
                string id, path;
                if (!SyncKit.IdAndPath(label, out id, out path)) return;
                string text = label.text ?? "";
                int color = label.colorIndex;
                SyncKit.Send("mach.sign", w => WriteSign(w, 1, id, path, text, color, 0, false, new bool[0]));
            }
        }

        static void WriteSign(ByteWriter w, byte kind, string id, string path, string text, int color, int scale, bool bg, bool[] elems)
        {
            w.U8(kind);
            w.Str(id);
            w.Str(path);
            w.Str(text);
            w.VarI(color);
            w.VarI(scale);
            w.Bool(bg);
            w.VarI(elems.Length);
            for (int i = 0; i < elems.Length; i++) w.Bool(elems[i]);
        }

        static bool[] ReadBools(ByteReader r)
        {
            int n = (int)r.VarI();
            if (n < 0 || n > 256) n = 0;
            var a = new bool[n];
            for (int i = 0; i < n; i++) a[i] = r.Bool();
            return a;
        }

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(CrafterLogic), "Craft")]
        static class P_Craft
        {
            static void Postfix(CrafterLogic __instance, TechType techType, float craftTime, bool __result)
            {
                try
                {
                    if (!__result || __instance == null || !SyncKit.ShouldSend) return;
                    var crafter = __instance.GetComponent<Crafter>();
                    if (crafter == null) crafter = __instance.GetComponentInParent<Crafter>();
                    if (crafter == null) return;   // rocket constructor and other logic users: not synced here
                    string id, path;
                    if (!SyncKit.IdAndPath(crafter, out id, out path)) return;
                    SyncKit.Send("mach.craft", w =>
                    {
                        w.Str(id);
                        w.Str(path);
                        SyncKit.Tech(w, techType);
                        w.F32(craftTime);
                    });
                }
                catch (Exception e) { SyncKit.Warn("machines: craft patch: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(uGUI_SignInput), "OnDeselect")]
        static class P_SignDeselect
        {
            static void Postfix(uGUI_SignInput __instance)
            {
                try
                {
                    if (__instance == null || !SyncKit.ShouldSend || Plugin.I == null) return;
                    Plugin.I.StartCoroutine(SendSignLater(__instance));
                }
                catch (Exception e) { SyncKit.Warn("machines: sign patch: " + e.Message); }
            }
        }
    }
}
