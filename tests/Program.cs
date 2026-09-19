using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LateGamePerformance;

// Applies every patch against the installed game's real assemblies (without running the game) and checks
// that each one landed. Usage: dotnet run --project tests -- "<GameDir>\Timberborn_Data\Managed"
internal static class Program
{
    private static int _failures;
    private static string _managed;

    private static int Main(string[] args)
    {
        string managed = args.Length > 0
            ? args[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed";
        _managed = managed;
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string path = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        // AccessTools.TypeByName only sees loaded assemblies; in the game they all are.
        foreach (string name in new[] { "Timberborn.Metrics", "Timberborn.Hauling", "Timberborn.InventorySystem", "Timberborn.TickSystem",
                     "Timberborn.Navigation", "Timberborn.NeedBehaviorSystem", "Timberborn.BlockingSystem",
                     "Timberborn.Emptying", "Timberborn.StockpilePrioritySystem", "Timberborn.Workshops" })
        {
            Assembly.LoadFrom(Path.Combine(managed, name + ".dll"));
        }

        // Kept out of Main so the mod's types are only touched once the resolver above is in place.
        return Run();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run()
    {
        List<string> warnings = new List<string>();
        Log.Sink = Console.WriteLine;
        Log.WarningSink = message => { warnings.Add(message); Console.WriteLine("WARN " + message); };

        TestConfigParsing();
        TestTimingStats();

        // The Workshop Harmony build only runs under Mono, so patches are validated here, not applied.
        Feature[] features =
        {
            HaulCache.CreateFeature(new Config()), RouteMaps.CreateFeature(new Config()),
            RouteMaps.CreateBackgroundFeature(), Timing.CreateFeature(), MetricsDump.CreateFeature(new Config()),
            Diagnostics.CreateFeature(), Plugin.CreateTickFeature()
        };
        int patchCount = 0;
        foreach (Feature feature in features)
        {
            List<string> problems = PatchValidator.Validate(feature);
            patchCount += feature.Patches.Count;
            Check(problems.Count == 0, $"{feature.Name}: all {feature.Patches.Count} patch targets resolve");
            foreach (string problem in problems)
            {
                Console.WriteLine("     " + problem);
            }
        }
        Check(patchCount == 31, $"31 patches declared (found {patchCount})");

        RouteMapsTests.Run(Assembly.LoadFrom(Path.Combine(_managed, "Timberborn.Navigation.dll")), Check);
        Check(warnings.Count == 1 && warnings[0].Contains("RouteMaps failed"),
            $"only the expected warning from the forced failure was logged ({warnings.Count})");

        Console.WriteLine(_failures == 0 ? "ALL PASSED" : _failures + " FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void TestConfigParsing()
    {
        Config config = new Config();
        config.Apply(Config.Parse(new[]
        {
            "# comment", "HaulCache = false", "haulcacheflusheveryticks=5 # trailing", "Diagnostics = TRUE",
            "StatsEveryTicks = -3", "Nonsense", "GcReport = maybe", "RouteMaps = false", "RouteMapsMinFields = 0"
        }));
        Check(!config.HaulCache, "config: bool parsed");
        Check(config.HaulCacheFlushEveryTicks == 5, "config: int parsed, key case-insensitive, trailing comment");
        Check(config.Diagnostics, "config: bool case-insensitive");
        Check(config.StatsEveryTicks == 0, "config: negative clamped to 0");
        Check(config.GcReport, "config: unparsable value keeps default");
        Check(!config.RouteMaps && config.RouteMapsMinFields == 1, "config: route map settings parsed and clamped");
    }

    private static void TestTimingStats()
    {
        // A clock of 1000 ticks per second, so stamps read as milliseconds.
        TimingStats stats = new TimingStats(1000);
        for (int frame = 0; frame < 50; frame++)
        {
            stats.Frame(1000 + frame * 20, 12, 7f);
        }
        stats.Frame(2000 + 5000, 900, 7f);   // a 5 s gap: loading or the window in the background
        stats.Frame(7020, 3, 0f);            // paused
        stats.Frame(7040, 3, 0f);
        string line = stats.TakeLine(10, 0.6f, 4);
        Console.WriteLine("     " + line);
        // 49 counted frames of 20 ms = 0.98 s; 49 x 12 ms of simulation = 588 ms.
        Check(line.Contains("10 ticks in 1.0 s unpaused = 10.2 ticks/s"), "timing: tick rate over unpaused time");
        Check(line.Contains("average speed setting 7.0 asks for 11.7"), "timing: what the speed setting asks for");
        Check(line.Contains("49 frames = 50 fps"), "timing: frames exclude the long gap and paused frames");
        Check(line.Contains("simulation 58.8 ms per tick = 60% of the main thread"), "timing: simulation share");
        Check(line.Contains("everything else 8.0 ms per frame = 40%"), "timing: everything else per frame");
        Check(line.Contains("longest frame 20 ms, longest simulation slice 12 ms"), "timing: longest frame ignores the gap");
        Check(line.Contains("4 garbage collections"), "timing: garbage collections reported");
        Check(stats.TakeLine(10, 0.6f, 0) == null, "timing: nothing to report after a reset");
        stats.Frame(9000, 5, 3f);
        stats.Frame(9010, 5, 3f);
        Check(!stats.TakeLine(1, -1f, 0).Contains("asks for"), "timing: unknown tick length omits the comparison");
    }

    private static void Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "ok   " : "FAIL ") + what);
        if (!condition)
        {
            _failures++;
        }
    }
}
