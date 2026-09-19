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

        private static Config _config = new Config();
        private static bool _diagnosticsActive;
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
            if (config.HaulCache && HaulCache.CreateFeature(config).Apply(HarmonyId))
            {
                HaulCache.Activate();
            }
            if (config.RouteMaps && RouteMaps.CreateFeature(config).Apply(HarmonyId))
            {
                RouteMaps.Activate();
                // If any gate cannot be installed, fall back to waiting for the whole batch, which needs none.
                bool background = config.RouteMapsBackground && RouteMaps.CreateBackgroundFeature().Apply(HarmonyId);
                RouteMaps.SetBackground(background);
                Log.Info($"RouteMaps: {RouteMaps.WorkerCount} worker threads, " +
                         (background ? "rebuilding in the background." : "main thread waits for each rebuild."));
            }
            if (config.Timing && Timing.CreateFeature().Apply(HarmonyId))
            {
                Timing.Activate();
            }
            if (config.SaveTiming)
            {
                SaveTiming.CreateFeature().Apply(HarmonyId);
            }
            if (config.MetricsEveryTicks > 0)
            {
                MetricsDump.CreateFeature(config).Apply(HarmonyId);
            }
            if (config.Diagnostics)
            {
                _diagnosticsActive = Diagnostics.CreateFeature().Apply(HarmonyId);
            }
            CreateTickFeature().Apply(HarmonyId);
            if (config.GcReport)
            {
                GcReport.Write();
            }
        }

        internal static Feature CreateTickFeature()
        {
            Type self = typeof(Plugin);
            Feature feature = new Feature { Name = "TickHooks" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableSingletonService.TickAll",
                // Without the tick hook the haul cache has no periodic flush; it still has its change hooks.
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

        private static void TickStartedPrefix()
        {
            HaulCache.OnTickStarted();
            MetricsDump.OnTickStarted();
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
                string routeLine = RouteMaps.TakeStatsLine();
                if (routeLine != null)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {routeLine}");
                }
                if (_diagnosticsActive)
                {
                    Log.Info($"Last {_config.StatsEveryTicks} ticks. {Diagnostics.TakeStatsLine()}");
                }
            }
        }

        private static void GameSceneCreatedPostfix()
        {
            // A new game or map editor scene: nothing cached from the previous one may survive.
            HaulCache.Reset();
            _ticksSinceReport = 0;
        }
    }
}
