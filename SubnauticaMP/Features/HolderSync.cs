using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MPCore.IO;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Fire extinguisher holders (the wall mounts in a Cyclops and in bases). Whoever takes the extinguisher out, or puts it
    /// back, says so at once; the host also repeats the state of every holder near a player every two seconds, and everybody
    /// who disagrees fixes their copy. So one extinguisher exists, wherever it was taken from, and a late or lost message is
    /// corrected a moment later. A change made on this machine is not overwritten by a repeat for a few seconds.
    /// </summary>
    sealed class HolderSync : ISyncFeature
    {
        public string Name { get { return "holders"; } }

        const float LocalEvery = 0.25f, RepeatEvery = 2f, RefreshEvery = 5f, Grace = 3f, Range = 100f;

        static Type _type; static bool _looked;
        readonly List<Component> _holders = new List<Component>();
        readonly Dictionary<string, bool> _lastLocal = new Dictionary<string, bool>();
        readonly Dictionary<string, float> _touch = new Dictionary<string, float>();
        readonly Dictionary<string, Spot> _pose = new Dictionary<string, Spot>();    // where the extinguisher sat in its holder (local to the holder)
        readonly HashSet<string> _restoring = new HashSet<string>();
        readonly List<Vector3> _origins = new List<Vector3>();
        float _nextLocal, _nextRepeat, _nextRefresh;

        struct Spot { public Vector3 P; public Quaternion R; public Transform Parent; }

        public void Register()
        {
            _holders.Clear(); _lastLocal.Clear(); _touch.Clear(); _pose.Clear(); _restoring.Clear();
            _nextLocal = _nextRepeat = _nextRefresh = 0f;
            if (!_looked)
            {
                _looked = true;
                try { _type = AccessTools.TypeByName("FireExtinguisherHolder"); } catch { _type = null; }
                SyncKit.Info(_type != null ? "holders: found FireExtinguisherHolder" : "holders: no FireExtinguisherHolder type in this game build; holder sync idle");
            }
            SyncKit.On("hold.state", OnState);
        }

        public void Tick()
        {
            if (_type == null || !SyncKit.ShouldSend || Player.main == null) return;
            float now = Time.unscaledTime;
            if (now < _nextLocal) return;
            _nextLocal = now + LocalEvery;
            if (now >= _nextRefresh) { _nextRefresh = now + RefreshEvery; Refresh(); }

            bool repeat = SyncKit.IsHost && now >= _nextRepeat;
            if (repeat) _nextRepeat = now + RepeatEvery;
            if (repeat)
            {
                _origins.Clear();
                _origins.Add(Player.main.transform.position);
                if (Plugin.I != null) _origins.AddRange(Plugin.I.RemotePlayerPositions());
            }
            for (int i = 0; i < _holders.Count; i++)
            {
                var h = _holders[i];
                if (h == null) continue;
                string id, path;
                if (!SyncKit.IdAndPath(h, out id, out path)) continue;
                string key = id + "/" + path;
                bool has = HasTank(h);
                bool known; bool last;
                known = _lastLocal.TryGetValue(key, out last);
                if (!known) { _lastLocal[key] = has; }
                else if (last != has)
                {
                    // changed here (taken or put back by a player on this machine): tell everybody now
                    _lastLocal[key] = has; _touch[key] = now;
                    if (has == false) Remember(h, key);
                    SendState(id, path, has, false);
                    continue;
                }
                if (has) Remember(h, key);
                if (repeat && NearAny(h.transform.position)) SendState(id, path, has, true);
            }
        }

        void Refresh()
        {
            _holders.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_type)) { var c = o as Component; if (c != null) _holders.Add(c); }
        }

        bool NearAny(Vector3 p)
        {
            float r2 = Range * Range;
            for (int i = 0; i < _origins.Count; i++) if ((p - _origins[i]).sqrMagnitude <= r2) return true;
            return false;
        }

        // ------------------------------------------------------------------ state of one holder

        static Pickupable TankOf(Component holder)
        {
            foreach (var pk in holder.GetComponentsInChildren<Pickupable>(false))
                if (pk != null && !pk.attached && pk.GetTechType() == TechType.FireExtinguisher) return pk;
            return null;
        }

        static bool HasTank(Component holder) { return TankOf(holder) != null; }

        void Remember(Component holder, string key)
        {
            var tank = TankOf(holder);
            if (tank == null) return;
            _pose[key] = new Spot { P = tank.transform.localPosition, R = tank.transform.localRotation, Parent = tank.transform.parent };
        }

        void SendState(string id, string path, bool has, bool periodic)
        {
            SyncKit.Send("hold.state", w => { w.Str(id); w.Str(path ?? ""); w.U8(has ? (byte)1 : (byte)0); w.U8(periodic ? (byte)1 : (byte)0); });
        }

        // ------------------------------------------------------------------ receive

        void OnState(int from, ByteReader r)
        {
            string id = r.Str(), path = r.Str(); bool has = r.U8() != 0; bool periodic = r.U8() != 0;
            if (string.IsNullOrEmpty(id)) return;
            string key = id + "/" + path;
            float now = Time.unscaledTime;
            float t;
            if (periodic && _touch.TryGetValue(key, out t) && now - t < Grace) return;   // we changed it a moment ago: the repeat may be older than that
            var tr = SyncKit.FindChild(id, path);
            var h = tr != null && _type != null ? tr.GetComponent(_type) : null;
            if (h == null) return;
            bool cur = HasTank(h);
            if (cur == has) { _lastLocal[key] = has; return; }
            _touch[key] = now;
            if (!has)
            {
                Remember(h, key);
                var tank = TankOf(h);
                _lastLocal[key] = false;       // set first: our own change detection must not send this back
                if (tank != null) SyncKit.Apply(() => { UnityEngine.Object.Destroy(tank.gameObject); SetFlag(h, false); });
                SyncKit.Info("holders: the extinguisher was taken elsewhere; removed it here (" + (periodic ? "repeat" : "event") + ")");
            }
            else
            {
                _lastLocal[key] = true;
                if (_restoring.Add(key) && Plugin.I != null) Plugin.I.StartCoroutine(Restore(h, key));
            }
        }

        IEnumerator Restore(Component holder, string key)
        {
            Spot pose;
            bool haveSpot = _pose.TryGetValue(key, out pose) && pose.Parent != null;
            var task = CraftData.GetPrefabForTechTypeAsync(TechType.FireExtinguisher, false);
            yield return task;
            var prefab = task.GetResult();
            _restoring.Remove(key);
            if (prefab == null || holder == null) yield break;
            if (!haveSpot) { SyncKit.Warn("holders: the extinguisher was put back elsewhere, but this holder's old place is unknown here; not restored"); _lastLocal[key] = false; yield break; }
            if (HasTank(holder)) yield break;
            SyncKit.Apply(() =>
            {
                var go = UnityEngine.Object.Instantiate(prefab, pose.Parent);
                go.transform.localPosition = pose.P; go.transform.localRotation = pose.R;
                go.SetActive(true);
                var rb = go.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true;
                SetFlag(holder, true);
            });
            SyncKit.Info("holders: the extinguisher was put back elsewhere; restored it here");
        }

        // the holder keeps its own "has a tank" flag under one of a few names; set whichever exists
        static void SetFlag(Component holder, bool v)
        {
            foreach (var n in new[] { "hasTank", "hasExtinguisher", "isFull", "full" })
            {
                try { var t = Traverse.Create(holder).Field(n); if (t.FieldExists()) { t.SetValue(v); return; } } catch { }
            }
        }
    }
}
