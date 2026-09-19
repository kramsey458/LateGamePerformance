using System;
using System.IO;
using Timberborn.Metrics;
using Timberborn.PlatformUtilities;

namespace LateGamePerformance
{
    // The game can time every tickable component, every once-per-tick system and every root behaviour (needs,
    // work, carrying, wandering...). It does so when launched with -metrics, but only writes the result at the
    // end of a benchmark run. This writes the same report during normal play, multiplayer included, every
    // MetricsEveryTicks ticks, then resets the timers so each file covers one interval.
    //
    // Nothing here runs unless the game was launched with -metrics. The timers themselves (two stopwatch calls
    // around every component tick) are the game's and do slow it a little, so use it for a profiling session,
    // not all the time.
    internal static class MetricsDump
    {
        private static IMetricsService _metricsService;
        private static int _everyTicks;
        private static long _ticks;
        private static long _intervalStartTick;
        private static bool _failed;

        public static Feature CreateFeature(Config config)
        {
            _everyTicks = config.MetricsEveryTicks;
            Feature feature = new Feature { Name = "MetricsDump" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "MetricsService.Load",
                Required = true,
                Target = () => Reflect.Method("Timberborn.Metrics.MetricsService", "Load"),
                Postfix = Reflect.Own(typeof(MetricsDump), nameof(MetricsServiceLoadedPostfix))
            });
            return feature;
        }

        public static string Folder => Path.Combine(UserDataFolder.Folder, "LateGamePerformance");

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

        // ReSharper disable once InconsistentNaming
        private static void MetricsServiceLoadedPostfix(object __instance)
        {
            // A new game or map editor scene.
            _ticks = _intervalStartTick = 0;
            _failed = false;
            IMetricsService service = __instance as IMetricsService;
            if (service != null && service.MetricsEnabled && _everyTicks > 0)
            {
                _metricsService = service;
                Log.Info($"Metrics: on. Per-component timings are written every {_everyTicks} ticks to {Folder}. " +
                         "The game's timers slow it a little; remove -metrics from the launch options when done.");
            }
            else
            {
                _metricsService = null;
                Log.Info("Metrics: off. To record what each part of a tick costs, add -metrics to the game's Steam " +
                         "launch options (Library > Timberborn > Properties > General) and restart.");
            }
        }
    }
}
