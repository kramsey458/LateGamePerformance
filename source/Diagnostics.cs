using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.NeedBehaviorSystem;

namespace LateGamePerformance
{
    // Opt-in timers for the code this mod does not change, so the next version can be aimed at whichever part
    // actually costs time in a real colony:
    //   - road flow field fills (every building's route map is thrown away when any connected road changes)
    //   - need selection (DistrictNeedBehaviorService.PickBestAction prices a round trip per provider)
    //   - walker path finding (Walker.FindPath, once per new destination), and its parts (0.4.31): where the walk
    //     starts, the destination's search (road and terrain maps, the A* fallbacks), reading the path out of a map,
    //     handing the path to the path follower, the path's bounds and the StartedNewPath subscriber. The 0.4.28
    //     verify session measured 1.8 ms per tick of walker paths with only 0.12 ms of it in the A* searches.
    //   - posts on the game's EventBus (0.4.31), by event type: each handler is called through reflection, and
    //     whether that costs anything worth a change depends on how many posts a tick makes.
    //   - the two A* searches the game falls back to when no route map answers: a beaver standing off the road
    //     network (a field, a forest) pricing a building, or walking to a random spot. Both run on the main
    //     thread inside the beaver's tick, and a search for an unreachable place explores the whole area.
    // Adds a prefix+postfix to hot methods. The patches are always installed; Enabled (the settings page box
    // "Diagnostics timers", or the .cfg key) says whether they measure. Off, each is one boolean check.
    internal static class Diagnostics
    {
        internal static volatile bool Enabled;

