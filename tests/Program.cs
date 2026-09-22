using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LateGamePerformance;

// Applies every patch against the installed game's real assemblies (without running the game) and checks
// that each one landed. Usage: dotnet run --project tests -- "<GameDir>\Timberborn_Data\Managed"
// With --hashes as the first argument it prints the current hash of every listed snapshot component's Save
// method instead, ready to paste into SaveSnapshot.Allowed after a game or fork update.
internal static class Program
{
    private const string ModSettingsDirectory =
        @"C:\Program Files (x86)\Steam\steamapps\workshop\content\1062090\3283831040\version-1.1\Scripts";
    // The owner's BeaverBuddies MultiColony fork, under Documents; its ColonyStamp is on the snapshot list.
    private const string BeaverBuddiesDll = @"Timberborn\Mods\BeaverBuddies-MultiColony\version-1.1\BeaverBuddies.dll";

    private static int _failures;
    private static string _managed;

    private static int Main(string[] args)
    {
        bool hashes = args.Length > 0 && args[0] == "--hashes";
        string managed = args.Length > (hashes ? 1 : 0)
            ? args[hashes ? 1 : 0]
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
                     "Timberborn.Emptying", "Timberborn.StockpilePrioritySystem", "Timberborn.Workshops",
                     "Timberborn.GameSaveRuntimeSystem", "Timberborn.SaveSystem", "Timberborn.WorldPersistence", "Timberborn.WorldSerialization",
                     "Timberborn.ThumbnailCapturing", "Timberborn.SingletonSystem", "Timberborn.YielderFinding", "Timberborn.WaterObjects",
                     "Timberborn.ResourceCountingSystem", "Timberborn.GameSaveRepositorySystem", "Timberborn.FileSystem",
                     "Timberborn.Yielding", "Timberborn.Goods", "Timberborn.SerializationSystem",
                     "Timberborn.WaterSystem", "Timberborn.SoilMoistureSystem", "Timberborn.SoilContaminationSystem",
                     "Timberborn.WaterSystemRendering", "Timberborn.TerrainSystemRendering", "Timberborn.MapIndexSystem",
                     "Timberborn.MapStateSystem", "Timberborn.TerrainSystem", "Timberborn.BlockSystem", "Timberborn.WalkingSystem",
                     "Timberborn.CoreSound", "Timberborn.CameraSystem", "Timberborn.SoundSystem", "Timberborn.StatusSystem",
                     "Timberborn.EntityPanelSystem", "Timberborn.TimbermeshAnimations", "Timberborn.BaseComponentSystem",
                     "Timberborn.EntitySystem", "Timberborn.TemplateSystem", "Timberborn.Versioning", "Timberborn.TopBarSystem",
                     "Timberborn.BuildingsReachability", "Timberborn.DwellingSystem", "Timberborn.Beavers", "Timberborn.GameDistricts",
                     "Timberborn.SimulationSystem", "Timberborn.Common", "Timberborn.Multithreading", "Timberborn.BehaviorSystem",
                     "Timberborn.TimeSystem", "Timberborn.EnterableSystem", "Timberborn.CharacterMovementSystem", "Timberborn.BehaviorSystemUI" })
        {
            Assembly.LoadFrom(Path.Combine(managed, name + ".dll"));
        }

        // Kept out of Main so the mod's types are only touched once the resolver above is in place.
        return hashes ? PrintHashes() : Run();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int PrintHashes()
    {
        LoadEveryGameAssembly();
        int missing = 0;
        Console.WriteLine("        // dotnet run --project tests -c Release -- --hashes");
        try
        {
            Console.WriteLine($"        internal const string HelpersHash = \"{SaveGuard.HelpersHashNow()}\";");
        }
        catch (Exception exception)
        {
            Console.WriteLine("        // HELPERS NOT FOUND: " + exception.Message);
            missing++;
        }
        foreach (KeyValuePair<string, string> entry in SaveSnapshot.Allowed)
        {
            Type type = FindType(entry.Key);
            if (type == null)
            {
                Console.WriteLine($"            // NOT FOUND: {entry.Key}");
                missing++;
                continue;
            }
            Console.WriteLine($"            {{ \"{entry.Key}\", \"{SaveSnapshot.SaveHash(type)}\" }},");
        }
        return missing == 0 ? 0 : 1;
    }

    // Every game assembly, and the fork when it is installed, so that any listed type can be found.
    internal static void LoadEveryGameAssembly()
    {
        foreach (string path in Directory.GetFiles(_managed, "Timberborn.*.dll"))
        {
            try
            {
                Assembly.LoadFrom(path);
            }
            catch (Exception)
            {
                // Not a managed assembly this runtime can load; nothing on the list lives there.
            }
        }
        string fork = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), BeaverBuddiesDll);
        if (File.Exists(fork))
        {
            try
            {
                Assembly.LoadFrom(fork);
            }
            catch (Exception exception)
            {
                Console.WriteLine("     the BeaverBuddies fork could not be loaded: " + exception.Message);
            }
        }
    }

    internal static Type FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = null;
            try
            {
                type = assembly.GetType(fullName, false);
            }
            catch (Exception)
            {
                // An assembly whose references cannot be resolved here.
            }
            if (type != null)
            {
                return type;
            }
        }
        return null;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run()
    {
        List<string> warnings = new List<string>();
        Log.Sink = Console.WriteLine;
        Log.WarningSink = message => { warnings.Add(message); Console.WriteLine("WARN " + message); };

        TestConfigParsing();
        TestTimingStats();
        TestSaveBreakdown();
        TestTimingMemory();
        TestBootConfig();
        TestYielderSearch();
        TestVerifyOnlyMeasures();
        TestGarbageCollection();
        TestCatchUp();
        TestSoundAndUi();
        FeatureTests.Run(Check);

        // The Workshop Harmony build only runs under Mono, so patches are validated here, not applied.
        Feature[] features =
        {
            HaulCache.CreateFeature(new Config()), RouteMaps.CreateFeature(new Config()),
            RouteMaps.CreateBackgroundFeature(), Timing.CreateFeature(), SaveTiming.CreateFeature(), YielderSearch.CreateFeature(new Config()), TerrainMaps.CreateFeature(new Config()),
            PlantWater.CreateFeature(new Config()), DistrictCounts.CreateFeature(new Config()), BackgroundSave.CreateFeature(),
            WaterMapCopy.CreateFeature(new Config()), SoilScans.CreateFeature(new Config()), TerrainSearch.CreateFeature(new Config()), IdleEntities.CreateFeature(new Config()), SaveSnapshot.CreateFeature(new Config()),
            HomeSearch.CreateFeature(new Config()), SoilScans.CreateListsFeature(),
            WaterRendering.CreateTilesFeature(), WaterRendering.CreateUploadsFeature(),
            MetricsDump.CreateFeature(new Config()),
            Diagnostics.CreateFeature(), CatchUp.CreateFeature(),
            SoundListenerSkip.CreateFeature(), UiThrottle.CreateFeature(), AnimatorCulling.CreateFeature(),
            TerrainReach.CreateFeature(), MapChanges.CreateFeature(), BehaviorLog.CreateFeature(), WalkerMove.CreateFeature(),
            Plugin.CreateTickFeature()
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
        Check(PatchValidator.HasExceptionFilter(Reflect.Method("Timberborn.GameSaveRuntimeSystem.GameSaver", "Save")),
            "validator: recognises an exception filter (GameSaver.Save, which crashed 0.4.3 when patched)");
        Check(patchCount == 121, $"121 patches declared (found {patchCount})");
        TestReplacingPrefixesRunLast(features);
        TestSettingsPage();
        TestHaulCacheFlush();

        RouteMapsTests.Run(Assembly.LoadFrom(Path.Combine(_managed, "Timberborn.Navigation.dll")), Check);
        TerrainAndWaterTests.Run(_managed, Check);
        SaveAndCountsTests.Run(Check);
        WaterAndSoilTests.Run(Check);
        TerrainSearchTests.Run(Check);
        IdleEntitiesTests.Run(Check);
        SaveSnapshotTests.Run(Check);
        HomeSearchTests.Run(Check);
        TestTurnedOff();
        string[] expectedWarnings =
        {
            "YielderSearch: result differs from the game's own search. Mod: nothing to take. Game: nothing in range.",
            "HaulCache verify: cached list differs from vanilla (cached 2, vanilla 2).",
            "CatchUp failed", "RouteMaps: a route map could not be built on the first try", "RouteMaps failed",
            "RouteMaps failed", "TerrainMaps: a terrain route map could not be built on the first try", "TerrainMaps failed",
            "PlantWater failed", "BackgroundSave: could not open", "on the worker thread failed",
            "BackgroundSave: SAVE FAILED", "BackgroundSave failed while preparing",
            "DistrictCounts verify: output capacity of Good0: the mod counted", "DistrictCounts failed",
            "WaterMapCopy verify: the copy made on a worker differs from the game's.",
            "PlantWater verify: object 3 read", "PlantWater verify: object 3 read", "SaveSnapshot: the saving code of",
            "HomeSearch verify: the mod moves in", "HomeSearch failed"
        };
        bool asExpected = warnings.Count == expectedWarnings.Length;
        for (int i = 0; asExpected && i < expectedWarnings.Length; i++)
        {
            asExpected = warnings[i].Contains(expectedWarnings[i]);
        }
        Check(asExpected, $"only the expected warnings from the forced failures were logged ({warnings.Count})");

        Console.WriteLine(_failures == 0 ? "ALL PASSED" : _failures + " FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // A simulation feature that turns itself off mid-game leaves this computer on the game's code and the other
    // players on the mod's. It is said again, with OFF, on every stats interval and in every new game scene, and a
    // dialog in the game asks every player to restart. Runs after the forced failures above.
    private static void TestTurnedOff()
    {
        string[] off = TurnedOff.Names();
        Check(off.SequenceEqual(new[] { "RouteMaps", "TerrainMaps", "PlantWater", "DistrictCounts", "HomeSearch" }),
            $"turned off: each simulation feature a forced failure above turned off was reported, once ({string.Join(", ", off)})");
        bool[] all = Enumerable.Repeat(true, Plugin.SimulationFeatureNames.Length).ToArray();
        string one = Plugin.TurnedOffLine(all, new[] { "RouteMaps" });
        Check(Plugin.TurnedOffLine(all, new string[0]) == null &&
              one.StartsWith("Simulation features: HaulCache on, RouteMaps OFF, YielderSearch on, TerrainMaps on,") &&
              one.Contains("RouteMaps turned itself off during this session after an error") &&
              one.Contains("every player should restart the game"),
            "turned off: the line says which feature is now OFF and asks every player to restart; nothing while none is");
        bool[] noTerrainSearch = (bool[])all.Clone();
        noTerrainSearch[Array.IndexOf(Plugin.SimulationFeatureNames, "TerrainSearch")] = false;
        string two = Plugin.TurnedOffLine(noTerrainSearch, new[] { "TerrainMaps", "HaulCache" });
        Check(two.Contains("HaulCache OFF") && two.Contains("TerrainMaps OFF") && two.Contains("TerrainSearch OFF") &&
              two.Contains("RouteMaps on") && two.Contains("TerrainMaps, HaulCache turned themselves off"),
            "turned off: several at once, and a feature that never started stays OFF");
        bool eachInPlace = true;
        foreach (string name in Plugin.SimulationFeatureNames)
        {
            string line = Plugin.TurnedOffLine(all, new[] { name });
            eachInPlace &= line.Contains(", " + name + " OFF") || line.StartsWith("Simulation features: " + name + " OFF");
            eachInPlace &= line.Split(new[] { "OFF" }, StringSplitOptions.None).Length == 2;
        }
        Check(eachInPlace, "turned off: each feature's name reported turns exactly that feature OFF in the line");
        string notice = TurnedOff.NoticeText(new[] { "RouteMaps", "TerrainMaps" });
        Check(notice.Contains("RouteMaps, TerrainMaps") && notice.Contains("Every player should quit and restart the game"),
            "turned off: the in-game notice names the features and asks every player to restart");

        // Said again in a new game scene and at the next stats interval.
        List<string> lines = new List<string>();
        Action<string> sink = Log.Sink;
        Log.Sink = message => { lines.Add(message); sink(message); };
        typeof(Plugin).GetMethod("GameSceneCreatedPostfix", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        int afterScene = lines.Count(IsTurnedOffLine);
        MethodInfo tickStarted = typeof(Plugin).GetMethod("TickStartedPrefix", BindingFlags.NonPublic | BindingFlags.Static);
        for (int i = 0; i < Plugin.Current.StatsEveryTicks; i++)
        {
            tickStarted.Invoke(null, null);
        }
        Log.Sink = sink;
        Check(afterScene == 1 && lines.Count(IsTurnedOffLine) == 2,
            $"turned off: the line is said again in a new game scene and on the next stats interval ({afterScene}, {lines.Count(IsTurnedOffLine)})");

        // The notice is built by the game's container in every game scene and looks once per frame.
        Type type = typeof(TurnedOffNotice);
        ConstructorInfo[] constructors = type.GetConstructors();
        Check(constructors.Length == 1 && constructors[0].GetParameters().Length == 1 &&
              constructors[0].GetParameters()[0].ParameterType.FullName == "Timberborn.CoreUI.DialogBoxShower" &&
              type.GetInterface("Timberborn.SingletonSystem.IUpdatableSingleton") != null &&
              typeof(TurnedOffNoticeConfigurator).GetCustomAttributesData().Any(attribute =>
                  attribute.AttributeType.Name == "ContextAttribute" &&
                  (string)attribute.ConstructorArguments[0].Value == "Game"),
            "turned off notice: constructed from the dialog shower, updated every frame, bound in the game scene");

        // When the notice shows: not while nothing is off, once when a feature turns itself off, not on the frames
        // after, again when another one does, and once at the start of a new game scene (a new notice) while any is.
        int shown = 0;
        bool nothingOff = !TurnedOffNotice.ShouldShow(ref shown, 0);
        bool first = TurnedOffNotice.ShouldShow(ref shown, 1);
        bool nextFrame = !TurnedOffNotice.ShouldShow(ref shown, 1);
        bool another = TurnedOffNotice.ShouldShow(ref shown, 2) && !TurnedOffNotice.ShouldShow(ref shown, 2);
        int newScene = 0;
        bool inNewScene = TurnedOffNotice.ShouldShow(ref newScene, 2) && !TurnedOffNotice.ShouldShow(ref newScene, 2);
        Check(nothingOff && first && nextFrame && another && inNewScene,
            "turned off notice: shown once per feature turning itself off and once per new game scene, never every frame");

        // The line is said right away when a feature reports, not only on the stats intervals (which can be off),
        // and only the first time that feature reports.
        List<string> infos = new List<string>();
        List<string> reportWarnings = new List<string>();
        Action<string> warningSink = Log.WarningSink;
        Log.Sink = infos.Add;
        Log.WarningSink = reportWarnings.Add;
        int before = TurnedOff.Version;
        TurnedOff.Report("IdleEntities", "IdleEntities failed (forced by the test)");
        int afterFirst = infos.Count(IsTurnedOffLine);
        TurnedOff.Report("IdleEntities", "IdleEntities failed again (forced by the test)");
        Log.Sink = sink;
        Log.WarningSink = warningSink;
        Check(reportWarnings.Count == 2 && afterFirst == 1 && infos.Count(IsTurnedOffLine) == 1 &&
              infos[0].Contains(string.Join(", ", off) + ", IdleEntities turned themselves off") &&
              TurnedOff.Version == before + 1,
            $"turned off: the line is said at once when a feature reports, and only the first time ({afterFirst}, {infos.Count(IsTurnedOffLine)})");
    }

    private static bool IsTurnedOffLine(string line)
    {
        return line.Contains("Simulation features: ") && line.Contains("turned themselves off during this session");
    }

    // Everything the haul cache holds is dropped at the start of every tick. That hook must come with the cache: the
    // TickHooks feature (stats lines, metrics) is optional and can fail on its own. Each tick runs every prefix the
    // given features put on TickableSingletonService.TickAll, as Harmony would.
    private static void TestHaulCacheFlush()
    {
        MethodBase tickAll = Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "TickAll");
        // CreateFeature and Activate set HaulCache's statics; put them back afterwards so later tests see what they
        // saw before this one.
        FieldInfo[] saved =
        {
            typeof(HaulCache).GetField("_active", BindingFlags.Static | BindingFlags.NonPublic),
            typeof(HaulCache).GetField("_verify", BindingFlags.Static | BindingFlags.NonPublic),
            typeof(HaulCache).GetField("_flushEveryTicks", BindingFlags.Static | BindingFlags.NonPublic)
        };
        object[] before = Array.ConvertAll(saved, field => field.GetValue(null));
        try
        {
            FlushChecks(tickAll);
        }
        finally
        {
            HaulCache.Reset();
            for (int i = 0; i < saved.Length; i++)
            {
                saved[i].SetValue(null, before[i]);
            }
        }
    }

    private static void FlushChecks(MethodBase tickAll)
    {
        Feature haulCache = HaulCache.CreateFeature(new Config());
        Feature tickHooks = Plugin.CreateTickFeature();
        PatchSpec own = haulCache.Patches.Find(patch => patch.Prefix != null && Equals(patch.Target(), tickAll));
        Check(own != null && own.Required, "haul cache: its per-tick flush is its own required prefix on TickableSingletonService.TickAll");
        FieldInfo epoch = typeof(HaulCache).GetField("_epoch", BindingFlags.Static | BindingFlags.NonPublic);
        HaulCache.Activate();
        int Flushes(params Feature[] installed)
        {
            int before = (int)epoch.GetValue(null);
            for (int tick = 0; tick < 3; tick++)
            {
                foreach (Feature feature in installed)
                {
                    foreach (PatchSpec patch in feature.Patches)
                    {
                        if (patch.Prefix != null && Equals(patch.Target(), tickAll))
                        {
                            patch.Prefix.Invoke(null, null);
                        }
                    }
                }
            }
            return (int)epoch.GetValue(null) - before;
        }
        int alone = Flushes(haulCache);
        int both = Flushes(haulCache, tickHooks);
        Check(alone == 3, $"haul cache: dropped on each of 3 ticks with the TickHooks feature missing (dropped {alone} times)");
        Check(both == 3, $"haul cache: dropped once per tick with TickHooks installed too ({both} times in 3 ticks)");

    }

    private static void TestCatchUp()
    {
        CatchUp.Policy policy = new CatchUp.Policy();
        bool steady = true;
        for (int i = 0; i < 200; i++)
        {
            steady &= policy.Allow(0.016f) == 0.016f;
        }
        Check(steady && Math.Abs(policy.AverageSeconds - 0.016f) < 0.001f,
            "catch-up: ordinary frames pass unchanged and set the average");
        float afterHitch = policy.Allow(0.3f);
        Check(Math.Abs(afterHitch - 1f / 30f) < 0.0001f,
            $"catch-up: a 300 ms frame after 16 ms frames may only catch up a thirtieth of a second (got {afterHitch:0.0000})");
        Check(policy.Allow(0.03f) == 0.03f, "catch-up: the frames after the hitch are not held back");
        CatchUp.Policy slow = new CatchUp.Policy();
        bool slowMachine = true;
        for (int i = 0; i < 200; i++)
        {
            // The average starts at a thirtieth of a second, so only the first couple of frames are trimmed.
            slowMachine &= slow.Allow(0.07f) == 0.07f || i < 3;
        }
        Check(slowMachine && slow.Allow(0.1f) == 0.1f && slow.Allow(0.5f) < 0.15f,
            "catch-up: a machine at 70 ms frames keeps its full share; the cap follows its own frame time");
        Check(policy.Allow(0f) == 0f && policy.Allow(-1f) == -1f, "catch-up: paused frames are left alone");
        // The prefix scales the game's delta by the same ratio, so the speed and Unity's own cap stay in it.
        CatchUp.Activate();
        CatchUp.MaximumDeltaTime = () => 1f / 3f;
        for (int i = 0; i < 200; i++)
        {
            CatchUp.UnscaledDeltaTime = () => 0.016f;
            float ordinary = 0.016f * 7f;
            CatchUp.UpdatePrefix(ref ordinary);
        }
        CatchUp.UnscaledDeltaTime = () => 0.3f;
        float delta = 0.3f * 7f;
        CatchUp.UpdatePrefix(ref delta);
        Check(Math.Abs(delta - 7f / 30f) < 0.001f, $"catch-up prefix: 2.1 s of game time becomes 0.23 s (got {delta:0.000})");
        string line = CatchUp.TakeStatsLine();
        Check(line != null && line.Contains("after 1 long frame(s)") && line.Contains("1.9 s of game time") &&
              line.Contains("longest such frame 300 ms"), "catch-up stats: " + line);
        // A 2 s stall: Unity hands the ticker a third of a second times the speed, and the policy's share of the
        // capped time is what the ticker keeps.
        CatchUp.UnscaledDeltaTime = () => 2f;
        float stalled = 7f / 3f;
        CatchUp.UpdatePrefix(ref stalled);
        Check(Math.Abs(stalled - 7f * (2f * 0.0169f)) < 0.02f, $"catch-up prefix: a stall beyond Unity's cap is scaled from the capped time (got {stalled:0.000})");
        CatchUp.TakeStatsLine();
        Check(CatchUp.TakeStatsLine() == null, "catch-up stats: nothing to say when nothing was limited");
        CatchUp.UnscaledDeltaTime = () => throw new InvalidOperationException("no unity");
        float untouched = 1f;
        CatchUp.UpdatePrefix(ref untouched);
        Check(untouched == 1f && !CatchUp.IsActive, "catch-up: an exception switches it off and leaves the delta alone");
    }

    private static void TestSoundAndUi()
    {
        // Sound listener: runs when the camera moved, while the listener glides, and every tenth frame otherwise.
        SoundListenerSkip.Policy policy = new SoundListenerSkip.Policy();
        Check(policy.ShouldRun(true), "sound listener: the first frame runs");
        policy.Ran(0.5f);
        Check(policy.ShouldRun(false), "sound listener: runs again while the listener is still gliding");
        policy.Ran(0.001f);
        int ran = 0;
        for (int i = 0; i < 100; i++)
        {
            if (policy.ShouldRun(false))
            {
                ran++;
                policy.Ran(0f);
            }
        }
        Check(ran == 10, $"sound listener: a still camera with a settled listener runs one frame in ten (ran {ran} of 100)");
        Check(policy.ShouldRun(true), "sound listener: a camera move runs at once");

        // UI throttle: the alert lists every fourth frame, the panel every second frame and when the entity changes.
        int statusRuns = 0, panelRuns = 0;
        for (int frame = 0; frame < 100; frame++)
        {
            statusRuns += UiThrottle.StatusDue(frame) ? 1 : 0;
            panelRuns += UiThrottle.PanelDue(frame, false) ? 1 : 0;
        }
        Check(statusRuns == 25 && panelRuns == 50, $"ui throttle: 25 status and 50 panel updates in 100 frames (got {statusRuns}, {panelRuns})");
        Check(UiThrottle.PanelDue(1, true), "ui throttle: a newly shown entity is refreshed on its first frame");
        int topBar = 0, reach = 0;
        for (int i = 0; i < 80; i++)
        {
            topBar += UiThrottle.TopBarDue(i) ? 1 : 0;
            reach += UiThrottle.ReachabilityDue(i) ? 1 : 0;
        }
        Check(topBar == 20 && reach == 10, $"ui throttle: the top bar every fourth frame and a selected entity's reachability every eighth tick ({topBar}, {reach})");
    }

    private static void TestConfigParsing()
    {
        Config config = new Config();
        config.Apply(Config.Parse(new[]
        {
            "# comment", "HaulCache = false", "haulcacheflusheveryticks=5 # trailing", "Diagnostics = TRUE",
            "StatsEveryTicks = -3", "Nonsense", "GcReport = maybe", "RouteMaps = false", "RouteMapsMinFields = 0",
            "LimitCatchUp = false", "UiThrottle = false"
        }));
        Check(!config.UiThrottle && config.SoundListener && config.AnimatorCulling,
            "config: SoundListener, UiThrottle and AnimatorCulling are ways out, on by default");
        Check(!config.LimitCatchUp && new Config().LimitCatchUp, "config: LimitCatchUp is a setting, on by default");
        Check(config.HaulCache && config.HaulCacheFlushEveryTicks == 1 && config.RouteMaps && config.YielderSearch && config.TerrainSearch && config.IdleEntities && config.HomeSearch,
            "config: what decides which simulation code runs cannot be changed from the file");
        Check(config.Diagnostics, "config: bool case-insensitive");
        Check(config.StatsEveryTicks == 0, "config: negative clamped to 0");
        Check(config.GcReport, "config: unparsable value keeps default");
        Check(config.RouteMapsMinFields == 1, "config: int parsed and clamped");
        Check(Plugin.SimulationFeaturesLine(true, true, true, true, true, true, true, true, true, true, true) ==
              "Simulation features: HaulCache on, RouteMaps on, YielderSearch on, TerrainMaps on, PlantWater on, DistrictCounts on, " +
              "WaterMapCopy on, SoilScans on, TerrainSearch on, IdleEntities on, HomeSearch on. " +
              "These are the same for every player on this version.",
            "startup: one line says which simulation features are running");
        Check(Plugin.SimulationFeaturesLine(true, false, true, true, true, true, true, true, true, true, true).Contains("RouteMaps OFF") &&
              Plugin.SimulationFeaturesLine(true, true, true, false, true, true, true, true, true, true, true).Contains("TerrainMaps OFF") &&
              Plugin.SimulationFeaturesLine(true, true, true, true, true, false, true, true, true, true, true).Contains("DistrictCounts OFF") &&
              Plugin.SimulationFeaturesLine(true, true, true, true, true, true, false, true, true, true, true).Contains("WaterMapCopy OFF") &&
              Plugin.SimulationFeaturesLine(true, true, true, true, true, true, true, false, true, true, true).Contains("SoilScans OFF") &&
              Plugin.SimulationFeaturesLine(true, true, true, true, true, true, true, true, false, true, true).Contains("TerrainSearch OFF") &&
              Plugin.SimulationFeaturesLine(true, true, true, true, true, true, true, true, true, false, true).Contains("IdleEntities OFF") &&
              Plugin.SimulationFeaturesLine(true, true, true, true, true, true, true, true, true, true, false).Contains("HomeSearch OFF") &&
              Plugin.SimulationFeaturesLine(true, false, true, true, true, true, true, true, true, true, true).Contains("other players' logs"),
            "startup: a feature that could not start is called out");
        Check(!config.RecordTimings, "config: per-component timings are off unless asked for");
        config.Apply(Config.Parse(new[] { "routemapsworkers=5 # trailing", "YielderSearchVerify = true", "RecordTimings = true" }));
        Check(config.RouteMapsWorkers == 5 && config.YielderSearchVerify && config.RecordTimings, "config: key case-insensitive, trailing comment, testing switches still work");
        Config all = new Config();
        all.Apply(Config.Parse(new[] { "VerifyAll = true", "AnimatorLodDistance = -5", "UnityMarkers = Camera.Render; Culling ;; Culling" }));
        Check(all.VerifyAll && all.HaulCacheVerify && all.YielderSearchVerify && all.PlantWaterVerify && all.DistrictCountsVerify &&
              all.WaterMapCopyVerify && all.SoilScansVerify && all.TerrainSearchVerify && all.HomeSearchVerify && all.SaveSnapshotVerify &&
              !new Config().VerifyAll, "config: VerifyAll switches on every verify mode, off by default");
        Check(all.AnimatorLod && all.AnimatorLodDistance == 0 && new Config().AnimatorLodDistance == 80,
            "config: the animator distance is clamped at 0 and 80 tiles by default");
        Check(all.UnityMarkers == "Camera.Render; Culling ;; Culling" &&
              UnityMarkers.Parse(all.UnityMarkers).SequenceEqual(new[] { "Camera.Render", "Culling" }) &&
              UnityMarkers.Parse(UnityMarkers.DefaultSpec).Length == 18 && UnityMarkers.Parse(null).Length == 0 &&
              new Config().UnityMarkers == UnityMarkers.DefaultSpec,
            "config: the Unity marker list is kept as written and parsed into trimmed, distinct names");
        // No public field or settable property may carry one of the fixed names, or a later change could quietly
        // make it a setting again.
        foreach (string key in Config.FixedKeys)
        {
            PropertyInfo property = typeof(Config).GetProperty(key);
            Check(typeof(Config).GetField(key) == null && property != null && !property.CanWrite, $"config: {key} is fixed, not a setting");
        }
        string shipped = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "packaging", "LateGamePerformance.cfg"));
        foreach (string key in Config.FixedKeys)
        {
            Check(!System.Text.RegularExpressions.Regex.IsMatch(shipped, @"(?m)^\s*" + key + @"\s*="), $"config: the shipped file does not offer {key}");
        }
    }

    // A prefix that returns bool can skip the game's method, and Harmony then skips every prefix after it that could
    // change the call (a ref argument, a bool result). Equal priorities run in the order the mods were loaded, which is
    // each computer's own mod manager setting. So a skipping prefix of this mod that ran first would, on one computer,
    // answer before another mod's prefix narrowed the arguments (MultiColony's colony filter on
    // YielderFinder.FindLivingYielderWithoutAccessible), and on another after it: two players, two answers. Every
    // skipping prefix therefore carries [HarmonyPriority(Priority.Last)]. Found from the declared patches, not a list,
    // and checked the way Harmony will use it: the attribute as Feature.Apply's HarmonyMethod reads it, then Harmony's
    // own sort with another mod's ordinary prefix registered after this one (this mod loaded first).
    private static void TestReplacingPrefixesRunLast(Feature[] features)
    {
        List<string> early = PrefixesRunningFirst(features, out int replacing);
        Check(replacing > 0 && early.Count == 0,
            $"patches: all {replacing} prefixes that can skip the game's method run after other mods' prefixes, whatever the load order" +
            (early.Count > 0 ? "; these run first: " + string.Join(", ", early) : ""));

        // The walk itself: a void prefix that skips through `ref bool __runOriginal`, with no priority, is caught.
        Feature probe = new Feature { Name = "Probe" };
        probe.Patches.Add(new PatchSpec
        {
            Name = "probe", Target = features[0].Patches[0].Target,
            Prefix = typeof(Program).GetMethod(nameof(RunOriginalPrefix), BindingFlags.Static | BindingFlags.NonPublic)
        });
        Check(PrefixesRunningFirst(new[] { probe }, out int probed).Count == 1 && probed == 1,
            "patches: a prefix that skips the game's method through ref bool __runOriginal is found and held to the same rule");
    }

    private static void RunOriginalPrefix(ref bool __runOriginal)
    {
        __runOriginal = false;
    }

    private static List<string> PrefixesRunningFirst(IEnumerable<Feature> features, out int replacing)
    {
        List<string> early = new List<string>();
        replacing = 0;
        MethodInfo another = typeof(Program).GetMethod(nameof(AnotherModsPrefix), BindingFlags.Static | BindingFlags.NonPublic);
        foreach (Feature feature in features)
        {
            foreach (PatchSpec patch in feature.Patches)
            {
                // A bool result or a `ref bool __runOriginal` argument can skip the original.
                if (patch.Prefix == null || (patch.Prefix.ReturnType != typeof(bool) &&
                                             !patch.Prefix.GetParameters().Any(p => p.Name == "__runOriginal")))
                {
                    continue;
                }
                replacing++;
                HarmonyLib.HarmonyMethod ours = new HarmonyLib.HarmonyMethod(patch.Prefix);
                List<MethodInfo> order = HarmonyLib.PatchProcessor.GetSortedPatchMethods(patch.Target(), new[]
                {
                    new HarmonyLib.Patch(ours, 0, Plugin.HarmonyId + "." + feature.Name),
                    new HarmonyLib.Patch(new HarmonyLib.HarmonyMethod(another), 1, "another.mod")
                });
                if (ours.priority != HarmonyLib.Priority.Last || order.Count != 2 || order[1] != patch.Prefix)
                {
                    early.Add($"{feature.Name}/{patch.Name} ({patch.Prefix.DeclaringType?.Name}.{patch.Prefix.Name})");
                }
            }
        }
        return early;
    }

    private static void AnotherModsPrefix()
    {
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
        Check(settings == 7 && page.GetProperty("IncrementalGc") != null && page.GetProperty("DiagnosticsTimers") != null &&
              page.GetProperty("VerifyTerrainSearches") != null && page.GetProperty("VerifySaveSnapshots") != null &&
              page.GetProperty("VerifyEverything") != null && page.GetProperty("MeasureLiveMemory") != null &&
              page.GetProperty("WriteMemorySnapshot") != null,
            "settings page: seven settings: incremental garbage collection, diagnostics timers, verify terrain searches, verify " +
            "save snapshots, verify everything, and the two memory measurements");
        // The main menu notice is built by the game's container: it needs one public constructor whose
        // parameters are things the main menu binds, and the game calls it through this interface.
        Type notice = typeof(GcNotice);
        ConstructorInfo[] constructors = notice.GetConstructors();
        Check(constructors.Length == 1 && constructors[0].GetParameters().Length == 3 &&
              constructors[0].GetParameters()[0].ParameterType.FullName == "Timberborn.CoreUI.DialogBoxShower" &&
              constructors[0].GetParameters()[1].ParameterType == page &&
              constructors[0].GetParameters()[2].ParameterType.FullName == "Timberborn.SettingsSystem.ISettings",
            "gc notice: constructed from the dialog shower, the settings page and the game's settings store");
        Check(notice.GetInterface("Timberborn.SingletonSystem.IPostLoadableSingleton") != null, "gc notice: runs after the main menu has loaded");
        string manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "packaging", "manifest.json"));
        Check(manifest.Contains("\"Id\": \"" + Plugin.ModId + "\""), "settings page: mod id matches manifest.json");
        Check(manifest.Contains("eMka.ModSettings"), "settings page: manifest requires Mod Settings");

        // The menu flag switches the game's timers on when a scene's metrics service is created and loaded.
        Type serviceType = Type.GetType("Timberborn.Metrics.MetricsService, Timberborn.Metrics", true);
        object service = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(serviceType);
        PropertyInfo enabled = serviceType.GetProperty("MetricsEnabled");
        MetricsDump.CreateFeature(new Config()).Patches.ForEach(patch => patch.Target());
        MetricsDump.UserDataFolderPath = () => Path.GetTempPath();
        MetricsDump.Requested = false;
        MetricsDump.MetricsServiceCreatedPostfix(service);
        MetricsDump.MetricsServiceLoadedPostfix(service);
        Check(!(bool)enabled.GetValue(service), "metrics: stay off when RecordTimings is off");
        MetricsDump.Requested = true;
        MetricsDump.MetricsServiceCreatedPostfix(service);
        Check((bool)enabled.GetValue(service), "metrics: switched on as the service is created");
        enabled.SetValue(service, false);   // what the game's own Load does without -metrics
        MetricsDump.MetricsServiceLoadedPostfix(service);
        Check((bool)enabled.GetValue(service), "metrics: switched on again after the game's Load reset it");
        MetricsDump.Requested = false;
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
        Check(line.Contains("(game time scale 7.0, which is the speed buttons after the game's large-colony throttle, asks for 11.7)"),
            "timing: what the time scale asks for, not readable as the button speed");
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
        Check(!quiet.Contains("save(s)"), "timing: no saves, nothing said about saves");

        // A save inside the simulation call of a frame (how a multiplayer mod runs it): the call at stamp 1040
        // took 812 ms, the frame it started lasted 830 ms and a collection ran in it.
        stats = new TimingStats(1000);
        stats.Frame(1000, 12, 7f, 5);
        stats.Frame(1020, 12, 7f, 5);
        stats.Save(1041, 800);
        stats.Frame(1040, 812, 7f, 5);         // accounts for the frame before the save; the save stays pending
        stats.Frame(1870, 12, 7f, 6);          // the save's frame: 830 ms, with a collection
        stats.Frame(1890, 12, 7f, 6);
        stats.Frame(1990, 12, 7f, 7);          // an ordinary 100 ms frame containing a collection
        string saved = stats.TakeLine(10, 0.6f, 2);
        Console.WriteLine("     " + saved);
        Check(saved.Contains("longest frame 100 ms, longest simulation slice 12 ms"), "timing: a save is not the longest frame or slice");
        Check(saved.Contains("1 garbage collection(s): the 1 frame(s) containing one took 100 ms in total, longest 100 ms"),
            "timing: a collection during a save is not counted as a collection frame");
        Check(saved.Contains("; 1 save(s): 800 ms, in frame(s) of 830 ms with 1 garbage collection(s), all left out of the other figures"),
            "timing: the save is reported on its own");
        Check(saved.Contains("4 frames = 25 fps") && saved.Contains("not counted 0.8 s"), "timing: the save's frame is not a counted frame");

        // A save between two simulation calls (the unmodded game saves in LateUpdate), in a frame over the 2 s limit.
        stats = new TimingStats(1000);
        stats.Frame(1000, 12, 7f, 5);
        stats.Frame(1020, 12, 7f, 5);
        stats.Save(1030, 2500);
        stats.Frame(3540, 12, 7f, 5);
        stats.Frame(3560, 12, 7f, 5);
        Check(stats.TakeLine(10, 0.6f, 0).Contains("; 1 save(s): 2500 ms, in frame(s) of 2520 ms with 0 garbage collection(s)"),
            "timing: a save between simulation calls, in a very long frame, is still reported");
    }

    private static void TestTimingMemory()
    {
        const long mb = 1024 * 1024;
        // 1000 stamps per second. Memory grows 1 MB a frame; two collections free 200 MB and 100 MB.
        TimingStats stats = new TimingStats(1000);
        long stamp = 1000, heap = 1600 * mb;
        for (int frame = 0; frame <= 600; frame++, stamp += 100)
        {
            if (frame == 250) heap -= 200 * mb;
            else if (frame == 500) heap -= 100 * mb;
            else heap += mb;
            stats.Frame(stamp, 10, 7f, frame < 250 ? 5 : frame < 500 ? 6 : 7, heap);
        }
        string line = stats.TakeLine(100, 0.6f, 2);
        Console.WriteLine("     " + line);
        // 600 counted frames = 60 s. It starts at 1601 MB, climbs to 1850, drops to 1650, climbs to 1899, drops to
        // 1799 and ends at 1899. 598 frames grew 1 MB each (the first call only sets the baseline).
        Check(line.Contains("; managed memory 1601-1899 MB in use"), "timing: range of managed memory in use");
        Check(line.Contains("allocating 10.0 MB/s"), "timing: allocation rate is growth between frames over the wall clock");
        Check(line.EndsWith("2.0 collection(s) per minute freeing about 150 MB each"), "timing: collection rate and what a collection frees");
        stats.Frame(stamp, 10, 7f, 7, heap);
        stats.Frame(stamp + 100, 10, 7f, 7, heap + mb);
        string next = stats.TakeLine(1, 0.6f, 0);
        Check(next.Contains("allocating 5.0 MB/s") && !next.Contains("per minute"), "timing: memory figures restart each interval, and no collections says nothing about them");
        TimingStats blind = new TimingStats(1000);
        blind.Frame(1000, 5, 3f, 1);
        blind.Frame(1010, 5, 3f, 1);
        Check(!blind.TakeLine(1, 0.6f, 0).Contains("managed memory"), "timing: no memory figures when the heap cannot be read");

        // 100 frames of 50 ms: 20 ms of simulation, 6 ms in the per-frame systems, 9 ms in the late ones.
        TimingStats split = new TimingStats(1000);
        for (int frame = 0; frame <= 100; frame++) split.Frame(1000 + frame * 50, 20, 7f, 1, 0, 6, 9);
        string splitLine = split.TakeLine(50, 0.6f, 0);
        Console.WriteLine("     " + splitLine);
        Check(splitLine.Contains("everything else 30.0 ms per frame = 60% (per-frame systems of the game and mods 6.0 ms, " +
                                 "their late-update systems 9.0 ms, the rest 15.0 ms: rendering, animation and Unity itself);"),
            "timing: everything else is split into the game's per-frame systems and the rest");
        split.Frame(10000, 20, 7f, 1);
        split.Frame(10050, 20, 7f, 1);
        Check(!split.TakeLine(1, 0.6f, 0).Contains("per-frame systems"), "timing: no split when the per-frame systems were not measured");
    }

    private static void TestBootConfig()
    {
        string crlf = "wait-for-native-debugger=0\r\nhdr-display-enabled=0\r\n";
        Check(!BootConfig.HasKey(crlf), "boot.config: key absent");
        string added = BootConfig.WithIncremental(crlf);
        Check(added == crlf + "gc-max-time-slice=3\r\n", "boot.config: line appended with the file's own line endings");
        Check(BootConfig.WithIncremental(added) == added, "boot.config: adding twice changes nothing");
        Check(BootConfig.WithIncremental("a=1") == "a=1\ngc-max-time-slice=3\n", "boot.config: a file without a final newline gets one first");
        Check(BootConfig.WithoutIncremental(added) == crlf, "boot.config: removing restores the original text exactly");
        string byHand = "a=1\n  gc-max-time-slice = 5\nb=2\n";
        Check(BootConfig.HasKey(byHand) && BootConfig.WithIncremental(byHand) == byHand, "boot.config: a hand-written value is recognised and left alone");
        Check(BootConfig.WithoutIncremental(byHand) == "a=1\nb=2\n", "boot.config: removing takes only that line");
        Check(!BootConfig.HasKey("gc-max-time-slice-other=1\n# gc-max-time-slice=3\n"), "boot.config: similar keys and comments do not count");

        string directory = Path.Combine(Path.GetTempPath(), "lgp-bootconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "boot.config");
            Check(BootConfig.Apply(path, true, out _) == BootConfig.Outcome.FileMissing, "boot.config: a missing file is reported, not created");
            File.WriteAllText(path, crlf);
            Check(BootConfig.Apply(path, false, out _) == BootConfig.Outcome.AlreadyAsWanted && !File.Exists(path + BootConfig.BackupSuffix),
                "boot.config: nothing to do means no write and no backup");
            Check(BootConfig.Apply(path, true, out _) == BootConfig.Outcome.Changed && File.ReadAllText(path) == added,
                "boot.config: enabling writes the line");
            Check(File.ReadAllText(path + BootConfig.BackupSuffix) == crlf, "boot.config: the backup holds the original");
            Check(BootConfig.FileHasKey(path), "boot.config: the file reads back as enabled");
            Check(BootConfig.Apply(path, false, out _) == BootConfig.Outcome.Changed && File.ReadAllText(path) == crlf,
                "boot.config: disabling restores the original");
            Check(File.ReadAllText(path + BootConfig.BackupSuffix) == crlf, "boot.config: the first backup is never overwritten");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            Check(BootConfig.Apply(path, true, out string error) == BootConfig.Outcome.Failed && !string.IsNullOrEmpty(error),
                "boot.config: a file that cannot be written is reported, not thrown");
            File.SetAttributes(path, FileAttributes.Normal);

            // The setting's handler: logs, never throws, and goes through the same path the startup report reads.
            Func<string> previous = GcReport.BootConfigPath;
            GcReport.BootConfigPath = () => path;
            GcReport.ApplyIncremental(true);
            Check(BootConfig.FileHasKey(path), "boot.config: ticking the setting enables it");
            GcReport.ApplyIncremental(false);
            Check(!BootConfig.FileHasKey(path), "boot.config: unticking the setting removes it");
            GcReport.BootConfigPath = previous;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // A plant for the search rule: what the game would see of it.
    private sealed class Plant
    {
        public bool Exists = true, Yielding, Alive, Reachable;
        public string Good = "Log";
        public float Distance;
        public int Order;
    }

    // The whole of the game's search, from ClosestYielderFinder: what was found at all, the closest yielding plant
    // of each good (strictly closer wins, so the first of two equally close ones stays), then those tried by
    // distance, ties by instantiation order, and the first whose good the building has room for is the answer.
    private static string GamesResult(IEnumerable<Plant> reachedCandidates, Func<string, bool> roomFor)
    {
        bool foundSomething = false;
        var closest = new Dictionary<string, Plant>();
        foreach (Plant plant in reachedCandidates)
        {
            if (plant == null || !plant.Exists) continue;
            foundSomething = foundSomething || plant.Yielding || plant.Alive;
            if (plant.Yielding && (!closest.TryGetValue(plant.Good, out Plant best) || plant.Distance < best.Distance))
            {
                closest[plant.Good] = plant;
            }
        }
        if (!foundSomething) return "nothing in range";
        foreach (Plant plant in closest.Values.OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Order))
        {
            if (roomFor(plant.Good)) return "plant " + plant.Order;
        }
        return "nothing to take";
    }

    // What ClosestYielderFinder.FindClosestYielders makes of a sequence of looked-up candidates: whether it found
    // anything at all, and the closest yielding plant of each good. (null = a lookup that did not reach its plant.)
    private static string GamesAnswer(IEnumerable<Plant> reachedCandidates)
    {
        bool foundSomething = false;
        var closest = new SortedDictionary<string, Plant>();
        foreach (Plant plant in reachedCandidates)
        {
            if (plant == null || !plant.Exists) continue;
            foundSomething = foundSomething || plant.Yielding || plant.Alive;
            if (plant.Yielding && (!closest.TryGetValue(plant.Good, out Plant best) || plant.Distance < best.Distance))
            {
                closest[plant.Good] = plant;
            }
        }
        return foundSomething + ":" + string.Join(",", closest.Select(pair => pair.Key + "@" + pair.Value.Distance));
    }

    private static void TestYielderSearch()
    {
        Random random = new Random(12345);
        int cases = 0, differences = 0; long lookupsSaved = 0, deadSkipped = 0;
        for (int round = 0; round < 4000; round++)
        {
            int count = random.Next(0, 40);
            // Different mixes: forests that are mostly growing, mostly grown, unreachable, dead, destroyed.
            double yielding = random.NextDouble(), alive = random.NextDouble(), reachable = round % 5 == 0 ? 0.1 : random.NextDouble();
            List<Plant> plants = new List<Plant>();
            for (int i = 0; i < count; i++)
            {
                plants.Add(new Plant
                {
                    Exists = random.NextDouble() > 0.03, Yielding = random.NextDouble() < yielding,
                    Alive = random.NextDouble() < alive, Reachable = random.NextDouble() < reachable,
                    Good = random.Next(3) == 0 ? "Pine" : "Log", Distance = random.Next(1, 12)   // ties on purpose
                });
            }
            var counters = new YielderSearch.Counters();
            int lookedUpDead = 0;
            Func<Plant, Plant> lookUp = plant => plant.Reachable ? plant : null;
            string games = GamesAnswer(plants.Select(lookUp));
            string mods = GamesAnswer(YielderSearch.LazyCandidates(plants, plant => plant.Exists, plant => plant.Yielding,
                plant => plant.Alive, plant => { if (plant.Exists && !plant.Yielding && !plant.Alive && counters.Lookups > 1) lookedUpDead++; return lookUp(plant); },
                reached => reached != null, counters));
            cases++;
            if (games != mods) differences++;
            if (lookedUpDead > 0) differences++;
            if (plants.Exists(plant => plant.Exists) && counters.Lookups == 0) differences++;   // the first lookup fills the route map on the game's tick
            lookupsSaved += counters.Candidates - counters.Lookups;
            deadSkipped += counters.DeadSkipped;
            if (counters.Candidates != count) differences++;
        }
        Check(differences == 0, $"yielder search: same answer as the game's search in {cases} random forests ({differences} differences)");
        Check(deadSkipped > 5000, $"yielder search: a plant neither yielding nor alive is never looked up once the search has looked anything up ({deadSkipped} left out)");
        Check(lookupsSaved > 10000, $"yielder search: lookups are actually left out ({lookupsSaved})");
        // The reach pre-filter (0.4.25): an oracle that is only ever wrong on the safe side (it never says "no" about a
        // plant the lookup would reach) leaves the result the game's, whatever it says about the unreachable ones.
        int reachDifferences = 0; long outOfReach = 0, reachLookups = 0, plainLookups = 0;
        for (int round = 0; round < 4000; round++)
        {
            int count = random.Next(0, 40);
            double yielding = random.NextDouble(), alive = random.NextDouble(), reachable = round % 5 == 0 ? 0.1 : random.NextDouble();
            List<Plant> plants = new List<Plant>();
            for (int i = 0; i < count; i++)
            {
                plants.Add(new Plant
                {
                    Exists = random.NextDouble() > 0.03, Yielding = random.NextDouble() < yielding, Alive = random.NextDouble() < alive,
                    Reachable = random.NextDouble() < reachable, Good = random.Next(3) == 0 ? "Pine" : "Log", Distance = random.Next(1, 12), Order = i
                });
            }
            Func<Plant, Plant> lookUp = plant => plant.Reachable ? plant : null;
            Func<Plant, bool> mayReach = plant => plant.Reachable || random.Next(2) == 0;
            var with = new YielderSearch.Counters();
            var without = new YielderSearch.Counters();
            string games = GamesResult(plants.Select(lookUp), good => true);
            string mods = GamesResult(YielderSearch.LazyCandidates(plants, plant => plant.Exists, plant => plant.Yielding, plant => plant.Alive,
                lookUp, reached => reached != null, with, plant => true, mayReach), good => true);
            GamesResult(YielderSearch.LazyCandidates(plants, plant => plant.Exists, plant => plant.Yielding, plant => plant.Alive,
                lookUp, reached => reached != null, without, plant => true), good => true);
            if (games != mods) reachDifferences++;
            outOfReach += with.OutOfReach;
            reachLookups += with.Lookups;
            plainLookups += without.Lookups;
        }
        Check(reachDifferences == 0, $"yielder search: the reach pre-filter leaves the result the game's in 4000 random forests ({reachDifferences} differences)");
        Check(outOfReach > 5000 && reachLookups < plainLookups && reachLookups + outOfReach >= plainLookups,
            $"yielder search: the pre-filter only ever removes lookups ({outOfReach} candidates ruled out, {plainLookups - reachLookups} lookups fewer)");

        // With the building's room for each good taken into account (0.4.11): the answer, not just the candidates
        // fed to the search, must be the game's, whatever there is room for.
        int resultDifferences = 0, asked = 0, askedAboutNotYielding = 0; long savedByRoom = 0;
        for (int round = 0; round < 4000; round++)
        {
            int count = random.Next(0, 40);
            double yielding = random.NextDouble(), alive = random.NextDouble(), reachable = round % 5 == 0 ? 0.1 : random.NextDouble();
            var room = new Dictionary<string, bool> { ["Log"] = random.Next(3) > 0, ["Pine"] = random.Next(3) > 0, ["Resin"] = random.Next(2) > 0 };
            string[] goods = { "Log", "Pine", "Resin" };
            List<Plant> plants = new List<Plant>();
            for (int i = 0; i < count; i++)
            {
                plants.Add(new Plant
                {
                    Exists = random.NextDouble() > 0.03, Yielding = random.NextDouble() < yielding, Alive = random.NextDouble() < alive,
                    Reachable = random.NextDouble() < reachable, Good = goods[random.Next(3)], Distance = random.Next(1, 8), Order = i
                });
            }
            Func<Plant, Plant> lookUp = plant => plant.Reachable ? plant : null;
            var with = new YielderSearch.Counters();
            var without = new YielderSearch.Counters();
            string games = GamesResult(plants.Select(lookUp), good => room[good]);
            string mods = GamesResult(YielderSearch.LazyCandidates(plants, plant => plant.Exists, plant => plant.Yielding,
                plant => plant.Alive, lookUp, reached => reached != null, with,
                plant => { asked++; if (!plant.Yielding) askedAboutNotYielding++; return room[plant.Good]; }),
                good => room[good]);
            GamesResult(YielderSearch.LazyCandidates(plants, plant => plant.Exists, plant => plant.Yielding, plant => plant.Alive,
                lookUp, reached => reached != null, without), good => room[good]);
            if (games != mods) resultDifferences++;
            savedByRoom += without.Lookups - with.Lookups;
        }
        Check(resultDifferences == 0, $"yielder search: same result as the game with full and part-full buildings in 4000 random forests ({resultDifferences} differences)");
        Check(askedAboutNotYielding == 0, "yielder search: room is only asked about for yielding plants");
        Check(savedByRoom > 5000 && asked > 0, $"yielder search: lookups for goods there is no room for are left out ({savedByRoom})");

        // The measured case: a full lumberjack flag and 1300 grown, reachable trees. One lookup instead of 1300.
        List<Plant> grown = new List<Plant>();
        for (int i = 0; i < 1300; i++) grown.Add(new Plant { Yielding = true, Alive = true, Reachable = true, Distance = i % 53, Order = i });
        var full = new YielderSearch.Counters();
        string fullFlag = GamesResult(YielderSearch.LazyCandidates(grown, plant => plant.Exists, plant => plant.Yielding,
            plant => plant.Alive, plant => plant, reached => reached != null, full, plant => false), good => false);
        Check(fullFlag == "nothing to take" && fullFlag == GamesResult(grown, good => false) && full.Lookups == 1,
            $"yielder search: a full building among 1300 grown trees needs {full.Lookups} lookup instead of 1300");
        var hasRoom = new YielderSearch.Counters();
        string working = GamesResult(YielderSearch.LazyCandidates(grown, plant => plant.Exists, plant => plant.Yielding,
            plant => plant.Alive, plant => plant, reached => reached != null, hasRoom, plant => true), good => true);
        Check(working == GamesResult(grown, good => true) && working == "plant 0" && hasRoom.Lookups == 1300,
            "yielder search: with room, every grown tree is looked up as before and the same tree is chosen");

        // A forest like the one measured: 2000 marked trees, 50 grown, everything reachable and alive.
        List<Plant> forest = new List<Plant>();
        for (int i = 0; i < 2000; i++) forest.Add(new Plant { Yielding = i % 40 == 7, Alive = true, Reachable = true, Distance = i % 97 });
        var forestCounters = new YielderSearch.Counters();
        string lazy = GamesAnswer(YielderSearch.LazyCandidates(forest, plant => plant.Exists, plant => plant.Yielding,
            plant => plant.Alive, plant => plant, reached => reached != null, forestCounters));
        Check(lazy == GamesAnswer(forest) && forestCounters.Lookups == 51, $"yielder search: 2000 trees with 50 grown need {forestCounters.Lookups} lookups instead of 2000");
        // Nothing reachable: every candidate has to be looked up, exactly as the game does.
        forest.ForEach(plant => plant.Reachable = false);
        var blocked = new YielderSearch.Counters();
        GamesAnswer(YielderSearch.LazyCandidates(forest, plant => plant.Exists, plant => plant.Yielding, plant => plant.Alive,
            plant => plant.Reachable ? plant : null, reached => reached != null, blocked));
        Check(blocked.Lookups == 2000, "yielder search: with nothing reachable nothing can be left out");
    }

    // A verify key is each player's own (the .cfg, the settings page), so it must not change what the game is handed:
    // in co-op a player with a key on would otherwise take the game's answer where the others take the mod's, the
    // first time the two differ, and the colonies drift apart. Every verify mode is made to see a difference here or
    // in its feature's tests, and must hand the game what it is handed with the key off. The tree search and the
    // hauling list cannot run outside the game (their candidates are live buildings and plants), so these two drive
    // the step each prefix takes once the game's own answer is in: the comparison.
    private static void TestVerifyOnlyMeasures()
    {
        // The tree search: the mod found nothing to take, the game's own search says nothing is in range.
        bool wasVerifying = YielderSearch.VerifyEnabled;
        YielderSearch.VerifyEnabled = true;
        Timberborn.YielderFinding.YielderSearchResult mods = Timberborn.YielderFinding.YielderSearchResult.CreateEmpty();
        Timberborn.YielderFinding.YielderSearchResult handed =
            YielderSearch.Compared(mods, Timberborn.YielderFinding.YielderSearchResult.CreateNoYielderInRange());
        YielderSearch.VerifyEnabled = wasVerifying;
        Check(ReferenceEquals(handed.Yielder, null) && handed.NoYielderInRange == mods.NoYielderInRange,
            "verify only measures: the tree search hands the game the mod's result, as with verify off, when the game's own " +
            $"search differs (handed: {(handed.NoYielderInRange ? "nothing in range, the game's" : "nothing to take, the mod's")})");

        // The hauling list: the cache holds [a, b], the game's own build says [b, a].
        Type haulBehavior = typeof(Timberborn.Hauling.WeightedBehavior).Assembly.GetType("Timberborn.Hauling.HaulWorkplaceBehavior", true);
        var a = (Timberborn.WorkSystem.WorkplaceBehavior)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(haulBehavior);
        var b = (Timberborn.WorkSystem.WorkplaceBehavior)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(haulBehavior);
        List<Timberborn.WorkSystem.WorkplaceBehavior> list = new List<Timberborn.WorkSystem.WorkplaceBehavior> { a, b };
        HaulCache.Compare(list, new List<Timberborn.Hauling.WeightedBehavior>
        {
            new Timberborn.Hauling.WeightedBehavior(2f, b), new Timberborn.Hauling.WeightedBehavior(1f, a)
        });
        Check(list.Count == 2 && ReferenceEquals(list[0], a) && ReferenceEquals(list[1], b),
            "verify only measures: the hauling list handed to the game stays the cached one, as with verify off, when the game's " +
            $"own build differs (handed: {(ReferenceEquals(list[0], a) ? "the cached order" : "the game's order")})");

        // The water map's verify pass swaps the worker's copy in from a postfix (WaterAndSoilTests drives it), so that
        // postfix must run before any other mod's postfix on the same method, as the swap does with verify off:
        // BeaverBuddies' desync trace hashes the map there. Sorted by Harmony with another mod's ordinary postfix
        // registered first (that mod loaded first).
        PatchSpec update = WaterMapCopy.CreateFeature(new Config()).Patches[0];
        MethodInfo another = typeof(Program).GetMethod(nameof(AnotherModsPrefix), BindingFlags.Static | BindingFlags.NonPublic);
        List<MethodInfo> order = HarmonyLib.PatchProcessor.GetSortedPatchMethods(update.Target(), new[]
        {
            new HarmonyLib.Patch(new HarmonyLib.HarmonyMethod(another), 0, "another.mod"),
            new HarmonyLib.Patch(new HarmonyLib.HarmonyMethod(update.Postfix), 1, Plugin.HarmonyId + ".WaterMapCopy")
        });
        Check(order.Count == 2 && order[0] == update.Postfix,
            "verify only measures: the water map's verify postfix, which swaps the worker's copy in, runs before other mods' " +
            "postfixes on ThreadSafeWaterMap.Update whatever the load order");
    }

    private static void TestGarbageCollection()
    {
        bool incremental = true;
        Func<bool> previousIncremental = GcReport.IsIncremental;
        Func<ulong> previousSlice = GcReport.SliceNanoseconds;
        GcReport.IsIncremental = () => incremental;
        GcReport.SliceNanoseconds = () => 3000000;
        Check(Timing.CollectorText() == "; garbage collection is incremental, slice 3.0 ms", "timing: says that collection is incremental, and the slice");
        incremental = false;
        Check(Timing.CollectorText().Contains("NOT incremental"), "timing: says so when collection is not incremental");
        GcReport.IsIncremental = () => throw new InvalidOperationException("no Unity here");
        Check(Timing.CollectorText() == "", "timing: a collector that cannot be asked leaves the line as it was");
        GcReport.IsIncremental = previousIncremental;
        GcReport.SliceNanoseconds = previousSlice;

        // The main menu question: asked once, never when it is on or about to be, never again after "Not now".
        Check(GcNotice.ShouldAsk(false, false, false, false), "gc notice: asks when collection is not incremental and nothing is set up");
        Check(!GcNotice.ShouldAsk(false, true, false, false), "gc notice: 'Not now' is remembered, so it is not asked again");
        Check(!GcNotice.ShouldAsk(false, false, true, false), "gc notice: nothing to ask when collection is incremental");
        Check(!GcNotice.ShouldAsk(false, false, false, true), "gc notice: nothing to ask when only a restart is missing");
        Check(!GcNotice.ShouldAsk(true, false, false, false), "gc notice: at most once per launch");

        // Re-adding the line after a game update: only through the path the ticked setting takes, once a launch.
        string directory = Path.Combine(Path.GetTempPath(), "lgp-reapply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "boot.config");
            File.WriteAllText(path, "a=1\n");
            Func<string> previousPath = GcReport.BootConfigPath;
            GcReport.BootConfigPath = () => path;
            GcReport.ReapplyIncremental();
            Check(BootConfig.FileHasKey(path), "boot.config: the line is put back when the setting is ticked and the file was restored");
            File.WriteAllText(path, "a=1\n");
            GcReport.ReapplyIncremental();
            Check(!BootConfig.FileHasKey(path), "boot.config: put back at most once per launch");
            GcReport.BootConfigPath = previousPath;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestSaveBreakdown()
    {
        SaveBreakdown save = new SaveBreakdown(1000);
        save.Add(SaveStage.Snapshot, 50);
        Check(!save.IsOpen && save.Close(100) == null, "save: a stage outside a save is ignored and nothing is reported");
        save.Open("Save");
        Check(save.IsEmpty, "save: a save in which no stage ran can be told apart");
        save.Add(SaveStage.FinishingTheTick, 14);
        save.Add(SaveStage.Snapshot, 190);
        save.Add(SaveStage.WorldJson, 520);
        save.Add(SaveStage.Thumbnail, 40);
        save.Add(SaveStage.Thumbnail, 20);
        string line = save.Close(812);
        Console.WriteLine("     " + line);
        Check(line == "Save: 812 ms total = finishing the tick 14 ms + snapshot 190 ms + world JSON and compression 520 ms + " +
              "thumbnail 60 ms + everything else 28 ms", "save: stages and the remainder add up to the total");
        Check(!save.IsOpen && save.Close(812) == null, "save: closed after reporting; an outer nested save reports nothing");
        save.Note("ignored, no save is open");
        save.Open("Save");
        save.Note("the rest follows on a worker thread");
        Check(save.Close(10).EndsWith("everything else 10 ms (the rest follows on a worker thread)"), "save: a note is appended to the line");
        save.Open("Save");
        Check(save.Close(10).EndsWith("everything else 10 ms"), "save: and does not carry over to the next save");
        save.Open("Save");
        save.Add(SaveStage.Snapshot, 300);
        save.Open("Save");                     // the first save threw, so it was never closed
        save.Add(SaveStage.WorldJson, 90);
        line = save.Close(80);
        Check(line.Contains("snapshot 0 ms") && line.Contains("world JSON and compression 90 ms"), "save: an abandoned save does not leak into the next");
        Check(line.EndsWith("everything else 0 ms"), "save: the remainder is never negative");
        SaveBreakdown fine = new SaveBreakdown(10000000);
        fine.Open("Save");
        fine.Add(SaveStage.Snapshot, 1234567);
        Check(fine.Close(2500000).Contains("250 ms total") , "save: stopwatch ticks converted with the clock's frequency");
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
