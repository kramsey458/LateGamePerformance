using System;
using HarmonyLib;
using Timberborn.StatusSystem;

namespace LateGamePerformance
{
    // Some of the game's per-frame systems redo all their work every frame although only the screen reads it:
    //   - StatusAggregator goes through every status in the colony to fill the alert lists the top bar and the
    //     alert buttons read (about 0.35 ms per frame in a large colony). It ran every fourth frame from 0.4.17
    //     (still 114 us per frame on average in the logged 360-beaver colony) and runs every sixteenth now; a
    //     status that appears or clears reaches the alert count up to sixteen frames (about a quarter of a second)
    //     later. Removing a subject still updates the lists at once, as in the game.
    //   - DynamicStatusAggregator does the same for the alerts that carry a value (a timed activation's days
    //     left, a tragic death), 48 us per frame in the logged colony. It runs every fourth frame (0.4.28), two
    //     frames apart from the one above. Its lists are read only by the alert panel's rows (count, value,
    //     blinking and the warning sound) and by the alert button that selects the next subject; adding a status
    //     and removing a subject still update them at once, and the value shown is read live from the status.
    //   - EntityPanel refreshes every fragment of the selected entity's panel every frame, the largest single
    //     source of garbage among the game's systems (about 120 KB/s). It runs every second frame now, and always
    //     on the frame a different entity is shown.
    //   - TopBarPanel refreshes its counters every frame (67 us). Every fourth frame now.
    //   - EntityReachabilityStatus is a tick component that the game switches on only while its entity is
    //     selected; it then asks every reachability part of that entity on every tick (the selected beaver in
    //     the logged session cost up to ten times an ordinary one). It runs every eighth tick now. Selection is
    //     per player, so this status was never simulation state.
    // All are read by the user interface only, never by the simulation.
    internal static class UiThrottle
    {
        public const int StatusEveryFrames = 16;
        public const int DynamicStatusEveryFrames = 4;
        public const int PanelEveryFrames = 2;
        public const int TopBarEveryFrames = 4;
        public const int ReachabilityEveryTicks = 8;

        private const string EntityPanelType = "Timberborn.EntityPanelSystem.EntityPanel";
        private const string TopBarType = "Timberborn.TopBarSystem.TopBarPanel";
        private const string ReachabilityType = "Timberborn.BuildingsReachability.EntityReachabilityStatus";

        // Swappable for the test harness, which has no Unity.
        internal static Func<int> FrameCount = () => UnityEngine.Time.frameCount;

        private static Func<object, object> _shownEntity;
        private static bool _active;
        private static object _lastShown;
        private static long _statusRuns;
        private static long _statusSkips;
        private static long _dynamicStatusRuns;
        private static long _dynamicStatusSkips;
        private static long _panelRuns;
        private static long _panelSkips;
        private static long _topBarSkips;
        private static long _reachabilitySkips;
        private static long _ticks;

        internal static bool StatusDue(int frame)
        {
            return frame % StatusEveryFrames == 0;
        }

        // Two frames after the lists above would run, so the two never share a frame.
        internal static bool DynamicStatusDue(int frame)
        {
            return frame % DynamicStatusEveryFrames == 2;
        }

        internal static bool PanelDue(int frame, bool entityChanged)
        {
            return entityChanged || frame % PanelEveryFrames == 0;
        }

        internal static bool TopBarDue(int frame)
        {
            return frame % TopBarEveryFrames == 0;
        }

        internal static bool ReachabilityDue(long tick)
        {
            return tick % ReachabilityEveryTicks == 0;
        }

        // Counted from the tick hook, so the reachability status runs on the same ticks whatever the frame rate.
        internal static void OnTickStarted()
        {
            _ticks++;
        }

        public static Feature CreateFeature()
        {
            Type self = typeof(UiThrottle);
            Feature feature = new Feature { Name = "UiThrottle" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "StatusAggregator.UpdateSingleton",
                Required = false,
                Target = () => AccessTools.Method(typeof(StatusAggregator), "UpdateSingleton"),
                Prefix = Reflect.Own(self, nameof(StatusPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DynamicStatusAggregator.UpdateSingleton",
                Required = false,
                Target = () => AccessTools.Method(typeof(DynamicStatusAggregator), "UpdateSingleton"),
                Prefix = Reflect.Own(self, nameof(DynamicStatusPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "EntityPanel.UpdateSingleton",
                Required = false,
                Target = () =>
                {
                    _shownEntity = Reflect.FieldGetter<object>(Reflect.GameType(EntityPanelType), "_shownEntity");
                    return Reflect.Method(EntityPanelType, "UpdateSingleton");
                },
                Prefix = Reflect.Own(self, nameof(PanelPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TopBarPanel.UpdateSingleton",
                Required = false,
                Target = () => Reflect.Method(TopBarType, "UpdateSingleton"),
                Prefix = Reflect.Own(self, nameof(TopBarPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "EntityReachabilityStatus.Tick",
                Required = false,
                Target = () => Reflect.Method(ReachabilityType, "Tick"),
                Prefix = Reflect.Own(self, nameof(ReachabilityPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _lastShown = null;
            _active = true;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _statusRuns + _statusSkips + _panelRuns + _panelSkips == 0)
            {
                return null;
            }
            string line = $"UiThrottle: status alert lists rebuilt in {_statusRuns} of {_statusRuns + _statusSkips} frames " +
                          $"(alerts with a value in {_dynamicStatusRuns} of {_dynamicStatusRuns + _dynamicStatusSkips}); " +
                          $"the entity panel refreshed in {_panelRuns} of {_panelRuns + _panelSkips} frames it was shown; " +
                          $"top bar refreshes left out {_topBarSkips}; selected entities' reachability checks left out {_reachabilitySkips}";
            _statusRuns = _statusSkips = _panelRuns = _panelSkips = _topBarSkips = _reachabilitySkips = 0;
            _dynamicStatusRuns = _dynamicStatusSkips = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        private static bool StatusPrefix()
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                if (StatusDue(FrameCount()))
                {
                    _statusRuns++;
                    return true;
                }
                _statusSkips++;
                return false;
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("UiThrottle failed and is off for this session: " + exception);
                return true;
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static bool DynamicStatusPrefix()
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                if (DynamicStatusDue(FrameCount()))
                {
                    _dynamicStatusRuns++;
                    return true;
                }
                _dynamicStatusSkips++;
                return false;
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("UiThrottle failed and is off for this session: " + exception);
                return true;
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static bool TopBarPrefix()
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                if (TopBarDue(FrameCount()))
                {
                    return true;
                }
                _topBarSkips++;
                return false;
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("UiThrottle failed and is off for this session: " + exception);
                return true;
            }
        }

        [HarmonyPriority(Priority.Last)]
        private static bool ReachabilityPrefix()
        {
            if (!_active)
            {
                return true;
            }
            if (ReachabilityDue(_ticks))
            {
                return true;
            }
            _reachabilitySkips++;
            return false;
        }

        [HarmonyPriority(Priority.Last)]
        private static bool PanelPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                object shown = _shownEntity(__instance);
                bool changed = !ReferenceEquals(shown, _lastShown);
                _lastShown = shown;
                if (shown == null)
                {
                    // Nothing shown: the game's method returns at once anyway.
                    return true;
                }
                if (PanelDue(FrameCount(), changed))
                {
                    _panelRuns++;
                    return true;
                }
                _panelSkips++;
                return false;
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("UiThrottle failed and is off for this session: " + exception);
                return true;
            }
        }
        // ReSharper restore InconsistentNaming
    }
}
