using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.Beavers;
using Timberborn.Common;
using Timberborn.DwellingSystem;

namespace LateGamePerformance
{
    // Every tick the game picks the dwelling that has waited longest for a dweller and looks for a beaver to move
    // in (DwellerHomeAssigner.AssignDweller): it walks every adult, then every child, of the district, asking each
    // one whether it is looking for a better home and, if so, whether it may move in; the first that may, does. In
    // a settled colony nobody is looking, so all 350 beavers are asked on every tick, and each is first found from
    // its Beaver component with a component lookup, through a LINQ concatenation of the two lists: 0.3 ms per
    // tick in the colony this was measured in, growing with the population.
    //
    // The mod asks exactly the same questions of exactly the same beavers in exactly the same order, with the
    // game's own methods (Dweller.IsLookingForBetterHome, AutoAssignableDwelling.CanAssignDweller, and its
    // AssignDweller for the one that moves in), and stops at the same first hit. What it leaves out: the LINQ
    // enumerators, and the per-beaver component lookup, which it does once per beaver and keeps, because the
    // components of an entity are fixed for its life. The two lists are the game's own lists, read by index. The
    // result cannot differ, so it is the same on every computer.
    //
    // HomeSearchVerify runs the game's walk as well (the same lookups, from scratch, through the same
    // concatenation) and compares the beaver picked; a mismatch is logged and the game's pick is used.
    internal static class HomeSearch
    {
        private const string AssignerType = "Timberborn.DwellingSystem.DwellerHomeAssigner";
        private const string DwellingType = "Timberborn.DwellingSystem.AutoAssignableDwelling";

        private sealed class ReferenceComparer : IEqualityComparer<Beaver>
        {
            public bool Equals(Beaver x, Beaver y) => ReferenceEquals(x, y);

            public int GetHashCode(Beaver beaver) => RuntimeHelpers.GetHashCode(beaver);
        }

        // The game's own methods; swappable for the test harness, where no entity can be built.
        internal static Func<Beaver, Dweller> DwellerOf = beaver => beaver.GetComponent<Dweller>();
        internal static Func<Dweller, bool> IsLooking = dweller => dweller.IsLookingForBetterHome();
        internal static Func<object, Dweller, bool> CanAssign;
        internal static Action<object, Dweller> Assign;

        private static readonly Dictionary<Beaver, Dweller> Dwellers = new Dictionary<Beaver, Dweller>(new ReferenceComparer());

        private static bool _active;
        private static bool _verify;

