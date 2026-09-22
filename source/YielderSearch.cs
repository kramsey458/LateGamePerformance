using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Timberborn.InventorySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.Navigation;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using UnityEngine;

namespace LateGamePerformance
{
    // Every time a lumberjack (or a gatherer, or a farmer looking for a harvest) looks for work, the game walks
    // every candidate plant, and for each one first looks up the path distance from the building and only then
    // asks whether the plant has anything to take (YielderFinder.FindLivingYielderWithoutAccessible feeding
    // ClosestYielderFinder). For lumberjacks the candidates are every unreserved marked tree on the map, more
    // than a thousand in a late game colony. Measured: 3.8 ms per tick, 22% of all tick time, on a fast computer.
    //
    // What the game does with each candidate, in order:
    //   1. distance lookup; a plant that cannot be reached is dropped and plays no further part
    //   2. "found something" becomes true if the plant is yielding, or alive
    //   3. if it is yielding, it competes for closest plant of its good
    // and the answer is: no plant in range (nothing found), or the closest plants tried in order.
    //
    // So the distance of a plant that is not yielding matters for one thing only: whether it can be reached, and
    // that only until "found something" is true. After that, a plant that is not yielding cannot change the
    // answer whatever its distance is, and LazyCandidates leaves it out. The result is the game's result, not an
    // approximation.
    //
    // The same holds for a yielding plant whose good the building cannot take (0.4.11). The game's last step tries
    // the closest plant of each good in order of distance and takes the first whose carry amount is above zero.
    // That amount is min(what the worker can lift, what the plant yields, the room left in the building for
    // that good), and the first two are at least 1 for any yielding plant, so it is zero exactly when the
    // building has no room for the good, whichever plant it is. A good with no room is passed over whatever its
    // closest plant's distance, so that distance need not be known. This is the case that was measured: all 20
    // lumberjack flags of a late game colony were full (20 logs each, read from the save), so 18 lumberjacks
    // each asked again on every decision, and every time the game looked up the distance to about 1300 grown
    // trees to conclude "nothing to do": 3.8 ms per tick, 22% of all tick time. Plants whose good can be taken are looked
    // up exactly as before, in the same order.
    //
    // One side effect is kept on purpose: the first lookup of a search fills the building's terrain route map if
    // it was thrown away. The first candidate is always looked up, dead or out of reach, so that still happens on
    // the same tick as in the unmodded game (the game reads whether a map is filled when a walker asks for a path).
    //
    // YielderSearchVerify runs the game's own search as well, compares and logs any difference; the game is handed
    // the mod's result either way. If anything throws, the feature switches itself off and the game's own code runs.
    internal static class YielderSearch
    {
        internal sealed class Counters
        {
            public long Candidates;
            public long Lookups;
            public long DeadSkipped;
            public long OutOfReach;
        }

        // The rule, free of game types so the tests can check it against a model of the game's search. A plant that
        // is neither yielding nor alive can neither be the answer nor set "found something", whatever its distance,
        // so it is not looked up (0.4.25), except as the search's first lookup: that lookup is also what fills the
        // building's terrain route map, and walkers' path requests read whether a cached map is filled without
        // filling it (PathfindingService.FindTerrainPathIfCached), so the map has to be filled on the same tick as
        // without the mod. One that may be reached is told by mayReach, a pre-filter that only ever says "no" when
        // the game's lookup would say "unreachable" (TerrainReach), so the lookup is skipped for it.
        internal static IEnumerable<TReached> LazyCandidates<TPlant, TReached>(IEnumerable<TPlant> plants,
            Func<TPlant, bool> exists, Func<TPlant, bool> isYielding, Func<TPlant, bool> isAlive,
            Func<TPlant, TReached> lookUp, Func<TReached, bool> wasReached, Counters counters,
            Func<TPlant, bool> canBeTaken = null, Func<TPlant, bool> mayReach = null)
        {
            bool foundSomething = false;
            bool lookedUpAny = false;
            foreach (TPlant plant in plants)
            {
                counters.Candidates++;
                // A destroyed plant goes through the game's own path, whatever that does with it.
                if (!exists(plant))
                {
                    counters.Lookups++;
                    lookedUpAny = true;
                    yield return lookUp(plant);
                    continue;
                }
                bool yielding = isYielding(plant);
                if (lookedUpAny && !yielding && !isAlive(plant))
                {
                    counters.DeadSkipped++;
                    continue;
                }
                // Only asked about yielding plants, and only once it can make a difference.
                if (foundSomething && !(yielding && (canBeTaken == null || canBeTaken(plant))))
                {
                    continue;
                }
                if (mayReach != null && !mayReach(plant))
                {
                    counters.OutOfReach++;
                    continue;
                }
                counters.Lookups++;
                lookedUpAny = true;
                TReached reached = lookUp(plant);
                if (!foundSomething && wasReached(reached) && (yielding || isAlive(plant)))
                {
                    foundSomething = true;
                }
                yield return reached;
            }
        }

