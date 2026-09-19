using System;
using System.Diagnostics;
using System.Threading;
using Timberborn.NeedBehaviorSystem;

namespace LateGamePerformance
{
    // Opt-in timers for the two suspects this version does not change, so the next version can be aimed at
    // whichever one actually costs time in a real colony:
    //   - road flow field fills (every building's route map is thrown away when any connected road changes)
    //   - need selection (DistrictNeedBehaviorService.PickBestAction prices a round trip per provider)
    // Adds a prefix+postfix to two hot methods, so leave it off for normal play.
    internal static class Diagnostics
    {
        private const string RoadFlowFieldGeneratorType = "Timberborn.Navigation.RoadFlowFieldGenerator";
        private const string AccessFlowFieldType = "Timberborn.Navigation.AccessFlowField";

        private static Func<object, bool> _isFilled;
        private static Func<object, int> _numberOfNodes;

        private static long _fills;
        private static long _fillNodes;
        private static long _fillStopwatchTicks;
        private static long _needPicks;
        private static long _needPickStopwatchTicks;

        public static Feature CreateFeature()
        {
            Type self = typeof(Diagnostics);
            Feature feature = new Feature { Name = "Diagnostics" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "RoadFlowFieldGenerator.FillFlowField",
                Required = false,
                Target = () =>
                {
                    Type flowField = Reflect.GameType(AccessFlowFieldType);
                    _isFilled = Reflect.PropertyGetter<bool>(flowField, "IsFilled");
                    _numberOfNodes = Reflect.PropertyGetter<int>(flowField, "NumberOfNodes");
                    return Reflect.Method(RoadFlowFieldGeneratorType, "FillFlowField");
                },
                Prefix = Reflect.Own(self, nameof(FillPrefix)),
                Postfix = Reflect.Own(self, nameof(FillPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictNeedBehaviorService.PickBestAction",
                Required = false,
                Target = () => HarmonyLib.AccessTools.Method(typeof(DistrictNeedBehaviorService), "PickBestAction"),
                Prefix = Reflect.Own(self, nameof(NeedPickPrefix)),
                Postfix = Reflect.Own(self, nameof(NeedPickPostfix))
            });
            return feature;
        }

        public static string TakeStatsLine()
        {
            double fillMs = _fillStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double needMs = _needPickStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line =
                $"Diagnostics: road flow field fills {_fills} ({_fillNodes} nodes) in {fillMs:0.0} ms summed over threads; " +
                $"need picks {_needPicks} in {needMs:0.0} ms";
            _fills = _fillNodes = _fillStopwatchTicks = _needPicks = _needPickStopwatchTicks = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        private static void FillPrefix(object flowField, out long __state)
        {
            // FillFlowField returns immediately for an already filled field; only real fills are timed.
            __state = _isFilled(flowField) ? 0 : Stopwatch.GetTimestamp();
        }

        private static void FillPostfix(object flowField, long __state)
        {
            if (__state != 0)
            {
                // Fills also run on RouteMaps worker threads.
                Interlocked.Add(ref _fillStopwatchTicks, Stopwatch.GetTimestamp() - __state);
                Interlocked.Add(ref _fillNodes, _numberOfNodes(flowField));
                Interlocked.Increment(ref _fills);
            }
        }

        private static void NeedPickPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void NeedPickPostfix(long __state)
        {
            _needPickStopwatchTicks += Stopwatch.GetTimestamp() - __state;
            _needPicks++;
        }
        // ReSharper restore InconsistentNaming
    }
}
