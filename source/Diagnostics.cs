using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using Timberborn.NeedBehaviorSystem;

namespace LateGamePerformance
{
    // Opt-in timers for the code this mod does not change, so the next version can be aimed at whichever part
    // actually costs time in a real colony:
    //   - road flow field fills (every building's route map is thrown away when any connected road changes)
    //   - need selection (DistrictNeedBehaviorService.PickBestAction prices a round trip per provider)
    //   - walker path finding (Walker.FindPath, once per new destination)
    //   - the two A* searches the game falls back to when no route map answers: a beaver standing off the road
    //     network (a field, a forest) pricing a building, or walking to a random spot. Both run on the main
    //     thread inside the beaver's tick, and a search for an unreachable place explores the whole area.
    // Adds a prefix+postfix to hot methods, so leave it off for normal play.
    internal static class Diagnostics
    {
        private const string RoadFlowFieldGeneratorType = "Timberborn.Navigation.RoadFlowFieldGenerator";
        private const string AccessFlowFieldType = "Timberborn.Navigation.AccessFlowField";
        private const string PathFlowFieldType = "Timberborn.Navigation.PathFlowField";
        private const string RoadAStarType = "Timberborn.Navigation.RoadAStarPathfinder";
        private const string TerrainAStarType = "Timberborn.Navigation.TerrainAStarPathfinder";
        private const string WalkerType = "Timberborn.WalkingSystem.Walker";

        private static Func<object, bool> _isFilled;
        private static Func<object, int> _numberOfNodes;
        private static Func<object, ICollection> _pathNodes;
        private static Func<object, int> _pathStart;
        private static Func<object, bool> _pathFullyFilled;

        private static long _fills;
        private static long _fillNodes;
        private static long _fillStopwatchTicks;

        private static readonly Timer NeedPicks = new Timer();
        private static readonly Timer WalkerPaths = new Timer();
        private static readonly SearchTimer RoadSearches = new SearchTimer();
        private static readonly SearchTimer TerrainSearches = new SearchTimer();

        // Calls, time and the slowest single call of one method (main thread only).
        private sealed class Timer
        {
            public long Calls;
            public long StopwatchTicks;
            public long LongestStopwatchTicks;

            public void Add(long stopwatchTicks)
            {
                Calls++;
                StopwatchTicks += stopwatchTicks;
                if (stopwatchTicks > LongestStopwatchTicks)
                {
                    LongestStopwatchTicks = stopwatchTicks;
                }
            }

            public string Text(string what)
            {
                return $"{what} {Calls} in {Ms(StopwatchTicks):0.0} ms (longest {Ms(LongestStopwatchTicks):0.00} ms)";
            }

            public void Reset()
            {
                Calls = StopwatchTicks = LongestStopwatchTicks = 0;
            }
        }

        // An A* call that finds its answer in the last search (same start node) costs nothing; only a call that
        // cleared and refilled the flow field is a search. The field's node count says how far it explored, and
        // "explored everything reachable" is what an unreachable target costs: the whole area is walked.
        private sealed class SearchTimer
        {
            public readonly Timer All = new Timer();
            public long Searches;
            public long SearchedNodes;
            public long FullyExplored;

            public string Text(string what)
            {
                return $"{All.Text(what + " calls")}, of which {Searches} searches explored {SearchedNodes} nodes " +
                       $"and {FullyExplored} explored everything reachable";
            }

