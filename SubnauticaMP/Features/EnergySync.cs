using System;
using System.Collections.Generic;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Battery charge of everything that is not in a player's own inventory: power cells in a Cyclops, batteries in a Seamoth,
    /// Prawn Suit or locker. The machine that knows the real value sends it often (twice a second when it changed, all of it
    /// every few seconds), so a lost or late message is corrected a moment later and the two sides never drift apart.
    /// Who knows the real value: the machine that drives the vehicle, else the host.
    /// Also watches the power readout of nearby Cyclops and logs it, since a bad number there shows on the helm display.
    /// </summary>
    sealed class EnergySync : ISyncFeature
    {
        public string Name { get { return "energy"; } }

        const float Interval = 0.5f, Range = 200f, RefreshEvery = 5f, ProbeEvery = 5f;
        const int FullEvery = 10, MaxPerMessage = 128;

        sealed class Src { public Battery B; public string Id, OwnerId; public Transform OwnerT; }
        readonly List<Src> _src = new List<Src>();
        readonly Dictionary<string, float> _last = new Dictionary<string, float>();
        readonly List<Vector3> _origins = new List<Vector3>();
        float _nextSend, _nextRefresh, _nextProbe;
        int _cycle, _start;
        bool _forceFull, _bok;
        readonly HashSet<string> _warned = new HashSet<string>();

        public void Register()
        {
            _src.Clear(); _last.Clear(); _cycle = 0; _start = 0; _nextSend = _nextRefresh = _nextProbe = 0f; _forceFull = false; _warned.Clear();
            SyncKit.On("eng.bat", OnBatteries);
            SyncKit.On("eng.req", (from, r) => { if (SyncKit.IsHost) _forceFull = true; });
        }

        public void Tick()
        {
            if (!SyncKit.ShouldSend || Player.main == null) return;
            float now = Time.unscaledTime;
            if (now >= _nextProbe) { _nextProbe = now + ProbeEvery; try { Probe(); } catch (Exception e) { SyncKit.Warn("energy: probe: " + e.Message); } }
            if (now < _nextSend) return;
            _nextSend = now + Interval;
            try { Send(now); } catch (Exception e) { SyncKit.Warn("energy: send: " + e.Message); }
        }

        // ------------------------------------------------------------------ send

        void Refresh()
        {
            _src.Clear();
            foreach (var b in Resources.FindObjectsOfTypeAll<Battery>())
            {
                if (b == null || !b.gameObject.scene.IsValid()) continue;
                var u = b.GetComponent<UniqueIdentifier>();      // its own id: a parent's id would be shared by every battery in it
                string id = u != null ? u.Id : null;
                if (string.IsNullOrEmpty(id)) continue;
                var src = new Src { B = b, Id = id };
                Component own = null;
                var vs = b.GetComponentsInParent<Vehicle>(true);
                if (vs != null && vs.Length > 0) own = vs[0];
                if (own == null) { var ss = b.GetComponentsInParent<SubRoot>(true); if (ss != null && ss.Length > 0) own = ss[0]; }
                if (own != null) { src.OwnerId = SyncKit.IdOf(own); src.OwnerT = own.transform; }
                _src.Add(src);
            }
        }

        static bool InPlayerInventory(Battery b)
        {
            try
            {
                var pk = b.GetComponent<Pickupable>();
                if (pk == null || Inventory.main == null) return false;
                var ii = Traverse.Create(pk).Field("inventoryItem").GetValue<InventoryItem>();
                return ii != null && ii.container != null && (object)ii.container == (object)Inventory.main.container;
            }
            catch { return false; }
        }

        static float ChargeOf(Battery b, out bool ok)
        {
            ok = false;
            try { var v = Traverse.Create(b).Field("_charge").GetValue<float>(); ok = !(float.IsNaN(v) || float.IsInfinity(v)); return v; }
            catch { return 0f; }
        }

        void Send(float now)
        {
            if (now >= _nextRefresh) { _nextRefresh = now + RefreshEvery; Refresh(); }
            _cycle++;
            bool full = _forceFull || _cycle % FullEvery == 0;
            _forceFull = false;
            if (_last.Count > 8192) _last.Clear();

            _origins.Clear();
            _origins.Add(Player.main.transform.position);
            if (SyncKit.IsHost && Plugin.I != null) _origins.AddRange(Plugin.I.RemotePlayerPositions());
            float r2 = Range * Range;
            string driven = EntitySync.DrivenId();

            var ids = new List<string>(); var vals = new List<float>();
            int count = _src.Count;
            if (count == 0) return;
            _start = (_start + 1) % count;
            for (int n = 0; n < count; n++)
            {
                var s = _src[(_start + n) % count];
                if (s.B == null) continue;
                // who knows the real charge: the driver of the vehicle it sits in, else the host
                bool mine = s.OwnerId != null && s.OwnerId == driven;
                if (!mine)
                {
                    if (!SyncKit.IsHost) continue;
                    if (s.OwnerId != null && EntitySync.OccupiedByOther(s.OwnerId)) continue;
                }
                var at = s.OwnerT != null ? s.OwnerT.position : s.B.transform.position;
                bool near = false;
                for (int i = 0; i < _origins.Count; i++) if ((at - _origins[i]).sqrMagnitude <= r2) { near = true; break; }
                if (!near) continue;
                if (InPlayerInventory(s.B)) continue;
                bool ok; float c = ChargeOf(s.B, out ok);
                if (!ok) continue;
                float last;
                bool changed = !_last.TryGetValue(s.Id, out last) || Mathf.Abs(last - c) > 0.01f;
                if (!changed && !full) continue;
                _last[s.Id] = c;
                ids.Add(s.Id); vals.Add(c);
                if (ids.Count >= MaxPerMessage) break;
            }
            if (ids.Count == 0) return;
            SyncKit.Send("eng.bat", w =>
            {
                w.VarI(ids.Count);
                for (int i = 0; i < ids.Count; i++) { w.Str(ids[i]); w.F32(vals[i]); }
            });
        }

        // ------------------------------------------------------------------ receive

        void OnBatteries(int from, ByteReader r)
        {
            int count = (int)r.VarI();
            if (count < 0 || count > MaxPerMessage) return;
            var bats = new List<Battery>(); var vals = new List<float>();
            string driven = EntitySync.DrivenId();
            for (int i = 0; i < count; i++)
            {
                string id = r.Str(); float v = r.F32();
                if (float.IsNaN(v) || float.IsInfinity(v) || v < 0f) continue;
                var go = SyncKit.Find(id);
                var b = go != null ? go.GetComponent<Battery>() : null;
                if (b == null) continue;
                if (driven != null)
                {
                    var vs = b.GetComponentsInParent<Vehicle>(true);
                    var ss = b.GetComponentsInParent<SubRoot>(true);
                    string own = vs != null && vs.Length > 0 ? SyncKit.IdOf(vs[0]) : (ss != null && ss.Length > 0 ? SyncKit.IdOf(ss[0]) : null);
                    if (own != null && own == driven) continue;      // the vehicle we drive: our own value counts
                }
                bats.Add(b); vals.Add(v);
            }
            if (bats.Count == 0) return;
            SyncKit.Apply(() =>
            {
                for (int i = 0; i < bats.Count; i++)
                {
                    try
                    {
                        if (bats[i] == null) continue;
                        bool ok; float cur = ChargeOf(bats[i], out ok);
                        if (ok && Mathf.Abs(cur - vals[i]) < 0.05f) continue;
                        Traverse.Create(bats[i]).Field("_charge").SetValue(vals[i]);
                    }
                    catch (Exception e) { SyncKit.Warn("energy: set charge: " + e.Message); }
                }
            });
        }

        // ------------------------------------------------------------------ the Cyclops power readout

        void Probe()
        {
            foreach (var sub in UnityEngine.Object.FindObjectsOfType<SubRoot>())
            {
                if (sub == null || sub.isBase) continue;
                if ((sub.transform.position - Player.main.transform.position).sqrMagnitude > 150f * 150f) continue;
                object relay = null;
                try { relay = Traverse.Create(sub).Field("powerRelay").GetValue(); } catch { }
                if (relay == null) continue;
                float p, m;
                try
                {
                    p = Traverse.Create(relay).Method("GetPower").GetValue<float>();
                    m = Traverse.Create(relay).Method("GetMaxPower").GetValue<float>();
                }
                catch (Exception e) { Once("relay-api", "energy: cannot read the power relay of a Cyclops: " + e.Message); continue; }
                bool bad = float.IsNaN(p) || float.IsInfinity(p) || float.IsNaN(m) || float.IsInfinity(m) || m <= 0f;
                if (bad)
                {
                    Once("bad-" + sub.GetInstanceID(), "energy: Cyclops '" + sub.name + "' power relay reads " + p + " / " + m + " (the helm shows a nonsense percentage); asking for all charges");
                    if (!SyncKit.IsHost) SyncKit.Send("eng.req", w => { });
                    _forceFull = true;
                    _warned.Remove("bad-" + sub.GetInstanceID());   // report again next time it is still bad, at most every probe
                    // A request for the charges did not clear it (it stayed 0 / 0 for the whole session): the relay has no power source
                    // connected here, or the batteries are not in the sub on this machine. Say which, once, so the next log shows the cause.
                    try
                    {
                        int nb = 0, nId = 0; var sb = new System.Text.StringBuilder();
                        foreach (var b in sub.GetComponentsInChildren<Battery>(true))
                        {
                            nb++;
                            var u = b.GetComponent<UniqueIdentifier>();
                            if (u != null && !string.IsNullOrEmpty(u.Id)) nId++;
                            if (sb.Length < 160) sb.Append(' ').Append(b.name).Append('=').Append(ChargeOf(b, out _bok).ToString("F0")).Append(b.gameObject.activeInHierarchy ? "" : "(inactive)");
                        }
                        int inbound = -1;
                        try { var l = Traverse.Create(relay).Field("inboundPowerSources").GetValue() as System.Collections.ICollection; if (l != null) inbound = l.Count; } catch { }
                        // what the six inbound sources are, and what in the sub looks like a power cell holder: names only, to find where the cells went
                        var kinds = new System.Text.StringBuilder();
                        try
                        {
                            var l2 = Traverse.Create(relay).Field("inboundPowerSources").GetValue() as System.Collections.IEnumerable;
                            if (l2 != null) foreach (var o in l2) { if (kinds.Length > 200) break; kinds.Append(' ').Append(o == null ? "null" : o.GetType().Name); }
                        }
                        catch { }
                        var holders = new System.Text.StringBuilder();
                        try
                        {
                            foreach (var c in sub.GetComponentsInChildren<Component>(true))
                            {
                                if (c == null || holders.Length > 300) continue;
                                string tn = c.GetType().Name;
                                if (tn.IndexOf("Charger", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("PowerCell", StringComparison.OrdinalIgnoreCase) >= 0 || tn.IndexOf("PowerSource", StringComparison.OrdinalIgnoreCase) >= 0)
                                    holders.Append(' ').Append(tn).Append('@').Append(c.gameObject.name);
                            }
                        }
                        catch { }
                        Once("detail-" + sub.GetInstanceID(), "energy: bad relay detail: " + nb + " battery objects under the sub (" + nId + " with an id), relay inbound sources " + inbound + " [" + kinds + " ] holders [" + holders + " ];" + sb);
                    }
                    catch (Exception e) { Once("detail-err", "energy: bad relay detail failed: " + e.Message); }
                }
                else SyncKit.Info("energy: Cyclops '" + sub.name + "' relay " + p.ToString("F1") + " / " + m.ToString("F1"));
            }
        }

        void Once(string key, string msg)
        {
            if (_warned.Add(key)) SyncKit.Warn(msg);
        }
    }
}
