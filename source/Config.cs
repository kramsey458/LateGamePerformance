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
        public bool Timing = true;
        public bool SaveTiming = true;
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
            Timing = Bool(values, nameof(Timing), Timing);
            SaveTiming = Bool(values, nameof(SaveTiming), SaveTiming);
            MetricsEveryTicks = Math.Max(0, Int(values, nameof(MetricsEveryTicks), MetricsEveryTicks));
            GcReport = Bool(values, nameof(GcReport), GcReport);
            Diagnostics = Bool(values, nameof(Diagnostics), Diagnostics);
            StatsEveryTicks = Math.Max(0, Int(values, nameof(StatsEveryTicks), StatsEveryTicks));
        }

        public override string ToString()
        {
            return $"(fixed: HaulCache={HaulCache}, HaulCacheFlushEveryTicks={HaulCacheFlushEveryTicks}, " +
                   $"RouteMaps={RouteMaps}, YielderSearch={YielderSearch}) " +
                   $"HaulCacheVerify={HaulCacheVerify}, RouteMapsBackground={RouteMapsBackground}, RouteMapsMinFields={RouteMapsMinFields}, " +
                   $"RouteMapsWorkers={RouteMapsWorkers}, YielderSearchVerify={YielderSearchVerify}, Timing={Timing}, SaveTiming={SaveTiming}, MetricsEveryTicks={MetricsEveryTicks}, " +
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
