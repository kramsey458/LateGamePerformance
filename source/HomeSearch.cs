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
    // Since 0.4.28 the first question is not asked again while its answer cannot have changed (about 950 searches
    // per 1000 ticks asked 355 beavers each for 5 to 12 move-ins). Whether a beaver is looking for a better home
    // (Dweller.IsLookingForBetterHome) is read from its home (Dweller.Home, and whether the home's game object still
    // exists), the home's numbers of adult and child dwellers (two private sets), the home's slot numbers (set once,
    // in Awake) and whether the beaver is an adult (set once, in Awake). The home and the two sets change only in
    // Dwelling.AssignDweller, Dwelling.UnassignDweller and Dweller.AssignToHome (which the first calls; the only
    // other writers of Home are loading a save, before a scene's first tick). Every other path goes through them:
    // the game's (a death or deletion, a dwelling blocked, demolished or deleted, an unreachable home, a birth into
    // a home, a child growing up into a new entity) and other mods' (OptimizedLocalHousing moves beavers with
    // Dweller.UnassignFromHome and Dwelling.AssignDweller). A counter moves before and after each of those three
    // calls, on a dweller's deletion and in every new scene; each answer is kept beside the list with the counter's
    // value when it was given, and used only while the counter still has that value and the list is unchanged (a
    // changed list asks all its beavers again). A search still asks CanAssignDweller, live, of exactly the beavers
    // that are looking, in list order, and stops at the same first hit: the same calls the game's `&&` makes. A home
    // whose game object died while a beaver still pointed at it would change an answer without such a call, but
    // the game never destroys a dwelling that way: deleting it first leaves its finished state, which unassigns
    // every dweller. If another mod patches the question, anything it reads, or CanAssignDweller, no answer is kept
    // and every beaver is asked on every search, as up to 0.4.27.
    //
    // HomeSearchVerify runs the game's walk as well (fresh lookups, through the same concatenation) and compares
    // the beaver picked; a mismatch is logged and counted, and the mod's pick still moves in (up to 0.4.26 the
    // game's did, so a player with the setting on could differ from the others in co-op). It also asks every beaver
    // whose answer is kept and counts the answers that differ, which are still used.
    internal static class HomeSearch
    {
        private const string AssignerType = "Timberborn.DwellingSystem.DwellerHomeAssigner";
        private const string DwellingType = "Timberborn.DwellingSystem.AutoAssignableDwelling";

        // The dwellers beside one of the game's lists, valid for one value of the list's change counter, with each
        // one's answer to "looking for a better home?" and the value of the homes counter it was given at.
        private sealed class Beside
        {
            public Dweller[] Dwellers = new Dweller[0];
            public bool[] Looking = new bool[0];
            public long[] AskedAt = new long[0];
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

        // Moves before and after every change of a home or of a dwelling's dwellers: an answer is kept for one value.
        // Starts at 1, so an answer stamped 0 is never current.
        private static long _homesChanged = 1;
        // False when another mod patches the question or what it reads (or the game's question is not the one this
        // was written for): every beaver is asked on every search.
        private static bool _keepAnswers;
        private static bool _checkedForeignPatches;
        private static string _notKeptBecause;
        private static MethodBase[] _questionMethods = new MethodBase[0];
        private static bool _questionFound;

        private static bool _active;
        private static bool _verify;

        private static long _searches;
        private static long _looked;
        private static long _asked;
        private static long _movedIn;
        private static long _rebuilds;
        private static long _otherLists;
        private static long _stopwatchTicks;
        private static long _verifyMismatches;
        private static long _staleAnswers;

        // Every patch on a method, as (Harmony id, "Namespace.Type.Method" of the patch); swapped by the tests, where
        // Harmony's patch registry cannot run.
        internal static Func<MethodBase, IEnumerable<(string Owner, string Patch)>> PatchesOn = HarmonyPatchesOn;

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
            // The three methods that change a home or a dwelling's dwellers. The counter moves before (so an exception
            // part-way leaves no answer current) and after (so nothing asked while the change ran stays current).
            // Required: without them no kept answer could be trusted.
            foreach ((Type type, string method) in new[]
                     {
                         (typeof(Dwelling), "AssignDweller"), (typeof(Dwelling), "UnassignDweller"), (typeof(Dweller), "AssignToHome")
                     })
            {
                feature.Patches.Add(new PatchSpec
                {
                    Name = type.Name + "." + method,
                    Required = true,
                    Target = () => AccessTools.Method(type, method),
                    Prefix = Reflect.Own(self, nameof(HomesChangingPrefix)),
                    Postfix = Reflect.Own(self, nameof(HomesChangedPostfix))
                });
            }
            return feature;
        }

        public static void Activate()
        {
            SceneCreated();
            _keepAnswers = true;
            _checkedForeignPatches = false;
            _notKeptBecause = null;
            _active = true;
        }

        public static bool IsActive => _active;

        public static void SceneCreated()
        {
            Besides.Clear();
            Dwellers.Clear();
            _homesChanged++;
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
            // The question and everything it reads, as decompiled; the kept answers depend on all of them being the
            // game's own. A game version that lacks any of them is not the one the answers were worked out for.
            List<MethodBase> question = new List<MethodBase>
            {
                AccessTools.Method(typeof(Dweller), "IsLookingForBetterHome"), AccessTools.Method(dwelling, "CanAssignDweller")
            };
            foreach (string property in new[] { "Home", "HasHome", "HomeIsOverpopulated", "HomeIsUnderpopulated" })
            {
                question.Add(AccessTools.PropertyGetter(typeof(Dweller), property));
            }
            foreach (string property in new[]
                     {
                         "NumberOfDwellers", "NumberOfAdultDwellers", "NumberOfChildDwellers", "OverpopulatedByAdults",
                         "OverpopulatedByChildren", "FreeAdultSlots", "AdultSlots", "DesiredNumberOfChildren"
                     })
            {
                question.Add(AccessTools.PropertyGetter(typeof(Dwelling), property));
            }
            _questionFound = !question.Contains(null);
            question.RemoveAll(method => method == null);
            _questionMethods = question.ToArray();
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
                "HomeSearch: {0} searches for a beaver to move into a home went through {1} beavers in {2:0.0} ms ({3:0.000} ms " +
                "each), asking {8} of them whether they were looking for a better home (the other answers were kept from " +
                "earlier searches, no home having changed since){9}; {4} moved in; the dweller lists beside the game's were " +
                "rebuilt {5} times; {6} searches over lists of another kind were left to the game{7}",
                _searches, _looked, ms, _searches > 0 ? ms / _searches : 0, _movedIn, _rebuilds, _otherLists,
                _verify ? $"; verify mismatches {_verifyMismatches}, kept answers that differed from the game's {_staleAnswers}" : "",
                _asked, _notKeptBecause != null ? "; no answers are kept because " + _notKeptBecause : "");
            _searches = _looked = _asked = _movedIn = _rebuilds = _otherLists = _stopwatchTicks = 0;
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
                if (!_checkedForeignPatches)
                {
                    _checkedForeignPatches = true;
                    CheckQuestion();
                }
                long started = Stopwatch.GetTimestamp();
                Dweller pick = Scan(dwelling, primary) ?? Scan(dwelling, secondary);
                if (_verify)
                {
                    // Counted and logged only: the setting is each player's own, so the mod's pick moves in either way.
                    Dweller expected = GamesPick(dwelling, primaryBeavers, secondaryBeavers);
                    if (!ReferenceEquals(pick, expected))
                    {
                        _verifyMismatches++;
                        if (_verifyMismatches <= 10)
                        {
                            Log.Warning("HomeSearch verify: the mod moves in " + Describe(pick) + " where the game would move in " +
                                        Describe(expected) + ".");
                        }
                    }
                    VerifyKeptAnswers(primary);
                    VerifyKeptAnswers(secondary);
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
                TurnedOff.Report("HomeSearch",
                    "HomeSearch failed and turned itself off for this session: " + exception);
                return true;
            }
        }

        // Before and after Dwelling.AssignDweller, Dwelling.UnassignDweller and Dweller.AssignToHome.
        internal static void HomesChangingPrefix()
        {
            _homesChanged++;
        }

        internal static void HomesChangedPostfix()
        {
            _homesChanged++;
        }

        internal static void DeleteEntityPostfix(Dweller __instance)
        {
            // A dweller that is deleted leaves its home through UnassignDweller already; this is only a second fence.
            _homesChanged++;
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

        // The game's loop over one list: the first beaver that is looking and may move in. The first question is
        // answered from the kept answer while it is current, otherwise asked (and kept); the second is always asked,
        // and only of a beaver that is looking, as the game's `&&` does.
        private static Dweller Scan(object dwelling, ReadOnlyList<Beaver> beavers)
        {
            int count = beavers.Count;
            if (_versionOf != null)
            {
                List<Beaver> list = _listOf(beavers);
                Beside beside = BesideOf(list, count);
                Dweller[] dwellers = beside.Dwellers;
                bool[] looking = beside.Looking;
                long[] askedAt = beside.AskedAt;
                long now = _homesChanged;
                bool keep = _keepAnswers;
                for (int i = 0; i < count; i++)
                {
                    Dweller dweller = dwellers[i];
                    _looked++;
                    bool isLooking;
                    if (keep && askedAt[i] == now)
                    {
                        isLooking = looking[i];
                    }
                    else
                    {
                        isLooking = IsLooking(dweller);
                        looking[i] = isLooking;
                        askedAt[i] = now;
                        _asked++;
                    }
                    if (isLooking && CanAssign(dwelling, dweller))
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
                _asked++;
                if (IsLooking(dweller) && CanAssign(dwelling, dweller))
                {
                    return dweller;
                }
            }
            return null;
        }

        // Once, at the first search (every mod has patched by then): the answers are kept only if the question and
        // everything it reads are the game's own. Never lets an exception out; if the check fails, nothing is kept.
        private static void CheckQuestion()
        {
            try
            {
                if (!_questionFound)
                {
                    _notKeptBecause = "the game's question is not the one this version was written for";
                }
                else
                {
                    foreach (MethodBase method in _questionMethods)
                    {
                        IEnumerable<(string Owner, string Patch)> patches = PatchesOn(method);
                        if (patches == null)
                        {
                            continue;
                        }
                        foreach ((string owner, string patch) in patches)
                        {
                            if (!owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                            {
                                _notKeptBecause = $"another mod patches {method.DeclaringType?.Name}.{method.Name} ({owner}, {patch})";
                                break;
                            }
                        }
                        if (_notKeptBecause != null)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                _notKeptBecause = "the other mods' patches could not be read (" + exception.Message + ")";
            }
            if (_notKeptBecause != null)
            {
                _keepAnswers = false;
                Log.Info("HomeSearch: " + _notKeptBecause + ", so every beaver is asked whether it is looking for a better " +
                         "home on every search, as up to 0.4.27. The result is the same.");
            }
        }

        // Verify only: every kept answer that is current is asked again; a difference is counted and logged, and the
        // kept answer was used all the same.
        private static void VerifyKeptAnswers(ReadOnlyList<Beaver> beavers)
        {
            if (_versionOf == null || !_keepAnswers || !Besides.TryGetValue(_listOf(beavers), out Beside beside))
            {
                return;
            }
            for (int i = 0; i < beside.Count; i++)
            {
                if (beside.AskedAt[i] != _homesChanged)
                {
                    continue;
                }
                bool now = IsLooking(beside.Dwellers[i]);
                if (now != beside.Looking[i])
                {
                    _staleAnswers++;
                    if (_staleAnswers <= 10)
                    {
                        Log.Warning($"HomeSearch verify: the kept answer for {Describe(beside.Dwellers[i])} was " +
                                    $"'{(beside.Looking[i] ? "looking" : "not looking")}' where the game now answers " +
                                    $"'{(now ? "looking" : "not looking")}'.");
                    }
                }
            }
        }

        private static IEnumerable<(string Owner, string Patch)> HarmonyPatchesOn(MethodBase method)
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
                    beside.Looking = new bool[beside.Dwellers.Length];
                    beside.AskedAt = new long[beside.Dwellers.Length];
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
                // A changed list asks all its beavers again.
                Array.Clear(beside.AskedAt, 0, beside.AskedAt.Length);
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
