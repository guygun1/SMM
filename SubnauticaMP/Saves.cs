using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace SubnauticaMP
{
    /// <summary>
    /// Save-slot rules for multiplayer (log prefix "saves: "):
    ///  - the world a joiner downloads lives in a slot folder that holds a marker file (mp_joined.txt). It is temporary;
    ///  - marked slots are hidden from every slot list the game builds (Play panel, Continue), except for our own load;
    ///  - marked slots are deleted when the player leaves the world (main menu appears), when the game closes, and on game start;
    ///  - a joiner cannot save: pause menu Save and the save inside quit are replaced with "Only the host saves this world".
    /// </summary>
    static class Saves
    {
        public const string Marker = "mp_joined.txt";

        /// <summary>The slot being downloaded right now (never cleaned up while this is set).</summary>
        internal static string Downloading;
        /// <summary>The slot WorldTransfer is loading: visible to the game's own slot calls until we are back at the menu.</summary>
        internal static string RevealSlot;
        /// <summary>A save started by us (join or backup) is running.</summary>
        internal static bool SaveInFlight;
        /// <summary>True only while this mod itself calls IngameMenu.SaveGame (backup, world for a joiner). Any other save that starts while
        /// SaveInFlight is set is the player's own and is held back: two saves at once delete each other's temporary folder.</summary>
        internal static bool ModSaving;

        static bool _hooked;
        static float _lastMsg = -10f;

        internal static void Log(string m) { try { if (Plugin.I != null) Plugin.I.Log.Info("saves: " + m); } catch { } }
        internal static void Warn(string m) { try { if (Plugin.I != null) Plugin.I.Log.Warn("saves: " + m); } catch { } }

        internal static string Root()
        {
            var gameDir = Path.GetDirectoryName(Application.dataPath);
            var c = Path.Combine(Path.Combine(gameDir, "SNAppData"), "SavedGames");
            if (Directory.Exists(c)) return c;
            var alt = Path.Combine(Application.persistentDataPath, "SavedGames");
            return Directory.Exists(alt) ? alt : c;
        }

        internal static bool IsMarked(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            try { return File.Exists(Path.Combine(Path.Combine(Root(), slot), Marker)); } catch { return false; }
        }

        internal static void WriteMarker(string dir)
        {
            try { File.WriteAllText(Path.Combine(dir, Marker), "Temporary copy of a multiplayer host's world. Deleted when you leave. " + DateTime.Now.ToString("s") + "\r\npid=" + OwnPid()); }
            catch (Exception e) { Warn("could not write the marker in " + dir + ": " + e.Message); }
        }

        static string CurrentSlot()
        {
            try { var m = SaveLoadManager.main; return m != null ? m.GetCurrentSlot() : null; } catch { return null; }
        }

        /// <summary>The save slot the game is in right now (null at the menu). Used to keep per-world files on the host.</summary>
        internal static string CurrentSlotName() { return CurrentSlot(); }

        static bool InWorld { get { return Player.main != null; } }

        /// <summary>The slot we are playing in right now is a downloaded host world.</summary>
        internal static bool InJoinedWorld
        {
            get { return InWorld && IsMarked(CurrentSlot()); }
        }

        /// <summary>Should this slot be invisible in the game's lists?</summary>
        internal static bool IsHidden(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return false;
            if (slot == RevealSlot) return false;
            if (InWorld && slot == CurrentSlot()) return false;   // the game may ask about the world it is running
            return IsMarked(slot);
        }

        static int OwnPid() { try { return System.Diagnostics.Process.GetCurrentProcess().Id; } catch { return 0; } }

        /// <summary>
        /// Two copies of the game on one PC share one SavedGames folder. A marked slot belongs to the game that made it: another copy that is
        /// still running must not delete it when it goes back to the menu or quits (that deleted the world the other copy was playing in).
        /// A marker with no pid (an old one) or whose game is gone belongs to nobody and may be deleted.
        /// </summary>
        static bool OwnedByOtherLiveGame(string slotDir)
        {
            try
            {
                var f = Path.Combine(slotDir, Marker);
                if (!File.Exists(f)) return false;
                foreach (var line in File.ReadAllLines(f))
                {
                    if (!line.StartsWith("pid=", StringComparison.Ordinal)) continue;
                    int pid;
                    if (!int.TryParse(line.Substring(4).Trim(), out pid) || pid <= 0 || pid == OwnPid()) return false;
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
                        if (p.HasExited) return false;
                        return string.Equals(p.ProcessName, System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }     // no such process
                }
            }
            catch { }
            return false;
        }

        internal static void DeleteSlot(string slot, string why)
        {
            if (string.IsNullOrEmpty(slot)) return;
            var dir = Path.Combine(Root(), slot);
            if (!Directory.Exists(dir)) return;
            if (!File.Exists(Path.Combine(dir, Marker))) { Warn("refusing to delete " + slot + ": no marker"); return; }
            if (OwnedByOtherLiveGame(dir)) { Log("kept joined world " + slot + ": another running game owns it (" + why + ")"); return; }
            // The marker goes last. A delete that stopped half way (a file still open in the other copy of the game, or the game being killed from the
            // task manager) used to remove the marker first and leave the world files behind, and an unmarked slot shows up in the world list
            // as a duplicate of the host's world. With the marker kept until the rest is gone, the slot stays hidden and is retried at the next menu.
            for (int i = 0; i < 3; i++)
            {
                bool rest = true;
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        if (string.Equals(Path.GetFileName(f), Marker, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase)) continue;
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { rest = false; }
                    }
                }
                catch { rest = false; }
                if (rest)
                {
                    try { Directory.Delete(dir, true); Log("deleted joined world " + slot + " (" + why + ")"); return; }
                    catch { }
                }
                if (i == 2) Warn("could not delete all of " + dir + " (it stays hidden and is retried at the next menu)");
                else Thread.Sleep(150);
            }
        }

        /// <summary>Delete every marked slot. onQuit: the game is closing, delete even the world we are in.</summary>
        internal static void Cleanup(string why, bool onQuit)
        {
            try
            {
                var root = Root();
                if (!Directory.Exists(root)) return;
                string cur = !onQuit && InWorld ? CurrentSlot() : null;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(d);
                    if (!File.Exists(Path.Combine(d, Marker))) continue;
                    if (!onQuit && (name == Downloading || name == cur)) continue;
                    DeleteSlot(name, why);
                }
            }
            catch (Exception e) { Warn("cleanup (" + why + "): " + e.Message); }
        }

        internal static void EnsureHooks()
        {
            if (_hooked) return;
            _hooked = true;
            try { Application.quitting += () => Cleanup("game closing", true); } catch (Exception e) { Warn("Application.quitting: " + e.Message); }
            try { AppDomain.CurrentDomain.ProcessExit += (s, a) => Cleanup("process exit", true); } catch { }
        }

        internal static IEnumerator Empty() { yield break; }

        internal static void TellHostSaves()
        {
            if (Time.unscaledTime - _lastMsg < 1.5f) return;
            _lastMsg = Time.unscaledTime;
            try { ErrorMessage.AddMessage("Only the host saves this world"); } catch { }
        }
    }

    // ---------------------------------------------------------------- hide marked slots from the game's lists

    // The Play panel (MainMenuLoadPanel.PopulateSaveList / UpdateLoadButtonState), Continue (uGUI_MainMenu.HasSavedGames,
    // LoadMostRecentSavedGame) and the load buttons all ask SaveLoadManager.GetGameInfo / GetActiveSlotNames.
    [HarmonyPatch(typeof(SaveLoadManager), "GetGameInfo")]
    static class SavesHideInfo
    {
        static void Postfix(string slotName, ref SaveLoadManager.GameInfo __result)
        {
            try { if (__result != null && Saves.IsHidden(slotName)) __result = null; } catch { }
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), "GetActiveSlotNames")]
    static class SavesHideNames
    {
        static void Postfix(ref string[] __result)
        {
            try
            {
                if (__result == null) return;
                var keep = new List<string>(__result.Length);
                foreach (var n in __result) if (!Saves.IsHidden(n)) keep.Add(n);
                if (keep.Count != __result.Length) __result = keep.ToArray();
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- lifecycle: main menu appears

    // The main menu scene starts when the game starts and again whenever a world is left: delete leftovers.
    [HarmonyPatch(typeof(uGUI_MainMenu), "Start")]
    static class SavesMenuStart
    {
        static void Postfix()
        {
            try
            {
                Saves.EnsureHooks();
                Saves.RevealSlot = null;
                Saves.SaveInFlight = false;
                Saves.Cleanup("main menu", false);
            }
            catch (Exception e) { Saves.Warn("menu start: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- the joiner cannot save

    // Every save (pause menu Save button, the save inside QuitGameAsync for permadeath, IngameMenu.SaveGame) goes through
    // this private coroutine. Replaced by an empty one, so a caller that does "yield return SaveGameAsync()" just continues.
    [HarmonyPatch(typeof(IngameMenu), "SaveGameAsync")]
    static class SavesBlockJoinerSave
    {
        static bool Prefix(ref IEnumerator __result)
        {
            try
            {
                // Any save already running counts, not only the mod's: the game keeps one temporary folder (TempSave\tmpNNNNNN) for all
                // saves, and a second save moves or deletes it under the first ("Could not find a part of the path ...\gameinfo.json").
                bool gameSaving = false;
                try { var slm0 = SaveLoadManager.main; gameSaving = slm0 != null && slm0.isSaving; } catch { }
                if ((Saves.SaveInFlight || gameSaving) && !Saves.ModSaving && !Saves.InJoinedWorld)
                {
                    // the mod is saving for a backup or a joining player: a second save now would delete the first one's temporary folder
                    Saves.Log("held back a save that started while the mod's own save was running");
                    try { ErrorMessage.AddMessage("A background save is running. Try again in a few seconds."); } catch { }
                    __result = Saves.Empty();
                    return false;
                }
                if (Saves.InJoinedWorld)
                {
                    Saves.Log("blocked a save in the joined world");
                    Saves.TellHostSaves();
                    __result = Saves.Empty();
                    return false;
                }
            }
            catch (Exception e) { Saves.Warn("save guard: " + e.Message); }
            return true;
        }
    }

    /// <summary>
    /// The game saves through one temporary folder (ProgramData\Unknown Worlds\Subnautica\TempSave\tmpNNNNNN). Two copies of the game on one PC
    /// share it, and a copy that cleans up TempSave deleted the other's folder in the middle of its save: "Could not find a part of the path
    /// ...\TempSave\tmpNNNNNN\gameinfo.json". Deleting anything in TempSave except this copy's own folder is skipped (leftovers are harmless).
    /// </summary>
    [HarmonyPatch]
    static class TempSaveKeep
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var a = AccessTools.Method(typeof(Directory), "Delete", new[] { typeof(string), typeof(bool) });
            var b = AccessTools.Method(typeof(Directory), "Delete", new[] { typeof(string) });
            if (a != null) yield return a;
            if (b != null) yield return b;
        }

        static readonly System.Collections.Generic.HashSet<string> _told = new System.Collections.Generic.HashSet<string>();

        static bool Prefix(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.IndexOf("TempSave", StringComparison.OrdinalIgnoreCase) < 0) return true;
                string full = Path.GetFullPath(path).TrimEnd('\\', '/');
                string own = null;
                try { own = SaveLoadManager.GetTemporarySavePath(); } catch { }
                if (!string.IsNullOrEmpty(own))
                {
                    own = Path.GetFullPath(own).TrimEnd('\\', '/');
                    if (string.Equals(full, own, StringComparison.OrdinalIgnoreCase)) return true;
                    if (full.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (_told.Add(full)) Saves.Log("kept " + full + " (not this game's own temporary save folder)");
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>The temporary save folder is created again whenever the game asks for its path, so a folder deleted by something else is back before the save writes into it.</summary>
    [HarmonyPatch(typeof(SaveLoadManager), "GetTemporarySavePath")]
    static class TempSaveEnsure
    {
        static void Postfix(string __result)
        {
            try { if (!string.IsNullOrEmpty(__result) && !Directory.Exists(__result)) Directory.CreateDirectory(__result); } catch { }
        }
    }
}
