using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LateGamePerformance
{
    // Plain key=value file next to the manifest.
    //
    // What decides which simulation code runs is NOT a setting: HaulCache, HaulCacheFlushEveryTicks, RouteMaps and
    // YielderSearch are fixed. Two players whose values differed could drift apart in multiplayer, a multiplayer
    // mod can compare versions but cannot see inside this file, and nobody should have to know that. With the
    // values fixed, the same version of this mod behaves the same for everyone. Everything that is still a
    // setting here is measurement, testing or how work is split over threads, and may differ between players.
    //
    // If something needs switching off to track a problem down, that is what disabling the mod is for; a build
    // with a different fixed value is a different version, which a multiplayer mod's version check can see.
    internal sealed class Config
    {
        public const string FileName = "LateGamePerformance.cfg";

        // Keys that were settings up to 0.4.8. Still recognised so an old file can be told they are ignored.
        public static readonly string[] FixedKeys = { "HaulCache", "HaulCacheFlushEveryTicks", "RouteMaps", "YielderSearch" };

        // Added after settings stopped deciding what runs, so these never were keys. Fixed for the same reason.
        public bool TerrainMaps => true;
        public bool PlantWater => true;
        public bool DistrictCounts => true;
        public bool WaterMapCopy => true;
        public bool SoilScans => true;

        public bool HaulCache => true;
        // Nothing cached survives a tick: the conservative choice, and the only one ever played.
        public int HaulCacheFlushEveryTicks => 1;
        public bool RouteMaps => true;
        public bool YielderSearch => true;

        public bool HaulCacheVerify = false;
        public bool RouteMapsBackground = true;
        public int RouteMapsMinFields = 4;
        public int RouteMapsWorkers = 0;
        public bool YielderSearchVerify = false;
        public bool PlantWaterVerify = false;
        public bool DistrictCountsVerify = false;
        public bool WaterMapCopyVerify = false;
        public bool SoilScansVerify = false;
        // Rendering only, so it may differ between players. A way out, not a setting.
        public bool WaterRendering = true;
        // Not part of the simulation, so it may differ between players. A way out, not a setting.
        public bool BackgroundSave = true;
        public bool Timing = true;
        // Pacing only (how much simulation one frame may catch up after a hitch), never which ticks run.
        public bool LimitCatchUp = true;
        public bool SaveTiming = true;
        public bool RecordTimings = false;
        public int MetricsEveryTicks = 3000;
        public bool GcReport = true;
        public bool Diagnostics = false;
        public int StatsEveryTicks = 1000;

        public static Config Load(params string[] directories)
        {
            Config config = new Config();
            foreach (string directory in directories)
            {
                if (string.IsNullOrEmpty(directory))
                {
                    continue;
                }
                string path = Path.Combine(directory, FileName);
                if (File.Exists(path))
                {
                    config.Apply(Parse(File.ReadAllLines(path)));
                    Log.Info("Settings read from " + path);
                    return config;
                }
            }
            Log.Info(FileName + " not found; using defaults.");
            return config;
        }

        public static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in lines)
            {
                string line = rawLine;
                int comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line.Substring(0, comment);
                }
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
            return values;
        }

        public void Apply(Dictionary<string, string> values)
        {
            foreach (string key in FixedKeys)
            {
                if (values.TryGetValue(key, out string text))
                {
                    Log.Info($"Settings: '{key} = {text}' is ignored. It is no longer a setting: since 0.4.9 it is the " +
                             "same for every player, so that everyone in a multiplayer game runs the same code.");
                }
            }
            HaulCacheVerify = Bool(values, nameof(HaulCacheVerify), HaulCacheVerify);
            RouteMapsBackground = Bool(values, nameof(RouteMapsBackground), RouteMapsBackground);
            RouteMapsMinFields = Math.Max(1, Int(values, nameof(RouteMapsMinFields), RouteMapsMinFields));
            RouteMapsWorkers = Math.Max(0, Int(values, nameof(RouteMapsWorkers), RouteMapsWorkers));
            YielderSearchVerify = Bool(values, nameof(YielderSearchVerify), YielderSearchVerify);
            PlantWaterVerify = Bool(values, nameof(PlantWaterVerify), PlantWaterVerify);
            DistrictCountsVerify = Bool(values, nameof(DistrictCountsVerify), DistrictCountsVerify);
            WaterMapCopyVerify = Bool(values, nameof(WaterMapCopyVerify), WaterMapCopyVerify);
            SoilScansVerify = Bool(values, nameof(SoilScansVerify), SoilScansVerify);
            WaterRendering = Bool(values, nameof(WaterRendering), WaterRendering);
            BackgroundSave = Bool(values, nameof(BackgroundSave), BackgroundSave);
            Timing = Bool(values, nameof(Timing), Timing);
            LimitCatchUp = Bool(values, nameof(LimitCatchUp), LimitCatchUp);
            SaveTiming = Bool(values, nameof(SaveTiming), SaveTiming);
            RecordTimings = Bool(values, nameof(RecordTimings), RecordTimings);
            MetricsEveryTicks = Math.Max(0, Int(values, nameof(MetricsEveryTicks), MetricsEveryTicks));
            GcReport = Bool(values, nameof(GcReport), GcReport);
            Diagnostics = Bool(values, nameof(Diagnostics), Diagnostics);
            StatsEveryTicks = Math.Max(0, Int(values, nameof(StatsEveryTicks), StatsEveryTicks));
        }

        public override string ToString()
        {
            return $"(fixed: HaulCache={HaulCache}, HaulCacheFlushEveryTicks={HaulCacheFlushEveryTicks}, " +
                   $"RouteMaps={RouteMaps}, YielderSearch={YielderSearch}, TerrainMaps={TerrainMaps}, PlantWater={PlantWater}, DistrictCounts={DistrictCounts}, " +
                   $"WaterMapCopy={WaterMapCopy}, SoilScans={SoilScans}) " +
                   $"HaulCacheVerify={HaulCacheVerify}, RouteMapsBackground={RouteMapsBackground}, RouteMapsMinFields={RouteMapsMinFields}, " +
                   $"RouteMapsWorkers={RouteMapsWorkers}, YielderSearchVerify={YielderSearchVerify}, PlantWaterVerify={PlantWaterVerify}, DistrictCountsVerify={DistrictCountsVerify}, " +
                   $"WaterMapCopyVerify={WaterMapCopyVerify}, SoilScansVerify={SoilScansVerify}, WaterRendering={WaterRendering}, BackgroundSave={BackgroundSave}, Timing={Timing}, LimitCatchUp={LimitCatchUp}, SaveTiming={SaveTiming}, RecordTimings={RecordTimings}, MetricsEveryTicks={MetricsEveryTicks}, " +
                   $"GcReport={GcReport}, Diagnostics={Diagnostics}, " +
                   $"StatsEveryTicks={StatsEveryTicks}";
        }

        private static bool Bool(Dictionary<string, string> values, string key, bool fallback)
        {
            if (values.TryGetValue(key, out string text) && bool.TryParse(text, out bool parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static int Int(Dictionary<string, string> values, string key, int fallback)
        {
            if (values.TryGetValue(key, out string text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
            return fallback;
        }
    }
}
