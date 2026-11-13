using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Remote players look like a real player: a copy of the local player's animated body, driven by the same Animator
    /// parameters the owner's game produces (swim, walk, turn, tool, underwater...). The parameters are sent as a small
    /// byte array in NetPlayerState, so nothing here needs to know the parameter names.
    /// If the body cannot be copied, a capsule is used instead.
    /// </summary>
    static class PlayerAvatar
    {
        // where the body sits relative to the player's own transform, measured from the local player
        public static Vector3 RelPos = new Vector3(0, -0.9f, 0);
        public static Quaternion RelRot = Quaternion.identity;

        static AnimatorControllerParameter[] _localParams;
        static RuntimeAnimatorController _localParamsFor;

        public static Animator LocalAnimator()
        {
            var p = Player.main;
            if (p == null) return null;
            Animator a = null;
            try
            {
                var arms = p.armsController;
                if (arms != null)
                {
                    a = Traverse.Create(arms).Field("animator").GetValue<Animator>();
                    if (a == null) a = arms.GetComponentInChildren<Animator>(true);
                }
            }
            catch { }
            if (a == null) a = p.GetComponentInChildren<Animator>(true);
            return a;
        }

        // ------------------------------------------------------------------ parameters <-> bytes

        public static AnimatorControllerParameter[] ParamsOf(Animator a)
        {
            if (a == null || a.runtimeAnimatorController == null) return null;
            return a.parameters;
        }

        public static byte[] Encode(Animator a)
        {
            if (a == null || !a.isInitialized || a.runtimeAnimatorController == null) return null;
            if (_localParams == null || _localParamsFor != a.runtimeAnimatorController)
            {
                _localParams = a.parameters;
                _localParamsFor = a.runtimeAnimatorController;
            }
            var ms = new MemoryStream(_localParams.Length * 2);
            foreach (var p in _localParams)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        Put16(ms, Mathf.RoundToInt(Mathf.Clamp(a.GetFloat(p.nameHash), -1000f, 1000f) * 32f)); break;
                    case AnimatorControllerParameterType.Int:
                        Put16(ms, Mathf.Clamp(a.GetInteger(p.nameHash), -32000, 32000)); break;
                    case AnimatorControllerParameterType.Bool:
                        ms.WriteByte(a.GetBool(p.nameHash) ? (byte)1 : (byte)0); break;
                }
            }
            return ms.ToArray();
        }

        public static void Apply(Animator a, AnimatorControllerParameter[] ps, byte[] data)
        {
            if (a == null || ps == null || data == null || data.Length == 0) return;
            int i = 0;
            foreach (var p in ps)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        if (i + 2 > data.Length) return;
                        a.SetFloat(p.nameHash, Get16(data, i) / 32f); i += 2; break;
                    case AnimatorControllerParameterType.Int:
                        if (i + 2 > data.Length) return;
                        a.SetInteger(p.nameHash, Get16(data, i)); i += 2; break;
                    case AnimatorControllerParameterType.Bool:
                        if (i + 1 > data.Length) return;
                        a.SetBool(p.nameHash, data[i] != 0); i += 1; break;
                }
            }
        }

        /// <summary>Like Apply, but float parameters (speed, turn, pitch...) glide to their new value instead of jumping at every
        /// network update. Call every frame. Returns true while a float is still on its way.</summary>
        public static bool ApplyDamped(Animator a, AnimatorControllerParameter[] ps, byte[] data, float dt)
        {
            if (a == null || ps == null || data == null || data.Length == 0) return false;
            bool moving = false;
            int i = 0;
            foreach (var p in ps)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        {
                            if (i + 2 > data.Length) return moving;
                            float target = Get16(data, i) / 32f; i += 2;
                            float cur = a.GetFloat(p.nameHash);
                            float diff = Mathf.Abs(target - cur);
                            if (diff < 0.002f) { if (diff > 0f) a.SetFloat(p.nameHash, target); }
                            else if (diff > 45f) a.SetFloat(p.nameHash, target);   // looks like an angle that wrapped: no gliding
                            else { a.SetFloat(p.nameHash, target, 0.07f, Mathf.Max(dt, 0.001f)); moving = true; }
                            break;
                        }
                    case AnimatorControllerParameterType.Int:
                        if (i + 2 > data.Length) return moving;
                        { int v = Get16(data, i); if (a.GetInteger(p.nameHash) != v) a.SetInteger(p.nameHash, v); }
                        i += 2; break;
                    case AnimatorControllerParameterType.Bool:
                        if (i + 1 > data.Length) return moving;
                        { bool v = data[i] != 0; if (a.GetBool(p.nameHash) != v) a.SetBool(p.nameHash, v); }
                        i += 1; break;
                }
            }
            return moving;
        }

        static void Put16(MemoryStream ms, int v) { ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)((v >> 8) & 0xFF)); }
        internal static int Get16(byte[] d, int i) { return (short)(d[i] | (d[i + 1] << 8)); }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>The object in the local player's hands (a tool, a fish...), or null.</summary>
        static bool _heldWarned;
        public static GameObject LocalHeldObject()
        {
            var inv = Inventory.main;
            if (inv == null) return null;
            object o = null;
            try { o = Traverse.Create(inv).Method("GetHeldObject").GetValue(); }
            catch (Exception e)
            {
                if (!_heldWarned) { _heldWarned = true; if (Plugin.I != null) Plugin.I.Log.Warn("held item lookup failed: " + e.Message); }
                return null;
            }
            var c = o as Component;
            return c != null ? c.gameObject : o as GameObject;
        }

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        /// <summary>The held item and its parents (up to the player), each with its name and local pose: how the game hangs the item on the hand.</summary>
        public static string RigOf(GameObject go)
        {
            try
            {
                var sb = new StringBuilder();
                var t = go.transform;
                var stop = Player.main != null ? Player.main.transform : null;
                for (int i = 0; i < 8 && t != null && t != stop; i++, t = t.parent)
                {
                    if (i > 0) sb.Append(';');
                    var p = t.localPosition; var q = t.localRotation; var sc = t.localScale;
                    sb.Append(t.name.Replace(';', '_').Replace('|', '_')).Append('|')
                      .Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append('|')
                      .Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append('|')
                      .Append(F(sc.x)).Append(',').Append(F(sc.y)).Append(',').Append(F(sc.z));
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ what the held item looks like in the owner's first-person view

        static string HexBits(List<bool> bits)
        {
            var sb = new StringBuilder((bits.Count + 3) / 4);
            for (int i = 0; i < bits.Count; i += 4)
            {
                int v = 0;
                for (int k = 0; k < 4; k++) if (i + k < bits.Count && bits[i + k]) v |= 1 << k;
                sb.Append("0123456789abcdef"[v]);
            }
            return sb.ToString();
        }

        static bool BitAt(string hex, int i)
        {
            int c = i / 4;
            if (c >= hex.Length) return true;
            char ch = hex[c];
            int v = ch >= 'a' ? ch - 'a' + 10 : ch - '0';
            return (v & (1 << (i % 4))) != 0;
        }

        static string NameHash(Transform[] ts)
        {
            uint h = 2166136261;
            foreach (var t in ts) { foreach (char c in t.name) h = (h ^ c) * 16777619; h = (h ^ (uint)t.childCount) * 16777619; }
            return h.ToString("x");
        }

        static string HexBytes(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static byte[] BytesHex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        static byte[] AnimBytes(Animator a, AnimatorControllerParameter[] ps)
        {
            var ms = new MemoryStream(ps.Length * 2);
            foreach (var p in ps)
            {
                int v;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        v = Mathf.RoundToInt(Mathf.Clamp(a.GetFloat(p.nameHash), -1000f, 1000f) * 32f); Put16(ms, v); break;
                    case AnimatorControllerParameterType.Int:
                        v = Mathf.Clamp(a.GetInteger(p.nameHash), -32000, 32000); Put16(ms, v); break;
                    case AnimatorControllerParameterType.Bool:
                        ms.WriteByte(a.GetBool(p.nameHash) ? (byte)1 : (byte)0); break;
                }
            }
            return ms.ToArray();
        }

        /// <summary>
        /// What the held object is showing the owner right now: which of its child objects and renderers are switched on (the game turns
        /// the hand view's parts on and the world model's parts off), and where each animator is (a fish held in the hand is in its
        /// held pose, not swimming). Sent next to the rig; the receiver puts its copy into the same state.
        /// </summary>
        public static string LookOf(GameObject go)
        {
            try
            {
                var ts = go.GetComponentsInChildren<Transform>(true);
                if (ts.Length > 400) return "";
                var act = new List<bool>(ts.Length);
                foreach (var t in ts) act.Add(t.gameObject.activeSelf);
                var rs = go.GetComponentsInChildren<Renderer>(true);
                var ren = new List<bool>(rs.Length);
                foreach (var r in rs) ren.Add(r != null && r.enabled);
                var sb = new StringBuilder("v1/");
                sb.Append(ts.Length).Append('/').Append(NameHash(ts)).Append('/').Append(HexBits(act))
                  .Append('/').Append(rs.Length).Append('/').Append(HexBits(ren)).Append('/');
                var ans = go.GetComponentsInChildren<Animator>(true);
                for (int i = 0; i < ans.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var a = ans[i];
                    if (a == null || !a.isActiveAndEnabled || !a.isInitialized || a.runtimeAnimatorController == null) { sb.Append('-'); continue; }
                    var st = a.GetCurrentAnimatorStateInfo(0);
                    sb.Append(st.fullPathHash).Append(':').Append(st.normalizedTime.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(HexBytes(AnimBytes(a, a.parameters)));
                }
                return sb.Length <= 2500 ? sb.ToString() : sb.ToString(0, sb.ToString().LastIndexOf('/') + 1);   // too long: leave the animators out
            }
            catch { return ""; }
        }

        static bool _lookWarned;

        /// <summary>Put a copy of the held item into the state captured by LookOf. Returns false if the copy is not the same kind of object.</summary>
        public static bool ApplyLook(GameObject copy, string look)
        {
            if (copy == null || string.IsNullOrEmpty(look)) return false;
            try
            {
                var f = look.Split('/');
                if (f.Length < 7 || f[0] != "v1") return false;
                var ts = copy.GetComponentsInChildren<Transform>(true);
                int n;
                if (!int.TryParse(f[1], out n) || n != ts.Length || f[2] != NameHash(ts))
                {
                    if (!_lookWarned && Plugin.I != null) { _lookWarned = true; Plugin.I.Log.Info("held item look: the copy is not built like the owner's object (" + ts.Length + " parts here, " + f[1] + " there); showing it as it is"); }
                    return false;
                }
                // parents first, so a child under a switched-off parent keeps its own flag
                for (int i = 1; i < ts.Length; i++) ts[i].gameObject.SetActive(BitAt(f[3], i));
                var rs = copy.GetComponentsInChildren<Renderer>(true);
                int m; int.TryParse(f[4], out m);
                if (m == rs.Length) for (int i = 0; i < rs.Length; i++) if (rs[i] != null) rs[i].enabled = BitAt(f[5], i);
                var ans = copy.GetComponentsInChildren<Animator>(true);
                var parts = f[6].Split(',');
                for (int i = 0; i < ans.Length && i < parts.Length; i++)
                {
                    if (parts[i] == "-" || ans[i] == null || !ans[i].isActiveAndEnabled || ans[i].runtimeAnimatorController == null) continue;
                    var q = parts[i].Split(':');
                    if (q.Length != 3) continue;
                    int hash; float time;
                    if (!int.TryParse(q[0], out hash) || !float.TryParse(q[1], NumberStyles.Float, CultureInfo.InvariantCulture, out time)) continue;
                    var a = ans[i];
                    a.speed = 0f;
                    Apply(a, a.parameters, BytesHex(q[2]));
                    if (a.HasState(0, hash)) a.Play(hash, 0, time - Mathf.Floor(time));
                    a.Update(0f);
                }
                return true;
            }
            catch (Exception e) { if (Plugin.I != null) Plugin.I.Log.Warn("held item look: " + e.Message); return false; }
        }

        // ------------------------------------------------------------------ worn equipment (tank, fins, rebreather, suit...)
        // The game shows what the local player wears as parts of the body model, switched on or off. The remote body is a copy of that model, so the
        // same switches are sent and applied: bits for "this renderer is on" and "this renderer was seen to change" (a part that never changes,
        // like a head hidden in the first-person view, is only ever switched on on the copy, never off).

        static Renderer[] _wornR; static GameObject _wornRoot; static bool[] _wornPrev, _wornDyn;

        public static string WornOf()
        {
            try
            {
                var an = LocalAnimator();
                if (an == null || Player.main == null || an.gameObject == Player.main.gameObject) return "";
                var root = an.gameObject;
                if (root != _wornRoot || _wornR == null) { _wornRoot = root; _wornR = root.GetComponentsInChildren<Renderer>(true); _wornPrev = null; _wornDyn = null; }
                var rs = _wornR;
                if (rs.Length == 0 || rs.Length > 300) return "";
                if (_wornPrev == null || _wornPrev.Length != rs.Length) { _wornPrev = new bool[rs.Length]; _wornDyn = new bool[rs.Length]; for (int i = 0; i < rs.Length; i++) _wornPrev[i] = rs[i] != null && rs[i].gameObject.activeInHierarchy; }
                var on = new List<bool>(rs.Length); var dyn = new List<bool>(rs.Length);
                var ts = new Transform[rs.Length];
                for (int i = 0; i < rs.Length; i++)
                {
                    bool a = rs[i] != null && rs[i].gameObject.activeInHierarchy;
                    if (a != _wornPrev[i]) { _wornDyn[i] = true; _wornPrev[i] = a; }
                    on.Add(a); dyn.Add(_wornDyn[i]);
                    ts[i] = rs[i] != null ? rs[i].transform : root.transform;
                }
                return "w1/" + rs.Length + "/" + NameHash(ts) + "/" + HexBits(on) + "/" + HexBits(dyn);
            }
            catch { return ""; }
        }

        static bool _wornWarned;

        /// <summary>Puts a remote body copy into the worn state captured by WornOf. False if the copy is not built the same.</summary>
        public static bool ApplyWorn(Transform body, string worn)
        {
            if (body == null || string.IsNullOrEmpty(worn)) return false;
            try
            {
                var f = worn.Split('/');
                if (f.Length < 5 || f[0] != "w1") return false;
                var rs = body.GetComponentsInChildren<Renderer>(true);
                int n;
                if (!int.TryParse(f[1], out n) || n != rs.Length) { if (!_wornWarned && Plugin.I != null) { _wornWarned = true; Plugin.I.Log.Info("worn equipment: the body copy has " + rs.Length + " renderers, the owner's " + f[1] + "; not applied"); } return false; }
                var ts = new Transform[rs.Length];
                for (int i = 0; i < rs.Length; i++) ts[i] = rs[i] != null ? rs[i].transform : body;
                if (NameHash(ts) != f[2]) { if (!_wornWarned && Plugin.I != null) { _wornWarned = true; Plugin.I.Log.Info("worn equipment: the body copy is built differently; not applied"); } return false; }
                for (int i = 0; i < rs.Length; i++)
                {
                    var r = rs[i]; if (r == null) continue;
                    bool want = BitAt(f[3], i), dyn = BitAt(f[4], i);
                    if (want)
                    {
                        for (var t = r.transform; t != null && t != body.parent; t = t.parent) { if (!t.gameObject.activeSelf) t.gameObject.SetActive(true); if (t == body) break; }
                        r.enabled = true;
                    }
                    else if (dyn)
                    {
                        if (r.transform.childCount == 0) r.gameObject.SetActive(false); else r.enabled = false;
                    }
                }
                return true;
            }
            catch (Exception e) { if (Plugin.I != null) Plugin.I.Log.Warn("worn equipment: " + e.Message); return false; }
        }

        internal struct RigNode { public string Name; public Vector3 P; public Quaternion Q; public Vector3 S; }

        internal static List<RigNode> ParseRig(string rig)
        {
            var list = new List<RigNode>();
            if (string.IsNullOrEmpty(rig)) return list;
            try
            {
                foreach (var part in rig.Split(';'))
                {
                    var f = part.Split('|');
                    if (f.Length != 4) return new List<RigNode>();
                    var p = f[1].Split(','); var q = f[2].Split(','); var sc = f[3].Split(',');
                    Func<string, float> n = x => float.Parse(x, CultureInfo.InvariantCulture);
                    list.Add(new RigNode { Name = f[0], P = new Vector3(n(p[0]), n(p[1]), n(p[2])),
                        Q = new Quaternion(n(q[0]), n(q[1]), n(q[2]), n(q[3])), S = new Vector3(n(sc[0]), n(sc[1]), n(sc[2])) });
                }
            }
            catch { return new List<RigNode>(); }
            return list;
        }

        /// <summary>TechType name of an object, "" for none or unknown.</summary>
        public static string TechNameOf(GameObject go)
        {
            try
            {
                if (go == null) return "";
                var t = CraftData.GetTechType(go);
                return t == TechType.None ? "" : t.ToString();
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ the body

        /// <summary>Copies the local player's animated body. Needs a loaded world. Returns null if it cannot.</summary>
        public static GameObject CreateBody(out Animator anim)
        {
            anim = null;
            var src = LocalAnimator();
            if (src == null) return null;
            if (src.gameObject == Player.main.gameObject) return null; // never clone the whole player

            // Instantiate under an inactive parent so no game script on the copy ever runs, then strip them.
            var hold = new GameObject("MP_hold");
            hold.SetActive(false);
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(src.gameObject, hold.transform);
                clone.name = "MP_Body";
                StripAll(clone);

                int renderers = 0;
                foreach (var t in clone.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0; // the first-person layer is invisible to the main camera
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    var sk = r as SkinnedMeshRenderer;
                    if (sk != null) sk.updateWhenOffscreen = true;
                    renderers++;
                }
                if (renderers == 0) { UnityEngine.Object.Destroy(clone); return null; }

                // remember where the body sits on the local player
                var pt = Player.main.transform;
                RelPos = pt.InverseTransformPoint(src.transform.position);
                RelRot = Quaternion.Inverse(pt.rotation) * src.transform.rotation;

                var scale = src.transform.lossyScale;
                clone.transform.SetParent(null, false);
                clone.transform.localScale = scale;
                UnityEngine.Object.DontDestroyOnLoad(clone);
                clone.SetActive(true);
                anim = clone.GetComponentInChildren<Animator>(true);
                if (anim != null)
                {
                    anim.enabled = true;
                    anim.applyRootMotion = false;
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }
                return clone;
            }
            catch (Exception e)
            {
                if (Plugin.I != null) Plugin.I.Log.Warn("player body copy failed, using a capsule: " + e.Message);
                if (clone != null) UnityEngine.Object.Destroy(clone);
                return null;
            }
            finally { UnityEngine.Object.Destroy(hold); }
        }

        /// <summary>
        /// Removes every game script, joint, collider, rigidbody, camera, light, audio source and particle system from a copy.
        /// A component that another one on the same object requires (Locomotion needs its Rigidbody, SwimBehaviour needs
        /// Locomotion...) goes only after the one that requires it, so Unity never refuses and nothing is left running.
        /// </summary>
        internal static void StripAll(GameObject root)
        {
            for (int pass = 0; pass < 16; pass++)
            {
                bool left = false, any = false;
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform) continue;
                    if (!(c is MonoBehaviour || c is Joint || c is Collider || c is Rigidbody || c is Camera || c is AudioListener
                          || c is AudioSource || c is Light || c is ParticleSystem)) continue;
                    if (RequiredByOther(c)) { left = true; continue; }
                    try { UnityEngine.Object.DestroyImmediate(c); any = true; } catch { left = true; }
                }
                if (!left || !any) break;
            }
        }

        static bool RequiredByOther(Component c)
        {
            var ct = c.GetType();
            foreach (var o in c.gameObject.GetComponents<Component>())
            {
                if (o == null || o == c) continue;
                object[] at;
                try { at = o.GetType().GetCustomAttributes(typeof(RequireComponent), true); } catch { continue; }
                foreach (RequireComponent r in at)
                    if ((r.m_Type0 != null && r.m_Type0.IsAssignableFrom(ct)) || (r.m_Type1 != null && r.m_Type1.IsAssignableFrom(ct))
                        || (r.m_Type2 != null && r.m_Type2.IsAssignableFrom(ct))) return true;
            }
            return false;
        }

        internal static void Strip<T>(GameObject root) where T : Component
        {
            foreach (var c in root.GetComponentsInChildren<T>(true)) if (c != null) { try { UnityEngine.Object.DestroyImmediate(c); } catch { } }
        }

        public static GameObject CreateCapsule()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "MP_Capsule";
            var col = go.GetComponent<Collider>();
            if (col) UnityEngine.Object.Destroy(col); // proxies must not push the real player around
            go.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f);
            return go;
        }
    }

    /// <summary>
    /// On a remote player's root object: owns its body, applies the replicated animation, and moves the root smoothly.
    /// The core delivers position and rotation at irregular moments; instead of chasing the newest value, the avatar
    /// keeps the last few values with their arrival times and draws the player a short, fixed time in the past
    /// (NetInterp.Delay, the same delay the vehicles use) by interpolating between two of them.
    /// </summary>
    [DefaultExecutionOrder(32001)]
    class RemoteAvatar : MonoBehaviour
    {
        public NetPlayerState state;
        public int OwnerPeer;
        public Transform body;
        public Animator anim;
        AnimatorControllerParameter[] _ps;
        byte[] _applied;
        float _nextTry;
        int _fails;
        Color _tinted = Color.white;
        bool _damping;

        struct Snap { public float T; public Vector3 P; public Quaternion R; }
        readonly Snap[] _q = new Snap[6];
        int _qn;
        Vector3 _seenP; Quaternion _seenR; bool _seen;
        Vector3 _shownP; Quaternion _shownR; bool _hasShown;

        string _wornApplied; float _nextWorn;
        bool _pdaWas; float _pdaLog; byte[] _pre;

        static bool IsPdaParam(string n) { return n != null && n.IndexOf("pda", StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>The swim parameters from before the PDA opened, with the owner's PDA parameters laid over them.</summary>
        byte[] MergePda(byte[] pre, byte[] live)
        {
            var o = (byte[])pre.Clone();
            int i = 0;
            foreach (var p in _ps)
            {
                int len = p.type == AnimatorControllerParameterType.Bool ? 1 : (p.type == AnimatorControllerParameterType.Trigger ? 0 : 2);
                if (i + len > live.Length || i + len > o.Length) break;
                if (IsPdaParam(p.name)) for (int k = 0; k < len; k++) o[i + k] = live[i + k];
                i += len;
            }
            return o;
        }

        /// <summary>Writes what the owner's body animator is doing while a PDA is held (parameters, layers, current states), so the next log shows how the controller builds that pose.</summary>
        void DescribeAnim(byte[] data)
        {
            try
            {
                if (Plugin.I == null || anim == null || _ps == null) return;
                var sb = new StringBuilder("pda pose: params");
                int i = 0;
                foreach (var p in _ps)
                {
                    if (p.type == AnimatorControllerParameterType.Float) { if (i + 2 > data.Length) break; sb.Append(' ').Append(p.name).Append('=').Append((PlayerAvatar.Get16(data, i) / 32f).ToString("0.##")); i += 2; }
                    else if (p.type == AnimatorControllerParameterType.Int) { if (i + 2 > data.Length) break; sb.Append(' ').Append(p.name).Append('=').Append(PlayerAvatar.Get16(data, i)); i += 2; }
                    else if (p.type == AnimatorControllerParameterType.Bool) { if (i + 1 > data.Length) break; sb.Append(' ').Append(p.name).Append('=').Append(data[i] != 0 ? "T" : "F"); i += 1; }
                }
                sb.Append(" | layers");
                for (int l = 0; l < anim.layerCount; l++)
                {
                    var st = anim.GetCurrentAnimatorStateInfo(l);
                    sb.Append(' ').Append(l).Append(':').Append(anim.GetLayerName(l)).Append(" w=").Append(anim.GetLayerWeight(l).ToString("0.##")).Append(" state=").Append(st.fullPathHash).Append(" t=").Append(st.normalizedTime.ToString("0.##"));
                }
                Plugin.I.Log.Info(sb.ToString());
            }
            catch { }
        }

        public void Drive()
        {
            if (body == null) TryAttach();
            if (body == null) return;
            if (state != null && Time.unscaledTime >= _nextWorn)
            {
                _nextWorn = Time.unscaledTime + 0.5f;
                string w = state.worn ?? "";
                if (w.Length > 0 && w != _wornApplied) { PlayerAvatar.ApplyWorn(body, w); _wornApplied = w; }
            }
            // the owner sends the pose of its body itself, so the copy sits at the root with no offset
            body.localPosition = Vector3.zero;
            body.localRotation = Quaternion.identity;
            if (state != null && state.color != _tinted)
            {
                _tinted = state.color;
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_Color", Color.Lerp(Color.white, _tinted, 0.55f));
                foreach (var r in body.GetComponentsInChildren<Renderer>(true)) r.SetPropertyBlock(mpb);
            }
            if (anim != null && _ps == null) _ps = PlayerAvatar.ParamsOf(anim);   // the controller may not have been ready when the body was attached
            if (anim != null && state != null && state.anim != null && state.anim.Length > 0)
            {
                byte[] incoming = state.anim;
                // PDA in hand: the owner's controller leaves the swimming state for a standing one while the PDA is up. Keep the swim
                // parameters of the moment before it opened and take only the PDA ones from the owner, so the copy swims and holds the PDA.
                bool pda = state.held == "PDA";
                if (pda != _pdaWas) { _pdaWas = pda; _pdaLog = 0f; }
                if (pda && _ps != null)
                {
                    if (_pdaLog <= 0f || Time.unscaledTime >= _pdaLog) { _pdaLog = Time.unscaledTime + 8f; DescribeAnim(incoming); }
                    if (_pre != null && _pre.Length == incoming.Length) incoming = MergePda(_pre, incoming);
                }
                else if (!pda) _pre = state.anim;
                if (!PlayerAvatar.Same(incoming, _applied)) { _applied = incoming; _damping = true; }
                if (_damping)
                {
                    try { _damping = PlayerAvatar.ApplyDamped(anim, _ps, _applied, Time.unscaledDeltaTime); }
                    catch { _damping = false; }
                }
            }
        }

        // Runs after the plugin's Update (which still sets a rough position) and replaces it with the smoothed one.
        void LateUpdate()
        {
            if (state == null) return;
            float now = Time.unscaledTime;
            var p = state.pos; var r = state.rot;
            if (!_seen)
            {
                if (p == Vector3.zero) return;   // nothing received yet
                _seen = true; _seenP = p; _seenR = r;
                Push(now, p, r);
            }
            else if (p != _seenP || r != _seenR)
            {
                _seenP = p; _seenR = r;
                Push(now, p, r);
            }
            if (_qn == 0) return;

            Vector3 tp; Quaternion tr;
            float rt = now - NetInterp.Delay;
            if (rt >= _q[_qn - 1].T) { tp = _q[_qn - 1].P; tr = _q[_qn - 1].R; }   // hold: no guessing, so a stopping player does not overshoot
            else if (rt <= _q[0].T) { tp = _q[0].P; tr = _q[0].R; }
            else
            {
                int i = 0;
                while (i < _qn - 2 && _q[i + 1].T <= rt) i++;
                var a = _q[i]; var b = _q[i + 1];
                float span = b.T - a.T;
                float u = span > 1e-4f ? Mathf.Clamp01((rt - a.T) / span) : 1f;
                tp = Vector3.Lerp(a.P, b.P, u);
                tr = Quaternion.Slerp(a.R, b.R, u);
            }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (!_hasShown || (tp - _shownP).sqrMagnitude > 400f) { _shownP = tp; _shownR = tr; _hasShown = true; }
            else
            {
                _shownP = Vector3.Lerp(_shownP, tp, 1f - Mathf.Exp(-35f * dt));
                _shownR = Quaternion.Slerp(_shownR, tr, 1f - Mathf.Exp(-25f * dt));
            }
            transform.SetPositionAndRotation(_shownP, _shownR);
        }

        void Push(float now, Vector3 p, Quaternion r)
        {
            if (_qn > 0 && (p - _q[_qn - 1].P).sqrMagnitude > 400f) { _qn = 0; _hasShown = false; }   // teleport: do not slide across the map
            if (_qn > 0 && now <= _q[_qn - 1].T) now = _q[_qn - 1].T + 0.001f;
            if (_qn == _q.Length) { Array.Copy(_q, 1, _q, 0, _qn - 1); _qn--; }
            _q[_qn].T = now; _q[_qn].P = p; _q[_qn].R = r;
            _qn++;
        }

        void TryAttach()
        {
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 1f;
            if (Player.main == null) return;   // the body is copied from the local player, which needs a loaded world
            Animator a = null;
            var b = _fails < 3 ? PlayerAvatar.CreateBody(out a) : null;
            if (b == null)
            {
                _fails++;
                if (_fails < 3) return;
                b = PlayerAvatar.CreateCapsule(); a = null;
            }
            b.transform.SetParent(transform, false);
            body = b.transform; anim = a;
            if (anim != null) _ps = PlayerAvatar.ParamsOf(anim);
            // name tag just above the body
            var label = transform.Find("label");
            var rs = b.GetComponentsInChildren<Renderer>(true);
            if (rs.Length > 0)
            {
                var bounds = rs[0].bounds;
                foreach (var r in rs) bounds.Encapsulate(r.bounds);
                if (label != null) label.position = new Vector3(transform.position.x, bounds.max.y + 0.3f, transform.position.z);
                try { PvP.MakeSolid(this, bounds); } catch (Exception e) { if (Plugin.I != null) Plugin.I.Log.Warn("pvp: could not make the player solid: " + e.Message); }
            }
            if (GetComponent<HeldVisual>() == null) gameObject.AddComponent<HeldVisual>().Init(this);
            if (Plugin.I != null) Plugin.I.Log.Info("player body attached: " + b.name + " renderers=" + rs.Length + (anim != null ? " animated" : " (capsule)"));
        }
    }
    /// <summary>
    /// What a remote player holds, as a picture only: a copy of the item's prefab with every script, collider and body
    /// removed (and no UniqueIdentifier), so nobody can pick it up, click it or collide with it. It follows the avatar's
    /// hand; the animation of using it comes from the replicated animator parameters.
    /// </summary>
    [DefaultExecutionOrder(32002)]   // after RemoteAvatar (32001), so the hand it follows is already in this frame's pose
    class HeldVisual : MonoBehaviour
    {
        RemoteAvatar _av;
        string _shown = "";
        GameObject _vis;
        bool _loading, _posed, _rigOk;
        string _rigSeen;
        string _lookSeen;
        readonly List<GameObject> _rigObjs = new List<GameObject>();
        float _next;

        public void Init(RemoteAvatar a) { _av = a; }

        void Update()
        {
            if (_av == null || _av.state == null || _av.body == null) return;
            string want = _av.state.held ?? "";
            if (want == _shown) return;
            if (_loading) return;
            if (want.Length > 0 && Time.unscaledTime < _next) return;   // putting an item away is not delayed
            _next = Time.unscaledTime + 0.2f;
            Clear();
            _shown = want;
            if (want.Length == 0) return;
            TechType t;
            try { t = (TechType)Enum.Parse(typeof(TechType), want); } catch { return; }
            StartCoroutine(Build(t, want));
        }

        // Drawn in world space on the avatar's hand bone. (Not parented: the bone chain of the body copy carries its own
        // scale, which would resize the item. The offsets are in metres, in the hand's own axes.)
        static ConfigEntry<string> _hand, _off, _rot, _pdaOff, _pdaRot;
        static void BindCfg()
        {
            if (_hand != null || Plugin.I == null) return;
            try
            {
                _hand = Plugin.I.Config.Bind("HeldItem", "Hand", "Right", "Which hand of other players' bodies carries what they hold: Right or Left.");
                _off = Plugin.I.Config.Bind("HeldItem", "Offset", "0,0,0", "Where a held item sits relative to the hand, x,y,z in metres along the hand's axes. Edit and watch the other player's hand to tune.");
                _rot = Plugin.I.Config.Bind("HeldItem", "Rotation", "0,0,0", "Extra rotation of a held item in degrees, x,y,z.");
                _pdaOff = Plugin.I.Config.Bind("HeldItem", "PdaOffset", "0,0,0", "Where the PDA sits relative to the left hand of other players, x,y,z in metres along the hand's axes.");
                _pdaRot = Plugin.I.Config.Bind("HeldItem", "PdaRotation", "0,0,0", "Extra rotation of the PDA in the left hand, degrees x,y,z.");
            }
            catch { }
        }
        static Vector3 V3(string s)
        {
            try
            {
                var p = (s ?? "").Split(',');
                return new Vector3(float.Parse(p[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                                   float.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                                   float.Parse(p[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { return Vector3.zero; }
        }

        static Transform FindByName(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && t.name == name) return t;
            return null;
        }

        void DropRig()
        {
            if (_vis != null) _vis.transform.SetParent(null, true);
            foreach (var g in _rigObjs) if (g != null) UnityEngine.Object.Destroy(g);
            _rigObjs.Clear();
        }

        /// <summary>The nearest parent of the item (in the owner's first-person rig) that this body copy also has becomes its anchor; the parents in between are rebuilt with the same local poses.</summary>
        bool AttachRig(string rig)
        {
            DropRig();
            var nodes = PlayerAvatar.ParseRig(rig);
            if (nodes.Count < 2 || _vis == null || _av == null || _av.body == null) return false;
            Transform bone = null; int at = -1;
            for (int i = 1; i < nodes.Count && bone == null; i++)
            {
                bone = FindByName(_av.body, nodes[i].Name);
                if (bone != null) at = i;
            }
            if (bone == null)
            {
                if (Plugin.I != null) Plugin.I.Log.Info("held item: none of " + nodes.Count + " parents exist on the body copy, using the hand bone");
                return false;
            }
            var parent = bone;
            for (int j = at - 1; j >= 1; j--)
            {
                var g = new GameObject("MP_rig_" + nodes[j].Name);
                g.transform.SetParent(parent, false);
                g.transform.localPosition = nodes[j].P; g.transform.localRotation = nodes[j].Q; g.transform.localScale = nodes[j].S;
                _rigObjs.Add(g); parent = g.transform;
            }
            var tr = _vis.transform;
            tr.SetParent(parent, false);
            tr.localPosition = nodes[0].P; tr.localRotation = nodes[0].Q; tr.localScale = nodes[0].S;
            if (Plugin.I != null) Plugin.I.Log.Info("held item: hung on '" + bone.name + "' like in the owner's view");
            return true;
        }

        Transform _handBone; string _handFor; bool _handSearched;
        Transform FindHand()
        {
            string side = (_av != null && _av.state != null && _av.state.held == "PDA") ? "Left" : (_hand != null ? _hand.Value : "Right");
            bool left = string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase);
            if (_handSearched && _handFor == side) return _handBone;
            _handFor = side; _handBone = null; _handSearched = true;
            var a = _av.anim;
            try { if (a != null && a.isHuman) _handBone = a.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand); } catch { }
            if (_handBone == null)
            {
                string want = left ? "l" : "r";
                int bestLen = int.MaxValue;
                foreach (var t in _av.body.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name.ToLowerInvariant();
                    if (!n.Contains("hand") || n.Contains("finger") || n.Contains("thumb") || n.Contains("index") || n.Contains("ik")) continue;
                    bool side2 = left ? (n.Contains("left") || n.StartsWith("l_") || n.StartsWith("l.") || n.EndsWith("_l") || n.EndsWith(".l") || n.Contains("_l_"))
                                      : (n.Contains("right") || n.StartsWith("r_") || n.StartsWith("r.") || n.EndsWith("_r") || n.EndsWith(".r") || n.Contains("_r_"));
                    if (!side2 || n.Length >= bestLen) continue;
                    bestLen = n.Length; _handBone = t;
                }
            }
            if (Plugin.I != null)
                Plugin.I.Log.Info("held item: " + (left ? "left" : "right") + " hand bone = " + (_handBone != null ? _handBone.name : "none found, using the owner's pose"));
            return _handBone;
        }

        void LateUpdate()
        {
            if (_vis == null || _av == null || _av.state == null || _av.body == null) return;
            BindCfg();
            string wantNow = _av.state.held ?? "";
            string rigNow = _av.state.heldRig ?? "";
            if (rigNow != _rigSeen)
            {
                _rigSeen = rigNow;
                // the rig is tagged with its item ("name#rig"): one that belongs to another item is not used
                bool ours = wantNow.Length > 0 && rigNow.StartsWith(wantNow + "#", StringComparison.Ordinal);
                // the PDA hangs from the first-person camera rig, not from a hand: hung that way it floated beside the other player. It is held in a hand instead.
                _rigOk = ours && wantNow != "PDA" && AttachRig(rigNow.Substring(wantNow.Length + 1));
            }
            // what the owner's object shows in the hand view (parts switched on or off, held pose): copied onto this copy
            string lookNow = _av.state.heldLook ?? "";
            if (lookNow != _lookSeen)
            {
                _lookSeen = lookNow;
                if (lookNow.Length > 0 && wantNow.Length > 0 && lookNow.StartsWith(wantNow + "#", StringComparison.Ordinal))
                    PlayerAvatar.ApplyLook(_vis, lookNow.Substring(wantNow.Length + 1));
            }
            if (_rigOk) return;   // hung on the same bone, with the same local pose, as in the owner's first-person view
            var tr = _vis.transform;
            var hb = FindHand();
            if (hb != null)
            {
                bool pda = wantNow == "PDA";
                var po = pda ? _pdaOff : _off; var pr = pda ? _pdaRot : _rot;
                tr.rotation = hb.rotation * Quaternion.Euler(V3(pr != null ? pr.Value : "0,0,0"));
                tr.position = hb.position + hb.rotation * V3(po != null ? po.Value : "0,0,0");
                // the PDA's model origin is not its middle: put the middle of the model in the hand, not the origin (the origin hung it out beside the arm)
                if (pda) tr.position -= tr.rotation * ModelCenter();
                return;
            }
            // no hand bone: follow the pose the owner measured against its body
            var b = _av.body;
            Vector3 wp = b.TransformPoint(_av.state.heldPos);
            Quaternion wr = b.rotation * _av.state.heldRot;
            float k = 1f - Mathf.Exp(-30f * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            if (!_posed) { tr.position = wp; tr.rotation = wr; _posed = true; return; }
            tr.position = Vector3.Lerp(tr.position, wp, k);
            tr.rotation = Quaternion.Slerp(tr.rotation, wr, k);
        }

        Vector3 _center; bool _centerOk;
        /// <summary>The middle of the visual's renderers, in the visual's own space (cached).</summary>
        Vector3 ModelCenter()
        {
            if (_centerOk) return _center;
            _centerOk = true; _center = Vector3.zero;
            try
            {
                var rs = _vis.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) return _center;
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                _center = Quaternion.Inverse(_vis.transform.rotation) * (b.center - _vis.transform.position);
            }
            catch { }
            return _center;
        }

        void OnDestroy() { Clear(); }

        void Clear()
        {
            foreach (var g in _rigObjs) if (g != null) UnityEngine.Object.Destroy(g);
            _rigObjs.Clear();
            if (_vis != null) UnityEngine.Object.Destroy(_vis);
            _vis = null; _rigOk = false; _rigSeen = null; _lookSeen = null; _centerOk = false;
        }

        System.Collections.IEnumerator Build(TechType t, string want)
        {
            _loading = true;
            var task = CraftData.GetPrefabForTechTypeAsync(t, false);
            yield return task;
            GameObject prefab = null;
            try { prefab = task.GetResult(); } catch { }
            _loading = false;
            if (prefab == null || _shown != want || _av == null || _av.body == null || (_av.state.held ?? "") != want) yield break;

            var hold = new GameObject("MP_hold");
            hold.SetActive(false);
            GameObject c = null;
            try
            {
                c = UnityEngine.Object.Instantiate(prefab, hold.transform);
                c.name = "MP_Held";
                PlayerAvatar.StripAll(c);
                foreach (var tr in c.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = 0;
                foreach (var r in c.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                c.transform.SetParent(null, false);
                UnityEngine.Object.DontDestroyOnLoad(c);
                // the copy keeps its animator, which would play the creature's swim loop: freeze it on its first pose
                foreach (var an in c.GetComponentsInChildren<Animator>(true)) an.speed = 0f;
                foreach (var an in c.GetComponentsInChildren<Animation>(true)) an.enabled = false;
                c.SetActive(true);
                _vis = c;
                _posed = false; _rigOk = false; _rigSeen = null; _lookSeen = null;
            }
            catch (Exception e)
            {
                if (Plugin.I != null) Plugin.I.Log.Warn("held item visual failed: " + e.Message);
                if (c != null) UnityEngine.Object.Destroy(c);
            }
            finally { UnityEngine.Object.Destroy(hold); }
        }
    }
}
