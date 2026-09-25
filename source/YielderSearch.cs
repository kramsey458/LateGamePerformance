using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Timberborn.Goods;
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
    // A building with no room at all stops the walk at the first plant found (0.4.28). Leaving lookups out did not
    // stop the walk itself: every remaining candidate was still fetched through the game's filter (and BeaverBuddies'
    // colony filter) and asked about, some 390 ns each over about 1,100 candidates per search late in a session, and
    // 17 of the 21 lumberjack flags of the logged colony were full. When the receiving inventory is fully reserved
    // (Inventory.IsFullyReserved: stock + reserved capacity >= capacity), the second bound in UnreservedCapacity,
    // Capacity - stock - reserved capacity, is 0 or below, so the room for every good is Max(Min(..., <= 0), 0) = 0,
    // the carry amount of every plant (CarryAmountCalculator.AmountToCarry: a Min over that room) is 0, and the
    // finder's last step answers "nothing to take" (CreateEmpty) whatever plants it was handed, as long as its "found
    // something" flag is true. That flag goes true in the finder on the very candidate that sets foundSomething here
    // (the same test: it exists, was reached, and is yielding or alive), and that candidate has already been handed to
    // the finder when the walk stops, on the finder's next request. So the answer is fixed there: "nothing to take" if
    // something was found, and the walk runs to the end as before if nothing is. What the rest of the walk would have
    // done is reads only (the game's `!Reserved` filter, BeaverBuddies' colony filter, the plants' state, this
    // feature's per-good room question), plus lookups of destroyed plants, which only read the building's route map
    // that the first lookup of the search has filled, and which the finder drops; the lookups of plants the building
    // has room for are none, since it has room for nothing. The inventory cannot change during a search (nothing in it
    // writes an inventory), so it is asked once, before the walk. The first lookup, which fills the route map, is
    // still made. This stands down for the session if another mod patches one of the methods the argument rests on
    // (FullStopMethods), and is decided at the first search, when every mod has patched what it patches.
    //
    // A lumberjack's search with no grown tree in reach stops at the first tree found too (0.4.31). The 0.4.28 session
    // showed the full-building stop firing 0 times: the flags had room, and the searches walked every candidate only to
    // find no grown tree in reach. GrownTrees keeps the marked trees that are yielding counted on a grid of the map, and
    // when none of them lies inside the box of the building's terrain route map, no candidate still to come can be both
    // yielding and reached, so nothing after the candidate that found something can change the finder's answer ("nothing
    // to take"). The walk ends there, as for a full building; the first lookup is made as before. See GrownTrees for what
    // the count rests on and when it stands down.
    //
    // No garbage per search (0.4.28): the walk is one object kept for all searches (Walk) instead of an iterator, and
    // the delegates it calls are made once and read the search's inventory, access and lifting capacity from fields
    // set for the length of the search, instead of two closures and two delegates made per search. Searches run on the
    // main thread, inside a behaviour's decision, and never inside one another: nothing a search calls (the filters,
    // the plants' state, the room question, the path lookup, the game's finder) starts another. Should one ever,
    // it goes to the game's own code (the guard at the top of FindPrefix), which is the game's result.
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
            // Searches that stopped at the first plant found because the building had no room, and the candidates
            // walked in them.
            public long Stopped;
            public long WalkedInStopped;
            // The same for a lumberjack's search with no grown marked tree in reach (GrownTrees, 0.4.31).
            public long NoneGrownStopped;
            public long WalkedInNoneGrownStopped;
        }

        // The rule, free of game types so the tests can check it against a model of the game's search. A plant that
        // is neither yielding nor alive can neither be the answer nor set "found something", whatever its distance,
        // so it is not looked up (0.4.25), except as the search's first lookup: that lookup is also what fills the
        // building's terrain route map, and walkers' path requests read whether a cached map is filled without
        // filling it (PathfindingService.FindTerrainPathIfCached), so the map has to be filled on the same tick as
        // without the mod. One that may be reached is told by mayReach, a pre-filter that only ever says "no" when
        // the game's lookup would say "unreachable" (TerrainReach), so the lookup is skipped for it. stopWhenFound says
        // that the building has room for no good at all (IsFullyReserved), and then the walk ends once something is
        // found (see the top of the file). noneGrown says that no plant among the candidates can be both yielding and
        // reached (GrownTrees), and ends the walk at the same place for that reason.
        internal static IEnumerable<TReached> LazyCandidates<TPlant, TReached>(IEnumerable<TPlant> plants,
            Func<TPlant, bool> exists, Func<TPlant, bool> isYielding, Func<TPlant, bool> isAlive,
            Func<TPlant, TReached> lookUp, Func<TReached, bool> wasReached, Counters counters,
            Func<TPlant, bool> canBeTaken = null, Func<TPlant, bool> mayReach = null, bool stopWhenFound = false,
            bool noneGrown = false)
        {
            return new Walk<TPlant, TReached>().Start(plants, exists, isYielding, isAlive, lookUp, wasReached, counters,
                canBeTaken, mayReach, stopWhenFound, noneGrown);
        }

        // The walk of LazyCandidates, written out as an enumerator so that one object can serve every search (up to
        // 0.4.27 it was a `yield` iterator, a new object per search). It behaves as that iterator did: the plants are
        // first asked for on the first MoveNext, handed back one looked-up candidate at a time, and let go of (Dispose)
        // when the walk ends or its consumer stops; asked for a second enumerator it starts a new walk from the top.
        internal sealed class Walk<TPlant, TReached> : IEnumerable<TReached>, IEnumerator<TReached>
        {
            private IEnumerable<TPlant> _plants;
            private Func<TPlant, bool> _exists;
            private Func<TPlant, bool> _isYielding;
            private Func<TPlant, bool> _isAlive;
            private Func<TPlant, TReached> _lookUp;
            private Func<TReached, bool> _wasReached;
            private Counters _counters;
            private Func<TPlant, bool> _canBeTaken;
            private Func<TPlant, bool> _mayReach;
            private bool _stopWhenFound;
            private bool _noneGrown;

            private IEnumerator<TPlant> _source;
            private TReached _current;
            private bool _handedOut;
            private bool _done;
            private bool _foundSomething;
            private bool _lookedUpAny;
            private long _walked;

            internal Walk<TPlant, TReached> Start(IEnumerable<TPlant> plants, Func<TPlant, bool> exists,
                Func<TPlant, bool> isYielding, Func<TPlant, bool> isAlive, Func<TPlant, TReached> lookUp,
                Func<TReached, bool> wasReached, Counters counters, Func<TPlant, bool> canBeTaken,
                Func<TPlant, bool> mayReach, bool stopWhenFound, bool noneGrown = false)
            {
                _plants = plants;
                _exists = exists;
                _isYielding = isYielding;
                _isAlive = isAlive;
                _lookUp = lookUp;
                _wasReached = wasReached;
                _counters = counters;
                _canBeTaken = canBeTaken;
                _mayReach = mayReach;
                _stopWhenFound = stopWhenFound;
                _noneGrown = noneGrown;
                _source = null;
                _current = default;
                _handedOut = false;
                _done = false;
                _foundSomething = false;
                _lookedUpAny = false;
                _walked = 0;
                return this;
            }

            // Lets go of everything the last search handed in, so that nothing of a finished game scene is kept.
            internal void Release()
            {
                Dispose();
                Start(null, null, null, null, null, null, null, null, null, false);
                _done = true;
            }

            public IEnumerator<TReached> GetEnumerator()
            {
                if (!_handedOut)
                {
                    _handedOut = true;
                    return this;
                }
                Walk<TPlant, TReached> again = new Walk<TPlant, TReached>().Start(_plants, _exists, _isYielding, _isAlive,
                    _lookUp, _wasReached, _counters, _canBeTaken, _mayReach, _stopWhenFound, _noneGrown);
                again._handedOut = true;
                return again;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public TReached Current => _current;

            object IEnumerator.Current => _current;

            public bool MoveNext()
            {
                if (_done)
                {
                    return false;
                }
                if (_foundSomething && (_stopWhenFound || _noneGrown))
                {
                    // The building has room for nothing, or no plant still to come can be yielding and reached, and the
                    // candidate that found something went to the finder on the previous call: the answer is "nothing to
                    // take", whatever else is on the list.
                    if (_stopWhenFound)
                    {
                        _counters.Stopped++;
                        _counters.WalkedInStopped += _walked;
                    }
                    else
                    {
                        _counters.NoneGrownStopped++;
                        _counters.WalkedInNoneGrownStopped += _walked;
                    }
                    return Finish();
                }
                if (_source == null)
                {
                    _source = _plants.GetEnumerator();
                }
                while (_source.MoveNext())
                {
                    TPlant plant = _source.Current;
                    _counters.Candidates++;
                    _walked++;
                    // A destroyed plant goes through the game's own path, whatever that does with it.
                    if (!_exists(plant))
                    {
                        _counters.Lookups++;
                        _lookedUpAny = true;
                        _current = _lookUp(plant);
                        return true;
                    }
                    bool yielding = _isYielding(plant);
                    // Once something is found a plant that is not yielding is passed over either way (up to 0.4.27 it
                    // was first asked whether it is alive, a component lookup, and then passed over all the same).
                    if (_foundSomething && !yielding)
                    {
                        continue;
                    }
                    if (_lookedUpAny && !yielding && !_isAlive(plant))
                    {
                        _counters.DeadSkipped++;
                        continue;
                    }
                    // Only asked about yielding plants, and only once it can make a difference.
                    if (_foundSomething && !(_canBeTaken == null || _canBeTaken(plant)))
                    {
                        continue;
                    }
                    if (_mayReach != null && !_mayReach(plant))
                    {
                        _counters.OutOfReach++;
                        continue;
                    }
                    _counters.Lookups++;
                    _lookedUpAny = true;
                    TReached reached = _lookUp(plant);
                    if (!_foundSomething && _wasReached(reached) && (yielding || _isAlive(plant)))
                    {
                        _foundSomething = true;
                    }
                    _current = reached;
                    return true;
                }
                return Finish();
            }

            private bool Finish()
            {
                _done = true;
                _current = default;
                IEnumerator<TPlant> source = _source;
                _source = null;
                source?.Dispose();
                return false;
            }

            public void Dispose()
            {
                if (!_done)
                {
                    Finish();
                }
            }

            void IEnumerator.Reset()
            {
                throw new NotSupportedException();
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
        // The search under way (main thread only, one at a time: see the top of the file), read by the delegates
        // below, which are made once. Emptied when the search ends.
        private static bool _searching;
        private static Accessible _start;
        private static Inventory _receivingInventory;
        private static int _liftingCapacity;
        private static Timberborn.Carrying.CarryAmountCalculator _calculator;
        private static readonly Func<Yielder, bool> ExistsCall = Exists;
        private static readonly Func<Yielder, bool> IsYieldingCall = IsYielding;
        private static readonly Func<Yielder, bool> IsAliveCall = IsAlive;
        private static readonly Func<ReachableYielder, bool> WasReachedCall = WasReached;
        private static readonly Func<Yielder, ReachableYielder> LookUpCall = plant => LookUp(_start, plant);
        private static readonly Func<Yielder, bool> CanBeTakenCall =
            plant => CanBeTaken(_calculator, _liftingCapacity, _receivingInventory, plant);
        private static readonly Walk<Yielder, ReachableYielder> SharedWalk = new Walk<Yielder, ReachableYielder>();
        // Whether a full building's search may stop at the first plant found: 0 until the first search decides it,
        // then 1 or -1 for the session. _fullStopOff says why not, for the stats line.
        private static int _fullStop;
        private static string _fullStopOff;
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
                "can take, {10} found nothing in range; {13} searches stopped at the first plant found because the " +
                "building had no room left ({14} candidates walked in them); {16} lumberjack searches stopped at the " +
                "first tree found because no grown marked tree was in reach ({17} candidates walked in them){15}{7}",
                _searches, Totals.Candidates, Totals.Lookups, skipped,
                Totals.Candidates > 0 ? 100.0 * skipped / Totals.Candidates : 0,
                _stopwatchTicks * 1000.0 / Stopwatch.Frequency,
                _searches > 0 ? _stopwatchTicks * 1000.0 / Stopwatch.Frequency / _searches : 0,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "", _found, _nothingToTake, _nothingInRange,
                Totals.DeadSkipped, Totals.OutOfReach, Totals.Stopped, Totals.WalkedInStopped,
                _fullStopOff != null ? $" (searches of a full building walk every candidate: {_fullStopOff})" : "",
                Totals.NoneGrownStopped, Totals.WalkedInNoneGrownStopped);
            _searches = _stopwatchTicks = _found = _nothingToTake = _nothingInRange = 0;
            Totals.Candidates = Totals.Lookups = Totals.DeadSkipped = Totals.OutOfReach = 0;
            Totals.Stopped = Totals.WalkedInStopped = Totals.NoneGrownStopped = Totals.WalkedInNoneGrownStopped = 0;
            return line;
        }

        // The methods the early stop for a full building rests on (see the top of the file). TotalAmountInStock is
        // listed too: IsFullyReserved goes through it where UnreservedCapacity reads the stock directly.
        internal static MethodBase[] FullStopMethods()
        {
            return new MethodBase[]
            {
                AccessTools.PropertyGetter(typeof(Inventory), nameof(Inventory.IsFullyReserved)),
                AccessTools.PropertyGetter(typeof(Inventory), nameof(Inventory.TotalAmountInStock)),
                AccessTools.Method(typeof(Inventory), nameof(Inventory.UnreservedCapacity), new[] { typeof(string) }),
                AccessTools.Method(typeof(Timberborn.Carrying.CarryAmountCalculator),
                    nameof(Timberborn.Carrying.CarryAmountCalculator.AmountToCarry),
                    new[] { typeof(int), typeof(GoodAmount), typeof(IAmountProvider) })
            };
        }

        // Every patch on a method, as (Harmony id, "Namespace.Type.Method" of the patch), as DistrictCounts reads them;
        // swapped by the tests, where Harmony's patch registry cannot run.
        internal static Func<MethodBase, IEnumerable<(string Owner, string Patch)>> PatchesOn = method =>
        {
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
            {
                return null;
            }
            List<(string, string)> all = new List<(string, string)>();
            foreach (IEnumerable<Patch> kind in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
            {
                foreach (Patch patch in kind)
                {
                    all.Add((patch.owner, patch.PatchMethod.DeclaringType?.FullName + "." + patch.PatchMethod.Name));
                }
            }
            return all;
        };

        // Why a full building's search may not stop early, or null if it may: a method of FullStopMethods that is not
        // there, or another mod's patch on one.
        internal static string FullStopBlocker()
        {
            foreach (MethodBase method in FullStopMethods())
            {
                if (method == null)
                {
                    return "a method the early stop rests on was not found in this game version";
                }
                IEnumerable<(string Owner, string Patch)> patches = PatchesOn(method);
                if (patches == null)
                {
                    continue;
                }
                foreach ((string owner, string patch) in patches)
                {
                    if (!owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                    {
                        return $"another mod patches {method.DeclaringType?.Name}.{method.Name} ({owner}, {patch})";
                    }
                }
            }
            return null;
        }

        // Decided once, at the first search of the session, when every mod has patched what it patches.
        internal static bool FullStopAllowed()
        {
            if (_fullStop == 0)
            {
                string blocker;
                try
                {
                    blocker = FullStopBlocker();
                }
                catch (Exception exception)
                {
                    blocker = "Harmony's patch list could not be read (" + exception.Message + ")";
                }
                _fullStop = blocker == null ? 1 : -1;
                _fullStopOff = blocker;
                if (blocker != null)
                {
                    Log.Info("YielderSearch: a search for a full building walks every candidate as before: " + blocker + ".");
                }
            }
            return _fullStop > 0;
        }

        internal static void ResetFullStopForTests()
        {
            _fullStop = 0;
            _fullStopOff = null;
        }

        // Whether the building has room for no good at all, so that the search may stop at the first plant found.
        private static bool HasNoRoom(Inventory receivingInventory)
        {
            return receivingInventory != null && FullStopAllowed() && receivingInventory.IsFullyReserved;
        }

        // ReSharper disable InconsistentNaming
        // Last, so that another mod's prefix on the same method runs first whatever the load order: BeaverBuddies
        // Timber Together narrows `yielders` to the worker's own colony there, and Harmony skips such a prefix once this
        // one has answered.
        [HarmonyPriority(Priority.Last)]
        private static bool FindPrefix(object __instance, Inventory receivingInventory, Accessible start,
            int liftingCapacity, IEnumerable<Yielder> yielders, ref YielderSearchResult __result)
        {
            // A search inside a search (none is known) is left to the game: the fields below belong to the outer one.
            if (!_active || _searching)
            {
                return true;
            }
            _searching = true;
            ClosestYielderFinder finder = null;
            try
            {
                long started = Stopwatch.GetTimestamp();
                finder = (ClosestYielderFinder)_closestFinderOf(__instance);
                _calculator = (Timberborn.Carrying.CarryAmountCalculator)_carryCalculatorOf(finder);
                _start = start;
                _receivingInventory = receivingInventory;
                _liftingCapacity = liftingCapacity;
                RoomForGood.Clear();
                Func<Yielder, bool> mayReach = ReachFilter(start);
                bool noRoom = HasNoRoom(receivingInventory);
                // Only with the box as the pre-filter: a building with no access (NoReach) or no box leaves it out.
                bool noneGrown = !noRoom && mayReach == InReach && GrownTrees.NoneGrownIn(_reachBox);
                if (_verify && mayReach == InReach)
                {
                    GrownTrees.Check(_reachBox);
                }
                YielderSearchResult result = finder.FindLivingYielder(receivingInventory, liftingCapacity,
                    SharedWalk.Start(yielders, ExistsCall, IsYieldingCall, IsAliveCall, LookUpCall, WasReachedCall, Totals,
                        CanBeTakenCall, mayReach, noRoom, noneGrown));
                _searches++;
                _stopwatchTicks += Stopwatch.GetTimestamp() - started;
                if (result.HasYielder) _found++;
                else if (result.NoYielderInRange) _nothingInRange++;
                else _nothingToTake++;
                if (_verify)
                {
                    YielderSearchResult games = finder.FindLivingYielder(receivingInventory, liftingCapacity,
                        yielders.Select(LookUpCall));
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
            finally
            {
                // Nothing of this search is kept: no plant, building or game scene stays reachable from here.
                SharedWalk.Release();
                _reachBox = null;
                _start = null;
                _receivingInventory = null;
                _calculator = null;
                _searching = false;
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
