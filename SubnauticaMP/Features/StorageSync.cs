using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Shared storage: lockers, wall lockers, small storage (StorageContainer), vehicle storage (Seamoth storage modules via
    /// SeamothStorageContainer, Prawn Suit and Cyclops lockers via StorageContainer).
    ///
    /// Messages:
    ///   store.add  owner id, path, TechType, item id   an item went into a container
    ///   store.rem  owner id, path, item id             an item left a container
    ///   store.deny item id                             host to a client: someone else took that item first, destroy your copy
    ///
    /// Sending: Harmony postfixes on ItemsContainer.NotifyAddItem / NotifyRemoveItem (every add/remove, whoever causes it:
    /// UI drag, drill arm, crafting that consumes from lockers, ...). Events are queued and sent one frame later in Tick, in order, because
    /// (1) an item destroyed because its container is being unloaded also fires NotifyRemoveItem, which must not be sent
    /// (we only send if the owner is still alive a frame later), and (2) a move between two containers keeps remove-then-add order.
    ///
    /// Not synced: the player's own inventory, containers loaded from disk (StorageHelper.TransferItems), item state such as battery charge.
    /// Removal race: both players take the same item. Whoever's removal reaches the host first wins; the host sends store.deny to the other,
    /// whose copy in the inventory is destroyed (only if the host has a record of that removal in the last 30 s).
    /// </summary>
    sealed class StorageSync : ISyncFeature
    {
        public string Name { get { return "storage"; } }

        sealed class Op { public bool Add; public Component Owner; public string OwnerId, Path, ItemId; public TechType Tech; public float Charge = -1f; public int Frame; }

        internal static int Loading;
        static readonly Queue<Op> _queue = new Queue<Op>();
        static readonly Dictionary<string, KeyValuePair<int, float>> _recentRem = new Dictionary<string, KeyValuePair<int, float>>();
        static float _nextPrune;

        public void Register()
        {
            _queue.Clear(); _recentRem.Clear(); Loading = 0;
            SyncKit.On("store.add", OnAdd);
            SyncKit.On("store.rem", OnRem);
            SyncKit.On("store.deny", OnDeny);
            SyncKit.On("store.chk", OnCheck);
            SyncKit.On("store.all", OnAll);
            _touch.Clear(); _asked.Clear();
        }

        // ------------------------------------------------------------------ check on open

        // when a container was last changed on this machine: the host's list may not have that change yet
        static readonly Dictionary<string, float> _touch = new Dictionary<string, float>();
        static readonly Dictionary<string, float> _asked = new Dictionary<string, float>();

        /// <summary>The local player opened a container: a joiner asks the host what is really in it and fixes any difference.</summary>
        internal static void OnOpened(StorageContainer sc)
        {
            if (sc == null || !SyncKit.ShouldSend || SyncKit.IsHost || sc.container == null) return;
            if (Inventory.main != null && Inventory.main.container == sc.container) return;
            string id, path;
            if (!SyncKit.IdAndPath(sc, out id, out path)) return;
            string key = id + "/" + path;
            float t;
            if (_asked.TryGetValue(key, out t) && Time.unscaledTime - t < 1f) return;
            _asked[key] = Time.unscaledTime;
            SyncKit.SendTo(0, "store.chk", w => { w.Str(id); w.Str(path ?? ""); });
        }

        /// <summary>The charge of a battery or power cell item, -1 if it is not one. Re-creating an item from its tech type alone used to reset it.</summary>
        internal static float ChargeOf(Pickupable pk)
        {
            try
            {
                var b = pk != null ? pk.GetComponent<Battery>() : null;
                if (b == null) return -1f;
                float v = Traverse.Create(b).Field("_charge").GetValue<float>();
                return float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? -1f : v;
            }
            catch { return -1f; }
        }

        internal static void SetCharge(GameObject go, float charge)
        {
            if (charge < 0f || go == null) return;
            try { var b = go.GetComponent<Battery>(); if (b != null) Traverse.Create(b).Field("_charge").SetValue(charge); } catch { }
        }

        static void OnCheck(int from, ByteReader r)
        {
            if (!SyncKit.IsHost || from == 0) return;
            string id = r.Str(), path = r.Str();
            var c = Resolve(id, path);
            if (c == null) return;
            var ids = new List<string>(); var techs = new List<TechType>(); var chs = new List<float>();
            foreach (InventoryItem ii in c)
            {
                if (ii == null || ii.item == null) continue;
                var u = ii.item.GetComponent<UniqueIdentifier>();
                if (u == null || string.IsNullOrEmpty(u.Id)) continue;
                ids.Add(u.Id); techs.Add(ii.item.GetTechType()); chs.Add(ChargeOf(ii.item));
            }
            SyncKit.SendTo(from, "store.all", w =>
            {
                w.Str(id); w.Str(path ?? ""); w.VarI(ids.Count);
                for (int i = 0; i < ids.Count; i++) { w.Str(ids[i]); SyncKit.Tech(w, techs[i]); w.F32(chs[i]); }
            });
        }

        static void OnAll(int from, ByteReader r)
        {
            if (SyncKit.IsHost || from != 0) return;
            string id = r.Str(), path = r.Str(); int n = (int)r.VarI();
            if (n < 0 || n > 4096) return;
            var hostIds = new HashSet<string>(); var tech = new Dictionary<string, TechType>(); var hch = new Dictionary<string, float>();
            for (int i = 0; i < n; i++) { string iid = r.Str(); var t = SyncKit.Tech(r); float ch = r.F32(); hostIds.Add(iid); tech[iid] = t; hch[iid] = ch; }
            var c = Resolve(id, path);
            if (c == null || Plugin.I == null) return;
            // a change made here a moment ago is still on its way to the host: its list cannot have it yet
            float touched;
            if (_touch.TryGetValue(id + "/" + path, out touched) && Time.unscaledTime - touched < 2f) return;
            foreach (var op in _queue) if (op.OwnerId == id && op.Path == path) return;

            var local = new List<InventoryItem>();
            foreach (InventoryItem ii in c) if (ii != null && ii.item != null) local.Add(ii);
            var localIds = new HashSet<string>();
            int fixedCount = 0;
            foreach (var ii in local)
            {
                var u = ii.item.GetComponent<UniqueIdentifier>();
                string iid = u != null ? u.Id : null;
                if (iid != null) localIds.Add(iid);
                if (iid != null && !hostIds.Contains(iid)) { RemoveAndDestroy(ii, c); fixedCount++; }      // not in the real container (a copy)
                else if (iid != null)
                {
                    // same item, different charge: the host's value is the real one
                    float lc = ChargeOf(ii.item), hc;
                    if (hch.TryGetValue(iid, out hc) && hc >= 0f && lc >= 0f && Mathf.Abs(lc - hc) > 0.5f) { SyncKit.Apply(() => SetCharge(ii.item.gameObject, hc)); fixedCount++; }
                }
            }
            foreach (var iid in hostIds)
                if (!localIds.Contains(iid)) { Plugin.I.StartCoroutine(AddRoutine(c, tech[iid], iid, hch[iid])); fixedCount++; }   // missing here
            if (fixedCount > 0) SyncKit.Info("storage: checked a container on opening it, fixed " + fixedCount + " difference(s)");
        }

        // ------------------------------------------------------------------ send side

        internal static bool OwnerOf(ItemsContainer c, out Component owner)
        {
            owner = null;
            if (c == null || c.tr == null) return false;
            var sc = c.tr.GetComponentInParent<StorageContainer>();
            if (sc != null && sc.container == c) { owner = sc; return true; }
            var ssc = c.tr.GetComponentInParent<SeamothStorageContainer>();
            if (ssc != null && ssc.container == c) { owner = ssc; return true; }
            return false;
        }

        internal static void Queue(ItemsContainer c, InventoryItem ii, bool add)
        {
            if (!SyncKit.ShouldSend || Loading > 0 || c == null || ii == null || ii.item == null) return;
            if (Inventory.main != null && Inventory.main.container == c) return;
            Component owner;
            if (!OwnerOf(c, out owner)) return;
            var uid = ii.item.GetComponent<UniqueIdentifier>();
            if (uid == null || string.IsNullOrEmpty(uid.Id)) return;
            string id, path;
            if (!SyncKit.IdAndPath(owner, out id, out path)) return;
            _touch[id + "/" + path] = Time.unscaledTime;
            _queue.Enqueue(new Op { Add = add, Owner = owner, OwnerId = id, Path = path, ItemId = uid.Id, Tech = ii.item.GetTechType(), Charge = ChargeOf(ii.item), Frame = Time.frameCount });
        }

        public void Tick()
        {
            while (_queue.Count > 0 && _queue.Peek().Frame < Time.frameCount)
            {
                var op = _queue.Dequeue();
                if (op.Owner == null || !SyncKit.ShouldSend) continue;   // container was unloaded or session ended: not a real change
                var o = op;
                if (o.Add)
                {
                    _recentRem.Remove(o.ItemId);
                    SyncKit.Send("store.add", w => { w.Str(o.OwnerId); w.Str(o.Path); SyncKit.Tech(w, o.Tech); w.Str(o.ItemId); w.F32(o.Charge); });
                }
                else
                {
                    _recentRem[o.ItemId] = new KeyValuePair<int, float>(SyncKit.LocalPeer, Time.unscaledTime);
                    SyncKit.Send("store.rem", w => { w.Str(o.OwnerId); w.Str(o.Path); w.Str(o.ItemId); });
                }
            }
            if (Time.unscaledTime > _nextPrune)
            {
                _nextPrune = Time.unscaledTime + 30f;
                var old = new List<string>();
                foreach (var kv in _recentRem) if (Time.unscaledTime - kv.Value.Value > 30f) old.Add(kv.Key);
                foreach (var k in old) _recentRem.Remove(k);
            }
        }

        // ------------------------------------------------------------------ receive side

        static ItemsContainer Resolve(string id, string path)
        {
            var t = SyncKit.FindChild(id, path);
            if (t == null) return null;
            var sc = t.GetComponent<StorageContainer>();
            if (sc != null) return sc.container;
            var ssc = t.GetComponent<SeamothStorageContainer>();
            return ssc != null ? ssc.container : null;
        }

        internal static InventoryItem FindItem(ItemsContainer c, string itemId)
        {
            foreach (InventoryItem ii in c)
            {
                if (ii == null || ii.item == null) continue;
                var u = ii.item.GetComponent<UniqueIdentifier>();
                if (u != null && u.Id == itemId) return ii;
            }
            return null;
        }

        internal static IItemsContainer ContainerOfItem(Pickupable p)
        {
            try
            {
                var ii = Traverse.Create(p).Field("inventoryItem").GetValue<InventoryItem>();
                return ii != null ? ii.container : null;
            }
            catch { return null; }
        }

        internal static void RemoveAndDestroy(InventoryItem ii, IItemsContainer c)
        {
            var pk = ii.item;
            SyncKit.Apply(() =>
            {
                if (!c.RemoveItem(ii, true, false)) { var ic = c as ItemsContainer; if (ic != null) ic.RemoveItem(pk, true); }
            });
            if (pk != null) { try { UnityEngine.Object.DestroyImmediate(pk.gameObject); } catch { } }
        }

        static void OnRem(int from, ByteReader r)
        {
            string ownerId = r.Str(), path = r.Str(), itemId = r.Str();
            if (SyncKit.IsHost)
            {
                KeyValuePair<int, float> prev;
                if (_recentRem.TryGetValue(itemId, out prev) && prev.Key != from && Time.unscaledTime - prev.Value < 30f)
                {
                    SyncKit.Info("storage: " + itemId + " already taken by peer " + prev.Key + ", denying peer " + from);
                    SyncKit.SendTo(from, "store.deny", w => w.Str(itemId));
                    return;
                }
                _recentRem[itemId] = new KeyValuePair<int, float>(from, Time.unscaledTime);
            }
            var c = Resolve(ownerId, path);
            if (c == null) return;
            var ii = FindItem(c, itemId);
            if (ii == null) return;
            RemoveAndDestroy(ii, c);
        }

        static void OnDeny(int from, ByteReader r)
        {
            string itemId = r.Str();
            UniqueIdentifier uid;
            if (!UniqueIdentifier.TryGetIdentifier(itemId, out uid) || uid == null) return;
            var pk = uid.GetComponent<Pickupable>();
            if (pk == null) return;
            var c = ContainerOfItem(pk);
            if (c == null || Inventory.main == null || (object)c != (object)Inventory.main.container) return;   // only undo a copy sitting in our inventory
            var ii = FindItem(Inventory.main.container, itemId);
            if (ii == null) return;
            RemoveAndDestroy(ii, c);
            SyncKit.Info("storage: item " + itemId + " was taken by someone else first; removed from our inventory");
            try { ErrorMessage.AddMessage("Another player took that item first."); } catch { }
        }

        static void OnAdd(int from, ByteReader r)
        {
            string ownerId = r.Str(), path = r.Str(); var tech = SyncKit.Tech(r); string itemId = r.Str(); float charge = r.F32();
            _recentRem.Remove(itemId);
            var c = Resolve(ownerId, path);
            if (c == null || Plugin.I == null) return;
            if (FindItem(c, itemId) != null) return;
            Plugin.I.StartCoroutine(AddRoutine(c, tech, itemId, charge));
        }

        internal static IEnumerator AddRoutine(ItemsContainer c, TechType tech, string itemId, float charge = -1f)
        {
            GameObject prefab = null;
            var task = CraftData.GetPrefabForTechTypeAsync(tech, false);
            yield return task;
            try { prefab = task.GetResult(); } catch { }
            if (prefab == null || c == null) yield break;
            if (FindItem(c, itemId) != null) yield break;   // arrived meanwhile
            try
            {
                bool keepId = true;
                UniqueIdentifier existing;
                if (UniqueIdentifier.TryGetIdentifier(itemId, out existing) && existing != null)
                {
                    var epk = existing.GetComponent<Pickupable>();
                    var ec = epk != null ? ContainerOfItem(epk) : null;
                    if (ec != null && Inventory.main != null && (object)ec == (object)Inventory.main.container)
                        keepId = false;   // our own inventory has the same id (copied from the host's save): leave it alone
                    else
                    {
                        // stale copy: in the world (already picked up elsewhere) or in another container (item moved): remove it, we re-create it here
                        if (ec != null) { var eii = epk != null ? Traverse.Create(epk).Field("inventoryItem").GetValue<InventoryItem>() : null; if (eii != null) SyncKit.Apply(() => ec.RemoveItem(eii, true, false)); }
                        UnityEngine.Object.DestroyImmediate(existing.gameObject);
                    }
                }
                SyncKit.Apply(() =>
                {
                    var owner = c.tr != null ? c.tr.position : Vector3.zero;
                    var go = UnityEngine.Object.Instantiate(prefab, owner, Quaternion.identity);
                    var uid = go.GetComponent<UniqueIdentifier>();
                    if (uid != null && keepId) uid.Id = itemId;
                    var pk = go.GetComponent<Pickupable>();
                    if (pk == null) { UnityEngine.Object.Destroy(go); return; }
                    go.SetActive(true);
                    SetCharge(go, charge);
                    pk.Initialize();                       // same state the game gives an item before it goes into a container (inactive, attached)
                    c.UnsafeAdd(new InventoryItem(pk));
                });
            }
            catch (Exception e) { SyncKit.Warn("storage: add failed: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(ItemsContainer), "NotifyAddItem")]
    static class P_StoreAdd
    {
        static void Postfix(ItemsContainer __instance, InventoryItem item) { try { StorageSync.Queue(__instance, item, true); } catch { } }
    }

    [HarmonyPatch(typeof(ItemsContainer), "NotifyRemoveItem")]
    static class P_StoreRemove
    {
        static void Postfix(ItemsContainer __instance, InventoryItem item) { try { StorageSync.Queue(__instance, item, false); } catch { } }
    }

    // Loading saved containers adds their items through TransferItems; that is not a player action.
    [HarmonyPatch(typeof(StorageHelper), "TransferItems")]
    static class P_StoreLoad
    {
        static void Prefix() { StorageSync.Loading++; SyncKit.BeginLoading(); }
        static void Finalizer() { if (StorageSync.Loading > 0) StorageSync.Loading--; SyncKit.EndLoading(); }
    }

    // the player opens a locker, wall locker, small storage or a vehicle's storage: check it against the host
    [HarmonyPatch]
    static class P_StoreOpen
    {
        static bool Prepare() { return TargetMethods().Any(); }
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in typeof(StorageContainer).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.Name == "Open" && !m.IsAbstract) yield return m;
        }
        static void Postfix(StorageContainer __instance) { try { StorageSync.OnOpened(__instance); } catch { } }
    }
}
