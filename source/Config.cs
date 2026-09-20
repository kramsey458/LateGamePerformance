using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LateGamePerformance
{
    // Plain key=value file next to the manifest. Multiplayer peers must use identical values for the
    // keys marked "simulation" in the shipped LateGamePerformance.cfg.
    internal sealed class Config
    {
        public const string FileName = "LateGamePerformance.cfg";

        public bool HaulCache = true;
        public int HaulCacheFlushEveryTicks = 1;
        public bool HaulCacheVerify = false;
        public bool RouteMaps = true;
        public bool RouteMapsBackground = true;
        public int RouteMapsMinFields = 4;
        public int RouteMapsWorkers = 0;
        public bool YielderSearch = true;
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
            HaulCache = Bool(values, nameof(HaulCache), HaulCache);
            HaulCacheFlushEveryTicks = Math.Max(0, Int(values, nameof(HaulCacheFlushEveryTicks), HaulCacheFlushEveryTicks));
            HaulCacheVerify = Bool(values, nameof(HaulCacheVerify), HaulCacheVerify);
            RouteMaps = Bool(values, nameof(RouteMaps), RouteMaps);
            RouteMapsBackground = Bool(values, nameof(RouteMapsBackground), RouteMapsBackground);
            RouteMapsMinFields = Math.Max(1, Int(values, nameof(RouteMapsMinFields), RouteMapsMinFields));
            RouteMapsWorkers = Math.Max(0, Int(values, nameof(RouteMapsWorkers), RouteMapsWorkers));
            YielderSearch = Bool(values, nameof(YielderSearch), YielderSearch);
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
            return $"HaulCache={HaulCache}, HaulCacheFlushEveryTicks={HaulCacheFlushEveryTicks}, " +
                   $"HaulCacheVerify={HaulCacheVerify}, RouteMaps={RouteMaps}, RouteMapsBackground={RouteMapsBackground}, RouteMapsMinFields={RouteMapsMinFields}, " +
                   $"RouteMapsWorkers={RouteMapsWorkers}, YielderSearch={YielderSearch}, YielderSearchVerify={YielderSearchVerify}, Timing={Timing}, SaveTiming={SaveTiming}, MetricsEveryTicks={MetricsEveryTicks}, " +
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
