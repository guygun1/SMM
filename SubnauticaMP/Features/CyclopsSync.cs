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
    /// Cyclops interior state, kept in step by RECONCILIATION on top of the event messages of ToggleSync.
    ///
    /// Events lose information: a player who joins late, or whose Cyclops was not loaded when the event arrived, never
    /// sees the change. So the machine that has the authority for a Cyclops sends its full state about once a second
    /// ("cyc.state"), and at once when a player joins. The authority is the pilot's machine while somebody pilots that
    /// Cyclops, otherwise the host. Receivers compare each field with their own copy and replay only what differs.
    ///
    /// Anti-echo and anti-ping-pong:
    ///  - every replay runs inside SyncKit.Apply, so nothing is sent back;
    ///  - a field touched by a user action (local or remote) is quiet for Grace seconds, so a reconciliation message
    ///    that was sent before the action cannot undo it;
    ///  - an engine or motor change is not replayed again for the same field within ReplayCooldown seconds, so a change
    ///    that the game finishes late (a start-up sequence) does not get started twice.
    ///
    /// Engine, motor mode, Cyclops name and colour are not in the game's public surface that this repo already uses,
    /// so they are found by name through reflection (CycRefl) and each name that is missing is logged once per session.
    /// </summary>
    sealed class CyclopsSync : ISyncFeature
    {
        const float SendInterval = 1f;       // full state of each Cyclops, at most once a second
        const float NearRange = 200f;        // metres: a Cyclops is synced only while a player is this close
        const float Grace = 2f;              // seconds a user action keeps its field from being overwritten
        const float ReplayCooldown = 3f;     // seconds before the same engine or motor field is replayed again
        const float DriverHold = 3f;         // seconds a remote pilot's state is trusted over the host's copy
        const float RefreshInterval = 5f;    // seconds between scene scans for Cyclops
        const int DictMax = 2048;

        // field bits: which fields a message carries (Known), and their values (Bits)
        const int KLights = 1, KFloods = 2, KSilent = 4, KShield = 8, KEngine = 16, KMotor = 32, KName = 64, KColor = 128;
        const int BLights = 1, BFloods = 2, BSilent = 4, BShield = 8, BEngine = 16, BDriving = 32;

        static float _nextSend, _nextRefresh;
        static int _lastPlayers;
        static bool _force, _probed;
        static readonly List<SubRoot> _subs = new List<SubRoot>();
        static readonly Dictionary<string, float> _touched = new Dictionary<string, float>();
        static readonly Dictionary<string, float> _replayed = new Dictionary<string, float>();
        static readonly Dictionary<string, float> _driver = new Dictionary<string, float>();

        internal sealed class State { public int Known, Bits, Motor = -1, Color = -1; public string Name = ""; }

        public string Name { get { return "cyclops"; } }

        public void Register()
        {
            _touched.Clear(); _replayed.Clear(); _driver.Clear(); _subs.Clear();
            _nextSend = 0f; _nextRefresh = 0f; _lastPlayers = 0; _force = false; _probed = false;
            CycRefl.ResetSession();

            SyncKit.On("cyc.state", OnState);

            SyncKit.On("cyc.engine", (from, r) =>
            {
                string id = r.Str(); bool on = r.Bool();
                var sub = SyncKit.Find<SubRoot>(id);
                if (sub == null || LocalPilots(sub)) return;     // not loaded here, or we are the pilot: we decide
                Touch(id, "engine");
                ReplayEngine(sub, id, on, false);
            });

            SyncKit.On("cyc.motor", (from, r) =>
            {
                string id = r.Str(); int mode = (int)r.VarI();
                var sub = SyncKit.Find<SubRoot>(id);
                if (sub == null || LocalPilots(sub)) return;
                Touch(id, "motor");
                ReplayMotor(sub, id, mode, false);
            });

            SyncKit.On("cyc.name", (from, r) =>
            {
                string id = r.Str(); string name = r.Str() ?? "";
                var sub = SyncKit.Find<SubRoot>(id);
                if (sub == null) return;
                Touch(id, "name");
                ReplayName(sub, name);
            });

            SyncKit.On("cyc.hsb", (from, r) =>
            {
                string id = r.Str(); int index = (int)r.VarI(); Vector3 hsb = SyncKit.Vec(r);
                var sub = SyncKit.Find<SubRoot>(id);
                var set = CycRefl.HsbSetter();
                var comp = sub != null ? CycRefl.Comp(sub, CycRefl.NameType) : null;
                if (comp == null || set == null) return;
                Touch(id, "hsb");
                SyncKit.Apply(() => { try { set.Invoke(comp, new object[] { index, hsb, Color.HSVToRGB(hsb.x, hsb.y, hsb.z) }); } catch (Exception e) { CycRefl.Once("hsb-set", "colour change failed: " + e.Message); } });
            });

            SyncKit.On("cyc.color", (from, r) =>
            {
                string id = r.Str(); int color = (int)r.VarI();
                var sub = SyncKit.Find<SubRoot>(id);
                if (sub == null) return;
                Touch(id, "color");
                ReplayColor(sub, color);
            });
        }

        // ------------------------------------------------------------------ tick (the authority sends)

        public void Tick()
        {
            if (!_probed && SyncKit.InSession) { _probed = true; CycRefl.Probe(); }
            try { PinOnJoiner(); } catch (Exception e) { SyncKit.Warn("cyclops: pin: " + e.Message); }
            if (!SyncKit.ShouldSend) return;

            float now = Time.unscaledTime;
            int players = CountRemote();
            bool joined = players > _lastPlayers;     // a new player must learn every Cyclops at once
            _lastPlayers = players;
            if (!joined && now < _nextSend) return;
            _nextSend = now + SendInterval;

            if (now >= _nextRefresh) { _nextRefresh = now + RefreshInterval; RefreshSubs(); }
            var origins = Origins();
            for (int i = 0; i < _subs.Count; i++)
            {
                var sub = _subs[i];
                if (sub == null || !IsCyclops(sub) || !NearAny(sub.transform.position, origins)) continue;
                string id = SyncKit.IdOf(sub);
                if (id == null) continue;
                bool driving = LocalPilots(sub);
                // a pilot elsewhere is the authority for this Cyclops; the host only steps in when nobody is piloting
                if (!driving && (!SyncKit.IsHost || RemoteDriving(id, now))) continue;
                try { Send(sub, id, driving); }
                catch (Exception e) { SyncKit.Warn("cyclops: send: " + e.Message); }
            }
        }

        static void Send(SubRoot sub, string id, bool driving)
        {
            var st = Read(sub, driving);
            SyncKit.Send("cyc.state", w =>
            {
                w.Str(id);
                w.U8((byte)st.Known);
                w.U8((byte)st.Bits);
                w.VarI(st.Motor);
                w.VarI(st.Color);
                w.Str(st.Name ?? "");
            });
        }

        /// <summary>The full state of one Cyclops as this machine sees it. A field that cannot be read is left out
        /// (its Known bit stays 0), so receivers never replay a guess.</summary>
        static State Read(SubRoot sub, bool driving)
        {
            var st = new State();
            bool v;
            int n;
            string s;

            var panel = sub.GetComponentInChildren<CyclopsLightingPanel>(true);
            if (panel != null)
            {
                st.Known |= KLights | KFloods;
                if (panel.lightingOn) st.Bits |= BLights;
                if (panel.floodlightsOn) st.Bits |= BFloods;
            }

            var silent = sub.GetComponentInChildren<CyclopsSilentRunningAbilityButton>(true);
            if (silent != null && ActiveOf(silent, out v)) { st.Known |= KSilent; if (v) st.Bits |= BSilent; }

            var shield = sub.GetComponentInChildren<CyclopsShieldButton>(true);
            if (shield != null && ActiveOf(shield, out v)) { st.Known |= KShield; if (v) st.Bits |= BShield; }

            if (CycRefl.EngineIn(CycRefl.Comp(sub, CycRefl.EngineType), out v)) { st.Known |= KEngine; if (v) st.Bits |= BEngine; }
            if (CycRefl.MotorIn(CycRefl.Comp(sub, CycRefl.MotorType), out n)) { st.Known |= KMotor; st.Motor = n; }
            if (CycRefl.NameIn(CycRefl.Comp(sub, CycRefl.NameType), out s)) { st.Known |= KName; st.Name = s; }
            if (CycRefl.ColorIn(CycRefl.Comp(sub, CycRefl.NameType), out n)) { st.Known |= KColor; st.Color = n; }

            if (driving) st.Bits |= BDriving;
            return st;
        }

        // ------------------------------------------------------------------ receive the full state

        static void OnState(int from, ByteReader r)
        {
            string id = r.Str();
            int known = r.U8();
            int bits = r.U8();
            int motor = (int)r.VarI();
            int color = (int)r.VarI();
            string name = r.Str() ?? "";
            if (string.IsNullOrEmpty(id)) return;

            float now = Time.unscaledTime;
            if ((bits & BDriving) != 0) _driver[id] = now;

            var sub = SyncKit.Find<SubRoot>(id);
            if (sub == null || !IsCyclops(sub)) return;     // not loaded here: fine, the next message repeats it
            if (LocalPilots(sub)) return;                    // we pilot it: our state is the one that counts

            if ((known & (KLights | KFloods)) != 0 && !Recent(id, "lights"))
            {
                var panel = sub.GetComponentInChildren<CyclopsLightingPanel>(true);
                if (panel != null) ReplayLights(panel, (bits & BLights) != 0, (bits & BFloods) != 0);
            }
            if ((known & KSilent) != 0 && !Recent(id, "silent"))
                ReplaySilent(sub.GetComponentInChildren<CyclopsSilentRunningAbilityButton>(true), (bits & BSilent) != 0);
            if ((known & KShield) != 0 && !Recent(id, "shield"))
                ReplayShield(sub.GetComponentInChildren<CyclopsShieldButton>(true), (bits & BShield) != 0);
            if ((known & KEngine) != 0 && !Recent(id, "engine")) ReplayEngine(sub, id, (bits & BEngine) != 0, true);
            if ((known & KMotor) != 0 && !Recent(id, "motor")) ReplayMotor(sub, id, motor, true);
            if ((known & KName) != 0 && !Recent(id, "name")) ReplayName(sub, name);
            if ((known & KColor) != 0 && !Recent(id, "color")) ReplayColor(sub, color);
        }

        // ------------------------------------------------------------------ replays (compare first, then apply)

        internal static void ReplayLights(CyclopsLightingPanel panel, bool internalOn, bool floodOn)
        {
            if (panel == null) return;
            SyncKit.Apply(() =>
            {
                if (panel.lightingOn != internalOn) panel.ToggleInternalLighting();
                if (panel.floodlightsOn != floodOn) panel.ToggleFloodlights();
            });
        }

        internal static void ReplaySilent(CyclopsSilentRunningAbilityButton b, bool on)
        {
            bool cur;
            if (b == null || !ActiveOf(b, out cur) || cur == on) return;
            SyncKit.Apply(() =>
            {
                Traverse.Create(b).Method(on ? "TurnOnSilentRunning" : "TurnOffSilentRunning").GetValue();
            });
        }

        internal static void ReplayShield(CyclopsShieldButton b, bool on)
        {
            bool cur;
            if (b == null || !ActiveOf(b, out cur) || cur == on) return;
            SyncKit.Apply(() =>
            {
                Traverse.Create(b).Method(on ? "StartShield" : "StopShield").GetValue();
            });
        }

        static void ReplayEngine(SubRoot sub, string id, bool on, bool reconciling)
        {
            var comp = CycRefl.Comp(sub, CycRefl.EngineType);
            bool cur;
            if (!CycRefl.EngineIn(comp, out cur)) { CycRefl.Once("engine-read", "engine state not readable on this build; engine sync idle"); return; }
            if (cur == on) return;
            if (reconciling && Cooling(id, "engine")) return;
            MarkReplay(id, "engine");
            SyncKit.Apply(() =>
            {
                if (!CycRefl.SetEngine(comp, cur, on)) CycRefl.Once("engine-set", "engine change method not found on this build");
            });
        }

        static void ReplayMotor(SubRoot sub, string id, int mode, bool reconciling)
        {
            var comp = CycRefl.Comp(sub, CycRefl.MotorType);
            int cur;
            if (!CycRefl.MotorIn(comp, out cur)) { CycRefl.Once("motor-read", "motor mode not readable on this build; motor sync idle"); return; }
            if (cur == mode) return;
            if (reconciling && Cooling(id, "motor")) return;
            MarkReplay(id, "motor");
            SyncKit.Apply(() =>
            {
                if (!CycRefl.SetMotor(comp, cur, mode)) CycRefl.Once("motor-set", "motor mode change method not found on this build");
            });
        }

        static void ReplayName(SubRoot sub, string name)
        {
            var comp = CycRefl.Comp(sub, CycRefl.NameType);
            string cur;
            if (comp == null || !CycRefl.NameIn(comp, out cur)) { CycRefl.Once("name-read", "Cyclops name not readable on this build; name sync idle"); return; }
            if (cur == name) return;
            SyncKit.Apply(() =>
            {
                if (!CycRefl.SetName(comp, name)) CycRefl.Once("name-set", "Cyclops name setter not found on this build");
            });
        }

        static void ReplayColor(SubRoot sub, int color)
        {
            var comp = CycRefl.Comp(sub, CycRefl.NameType);
            int cur;
            if (comp == null || !CycRefl.ColorIn(comp, out cur)) { CycRefl.Once("color-read", "Cyclops colour not readable on this build; colour sync idle"); return; }
            if (cur == color) return;
            SyncKit.Apply(() =>
            {
                if (!CycRefl.SetColor(comp, color)) CycRefl.Once("color-set", "Cyclops colour setter not found on this build");
            });
        }

        static bool ActiveOf(object button, out bool active)
        {
            active = false;
            try { active = Traverse.Create(button).Field("active").GetValue<bool>(); return true; }
            catch (Exception) { CycRefl.Once("active", "button 'active' field not readable"); return false; }
        }

        // ------------------------------------------------------------------ authority and grace bookkeeping

        /// <summary>True when the local player is piloting this Cyclops (Player.isPiloting and currentSub, as used in EntitySync).</summary>
        internal static bool LocalPilots(SubRoot sub)
        {
            var p = Player.main;
            return p != null && sub != null && p.isPiloting && p.currentSub == sub;
        }

        static bool RemoteDriving(string id, float now)
        {
            float t;
            return _driver.TryGetValue(id, out t) && now - t < DriverHold;
        }

        /// <summary>A user action (local or from another player) happened on this field. For Grace seconds the
        /// reconciliation does not overwrite it.</summary>
        internal static void Touch(string id, string field)
        {
            if (string.IsNullOrEmpty(id)) return;
            Trim(_touched);
            _touched[id + "/" + field] = Time.unscaledTime;
        }

        internal static void Touch(Component c, string field)
        {
            string id;
            if (IdOfComp(c, out id)) Touch(id, field);
        }

        static bool Recent(string id, string field)
        {
            float t;
            return _touched.TryGetValue(id + "/" + field, out t) && Time.unscaledTime - t < Grace;
        }

        static bool Cooling(string id, string field)
        {
            float t;
            return _replayed.TryGetValue(id + "/" + field, out t) && Time.unscaledTime - t < ReplayCooldown;
        }

        static void MarkReplay(string id, string field)
        {
            Trim(_replayed);
            _replayed[id + "/" + field] = Time.unscaledTime;
        }

        static void Trim(Dictionary<string, float> d)
        {
            if (d.Count > DictMax) d.Clear();
        }

        static bool IdOfComp(Component c, out string id)
        {
            id = null;
            if (c == null) return false;
            var sub = c.GetComponentInParent<SubRoot>();
            id = SyncKit.IdOf(sub);
            return id != null;
        }

        static bool IsCyclops(SubRoot sub)
        {
            return sub.isCyclops || !sub.isBase;     // same test as EntitySync: a Cyclops or any modded sub that is not a base
        }

        // ------------------------------------------------------------------ a joiner must not be able to push a Cyclops

        static readonly Dictionary<int, bool> _pinned = new Dictionary<int, bool>();   // Cyclops instance id -> was kinematic before we pinned it
        static float _nextPin, _nextPinScan;
        static SubRoot _lastSub;

        /// <summary>
        /// On a joining machine every Cyclops it does not pilot itself is held kinematic. Its pose comes from the network
        /// (the follower moves it), so a local physics push would only fight the snapshots and look like the sub being shoved.
        /// The follower already does this for a sub it receives; this covers a sub that has not sent anything yet, or whose
        /// follower was released after a quiet spell. A sub the local player pilots gets its old state back.
        /// </summary>
        static void PinOnJoiner()
        {
            if (!SyncKit.InSession || SyncKit.IsHost) { if (_pinned.Count > 0) UnpinAll(); return; }
            var cs = Player.main != null ? Player.main.currentSub : null;
            if (cs != _lastSub)
            {
                // evidence for the flashing Cyclops buttons: they hang on the player being "inside" the sub
                SyncKit.Info("cyclops: player's current sub " + (_lastSub != null ? _lastSub.name : "none") + " -> " + (cs != null ? cs.name : "none") + " at " + Time.unscaledTime.ToString("F2"));
                _lastSub = cs;
            }
            float now = Time.unscaledTime;
            if (now < _nextPin) return;
            _nextPin = now + 0.2f;
            if (now >= _nextPinScan) { _nextPinScan = now + RefreshInterval; RefreshSubs(); }
            for (int i = 0; i < _subs.Count; i++)
            {
                var sub = _subs[i];
                if (sub == null) continue;
                var rb = sub.GetComponent<Rigidbody>();
                if (rb == null) continue;
                int key = sub.GetInstanceID();
                if (LocalPilots(sub))
                {
                    bool was;
                    if (_pinned.TryGetValue(key, out was)) { rb.isKinematic = was; _pinned.Remove(key); SyncKit.Info("cyclops: released the pin, local pilot"); }
                    continue;
                }
                if (!_pinned.ContainsKey(key)) { _pinned[key] = rb.isKinematic; SyncKit.Info("cyclops: pinned (kinematic) so it cannot be pushed here"); }
                if (!rb.isKinematic) rb.isKinematic = true;
            }
        }

        static void UnpinAll()
        {
            foreach (var s in UnityEngine.Object.FindObjectsOfType<SubRoot>())
            {
                bool was;
                if (s == null || !_pinned.TryGetValue(s.GetInstanceID(), out was)) continue;
                var rb = s.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = was;
            }
            _pinned.Clear();
        }

        static void RefreshSubs()
        {
            _subs.Clear();
            foreach (var s in UnityEngine.Object.FindObjectsOfType<SubRoot>())
                if (s != null && IsCyclops(s)) _subs.Add(s);
        }

        static int CountRemote()
        {
            int n = 0;
            if (Plugin.I != null) foreach (var pos in Plugin.I.RemotePlayerPositions()) n++;
            return n;
        }

        static List<Vector3> Origins()
        {
            var l = new List<Vector3>();
            if (Player.main != null) l.Add(Player.main.transform.position);
            if (Plugin.I != null) l.AddRange(Plugin.I.RemotePlayerPositions());
            return l;
        }

        static bool NearAny(Vector3 pos, List<Vector3> origins)
        {
            float r2 = NearRange * NearRange;
            for (int i = 0; i < origins.Count; i++)
                if ((pos - origins[i]).sqrMagnitude <= r2) return true;
            return false;
        }

        // ------------------------------------------------------------------ helpers called by the patches and ToggleSync

        internal static int EngineNow(Component c) { bool on; return CycRefl.EngineIn(c, out on) ? (on ? 1 : 0) : -1; }
        internal static int MotorNow(Component c) { int m; return CycRefl.MotorIn(c, out m) ? m : -1; }
        internal static int ColorNow(Component c) { int m; return CycRefl.ColorIn(c, out m) ? m : -1; }
        internal static string NameNow(Component c) { string s; return CycRefl.NameIn(c, out s) ? s : null; }

        internal static void EngineRequested(Component comp, int before, bool want)
        {
            if (!SyncKit.ShouldSend || (want ? 1 : 0) == before) return;
            string id;
            if (!IdOfComp(comp, out id)) return;
            Touch(id, "engine");
            SyncKit.Send("cyc.engine", w => { w.Str(id); w.Bool(want); });
        }

        internal static void HsbChanged(Component comp, int index, Vector3 hsb)
        {
            if (!SyncKit.ShouldSend) return;
            string id;
            if (!IdOfComp(comp, out id)) return;
            Touch(id, "hsb");
            SyncKit.Send("cyc.hsb", w => { w.Str(id); w.VarI(index); SyncKit.Vec(w, hsb); });
        }

        internal static void EngineChanged(Component comp, int before)
        {
            bool on;
            if (!SyncKit.ShouldSend || !CycRefl.EngineIn(comp, out on)) return;
            if ((on ? 1 : 0) == before) return;
            string id;
            if (!IdOfComp(comp, out id)) return;
            Touch(id, "engine");
            SyncKit.Send("cyc.engine", w => { w.Str(id); w.Bool(on); });
        }

        internal static void MotorChanged(Component comp, int before)
        {
            int mode;
            if (!SyncKit.ShouldSend || !CycRefl.MotorIn(comp, out mode) || mode == before) return;
            string id;
            if (!IdOfComp(comp, out id)) return;
            Touch(id, "motor");
            SyncKit.Send("cyc.motor", w => { w.Str(id); w.VarI(mode); });
        }

        internal static void NameChanged(Component comp, string before)
        {
            string name;
            if (!SyncKit.ShouldSend || !CycRefl.NameIn(comp, out name) || name == before) return;
            string id;
            if (!IdOfComp(comp, out id)) return;
            Touch(id, "name");
            SyncKit.Send("cyc.name", w => { w.Str(id); w.Str(name); });
        }

        internal static void ColorChanged(Component comp, int before)
        {
            int color;
            if (!SyncKit.ShouldSend || !CycRefl.ColorIn(comp, out color) || color == before) return;
            string id;
            if (!IdOfComp(comp, out id)) return;
            Touch(id, "color");
            SyncKit.Send("cyc.color", w => { w.Str(id); w.VarI(color); });
        }
    }

    // ---------------------------------------------------------------- patches on the game's change methods
    // Each target is found by name. Prepare returns false when the name does not exist on this build, so that
    // patch is skipped and the others still apply. Prefix/Postfix never throw.

    [HarmonyPatch]
    static class P_CycEngine
    {
        static bool Prepare() { return CycRefl.EngineTarget() != null; }
        static MethodBase TargetMethod() { return CycRefl.EngineTarget(); }

        static void Prefix(object __instance, out int __state)
        {
            __state = -1;
            try { __state = CyclopsSync.EngineNow(__instance as Component); } catch (Exception) { }
        }

        static void Postfix(object __instance, object[] __args, int __state)
        {
            try
            {
                // the engine changes over a few seconds (a coroutine): go by what was asked, not by the state right after the call
                if (__args != null && __args.Length > 0 && __args[0] is bool) CyclopsSync.EngineRequested(__instance as Component, __state, (bool)__args[0]);
                else CyclopsSync.EngineChanged(__instance as Component, __state);
            }
            catch (Exception e) { SyncKit.Warn("cyclops: engine patch: " + e.Message); }
        }
    }

    [HarmonyPatch]
    static class P_CycMotor
    {
        static bool Prepare() { return CycRefl.MotorTarget() != null; }
        static MethodBase TargetMethod() { return CycRefl.MotorTarget(); }

        static void Prefix(object __instance, out int __state)
        {
            __state = -1;
            try { __state = CyclopsSync.MotorNow(__instance as Component); } catch (Exception) { }
        }

        static void Postfix(object __instance, int __state)
        {
            try { CyclopsSync.MotorChanged(__instance as Component, __state); }
            catch (Exception e) { SyncKit.Warn("cyclops: motor patch: " + e.Message); }
        }
    }

    [HarmonyPatch]
    static class P_CycName
    {
        static bool Prepare() { return CycRefl.NameTarget() != null; }
        static MethodBase TargetMethod() { return CycRefl.NameTarget(); }

        static void Prefix(object __instance, out string __state)
        {
            __state = null;
            try { __state = CyclopsSync.NameNow(__instance as Component); } catch (Exception) { }
        }

        static void Postfix(object __instance, string __state)
        {
            try { CyclopsSync.NameChanged(__instance as Component, __state); }
            catch (Exception e) { SyncKit.Warn("cyclops: name patch: " + e.Message); }
        }
    }

    /// <summary>The Cyclops colour picker: SubName.SetColor(index, hsb, colour).</summary>
    [HarmonyPatch]
    static class P_CycHsb
    {
        static bool Prepare() { return CycRefl.HsbSetter() != null; }
        static MethodBase TargetMethod() { return CycRefl.HsbSetter(); }

        static void Postfix(object __instance, int __0, Vector3 __1)
        {
            try { CyclopsSync.HsbChanged(__instance as Component, __0, __1); }
            catch (Exception e) { SyncKit.Warn("cyclops: colour patch: " + e.Message); }
        }
    }

    [HarmonyPatch]
    static class P_CycColor
    {
        static bool Prepare() { return CycRefl.ColorTarget() != null; }
        static MethodBase TargetMethod() { return CycRefl.ColorTarget(); }

        static void Prefix(object __instance, out int __state)
        {
            __state = -1;
            try { __state = CyclopsSync.ColorNow(__instance as Component); } catch (Exception) { }
        }

        static void Postfix(object __instance, int __state)
        {
            try { CyclopsSync.ColorChanged(__instance as Component, __state); }
            catch (Exception e) { SyncKit.Warn("cyclops: colour patch: " + e.Message); }
        }
    }

    /// <summary>Reflection by name for the Cyclops parts that this repo does not reference at compile time.
    /// Every lookup is cached; a missing name gives null and is logged once per session.</summary>
    internal static class CycRefl
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Candidate names, tried in order. The probe log says which exist on this game build.
        const string EngineTypeName = "CyclopsMotorMode";   // from the probe: engineOn, InvokeChangeEngineState and ChangeCyclopsMotorMode all live on this class
        const string MotorTypeName = "CyclopsMotorMode";
        const string NameTypeName = "SubName";

        static readonly string[] EngineStateNames = { "engineOn", "isEngineOn", "engineState", "engineActive", "isOn" };
        static readonly string[] EngineSetNames = { "InvokeChangeEngineState", "SetEngineState", "SetEngineOn", "SetEngine", "SetEngineEnabled" };
        static readonly string[] EngineToggleNames = { "ToggleEngine", "ToggleEngineState", "OnEngineToggle" };
        static readonly string[] MotorStateNames = { "cyclopsMotorMode", "motorMode", "currentMotorMode" };
        static readonly string[] MotorSetNames = { "ChangeCyclopsMotorMode", "SetMotorMode", "ChangeMotorMode", "SetCyclopsMotorMode" };
        static readonly string[] MotorCycleNames = { "CycleMotorMode", "NextMotorMode", "ToggleMotorMode" };
        static readonly string[] NameStateNames = { "GetName", "subName", "_subName" };
        static readonly string[] NameSetNames = { "SetName", "SetSubName" };
        static readonly string[] ColorStateNames = { "GetColorIndex", "colorIndex", "_colorIndex" };
        static readonly string[] ColorSetNames = { "SetColorIndex", "SetColor" };

        static readonly Dictionary<string, object> _cache = new Dictionary<string, object>();
        static readonly HashSet<string> _once = new HashSet<string>();

        internal static void ResetSession() { _once.Clear(); }

        internal static void Once(string key, string msg)
        {
            if (_once.Add(key)) SyncKit.Warn("cyclops: " + msg);
        }

        static T Cached<T>(string key, Func<T> make) where T : class
        {
            object v;
            if (_cache.TryGetValue(key, out v)) return v as T;
            T r = null;
            try { r = make(); } catch (Exception) { r = null; }
            _cache[key] = r;
            return r;
        }

        internal static Type TypeNamed(string name)
        {
            return Cached<Type>("type:" + name, () => AccessTools.TypeByName(name));
        }

        internal static Type EngineType { get { return TypeNamed(EngineTypeName); } }
        internal static Type MotorType { get { return TypeNamed(MotorTypeName); } }
        internal static Type NameType { get { return TypeNamed(NameTypeName); } }

        static MethodInfo FindMethod(Type t, string[] names, Func<MethodInfo, bool> ok)
        {
            if (t == null) return null;
            MethodInfo[] all;
            try { all = t.GetMethods(All); } catch (Exception) { return null; }
            foreach (var n in names)
                foreach (var m in all)
                    if (m.Name == n && ok(m)) return m;
            return null;
        }

        static bool OneParam(MethodInfo m, Type t) { var ps = m.GetParameters(); return ps.Length == 1 && ps[0].ParameterType == t; }
        static bool OneEnumParam(MethodInfo m) { var ps = m.GetParameters(); return ps.Length == 1 && ps[0].ParameterType.IsEnum; }
        static bool NoParams(MethodInfo m) { return m.GetParameters().Length == 0; }

        internal static MethodInfo EngineSetter()
        {
            return Cached<MethodInfo>("m:engineSet", () => FindMethod(EngineType, EngineSetNames, m => { var ps = m.GetParameters(); return ps.Length >= 1 && m.ReturnType == typeof(void) && ps.All(x => x.ParameterType == typeof(bool)); }));
        }
        internal static MethodInfo EngineToggle()
        {
            return Cached<MethodInfo>("m:engineToggle", () => FindMethod(EngineType, EngineToggleNames, m => NoParams(m) && m.ReturnType == typeof(void)));
        }
        internal static MethodBase EngineTarget() { return (MethodBase)EngineSetter() ?? EngineToggle(); }

        internal static MethodInfo MotorSetter()
        {
            return Cached<MethodInfo>("m:motorSet", () => FindMethod(MotorType, MotorSetNames, OneEnumParam));
        }
        internal static MethodInfo MotorCycle()
        {
            return Cached<MethodInfo>("m:motorCycle", () => FindMethod(MotorType, MotorCycleNames, NoParams));
        }
        internal static MethodBase MotorTarget() { return (MethodBase)MotorSetter() ?? MotorCycle(); }

        internal static MethodInfo NameSetter()
        {
            return Cached<MethodInfo>("m:nameSet", () => FindMethod(NameType, NameSetNames, m => OneParam(m, typeof(string))));
        }
        internal static MethodBase NameTarget() { return NameSetter(); }

        internal static MethodInfo ColorSetter()
        {
            return Cached<MethodInfo>("m:colorSet", () => FindMethod(NameType, ColorSetNames, m => OneParam(m, typeof(int))));
        }
        internal static MethodBase ColorTarget() { return ColorSetter(); }

        internal static MethodInfo HsbSetter()
        {
            return Cached<MethodInfo>("m:hsbSet", () =>
            {
                var t = NameType; if (t == null) return null;
                foreach (var m in t.GetMethods(All))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "SetColor" && ps.Length == 3 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(Vector3) && ps[2].ParameterType == typeof(Color)) return m;
                }
                return null;
            });
        }

        /// <summary>Component of type t inside the Cyclops (or null when the type or the component is not here).</summary>
        internal static Component Comp(SubRoot sub, Type t)
        {
            if (sub == null || t == null) return null;
            try { return sub.GetComponentInChildren(t, true); } catch (Exception) { return null; }
        }

        static readonly Dictionary<string, MemberInfo> _members = new Dictionary<string, MemberInfo>();

        // plain reflection (AccessTools logs a HarmonyX warning for every name that does not exist, hundreds of times)
        static MemberInfo Lookup(Type t, string n)
        {
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var f = cur.GetField(n, All | BindingFlags.DeclaredOnly); if (f != null) return f;
                var p = cur.GetProperty(n, All | BindingFlags.DeclaredOnly); if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p;
                var m = cur.GetMethod(n, All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null); if (m != null && m.ReturnType != typeof(void)) return m;
            }
            return null;
        }

        /// <summary>Reads a field, property or no-argument method by one of the candidate names.</summary>
        internal static bool ReadValue(object o, string[] names, out object value)
        {
            value = null;
            if (o == null) return false;
            var t = o.GetType();
            foreach (var n in names)
            {
                string key = t.FullName + "|" + n;
                MemberInfo mi;
                if (!_members.TryGetValue(key, out mi)) { try { mi = Lookup(t, n); } catch (Exception) { mi = null; } _members[key] = mi; }
                if (mi == null) continue;
                try
                {
                    var f = mi as FieldInfo; if (f != null) { value = f.GetValue(o); return true; }
                    var p = mi as PropertyInfo; if (p != null) { value = p.GetValue(o, null); return true; }
                    var m = mi as MethodInfo; if (m != null) { value = m.Invoke(o, null); return true; }
                }
                catch (Exception) { }
            }
            return false;
        }

        internal static bool EngineIn(Component c, out bool on)
        {
            on = false;
            object v;
            if (c == null || !ReadValue(c, EngineStateNames, out v) || !(v is bool)) return false;
            on = (bool)v;
            return true;
        }

        internal static bool MotorIn(Component c, out int mode)
        {
            mode = -1;
            object v;
            if (c == null || !ReadValue(c, MotorStateNames, out v) || !(v is Enum || v is int)) return false;
            mode = Convert.ToInt32(v);
            return true;
        }

        internal static bool NameIn(Component c, out string name)
        {
            name = null;
            object v;
            if (c == null || !ReadValue(c, NameStateNames, out v) || !(v is string)) return false;
            name = (string)v;
            return true;
        }

        internal static bool ColorIn(Component c, out int color)
        {
            color = -1;
            object v;
            if (c == null || !ReadValue(c, ColorStateNames, out v) || !(v is Enum || v is int)) return false;
            color = Convert.ToInt32(v);
            return true;
        }

        internal static bool SetEngine(Component comp, bool cur, bool on)
        {
            var set = EngineSetter();
            if (set != null)
            {
                var args = new object[set.GetParameters().Length];      // the first is the wanted state, any others (instant, ...) stay false so the animation plays
                for (int i = 0; i < args.Length; i++) args[i] = i == 0 ? on : false;
                set.Invoke(comp, args);
                return true;
            }
            var tog = EngineToggle();
            if (tog != null && cur != on) { tog.Invoke(comp, null); return true; }   // a toggle only changes when the state differs
            return false;
        }

        internal static bool SetMotor(Component comp, int cur, int mode)
        {
            var set = MotorSetter();
            if (set != null)
            {
                var pt = set.GetParameters()[0].ParameterType;
                set.Invoke(comp, new object[] { Enum.ToObject(pt, mode) });
                return true;
            }
            var cyc = MotorCycle();
            if (cyc == null) return false;
            int now = cur;
            for (int i = 0; i < 4 && now != mode; i++)
            {
                cyc.Invoke(comp, null);
                if (!MotorIn(comp, out now)) break;
            }
            return true;
        }

        internal static bool SetName(Component comp, string name)
        {
            var set = NameSetter();
            if (set == null) return false;
            set.Invoke(comp, new object[] { name });
            return true;
        }

        internal static bool SetColor(Component comp, int color)
        {
            var set = ColorSetter();
            if (set == null) return false;
            set.Invoke(comp, new object[] { color });
            return true;
        }

        // ------------------------------------------------------------------ probe (logged once per session)

        static readonly string[] ProbeTypes = { EngineTypeName, MotorTypeName, NameTypeName, "SubControl", "CyclopsHelmHUDManager", "CyclopsLightingPanel" };

        internal static void Probe()
        {
            foreach (var n in ProbeTypes)
            {
                var t = TypeNamed(n);
                if (t == null) SyncKit.Info("cyclops probe: type " + n + " NOT FOUND");
                else SyncKit.Info("cyclops probe: type " + n + " found; members: " + Keywords(t));
            }
            SyncKit.Info("cyclops probe: engine setter " + Describe(EngineSetter()) + ", engine toggle " + Describe(EngineToggle())
                + ", motor setter " + Describe(MotorSetter()) + ", motor cycle " + Describe(MotorCycle())
                + ", name setter " + Describe(NameSetter()) + ", colour setter " + Describe(ColorSetter()));
            SyncKit.Info("cyclops probe: pilot test uses Player.isPiloting and Player.currentSub (already used by EntitySync)");
        }

        static string Describe(MethodInfo m) { return m == null ? "NOT FOUND" : m.DeclaringType.Name + "." + m.Name; }

        static string Keywords(Type t)
        {
            var seen = new List<string>();
            try
            {
                foreach (var m in t.GetMembers(All))
                {
                    string n = m.Name;
                    string l = n.ToLowerInvariant();
                    bool hit = l.Contains("engine") || l.Contains("motor") || l.Contains("mode") || l.Contains("speed")
                        || l.Contains("power") || l.Contains("hud") || l.Contains("helm") || l.Contains("pilot")
                        || l.Contains("name") || l.Contains("colo") || l.Contains("state") || l.Contains("light");
                    if (!hit || seen.Contains(n)) continue;
                    seen.Add(n);
                    if (seen.Count >= 40) break;
                }
            }
            catch (Exception) { }
            return seen.Count == 0 ? "(no matching members)" : string.Join(", ", seen.ToArray());
        }
    }
}
