using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.Beavers;
using Timberborn.Common;
using Timberborn.DwellingSystem;

// The home search against a model of the game's walk. The game's own predicates need a live entity (a Unity object
// behind every "has a home"), so here they are scripted per beaver; what is checked is the part the mod changes:
// which beaver is picked over the game's two real list types, in which order they are asked, that one component
// lookup per beaver is enough for any number of searches, what a list of another kind does, and what happens when
// a lookup throws. The script changes who is looking only the way the game can: through a home changing, which
// in the game passes the mod's hooks on Dwelling.AssignDweller / UnassignDweller / Dweller.AssignToHome (called
// here directly, as Harmony would); who may move in (the second question, always asked) changes freely.
internal static class HomeSearchTests
{
    private sealed class World
    {
        public readonly Dictionary<Beaver, Dweller> Components = new Dictionary<Beaver, Dweller>();
        public readonly HashSet<Dweller> Looking = new HashSet<Dweller>();
        public readonly HashSet<Dweller> MayMoveIn = new HashSet<Dweller>();
        public readonly List<Dweller> Assigned = new List<Dweller>();
        public readonly List<Dweller> CanAssignAsked = new List<Dweller>();
        public int Lookups;
        public int Asked;
    }

    // What the game does around a change of homes: the hooks before and after.
    private static void HomesChange()
    {
        HomeSearch.HomesChangingPrefix();
        HomeSearch.HomesChangedPostfix();
    }