        private static long _searches;
        private static long _looked;
        private static long _movedIn;
        private static long _otherLists;
        private static long _stopwatchTicks;
        private static long _verifyMismatches;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.HomeSearchVerify;
            Type self = typeof(HomeSearch);
            Feature feature = new Feature { Name = "HomeSearch" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "DwellerHomeAssigner.AssignDweller",
                Required = true,
                Target = () =>
                {
                    BindAccessors();
                    return Reflect.Method(AssignerType, "AssignDweller");
                },
                Prefix = Reflect.Own(self, nameof(AssignPrefix))
            });
            // Memory only: a beaver that is gone leaves the table. Without it the table keeps a few bytes per
            // beaver that ever lived; the lookups stay right, because a key is a live object.
            feature.Patches.Add(new PatchSpec
            {
                Name = "Dweller.DeleteEntity",
                Required = false,
                Target = () => AccessTools.Method(typeof(Dweller), "DeleteEntity"),
                Postfix = Reflect.Own(self, nameof(DeleteEntityPostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            Dwellers.Clear();
            _active = true;
        }

        public static bool IsActive => _active;

        public static void SceneCreated()
        {
            Dwellers.Clear();
        }

        internal static int TableSize => Dwellers.Count;

        // Resolves everything this feature touches; throws if the game no longer matches.
        public static void BindAccessors()
        {
            Type dwelling = Reflect.GameType(DwellingType);
            if (dwelling == null || Reflect.GameType(AssignerType) == null)
            {
                throw new TypeLoadException("dwelling types not found");
            }
            CanAssign = Reflect.InstanceCall<Func<object, Dweller, bool>>(AccessTools.Method(dwelling, "CanAssignDweller"));
            Assign = Reflect.InstanceCall<Action<object, Dweller>>(AccessTools.Method(dwelling, "AssignDweller"));
        }

        public static string TakeStatsLine()
        {
            if (!_active && _searches == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "HomeSearch: {0} searches for a beaver to move into a home asked {1} beavers in {2:0.0} ms ({3:0.000} ms " +
                "each); {4} moved in; {5} beavers in the table; {6} searches over lists of another kind were left to the game{7}",
                _searches, _looked, ms, _searches > 0 ? ms / _searches : 0, _movedIn, Dwellers.Count, _otherLists,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _searches = _looked = _movedIn = _otherLists = _stopwatchTicks = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool AssignPrefix(object dwelling, IEnumerable<Beaver> primaryBeavers, IEnumerable<Beaver> secondaryBeavers,
            ref bool __result)
        {
            if (!_active)
            {
                return true;
            }
            // The game passes its district lists; anything else (another mod's) is walked by the game's own code.
            if (!(primaryBeavers is ReadOnlyList<Beaver> primary) || !(secondaryBeavers is ReadOnlyList<Beaver> secondary))
            {
                _otherLists++;
                return true;
            }
            try
            {
                long started = Stopwatch.GetTimestamp();
                Dweller pick = Scan(dwelling, primary) ?? Scan(dwelling, secondary);
                if (_verify)
                {
                    Dweller expected = GamesPick(dwelling, primaryBeavers, secondaryBeavers);
                    if (!ReferenceEquals(pick, expected))
                    {
                        _verifyMismatches++;
                        if (_verifyMismatches <= 10)
                        {
                            Log.Warning("HomeSearch verify: the mod would move in " + Describe(pick) + " and the game " +
                                        Describe(expected) + ". Using the game's.");
                        }
                        pick = expected;
                    }
                }
                if (pick != null)
                {
                    Assign(dwelling, pick);
                    _movedIn++;
                }
                __result = pick != null;
                _searches++;
                _stopwatchTicks += Stopwatch.GetTimestamp() - started;
                return false;
            }
            catch (Exception exception)
            {
                // Nothing has been changed unless the game's own AssignDweller threw, which it would have thrown
                // for the same beaver in the game's own walk; the game's walk now runs.
                _active = false;
                Log.Warning("HomeSearch failed and turned itself off for this session: " + exception);
                return true;
            }
        }

        internal static void DeleteEntityPostfix(Dweller __instance)
        {
            if (!_active || Dwellers.Count == 0)
            {
                return;
            }
            Beaver key = null;
            foreach (KeyValuePair<Beaver, Dweller> entry in Dwellers)
            {
                if (ReferenceEquals(entry.Value, __instance))
                {
                    key = entry.Key;
                    break;
                }
            }
            if (key != null)
            {
                Dwellers.Remove(key);
            }
        }
        // ReSharper restore InconsistentNaming

        // The game's loop over one list: the first beaver that is looking and may move in.
        private static Dweller Scan(object dwelling, ReadOnlyList<Beaver> beavers)
        {
            int count = beavers.Count;
            for (int i = 0; i < count; i++)
            {
                Beaver beaver = beavers[i];
                if (!Dwellers.TryGetValue(beaver, out Dweller dweller))
                {
                    dweller = DwellerOf(beaver);
                    Dwellers[beaver] = dweller;
                }
                _looked++;
                if (IsLooking(dweller) && CanAssign(dwelling, dweller))
                {
                    return dweller;
                }
            }
            return null;
        }

        // The game's walk, as decompiled, with fresh lookups.
        private static Dweller GamesPick(object dwelling, IEnumerable<Beaver> primaryBeavers, IEnumerable<Beaver> secondaryBeavers)
        {
            foreach (Beaver item in primaryBeavers.Concat(secondaryBeavers))
            {
                Dweller component = DwellerOf(item);
                if (IsLooking(component) && CanAssign(dwelling, component))
                {
                    return component;
                }
            }
            return null;
        }

        private static string Describe(Dweller dweller)
        {
            return dweller == null ? "nobody" : "dweller " + RuntimeHelpers.GetHashCode(dweller);
        }
    }
}
