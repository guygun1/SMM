using System;
using HarmonyLib;
using MPCore.IO;
using Story;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>Shared progress: blueprints, scanner fragments, encyclopedia, story goals.</summary>
    sealed class ProgressSync : ISyncFeature
    {
        public string Name { get { return "progress"; } }

        internal static GameObject ScanGo; internal static string ScanId; internal static TechType ScanTech;
        internal static GameObject LastGo; internal static string LastId;   // id lookup cache for the scan target

        public void Register()
        {
            SyncKit.On("prog.tech", (from, r) =>
            {
                var t = SyncKit.Tech(r);
                if (!HostSettings.SharedBlueprints) return;
                Later(() => { if (!KnownTech.Contains(t)) KnownTech.Add(t, Notify()); });
            });
            SyncKit.On("prog.analyze", (from, r) =>
            {
                var t = SyncKit.Tech(r);
                if (!HostSettings.SharedBlueprints) return;
                Later(() => KnownTech.Analyze(t, Notify()));
            });
            SyncKit.On("prog.enc", (from, r) =>
            {
                string key = r.Str();
                if (!HostSettings.SharedPda) return;
                // while the world loads the encyclopedia tables do not exist yet: ContainsEntry throws. Try again later, like the blueprints.
                Later(() => { if (!PDAEncyclopedia.ContainsEntry(key)) PDAEncyclopedia.Add(key, Notify()); });
            });
            SyncKit.On("prog.scan", (from, r) =>
            {
                var t = SyncKit.Tech(r); int unlocked = (int)r.VarI();
                if (!HostSettings.SharedPda) return;
                SyncKit.Apply(() =>
                {
                    PDAScanner.Entry e = default(PDAScanner.Entry);
                    if (PDAScanner.GetPartialEntryByKey(t, out e) && e.unlocked >= unlocked) return;
                    var m = AccessTools.Method(typeof(PDAScanner), "Add", new[] { typeof(TechType), typeof(int) });
                    if (m != null) m.Invoke(null, new object[] { t, unlocked });
                });
            });
            SyncKit.On("prog.scandone", (from, r) =>
            {
                var t = SyncKit.Tech(r); string id = r.Str();
                if (!HostSettings.SharedPda) return;
                SyncKit.Apply(() =>
                {
                    var go = SyncKit.Find(id);
                    // only destroy the object that was scanned: same unique id AND the same tech
                    if (go == null || !IsScanTarget(go, t)) return;
                    UnityEngine.Object.Destroy(go);
                });
            });
            SyncKit.On("prog.goalx", (from, r) =>
            {
                string key = r.Str(); var type = (Story.GoalType)(int)r.VarI();
                if (!(type == Story.GoalType.Radio ? HostSettings.SharedRadio : HostSettings.SharedStory)) return;
                SyncKit.Apply(() =>
                {
                    var m = StoryGoalManager.main;
                    if (m != null && m.IsGoalComplete(key)) return;
                    StoryGoal.Execute(key, type);
                });
            });
            SyncKit.On("prog.goal", (from, r) =>
            {
                string key = r.Str();
                if (!HostSettings.SharedStory) return;
                SyncKit.Apply(() =>
                {
                    var m = StoryGoalManager.main;
                    if (m != null && !m.IsGoalComplete(key)) m.OnGoalComplete(key);
                });
            });
        }

        /// <summary>A shared unlock shows the same notice as for the player who made it ("blueprint unlocked", "new data entry in the PDA"),
        /// but only while we are playing: not while the world is still loading.</summary>
        static bool Notify() { try { return Player.main != null && Time.timeSinceLevelLoad > 5f; } catch { return false; } }

        // Blueprint messages can arrive while the game's own tech lists are not built yet (the world is still loading): the game
        // then throws inside KnownTech. Those are kept and tried again once a second, for up to a minute, instead of being lost.
        sealed class Wait { public Action A; public float Until; }
        static readonly System.Collections.Generic.List<Wait> _wait = new System.Collections.Generic.List<Wait>();
        float _nextWait;

        static void Later(Action a)
        {
            bool failed = false;
            SyncKit.Apply(() => { try { a(); } catch (NullReferenceException) { failed = true; } });
            if (failed && _wait.Count < 500) _wait.Add(new Wait { A = a, Until = Time.unscaledTime + 60f });
        }

        public void Tick()
        {
            if (_wait.Count == 0 || Time.unscaledTime < _nextWait) return;
            _nextWait = Time.unscaledTime + 1f;
            var todo = _wait.ToArray(); _wait.Clear();
            foreach (var w in todo)
            {
                bool failed = false;
                SyncKit.Apply(() => { try { w.A(); } catch (NullReferenceException) { failed = true; } });
                if (failed && Time.unscaledTime < w.Until) _wait.Add(w);
            }
        }

        /// <summary>The unique id of this exact object, or null. Unlike SyncKit.IdOf it never takes a parent's id:
        /// a parent's id would name the whole machine or base, not the scanned object.</summary>
        internal static string OwnId(GameObject go)
        {
            if (go == null) return null;
            var uid = go.GetComponent<UniqueIdentifier>();
            return uid != null && !string.IsNullOrEmpty(uid.Id) ? uid.Id : null;
        }

        /// <summary>False only if go clearly carries a different tech than the scanned one, so a remote scandone cannot
        /// destroy a different object. If the tech of go cannot be read the unique-id match alone is trusted (and logged).
        /// Uses CraftData.GetTechType (no TechTag member needed).</summary>
        internal static bool IsScanTarget(GameObject go, TechType tech)
        {
            TechType have = TechType.None;
            try { have = CraftData.GetTechType(go); } catch { }
            if (have == TechType.None || tech == TechType.None)
            {
                SyncKit.Info("prog: scandone target tech unknown, trusting unique id (" + tech + ")");
                return true;
            }
            return have == tech;
        }
    }

    [HarmonyPatch(typeof(KnownTech), "Add", new[] { typeof(TechType), typeof(bool) })]
    static class P_KnownTechAdd
    {
        static void Postfix(TechType techType, bool __result)
        {
            try
            {
                if (!__result || !HostSettings.SharedBlueprints || !SyncKit.ShouldSend) return;
                SyncKit.Send("prog.tech", w => SyncKit.Tech(w, techType));
            }
            catch (Exception e) { SyncKit.Warn("prog tech: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(KnownTech), "Analyze", new[] { typeof(TechType), typeof(bool) })]
    static class P_KnownTechAnalyze
    {
        static void Postfix(TechType techType)
        {
            try
            {
                if (!HostSettings.SharedBlueprints || !SyncKit.ShouldSend) return;
                SyncKit.Send("prog.analyze", w => SyncKit.Tech(w, techType));
            }
            catch (Exception e) { SyncKit.Warn("prog analyze: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(PDAEncyclopedia), "Add", new[] { typeof(string), typeof(bool) })]
    static class P_EncAdd
    {
        static void Postfix(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !HostSettings.SharedPda || !SyncKit.ShouldSend) return;
                SyncKit.Send("prog.enc", w => w.Str(key));
            }
            catch (Exception e) { SyncKit.Warn("prog enc: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(PDAScanner), "Add", new[] { typeof(TechType), typeof(int) })]
    static class P_ScanAdd
    {
        static void Postfix(PDAScanner.Entry __result)
        {
            try
            {
                if (!HostSettings.SharedPda || !SyncKit.ShouldSend) return;
                if ((object)__result == null) return;          // Add returns nothing when the entry already existed or the tech is unknown
                var t = __result.techType; int u = __result.unlocked;
                SyncKit.Send("prog.scan", w => { SyncKit.Tech(w, t); w.VarI(u); });
            }
            catch (Exception e) { SyncKit.Warn("prog scan: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(PDAScanner), "Scan")]
    static class P_Scan
    {
        static void Prefix()
        {
            try
            {
                // Scan() runs every frame while the scanner points at something: look the id up once per target
                ProgressSync.ScanGo = null; ProgressSync.ScanId = null;
                if (!SyncKit.ShouldSend) return;
                var st = PDAScanner.scanTarget;
                if (!st.isValid || st.gameObject == null) return;
                ProgressSync.ScanGo = st.gameObject; ProgressSync.ScanTech = st.techType;
                if (ProgressSync.LastGo != st.gameObject) { ProgressSync.LastGo = st.gameObject; ProgressSync.LastId = ProgressSync.OwnId(st.gameObject); }
                ProgressSync.ScanId = ProgressSync.LastId;
            }
            catch (Exception e) { SyncKit.Warn("prog scan pre: " + e.Message); }
        }

        static void Postfix()
        {
            try
            {
                var go = ProgressSync.ScanGo; var id = ProgressSync.ScanId; var tech = ProgressSync.ScanTech;
                ProgressSync.ScanGo = null; ProgressSync.ScanId = null;
                if (go == null || id == null || !HostSettings.SharedPda || !SyncKit.ShouldSend) return;
                // target was invalidated by the game = scan finished and the object is being destroyed
                if (PDAScanner.scanTarget.gameObject == go) return;
                if (!PDAScanner.IsFragment(tech)) return;
                SyncKit.Send("prog.scandone", w => { SyncKit.Tech(w, tech); w.Str(id); });
            }
            catch (Exception e) { SyncKit.Warn("prog scan post: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(StoryGoal), "Execute", new[] { typeof(string), typeof(Story.GoalType) })]
    static class P_GoalExecute
    {
        static void Prefix(string key, Story.GoalType goalType)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || !(goalType == Story.GoalType.Radio ? HostSettings.SharedRadio : HostSettings.SharedStory) || !SyncKit.ShouldSend) return;
                var m = StoryGoalManager.main;
                if (m != null && m.IsGoalComplete(key)) return;
                SyncKit.Send("prog.goalx", w => { w.Str(key); w.VarI((int)goalType); });
            }
            catch (Exception e) { SyncKit.Warn("prog goalx: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(StoryGoalManager), "OnGoalComplete")]
    static class P_GoalComplete
    {
        static void Postfix(string key, bool __result)
        {
            try
            {
                if (!__result || string.IsNullOrEmpty(key) || !HostSettings.SharedStory || !SyncKit.ShouldSend) return;
                SyncKit.Send("prog.goal", w => w.Str(key));
            }
            catch (Exception e) { SyncKit.Warn("prog goal: " + e.Message); }
        }
    }
}
