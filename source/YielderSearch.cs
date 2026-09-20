using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using Timberborn.YielderFinding;
using Timberborn.Yielding;

namespace LateGamePerformance
{
    // Every time a lumberjack (or a gatherer, or a farmer looking for a harvest) looks for work, the game walks
    // every candidate plant, and for each one first looks up the path distance from the building and only then
    // asks whether the plant has anything to take (YielderFinder.FindLivingYielderWithoutAccessible feeding
    // ClosestYielderFinder). For lumberjacks the candidates are every unreserved marked tree on the map. In a
    // late game colony most of those are still growing, so nearly all of the distance lookups are thrown away.
    // Measured: 2.9 ms per tick, 16% of all tick time, on a fast computer.
    //
    // What the game does with each candidate, in order:
    //   1. distance lookup; a plant that cannot be reached is dropped and plays no further part
    //   2. "found something" becomes true if the plant is yielding, or alive
    //   3. if it is yielding, it competes for closest plant of its good
    // and the answer is: no plant in range (nothing found), or the closest plants tried in order.
    //
    // So the distance of a plant that is not yielding matters for one thing only: whether it can be reached, and
    // that only until "found something" is true. After that, a plant that is not yielding cannot change the
    // answer whatever its distance is, and LazyCandidates leaves it out. Plants that are yielding are always
    // looked up, exactly as before, and in the same order. The result is the game's result, not an approximation.
    //
    // One side effect is kept on purpose: the first lookup of a search fills the building's terrain route map if
    // it was thrown away. The first candidate is always looked up ("found something" starts false), so that
    // still happens on the same tick as in the unmodded game.
    //
    // YielderSearchVerify runs the game's own search as well, compares, logs any difference and uses the game's.
    // If anything throws, the feature switches itself off and the game's own code runs.
    internal static class YielderSearch
    {
        internal sealed class Counters
        {
            public long Candidates;
            public long Lookups;
        }

        // The rule, free of game types so the tests can check it against a model of the game's search.
        internal static IEnumerable<TReached> LazyCandidates<TPlant, TReached>(IEnumerable<TPlant> plants,
            Func<TPlant, bool> exists, Func<TPlant, bool> isYielding, Func<TPlant, bool> isAlive,
            Func<TPlant, TReached> lookUp, Func<TReached, bool> wasReached, Counters counters)
        {
            bool foundSomething = false;
            foreach (TPlant plant in plants)
            {
                counters.Candidates++;
                // A destroyed plant goes through the game's own path, whatever that does with it.
                bool known = exists(plant);
                bool yielding = known && isYielding(plant);
                if (known && !yielding && foundSomething)
                {
                    continue;
                }
                counters.Lookups++;
                TReached reached = lookUp(plant);
                if (known && !foundSomething && wasReached(reached) && (yielding || isAlive(plant)))
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
        private static bool _active;
        private static bool _verify;
        private static long _searches;
        private static long _stopwatchTicks;
        private static long _verifyMismatches;

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

        public static string TakeStatsLine()
        {
            if (!_active && _searches == 0)
            {
                return null;
            }
            long skipped = Totals.Candidates - Totals.Lookups;
            string line = string.Format(CultureInfo.InvariantCulture,
                "YielderSearch: {0} searches for trees and plants over {1} candidates; {2} distance lookups, {3} left " +
                "out ({4:0}%); {5:0.0} ms in total ({6:0.000} ms each){7}",
                _searches, Totals.Candidates, Totals.Lookups, skipped,
                Totals.Candidates > 0 ? 100.0 * skipped / Totals.Candidates : 0,
                _stopwatchTicks * 1000.0 / Stopwatch.Frequency,
                _searches > 0 ? _stopwatchTicks * 1000.0 / Stopwatch.Frequency / _searches : 0,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _searches = _stopwatchTicks = 0;
            Totals.Candidates = Totals.Lookups = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
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
                YielderSearchResult result = finder.FindLivingYielder(receivingInventory, liftingCapacity,
                    LazyCandidates(yielders, Exists, IsYielding, IsAlive, plant => LookUp(start, plant), WasReached, Totals));
                _searches++;
                _stopwatchTicks += Stopwatch.GetTimestamp() - started;
                if (_verify)
                {
                    YielderSearchResult games = finder.FindLivingYielder(receivingInventory, liftingCapacity,
                        yielders.Select(plant => LookUp(start, plant)));
                    if (!Same(result, games))
                    {
                        _verifyMismatches++;
                        Log.Warning("YielderSearch: result differs from the game's own search; using the game's. " +
                                    $"Mod: {Describe(result)}. Game: {Describe(games)}.");
                        result = games;
                    }
                }
                __result = result;
                return false;
            }
            catch (Exception exception)
            {
                _active = false;
                ClearScratch(finder);
                Log.Warning("YielderSearch failed and turned itself off for this session: " + exception);
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

        private static bool IsAlive(Yielder plant)
        {
            return plant.IsAlive();
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