    public static void Run(Action<bool, string> check)
    {
        World world = new World();
        Random random = new Random(77);
        List<Beaver> adults = MakeBeavers(world, 300), children = MakeBeavers(world, 60);
        ReadOnlyList<Beaver> adultList = adults.AsReadOnlyList(), childList = children.AsReadOnlyList();
        object dwelling = new object();

        Bind(world, verify: false);
        bool same = true, sameAsks = true;
        int moves = 0, nobody = 0, gameAsks = 0;
        for (int round = 0; round < 500; round++)
        {
            Dweller expected = Script(world, random, adults, children, out IEnumerable<Beaver> primary, out IEnumerable<Beaver> secondary,
                adultList, childList, out int asks, out List<Dweller> expectedCanAssign);
            world.Assigned.Clear();
            world.CanAssignAsked.Clear();
            bool result = false;
            bool ranOriginal = HomeSearch.AssignPrefix(dwelling, primary, secondary, ref result);
            same &= !ranOriginal && result == (expected != null) && world.Assigned.Count == (expected == null ? 0 : 1) &&
                    (expected == null || ReferenceEquals(world.Assigned[0], expected));
            // The second question: asked of the same beavers, in the same order, as the game's `&&` asks it.
            sameAsks &= world.CanAssignAsked.SequenceEqual(expectedCanAssign);
            gameAsks += asks;
            if (expected != null) moves++;
            else nobody++;
        }
        check(same, $"home search: the same beaver moves in as in the game's walk, or nobody ({moves} moves, {nobody} searches with nobody to move)");
        check(sameAsks, "home search: 'may this beaver move in' is asked of the same beavers in the same order as in the game's walk");
        check(world.Lookups == 360, $"home search: one component lookup per beaver for all 500 searches ({world.Lookups} lookups)");
        check(world.Asked * 4 < gameAsks,
            $"home search: 'looking for a better home' is asked again only after a home changed ({world.Asked} times where the game asks {gameAsks})");
        string stats = HomeSearch.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(stats.StartsWith("HomeSearch: 500 searches") && stats.Contains($"asking {world.Asked} of them") && HomeSearch.IsActive,
            "home search: every search went through the mod, which is still active");

        // Verify mode: the game's walk runs as well, with fresh lookups, and must agree; every kept answer is asked
        // again and must be the game's.
        Bind(world, verify: true);
        bool verifiedSame = true;
        for (int round = 0; round < 60; round++)
        {
            Dweller expected = Script(world, random, adults, children, out IEnumerable<Beaver> primary, out IEnumerable<Beaver> secondary,
                adultList, childList, out _, out _);
            world.Assigned.Clear();
            bool result = false;
            verifiedSame &= !HomeSearch.AssignPrefix(dwelling, primary, secondary, ref result) && result == (expected != null);
        }
        stats = HomeSearch.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(verifiedSame && stats.Contains("60 searches") && stats.Contains("verify mismatches 0, kept answers that differed from the game's 0"),
            "home search: verify mode agrees with the game's walk on every search, and every kept answer is the game's");

        // With the list's own change counter readable (0.4.25) the tables are the two lists' companions; otherwise one
        // entry per beaver, and a beaver that is gone leaves it.
        HomeSearch.DeleteEntityPostfix(world.Components[adults[0]]);
        check(HomeSearch.UsesListVersions ? HomeSearch.TableSize == 2 : HomeSearch.TableSize == 359,
            $"home search: {(HomeSearch.UsesListVersions ? "one companion per list" : "a deleted beaver leaves the table")} ({HomeSearch.TableSize})");
        // The lists change: a beaver dies, two are born. The companions are rebuilt once each, and the next searches
        // ask the new lists.
        Bind(world, verify: false);
        HomeSearch.TakeStatsLine();
        int lookupsBefore = world.Lookups;
        adults.RemoveAt(3);
        adults.Add(MakeBeavers(world, 1)[0]);
        children.Add(MakeBeavers(world, 1)[0]);
        world.Looking.Clear();
        world.MayMoveIn.Clear();
        world.Assigned.Clear();
        bool noPick = false;
        bool ranNobody = HomeSearch.AssignPrefix(dwelling, adultList, childList, ref noPick);
        Dweller newcomer = world.Components[adults[adults.Count - 1]];
        world.Looking.Add(newcomer);
        world.MayMoveIn.Add(newcomer);
        HomesChange();
        bool found = false;
        bool ranFound = HomeSearch.AssignPrefix(dwelling, adultList, childList, ref found);
        string changedStats = HomeSearch.TakeStatsLine();
        Console.WriteLine("     " + changedStats);
        check(!ranNobody && !noPick && !ranFound && found && world.Assigned.Count == 1 && ReferenceEquals(world.Assigned[0], newcomer) &&
              world.Lookups == lookupsBefore + (HomeSearch.UsesListVersions ? 361 : 2) &&
              changedStats.Contains(HomeSearch.UsesListVersions ? "rebuilt 2 times" : "rebuilt 0 times"),
            $"home search: after a beaver left and two were born the next searches see the new lists, rebuilt once each ({world.Lookups - lookupsBefore} lookups)");

        // Lists of another kind (another mod's) are walked by the game.
        bool other = false;
        check(HomeSearch.AssignPrefix(dwelling, new List<Beaver>(adults), childList, ref other) && !other,
            "home search: a list of another kind is left to the game's own walk");

        // A verify key is each player's own, so it must not change who moves in. Forced difference: after the mod's
        // table was built, one beaver's component is replaced behind it by one that is looking and may move in; the
        // mod still asks the old one (not looking) and moves in the next beaver that may, the game's walk looks up the
        // new one. The same beaver must move in with the key off and on.
        Beaver swapped = adults[10];
        Dweller original = world.Components[swapped];
        Dweller impostor = (Dweller)RuntimeHelpers.GetUninitializedObject(typeof(Dweller));
        Dweller later = world.Components[adults[20]];
        Dweller MovedIn(bool verify)
        {
            Bind(world, verify);
            world.Components[swapped] = original;
            world.Looking.Clear();
            world.MayMoveIn.Clear();
            bool none = false;
            HomeSearch.AssignPrefix(dwelling, adultList, childList, ref none);   // builds the mod's table; nobody looks
            world.Components[swapped] = impostor;
            world.Looking.Add(impostor);
            world.MayMoveIn.Add(impostor);
            world.Looking.Add(later);
            world.MayMoveIn.Add(later);
            HomesChange();
            world.Assigned.Clear();
            bool moved = false;
            bool ranGame = HomeSearch.AssignPrefix(dwelling, adultList, childList, ref moved);
            world.Components[swapped] = original;
            world.Looking.Clear();
            world.MayMoveIn.Clear();
            return !ranGame && moved && world.Assigned.Count == 1 ? world.Assigned[0] : null;
        }
        Dweller withoutVerify = MovedIn(false);
        Dweller withVerify = MovedIn(true);
        string verifyStats = HomeSearch.TakeStatsLine();
        Console.WriteLine("     " + verifyStats);
        check(withoutVerify == later && withVerify == later && verifyStats.Contains("verify mismatches 1"),
            "home search verify: the game's walk picked another beaver and that was logged, and the same beaver moves in as " +
            $"with verify off ({(withVerify == later ? "the mod's pick" : withVerify == impostor ? "the game's pick" : "nobody")})");

        // A kept answer is used until a home changes. Forced here: a beaver starts looking with no home changing (the
        // game has no such path). The mod keeps its answer and does not ask it the second question; verify mode asks
        // again, counts and logs the difference, and the kept answer is still what the search uses. Once a home
        // changes, the beaver is asked again.
        Dweller sneaky = world.Components[adults[30]];
        int KeptAnswerSearch(bool verify, out string keptStats, out bool askedSneaky)
        {
            Bind(world, verify);
            world.Looking.Clear();
            world.MayMoveIn.Clear();
            bool nobodyYet = false;
            HomeSearch.AssignPrefix(dwelling, adultList, childList, ref nobodyYet);   // every beaver asked once
            HomeSearch.TakeStatsLine();
            world.Looking.Add(sneaky);                                              // no hook
            world.CanAssignAsked.Clear();
            int askedBefore = world.Asked;
            bool stillNobody = false;
            bool ranGame = HomeSearch.AssignPrefix(dwelling, adultList, childList, ref stillNobody);
            keptStats = HomeSearch.TakeStatsLine();
            askedSneaky = world.CanAssignAsked.Contains(sneaky);
            return ranGame || stillNobody ? -1 : world.Asked - askedBefore;
        }
        int keptAsks = KeptAnswerSearch(false, out string keptOff, out bool askedOff);
        Console.WriteLine("     " + keptOff);
        check(keptAsks == 0 && !askedOff && keptOff.Contains("went through 361 beavers") && keptOff.Contains("asking 0 of them"),
            "home search: with no home changed, every answer is kept, and a beaver whose kept answer is 'not looking' is not asked " +
            "whether it may move in");
        KeptAnswerSearch(true, out string keptOn, out _);
        Console.WriteLine("     " + keptOn);
        world.CanAssignAsked.Clear();
        HomesChange();
        bool afterChange = false;
        HomeSearch.AssignPrefix(dwelling, adultList, childList, ref afterChange);
        string changedHomeStats = HomeSearch.TakeStatsLine();
        Console.WriteLine("     " + changedHomeStats);
        // (After the home changed the looking beaver is asked whether it may move in twice: by the mod, then by verify's
        // walk of the game's code.)
        check(keptOn.Contains("verify mismatches 1, kept answers that differed from the game's 1") &&
              changedHomeStats.Contains("asking 361 of them") && changedHomeStats.Contains("kept answers that differed from the game's 1") &&
              world.CanAssignAsked.SequenceEqual(new[] { sneaky, sneaky }),
            "home search verify: a kept answer that differs from the game's is counted and logged; after a home changed every " +
            "beaver is asked again and the looking one is asked whether it may move in");

        // Another mod patches the question (or what it reads): no answer is kept, every beaver is asked on every
        // search, as up to 0.4.27. This mod's own patches do not count.
        foreach (bool foreign in new[] { true, false })
        {
            string owner = foreign ? "some.other.mod" : Plugin.HarmonyId + ".Something";
            Bind(world, verify: false, method => method.Name == "IsLookingForBetterHome"
                ? new[] { (owner, "Other.Mod.HomePatch.Prefix") }
                : null);
            world.Looking.Clear();
            world.MayMoveIn.Clear();
            for (int search = 0; search < 3; search++)
            {
                bool nobodyHere = false;
                HomeSearch.AssignPrefix(dwelling, adultList, childList, ref nobodyHere);
            }
            string standStats = HomeSearch.TakeStatsLine();
            Console.WriteLine("     " + standStats);
            check(foreign
                    ? standStats.Contains("went through 1083 beavers") && standStats.Contains("asking 1083 of them") &&
                      standStats.Contains("no answers are kept because another mod patches Dweller.IsLookingForBetterHome (some.other.mod, " +
                                          "Other.Mod.HomePatch.Prefix)")
                    : standStats.Contains("asking 361 of them") && !standStats.Contains("no answers are kept"),
                foreign
                    ? "home search: another mod's patch on the question means every beaver is asked on every search"
                    : "home search: this mod's own patches on the question do not stop it keeping answers");
        }
        Bind(world, verify: false);

        // A lookup that throws hands the search to the game and switches the feature off.
        world.Looking.Add(world.Components[adults[5]]);
        HomeSearch.CanAssign = (_, _) => throw new InvalidOperationException("boom");
        bool thrown = false;
        bool handedOver = HomeSearch.AssignPrefix(dwelling, adultList, childList, ref thrown);
        check(handedOver && !thrown && !HomeSearch.IsActive, "home search: a throwing lookup switches the feature off and hands the search to the game");
    }

