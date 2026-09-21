using System;
using System.IO;
using System.Reflection;
using Timberborn.Metrics;
using Timberborn.PlatformUtilities;

namespace LateGamePerformance
{
    // The game can time every tickable component, every once-per-tick system and every root behaviour (needs,
    // work, carrying, wandering...). It only does so when launched with -metrics, and only writes the result at
    // the end of a benchmark run. This:
    //
    //  - switches those timers on when RecordTimings = true in the settings file, so no launch option is needed
    //    (-metrics still works too). Up to 0.4.9 this was a box on the settings page; it is a profiling tool, not
    //    something a player needs to see;
    //  - writes the same report during normal play, multiplayer included, every MetricsEveryTicks ticks, then
    //    resets the timers so each file covers one interval.
    //
    // Every building and beaver decides whether to time itself when it is created, from the metrics service's
    // flag. So the flag is set as the service is created and again after it loads (its Load resets it from the
    // command line), before anything is created.
    //
    // The timers (two stopwatch calls around every component tick) are the game's and slow it a little, so this
    // is for a profiling session, not for all the time.
    internal static class MetricsDump
    {
        // RecordTimings in the settings file. Up to 0.4.9 this was a box on the settings page.
        public static bool Requested;

        private static IMetricsService _metricsService;
        private static MethodInfo _setMetricsEnabled;
        private static int _everyTicks;
        private static long _ticks;
        private static long _intervalStartTick;
        private static bool _failed;

        public static Feature CreateFeature(Config config)
        {
            _everyTicks = config.MetricsEveryTicks;
            Type self = typeof(MetricsDump);
            Feature feature = new Feature { Name = "MetricsDump" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "MetricsService.Load",
                Required = true,
                Target = () =>
                {
                    Type service = Reflect.GameType("Timberborn.Metrics.MetricsService");
                    _setMetricsEnabled = Reflect.Setter(service, "MetricsEnabled");
                    if (_setMetricsEnabled == null)
                    {
                        throw new MissingMethodException("MetricsService.MetricsEnabled setter");
                    }
                    return Reflect.Method("Timberborn.Metrics.MetricsService", "Load");
                },
                Postfix = Reflect.Own(self, nameof(MetricsServiceLoadedPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "MetricsService..ctor",
                // Covers anything that reads the flag before the service's own Load has run.
                Required = false,
                Target = () => Reflect.FirstConstructor("Timberborn.Metrics.MetricsService"),
                Postfix = Reflect.Own(self, nameof(MetricsServiceCreatedPostfix))
            });
            return feature;
        }

        // Replaced by the test harness, where the game's folder lookup (a Unity call) is not available.
        public static Func<string> UserDataFolderPath = () => UserDataFolder.Folder;

        public static string Folder => Path.Combine(UserDataFolderPath(), "LateGamePerformance");

        public static void OnTickStarted()
        {
            if (_metricsService == null || _failed || _everyTicks <= 0)
            {
                return;
            }
            _ticks++;
            if (_ticks - _intervalStartTick < _everyTicks)
            {
                return;
            }
            try
            {
                string name = $"metrics {DateTime.Now:yyyy-MM-dd HH\\hmm\\mss\\s} ticks {_intervalStartTick}-{_ticks}";
                // The game appends ".csv" and creates the folder.
                _metricsService.WriteCollectedDataToFile(Path.Combine(Folder, name));
                _metricsService.ResetMetrics();
                Log.Info($"Metrics: wrote '{name}.csv' to {Folder}");
            }
            catch (Exception exception)
            {
                _failed = true;
                Log.Warning("Metrics: writing failed and is off for this session: " + exception.Message);
            }
            _intervalStartTick = _ticks;
        }

        // ReSharper disable InconsistentNaming
        internal static void MetricsServiceCreatedPostfix(object __instance)
        {
            if (Requested && _everyTicks > 0)
            {
                TryForceOn(__instance);
            }
        }

        internal static void MetricsServiceLoadedPostfix(object __instance)
        {
            try
            {
                OnMetricsServiceLoaded(__instance);
            }
            catch (Exception exception)
            {
                // This runs while a save is loading; nothing here may interrupt that.
                _metricsService = null;
                Log.Warning("Metrics: setup failed and is off for this session: " + exception.Message);
            }
        }

        private static void OnMetricsServiceLoaded(object __instance)
        {
            // A new game or map editor scene.
            _ticks = _intervalStartTick = 0;
            _failed = false;
            _metricsService = null;
            IMetricsService service = __instance as IMetricsService;
            if (service == null || _everyTicks <= 0)
            {
                return;
            }
            bool fromLaunchOption = service.MetricsEnabled;
            if (!fromLaunchOption && Requested)
            {
                TryForceOn(__instance);
            }
            if (service.MetricsEnabled)
            {
                _metricsService = service;
                Log.Info($"Metrics: on ({(fromLaunchOption ? "-metrics launch option" : "RecordTimings in the settings file")}). Per-component " +
                         $"timings are written every {_everyTicks} ticks to {Folder}. The game's timers slow it a " +
                         "little; turn this off again when done.");
            }
        }
        // ReSharper restore InconsistentNaming

        private static void TryForceOn(object metricsService)
        {
            try
            {
                _setMetricsEnabled.Invoke(metricsService, new object[] { true });
            }
            catch (Exception exception)
            {
                Log.Warning("Metrics: could not switch the game's timers on: " + exception.Message);
            }
        }
    }
}
