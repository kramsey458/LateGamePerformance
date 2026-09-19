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
    private const string ModSettingsDirectory =
        @"C:\Program Files (x86)\Steam\steamapps\workshop\content\1062090\3283831040\version-1.1\Scripts";

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
            foreach (string directory in new[] { managed, ModSettingsDirectory })
            {
                string path = Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(path))
                {
                    return Assembly.LoadFrom(path);
                }
            }
            return null;
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
        Check(patchCount == 32, $"32 patches declared (found {patchCount})");
        TestSettingsPage();

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

    // The settings page cannot be shown outside the game, but what Mod Settings needs from it can be checked:
    // the type loads against the installed Mod Settings, it exposes exactly one setting property (Mod Settings
    // finds settings by reflecting over public properties), and its id matches the manifest.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void TestSettingsPage()
    {
        Type page = typeof(PerformanceSettings);
        Type modSetting = page.BaseType.Assembly.GetType("ModSettings.Core.ModSetting", true);
        int settings = 0;
        foreach (PropertyInfo property in page.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (modSetting.IsAssignableFrom(property.PropertyType))
            {
                settings++;
            }
        }
        Check(settings == 1, "settings page: one setting property is discoverable by Mod Settings");
        string manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "packaging", "manifest.json"));
        Check(manifest.Contains("\"Id\": \"" + Plugin.ModId + "\""), "settings page: mod id matches manifest.json");
        Check(manifest.Contains("eMka.ModSettings"), "settings page: manifest requires Mod Settings");

        // The menu flag switches the game's timers on when a scene's metrics service is created and loaded.
        Type serviceType = Type.GetType("Timberborn.Metrics.MetricsService, Timberborn.Metrics", true);
        object service = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(serviceType);
        PropertyInfo enabled = serviceType.GetProperty("MetricsEnabled");
        MetricsDump.CreateFeature(new Config()).Patches.ForEach(patch => patch.Target());
        MetricsDump.UserDataFolderPath = () => Path.GetTempPath();
        MetricsDump.RequestedFromMenu = false;
        MetricsDump.MetricsServiceCreatedPostfix(service);
        MetricsDump.MetricsServiceLoadedPostfix(service);
        Check(!(bool)enabled.GetValue(service), "metrics: stay off when the menu setting is off");
        MetricsDump.RequestedFromMenu = true;
        MetricsDump.MetricsServiceCreatedPostfix(service);
        Check((bool)enabled.GetValue(service), "metrics: switched on as the service is created");
        enabled.SetValue(service, false);   // what the game's own Load does without -metrics
        MetricsDump.MetricsServiceLoadedPostfix(service);
        Check((bool)enabled.GetValue(service), "metrics: switched on again after the game's Load reset it");
        MetricsDump.RequestedFromMenu = false;
    }

    private static void TestTimingStats()
    {
        // A clock of 1000 ticks per second, so stamps read as milliseconds.
        TimingStats stats = new TimingStats(1000);
        long stamp = 1000;
        for (int frame = 0; frame < 30; frame++, stamp += 20)
        {
            stats.Frame(stamp, 12, 7f, 5);
        }
        stamp += 280;                          // the 30th frame ran 300 ms and the collection counter moved in it
        stats.Frame(stamp, 12, 7f, 6);
        for (int frame = 0; frame < 19; frame++)
        {
            stamp += 20;
            stats.Frame(stamp, 12, 7f, 6);
        }
        stamp += 5000;                         // loading or the window in the background: not counted
        stats.Frame(stamp, 900, 7f, 6);
        stamp += 500;                          // paused, for instance silently by a multiplayer mod
        stats.Frame(stamp, 3, 0f, 6);
        stamp += 500;
        stats.Frame(stamp, 3, 0f, 6);
        string line = stats.TakeLine(10, 0.6f, 1);
        Console.WriteLine("     " + line);
        // 49 counted frames: 29 x 20 + 300 + 19 x 20 = 1260 ms; 49 x 12 ms of simulation = 588 ms.
        Check(line.Contains("10 ticks in 1.3 s unpaused = 7.9 ticks/s"), "timing: tick rate over unpaused time");
        Check(line.Contains("average speed setting 7.0 asks for 11.7"), "timing: what the speed setting asks for");
        Check(line.Contains("wall clock 7.3 s, of which paused 1.0 s and not counted 5.0 s"), "timing: wall clock, paused and uncounted time");
        Check(line.Contains("49 frames = 39 fps"), "timing: frames exclude the long gap and paused frames");
        Check(line.Contains("simulation 58.8 ms per tick = 47% of the main thread"), "timing: simulation share");
        Check(line.Contains("everything else 13.7 ms per frame = 53%"), "timing: everything else per frame");
        Check(line.Contains("longest frame 300 ms, longest simulation slice 12 ms"), "timing: longest frame ignores the gap");
        Check(line.Contains("1 garbage collection(s): the 1 frame(s) containing one took 300 ms in total, longest 300 ms (an average frame is 25.7 ms)"),
            "timing: the frame containing a collection is identified and timed");
        Check(stats.TakeLine(10, 0.6f, 0) == null, "timing: nothing to report after a reset");
        stats.Frame(20000, 5, 3f, 6);
        stats.Frame(20010, 5, 3f, 6);
        string quiet = stats.TakeLine(1, -1f, 0);
        Check(!quiet.Contains("asks for"), "timing: unknown tick length omits the comparison");
        Check(quiet.Contains("0 garbage collection(s), none during counted frames"), "timing: no collections reported plainly");
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