        private static readonly Counters Totals = new Counters();

        private static Func<object, object> _closestFinderOf;
        private static Func<object, object> _finderScratchOf;
        private static Func<object, object> _finderOrderedScratchOf;
        private static Func<object, object> _carryCalculatorOf;
        // Room for a good in the building of the current search. Main thread only, emptied per search.
        private static readonly Dictionary<string, bool> RoomForGood = new Dictionary<string, bool>();
        // The reach pre-filter (TerrainReach): the building's terrain route map and its box, for one search.
        private static Func<object, object> _nodeIdServiceOf;
        private static Func<object, Vector3, int> _worldToId;
        private static Func<object, object> _terrainCacheOf;
        private static Func<object, int, object> _fieldAt;
        private static bool _reachBound;
        private static TerrainReach.Box _reachBox;
        private static readonly Func<Yielder, bool> InReach = plant => TerrainReach.MayReach(_reachBox, plant.CenterPosition);
        private static readonly Func<Yielder, bool> NoReach = _ => false;
        private static bool _active;
        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        private static bool _verify;
        private static long _searches;
        private static long _stopwatchTicks;
        private static long _verifyMismatches;
        private static long _found;
        private static long _nothingToTake;
        private static long _nothingInRange;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.YielderSearchVerify;
            Feature feature = new Feature { Name = "YielderSearch" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "YielderFinder.FindLivingYielderWithoutAccessible",
                Required = true,
                Target = () =>
                {
                    _closestFinderOf = Reflect.FieldGetter<object>(typeof(YielderFinder), "_closestYielderFinder");
                    _finderScratchOf = Reflect.FieldGetter<object>(typeof(ClosestYielderFinder), "_yielders");
                    _finderOrderedScratchOf = Reflect.FieldGetter<object>(typeof(ClosestYielderFinder), "_orderedYielders");
                    _carryCalculatorOf = Reflect.FieldGetter<object>(typeof(ClosestYielderFinder), "_carryAmountCalculator");
                    BindReach();
                    return HarmonyLib.AccessTools.Method(typeof(YielderFinder), "FindLivingYielderWithoutAccessible");
                },
                Prefix = Reflect.Own(typeof(YielderSearch), nameof(FindPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        // The navigation members the reach pre-filter needs; without them every candidate is looked up as before.
        private static void BindReach()
        {
            try
            {
                Type pathfinding = Reflect.GameType("Timberborn.Navigation.PathfindingService");
                Type nodeIds = Reflect.GameType("Timberborn.Navigation.NodeIdService");
                Type cache = Reflect.GameType("Timberborn.Navigation.TerrainFlowFieldCache");
                _nodeIdServiceOf = Reflect.FieldGetter<object>(pathfinding, "_nodeIdService");
                _terrainCacheOf = Reflect.FieldGetter<object>(pathfinding, "_terrainFlowFieldCache");
                _worldToId = Reflect.InstanceCall<Func<object, Vector3, int>>(HarmonyLib.AccessTools.Method(nodeIds, "WorldToId"));
                _fieldAt = Reflect.InstanceCall<Func<object, int, object>>(HarmonyLib.AccessTools.Method(cache, "GetFlowFieldAtNode"));
                _reachBound = true;
            }
            catch (Exception exception)
            {
                _reachBound = false;
                Log.Info("YielderSearch: the reach pre-filter is not available, every candidate is looked up: " + exception.Message);
            }
        }

        // The pre-filter for one search: the box of the building's terrain route map, or nothing to go by.
        private static Func<Yielder, bool> ReachFilter(Accessible start)
        {
            if (!_reachBound || !TerrainReach.IsActive)
            {
                return null;
            }
            Vector3? access;
            try
            {
                access = start.UnblockedSingleAccess;
            }
            catch (InvalidOperationException)
            {
                // A building with no access or several (Single() throws): the game's own lookup throws the same
                // at the first candidate; no pre-filter, and no verdict of this feature's own.
                return null;
            }
            if (!access.HasValue)
            {
                // The game's lookup answers "unreachable" for every candidate of a building with no access.
                return NoReach;
            }
            object pathfinding = TerrainMaps.PathfindingServiceInstance;
            if (pathfinding == null)
            {
                return null;
            }
            object field;
            try
            {
                field = _fieldAt(_terrainCacheOf(pathfinding), _worldToId(_nodeIdServiceOf(pathfinding), access.Value));
            }
            catch (Exception)
            {
                // No cached map at the access: the game's own lookup runs, and throws as the game would.
                return null;
            }
            _reachBox = TerrainReach.BoxOf(field);
            return _reachBox == null ? null : InReach;
        }

        public static string TakeStatsLine()
        {
            if (!_active && _searches == 0)
            {
                return null;
            }
            long skipped = Totals.Candidates - Totals.Lookups;
            string line = string.Format(CultureInfo.InvariantCulture,
                "YielderSearch: {0} searches for trees and plants over {1} candidates; {2} distance lookups, {3} left " +
                "out ({4:0}%), of which {11} dead plants and {12} plants outside the route map's reach; {5:0.0} ms in total " +
                "({6:0.000} ms each); outcomes: {8} found work, {9} found nothing the building has room for or the worker " +
                "can take, {10} found nothing in range{7}",
                _searches, Totals.Candidates, Totals.Lookups, skipped,
                Totals.Candidates > 0 ? 100.0 * skipped / Totals.Candidates : 0,
                _stopwatchTicks * 1000.0 / Stopwatch.Frequency,
                _searches > 0 ? _stopwatchTicks * 1000.0 / Stopwatch.Frequency / _searches : 0,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "", _found, _nothingToTake, _nothingInRange,
                Totals.DeadSkipped, Totals.OutOfReach);
            _searches = _stopwatchTicks = _found = _nothingToTake = _nothingInRange = 0;
            Totals.Candidates = Totals.Lookups = Totals.DeadSkipped = Totals.OutOfReach = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        // Last, so that another mod's prefix on the same method runs first whatever the load order: BeaverBuddies
        // MultiColony narrows `yielders` to the worker's own colony there, and Harmony skips such a prefix once this
        // one has answered.
        [HarmonyPriority(Priority.Last)]
        private static bool FindPrefix(object __instance, Inventory receivingInventory, Accessible start,
            int liftingCapacity, IEnumerable<Yielder> yielders, ref YielderSearchResult __result)
        {
            if (!_active)
            {
                return true;
            }
            ClosestYielderFinder finder = null;
            try
            {
                long started = Stopwatch.GetTimestamp();
                finder = (ClosestYielderFinder)_closestFinderOf(__instance);
                var calculator = (Timberborn.Carrying.CarryAmountCalculator)_carryCalculatorOf(finder);
                RoomForGood.Clear();
                Func<Yielder, bool> mayReach = ReachFilter(start);
                YielderSearchResult result = finder.FindLivingYielder(receivingInventory, liftingCapacity,
                    LazyCandidates(yielders, Exists, IsYielding, IsAlive, plant => LookUp(start, plant), WasReached, Totals,
                        plant => CanBeTaken(calculator, liftingCapacity, receivingInventory, plant), mayReach));
                _reachBox = null;
                _searches++;
                _stopwatchTicks += Stopwatch.GetTimestamp() - started;
                if (result.HasYielder) _found++;
                else if (result.NoYielderInRange) _nothingInRange++;
                else _nothingToTake++;
                if (_verify)
                {
                    YielderSearchResult games = finder.FindLivingYielder(receivingInventory, liftingCapacity,
                        yielders.Select(plant => LookUp(start, plant)));
                    // Compared returns the mod's result: that, not `games`, is what the game is handed (the tests
                    // drive Compared, not this prefix, so keep any substitution out of here too).
                    result = Compared(result, games);
                }
                __result = result;
                return false;
            }
            catch (Exception exception)
            {
                _active = false;
                ClearScratch(finder);
                TurnedOff.Report("YielderSearch",
                    "YielderSearch failed and turned itself off for this session: " + exception);
                return true;
            }
        }
        // ReSharper restore InconsistentNaming

        private static bool Exists(Yielder plant)
        {
            return plant;
        }

        private static bool IsYielding(Yielder plant)
        {
            return plant.IsYielding;
        }

        // The game's IsAlive (!GetComponent<LivingNaturalResource>().IsDead); a plant without the component, which the
        // game would trip over, counts as alive here and is looked up as before.
        private static bool IsAlive(Yielder plant)
        {
            return !plant.TryGetComponent(out LivingNaturalResource living) || !living.IsDead;
        }

        // The game's own question, asked with this plant's own yield: would the finder's last step accept this good?
        // The answer does not depend on which plant of the good is asked about (see the top of the file), so it is
        // kept per good for the length of one search.
        private static bool CanBeTaken(Timberborn.Carrying.CarryAmountCalculator calculator, int liftingCapacity,
            Inventory receivingInventory, Yielder plant)
        {
            var yield = plant.Yield;
            if (!RoomForGood.TryGetValue(yield.GoodId, out bool room))
            {
                room = calculator.AmountToCarry(liftingCapacity, yield, receivingInventory).Amount > 0;
                RoomForGood[yield.GoodId] = room;
            }
            return room;
        }

        // YielderFinder.RegularYielderAsReachable, which is private.
        private static ReachableYielder LookUp(Accessible start, Yielder plant)
        {
            return start.FindTerrainPath(plant.CenterPosition, out float distance)
                ? new ReachableYielder(plant, distance)
                : default(ReachableYielder);
        }

        private static bool WasReached(ReachableYielder reached)
        {
            return reached.Yielder;
        }

        // Verify mode: the game's own search beside the mod's. Returns the result the game is handed, which is the
        // mod's whatever the comparison says: the setting is each player's own, so in co-op the one player who has it
        // on must get the same answer as the others (up to 0.4.26 a difference handed over the game's).
        internal static YielderSearchResult Compared(YielderSearchResult result, YielderSearchResult games)
        {
            if (!Same(result, games))
            {
                _verifyMismatches++;
                if (_verifyMismatches <= 10)
                {
                    Log.Warning("YielderSearch: result differs from the game's own search. " +
                                $"Mod: {Describe(result)}. Game: {Describe(games)}.");
                }
            }
            return result;
        }

        private static bool Same(YielderSearchResult a, YielderSearchResult b)
        {
            return ReferenceEquals(a.Yielder, b.Yielder) && a.NoYielderInRange == b.NoYielderInRange &&
                   a.Yield.Amount == b.Yield.Amount && a.Yield.GoodId == b.Yield.GoodId;
        }

        private static string Describe(YielderSearchResult result)
        {
            return result.HasYielder
                ? $"{result.Yield.Amount} {result.Yield.GoodId} at {result.Yielder.Coordinates}"
                : result.NoYielderInRange ? "nothing in range" : "nothing to take";
        }

        // The game's finder only empties its scratch lists at the end of a search. If ours died half way, the
        // game's next search must not start with what we left there.
        private static void ClearScratch(ClosestYielderFinder finder)
        {
            try
            {
                if (finder != null)
                {
                    ((IDictionary)_finderScratchOf(finder)).Clear();
                    object ordered = _finderOrderedScratchOf(finder);
                    ordered.GetType().GetMethod("Clear").Invoke(ordered, null);
                }
            }
            catch (Exception)
            {
                // Nothing more can be done from here.
            }
        }
    }
}
