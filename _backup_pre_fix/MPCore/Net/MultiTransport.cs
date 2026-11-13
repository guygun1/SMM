using System;
using System.Collections.Generic;

namespace MPCore.Net
{
    /// <summary>
    /// Lets one host accept players through several transports at once (TCP by IP and Steam relay) while the
    /// session sees a single peer id space (1..127). Players on different transports can play together.
    /// </summary>
    public sealed class MultiHostTransport : ITransport
    {
        readonly List<ITransport> _inner = new List<ITransport>();
        readonly Dictionary<int, KeyValuePair<int, int>> _byUnified = new Dictionary<int, KeyValuePair<int, int>>();
        readonly Dictionary<long, int> _byInner = new Dictionary<long, int>();

        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> Received;

        public MultiHostTransport(params ITransport[] inner)
        {
            for (int i = 0; i < inner.Length; i++) Add(inner[i]);
        }

        public void Add(ITransport t)
        {
            int idx = _inner.Count;
            _inner.Add(t);
            t.PeerConnected += p => OnConnected(idx, p);
            t.PeerDisconnected += p => OnDisconnected(idx, p);
            t.Received += (p, d) => OnReceived(idx, p, d);
        }

        static long Key(int t, int p) { return ((long)t << 32) | (uint)p; }

        void OnConnected(int t, int p)
        {
            int id = 0;
            for (int i = 1; i <= 127; i++) if (!_byUnified.ContainsKey(i)) { id = i; break; }
            if (id == 0) { _inner[t].Disconnect(p); return; } // full
            _byUnified[id] = new KeyValuePair<int, int>(t, p);
            _byInner[Key(t, p)] = id;
            PeerConnected?.Invoke(id);
        }

        void OnDisconnected(int t, int p)
        {
            int id;
            if (!_byInner.TryGetValue(Key(t, p), out id)) return;
            _byInner.Remove(Key(t, p));
            _byUnified.Remove(id);
            PeerDisconnected?.Invoke(id);
        }

        void OnReceived(int t, int p, byte[] d)
        {
            int id;
            if (_byInner.TryGetValue(Key(t, p), out id)) Received?.Invoke(id, d);
        }

        public void Send(int peer, byte[] data)
        {
            KeyValuePair<int, int> m;
            if (_byUnified.TryGetValue(peer, out m)) _inner[m.Key].Send(m.Value, data);
        }

        public void Disconnect(int peer)
        {
            KeyValuePair<int, int> m;
            if (_byUnified.TryGetValue(peer, out m)) _inner[m.Key].Disconnect(m.Value);
        }

        public void Poll()
        {
            for (int i = 0; i < _inner.Count; i++) _inner[i].Poll();
        }

        public void Dispose()
        {
            foreach (var t in _inner) { try { t.Dispose(); } catch { } }
        }
    }
}
