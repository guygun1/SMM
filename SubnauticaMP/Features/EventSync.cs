using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Event forwarding for things nobody wrote a sync for. A listed method (a game method, or a method of any mod) is replayed
    /// on the other players when it runs: "this Cyclops caught fire" becomes "make that Cyclops catch fire" there. The call is sent as
    /// the component that owns the method (its object's unique id plus the path to it), the method, and its arguments.
    ///
    /// Arguments that can travel: bool, numbers, enums (TechType...), string, Vector3, Quaternion, Color, any component or GameObject of the
    /// shared world (by id and path), and any object that lives in a list, array or dictionary field of the component (a room of a
    /// Cyclops, a fire slot...), found again by position or key. A call with any other argument is not sent (logged once).
    ///
    /// Two modes per method:
    ///   host  the world simulates it (fire starts, leaks, random events). Only the host runs it; a joiner's own copy is skipped
    ///         and it runs there only when the host's call arrives. Only for methods that return nothing.
    ///   any   a player does it (douse a fire, repair a leak, press a button). It runs where it happened and is replayed on the others.
    ///   sum   (after any) a method with one float argument called every frame (a fire extinguisher): the amounts are added up and sent about 7 times a second.
    ///
    /// The list: a few game methods built in, plus one line per method in BepInEx/config/SubnauticaMP.events.txt:
    ///     Full.Type.Name.MethodName  host|any [sum]
    /// Both machines must list the same methods. A method that does not exist (mod not installed, other game version) is skipped and
    /// looked for again later.
    /// </summary>
    sealed class EventSync : ISyncFeature
    {
        public string Name { get { return "events"; } }

        sealed class Spec { public Type T; public string Method, Sig; public bool Host, Sum; }
        sealed class Want { public string Type, Method; public bool Host, Sum, Done; }
        sealed class Pend { public Spec S; public Component C; public string Id, Path; public float Total, Due; }

        static readonly string[] Defaults =
        {
            "CyclopsExternalDamageManager.CreatePoint host",   // a leak appears in a Cyclops
            "CyclopsDamagePoint.OnRepair any",                 // a leak is repaired
            "SubFire.CreateFire host",                         // a fire starts in a Cyclops room
            "Fire.Douse any sum",                              // a fire is put out
        };

        static readonly Dictionary<MethodBase, Spec> _specs = new Dictionary<MethodBase, Spec>();
        static readonly Dictionary<string, MethodInfo> _resolved = new Dictionary<string, MethodInfo>();
        static readonly List<Want> _wants = new List<Want>();
        static readonly Dictionary<string, Pend> _pend = new Dictionary<string, Pend>();
        static readonly HashSet<string> _warned = new HashSet<string>();
        static Harmony _h;
        static bool _loaded;
        static float _nextInstall;
        static int _sent, _got;
        static float _nextStats;

        // ------------------------------------------------------------------ list and patching

        public static void Install()
        {
            if (!_loaded)
            {
                _loaded = true;
                foreach (var d in Defaults) AddLine(d);
                try
                {
                    var path = Path.Combine(BepInEx.Paths.ConfigPath, "SubnauticaMP.events.txt");
                    if (!File.Exists(path))
                        File.WriteAllText(path,
                            "# Methods replayed on the other players when they run. One per line:  Full.Type.Name.MethodName  host|any [sum]\r\n" +
                            "#   host = the world simulates it (only the host runs it, joiners get the result)\r\n" +
                            "#   any  = a player does it (runs where it happened, replayed on the others)\r\n" +
                            "#   sum  = a method with one float argument that is called every frame; the amounts are added up\r\n" +
                            "# Every player must list the same methods. Example:\r\n" +
                            "# MyMod.Reactor.Meltdown host\r\n");
                    else
                        foreach (var line in File.ReadAllLines(path)) AddLine(line);
                }
                catch (Exception e) { SyncKit.Warn("events: config file: " + e.Message); }
            }
            if (_h == null) _h = new Harmony("com.guydevash.subnauticamp.events");
            foreach (var w in _wants) if (!w.Done) { try { Patch(w); } catch (Exception e) { SyncKit.Warn("events: " + w.Type + "." + w.Method + ": " + e.Message); w.Done = true; } }
        }

        static void AddLine(string line)
        {
            int hash = line.IndexOf('#'); if (hash >= 0) line = line.Substring(0, hash);
            var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) return;
            int dot = p[0].LastIndexOf('.'); if (dot <= 0) return;
            string mode = p[1].ToLowerInvariant();
            if (mode != "host" && mode != "any") return;
            _wants.Add(new Want { Type = p[0].Substring(0, dot), Method = p[0].Substring(dot + 1), Host = mode == "host", Sum = p.Length > 2 && p[2].ToLowerInvariant() == "sum" });
        }

        static void Patch(Want w)
        {
            var t = AccessTools.TypeByName(w.Type);
            if (t == null) return;                                   // not loaded (yet): tried again later
            var ms = AccessTools.GetDeclaredMethods(t).Where(m => m.Name == w.Method && !m.IsStatic && !m.IsAbstract && !m.IsGenericMethodDefinition).ToList();
            w.Done = true;
            if (ms.Count == 0) { SyncKit.Info("events: " + w.Type + " has no instance method " + w.Method + ", skipped"); return; }
            foreach (var m in ms)
            {
                var ps = m.GetParameters();
                if (ps.Any(p => p.ParameterType.IsByRef || p.IsOut)) { SyncKit.Info("events: " + w.Type + "." + w.Method + " has out/ref arguments, skipped"); continue; }
                if (w.Host && m.ReturnType != typeof(void)) { SyncKit.Info("events: " + w.Type + "." + w.Method + " returns a value, cannot be host-only, skipped"); continue; }
                bool sum = w.Sum && ps.Length == 1 && ps[0].ParameterType == typeof(float);
                _specs[m] = new Spec { T = t, Method = m.Name, Sig = string.Join(",", ps.Select(p => p.ParameterType.Name).ToArray()), Host = w.Host, Sum = sum };
                _h.Patch(m, prefix: new HarmonyMethod(typeof(EventSync), "Pre"), postfix: new HarmonyMethod(typeof(EventSync), "Post"));
                SyncKit.Info("events: forwarding " + w.Type + "." + w.Method + "(" + _specs[m].Sig + ") " + (w.Host ? "host" : "any") + (sum ? " sum" : ""));
            }
        }

        static bool Pre(object __instance, MethodBase __originalMethod)
        {
            try
            {
                Spec s;
                if (SyncKit.Applying || !_specs.TryGetValue(__originalMethod, out s)) return true;
                if (s.Host && SyncKit.Active && !SyncKit.IsHost) { DebugLog.Log("event", "skipped on a joiner (host decides): " + s.T.Name + "." + s.Method, 5); return false; }
            }
            catch { }
            return true;
        }

        static void Post(object __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                Spec s;
                if (!SyncKit.ShouldSend || !_specs.TryGetValue(__originalMethod, out s)) return;
                if (s.Host && !SyncKit.IsHost) return;
                var c = __instance as Component; if (c == null) return;
                string id, path;
                if (!SyncKit.IdAndPath(c, out id, out path)) return;
                if (s.Sum)
                {
                    string k = id + "|" + path + "|" + s.Method;
                    Pend p;
                    if (!_pend.TryGetValue(k, out p)) { p = new Pend { S = s, C = c, Id = id, Path = path, Due = Time.unscaledTime + 0.15f }; _pend[k] = p; }
                    p.Total += Convert.ToSingle(__args[0]);
                    return;
                }
                Emit(s, c, id, path, __args);
            }
            catch (Exception e) { Once("post " + __originalMethod.Name, "events: " + e.Message); }
        }

        static void Once(string key, string msg) { if (_warned.Add(key)) SyncKit.Warn(msg); }

        // ------------------------------------------------------------------ send

        static void Emit(Spec s, Component c, string id, string path, object[] args)
        {
            var w = new ByteWriter(64);
            w.Str(s.T.FullName); w.Str(s.Method); w.Str(s.Sig); w.Str(id); w.Str(path ?? "");
            w.VarU((uint)args.Length);
            foreach (var a in args)
                if (!WriteArg(w, a, c)) { Once("arg " + s.T.Name + "." + s.Method, "events: " + s.T.Name + "." + s.Method + " has an argument that cannot be sent (" + (a != null ? a.GetType().Name : "null") + "), not forwarded"); return; }
            var payload = w.ToArray();
            SyncKit.Send("evt.call", x => x.Bytes(payload));
            _sent++;
            DebugLog.Log("event", "sent " + s.T.Name + "." + s.Method + "(" + s.Sig + ") on " + id, 10);
        }

        static bool WriteArg(ByteWriter w, object v, Component inst)
        {
            if (v == null) { w.U8(0); return true; }
            var t = v.GetType();
            if (v is bool) { w.U8(1); w.Bool((bool)v); return true; }
            if (t.IsEnum) { w.U8(5); w.Str(t.AssemblyQualifiedName); w.VarI(Convert.ToInt64(v)); return true; }
            if (v is int || v is short || v is byte || v is sbyte || v is ushort || v is uint || v is long) { w.U8(2); w.VarI(Convert.ToInt64(v)); return true; }
            if (v is float) { w.U8(3); w.F32((float)v); return true; }
            if (v is double) { w.U8(4); w.F64((double)v); return true; }
            if (v is string) { w.U8(6); w.Str((string)v); return true; }
            if (v is Vector3) { w.U8(7); SyncKit.Vec(w, (Vector3)v); return true; }
            if (v is Quaternion) { w.U8(8); SyncKit.Rot(w, (Quaternion)v); return true; }
            if (v is Color) { var c = (Color)v; w.U8(9); w.F32(c.r); w.F32(c.g); w.F32(c.b); w.F32(c.a); return true; }
            var comp = v as Component;
            var go = v as GameObject;
            if (comp != null || go != null)
            {
                string id, path;
                if (!SyncKit.IdAndPath(comp != null ? comp : go.transform, out id, out path)) return false;
                w.U8(10); w.Str(id); w.Str(path ?? ""); return true;
            }
            return WriteMember(w, v, inst);
        }

        /// <summary>An object that is kept in a field of the component: sent as which field and which place in it.</summary>
        static bool WriteMember(ByteWriter w, object v, Component inst)
        {
            for (var cur = inst.GetType(); cur != null && cur != typeof(MonoBehaviour) && cur != typeof(object); cur = cur.BaseType)
                foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object val;
                    try { val = f.GetValue(inst); } catch { continue; }
                    if (val == null) continue;
                    if (ReferenceEquals(val, v)) { w.U8(11); w.Str(f.Name); w.U8(2); return true; }
                    var dict = val as IDictionary;
                    if (dict != null)
                    {
                        foreach (DictionaryEntry kv in dict)
                            if (ReferenceEquals(kv.Value, v))
                            {
                                var tmp = new ByteWriter(16);
                                if (!WriteArg(tmp, kv.Key, inst)) continue;
                                var tb = tmp.ToArray(); w.U8(11); w.Str(f.Name); w.U8(1); w.Raw(tb, 0, tb.Length); return true;
                            }
                        continue;
                    }
                    var list = val as IList;
                    if (list != null && !(val is string))
                    {
                        for (int i = 0; i < list.Count; i++)
                            if (ReferenceEquals(list[i], v)) { w.U8(11); w.Str(f.Name); w.U8(0); w.VarU((uint)i); return true; }
                    }
                }
            return false;
        }

        // ------------------------------------------------------------------ receive

        public void Register()
        {
            _pend.Clear(); _sent = _got = 0;
            SyncKit.On("evt.call", (from, r) => { var b = r.Bytes(); if (b != null) OnCall(b); });
        }

        static MethodInfo Resolve(string typeName, string method, string sig, out Type type)
        {
            type = AccessTools.TypeByName(typeName);
            if (type == null) return null;
            string key = typeName + "|" + method + "|" + sig;
            MethodInfo m;
            if (_resolved.TryGetValue(key, out m)) return m;
            m = AccessTools.GetDeclaredMethods(type).FirstOrDefault(x => x.Name == method && !x.IsStatic
                && string.Join(",", x.GetParameters().Select(p => p.ParameterType.Name).ToArray()) == sig);
            _resolved[key] = m;
            return m;
        }

        void OnCall(byte[] data)
        {
            var r = new ByteReader(data);
            string typeName = r.Str(), method = r.Str(), sig = r.Str(), id = r.Str(), path = r.Str();
            int n = (int)r.VarU();
            Type t;
            var m = Resolve(typeName, method, sig, out t);
            if (m == null) { Once("nomethod " + typeName + method, "events: cannot replay " + typeName + "." + method + " here (not installed, or a different version)"); return; }
            var tr = SyncKit.FindChild(id, path);
            var comp = tr != null ? tr.GetComponent(t) : null;
            if (comp == null) { DebugLog.Log("event", "no target for " + t.Name + "." + method + " (" + id + ")", 5); return; }
            var ps = m.GetParameters();
            if (ps.Length != n) return;
            var args = new object[n];
            for (int i = 0; i < n; i++)
            {
                bool ok;
                args[i] = ReadArg(r, ps[i].ParameterType, comp, out ok);
                if (!ok) { DebugLog.Log("event", "dropped " + t.Name + "." + method + ": argument " + i + " not found here", 5); return; }
            }
            _got++;
            DebugLog.Log("event", "replaying " + t.Name + "." + method + " on " + id, 10);
            SyncKit.Apply(() =>
            {
                try { m.Invoke(comp, args); }
                catch (Exception e) { Once("invoke " + typeName + method, "events: replaying " + typeName + "." + method + " failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message)); }
            });
        }

        static object ReadArg(ByteReader r, Type pt, Component inst, out bool ok)
        {
            ok = true;
            byte tag = r.U8();
            try
            {
                switch (tag)
                {
                    case 0: return null;
                    case 1: return r.Bool();
                    case 2: { long v = r.VarI(); return pt.IsEnum ? Enum.ToObject(pt, v) : Convert.ChangeType(v, pt); }
                    case 3: { float v = r.F32(); return pt == typeof(object) ? (object)v : Convert.ChangeType(v, pt); }
                    case 4: { double v = r.F64(); return pt == typeof(object) ? (object)v : Convert.ChangeType(v, pt); }
                    case 5: { var et = Type.GetType(r.Str()); long v = r.VarI(); return et != null ? Enum.ToObject(et, v) : null; }
                    case 6: return r.Str();
                    case 7: return SyncKit.Vec(r);
                    case 8: return SyncKit.Rot(r);
                    case 9: return new Color(r.F32(), r.F32(), r.F32(), r.F32());
                    case 10:
                        {
                            string id = r.Str(), path = r.Str();
                            var tr = SyncKit.FindChild(id, path);
                            if (tr == null) { ok = false; return null; }
                            object res = pt == typeof(GameObject) ? (object)tr.gameObject : tr.GetComponent(pt);
                            if (res == null) ok = false;
                            return res;
                        }
                    case 11:
                        {
                            string field = r.Str(); byte how = r.U8();
                            var f = AccessTools.Field(inst.GetType(), field);
                            object val = f != null ? f.GetValue(inst) : null;
                            if (val == null) { ok = false; return null; }
                            if (how == 2) return val;
                            if (how == 0)
                            {
                                int i = (int)r.VarU(); var list = val as IList;
                                if (list == null || i >= list.Count) { ok = false; return null; }
                                return list[i];
                            }
                            bool kok;
                            var key = ReadArg(r, typeof(object), inst, out kok);
                            var dict = val as IDictionary;
                            if (!kok || dict == null || !dict.Contains(key)) { ok = false; return null; }
                            return dict[key];
                        }
                }
            }
            catch { }
            ok = false;
            return null;
        }

        // ------------------------------------------------------------------ tick

        public void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextInstall) { _nextInstall = now + 10f; if (_wants.Any(w => !w.Done)) Install(); }
            if (_pend.Count > 0)
            {
                List<string> due = null;
                foreach (var kv in _pend) if (now >= kv.Value.Due) (due ?? (due = new List<string>())).Add(kv.Key);
                if (due != null)
                    foreach (var k in due)
                    {
                        var p = _pend[k]; _pend.Remove(k);
                        if (p.C != null && SyncKit.ShouldSend) Emit(p.S, p.C, p.Id, p.Path, new object[] { p.Total });
                    }
            }
            if (now >= _nextStats && (_sent + _got) > 0) { _nextStats = now + 60f; SyncKit.Info("events: sent " + _sent + ", replayed " + _got); }
        }
    }
}
