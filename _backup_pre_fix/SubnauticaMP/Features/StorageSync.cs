using System;
using System.Collections;
using System.Collections.Generic;
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

        sealed class Op { public bool Add; public Component Owner; public string OwnerId, Path, ItemId; public TechType Tech; public int Frame; }

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
            _queue.Enqueue(new Op { Add = add, Owner = owner, OwnerId = id, Path = path, ItemId = uid.Id, Tech = ii.item.GetTechType(), Frame = Time.frameCount });
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
                    SyncKit.Send("store.add", w => { w.Str(o.OwnerId); w.Str(o.Path); SyncKit.Tech(w, o.Tech); w.Str(o.ItemId); });
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

        static InventoryItem FindItem(ItemsContainer c, string itemId)
        {
            foreach (InventoryItem ii in c)
            {
                if (ii == null || ii.item == null) continue;
                var u = ii.item.GetComponent<UniqueIdentifier>();
                if (u != null && u.Id == itemId) return ii;
            }
            return null;
        }

        static IItemsContainer ContainerOfItem(Pickupable p)
        {
            try
            {
                var ii = Traverse.Create(p).Field("inventoryItem").GetValue<InventoryItem>();
                return ii != null ? ii.container : null;
            }
            catch { return null; }
        }

        static void RemoveAndDestroy(InventoryItem ii, IItemsContainer c)
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
            string ownerId = r.Str(), path = r.Str(); var tech = SyncKit.Tech(r); string itemId = r.Str();
            _recentRem.Remove(itemId);
            var c = Resolve(ownerId, path);
            if (c == null || Plugin.I == null) return;
            if (FindItem(c, itemId) != null) return;
            Plugin.I.StartCoroutine(AddRoutine(c, tech, itemId));
        }

        static IEnumerator AddRoutine(ItemsContainer c, TechType tech, string itemId)
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
        static void Prefix() { StorageSync.Loading++; }
        static void Finalizer() { if (StorageSync.Loading > 0) StorageSync.Loading--; }
    }
}
