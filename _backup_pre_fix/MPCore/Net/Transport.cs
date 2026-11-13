using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MPCore.Net
{
    /// <summary>
    /// Reliable ordered message transport. Star topology: the host is peer 0,
    /// clients are 1..127. A client only ever sees peer 0.
    /// All events are raised on the thread that calls Poll().
    /// </summary>
    public interface ITransport : IDisposable
    {
        event Action<int> PeerConnected;
        event Action<int> PeerDisconnected;
        event Action<int, byte[]> Received;
        void Send(int peer, byte[] data);
        void Disconnect(int peer);
        void Poll();
    }

    // ---------------------------------------------------------------- loopback (tests, single process)

    public sealed class LoopbackHub
    {
        internal readonly Dictionary<int, LoopbackTransport> Ends = new Dictionary<int, LoopbackTransport>();
        int _next = 1;

        /// <summary>Test hook: (from, to, data) -> true drops the message.</summary>
        public Func<int, int, byte[], bool> Drop;

        public LoopbackTransport End(int peerId) { return Ends[peerId]; }

        public LoopbackTransport CreateHost()
        {
            var t = new LoopbackTransport(this, 0);
            Ends[0] = t;
            return t;
        }

        public LoopbackTransport CreateClient()
        {
            int id = _next++;
            var t = new LoopbackTransport(this, id);
            Ends[id] = t;
            Ends[0].Enqueue(() => Ends[0].RaiseConnected(id));
            t.Enqueue(() => t.RaiseConnected(0));
            return t;
        }
    }

    public sealed class LoopbackTransport : ITransport
    {
        readonly LoopbackHub _hub;
        readonly int _id;
        readonly Queue<Action> _q = new Queue<Action>();

        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> Received;

        internal LoopbackTransport(LoopbackHub hub, int id) { _hub = hub; _id = id; }
        internal void Enqueue(Action a) { lock (_q) _q.Enqueue(a); }
        internal void RaiseConnected(int p) { PeerConnected?.Invoke(p); }

        public void Send(int peer, byte[] data)
        {
            LoopbackTransport dst;
            if (!_hub.Ends.TryGetValue(peer, out dst)) return;
            if (_hub.Drop != null && _hub.Drop(_id, peer, data)) return;
            var copy = (byte[])data.Clone();
            int from = _id;
            dst.Enqueue(() => dst.Received?.Invoke(from, copy));
        }

        public void Disconnect(int peer)
        {
            LoopbackTransport dst;
            if (!_hub.Ends.TryGetValue(peer, out dst)) return;
            int from = _id;
            dst.Enqueue(() => dst.PeerDisconnected?.Invoke(from));
            Enqueue(() => PeerDisconnected?.Invoke(peer));
        }

        public void Poll()
        {
            while (true)
            {
                Action a;
                lock (_q) { if (_q.Count == 0) return; a = _q.Dequeue(); }
                a();
            }
        }

        public void Dispose()
        {
            foreach (var kv in new List<KeyValuePair<int, LoopbackTransport>>(_hub.Ends))
                if (kv.Key != _id && (_id != 0 ? kv.Key == 0 : true)) Disconnect(kv.Key);
        }
    }

    // ---------------------------------------------------------------- TCP

    sealed class TcpConn
    {
        public readonly int Id;
        readonly TcpClient _c;
        readonly NetworkStream _s;
        readonly object _wl = new object();
        readonly ConcurrentQueue<Action> _evt;
        readonly Action<int, byte[]> _onData;
        readonly Action<int> _onClose;
        volatile bool _closed;
        const int MaxFrame = 64 * 1024 * 1024;

        public TcpConn(int id, TcpClient c, ConcurrentQueue<Action> evt, Action<int, byte[]> onData, Action<int> onClose)
        {
            Id = id; _c = c; _evt = evt; _onData = onData; _onClose = onClose;
            c.NoDelay = true;
            _s = c.GetStream();
            var t = new Thread(ReadLoop) { IsBackground = true, Name = "mp-read-" + id };
            t.Start();
        }

        void ReadLoop()
        {
            var hdr = new byte[4];
            try
            {
                while (!_closed)
                {
                    ReadExact(hdr, 4);
                    int len = BitConverter.ToInt32(hdr, 0);
                    if (len < 0 || len > MaxFrame) throw new IOException("bad frame length");
                    var buf = new byte[len];
                    ReadExact(buf, len);
                    int id = Id;
                    _evt.Enqueue(() => _onData(id, buf));
                }
            }
            catch { }
            Close();
        }

        void ReadExact(byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = _s.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
        }

        // Sends go through a queue and a writer thread, so a slow peer or a big transfer never blocks the game.
        readonly Queue<byte[]> _out = new Queue<byte[]>();
        Thread _writer;

        public void Send(byte[] data)
        {
            if (_closed) return;
            lock (_wl)
            {
                _out.Enqueue(data);
                if (_writer == null)
                {
                    _writer = new Thread(WriteLoop) { IsBackground = true, Name = "mp-write-" + Id };
                    _writer.Start();
                }
                Monitor.Pulse(_wl);
            }
        }

        void WriteLoop()
        {
            try
            {
                while (!_closed)
                {
                    byte[] d;
                    lock (_wl)
                    {
                        while (_out.Count == 0 && !_closed) Monitor.Wait(_wl, 500);
                        if (_closed) return;
                        d = _out.Dequeue();
                    }
                    var frame = new byte[4 + d.Length];
                    Buffer.BlockCopy(BitConverter.GetBytes(d.Length), 0, frame, 0, 4);
                    Buffer.BlockCopy(d, 0, frame, 4, d.Length);
                    _s.Write(frame, 0, frame.Length);
                }
            }
            catch { Close(); }
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _c.Close(); } catch { }
            int id = Id;
            _evt.Enqueue(() => _onClose(id));
        }
    }

    public sealed class TcpHostTransport : ITransport
    {
        readonly TcpListener _l;
        readonly ConcurrentQueue<Action> _evt = new ConcurrentQueue<Action>();
        readonly Dictionary<int, TcpConn> _conns = new Dictionary<int, TcpConn>();
        readonly object _lock = new object();
        int _next = 1;
        volatile bool _stop;

        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> Received;

        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

        public TcpHostTransport(int port)
        {
            _l = new TcpListener(IPAddress.Any, port);
            _l.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "mp-accept" }.Start();
        }

        void AcceptLoop()
        {
            while (!_stop)
            {
                try
                {
                    var c = _l.AcceptTcpClient();
                    int id;
                    lock (_lock)
                    {
                        if (_next > 127) { c.Close(); continue; } // peer id space is 7 bits (see NetId layout)
                        id = _next++;
                    }
                    var conn = new TcpConn(id, c, _evt, (p, d) => Received?.Invoke(p, d), OnClose);
                    lock (_lock) _conns[id] = conn;
                    _evt.Enqueue(() => PeerConnected?.Invoke(id));
                }
                catch { if (_stop) return; }
            }
        }

        void OnClose(int id)
        {
            lock (_lock) _conns.Remove(id);
            PeerDisconnected?.Invoke(id);
        }

        public void Send(int peer, byte[] data)
        {
            TcpConn c;
            lock (_lock) _conns.TryGetValue(peer, out c);
            c?.Send(data);
        }

        public void Disconnect(int peer)
        {
            TcpConn c;
            lock (_lock) _conns.TryGetValue(peer, out c);
            c?.Close();
        }

        public void Poll()
        {
            Action a;
            while (_evt.TryDequeue(out a)) a();
        }

        public void Dispose()
        {
            _stop = true;
            try { _l.Stop(); } catch { }
            List<TcpConn> all;
            lock (_lock) all = new List<TcpConn>(_conns.Values);
            foreach (var c in all) c.Close();
        }
    }

    public sealed class TcpClientTransport : ITransport
    {
        readonly ConcurrentQueue<Action> _evt = new ConcurrentQueue<Action>();
        TcpConn _conn;

        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> Received;

        public TcpClientTransport(string host, int port)
        {
            // Connect on a worker thread so the game never freezes.
            new Thread(() =>
            {
                try
                {
                    var c = new TcpClient();
                    c.Connect(host, port);
                    _conn = new TcpConn(0, c, _evt, (p, d) => Received?.Invoke(p, d), id => PeerDisconnected?.Invoke(id));
                    _evt.Enqueue(() => PeerConnected?.Invoke(0));
                }
                catch { _evt.Enqueue(() => PeerDisconnected?.Invoke(0)); }
            }) { IsBackground = true, Name = "mp-connect" }.Start();
        }

        public void Send(int peer, byte[] data) { _conn?.Send(data); }
        public void Disconnect(int peer) { _conn?.Close(); }

        public void Poll()
        {
            Action a;
            while (_evt.TryDequeue(out a)) a();
        }

        public void Dispose() { _conn?.Close(); }
    }
}
