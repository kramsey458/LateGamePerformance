using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.Carrying;
using Timberborn.Common;
using Timberborn.Goods;
using Timberborn.InventorySystem;

// The tree search's stop for a building with no room (0.4.28), against the game's real Inventory and
// CarryAmountCalculator; the guard that stands it down when another mod patches what it rests on; and the walk kept
// for every search (no garbage per search).
internal static class YielderSearchTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        FullMeansNoRoom(check);
        Guard(check);
        SharedWalk(check);
    }

    // The game's goods, as far as CarryAmountCalculator asks: a weight per good.
    private sealed class GoodBook : IGoodService
    {
        private readonly Dictionary<string, GoodSpec> _specs = new Dictionary<string, GoodSpec>();

        public GoodBook(IEnumerable<string> goods, Random random)
        {
            foreach (string good in goods)
            {
                GoodSpec spec = (GoodSpec)RuntimeHelpers.GetUninitializedObject(typeof(GoodSpec));
                Set(spec, "<Id>k__BackingField", good);
                Set(spec, "<Weight>k__BackingField", 1 + random.Next(4));
                _specs[good] = spec;
            }
        }

        public ReadOnlyList<string> Goods => throw new NotSupportedException();
        public bool HasGood(string id) => _specs.ContainsKey(id);
        public GoodSpec GetGoodOrNull(string id) => _specs.TryGetValue(id, out GoodSpec spec) ? spec : null;
        public GoodSpec GetGood(string id) => _specs[id];
        public IEnumerable<string> GetGoodsForGroup(string groupId) => throw new NotSupportedException();
        public IEnumerable<string> GetGoodsForType(string goodType) => throw new NotSupportedException();
    }

    // A capacity rule from a mod, with any answer at all.
    private sealed class AnyDisallower : IGoodDisallower
    {
        private readonly int _allowed;

        public AnyDisallower(int allowed)
        {
            _allowed = allowed;
        }

#pragma warning disable CS0067
        public event EventHandler<DisallowedGoodsChangedEventArgs> DisallowedGoodsChanged;
#pragma warning restore CS0067

        public int AllowedAmount(string goodId) => _allowed;
    }

    private static void Set(object target, string field, object value)
    {
        FieldInfo info = null;
        for (Type type = target.GetType(); type != null && info == null; type = type.BaseType)
        {
            info = type.GetField(field, Any | BindingFlags.DeclaredOnly);
        }
        if (info == null)
        {
            throw new MissingFieldException(target.GetType().Name, field);
        }
        info.SetValue(target, value);
    }

    // The heart of the stop: a fully reserved inventory has no room for any good, so the game's carry amount is 0 for
    // every plant, whatever the plant yields, the worker lifts, or the inventory's capacity rule says. Random
    // inventories of the game's own class, with stock, reserved capacity, allowed goods and capacity rules of every kind.
    private static void FullMeansNoRoom(Action<bool, string> check)
    {
        Random random = new Random(4242);
        string[] goods = { "Log", "Pine", "Resin", "Berries", "Maple", "NotStoredHere" };
        CarryAmountCalculator calculator = new CarryAmountCalculator(new GoodBook(goods, random));
        int full = 0, withRoomSomewhere = 0, roomWhenFull = 0, carriedWhenFull = 0;
        for (int round = 0; round < 3000; round++)
        {
            Inventory inventory = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
            int capacity = 1 + random.Next(60);
            StorableGoodRegistry allowed = new StorableGoodRegistry();
            List<StorableGoodAmount> amounts = new List<StorableGoodAmount>();
            GoodRegistry storage = new GoodRegistry();
            GoodRegistry reserved = new GoodRegistry();
            int kinds = 1 + random.Next(4);
            for (int i = 0; i < kinds; i++)
            {
                string good = goods[(round + i) % 5];
                StorableGood storable = random.Next(3) == 0 ? StorableGood.CreateAsGivable(good)
                    : random.Next(2) == 0 ? StorableGood.CreateAsTakeable(good) : StorableGood.CreateGiveableAndTakeable(good);
                amounts.Add(new StorableGoodAmount(storable, random.Next(2) == 0 ? capacity : random.Next(1, 3 * capacity)));
            }
            // Stock and reservations around the capacity, so that about half the inventories are fully reserved and
            // some hold more than their capacity (the game allows that when capacity is ignorable or rules change).
            int target = Math.Max(0, capacity + random.Next(-capacity / 2 - 2, 4));
            int stocked = random.Next(target + 1);
            int remaining = stocked;
            for (int i = 0; i < kinds && remaining > 0; i++)
            {
                int amount = i == kinds - 1 ? remaining : random.Next(remaining + 1);
                if (amount > 0) storage.Add(new GoodAmount(goods[(round + i) % 5], amount));
                remaining -= amount;
            }
            remaining = target - stocked;
            for (int i = 0; i < kinds && remaining > 0; i++)
            {
                int amount = i == kinds - 1 ? remaining : random.Next(remaining + 1);
                if (amount > 0) reserved.Add(new GoodAmount(goods[(round + i) % 5], amount));
                remaining -= amount;
            }
            allowed.Add(amounts);
            Set(inventory, "_allowedGoods", allowed);
            Set(inventory, "_storage", storage);
            Set(inventory, "_reservedCapacity", reserved);
            Set(inventory, "<Capacity>k__BackingField", capacity);
            object disallower;
            switch (round % 3)
            {
                case 0:
                    disallower = Activator.CreateInstance(typeof(Inventory).Assembly.GetType("Timberborn.InventorySystem.NullGoodDisallower", true), true);
                    break;
                case 1:
                    Type recipe = Assembly.Load("Timberborn.Workshops").GetType("Timberborn.Workshops.RecipeGoodDisallower", true);
                    disallower = RuntimeHelpers.GetUninitializedObject(recipe);
                    Dictionary<string, int> limits = new Dictionary<string, int>();
                    foreach (string good in goods) if (random.Next(2) == 0) limits[good] = random.Next(3 * capacity);
                    Set(disallower, "_limits", limits);
                    break;
                default:
                    disallower = new AnyDisallower(random.Next(3) == 0 ? int.MaxValue : random.Next(-5, 3 * capacity));
                    break;
            }
            Set(inventory, "_goodDisallower", disallower);

            bool fullyReserved = inventory.IsFullyReserved;
            bool anyRoom = false;
            foreach (string good in goods)
            {
                int room = inventory.UnreservedCapacity(good);
                int carried = calculator.AmountToCarry(1 + random.Next(4), new GoodAmount(good, 1 + random.Next(5)), inventory).Amount;
                anyRoom |= room > 0 && carried > 0;
                if (fullyReserved)
                {
                    if (room != 0) roomWhenFull++;
                    if (carried != 0) carriedWhenFull++;
                }
            }
            if (fullyReserved) full++;
            else if (anyRoom) withRoomSomewhere++;
        }
        check(roomWhenFull == 0 && carriedWhenFull == 0 && full > 500 && withRoomSomewhere > 500,
            $"yielder search: the game's fully reserved inventory has room for no good and the game's carry amount is 0 for " +
            $"every plant ({full} fully reserved of 3000 random inventories: {roomWhenFull} goods with room, " +
            $"{carriedWhenFull} carry amounts above 0; {withRoomSomewhere} others had room)");
    }

    // The stop stands down when another mod patches a method it rests on; this mod's own patches do not count.
    private static void Guard(Action<bool, string> check)
    {
        Func<MethodBase, IEnumerable<(string Owner, string Patch)>> previous = YielderSearch.PatchesOn;
        FieldInfo active = typeof(YielderSearch).GetField("_active", BindingFlags.Static | BindingFlags.NonPublic);
        object wasActive = active?.GetValue(null);
        try
        {
            MethodBase[] methods = YielderSearch.FullStopMethods();
            check(methods.Length == 4 && methods.All(method => method != null),
                "yielder search: the methods the full-building stop rests on are found in the game (IsFullyReserved, " +
                "TotalAmountInStock, UnreservedCapacity, AmountToCarry)");

            YielderSearch.PatchesOn = _ => null;
            bool none = YielderSearch.FullStopBlocker() == null;
            YielderSearch.PatchesOn = _ => new[] { (Plugin.HarmonyId + ".DistrictCounts", "LateGamePerformance.Some.Postfix") };
            bool ours = YielderSearch.FullStopBlocker() == null;
            int blocked = 0;
            foreach (MethodBase method in methods)
            {
                YielderSearch.PatchesOn = m => m == method ? new[] { ("some.other.mod", "Other.Mod.Patch.Prefix") } : null;
                string blocker = YielderSearch.FullStopBlocker();
                if (blocker != null && blocker.Contains(method.Name) && blocker.Contains("some.other.mod")) blocked++;
            }
            check(none && ours && blocked == methods.Length,
                $"yielder search: another mod's patch on any of the {methods.Length} methods stands the stop down, this mod's own " +
                $"do not (stood down for {blocked})");

            // Decided once, at the first search, and said in the stats line.
            active?.SetValue(null, true);
            YielderSearch.ResetFullStopForTests();
            YielderSearch.PatchesOn = m => m.Name == "UnreservedCapacity" ? new[] { ("some.other.mod", "Other.Mod.Room.Postfix") } : null;
            bool offFirst = !YielderSearch.FullStopAllowed();
            YielderSearch.PatchesOn = _ => null;
            bool staysOff = !YielderSearch.FullStopAllowed();
            string offLine = YielderSearch.TakeStatsLine();
            YielderSearch.ResetFullStopForTests();
            bool on = YielderSearch.FullStopAllowed();
            string onLine = YielderSearch.TakeStatsLine();
            Console.WriteLine("     " + offLine);
            check(offFirst && staysOff && on && offLine != null && offLine.Contains("searches of a full building walk every candidate") &&
                  offLine.Contains("Other.Mod.Room.Postfix") && onLine != null && !onLine.Contains("walk every candidate") &&
                  onLine.Contains("0 searches stopped at the first plant found because the building had no room left (0 candidates walked in them)"),
                "yielder search: the stop is decided at the first search for the session, and the stats line says when it " +
                "stands down and how many searches stopped");
        }
        finally
        {
            active?.SetValue(null, wasActive);
            YielderSearch.PatchesOn = previous;
            YielderSearch.ResetFullStopForTests();
        }
    }

    // A list that hands itself out as its enumerator, so that walking it allocates nothing.
    private sealed class ReusedList : IEnumerable<int>, IEnumerator<int>
    {
        private readonly int[] _items;
        private int _index;

        public ReusedList(int[] items)
        {
            _items = items;
        }

        public IEnumerator<int> GetEnumerator()
        {
            _index = -1;
            return this;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool MoveNext() => ++_index < _items.Length;
        public int Current => _items[_index];
        object IEnumerator.Current => Current;
        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }

    // The walk kept for every search: the same candidates as a new walk, nothing allocated per search, a second
    // enumeration starting again from the top as the iterator did, and no closure or iterator class left in the feature.
    private static void SharedWalk(Action<bool, string> check)
    {
        Random random = new Random(77);
        int[] items = Enumerable.Range(0, 400).Select(_ => random.Next(1000)).ToArray();
        ReusedList source = new ReusedList(items);
        Func<int, bool> exists = plant => plant % 97 != 0;
        Func<int, bool> yielding = plant => plant % 5 == 0;
        Func<int, bool> alive = plant => plant % 3 != 0;
        Func<int, int> lookUp = plant => plant % 4 == 0 ? plant : -1;
        Func<int, bool> reached = result => result >= 0;
        Func<int, bool> room = plant => plant % 10 == 0;
        YielderSearch.Counters counters = new YielderSearch.Counters();
        YielderSearch.Walk<int, int> walk = new YielderSearch.Walk<int, int>();

        long Search(bool full)
        {
            long sum = 0;
            foreach (int result in walk.Start(source, exists, yielding, alive, lookUp, reached, counters, room, null, full))
            {
                sum = sum * 31 + result;
            }
            walk.Release();
            return sum;
        }

        long Fresh(bool full)
        {
            long sum = 0;
            foreach (int result in YielderSearch.LazyCandidates(items, exists, yielding, alive, lookUp, reached,
                         new YielderSearch.Counters(), room, null, full))
            {
                sum = sum * 31 + result;
            }
            return sum;
        }

        bool same = Search(false) == Fresh(false) && Search(true) == Fresh(true) && Search(false) == Fresh(false);
        for (int i = 0; i < 50; i++) Search(i % 2 == 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Search(i % 2 == 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(same && allocated == 0,
            $"yielder search: one walk serves every search with the same candidates as a new one, and allocates nothing " +
            $"({allocated} bytes over 1000 searches)");

        IEnumerable<int> once = YielderSearch.LazyCandidates(items, exists, yielding, alive, lookUp, reached,
            new YielderSearch.Counters(), room);
        List<int> first = once.ToList();
        List<int> second = once.ToList();
        check(first.Count > 0 && first.SequenceEqual(second),
            "yielder search: a walk enumerated a second time starts again from the top, as the iterator did");

        string[] generated = typeof(YielderSearch).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => type.Name).Where(name => name.Contains("DisplayClass") || name.Contains(">d__")).ToArray();
        check(generated.Length == 0,
            "yielder search: no closure or iterator is made per search (no compiler-made closure or iterator class in the " +
            $"feature{(generated.Length > 0 ? ": " + string.Join(", ", generated) : "")})");
    }
}