    private static List<Beaver> MakeBeavers(World world, int count)
    {
        List<Beaver> beavers = new List<Beaver>();
        for (int i = 0; i < count; i++)
        {
            Beaver beaver = (Beaver)RuntimeHelpers.GetUninitializedObject(typeof(Beaver));
            world.Components[beaver] = (Dweller)RuntimeHelpers.GetUninitializedObject(typeof(Dweller));
            beavers.Add(beaver);
        }
        return beavers;
    }

    private static void Bind(World world, bool verify, Func<System.Reflection.MethodBase, IEnumerable<(string, string)>> patchesOn = null)
    {
        // Binds the game's own methods first (as the mod does), then swaps in the scripted ones. Harmony's patch
        // registry cannot run here: nobody else patches the question unless a test says so.
        HomeSearch.CreateFeature(new Config { HomeSearchVerify = verify }).Patches[0].Target();
        HomeSearch.PatchesOn = patchesOn ?? (_ => null);
        HomeSearch.Activate();
        HomeSearch.DwellerOf = beaver =>
        {
            world.Lookups++;
            return world.Components[beaver];
        };
        HomeSearch.IsLooking = dweller =>
        {
            world.Asked++;
            return world.Looking.Contains(dweller);
        };
        HomeSearch.CanAssign = (_, dweller) =>
        {
            world.CanAssignAsked.Add(dweller);
            return world.MayMoveIn.Contains(dweller);
        };
        // A move-in goes through Dwelling.AssignDweller in the game, and so past the hooks.
        HomeSearch.Assign = (_, dweller) =>
        {
            HomeSearch.HomesChangingPrefix();
            world.Assigned.Add(dweller);
            HomeSearch.HomesChangedPostfix();
        };
    }

