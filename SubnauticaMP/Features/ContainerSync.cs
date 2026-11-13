using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Every other place that holds items: bioreactors, nuclear reactors, battery and power cell chargers, vehicle upgrade slots,
    /// filtration machines, and containers added by mods. Nothing here is named: any component that has a field of type
    /// ItemsContainer or Equipment is found by reflection, on any object near the player, and kept in step. Lockers and Seamoth
    /// storage have their own feature (StorageSync) and are left to it; the player's own inventory is never touched.
    ///
    /// How: each machine looks at the containers around it four times a second. When one differs from what was last in step, the
    /// change was made here: the new contents are sent at once ("ctr.set"). The host also repeats the contents of every container
    /// near a player every three seconds, and anybody who differs fixes their copy, so a lost or late message heals. A container that
    /// was just changed here is not overwritten by a repeat for a few seconds, and one that is being filled from a message is
    /// not read until it has settled. Items keep their ids, so one battery is one battery everywhere.
    /// </summary>
    sealed class ContainerSync : ISyncFeature
    {
        public string Name { get { return "containers"; } }

        const float PollEvery = 0.25f, ScanEvery = 4f, RepeatEvery = 3f, Grace = 3f, Busy = 3f, Range = 45f;
        const int MaxContainers = 300;

        sealed class Cont
        {
            public Component Owner; public string Id, Path, TypeName, Field, Key; public object Container; public bool Equip;
        }

        static readonly Dictionary<Type, FieldInfo[]> _fields = new Dictionary<Type, FieldInfo[]>();
        static readonly HashSet<string> _skipOwners = new HashSet<string> { "StorageContainer", "SeamothStorageContainer", "Inventory", "Player" };
        readonly List<Cont> _conts = new List<Cont>();
        readonly Dictionary<string, string> _agreed = new Dictionary<string, string>();
        readonly Dictionary<string, float> _touch = new Dictionary<string, float>(), _busy = new Dictionary<string, float>();
        readonly HashSet<string> _logged = new HashSet<string>();
        float _nextPoll, _nextScan, _nextRepeat;
        int _sent, _fixed;

        public void Register()
        {
            _conts.Clear(); _agreed.Clear(); _touch.Clear(); _busy.Clear(); _logged.Clear();
            _nextPoll = _nextScan = _nextRepeat = 0f; _sent = _fixed = 0;
            SyncKit.On("ctr.set", OnSet);
        }

        // ------------------------------------------------------------------ find containers

        static FieldInfo[] ContainerFields(Type t)
        {
            FieldInfo[] r;
            if (_fields.TryGetValue(t, out r)) return r;
            var list = new List<FieldInfo>();
            for (var cur = t; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Component) && cur != typeof(object); cur = cur.BaseType)
                foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(ItemsContainer) || f.FieldType == typeof(Equipment)) list.Add(f);
            r = list.ToArray();
            _fields[t] = r;
            return r;
        }

        void Scan()
        {
            _conts.Clear();
            var me = Player.main.transform.position;
            float r2 = Range * Range;
            var buf = new List<MonoBehaviour>();
            foreach (var uid in UniqueIdentifier.AllIdentifiers)
            {
                if (_conts.Count >= MaxContainers) break;
                if (uid == null || string.IsNullOrEmpty(uid.Id)) continue;
                if ((uid.transform.position - me).sqrMagnitude > r2) continue;
                if (uid.GetComponentInParent<Player>() != null) continue;
                buf.Clear();
                uid.GetComponentsInChildren<MonoBehaviour>(true, buf);
                foreach (var c in buf)
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    if (_skipOwners.Contains(t.Name)) continue;
                    var fs = ContainerFields(t);
                    if (fs.Length == 0 || c.GetComponentInParent<UniqueIdentifier>() != uid) continue;
                    string id, path;
                    if (!SyncKit.IdAndPath(c, out id, out path)) continue;
                    foreach (var f in fs)
                    {
                        object val;
                        try { val = f.GetValue(c); } catch { continue; }
                        if (val == null) continue;
                        var k = new Cont { Owner = c, Id = id, Path = path, TypeName = t.FullName, Field = f.Name, Container = val, Equip = f.FieldType == typeof(Equipment) };
                        k.Key = id + "/" + path + "#" + k.TypeName + "." + k.Field;
                        _conts.Add(k);
                        if (_logged.Add(k.TypeName + "." + k.Field))
                            SyncKit.Info("containers: syncing " + t.Name + "." + f.Name + " (" + (k.Equip ? "Equipment" : "ItemsContainer") + ")");
                    }
                }
            }
        }

        // ------------------------------------------------------------------ read and write one container

        sealed class Line { public string Slot, Id; public TechType Tech; public float Charge = -1f; public InventoryItem Item; }

        static List<Line> Items(object container, bool equip)
        {
            var list = new List<Line>();
            if (equip)
            {
                var eq = Traverse.Create(container).Field("equipment").GetValue<IDictionary>();
                if (eq != null)
                    foreach (DictionaryEntry kv in eq)
                    {
                        var ii = kv.Value as InventoryItem;
                        if (ii == null || ii.item == null) continue;
                        var u = ii.item.GetComponent<UniqueIdentifier>();
                        if (u == null || string.IsNullOrEmpty(u.Id)) continue;
                        list.Add(new Line { Slot = kv.Key as string ?? "", Id = u.Id, Tech = ii.item.GetTechType(), Charge = StorageSync.ChargeOf(ii.item), Item = ii });
                    }
            }
            else
            {
                var c = container as ItemsContainer;
                if (c != null)
                    foreach (InventoryItem ii in c)
                    {
                        if (ii == null || ii.item == null) continue;
                        var u = ii.item.GetComponent<UniqueIdentifier>();
                        if (u == null || string.IsNullOrEmpty(u.Id)) continue;
                        list.Add(new Line { Slot = "", Id = u.Id, Tech = ii.item.GetTechType(), Charge = StorageSync.ChargeOf(ii.item), Item = ii });
                    }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Slot + "|" + a.Id, b.Slot + "|" + b.Id));
            return list;
        }

        static string Sig(List<Line> items)
        {
            var sb = new StringBuilder();
            foreach (var l in items) sb.Append(l.Slot).Append('|').Append(l.Id).Append('|').Append(l.Tech).Append('\n');
            return sb.ToString();
        }

        /// <summary>What is sent: the lines of Sig plus the charge of each battery. The charge is not part of Sig, so a battery that is charging
        /// does not look like a changed container.</summary>
        static string Wire(List<Line> items)
        {
            var sb = new StringBuilder();
            foreach (var l in items) sb.Append(l.Slot).Append('|').Append(l.Id).Append('|').Append(l.Tech).Append('|').Append(l.Charge.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        static string StripCharge(string wire)
        {
            var sb = new StringBuilder();
            foreach (var ln in wire.Split('\n'))
            {
                if (ln.Length == 0) continue;
                var p = ln.Split('|');
                if (p.Length < 3) { sb.Append(ln).Append('\n'); continue; }
                sb.Append(p[0]).Append('|').Append(p[1]).Append('|').Append(p[2]).Append('\n');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ tick

        public void Tick()
        {
            if (!SyncKit.Active || Player.main == null) return;
            float now = Time.unscaledTime;
            if (now >= _nextScan) { _nextScan = now + ScanEvery; try { Scan(); } catch (Exception e) { SyncKit.Warn("containers: scan: " + e.Message); } }
            if (now < _nextPoll) return;
            _nextPoll = now + PollEvery;
            bool repeat = SyncKit.IsHost && now >= _nextRepeat;
            if (repeat) _nextRepeat = now + RepeatEvery;
            for (int i = 0; i < _conts.Count; i++)
            {
                var c = _conts[i];
                if (c.Owner == null || c.Container == null) continue;
                string sig;
                try { sig = Sig(Items(c.Container, c.Equip)); } catch { continue; }
                float until;
                if (_busy.TryGetValue(c.Key, out until))
                {
                    if (now < until) continue;                       // still being filled from a message
                    _busy.Remove(c.Key); _agreed[c.Key] = sig; continue; // settled: whatever it is now is the shared state
                }
                string agreed;
                if (!_agreed.TryGetValue(c.Key, out agreed)) { _agreed[c.Key] = sig; continue; }   // first look: from the shared save
                if (sig != agreed)
                {
                    if (!SyncKit.ShouldSend || StorageSync.Loading > 0) { _agreed[c.Key] = sig; continue; }
                    _agreed[c.Key] = sig; _touch[c.Key] = now;       // changed here: tell everybody
                    Send(c, sig, false); _sent++;
                    DebugLog.Log("container", "changed here: " + c.TypeName + "." + c.Field + " -> " + sig.Split('\n').Length + " items", 10);
                }
                else if (repeat) Send(c, sig, true);
            }
        }

        static void Send(Cont c, string sig, bool periodic)
        {
            string wire = sig;
            try { wire = Wire(Items(c.Container, c.Equip)); } catch { }
            SyncKit.Send("ctr.set", w => { w.Str(c.Id); w.Str(c.Path ?? ""); w.Str(c.TypeName); w.Str(c.Field); w.Str(wire); w.U8(periodic ? (byte)1 : (byte)0); });
        }

        // ------------------------------------------------------------------ receive

        void OnSet(int from, ByteReader r)
        {
            string id = r.Str(), path = r.Str(), typeName = r.Str(), field = r.Str(), state = r.Str() ?? ""; bool periodic = r.U8() != 0;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(field)) return;
            string key = id + "/" + path + "#" + typeName + "." + field;
            float now = Time.unscaledTime;
            float t;
            if (periodic && _touch.TryGetValue(key, out t) && now - t < Grace) return;     // changed here a moment ago: the repeat may be older
            float until;
            if (_busy.TryGetValue(key, out until) && now < until) return;                   // already filling it from an earlier message

            var tr = SyncKit.FindChild(id, path);
            Type type = null;
            try { type = AccessTools.TypeByName(typeName); } catch { }
            if (tr == null || type == null) return;
            var owner = tr.GetComponent(type);
            if (owner == null) return;
            FieldInfo fi = null;
            foreach (var f in ContainerFields(type)) if (f.Name == field) { fi = f; break; }
            if (fi == null) return;
            object container;
            try { container = fi.GetValue(owner); } catch { return; }
            if (container == null) return;
            bool equip = fi.FieldType == typeof(Equipment);

            // what the sender says is in it, against what is in it here
            var want = new List<Line>();
            foreach (var ln in state.Split('\n'))
            {
                var p = ln.Split('|');
                if (p.Length < 3 || string.IsNullOrEmpty(p[1])) continue;
                TechType tech;
                try { tech = (TechType)Enum.Parse(typeof(TechType), p[2]); } catch { continue; }
                float chg = -1f;
                if (p.Length >= 4) float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out chg);
                want.Add(new Line { Slot = p[0], Id = p[1], Tech = tech, Charge = chg });
            }
            var have = Items(container, equip);
            string plain = StripCharge(state);
            if (!SyncKit.IsHost)
                foreach (var h in have)
                    foreach (var wl in want)
                        if (wl.Id == h.Id && wl.Charge >= 0f && h.Charge >= 0f && Mathf.Abs(wl.Charge - h.Charge) > 0.5f) { var hh = h; var ww = wl; SyncKit.Apply(() => StorageSync.SetCharge(hh.Item.item.gameObject, ww.Charge)); }
            if (Sig(have) == plain) { _agreed[key] = plain; return; }
            var wantIds = new HashSet<string>(want.Select(l => l.Id));
            var haveIds = new HashSet<string>(have.Select(l => l.Id));
            _busy[key] = now + Busy;
            _agreed[key] = plain;
            int changes = 0;
            var ic = container as IItemsContainer;
            foreach (var l in have)
                if (!wantIds.Contains(l.Id) && ic != null) { StorageSync.RemoveAndDestroy(l.Item, ic); changes++; }
            foreach (var l in want)
            {
                if (haveIds.Contains(l.Id) || Plugin.I == null) continue;
                changes++;
                if (equip) Plugin.I.StartCoroutine(AddEquip(container, l.Slot, l.Tech, l.Id, l.Charge));
                else Plugin.I.StartCoroutine(StorageSync.AddRoutine((ItemsContainer)container, l.Tech, l.Id, l.Charge));
            }
            _fixed += changes;
            SyncKit.Info("containers: " + type.Name + "." + field + " brought in step (" + changes + " change(s), " + (periodic ? "repeat" : "event") + ")");
        }

        static IEnumerator AddEquip(object equipment, string slot, TechType tech, string itemId, float charge = -1f)
        {
            GameObject prefab = null;
            var task = CraftData.GetPrefabForTechTypeAsync(tech, false);
            yield return task;
            try { prefab = task.GetResult(); } catch { }
            if (prefab == null || equipment == null) yield break;
            UniqueIdentifier existing;
            if (UniqueIdentifier.TryGetIdentifier(itemId, out existing) && existing != null)
            {
                // a stale copy with this id (in the world or elsewhere): take it out, this slot gets the real one
                var epk = existing.GetComponent<Pickupable>();
                var ec = epk != null ? StorageSync.ContainerOfItem(epk) : null;
                if (ec != null && Inventory.main != null && (object)ec == (object)Inventory.main.container) yield break;   // our own inventory: leave it
                if (ec != null) { var eii = Traverse.Create(epk).Field("inventoryItem").GetValue<InventoryItem>(); if (eii != null) SyncKit.Apply(() => ec.RemoveItem(eii, true, false)); }
                UnityEngine.Object.DestroyImmediate(existing.gameObject);
            }
            try
            {
                SyncKit.Apply(() =>
                {
                    var go = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
                    var uid = go.GetComponent<UniqueIdentifier>();
                    if (uid != null) uid.Id = itemId;
                    var pk = go.GetComponent<Pickupable>();
                    if (pk == null) { UnityEngine.Object.Destroy(go); return; }
                    go.SetActive(true);
                    StorageSync.SetCharge(go, charge);
                    pk.Initialize();
                    var item = new InventoryItem(pk);
                    bool ok = false;
                    try { ok = Traverse.Create(equipment).Method("AddItem", new object[] { slot, item, true }).GetValue<bool>(); } catch { }
                    if (!ok) { UnityEngine.Object.Destroy(go); SyncKit.Warn("containers: could not add " + tech + " to slot '" + slot + "'"); }
                });
            }
            catch (Exception e) { SyncKit.Warn("containers: equip add failed: " + e.Message); }
        }
    }
}