            public void Reset()
            {
                All.Reset();
                Searches = SearchedNodes = FullyExplored = 0;
            }
        }

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
                Prefix = Reflect.Own(self, nameof(StampPrefix)),
                Postfix = Reflect.Own(self, nameof(NeedPickPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "Walker.FindPath",
                Required = false,
                // Told apart by parameter count: a name lookup alone reports it as ambiguous.
                Target = () => Reflect.Overload(WalkerType, "FindPath", 1),
                Prefix = Reflect.Own(self, nameof(StampPrefix)),
                Postfix = Reflect.Own(self, nameof(WalkerPathPostfix))
            });
            // Both pathfinders have two overloads: one destination, and a list of destinations (out int).
            foreach (int parameters in new[] { 4, 5 })
            {
                int count = parameters;
                feature.Patches.Add(new PatchSpec
                {
                    Name = $"RoadAStarPathfinder.FillFlowFieldWithPath ({count} parameters)",
                    Required = false,
                    Target = () =>
                    {
                        PreparePathFlowFieldAccess();
                        return Reflect.Overload(RoadAStarType, "FillFlowFieldWithPath", count);
                    },
                    Prefix = Reflect.Own(self, nameof(SearchPrefix)),
                    Postfix = Reflect.Own(self, nameof(RoadSearchPostfix))
                });
                feature.Patches.Add(new PatchSpec
                {
                    Name = $"TerrainAStarPathfinder.FillFlowFieldWithPath ({count} parameters)",
                    Required = false,
                    Target = () =>
                    {
                        PreparePathFlowFieldAccess();
                        return Reflect.Overload(TerrainAStarType, "FillFlowFieldWithPath", count);
                    },
                    Prefix = Reflect.Own(self, nameof(SearchPrefix)),
                    Postfix = Reflect.Own(self, nameof(TerrainSearchPostfix))
                });
            }
            return feature;
        }

        private static void PreparePathFlowFieldAccess()
        {
            if (_pathNodes != null)
            {
                return;
            }
            Type pathFlowField = Reflect.GameType(PathFlowFieldType);
            _pathNodes = Reflect.FieldGetter<ICollection>(pathFlowField, "_nodes");
            _pathStart = Reflect.FieldGetter<int>(pathFlowField, "_startNodeId");
            _pathFullyFilled = Reflect.FieldGetter<bool>(pathFlowField, "_fullyFilled");
        }

        public static string TakeStatsLine()
        {
            string line =
                $"Diagnostics: road flow field fills {_fills} ({_fillNodes} nodes) in {Ms(_fillStopwatchTicks):0.0} ms " +
                "summed over threads; " +
                NeedPicks.Text("need picks") + "; " +
                WalkerPaths.Text("walker paths") + "; " +
                RoadSearches.Text("road A*") + "; " +
                TerrainSearches.Text("terrain A*");
            _fills = _fillNodes = _fillStopwatchTicks = 0;
            NeedPicks.Reset();
            WalkerPaths.Reset();
            RoadSearches.Reset();
            TerrainSearches.Reset();
            return line;
        }

        private static double Ms(long stopwatchTicks)
        {
            return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
        }

        // What a search prefix remembers, so the postfix can tell a search from an answer out of the last one.
        internal struct SearchState
        {
            public long Stamp;
            public int StartNode;
            public int Nodes;
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

        private static void StampPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void NeedPickPostfix(long __state)
        {
            NeedPicks.Add(Stopwatch.GetTimestamp() - __state);
        }

        private static void WalkerPathPostfix(long __state)
        {
            WalkerPaths.Add(Stopwatch.GetTimestamp() - __state);
        }

        private static void SearchPrefix(object flowField, out SearchState __state)
        {
            __state = new SearchState
            {
                Stamp = Stopwatch.GetTimestamp(), StartNode = _pathStart(flowField), Nodes = _pathNodes(flowField).Count
            };
        }

        private static void RoadSearchPostfix(object flowField, SearchState __state)
        {
            SearchPostfix(RoadSearches, flowField, __state);
        }

        private static void TerrainSearchPostfix(object flowField, SearchState __state)
        {
            SearchPostfix(TerrainSearches, flowField, __state);
        }
        // ReSharper restore InconsistentNaming

        private static void SearchPostfix(SearchTimer timer, object flowField, SearchState state)
        {
            timer.All.Add(Stopwatch.GetTimestamp() - state.Stamp);
            int nodes = _pathNodes(flowField).Count;
            // A search clears the field and fills it again; an answer from the last search leaves it alone. A
            // repeat search from the same start that happens to end on the same count is missed, which is rare.
            if (nodes != state.Nodes || _pathStart(flowField) != state.StartNode)
            {
                timer.Searches++;
                timer.SearchedNodes += nodes;
                if (_pathFullyFilled(flowField))
                {
                    timer.FullyExplored++;
                }
            }
        }
    }
}
