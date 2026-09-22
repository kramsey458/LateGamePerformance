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
// a lookup throws.
internal static class HomeSearchTests
{
    private sealed class World
    {
        public readonly Dictionary<Beaver, Dweller> Components = new Dictionary<Beaver, Dweller>();
        public readonly HashSet<Dweller> Looking = new HashSet<Dweller>();
        public readonly HashSet<Dweller> MayMoveIn = new HashSet<Dweller>();
        public readonly List<Dweller> Assigned = new List<Dweller>();
        public int Lookups;
    }

    public static void Run(Action<bool, string> check)
    {
        World world = new World();
        Random random = new Random(77);
        List<Beaver> adults = MakeBeavers(world, 300), children = MakeBeavers(world, 60);
        ReadOnlyList<Beaver> adultList = adults.AsReadOnlyList(), childList = children.AsReadOnlyList();
        object dwelling = new object();

        Bind(world, verify: false);
        bool same = true;
        int moves = 0, nobody = 0;
        for (int round = 0; round < 500; round++)
        {
            Dweller expected = Script(world, random, adults, children, out IEnumerable<Beaver> primary, out IEnumerable<Beaver> secondary,
                adultList, childList);
            world.Assigned.Clear();
            bool result = false;
            bool ranOriginal = HomeSearch.AssignPrefix(dwelling, primary, secondary, ref result);
            same &= !ranOriginal && result == (expected != null) && world.Assigned.Count == (expected == null ? 0 : 1) &&
                    (expected == null || ReferenceEquals(world.Assigned[0], expected));
            if (expected != null) moves++;
            else nobody++;
        }
        check(same, $"home search: the same beaver moves in as in the game's walk, or nobody ({moves} moves, {nobody} searches with nobody to move)");
        check(world.Lookups == 360, $"home search: one component lookup per beaver for all 500 searches ({world.Lookups} lookups)");
        string stats = HomeSearch.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(stats.StartsWith("HomeSearch: 500 searches") && HomeSearch.IsActive, "home search: every search went through the mod, which is still active");

        // Verify mode: the game's walk runs as well, with fresh lookups, and must agree.
        Bind(world, verify: true);
        bool verifiedSame = true;
        for (int round = 0; round < 60; round++)
        {
            Dweller expected = Script(world, random, adults, children, out IEnumerable<Beaver> primary, out IEnumerable<Beaver> secondary,
                adultList, childList);
            world.Assigned.Clear();
            bool result = false;
            verifiedSame &= !HomeSearch.AssignPrefix(dwelling, primary, secondary, ref result) && result == (expected != null);
        }
        stats = HomeSearch.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(verifiedSame && stats.Contains("60 searches") && stats.Contains("verify mismatches 0"),
            "home search: verify mode agrees with the game's walk on every search");

        // A beaver that is gone leaves the table.
        HomeSearch.DeleteEntityPostfix(world.Components[adults[0]]);
        check(HomeSearch.TableSize == 359, $"home search: a deleted beaver leaves the table ({HomeSearch.TableSize} left)");

        // Lists of another kind (another mod's) are walked by the game.
        bool other = false;
        check(HomeSearch.AssignPrefix(dwelling, new List<Beaver>(adults), childList, ref other) && !other,
            "home search: a list of another kind is left to the game's own walk");

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

    private static void Bind(World world, bool verify)
    {
        // Binds the game's own methods first (as the mod does), then swaps in the scripted ones.
        HomeSearch.CreateFeature(new Config { HomeSearchVerify = verify }).Patches[0].Target();
        HomeSearch.Activate();
        HomeSearch.DwellerOf = beaver =>
        {
            world.Lookups++;
            return world.Components[beaver];
        };
        HomeSearch.IsLooking = dweller => world.Looking.Contains(dweller);
        HomeSearch.CanAssign = (_, dweller) => world.MayMoveIn.Contains(dweller);
        HomeSearch.Assign = (_, dweller) => world.Assigned.Add(dweller);
    }

    // Mostly nobody is looking, as in a settled colony; sometimes a few are, and not all of those may move in.
    // Returns what the game's walk (Adults.Concat(Children) or the other way round, as decompiled) would pick.
    private static Dweller Script(World world, Random random, List<Beaver> adults, List<Beaver> children,
        out IEnumerable<Beaver> primary, out IEnumerable<Beaver> secondary, ReadOnlyList<Beaver> adultList, ReadOnlyList<Beaver> childList)
    {
        world.Looking.Clear();
        world.MayMoveIn.Clear();
        int lookers = random.Next(4) == 0 ? random.Next(1, 6) : 0;
        for (int k = 0; k < lookers; k++)
        {
            Beaver beaver = random.Next(5) == 0 ? children[random.Next(children.Count)] : adults[random.Next(adults.Count)];
            Dweller dweller = world.Components[beaver];
            world.Looking.Add(dweller);
            if (random.Next(3) != 0) world.MayMoveIn.Add(dweller);
        }
        bool adultsFirst = random.Next(2) == 0;
        primary = adultsFirst ? adultList : childList;
        secondary = adultsFirst ? childList : adultList;
        foreach (Beaver item in primary.Concat(secondary))
        {
            Dweller component = world.Components[item];
            if (world.Looking.Contains(component) && world.MayMoveIn.Contains(component)) return component;
        }
        return null;
    }
}
