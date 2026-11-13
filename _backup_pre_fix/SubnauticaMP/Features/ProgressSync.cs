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
                SyncKit.Apply(() => { if (!KnownTech.Contains(t)) KnownTech.Add(t, false); });
            });
            SyncKit.On("prog.analyze", (from, r) =>
            {
                var t = SyncKit.Tech(r);
                if (!HostSettings.SharedBlueprints) return;
                SyncKit.Apply(() => KnownTech.Analyze(t, false));
            });
            SyncKit.On("prog.enc", (from, r) =>
            {
                string key = r.Str();
                if (!HostSettings.SharedPda) return;
                SyncKit.Apply(() => { if (!PDAEncyclopedia.ContainsEntry(key)) PDAEncyclopedia.Add(key, false); });
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
                    if (go != null) UnityEngine.Object.Destroy(go);
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

        public void Tick() { }
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
                if (ProgressSync.LastGo != st.gameObject) { ProgressSync.LastGo = st.gameObject; ProgressSync.LastId = SyncKit.IdOf(st.gameObject); }
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
