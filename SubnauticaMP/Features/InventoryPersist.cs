using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// A joining player cannot save the game (the host's world is a temporary copy), so their inventory used to start empty
    /// every time. This feature keeps it on the host instead:
    ///
    ///   inv.save  name, data   joiner to host: the joiner's current inventory (sent a few seconds after it changes)
    ///   inv.ask   name         joiner to host: send me my inventory (once, after the world is loaded and the copy of the host's cleared)
    ///   inv.set   data         host to joiner: the stored inventory, "" if there is none
    ///
    /// The host keeps one text file per player and per world (BepInEx config folder, SubnauticaMP/inventories). The host's own
    /// inventory is part of the game's own save and is not touched here.
    /// Saved: the items in the inventory and what is worn or equipped, with battery charge. The battery inside a tool (type and charge) is saved too. Not saved: quick slot bindings, other item state.
    /// </summary>
    public class InventoryPersist : ISyncFeature
    {
        public string Name { get { return "inventory"; } }

        const int MaxLen = 60000;
        const float SaveEvery = 3f;

        // host
        static readonly Dictionary<string, string> _store = new Dictionary<string, string>();
        // joiner
        static bool _asked, _restored, _applying;
        static string _lastSent;
        static float _next;

        public void Register()
        {
            _store.Clear(); _asked = false; _restored = false; _applying = false; _lastSent = null; _next = 0f;
            SyncKit.On("inv.ask", OnAsk);
            SyncKit.On("inv.save", OnSave);
            SyncKit.On("inv.set", OnSet);
        }

        // ------------------------------------------------------------------ joiner side

        /// <summary>The joiner has loaded the host's world and emptied the copy of the host's inventory: fetch our own.</summary>
        internal static void OnInventoryCleared()
        {
            if (SyncKit.IsHost || !SyncKit.InSession || _asked) return;
            _asked = true;
            string name = Plugin.I != null ? Plugin.I.PlayerName : "player";
            SyncKit.SendTo(0, "inv.ask", w => w.Str(name));
        }

        public void Tick()
        {
            if (SyncKit.IsHost || !_restored || _applying || !SyncKit.InSession) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + SaveEvery;
            string snap;
            try { snap = Snapshot(); } catch (Exception e) { SyncKit.Warn("inventory: snapshot failed: " + e.Message); return; }
            if (snap == null || snap == _lastSent) return;
            _lastSent = snap;
            string name = Plugin.I != null ? Plugin.I.PlayerName : "player";
            SyncKit.SendTo(0, "inv.save", w => { w.Str(name); w.Str(snap); });
        }

        /// <summary>The joiner is leaving (quit to the menu): send the inventory now, not at the next 3-second tick.</summary>
        internal static void FlushNow()
        {
            if (SyncKit.IsHost || !_restored || _applying || !SyncKit.InSession) return;
            string snap;
            try { snap = Snapshot(); } catch { return; }
            if (snap == null || snap == _lastSent) return;
            _lastSent = snap;
            string name = Plugin.I != null ? Plugin.I.PlayerName : "player";
            SyncKit.SendTo(0, "inv.save", w => { w.Str(name); w.Str(snap); });
        }

        static void OnSet(int from, ByteReader r)
        {
            if (SyncKit.IsHost || from != 0) return;
            string data = r.Str() ?? "";
            if (data.Length == 0 || Plugin.I == null) { _restored = true; return; }   // nothing stored: start empty, from now on we save
            _applying = true;
            Plugin.I.StartCoroutine(ApplyRoutine(data));
        }

        static IEnumerator ApplyRoutine(string data)
        {
            var lines = data.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length < 4) continue;   // kind | slot | tech | charge
                TechType tech;
                try { tech = (TechType)Enum.Parse(typeof(TechType), parts[2]); } catch { continue; }
                float charge; if (!float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out charge)) charge = -1f;
                while (Inventory.main == null || Player.main == null) yield return null;
                GameObject prefab = null;
                var task = CraftData.GetPrefabForTechTypeAsync(tech, false);
                yield return task;
                try { prefab = task.GetResult(); } catch { }
                if (prefab == null) { SyncKit.Warn("inventory: no prefab for " + parts[2]); continue; }
                GameObject made = null;
                try { made = AddOne(prefab, parts[0] == "E" ? parts[1] : null, charge); }
                catch (Exception e) { SyncKit.Warn("inventory: could not restore " + parts[2] + ": " + e.Message); }
                // the battery that sat inside a tool: fields 5 and 6 (battery tech, charge)
                if (made != null && parts.Length >= 6 && parts[4].Length > 0)
                {
                    TechType btech; float bch;
                    bool okT = true;
                    try { btech = (TechType)Enum.Parse(typeof(TechType), parts[4]); } catch { btech = TechType.None; okT = false; }
                    if (!float.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out bch)) bch = -1f;
                    if (okT && btech != TechType.None)
                    {
                        GameObject bprefab = null;
                        var bt = CraftData.GetPrefabForTechTypeAsync(btech, false);
                        yield return bt;
                        try { bprefab = bt.GetResult(); } catch { }
                        if (bprefab != null) { try { SetToolBattery(made, bprefab, btech, bch); } catch (Exception e) { SyncKit.Warn("inventory: could not put the battery back in " + parts[2] + ": " + e.Message); } }
                    }
                }
            }
            _applying = false;
            _restored = true;
            _lastSent = null;
            SyncKit.Info("inventory restored (" + lines.Length + " entries)");
        }

        static GameObject AddOne(GameObject prefab, string slot, float charge)
        {
            GameObject result = null;
            SyncKit.Apply(() =>
            {
                var inv = Inventory.main;
                var go = UnityEngine.Object.Instantiate(prefab, Player.main.transform.position, Quaternion.identity);
                var uid = go.GetComponent<UniqueIdentifier>();
                if (uid != null) uid.Id = Guid.NewGuid().ToString();   // never the id of an item that came with the host's save
                var pk = go.GetComponent<Pickupable>();
                if (pk == null) { UnityEngine.Object.Destroy(go); return; }
                result = go;
                go.SetActive(true);
                if (charge >= 0f)
                {
                    var b = go.GetComponent<Battery>();
                    if (b != null) { try { Traverse.Create(b).Field("_charge").SetValue(charge); } catch { } }
                }
                pk.Initialize();
                var item = new InventoryItem(pk);
                if (slot != null)
                {
                    bool ok = false;
                    try { ok = Traverse.Create(inv.equipment).Method("AddItem", new object[] { slot, item, true }).GetValue<bool>(); } catch { }
                    if (ok) return;
                }
                inv.container.UnsafeAdd(item);
            });
            return result;
        }

        /// <summary>The battery a tool holds: its tech type and charge (null / -1 if the item has no energy slot or it is empty).</summary>
        static void ToolBattery(Pickupable pk, out string tech, out float charge)
        {
            tech = ""; charge = -1f;
            try
            {
                var em = pk.GetComponent<EnergyMixin>();
                if (em == null) return;
                var bgo = Traverse.Create(em).Method("GetBatteryGameObject").GetValue<GameObject>();
                if (bgo == null) return;
                var bp = bgo.GetComponent<Pickupable>();
                var bb = bgo.GetComponent<Battery>();
                if (bp == null || bb == null) return;
                tech = bp.GetTechType().ToString();
                charge = Traverse.Create(bb).Field("_charge").GetValue<float>();
            }
            catch { tech = ""; charge = -1f; }
        }

        /// <summary>Puts a battery into a restored tool: the charge of the one it already holds if it is the same kind, else a new one.</summary>
        static void SetToolBattery(GameObject tool, GameObject batteryPrefab, TechType btech, float charge)
        {
            SyncKit.Apply(() =>
            {
                var em = tool.GetComponent<EnergyMixin>();
                if (em == null) return;
                var cur = Traverse.Create(em).Method("GetBatteryGameObject").GetValue<GameObject>();
                if (cur != null)
                {
                    var cp = cur.GetComponent<Pickupable>(); var cb = cur.GetComponent<Battery>();
                    if (cp != null && cb != null && cp.GetTechType() == btech)
                    {
                        if (charge >= 0f) Traverse.Create(cb).Field("_charge").SetValue(charge);
                        return;
                    }
                }
                var go = UnityEngine.Object.Instantiate(batteryPrefab, tool.transform.position, Quaternion.identity);
                var uid = go.GetComponent<UniqueIdentifier>();
                if (uid != null) uid.Id = Guid.NewGuid().ToString();
                var pk = go.GetComponent<Pickupable>();
                if (pk == null) { UnityEngine.Object.Destroy(go); return; }
                go.SetActive(true);
                var bat = go.GetComponent<Battery>();
                if (bat != null && charge >= 0f) { try { Traverse.Create(bat).Field("_charge").SetValue(charge); } catch { } }
                pk.Initialize();
                var item = new InventoryItem(pk);
                bool placed = false;
                try
                {
                    var slot = Traverse.Create(em).Field("batterySlot").GetValue();
                    if (slot != null) { Traverse.Create(slot).Method("AddItem", new object[] { item }).GetValue(); placed = true; }
                }
                catch (Exception e) { SyncKit.Warn("inventory: battery slot add failed: " + e.Message); }
                try { Traverse.Create(em).Method("NotifyHasBattery", new object[] { item }).GetValue(); placed = true; } catch { }
                if (!placed) { UnityEngine.Object.Destroy(go); SyncKit.Warn("inventory: no way to put a battery in " + tool.name); }
            });
        }

        // ------------------------------------------------------------------ snapshot

        static string Snapshot()
        {
            var inv = Inventory.main;
            if (inv == null || Player.main == null) return null;
            var sb = new StringBuilder();
            foreach (InventoryItem ii in inv.container) AppendItem(sb, "I", "", ii);
            var eq = Traverse.Create(inv.equipment).Field("equipment").GetValue<IDictionary>();
            if (eq != null)
                foreach (DictionaryEntry kv in eq) AppendItem(sb, "E", kv.Key as string ?? "", kv.Value as InventoryItem);
            return sb.ToString();
        }

        static void AppendItem(StringBuilder sb, string kind, string slot, InventoryItem ii)
        {
            if (ii == null || ii.item == null) return;
            var tech = ii.item.GetTechType();
            if (tech == TechType.None || slot.IndexOf('|') >= 0 || slot.IndexOf('\n') >= 0) return;
            float charge = -1f;
            var b = ii.item.GetComponent<Battery>();
            if (b != null) { try { charge = Traverse.Create(b).Field("_charge").GetValue<float>(); } catch { } }
            if (sb.Length > 0) sb.Append('\n');
            string btech; float bch;
            ToolBattery(ii.item, out btech, out bch);
            sb.Append(kind).Append('|').Append(slot).Append('|').Append(tech.ToString()).Append('|')
              .Append(charge.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
              .Append('|').Append(btech).Append('|').Append(bch.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------ host side

        static void OnAsk(int from, ByteReader r)
        {
            if (!SyncKit.IsHost || from == 0) return;
            string name = r.Str();
            string data = Load(name) ?? "";
            SyncKit.SendTo(from, "inv.set", w => w.Str(data));
        }

        static void OnSave(int from, ByteReader r)
        {
            if (!SyncKit.IsHost || from == 0) return;
            string name = r.Str(), data = r.Str();
            if (string.IsNullOrEmpty(name) || data == null || data.Length > MaxLen) return;
            _store[Key(name)] = data;
            try
            {
                var path = PathFor(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".tmp", data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) { SyncKit.Warn("inventory: could not write the file for " + name + ": " + e.Message); }
        }

        static string Load(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string data;
            if (_store.TryGetValue(Key(name), out data)) return data;
            try
            {
                var path = PathFor(name);
                if (File.Exists(path)) { data = File.ReadAllText(path); _store[Key(name)] = data; return data; }
            }
            catch (Exception e) { SyncKit.Warn("inventory: could not read the file for " + name + ": " + e.Message); }
            return null;
        }

        internal static string Key(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.Trim().ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length == 0 ? "player" : sb.ToString();
        }

        internal static string PathFor(string name)
        {
            string slot = Saves.CurrentSlotName();
            if (string.IsNullOrEmpty(slot)) slot = "world";
            var sb = new StringBuilder();
            foreach (char c in slot) sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return Path.Combine(Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "SubnauticaMP"), Path.Combine("inventories", sb.ToString())), Key(name) + ".inv");
        }
    }
}
