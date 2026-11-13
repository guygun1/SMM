using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// A joining player's own state is kept on the host, so leaving and coming back continues where they were:
    /// the place (in the world, or inside a Cyclops or base at the same spot of it), health, food and water.
    /// (The inventory has its own file, see InventoryPersist.)
    ///
    ///   ps.ask   name        joiner to host: send me my state (once, after the host's world is loaded)
    ///   ps.set   data        host to joiner: the stored state, "" if there is none
    ///   ps.save  name, data  joiner to host: the current state (every 3 s when it changed, and at once when quitting to the menu)
    ///
    /// Nothing is saved before the stored state has been applied, so the host's copy cannot be overwritten with the default.
    /// The host's own state is part of the game's own save and is not touched.
    /// </summary>
    sealed class PlayerStatePersist : ISyncFeature
    {
        public string Name { get { return "player state"; } }

        const int MaxLen = 2000;
        const float SaveEvery = 3f;

        static readonly Dictionary<string, string> _store = new Dictionary<string, string>();
        static bool _asked, _restored, _applying;
        static string _lastSent;
        static float _next;

        public void Register()
        {
            _store.Clear(); _asked = false; _restored = false; _applying = false; _lastSent = null; _next = 0f;
            SyncKit.On("ps.ask", OnAsk);
            SyncKit.On("ps.save", OnSave);
            SyncKit.On("ps.set", OnSet);
        }

        // ------------------------------------------------------------------ joiner

        internal static void Ask()
        {
            if (SyncKit.IsHost || !SyncKit.InSession || _asked) return;
            _asked = true;
            string name = Plugin.I != null ? Plugin.I.PlayerName : "player";
            SyncKit.SendTo(0, "ps.ask", w => w.Str(name));
        }

        public void Tick()
        {
            if (SyncKit.IsHost || !_restored || _applying || !SyncKit.InSession) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + SaveEvery;
            Send(false);
        }

        /// <summary>The joiner is leaving (quit to the menu): save the state now, not at the next tick.</summary>
        internal static void FlushNow()
        {
            if (SyncKit.IsHost || !_restored || _applying || !SyncKit.InSession) return;
            Send(true);
        }

        static void Send(bool force)
        {
            string snap;
            try { snap = Snapshot(); } catch (Exception e) { SyncKit.Warn("player state: snapshot failed: " + e.Message); return; }
            if (snap == null || (!force && snap == _lastSent)) return;
            _lastSent = snap;
            string name = Plugin.I != null ? Plugin.I.PlayerName : "player";
            SyncKit.SendTo(0, "ps.save", w => { w.Str(name); w.Str(snap); });
        }

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        static string V(Vector3 v) { return F(v.x) + "," + F(v.y) + "," + F(v.z); }

        static string Snapshot()
        {
            var p = Player.main;
            if (p == null) return null;
            var pos = p.transform.position;
            if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)) return null;
            var sb = new StringBuilder("v=1;pos=").Append(V(pos));
            var sub = p.currentSub;
            string sid = sub != null ? SyncKit.IdOf(sub) : null;
            if (sid != null) sb.Append(";sub=").Append(sid).Append(";lpos=").Append(V(sub.transform.InverseTransformPoint(pos)));
            float v;
            if (Stat(p.GetComponent<LiveMixin>(), "health", out v)) sb.Append(";hp=").Append(F(v));
            var surv = SurvivalOf(p);
            if (surv != null)
            {
                if (Stat(surv, "food", out v)) sb.Append(";food=").Append(F(v));
                if (Stat(surv, "water", out v)) sb.Append(";water=").Append(F(v));
            }
            return sb.ToString();
        }

        static Component SurvivalOf(Player p)
        {
            try { var t = AccessTools.TypeByName("Survival"); return t != null ? p.GetComponent(t) : null; } catch { return null; }
        }

        static bool Stat(object o, string field, out float v)
        {
            v = 0f;
            if (o == null) return false;
            try { v = Traverse.Create(o).Field(field).GetValue<float>(); return !(float.IsNaN(v) || float.IsInfinity(v)); } catch { return false; }
        }

        static void OnSet(int from, ByteReader r)
        {
            if (SyncKit.IsHost || from != 0) return;
            string data = r.Str() ?? "";
            if (data.Length == 0 || Plugin.I == null) { _restored = true; return; }   // nothing stored: start where the host is, from now on we save
            _applying = true;
            Plugin.I.StartCoroutine(ApplyRoutine(data));
        }

        static IEnumerator ApplyRoutine(string data)
        {
            while (Player.main == null) yield return null;
            yield return new WaitForSeconds(1.5f);     // let the world around the player settle first
            try { Apply(data); }
            catch (Exception e) { SyncKit.Warn("player state: could not restore: " + e.Message); }
            _applying = false; _restored = true; _lastSent = null;
        }

        static Dictionary<string, string> Parse(string data)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in data.Split(';')) { int i = part.IndexOf('='); if (i > 0) d[part.Substring(0, i)] = part.Substring(i + 1); }
            return d;
        }

        static bool ParseVec(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (s == null) return false;
            var a = s.Split(',');
            float x, y, z;
            if (a.Length != 3 || !float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !float.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            v = new Vector3(x, y, z);
            return !(float.IsNaN(x + y + z) || float.IsInfinity(x + y + z)) && v.magnitude < 20000f;
        }

        static void Apply(string data)
        {
            var p = Player.main;
            if (p == null) return;
            var d = Parse(data);
            Vector3 pos = Vector3.zero, lpos = Vector3.zero; string s;
            bool hasPos = d.TryGetValue("pos", out s) && ParseVec(s, out pos);
            Vector3 target = pos;
            SubRoot sub = null;
            if (d.TryGetValue("sub", out s) && !string.IsNullOrEmpty(s)) sub = SyncKit.Find<SubRoot>(s);
            string ls;
            if (sub != null && d.TryGetValue("lpos", out ls) && ParseVec(ls, out lpos)) target = sub.transform.TransformPoint(lpos);   // the same spot of the sub, wherever it is now
            if (hasPos)
            {
                SyncKit.Apply(() =>
                {
                    p.SetPosition(target);
                    if (sub != null) { try { Traverse.Create(p).Method("SetCurrentSub", new object[] { sub }).GetValue(); } catch { } }
                });
                SyncKit.Info("player state: back at the place we left" + (sub != null ? " (inside '" + sub.name + "')" : ""));
            }
            float v;
            if (d.TryGetValue("hp", out s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 1f) SetStat(p.GetComponent<LiveMixin>(), "health", v);
            var surv = SurvivalOf(p);
            if (surv != null)
            {
                if (d.TryGetValue("food", out s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0f) SetStat(surv, "food", v);
                if (d.TryGetValue("water", out s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0f) SetStat(surv, "water", v);
            }
        }

        static void SetStat(object o, string field, float v)
        {
            if (o == null) return;
            try { Traverse.Create(o).Field(field).SetValue(v); } catch (Exception e) { SyncKit.Warn("player state: could not set " + field + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ host

        static void OnAsk(int from, ByteReader r)
        {
            if (!SyncKit.IsHost || from == 0) return;
            string data = Load(r.Str()) ?? "";
            SyncKit.SendTo(from, "ps.set", w => w.Str(data));
        }

        static void OnSave(int from, ByteReader r)
        {
            if (!SyncKit.IsHost || from == 0) return;
            string name = r.Str(), data = r.Str();
            if (string.IsNullOrEmpty(name) || data == null || data.Length > MaxLen) return;
            _store[InventoryPersist.Key(name)] = data;
            try
            {
                var path = FileFor(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".tmp", data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) { SyncKit.Warn("player state: could not write the file for " + name + ": " + e.Message); }
        }

        static string Load(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string data;
            if (_store.TryGetValue(InventoryPersist.Key(name), out data)) return data;
            try
            {
                var path = FileFor(name);
                if (File.Exists(path)) { data = File.ReadAllText(path); _store[InventoryPersist.Key(name)] = data; return data; }
            }
            catch (Exception e) { SyncKit.Warn("player state: could not read the file for " + name + ": " + e.Message); }
            return null;
        }

        static string FileFor(string name)
        {
            var inv = InventoryPersist.PathFor(name);      // .../inventories/<world>/<player>.inv -> the same place, own extension
            return Path.ChangeExtension(inv, ".ps");
        }
    }

    // quitting to the menu: send the last state and inventory before the world goes away
    [HarmonyPatch]
    static class P_QuitFlush
    {
        static MethodBase TargetMethod() { return AccessTools.Method(typeof(IngameMenu), "QuitGame", new[] { typeof(bool) }); }
        static bool Prepare() { return TargetMethod() != null; }
        static void Prefix()
        {
            try { PlayerStatePersist.FlushNow(); } catch { }
            try { InventoryPersist.FlushNow(); } catch { }
        }
    }
}
