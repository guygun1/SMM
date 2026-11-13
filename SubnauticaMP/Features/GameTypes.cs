using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MPCore.IO;
using MPCore.Sync;
using UnityEngine;

namespace SubnauticaMP.Features
{
    /// <summary>
    /// Game components kept in step by field, through the same registry mods use (ModStateSync does the work: the host decides,
    /// a joiner sends for objects it touched). One row per game type: the type name and the fields to keep equal. A type or field
    /// that this game version does not have is left out and logged, so a wrong name costs nothing. Both machines run the same
    /// game version, so they register the same fields. Called once from Plugin.Awake, before anybody connects.
    ///
    /// Also here: a drillable deposit that was mined out is removed on every machine (drill.gone).
    /// </summary>
    sealed class GameTypes : ISyncFeature
    {
        public string Name { get { return "game-types"; } }

        static readonly string[][] Table =
        {
            new[] { "Drillable", "health" },                                   // mining progress of a deposit
            new[] { "GrowingPlant", "timeStartGrowth" },                       // planters and the plants in them grow in step
            new[] { "PingInstance", "colorIndex", "visible", "_label" },       // PDA pings and beacon colour/name
            new[] { "BeaconLabel", "labelName" },
            new[] { "BaseBioReactor", "charge" },
            new[] { "FiltrationMachine", "timeRemainingWater", "timeRemainingSalt" },
            new[] { "Charger", "charging" },
        };

        static bool _done;

        public static void RegisterAll()
        {
            if (_done) return;
            _done = true;
            foreach (var row in Table)
            {
                try
                {
                    var t = AccessTools.TypeByName(row[0]);
                    if (t == null) { SyncKit.Info("game-types: no type " + row[0] + " in this game version, skipped"); continue; }
                    var fields = row.Skip(1).Where(f => AccessTools.Field(t, f) != null).ToArray();
                    foreach (var f in row.Skip(1)) if (Array.IndexOf(fields, f) < 0) SyncKit.Info("game-types: " + row[0] + " has no field " + f + ", skipped");
                    if (fields.Length == 0) continue;
                    SyncRegistry.Register(t, fields, "Subnautica");
                    SyncKit.Info("game-types: syncing " + row[0] + " (" + string.Join(", ", fields) + ")");
                }
                catch (Exception e) { SyncKit.Warn("game-types: " + row[0] + ": " + e.Message); }
            }
        }

        public void Register()
        {
            SyncKit.On("drill.gone", (from, r) =>
            {
                string id = r.Str();
                SyncKit.Apply(() =>
                {
                    var go = SyncKit.Find(id);
                    if (go != null && go.GetComponent<Drillable>() != null) UnityEngine.Object.Destroy(go);
                });
            });
        }

        public void Tick() { }
    }

    [HarmonyPatch]
    static class P_DrillDone
    {
        static bool Prepare() { return AccessTools.Method(typeof(Drillable), "OnDrill") != null; }
        static System.Reflection.MethodBase TargetMethod() { return AccessTools.Method(typeof(Drillable), "OnDrill"); }

        static void Postfix(Drillable __instance)
        {
            try
            {
                if (__instance == null || !SyncKit.ShouldSend) return;
                var hp = Traverse.Create(__instance).Field("health").GetValue<float[]>();
                if (hp == null || hp.Any(h => h > 0f)) return;     // still has something left: the field sync carries the progress
                var id = SyncKit.IdOf(__instance.gameObject);
                if (id != null) SyncKit.Send("drill.gone", w => w.Str(id));
            }
            catch { }
        }
    }
}
