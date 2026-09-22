using System;
using System.Reflection;
using Timberborn.ModManagerScene;

namespace LateGamePerformance
{
    public class Plugin : IModStarter
    {
        // Must equal the Id in manifest.json; the settings page is registered under it.
        public const string ModId = "kyler.lategameperformance";

        public const string HarmonyId = ModId;

        // The simulation features in the order of the startup line, under the names they report to TurnedOff.
        internal static readonly string[] SimulationFeatureNames =
        {
            "HaulCache", "RouteMaps", "YielderSearch", "TerrainMaps", "PlantWater", "DistrictCounts", "WaterMapCopy",
            "SoilScans", "TerrainSearch", "IdleEntities", "HomeSearch",
            "Reachability"
        };

        private static Config _config = new Config();
        private static bool _diagnosticsActive;
        // Which of them started, for the line said again after one turns itself off.
        private static bool[] _started = new bool[SimulationFeatureNames.Length];

        // Read by the settings page for the defaults of the boxes that mirror .cfg keys.
        internal static Config Current => _config;
        private static long _ticksSinceReport;

        public void StartMod(IModEnvironment modEnvironment)
        {
            try
            {
                string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                Log.Info(version + " loading.");
                _config = Config.Load(modEnvironment.ModPath, modEnvironment.OriginPath, FolderAboveScripts());
                Log.Info("Settings: " + _config);
                Start(_config);
            }
            catch (Exception exception)
            {
                Log.Warning("Failed to start; the game runs unmodified. " + exception);
            }
        }

