using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MPCore.IO;

namespace MPCore.Sync
{
    public sealed class FileEntry
    {
        public string Path;   // relative, forward slashes
        public byte[] Data;
    }

    /// <summary>
    /// Sends a set of files from one peer to another over a session's custom messages, with flow control:
    /// at most Window bytes are in flight until the receiver acknowledges them. That keeps Steam's send buffer from
    /// overflowing (it drops messages when full) and stops a large transfer from blocking a TCP socket.
    /// Messages: tag.b (begin), tag.c (chunk), tag.e (end), tag.a (ack). Engine independent, so it is unit tested.
    /// Every message carries the transfer id, so stale messages from an earlier transfer are ignored.
    /// </summary>
    public sealed class FileChannel
    {
        public const int ChunkSize = 48000;
        public int Window = 6 * ChunkSize;
        /// <summary>Receiver: a transfer with no chunk for this long is aborted. Sender: a full window with no ack for this long drops the send.</summary>
        public int IdleTimeoutMs = 30000;

        readonly SyncSession _s;
        readonly string _tag;
        readonly Dictionary<int, Sender> _senders = new Dictionary<int, Sender>();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        ulong _nextId;
        Receiver _rx;

        /// <summary>Receiver: opens the stream for one incoming file. Return null to skip it.</summary>
        public Func<string, Stream> CreateFile;
        public event Action<int, long> ReceiveStarted;      // file count, total bytes
        public event Action<long, long> Progress;           // received, total
        public event Action ReceiveCompleted;
        public event Action<string> ReceiveFailed;
        public event Action<int> SendCompleted;             // peer
        /// <summary>Sender: a send was dropped (peer left, connection lost, or no acknowledgement for IdleTimeoutMs). Peer, reason.</summary>
        public event Action<int, string> SendFailed;

        public FileChannel(SyncSession s, string tag)
        {
            _s = s; _tag = tag;
            s.RegisterMessage(tag + ".b", OnBegin);
            s.RegisterMessage(tag + ".c", OnChunk);
            s.RegisterMessage(tag + ".e", OnEnd);
            s.RegisterMessage(tag + ".a", OnAck);
            s.PeerLeft += OnPeerLeft;
            s.Disconnected += Reset;
        }

        public bool Sending { get { return _senders.Count > 0; } }
        public bool Receiving { get { return _rx != null; } }

        // ------------------------------------------------------------------ send

        public void Send(int to, IList<FileEntry> files)
        {
            _senders[to] = new Sender { To = to, Id = ++_nextId, Files = files.ToList(), Acked = 0, LastAckMs = _clock.ElapsedMilliseconds };
        }

        /// <summary>Call every frame. Sends what the window allows.</summary>
        public void Tick(int maxChunksPerTick = 64)
        {
            long now = _clock.ElapsedMilliseconds;
            var rx = _rx;
            if (rx != null && now - rx.LastMs > IdleTimeoutMs) Abort("no data for " + (IdleTimeoutMs / 1000) + " seconds");
            if (_senders.Count == 0) return;
            foreach (var sd in _senders.Values.ToList())
            {
                Sender cur;
                if (!_senders.TryGetValue(sd.To, out cur) || !ReferenceEquals(cur, sd)) continue;   // replaced or dropped meanwhile
                if (sd.To != 0 && _s.IsHost && !_s.PeerIds.Contains(sd.To)) { DropSender(sd.To, "the peer left"); continue; } // left
                if (!sd.Done && sd.Sent - sd.Acked >= Window && now - sd.LastAckMs > IdleTimeoutMs)
                {
                    DropSender(sd.To, "no acknowledgement for " + (IdleTimeoutMs / 1000) + " seconds");
                    continue;
                }
                Pump(sd, maxChunksPerTick);
                if (sd.Done) { _senders.Remove(sd.To); SendCompleted?.Invoke(sd.To); }
            }
        }

        sealed class Sender
        {
            public int To;
            public ulong Id;
            public List<FileEntry> Files;
            public int File;
            public int Offset;
            public long Sent, Acked;
            public long LastAckMs;
            public bool Begun, Done;
        }

        void DropSender(int to, string why)
        {
            if (!_senders.Remove(to)) return;
            if (why != null) SendFailed?.Invoke(to, why);
        }

        void Pump(Sender sd, int max)
        {
            if (!sd.Begun)
            {
                var w = new ByteWriter();
                w.VarU(sd.Id);
                w.VarU((ulong)sd.Files.Count);
                foreach (var f in sd.Files) { w.Str(f.Path); w.VarU((ulong)(f.Data?.Length ?? 0)); }
                _s.SendMessage(_tag + ".b", w, sd.To);
                sd.Begun = true;
            }
            int n = 0;
            while (n < max && sd.Sent - sd.Acked < Window)
            {
                if (sd.File >= sd.Files.Count)
                {
                    var e = new ByteWriter(); e.VarU(sd.Id);
                    _s.SendMessage(_tag + ".e", e, sd.To);
                    sd.Done = true;
                    return;
                }
                var data = sd.Files[sd.File].Data ?? new byte[0];
                if (sd.Offset >= data.Length) { sd.File++; sd.Offset = 0; continue; }
                int len = Math.Min(ChunkSize, data.Length - sd.Offset);
                var w = new ByteWriter(len + 16);
                w.VarU(sd.Id); w.VarU((ulong)sd.File); w.VarU((ulong)sd.Offset);
                w.VarU((ulong)len + 1); w.Raw(data, sd.Offset, len);   // same layout as ByteWriter.Bytes
                _s.SendMessage(_tag + ".c", w, sd.To);
                sd.Offset += len; sd.Sent += len; n++;
            }
        }

