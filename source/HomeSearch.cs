using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
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
    // enumerators, and the per-beaver component lookup, which it does once per list and keeps, because the
    // components of an entity are fixed for its life: beside each of the game's two lists (the district's adults
    // and children) it keeps an array of their Dweller components in the same order, rebuilt whenever the list's
    // own change counter moved. A search is then one array walk with one predicate call per beaver. The result
    // cannot differ, so it is the same on every computer.
    //
    // HomeSearchVerify runs the game's walk as well (fresh lookups, through the same concatenation) and compares
    // the beaver picked; a mismatch is logged and the game's pick is used.
    internal static class HomeSearch
    {
        private const string AssignerType = "Timberborn.DwellingSystem.DwellerHomeAssigner";
        private const string DwellingType = "Timberborn.DwellingSystem.AutoAssignableDwelling";

        // The dwellers beside one of the game's lists, valid for one value of the list's change counter.
        private sealed class Beside
        {
            public Dweller[] Dwellers = new Dweller[0];
            public int Count;
            public int Version = -1;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T x, T y) => ReferenceEquals(x, y);

            public int GetHashCode(T item) => RuntimeHelpers.GetHashCode(item);
        }

        // The game's own methods; swappable for the test harness, where no entity can be built.
        internal static Func<Beaver, Dweller> DwellerOf = beaver => beaver.GetComponent<Dweller>();
        internal static Func<Dweller, bool> IsLooking = dweller => dweller.IsLookingForBetterHome();
        internal static Func<object, Dweller, bool> CanAssign;
        internal static Action<object, Dweller> Assign;

        private static Func<ReadOnlyList<Beaver>, List<Beaver>> _listOf;
        private static Func<object, int> _versionOf;
        private static Func<object, Beaver[]> _itemsOf;
        private static readonly Dictionary<List<Beaver>, Beside> Besides =
            new Dictionary<List<Beaver>, Beside>(new ReferenceComparer<List<Beaver>>());
        // The fallback when the list's change counter cannot be read: one lookup per beaver, kept for its life.
        private static readonly Dictionary<Beaver, Dweller> Dwellers = new Dictionary<Beaver, Dweller>(new ReferenceComparer<Beaver>());

        private static bool _active;
        private static bool _verify;

        private static long _searches;
        private static long _looked;
        private static long _movedIn;
        private static long _rebuilds;
        private static long _otherLists;
        private static long _stopwatchTicks;
        private static long _verifyMismatches;

        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

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
            // Memory only, for the fallback table: a beaver that is gone leaves it. The lookups stay right without
            // it, because a key is a live object.
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
            SceneCreated();
            _active = true;
        }

        public static bool IsActive => _active;

        public static void SceneCreated()
        {
            Besides.Clear();
            Dwellers.Clear();
        }

        internal static int TableSize => Dwellers.Count + Besides.Count;

        internal static bool UsesListVersions => _versionOf != null;

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
            // The list behind the game's read-only view, and the list's own change counter and array. The names are
            // those of the runtime's List<T>; a runtime without them gets the fallback table.
            try
            {
                FieldInfo list = AccessTools.Field(typeof(ReadOnlyList<Beaver>), "_list");
                ParameterExpression view = Expression.Parameter(typeof(ReadOnlyList<Beaver>), "view");
                _listOf = Expression.Lambda<Func<ReadOnlyList<Beaver>, List<Beaver>>>(Expression.Field(view, list), view).Compile();
                _versionOf = Reflect.FieldGetter<int>(typeof(List<Beaver>), "_version");
                _itemsOf = Reflect.FieldGetter<Beaver[]>(typeof(List<Beaver>), "_items");
            }
            catch (Exception)
            {
                _listOf = null;
                _versionOf = null;
                _itemsOf = null;
            }
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
                "each); {4} moved in; the dweller lists beside the game's were rebuilt {5} times; {6} searches over lists of " +
                "another kind were left to the game{7}",
                _searches, _looked, ms, _searches > 0 ? ms / _searches : 0, _movedIn, _rebuilds, _otherLists,
                _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _searches = _looked = _movedIn = _rebuilds = _otherLists = _stopwatchTicks = 0;
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
            if (_versionOf != null)
            {
                List<Beaver> list = _listOf(beavers);
                Beside beside = BesideOf(list, count);
                Dweller[] dwellers = beside.Dwellers;
                for (int i = 0; i < count; i++)
                {
                    Dweller dweller = dwellers[i];
                    _looked++;
                    if (IsLooking(dweller) && CanAssign(dwelling, dweller))
                    {
                        return dweller;
                    }
                }
                return null;
            }
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

        // The dwellers beside a list, rebuilt when the list changed since (its change counter moves on every
        // addition, removal and replacement).
        private static Beside BesideOf(List<Beaver> list, int count)
        {
            if (!Besides.TryGetValue(list, out Beside beside))
            {
                beside = new Beside();
                Besides[list] = beside;
            }
            int version = _versionOf(list);
            if (beside.Version != version || beside.Count != count)
            {
                if (beside.Dwellers.Length < count)
                {
                    beside.Dwellers = new Dweller[count + count / 4 + 4];
                }
                Beaver[] items = _itemsOf(list);
                for (int i = 0; i < count; i++)
                {
                    beside.Dwellers[i] = DwellerOf(items[i]);
                }
                for (int i = count; i < beside.Dwellers.Length; i++)
                {
                    beside.Dwellers[i] = null;
                }
                beside.Count = count;
                beside.Version = version;
                _rebuilds++;
            }
            return beside;
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
