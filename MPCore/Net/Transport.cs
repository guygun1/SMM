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
        volatile bool _closing;          // Shutdown(): no new sends, writer drains _out, then the socket closes
        int _closeQueued;                // Interlocked flag: the close event is queued exactly once
        Timer _watchdog;
        const int MaxFrame = 16 * 1024 * 1024;
        const int MaxQueuedBytes = 16 * 1024 * 1024;   // unsent bytes per connection before the peer is dropped
        const int MaxQueuedEvents = 100000;            // events not yet raised by Poll() (shared queue)
        const int DrainTimeoutMs = 2000;

        public TcpConn(int id, TcpClient c, ConcurrentQueue<Action> evt, Action<int, byte[]> onData, Action<int> onClose)
        {
            Id = id; _c = c; _evt = evt; _onData = onData; _onClose = onClose;
            c.NoDelay = true;
            try
            {
                c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                // Windows: detect a silent dead peer after 30 s instead of the OS default of about 2 hours.
                var ka = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(ka, 0);
                BitConverter.GetBytes(30000u).CopyTo(ka, 4);
                BitConverter.GetBytes(5000u).CopyTo(ka, 8);
                c.Client.IOControl(IOControlCode.KeepAliveValues, ka, null);
            }
            catch { }
            _s = c.GetStream();
        }

        // Called by the owner after PeerConnected has been queued, so no frame can be raised before it.
        public void Start()
        {
            var t = new Thread(ReadLoop) { IsBackground = true, Name = "mp-read-" + Id };
            t.Start();
            lock (_wl) StartWriterLocked();
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
                    if (_evt.Count > MaxQueuedEvents) throw new IOException("event queue overflow");
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
        long _queued;                    // bytes in _out, guarded by _wl
        Thread _writer;

        void StartWriterLocked()         // caller holds _wl
        {
            if (_writer != null || _closed) return;
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "mp-write-" + Id };
            _writer.Start();
        }

        public void Send(byte[] data)
        {
            if (_closed || _closing) return;
            bool overflow = false;
            lock (_wl)
            {
                if (_closed || _closing) return;
                if (_queued + data.Length > MaxQueuedBytes) overflow = true;
                else
                {
                    _out.Enqueue(data);
                    _queued += data.Length;
                    StartWriterLocked();
                    Monitor.Pulse(_wl);
                }
            }
            if (overflow) Close();       // the peer cannot keep up: drop it rather than grow memory without bound
        }

        void WriteLoop()
        {
            try
            {
                while (true)
                {
                    byte[] d;
                    lock (_wl)
                    {
                        while (_out.Count == 0 && !_closed && !_closing) Monitor.Wait(_wl, 500);
                        if (_closed) return;
                        if (_out.Count == 0) break;   // shutting down and everything has been written
                        d = _out.Dequeue();
                        _queued -= d.Length;
                    }
                    var frame = new byte[4 + d.Length];
                    Buffer.BlockCopy(BitConverter.GetBytes(d.Length), 0, frame, 0, 4);
                    Buffer.BlockCopy(d, 0, frame, 4, d.Length);
                    _s.Write(frame, 0, frame.Length);
                }
                // Drained: send FIN so the peer reads every frame before it sees the end of the stream.
                try { _c.Client.Shutdown(SocketShutdown.Send); } catch { }
                // Do not close yet: closing a socket that still has unread input sends a RST, which can make the peer drop
                // frames it has not read. The read loop closes when the peer answers with its own FIN, or the watchdog does.
                return;
            }
            catch { }
            Close();
        }

        /// <summary>Graceful close: frames already queued are still written, then the socket closes.
        /// Gives up after a short timeout so a stuck peer cannot hold the connection open.</summary>
        public void Shutdown()
        {
            lock (_wl)
            {
                if (_closing || _closed) return;
                _closing = true;
                StartWriterLocked();          // makes sure the drain runs even if Start() was never called
                Monitor.PulseAll(_wl);
            }
            _watchdog = new Timer(_ => Close(), null, DrainTimeoutMs, Timeout.Infinite);
        }

        /// <summary>Hard close. Safe to call many times; the close event is raised once.</summary>
        public void Close()
        {
            _closed = true;
            lock (_wl) Monitor.PulseAll(_wl);
            try { _c.Close(); } catch { }
            if (Interlocked.CompareExchange(ref _closeQueued, 1, 0) == 0)
            {
                int id = Id;
                _evt.Enqueue(() => _onClose(id));
            }
        }
    }

    public sealed class TcpHostTransport : ITransport
    {
        const int ReuseCooldownMs = 5000;   // a freed id waits this long before it is handed out again (if another id is free)
        readonly TcpListener _l;
        readonly ConcurrentQueue<Action> _evt = new ConcurrentQueue<Action>();
        readonly Dictionary<int, TcpConn> _conns = new Dictionary<int, TcpConn>();
        readonly Dictionary<int, int> _freedAt = new Dictionary<int, int>();   // id -> Environment.TickCount when its peer closed
        readonly object _lock = new object();
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

        // Lowest free id in 1..127. Caller holds _lock. Returns 0 when the id space is full.
        int AllocateIdLocked()
        {
            int now = Environment.TickCount;
            int cooling = 0;
            for (int i = 1; i <= 127; i++)
            {
                if (_conns.ContainsKey(i)) continue;
                int freed;
                if (_freedAt.TryGetValue(i, out freed) && now - freed < ReuseCooldownMs)
                {
                    if (cooling == 0) cooling = i;   // usable only if no other id is free
                    continue;
                }
                return i;
            }
            return cooling;
        }

        void AcceptLoop()
        {
            while (!_stop)
            {
                try
                {
                    var c = _l.AcceptTcpClient();
                    TcpConn conn;
                    int id;
                    lock (_lock)
                    {
                        id = AllocateIdLocked();
                        if (id == 0) { c.Close(); continue; } // peer id space is 7 bits (see NetId layout)
                        conn = new TcpConn(id, c, _evt, (p, d) => Received?.Invoke(p, d), OnClose);
                        _conns[id] = conn;
                    }
                    _evt.Enqueue(() => PeerConnected?.Invoke(id));
                    conn.Start();   // only now: the first frame (Hello) must not be raised before PeerConnected
                }
                catch { if (_stop) return; Thread.Sleep(50); }
            }
        }

        void OnClose(int id)
        {
            lock (_lock) { _conns.Remove(id); _freedAt[id] = Environment.TickCount; }
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
            c?.Shutdown();
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
        const int ConnectTimeoutMs = 8000;
        readonly ConcurrentQueue<Action> _evt = new ConcurrentQueue<Action>();
        readonly object _gate = new object();
        volatile TcpConn _conn;
        volatile bool _disposed;
        TcpClient _pending;              // the socket being connected, guarded by _gate

        public event Action<int> PeerConnected;
        public event Action<int> PeerDisconnected;
        public event Action<int, byte[]> Received;

        public TcpClientTransport(string host, int port)
        {
            // Connect on a worker thread so the game never freezes.
            new Thread(() =>
            {
                TcpClient c = new TcpClient();
                try
                {
                    lock (_gate)
                    {
                        if (_disposed) { c.Close(); return; }
                        _pending = c;
                    }
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) throw new TimeoutException("connect timed out");
                    c.EndConnect(ar);
                    TcpConn conn;
                    lock (_gate)
                    {
                        _pending = null;
                        if (_disposed) { c.Close(); return; }
                        conn = new TcpConn(0, c, _evt,
                            (p, d) => { if (!_disposed) Received?.Invoke(p, d); },
                            id => { if (!_disposed) PeerDisconnected?.Invoke(id); });
                        _conn = conn;
                    }
                    _evt.Enqueue(() => { if (!_disposed) PeerConnected?.Invoke(0); });
                    conn.Start();
                }
                catch
                {
                    try { c.Close(); } catch { }
                    lock (_gate) _pending = null;
                    _evt.Enqueue(() => { if (!_disposed) PeerDisconnected?.Invoke(0); });
                }
            }) { IsBackground = true, Name = "mp-connect" }.Start();
        }

        public void Send(int peer, byte[] data) { _conn?.Send(data); }
        public void Disconnect(int peer) { _conn?.Shutdown(); }

        public void Poll()
        {
            Action a;
            while (_evt.TryDequeue(out a)) a();
        }

        public void Dispose()
        {
            TcpClient p;
            TcpConn c;
            lock (_gate) { _disposed = true; p = _pending; _pending = null; c = _conn; }
            try { p?.Close(); } catch { }   // aborts an in-progress connect
            c?.Close();
        }
    }
}