        private const string RoadFlowFieldGeneratorType = "Timberborn.Navigation.RoadFlowFieldGenerator";
        private const string AccessFlowFieldType = "Timberborn.Navigation.AccessFlowField";
        private const string PathFlowFieldType = "Timberborn.Navigation.PathFlowField";
        private const string RoadAStarType = "Timberborn.Navigation.RoadAStarPathfinder";
        private const string TerrainAStarType = "Timberborn.Navigation.TerrainAStarPathfinder";
        private const string WalkerType = "Timberborn.WalkingSystem.Walker";
        private const string FlowFieldPathFinderType = "Timberborn.Navigation.FlowFieldPathFinder";
        private const string EventBusType = "Timberborn.SingletonSystem.EventBus";

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
        private static readonly Timer CitizenAssigns = new Timer();
        private static readonly Timer CutOffChecks = new Timer();
        private static readonly SearchTimer RoadSearches = new SearchTimer();
        private static readonly SearchTimer TerrainSearches = new SearchTimer();
        // The parts of Walker.FindPath, counted only inside one (on the thread running it).
        [ThreadStatic] private static bool _inWalkerPath;
        private static readonly Timer PathStarts = new Timer();
        private static readonly Timer DestinationSearches = new Timer();
        private static readonly Timer PathReads = new Timer();
        private static readonly Timer PathHandOvers = new Timer();
        private static readonly Timer PathBounds = new Timer();
        private static readonly Timer NewPathEvents = new Timer();
        // EventBus posts by event type (main thread), and the posts made inside another post.
        private static int _postDepth;
        private static readonly Dictionary<Type, Timer> Posts = new Dictionary<Type, Timer>();
        private static readonly Timer AllPosts = new Timer();
        private static long _nestedPosts;
        // The thread the mod was loaded on, Unity's main thread.
        private static int _mainThread;

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
            _mainThread = Thread.CurrentThread.ManagedThreadId;
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
                Prefix = Reflect.Own(self, nameof(WalkerPathPrefix)),
                Postfix = Reflect.Own(self, nameof(WalkerPathPostfix))
            });
            AddPart(feature, "WalkerPathStart.GetPathStart", () => Reflect.Method("Timberborn.WalkingSystem.WalkerPathStart", "GetPathStart"),
                nameof(PathStartPostfix));
            AddPart(feature, "AccessibleDestination.FindPath", () => Reflect.Method("Timberborn.WalkingSystem.AccessibleDestination", "FindPath"),
                nameof(DestinationPostfix));
            AddPart(feature, "PositionDestination.FindPath", () => Reflect.Method("Timberborn.WalkingSystem.PositionDestination", "FindPath"),
                nameof(DestinationPostfix));
            // Five overloads, told apart by their place in the class.
            for (int i = 0; i < 5; i++)
            {
                int index = i;
                AddPart(feature, $"FlowFieldPathFinder.FindPathInFlowField ({index + 1} of 5)", () => FlowFieldReads()[index],
                    nameof(PathReadPostfix));
            }
            AddPart(feature, "PathFollower.StartMovingAlongPath",
                () => Reflect.Method("Timberborn.CharacterMovementSystem.PathFollower", "StartMovingAlongPath"), nameof(HandOverPostfix));
            AddPart(feature, "Walker.RecalculatePathBounds", () => Reflect.Method(WalkerType, "RecalculatePathBounds"), nameof(BoundsPostfix));
            AddPart(feature, "RunningStateUpdater.OnStartedNewPath",
                () => Reflect.Method("Timberborn.WalkingSystem.RunningStateUpdater", "OnStartedNewPath"), nameof(NewPathEventPostfix));
            feature.Patches.Add(new PatchSpec
            {
                Name = "EventBus.Post",
                Required = false,
                Target = () => Reflect.Method(EventBusType, "Post"),
                Prefix = Reflect.Own(self, nameof(PostPrefix)),
                Postfix = Reflect.Own(self, nameof(PostPostfix))
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
            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictCitizenAssigner.AssignToClosestDistrict",
                Required = false,
                Target = () => Reflect.Method("Timberborn.GameDistricts.DistrictCitizenAssigner", "AssignToClosestDistrict"),
                Prefix = Reflect.Own(self, nameof(StampPrefix)),
                Postfix = Reflect.Own(self, nameof(CitizenAssignPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DistrictCitizenAssigner.UnassignCharactersCutOffFromTheirDistricts",
                Required = false,
                Target = () => Reflect.Method("Timberborn.GameDistricts.DistrictCitizenAssigner", "UnassignCharactersCutOffFromTheirDistricts"),
                Prefix = Reflect.Own(self, nameof(StampPrefix)),
                Postfix = Reflect.Own(self, nameof(CutOffPostfix))
            });
            return feature;
        }

        private static void AddPart(Feature feature, string name, Func<MethodBase> target, string postfix)
        {
            feature.Patches.Add(new PatchSpec
            {
                Name = name,
                Required = false,
                Target = target,
                Prefix = Reflect.Own(typeof(Diagnostics), nameof(PartPrefix)),
                Postfix = Reflect.Own(typeof(Diagnostics), postfix)
            });
        }

        internal static List<MethodInfo> FlowFieldReads()
        {
            List<MethodInfo> methods = Reflect.GameType(FlowFieldPathFinderType)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "FindPathInFlowField").ToList();
            methods.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            return methods;
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
                WalkerPaths.Text("walker paths") + " = " + PathStarts.Text("path starts") + " + " +
                DestinationSearches.Text("destination searches") + " (of which " + PathReads.Text("paths read out of a map") +
                ") + " + PathHandOvers.Text("paths handed to the follower") + " + " + PathBounds.Text("path bounds") + " + " +
                NewPathEvents.Text("new path events") + " + the rest; " +
                PostsText() + "; " +
                RoadSearches.Text("road A*") + "; " +
                TerrainSearches.Text("terrain A*") + "; " +
                CitizenAssigns.Text("citizens without a district looked for one") + "; " +
                CutOffChecks.Text("checks of every citizen for a cut-off district");
            string markers = UnityMarkers.TakeStatsText();
            if (markers != null)
            {
                line += "; " + markers;
            }
            _fills = _fillNodes = _fillStopwatchTicks = 0;
            NeedPicks.Reset();
            WalkerPaths.Reset();
            PathStarts.Reset();
            DestinationSearches.Reset();
            PathReads.Reset();
            PathHandOvers.Reset();
            PathBounds.Reset();
            NewPathEvents.Reset();
            Posts.Clear();
            AllPosts.Reset();
            _nestedPosts = 0;
            _postDepth = 0;
            CitizenAssigns.Reset();
            CutOffChecks.Reset();
            RoadSearches.Reset();
            TerrainSearches.Reset();
            return line;
        }

        // "event posts 120 in 3.1 ms (longest 0.40 ms), 4 inside another post; most time: X 50 in 2.0 ms (...), ..."
        private static string PostsText()
        {
            string text = AllPosts.Text("event posts") + $", {_nestedPosts} inside another post";
            if (Posts.Count == 0)
            {
                return text;
            }
            return text + "; most time: " + string.Join(", ", Posts
                .OrderByDescending(pair => pair.Value.StopwatchTicks).Take(6)
                .Select(pair => pair.Value.Text(pair.Key.Name)));
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
            __state = !Enabled || _isFilled(flowField) ? 0 : Stopwatch.GetTimestamp();
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
            __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        private static void NeedPickPostfix(long __state)
        {
            if (__state != 0)
            {
                NeedPicks.Add(Stopwatch.GetTimestamp() - __state);
            }
        }

        private static void WalkerPathPrefix(out long __state)
        {
            __state = Enabled ? Stopwatch.GetTimestamp() : 0;
            _inWalkerPath = __state != 0;
        }

        private static void WalkerPathPostfix(long __state)
        {
            _inWalkerPath = false;
            if (__state != 0)
            {
                WalkerPaths.Add(Stopwatch.GetTimestamp() - __state);
            }
        }

        // A part of Walker.FindPath: timed only inside one. (A FindPath that threw leaves the flag set until the next
        // one; that only misfiles a part or two, in a measurement.)
        private static void PartPrefix(out long __state)
        {
            __state = Enabled && _inWalkerPath ? Stopwatch.GetTimestamp() : 0;
        }

        private static void PathStartPostfix(long __state) => AddPart(PathStarts, __state);

        private static void DestinationPostfix(long __state) => AddPart(DestinationSearches, __state);

        private static void PathReadPostfix(long __state) => AddPart(PathReads, __state);

        private static void HandOverPostfix(long __state) => AddPart(PathHandOvers, __state);

        private static void BoundsPostfix(long __state) => AddPart(PathBounds, __state);

        private static void NewPathEventPostfix(long __state) => AddPart(NewPathEvents, __state);

        private static void AddPart(Timer timer, long state)
        {
            if (state != 0)
            {
                timer.Add(Stopwatch.GetTimestamp() - state);
            }
        }

        private static bool OnMainThread()
        {
            return Thread.CurrentThread.ManagedThreadId == _mainThread;
        }

        private static void PostPrefix(out long __state)
        {
            if (!Enabled || !OnMainThread())
            {
                __state = 0;
                return;
            }
            if (_postDepth > 0)
            {
                _nestedPosts++;
            }
            _postDepth++;
            __state = Stopwatch.GetTimestamp();
        }

        // Inclusive: a post made inside another counts in both, and only the outer one in the total. A post that threw
        // leaves the depth one too high until the next stats line resets it; that only misfiles posts in a measurement.
        private static void PostPostfix(object eventObject, long __state)
        {
            if (__state == 0)
            {
                return;
            }
            long elapsed = Stopwatch.GetTimestamp() - __state;
            if (_postDepth > 0)
            {
                _postDepth--;
            }
            if (_postDepth == 0)
            {
                AllPosts.Add(elapsed);
            }
            Type type = eventObject?.GetType();
            if (type == null)
            {
                return;
            }
            if (!Posts.TryGetValue(type, out Timer timer))
            {
                timer = new Timer();
                Posts[type] = timer;
            }
            timer.Add(elapsed);
        }

        private static void CitizenAssignPostfix(long __state)
        {
            if (__state != 0)
            {
                CitizenAssigns.Add(Stopwatch.GetTimestamp() - __state);
            }
        }

        private static void CutOffPostfix(long __state)
        {
            if (__state != 0)
            {
                CutOffChecks.Add(Stopwatch.GetTimestamp() - __state);
            }
        }

        private static void SearchPrefix(object flowField, out SearchState __state)
        {
            if (!Enabled)
            {
                __state = default;
                return;
            }
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

        // TerrainSearch's kept searches (0.4.30) run the terrain A* without going through
        // TerrainAStarPathfinder.FillFlowFieldWithPath; it reports each one here, so the terrain A* figures still count
        // every search.
        internal static void TerrainSearchStarting(object flowField, out SearchState state)
        {
            if (_pathNodes == null)
            {
                state = default;
                return;
            }
            SearchPrefix(flowField, out state);
        }

        internal static void TerrainSearchDone(object flowField, SearchState state)
        {
            SearchPostfix(TerrainSearches, flowField, state);
        }

        private static void SearchPostfix(SearchTimer timer, object flowField, SearchState state)
        {
            if (state.Stamp == 0)
            {
                return;
            }
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
