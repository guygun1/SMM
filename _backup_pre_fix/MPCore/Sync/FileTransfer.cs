using System;
using System.Collections.Generic;
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
    /// </summary>
    public sealed class FileChannel
    {
        public const int ChunkSize = 48000;
        public int Window = 6 * ChunkSize;

        readonly SyncSession _s;
        readonly string _tag;
        readonly Dictionary<int, Sender> _senders = new Dictionary<int, Sender>();
        Receiver _rx;

        /// <summary>Receiver: opens the stream for one incoming file. Return null to skip it.</summary>
        public Func<string, Stream> CreateFile;
        public event Action<int, long> ReceiveStarted;      // file count, total bytes
        public event Action<long, long> Progress;           // received, total
        public event Action ReceiveCompleted;
        public event Action<string> ReceiveFailed;
        public event Action<int> SendCompleted;             // peer

        public FileChannel(SyncSession s, string tag)
        {
            _s = s; _tag = tag;
            s.RegisterMessage(tag + ".b", OnBegin);
            s.RegisterMessage(tag + ".c", OnChunk);
            s.RegisterMessage(tag + ".e", OnEnd);
            s.RegisterMessage(tag + ".a", OnAck);
        }

        public bool Sending { get { return _senders.Count > 0; } }
        public bool Receiving { get { return _rx != null; } }

        // ------------------------------------------------------------------ send

        public void Send(int to, IList<FileEntry> files)
        {
            _senders[to] = new Sender { To = to, Files = files.ToList() };
        }

        /// <summary>Call every frame. Sends what the window allows.</summary>
        public void Tick(int maxChunksPerTick = 64)
        {
            if (_senders.Count == 0) return;
            foreach (var sd in _senders.Values.ToList())
            {
                if (sd.To != 0 && _s.IsHost && !_s.PeerIds.Contains(sd.To)) { _senders.Remove(sd.To); continue; } // left
                Pump(sd, maxChunksPerTick);
                if (sd.Done) { _senders.Remove(sd.To); SendCompleted?.Invoke(sd.To); }
            }
        }

        sealed class Sender
        {
            public int To;
            public List<FileEntry> Files;
            public int File;
            public int Offset;
            public long Sent, Acked;
            public bool Begun, Done;
        }

        void Pump(Sender sd, int max)
        {
            if (!sd.Begun)
            {
                var w = new ByteWriter();
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
                    _s.SendMessage(_tag + ".e", new ByteWriter(), sd.To);
                    sd.Done = true;
                    return;
                }
                var data = sd.Files[sd.File].Data ?? new byte[0];
                if (sd.Offset >= data.Length) { sd.File++; sd.Offset = 0; continue; }
                int len = Math.Min(ChunkSize, data.Length - sd.Offset);
                var w = new ByteWriter(len + 16);
                w.VarU((ulong)sd.File); w.VarU((ulong)sd.Offset);
                w.VarU((ulong)len + 1); w.Raw(data, sd.Offset, len);   // same layout as ByteWriter.Bytes
                _s.SendMessage(_tag + ".c", w, sd.To);
                sd.Offset += len; sd.Sent += len; n++;
            }
        }

        void OnAck(int from, ByteReader r)
        {
            long got = (long)r.VarU();
            Sender sd;
            if (_senders.TryGetValue(from, out sd) && got > sd.Acked) sd.Acked = got;
        }

        // ------------------------------------------------------------------ receive

        sealed class Receiver
        {
            public int From;
            public string[] Paths;
            public long[] Lens;
            public Stream[] Streams;
            public long Total, Got;
        }

        void OnBegin(int from, ByteReader r)
        {
            Abort(null);
            try
            {
                int n = (int)r.VarU();
                var rx = new Receiver { From = from, Paths = new string[n], Lens = new long[n], Streams = new Stream[n] };
                for (int i = 0; i < n; i++)
                {
                    rx.Paths[i] = r.Str() ?? "";
                    rx.Lens[i] = (long)r.VarU();
                    rx.Total += rx.Lens[i];
                    if (!SafePath(rx.Paths[i])) throw new IOException("unsafe path: " + rx.Paths[i]);
                }
                for (int i = 0; i < n; i++) rx.Streams[i] = CreateFile != null ? CreateFile(rx.Paths[i]) : null;
                _rx = rx;
                ReceiveStarted?.Invoke(n, rx.Total);
            }
            catch (Exception e) { Abort("could not prepare the files: " + e.Message); }
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
            if (rx == null || from != rx.From) return;
            try
            {
                int i = (int)r.VarU(); long off = (long)r.VarU();
                var data = r.Bytes() ?? new byte[0];
                if (i < 0 || i >= rx.Streams.Length) throw new IOException("bad file index " + i);
                if (off + data.Length > rx.Lens[i]) throw new IOException("chunk past the end of " + rx.Paths[i]);
                var s = rx.Streams[i];
                if (s != null) { s.Position = off; s.Write(data, 0, data.Length); }
                rx.Got += data.Length;
                var w = new ByteWriter(); w.VarU((ulong)rx.Got);
                _s.SendMessage(_tag + ".a", w, from);
                Progress?.Invoke(rx.Got, rx.Total);
            }
            catch (Exception e) { Abort("write failed: " + e.Message); }
        }

        void OnEnd(int from, ByteReader r)
        {
            var rx = _rx;
            if (rx == null || from != rx.From) return;
            _rx = null;
            foreach (var s in rx.Streams) if (s != null) { try { s.Dispose(); } catch { } }
            if (rx.Got != rx.Total) { ReceiveFailed?.Invoke("incomplete: got " + rx.Got + " of " + rx.Total + " bytes"); return; }
            ReceiveCompleted?.Invoke();
        }

        public void Abort(string why)
        {
            var rx = _rx;
            _rx = null;
            if (rx != null) foreach (var s in rx.Streams) if (s != null) { try { s.Dispose(); } catch { } }
            if (why != null) ReceiveFailed?.Invoke(why);
        }
    }
}