        // The settings file ships next to the manifest, one level above Scripts/. ModPath may or may not be that
        // folder depending on how the game resolved the versioned layout, so it is tried as well.
        private static string FolderAboveScripts()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location)
                    ? null
                    : System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(location));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Separate from StartMod so the test harness can apply the patches without a mod environment.
        internal static void Start(Config config)
        {
            _config = config;
            TickWorkers.Configure(config.RouteMapsWorkers > 0
                ? Math.Min(config.RouteMapsWorkers, 32)
                : Math.Max(1, Math.Min(7, Environment.ProcessorCount / 2 - 1)));
            UnityMarkers.Configure(config.UnityMarkers);
            bool haulCache = config.HaulCache && HaulCache.CreateFeature(config).Apply(HarmonyId);
            if (haulCache)
            {
                HaulCache.Activate();
            }
            bool routeMaps = config.RouteMaps && RouteMaps.CreateFeature(config).Apply(HarmonyId);
            if (routeMaps)
            {
                RouteMaps.Activate();
                // If any gate cannot be installed, fall back to waiting for the whole batch, which needs none.
                bool background = config.RouteMapsBackground && RouteMaps.CreateBackgroundFeature().Apply(HarmonyId);
                RouteMaps.SetBackground(background);
                Log.Info($"RouteMaps: {RouteMaps.WorkerCount} worker threads, " +
                         (background ? "rebuilding in the background." : "main thread waits for each rebuild."));
            }
            bool yielderSearch = config.YielderSearch && YielderSearch.CreateFeature(config).Apply(HarmonyId);
            if (yielderSearch)
            {
                YielderSearch.Activate();
            }
            bool terrainMaps = config.TerrainMaps && TerrainMaps.CreateFeature(config).Apply(HarmonyId);
            if (terrainMaps)
            {
                TerrainMaps.Activate();
            }
            bool plantWater = config.PlantWater && PlantWater.CreateFeature(config).Apply(HarmonyId);
            if (plantWater)
            {
                PlantWater.Activate();
            }
            bool districtCounts = config.DistrictCounts && DistrictCounts.CreateFeature(config).Apply(HarmonyId);
            if (districtCounts)
            {
                DistrictCounts.Activate();
            }
            bool waterMapCopy = config.WaterMapCopy && WaterMapCopy.CreateFeature(config).Apply(HarmonyId);
            if (waterMapCopy)
            {
                WaterMapCopy.Activate();
            }
            bool soilScans = config.SoilScans && SoilScans.CreateFeature(config).Apply(HarmonyId);
            if (soilScans)
            {
                SoilScans.Activate();
            }
            bool terrainSearch = config.TerrainSearch && TerrainSearch.CreateFeature(config).Apply(HarmonyId);
            if (terrainSearch)
            {
                TerrainSearch.Activate();
            }
            bool idleEntities = config.IdleEntities && IdleEntities.CreateFeature(config).Apply(HarmonyId);
            if (idleEntities)
            {
                IdleEntities.Activate();
            }
            bool homeSearch = config.HomeSearch && HomeSearch.CreateFeature(config).Apply(HarmonyId);
            if (homeSearch)
            {
                HomeSearch.Activate();
            }
            bool reachability = config.Reachability && Reachability.CreateFeature(config).Apply(HarmonyId);
            if (reachability)
            {
                Reachability.Activate();
            }
            // The soil lists ride on the scans; the plant water levels on the water map copy. Each falls back to the
            // feature it rides on, so neither is a simulation feature of its own.
            if (soilScans && SoilScans.CreateListsFeature().Apply(HarmonyId))
            {
                SoilScans.ActivateLists();
            }
            if (plantWater && waterMapCopy)
            {
                PlantWater.UseWaterMapCopy();
            }
            // Exact replacements that cannot give a different result (the same calls with the same arguments): not
            // settings, and not in the startup line, because a peer running the game's own code computes the same.
            if (TerrainReach.CreateFeature().Apply(HarmonyId))
            {
                TerrainReach.Activate();
            }
            if (BehaviorLog.CreateFeature().Apply(HarmonyId))
            {
                BehaviorLog.Activate();
            }
            if (WalkerMove.CreateFeature().Apply(HarmonyId))
            {
                WalkerMove.Activate();
            }
            if ((routeMaps || terrainMaps) && MapChanges.CreateFeature().Apply(HarmonyId))
            {
                MapChanges.Activate();
            }
            // In the order of SimulationFeatureNames.
            _started = new[]
            {
                haulCache, routeMaps, yielderSearch, terrainMaps, plantWater, districtCounts, waterMapCopy, soilScans,
                terrainSearch, idleEntities, homeSearch,
                reachability
            };
            Log.Info(SimulationFeaturesLine(_started));
            if (config.WaterRendering)
            {
                if (WaterRendering.CreateTilesFeature().Apply(HarmonyId))
                {
                    WaterRendering.ActivateTiles();
                }
                if (WaterRendering.CreateUploadsFeature().Apply(HarmonyId))
                {
                    WaterRendering.ActivateUploads();
                }
            }
            if (config.BackgroundSave && BackgroundSave.CreateFeature().Apply(HarmonyId))
            {
                BackgroundSave.Activate();
            }
            if (config.SaveSnapshot && SaveSnapshot.CreateFeature(config).Apply(HarmonyId))
            {
                SaveSnapshot.Activate();
            }
            // Not a setting: a save's snapshot waits for the game's parallel tick, which BeaverBuddies' saves skip.
            if (ParallelTickWait.CreateFeature().Apply(HarmonyId))
            {
                ParallelTickWait.Activate();
            }
            if (config.SoundListener && SoundListenerSkip.CreateFeature().Apply(HarmonyId))
            {
                SoundListenerSkip.Activate();
            }
            if (config.UiThrottle && UiThrottle.CreateFeature().Apply(HarmonyId))
            {
                UiThrottle.Activate();
            }
            if (config.AnimatorCulling && AnimatorCulling.CreateFeature().Apply(HarmonyId))
            {
                AnimatorCulling.Activate();
                AnimatorCulling.ConfigureLod(config.AnimatorLod, config.AnimatorLodDistance);
            }
            if (config.DeferPhysicsSync && PhysicsSync.CreateFeature().Apply(HarmonyId))
            {
                PhysicsSync.Activate();
            }
            if (config.ShaftAnimators && ShaftAnimators.CreateFeature().Apply(HarmonyId))
            {
                ShaftAnimators.Activate();
            }
            if (config.Timing && Timing.CreateFeature().Apply(HarmonyId))
            {
                Timing.Activate();
            }
            if (config.LimitCatchUp && CatchUp.CreateFeature().Apply(HarmonyId))
            {
                CatchUp.Activate();
            }
            if (config.SaveTiming)
            {
                SaveTiming.CreateFeature().Apply(HarmonyId);
            }
            MetricsDump.Requested = config.RecordTimings;
            if (config.MetricsEveryTicks > 0)
            {
                MetricsDump.CreateFeature(config).Apply(HarmonyId);
            }
            // The timers are installed always and switched on from the settings page or the .cfg, so a player can
            // turn them on for one session without editing a file. Off, each hooked call costs one boolean check.
            Diagnostics.Enabled = config.Diagnostics;
            _diagnosticsActive = Diagnostics.CreateFeature().Apply(HarmonyId);
            CreateTickFeature().Apply(HarmonyId);
            if (config.GcReport)
            {
                GcReport.Write();
            }
        }

        // The parts that replace simulation code are not settings, so they can only differ between two players if
        // one failed to start (a game update moved something) or turned itself off after an error (TurnedOffLine).
        // One line, the same words for everyone, so two players' logs can be compared at a glance. `started` holds one
        // entry per SimulationFeatureNames entry, in that order.
        internal static string SimulationFeaturesLine(bool[] started)
        {
            string line = FeatureList(started);
            return Array.TrueForAll(started, on => on)
                ? line + " These are the same for every player on this version."
                : line + " One or more could not start (see the warnings above), so this computer runs the game's " +
                  "own code for it. In multiplayer, check that the other players' logs show the same line.";
        }

        // The same line once a simulation feature has turned itself off during the session (TurnedOff), with that
        // feature OFF; null while none has. Said on every stats interval and in every new game scene, because the
        // feature stays off until the game is restarted and the startup line no longer tells the truth.
        internal static string TurnedOffLine()
        {
            return TurnedOffLine(_started, TurnedOff.Names());
        }

        internal static string TurnedOffLine(bool[] started, string[] off)
        {
            if (off.Length == 0)
            {
                return null;
            }
            bool[] on = new bool[SimulationFeatureNames.Length];
            for (int i = 0; i < on.Length; i++)
            {
                on[i] = started[i] && Array.IndexOf(off, SimulationFeatureNames[i]) < 0;
            }
            bool one = off.Length == 1;
            return FeatureList(on) + " " +
                   string.Join(", ", off) + (one ? " turned itself" : " turned themselves") + " off during this " +
                   "session after an error (see the " + (one ? "warning" : "warnings") + " above), so this computer " +
                   "runs the game's own code for " + (one ? "it" : "them") + ". In multiplayer every player should " +
                   "restart the game before playing on together.";
        }

        // "Simulation features: HaulCache on, RouteMaps OFF, ...", one entry per SimulationFeatureNames entry.
        private static string FeatureList(bool[] on)
        {
            if (on.Length != SimulationFeatureNames.Length)
            {
                throw new ArgumentException($"{on.Length} states for {SimulationFeatureNames.Length} simulation features");
            }
            string[] parts = new string[on.Length];
            for (int i = 0; i < on.Length; i++)
            {
                parts[i] = SimulationFeatureNames[i] + (on[i] ? " on" : " OFF");
            }
            return "Simulation features: " + string.Join(", ", parts) + ".";
        }

        // The settings page's "verify every feature" box: on, every verify mode; off, each back to its .cfg value.
        internal static void SetVerifyAll(bool on)
        {
            Config cfg = _config;
            HaulCache.VerifyEnabled = on || cfg.HaulCacheVerify;
            YielderSearch.VerifyEnabled = on || cfg.YielderSearchVerify;
            PlantWater.VerifyEnabled = on || cfg.PlantWaterVerify;
            DistrictCounts.VerifyEnabled = on || cfg.DistrictCountsVerify;
            WaterMapCopy.VerifyEnabled = on || cfg.WaterMapCopyVerify;
            SoilScans.VerifyEnabled = on || cfg.SoilScansVerify;
            HomeSearch.VerifyEnabled = on || cfg.HomeSearchVerify;
            TerrainSearch.VerifyEnabled = on || cfg.TerrainSearchVerify;
            SaveSnapshot.VerifyEnabled = on || cfg.SaveSnapshotVerify;
            Reachability.VerifyEnabled = on || cfg.ReachabilityVerify;
        }

        internal static Feature CreateTickFeature()
        {
            Type self = typeof(Plugin);
            Feature feature = new Feature { Name = "TickHooks" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableSingletonService.TickAll",
                // Metrics, the UI throttle's tick count and the stats lines. The haul cache's per-tick flush is a
                // required hook of its own on the same method, so it does not depend on this one.
                Required = false,
                Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "TickAll"),
                Prefix = Reflect.Own(self, nameof(TickStartedPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableBucketService..ctor",
                Required = false,
                Target = () => Reflect.FirstConstructor("Timberborn.TickSystem.TickableBucketService"),
                Postfix = Reflect.Own(self, nameof(GameSceneCreatedPostfix))
            });
            return feature;
        }

        private static bool _tickHookFailed;

        private static void TickStartedPrefix()
        {
            try
            {
                TickStarted();
            }
            catch (Exception exception)
            {
                // Only counters and lines: nothing a game tick depends on, so never let it reach the game's loop.
                if (!_tickHookFailed)
                {
                    _tickHookFailed = true;
                    Log.Warning("LateGamePerformance: the per-tick housekeeping threw and is skipped from now on: " + exception);
                }
            }
        }

        private static void TickStarted()
        {
            MetricsDump.OnTickStarted();
            UiThrottle.OnTickStarted();
            if (_config.StatsEveryTicks > 0 && ++_ticksSinceReport >= _config.StatsEveryTicks)
            {
                _ticksSinceReport = 0;
                string timingLine = Timing.TakeStatsLine(_config.StatsEveryTicks);
                if (timingLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {timingLine}");
                }
                string haulLine = HaulCache.TakeStatsLine();
                if (haulLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {haulLine}");
                }
                string yielderLine = YielderSearch.TakeStatsLine();
                if (yielderLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {yielderLine}");
                }
                string terrainLine = TerrainMaps.TakeStatsLine();
                if (terrainLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {terrainLine}");
                }
                string plantWaterLine = PlantWater.TakeStatsLine();
                if (plantWaterLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {plantWaterLine}");
                }
                string districtLine = DistrictCounts.TakeStatsLine();
                if (districtLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {districtLine}");
                }
                foreach (string line in new[]
                         {
                             WaterMapCopy.TakeStatsLine(), SoilScans.TakeStatsLine(), WaterRendering.TakeStatsLine(),
                             SoundListenerSkip.TakeStatsLine(), UiThrottle.TakeStatsLine(), AnimatorCulling.TakeStatsLine(),
                             PhysicsSync.TakeStatsLine(), ShaftAnimators.TakeStatsLine(),
                             TerrainSearch.TakeStatsLine(), IdleEntities.TakeStatsLine(), HomeSearch.TakeStatsLine(),
                             TerrainReach.TakeStatsLine(), BehaviorLog.TakeStatsLine(), WalkerMove.TakeStatsLine(),
                             TickWorkers.TakeStatsLine(), SaveSnapshot.TakeStatsLine(), SaveSnapshot.TakeUnlistedLine(),
                             ParallelTickWait.TakeStatsLine(), Reachability.TakeStatsLine()
                         })
                {
                    if (line != null)
                    {
                        Log.Info($"Last {_config.StatsEveryTicks} ticks. {line}");
                    }
                }
                string routeLine = RouteMaps.TakeStatsLine();
                if (routeLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {routeLine}");
                }
                string catchUpLine = CatchUp.TakeStatsLine();
                if (catchUpLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {catchUpLine}");
                }
                string turnedOffLine = TurnedOffLine();
                if (turnedOffLine != null)
                {
                    Log.Info(turnedOffLine);
                }
                if (_diagnosticsActive && Diagnostics.Enabled)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {Diagnostics.TakeStatsLine()}");
                }
            }
        }

        private static void GameSceneCreatedPostfix()
        {
            // A new game or map editor scene: nothing cached from the previous one may survive.
            HaulCache.Reset();
            BackgroundSave.SceneCreated();
            WaterMapCopy.SceneCreated();
            WaterRendering.SceneCreated();
            TerrainSearch.SceneCreated();
            SoundListenerSkip.SceneCreated();
            IdleEntities.SceneCreated();
            PlantWater.SceneCreated();
            SoilScans.SceneCreated();
            HomeSearch.SceneCreated();
            SaveSnapshot.SceneCreated();
            DistrictCounts.SceneCreated();
            _ticksSinceReport = 0;
            string turnedOffLine = TurnedOffLine();
            if (turnedOffLine != null)
            {
                Log.Info(turnedOffLine);
            }
        }
    }
}
