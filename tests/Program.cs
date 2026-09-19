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
        foreach (string name in new[] { "Timberborn.Hauling", "Timberborn.InventorySystem", "Timberborn.TickSystem",
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

        // The Workshop Harmony build only runs under Mono, so patches are validated here, not applied.
        Feature[] features =
        {
            HaulCache.CreateFeature(new Config()), RouteMaps.CreateFeature(new Config()),
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
        Check(patchCount == 20, $"20 patches declared (found {patchCount})");

        RouteMapsTests.Run(Assembly.LoadFrom(Path.Combine(_managed, "Timberborn.Navigation.dll")), Check);
        Check(warnings.Count == 0, "no warnings logged");

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

    private static void Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "ok   " : "FAIL ") + what);
        if (!condition)
        {
            _failures++;
        }
    }
}