    // Mostly nobody is looking, as in a settled colony; sometimes a few are, and not all of those may move in.
    // Returns what the game's walk (Adults.Concat(Children) or the other way round, as decompiled) would pick. Who
    // is looking changes only with a home (the hooks); who may move in changes every search.
    private static Dweller Script(World world, Random random, List<Beaver> adults, List<Beaver> children,
        out IEnumerable<Beaver> primary, out IEnumerable<Beaver> secondary, ReadOnlyList<Beaver> adultList, ReadOnlyList<Beaver> childList,
        out int asks, out List<Dweller> canAssign)
    {
        world.MayMoveIn.Clear();
        if (random.Next(3) == 0)
        {
            HashSet<Dweller> before = new HashSet<Dweller>(world.Looking);
            world.Looking.Clear();
            int lookers = random.Next(4) == 0 ? random.Next(1, 6) : 0;
            for (int k = 0; k < lookers; k++)
            {
                Beaver beaver = random.Next(5) == 0 ? children[random.Next(children.Count)] : adults[random.Next(adults.Count)];
                world.Looking.Add(world.Components[beaver]);
            }
            if (!before.SetEquals(world.Looking))
            {
                HomesChange();
            }
        }
        foreach (Dweller dweller in world.Looking)
        {
            if (random.Next(3) != 0) world.MayMoveIn.Add(dweller);
        }
        bool adultsFirst = random.Next(2) == 0;
        primary = adultsFirst ? adultList : childList;
        secondary = adultsFirst ? childList : adultList;
        asks = 0;
        canAssign = new List<Dweller>();
        foreach (Beaver item in primary.Concat(secondary))
        {
            Dweller component = world.Components[item];
            asks++;
            if (!world.Looking.Contains(component)) continue;
            canAssign.Add(component);
            if (world.MayMoveIn.Contains(component)) return component;
        }
        return null;
    }
}
