using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Gives a joining player the host's world. The host saves, then sends its save slot to the joiner
    /// (MPCore.Sync.FileChannel: chunked, flow controlled, unit tested). The joiner stores it as a new save slot
    /// (its own saves are never touched) and loads it. Both players then start in the same world.
    /// Every step is logged with the prefix "world transfer:".
    /// </summary>
    sealed class WorldTransfer
    {
        public static WorldTransfer I;
        public bool Loaded;   // this machine loaded the host's world

        readonly Plugin _p;
        readonly SyncSession _s;
        readonly FileChannel _ch;
        // host state: one joiner at a time (being prepared or sent to), the others wait in FIFO order
        int _busy = -1;
        readonly List<int> _waiting = new List<int>();

        // joiner state
        string _slot, _dir;
        bool _requesting, _gotAnswer;
        float _reqStart, _lastReq;
        int _lastPct = -1;

        public static void Attach(Plugin p, SyncSession s) { I = new WorldTransfer(p, s); }
        public static void Detach() { I = null; }

        WorldTransfer(Plugin p, SyncSession s)
        {
            _p = p; _s = s;
            s.RegisterMessage("wf.req", OnRequest);
            s.RegisterMessage("wf.no", OnNo);
            s.RegisterMessage("wf.wait", OnWait);
            _ch = new FileChannel(s, "wf");
            _ch.CreateFile = CreateFile;
            _ch.ReceiveStarted += (n, total) =>
            {
                _gotAnswer = true; _requesting = false;
                Log("receiving " + n + " files, " + Mb(total) + " into " + _slot);
                _p.JoinStatus = "Downloading the world... 0%";
            };
            _ch.Progress += (got, total) =>
            {
                int pct = total > 0 ? (int)(100 * got / total) : 100;
                if (pct != _lastPct) { _lastPct = pct; _p.JoinStatus = "Downloading the world... " + pct + "% (" + Mb(got) + " of " + Mb(total) + ")"; }
            };
            _ch.ReceiveCompleted += () => { Log("download complete"); Saves.Downloading = null; _p.StartCoroutine(LoadRoutine(_slot)); };
            _ch.ReceiveFailed += why => Fail("download failed: " + why);
            _ch.SendCompleted += peer => { Log("sent the world to peer " + peer); if (peer == _busy) Next(); };
            _ch.SendFailed += (peer, why) => { Log("could not send the world to peer " + peer + ": " + why); if (peer == _busy) Next(); };
        }

        void Log(string m) { _p.Log.Info("world transfer: " + m); }
        static string Mb(long b) { return (b / 1048576.0).ToString("0.0") + " MB"; }

        /// <summary>Every frame: sends pending chunks, retries the request.</summary>
        public void Tick()
        {
            _ch.Tick();
            if (_requesting && !_gotAnswer)
            {
                float now = Time.unscaledTime;
                if (now - _reqStart > 30f) { _requesting = false; Fail("the host did not answer within 30 seconds (same plugin version on both? host in a world?)"); }
                else if (now - _lastReq > 4f) { _lastReq = now; SendRequest(); Log("asking again"); }
            }
        }

        static string Root() { return Saves.Root(); }

        static bool Skip(string rel)
        {
            var r = rel.ToLowerInvariant();
            return r == Saves.Marker || r.StartsWith("screenshots/") || r.StartsWith("timecapsules/");
        }

        // ------------------------------------------------------------------ joiner

        public void Request()
        {
            _slot = null; _dir = null; _lastPct = -1;
            _gotAnswer = false; _requesting = true;
            _reqStart = _lastReq = Time.unscaledTime;
            _p.JoinStatus = "Asking the host for its world...";
            Log("asking the host for its world (save folder here: " + Root() + ")");
            SendRequest();
        }

        void SendRequest() { _s.SendMessage("wf.req", new ByteWriter(), 0); }

        void OnNo(int from, ByteReader r)
        {
            if (from != 0) return;   // only the host answers a request
            _gotAnswer = true; _requesting = false;
            Fail(r.Str() ?? "the host cannot share its world");
        }

        /// <summary>The host is busy (another joiner, or saving): the request is queued, so keep waiting.</summary>
        void OnWait(int from, ByteReader r)
        {
            if (from != 0 || !_requesting || _gotAnswer) return;
            float now = Time.unscaledTime;
            _reqStart = now; _lastReq = now;
            _p.JoinStatus = "The host is busy, still waiting...";
        }

        void Fail(string why)
        {
            _p.Log.Warn("world transfer: " + why);
            _requesting = false;
            // never leave a half-downloaded or unloadable host world behind
            var bad = _slot; _slot = null; _dir = null; Saves.Downloading = null;
            if (bad != null) Saves.DeleteSlot(bad, "transfer failed");
            _p.OnWorldTransferFailed(why);
        }

        Stream CreateFile(string rel)
        {
            if (_dir == null)
            {
                _slot = ChooseSlot();
                Log("chose slot " + _slot + " in " + Root());
                if (_slot == null) throw new IOException("no free save slot (delete an old save and try again)");
                _dir = Path.Combine(Root(), _slot);
                Directory.CreateDirectory(_dir);
                Saves.Downloading = _slot;
                Saves.WriteMarker(_dir);   // temporary world: hidden from the world list, deleted when we leave
            }
            var full = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            return new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        static string ChooseSlot()
        {
            var slm = SaveLoadManager.main;
            var root = Root();
            var active = new HashSet<string>(slm != null ? (slm.GetActiveSlotNames() ?? new string[0]) : new string[0]);
            var possible = slm != null ? (slm.GetPossibleSlotNames() ?? new string[0]) : new string[0];
            foreach (var name in possible)
                if (!active.Contains(name) && !Directory.Exists(Path.Combine(root, name))) return name;
            // every slot the game offers is in use: take the next number
            for (int i = 0; i < 10000; i++)
            {
                var name = "slot" + i.ToString("0000");
                if (!active.Contains(name) && !Directory.Exists(Path.Combine(root, name))) return name;
            }
            return null;
        }

        IEnumerator LoadRoutine(string slot)
        {
            _p.JoinStatus = "Loading the host's world...";
            var slm = SaveLoadManager.main;
            if (slm == null) { Fail("the game's save manager is not available"); yield break; }
            Log("refreshing the save list");
            Saves.RevealSlot = slot;   // the game's own slot calls may see this one until we are back at the menu
            yield return slm.LoadSlotsAsync();
            SaveLoadManager.GameInfo info = null;
            try { info = slm.GetGameInfo(slot); } catch (Exception e) { Log("GetGameInfo: " + e.Message); }
            if (info == null) { Saves.RevealSlot = null; Fail("the game could not read the downloaded save " + slot + " (slots it knows: " + string.Join(",", slm.GetActiveSlotNames() ?? new string[0]) + ")"); yield break; }
            Loaded = true;
            Log("loading " + slot + " (session " + info.session + ", changeset " + info.changeSet + ", mode " + info.gameMode + ")");
            if (uGUI_MainMenu.main == null) { Saves.RevealSlot = null; Fail("not on the main menu any more"); yield break; }
            yield return uGUI_MainMenu.main.LoadGameAsync(slot, info.session, info.changeSet, info.gameMode);
        }

        // ------------------------------------------------------------------ host

        void OnRequest(int from, ByteReader r)
        {
            if (!_s.IsHost) return;
            if (from == _busy || _waiting.Contains(from)) { Wait(from); return; }   // repeated request: already on it or queued
            if (_busy >= 0)
            {
                _waiting.Add(from);
                Log("peer " + from + " waits for the world (peer " + _busy + " is being served first)");
                Wait(from);
                return;
            }
            Start(from);
        }

        void Start(int to)
        {
            _busy = to;
            Log("peer " + to + " asks for the world");
            _p.StartCoroutine(PrepareAndSend(to));
        }

        /// <summary>Host: serves the next waiting joiner that is still in the session.</summary>
        void Next()
        {
            _busy = -1;
            while (_waiting.Count > 0)
            {
                int next = _waiting[0];
                _waiting.RemoveAt(0);
                if (!_s.PeerIds.Contains(next)) continue;   // left while waiting
                Start(next);
                return;
            }
        }

        /// <summary>Host: tells a joiner the request is queued or being prepared, so its answer timer does not run out.</summary>
        void Wait(int to)
        {
            _s.SendMessage("wf.wait", new ByteWriter(), to);
        }

        void No(int to, string why)
        {
            Log("cannot send the world to peer " + to + ": " + why);
            var w = new ByteWriter(); w.Str(why);
            _s.SendMessage("wf.no", w, to);
        }

        IEnumerator PrepareAndSend(int to)
        {
            bool handedOff = false;   // true once FileChannel owns the send; until then this coroutine releases the host
            try
            {
                var slm = SaveLoadManager.main;
                if (Player.main == null || slm == null) { No(to, "The host is not in a world right now."); yield break; }
                string slot = null;
                try { slot = slm.GetCurrentSlot(); } catch (Exception e) { Log("GetCurrentSlot: " + e.Message); }
                string dir = string.IsNullOrEmpty(slot) ? null : Path.Combine(Root(), slot);
                Log("current slot " + slot + " at " + dir + " (exists=" + (dir != null && Directory.Exists(dir)) + ")");
                if (dir == null) { No(to, "The host has no current save slot."); yield break; }
                try { ErrorMessage.AddMessage("Saving the world for a joining player..."); } catch { }

                // save first, so the joiner gets the current state. A world that was never saved has no folder until now.
                var info = new FileInfo(Path.Combine(dir, "gameinfo.json"));
                DateTime before = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                bool called = false;
                float lastWait = Time.unscaledTime;
                for (float w0 = Time.unscaledTime; (Saves.SaveInFlight || slm.isSaving) && Time.unscaledTime - w0 < 60f;)
                {
                    if (Time.unscaledTime - lastWait > 5f) { lastWait = Time.unscaledTime; Wait(to); }
                    yield return null;   // a backup save is running
                }
                if (slm.isSaving) Log("another save is still running after 60 s: not starting a second one (they share one temp folder), sending the last save");
                else
                {
                    Saves.SaveInFlight = true;
                    Saves.ModSaving = true;
                    try { IngameMenu.main.SaveGame(); called = true; } catch (Exception e) { Log("SaveGame failed: " + e.Message); }
                    finally { Saves.ModSaving = false; }
                }
                float t0 = Time.unscaledTime; bool sawSaving = false;
                while (called && Time.unscaledTime - t0 < 60f)
                {
                    yield return null;
                    if (Time.unscaledTime - lastWait > 5f) { lastWait = Time.unscaledTime; Wait(to); }
                    if (slm.isSaving) sawSaving = true;
                    info.Refresh();
                    if (!slm.isSaving && info.Exists && info.LastWriteTimeUtc > before) break;
                    if (!sawSaving && Time.unscaledTime - t0 > 6f) break;   // the game refused to save now: send the last save
                }
                Saves.SaveInFlight = false;
                info.Refresh();
                Log("save step done (saving seen=" + sawSaving + ", gameinfo updated=" + (info.Exists && info.LastWriteTimeUtc > before) + ")");
                if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, "gameinfo.json")))
                {
                    string tmp = null;
                    try { tmp = SaveLoadManager.GetTemporarySavePath(); } catch { }
                    Log("slot folder missing after save, temporary save is " + tmp + " (exists=" + (tmp != null && Directory.Exists(tmp)) + ")");
                    if (tmp != null && Directory.Exists(tmp) && File.Exists(Path.Combine(tmp, "gameinfo.json"))) dir = tmp;
                    else { No(to, "The host could not save its world (save it once from the pause menu, then try again)."); yield break; }
                }

                try { SavesBackup.BackupAsync(slot, dir, "a player joined"); } catch (Exception e) { Log("backup: " + e.Message); }
                var files = new List<FileEntry>();
                long total = 0;
                foreach (var full in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = full.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (Skip(rel)) continue;
                    byte[] data = null;
                    try
                    {
                        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            data = new byte[fs.Length];
                            int off = 0;
                            while (off < data.Length) { int n = fs.Read(data, off, data.Length - off); if (n <= 0) break; off += n; }
                            if (off != data.Length) Array.Resize(ref data, off);
                        }
                    }
                    catch (Exception e) { Log("skip " + rel + ": " + e.Message); continue; }
                    files.Add(new FileEntry { Path = rel, Data = data });
                    total += data.Length;
                }
                if (!_s.PeerIds.Contains(to)) { Log("peer " + to + " left before the world was ready"); yield break; }
                Log("sending " + files.Count + " files, " + Mb(total) + " to peer " + to);
                try { ErrorMessage.AddMessage("Sending the world (" + Mb(total) + ") to a joining player"); } catch { }
                _ch.Send(to, files);
                handedOff = true;
            }
            finally
            {
                if (!handedOff) Next();   // any exit before the send: serve the next joiner
            }
        }
    }
}