        void OnAck(int from, ByteReader r)
        {
            ulong id = r.VarU();
            long got = (long)r.VarU();
            Sender sd;
            if (!_senders.TryGetValue(from, out sd) || sd.Id != id) return;   // ack of an earlier transfer
            if (got <= sd.Acked || got > sd.Sent) return;
            sd.Acked = got;
            sd.LastAckMs = _clock.ElapsedMilliseconds;
        }

        // ------------------------------------------------------------------ receive

        sealed class Receiver
        {
            public int From;
            public ulong Id;
            public string[] Paths;
            public long[] Lens;
            public Stream[] Streams;
            public long Total, Got;
            public long LastMs;
        }

        /// <summary>Files only come from the host (peer 0) to a client. A host never receives files.</summary>
        bool AcceptsFrom(int from) { return !_s.IsHost && from == 0; }

        void OnBegin(int from, ByteReader r)
        {
            if (!AcceptsFrom(from)) return;
            Receiver rx = null;
            try
            {
                ulong id = r.VarU();
                ulong count = r.VarU();
                if (count > (ulong)r.Remaining) throw new FormatException("file count " + count + " exceeds the bytes left"); // each entry takes 2+ bytes
                int n = (int)count;
                rx = new Receiver { From = from, Id = id, Paths = new string[n], Lens = new long[n], Streams = new Stream[n], LastMs = _clock.ElapsedMilliseconds };
                for (int i = 0; i < n; i++)
                {
                    rx.Paths[i] = r.Str() ?? "";
                    rx.Lens[i] = (long)r.VarU();
                    rx.Total += rx.Lens[i];
                    if (!SafePath(rx.Paths[i])) throw new IOException("unsafe path: " + rx.Paths[i]);
                }
                Abort(null);   // a new transfer replaces an unfinished one
                for (int i = 0; i < n; i++) rx.Streams[i] = CreateFile != null ? CreateFile(rx.Paths[i]) : null;
                _rx = rx;
                ReceiveStarted?.Invoke(n, rx.Total);
            }
            catch (Exception e)
            {
                if (rx != null) DisposeStreams(rx);   // files opened before the failing one
                Abort("could not prepare the files: " + e.Message);
            }
        }

        public static bool SafePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (p.StartsWith("/") || p.StartsWith("\\") || p.Contains(":")) return false;
            foreach (var part in p.Split('/', '\\')) if (part == "..") return false;
            return true;
        }

        void OnChunk(int from, ByteReader r)
        {
            var rx = _rx;
            if (rx == null) return;
            try
            {
                ulong id = r.VarU();
                if (from != rx.From || id != rx.Id) return;   // another sender, or an earlier transfer
                int i = (int)r.VarU(); long off = (long)r.VarU();
                var data = r.Bytes() ?? new byte[0];
                if (i < 0 || i >= rx.Streams.Length) throw new IOException("bad file index " + i);
                if (off < 0 || off + data.Length > rx.Lens[i]) throw new IOException("chunk past the end of " + rx.Paths[i]);
                var s = rx.Streams[i];
                if (s != null) { s.Position = off; s.Write(data, 0, data.Length); }
                rx.Got += data.Length;
                rx.LastMs = _clock.ElapsedMilliseconds;
                var w = new ByteWriter(); w.VarU(rx.Id); w.VarU((ulong)rx.Got);
                _s.SendMessage(_tag + ".a", w, from);
                Progress?.Invoke(rx.Got, rx.Total);
            }
            catch (Exception e) { Abort("write failed: " + e.Message); }
        }

        void OnEnd(int from, ByteReader r)
        {
            var rx = _rx;
            if (rx == null || from != rx.From) return;
            ulong id = r.VarU();
            if (id != rx.Id) return;
            _rx = null;
            DisposeStreams(rx);
            if (rx.Got != rx.Total) { ReceiveFailed?.Invoke("incomplete: got " + rx.Got + " of " + rx.Total + " bytes"); return; }
            ReceiveCompleted?.Invoke();
        }

        static void DisposeStreams(Receiver rx)
        {
            foreach (var s in rx.Streams) if (s != null) { try { s.Dispose(); } catch { } }
        }

        public void Abort(string why)
        {
            var rx = _rx;
            _rx = null;
            if (rx != null) DisposeStreams(rx);
            if (why != null) ReceiveFailed?.Invoke(why);
        }

        /// <summary>Call when a peer left the session: drops a send to it and aborts a receive from it.</summary>
        public void OnPeerLeft(int peer)
        {
            DropSender(peer, "the peer left");
            var rx = _rx;
            if (rx != null && rx.From == peer) Abort("the sender left");
        }

        /// <summary>Call when the connection to the host is lost: drops every send and aborts a receive.</summary>
        public void Reset()
        {
            foreach (var peer in _senders.Keys.ToList()) DropSender(peer, "disconnected");
            if (_rx != null) Abort("disconnected");
        }
    }
}
