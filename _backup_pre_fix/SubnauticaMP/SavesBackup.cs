using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Automatic backups of the host's world (log prefix "saves: "). Saves go to
    /// SNAppData/MultiplayerBackups/&lt;slot&gt;/&lt;yyyyMMdd-HHmmss&gt;/ and only the newest N are kept.
    /// The game saves first (on the main thread, as usual); the folder copy runs on a background thread.
    /// Config: Saves/AutoBackup (true), Saves/BackupIntervalMinutes (10), Saves/BackupsToKeep (10).
    /// </summary>
    public sealed class SavesBackup : ISyncFeature
    {
        public string Name { get { return "saves-backup"; } }

        static ConfigEntry<bool> _auto;
        static ConfigEntry<int> _interval, _keep;
        static bool _bound;
        static readonly object _lock = new object();
        static bool _copying;

        float _next = -1f;
        bool _running;

        static void Bind()
        {
            if (_bound || Plugin.I == null) return;
            _bound = true;
            try
            {
                var c = Plugin.I.Config;
                _auto = c.Bind("Saves", "AutoBackup", true, "While hosting with players connected, copy the world to SNAppData/MultiplayerBackups every few minutes and when a player joins.");
                _interval = c.Bind("Saves", "BackupIntervalMinutes", 10, "Minutes between automatic backups (1 or more).");
                _keep = c.Bind("Saves", "BackupsToKeep", 10, "How many backups to keep per world (the newest). 1 or more.");
            }
            catch (Exception e) { Saves.Warn("config: " + e.Message); }
        }

        internal static bool Enabled { get { Bind(); return _auto == null || _auto.Value; } }
        static int IntervalMin { get { Bind(); return Math.Max(1, _interval != null ? _interval.Value : 10); } }
        static int Keep { get { Bind(); return Math.Max(1, _keep != null ? _keep.Value : 10); } }

        public void Register() { Bind(); }

        public void Tick()
        {
            if (!SyncKit.IsHost || !Enabled) return;
            float now = Time.unscaledTime;
            if (!SyncKit.Active || Player.main == null) { _next = -1f; return; }   // nobody connected: nothing to protect
            if (_next < 0f) { _next = now + IntervalMin * 60f; return; }
            if (now < _next || _running || Saves.SaveInFlight) return;
            _running = true;
            Plugin.I.StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            try
            {
                var slm = SaveLoadManager.main;
                var menu = IngameMenu.main;
                string slot = null;
                try { slot = slm != null ? slm.GetCurrentSlot() : null; } catch { }
                if (slm == null || menu == null || string.IsNullOrEmpty(slot) || slm.isSaving || !CanSaveNow(menu))
                {
                    _next = Time.unscaledTime + 30f;   // not a good moment (cinematic, already saving...): try again soon
                    yield break;
                }
                Saves.Log("periodic backup: saving slot " + slot);
                Saves.SaveInFlight = true;
                var info = new FileInfo(Path.Combine(Path.Combine(Saves.Root(), slot), "gameinfo.json"));
                DateTime before = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                bool called = false;
                try { menu.SaveGame(); called = true; } catch (Exception e) { Saves.Warn("SaveGame: " + e.Message); }
                float t0 = Time.unscaledTime; bool sawSaving = false;
                while (called && Time.unscaledTime - t0 < 60f)
                {
                    yield return null;
                    if (slm == null) break;
                    if (slm.isSaving) sawSaving = true;
                    info.Refresh();
                    if (!slm.isSaving && info.Exists && info.LastWriteTimeUtc > before) break;
                    if (!sawSaving && Time.unscaledTime - t0 > 6f) break;
                }
                Saves.SaveInFlight = false;
                info.Refresh();
                if (info.Exists && info.LastWriteTimeUtc > before) BackupAsync(slot, Path.Combine(Saves.Root(), slot), "timer");
                else Saves.Warn("periodic backup skipped: the game did not finish a save");
            }
            finally
            {
                Saves.SaveInFlight = false;
                _running = false;
                if (_next < Time.unscaledTime) _next = Time.unscaledTime + IntervalMin * 60f;
            }
        }

        static MethodInfo _allow;
        static bool CanSaveNow(IngameMenu menu)
        {
            try
            {
                if (_allow == null) _allow = AccessTools.Method(typeof(IngameMenu), "GetAllowSaving");
                return _allow == null || (bool)_allow.Invoke(menu, null);
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ copy (background thread)

        internal static string BackupRoot()
        {
            return Path.Combine(Path.GetDirectoryName(Saves.Root()), "MultiplayerBackups");
        }

        /// <summary>Copy a finished save folder to the backup folder on a thread-pool thread. Safe to call from the main thread.</summary>
        internal static void BackupAsync(string slot, string srcDir, string reason)
        {
            if (!Enabled) return;
            if (string.IsNullOrEmpty(slot) || !Directory.Exists(srcDir)) { Saves.Warn("backup (" + reason + ") skipped: no folder for " + slot); return; }
            int keep = Keep; string root = BackupRoot();
            lock (_lock) { if (_copying) { Saves.Log("backup (" + reason + ") skipped: another backup is still copying"); return; } _copying = true; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { DoBackup(slot, srcDir, root, keep, reason); }
                catch (Exception e) { Saves.Warn("backup (" + reason + ") failed: " + e.Message); }
                finally { lock (_lock) _copying = false; }
            });
        }

        static string Signature(string dir)
        {
            long h = 0;
            foreach (var f in Directory.GetFiles(dir))
            {
                var fi = new FileInfo(f);
                h = h * 31 + fi.LastWriteTimeUtc.Ticks + fi.Length;
            }
            return h.ToString();
        }

        static void DoBackup(string slot, string src, string root, int keep, string reason)
        {
            var slotRoot = Path.Combine(root, slot);
            Directory.CreateDirectory(slotRoot);
            foreach (var old in Directory.GetDirectories(slotRoot, "*.tmp"))   // half-written leftovers of a crash
                try { Directory.Delete(old, true); } catch { }
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dest = Path.Combine(slotRoot, stamp);
            for (int n = 2; Directory.Exists(dest); n++) dest = Path.Combine(slotRoot, stamp + "-" + n);
            string tmp = dest + ".tmp";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = false; long bytes = 0;
            for (int attempt = 0; attempt < 3 && !ok; attempt++)
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                string sig = Signature(src);
                bytes = CopyDir(src, tmp);
                if (Signature(src) == sig) ok = true;
                else { Saves.Log("the world was saved while it was being copied, copying again"); Thread.Sleep(1000); }
            }
            if (!ok) { try { Directory.Delete(tmp, true); } catch { } Saves.Warn("backup (" + reason + ") dropped: the world kept changing"); return; }
            Directory.Move(tmp, dest);
            Saves.Log("backup (" + reason + ") " + slot + " -> " + dest + " (" + (bytes / 1048576.0).ToString("0.0") + " MB, " + sw.ElapsedMilliseconds + " ms)");
            var all = Directory.GetDirectories(slotRoot).Where(d => !d.EndsWith(".tmp")).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
            for (int i = 0; i < all.Count - keep; i++)
            {
                try { Directory.Delete(all[i], true); Saves.Log("removed old backup " + Path.GetFileName(all[i])); }
                catch (Exception e) { Saves.Warn("could not remove old backup " + all[i] + ": " + e.Message); }
            }
        }

        static long CopyDir(string src, string dst)
        {
            long total = 0;
            Directory.CreateDirectory(dst);
            var buf = new byte[256 * 1024];
            foreach (var f in Directory.GetFiles(src))
            {
                if (Path.GetFileName(f) == Saves.Marker) continue;
                try
                {
                    using (var i = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var o = new FileStream(Path.Combine(dst, Path.GetFileName(f)), FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        int n;
                        while ((n = i.Read(buf, 0, buf.Length)) > 0) { o.Write(buf, 0, n); total += n; }
                    }
                }
                catch (Exception e) { Saves.Warn("backup: skipped " + f + ": " + e.Message); }
            }
            foreach (var d in Directory.GetDirectories(src)) total += CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            return total;
        }
    }
}
