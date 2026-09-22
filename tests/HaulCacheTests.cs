using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockingSystem;
using Timberborn.Emptying;
using Timberborn.Goods;
using Timberborn.Hauling;
using Timberborn.InventorySystem;
using Timberborn.StockpilePrioritySystem;
using Timberborn.WorkSystem;
using Timberborn.Workshops;

// The hauling job cache against the game's real classes: buildings made of the game's own components (haul candidate,
// seven of the game's eight haul providers (all but the breeding pod's), inventories with the game's capacity rules,
// blockable object, emptiable, obtainer, supplier, manufactory, haul priority), wired as the game wires them (the
// components' own Awake, Inventory.Initialize, Inventories.AddInventory, Inventory.Enable), with the game's own
// DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered as the reference. Harmony does not run here, so where the game
// would run one of the cache's hooks the test runs it, right after the game's method that Harmony would have hooked.
//
// What is checked (0.4.28): answers survive ticks; every hooked change asks the changed building again and only it;
// the five flags that are compared rather than hooked are caught with no hook at all; the capacity rule with inputs
// nothing announces (InRangeYielderGoodAllower) is kept beside SimpleOutput and asked every time beside anything
// else; another mod's provider is asked every time; verify catches an answer that went stale across ticks; another
// mod's unread patch keeps nothing; every method hooked for an input is too large for Mono to inline.
internal static class HaulCacheTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly InventoryFillCalculator FillCalculator = new InventoryFillCalculator();

    // Another mod's haul provider: a plain weight, changed with no event.
    private sealed class ModProvider : IHaulBehaviorProvider
    {
        public float Weight = 0.55f;
        public WorkplaceBehavior Behavior;

        public void GetWeightedBehaviors(IList<WeightedBehavior> weightedBehaviors)
        {
            weightedBehaviors.Add(new WeightedBehavior(Weight, Behavior));
        }
    }

    private sealed class Building
    {
        public readonly List<object> Components = new List<object>();
        public HaulCandidate Candidate;
        public Inventory Inventory;
        public BlockableObject Blockable;
        public HaulPrioritizable Prioritizable;
        public object Rule;
        public object Workplace;

        public T Get<T>()
        {
            return Components.OfType<T>().FirstOrDefault();
        }
    }

    private static Type FindType(string fullName)
    {
        Type type = Program.FindType(fullName);
        if (type == null)
        {
            throw new TypeLoadException(fullName);
        }
        return type;
    }

    private static object New(string fullName)
    {
        return RuntimeHelpers.GetUninitializedObject(FindType(fullName));
    }

    private static FieldInfo Field(Type type, string field)
    {
        for (; type != null; type = type.BaseType)
        {
            FieldInfo info = type.GetField(field, Any | BindingFlags.DeclaredOnly);
            if (info != null)
            {
                return info;
            }
        }
        throw new MissingFieldException(field);
    }

    private static void Set(object target, string field, object value)
    {
        Field(target.GetType(), field).SetValue(target, value);
    }

    // UnityEngine.Object's pointer to its native object, which is all its "is it alive" test reads. Written with a
    // plain field store: reflection would first run the type's initializer, which calls into the engine.
    private static readonly Action<UnityEngine.Object, IntPtr> SetCachedPointer = CachedPointerSetter();

    private static Action<UnityEngine.Object, IntPtr> CachedPointerSetter()
    {
        FieldInfo field = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
        System.Reflection.Emit.DynamicMethod method = new System.Reflection.Emit.DynamicMethod("SetCachedPointer", null,
            new[] { typeof(UnityEngine.Object), typeof(IntPtr) }, typeof(HaulCacheTests).Module, true);
        System.Reflection.Emit.ILGenerator il = method.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        il.Emit(System.Reflection.Emit.OpCodes.Stfld, field);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return (Action<UnityEngine.Object, IntPtr>)method.CreateDelegate(typeof(Action<UnityEngine.Object, IntPtr>));
    }

    // The component cache the game builds for an entity, over these components, with a live game object.
    private static void Attach(List<object> components)
    {
        Assembly assembly = typeof(BaseComponent).Assembly;
        object cache = RuntimeHelpers.GetUninitializedObject(assembly.GetType("Timberborn.BaseComponentSystem.ComponentCache", true));
        Set(cache, "_typeIndexMap", Activator.CreateInstance(assembly.GetType("Timberborn.BaseComponentSystem.TypeIndexMap", true), true));
        Set(cache, "_typeBlacklist", Activator.CreateInstance(assembly.GetType("Timberborn.BaseComponentSystem.TypeBlacklist", true), true));
        Set(cache, "_components", components);
        UnityEngine.GameObject gameObject = (UnityEngine.GameObject)RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.GameObject));
        SetCachedPointer(gameObject, (IntPtr)1);
        Set(cache, "<CachedGameObject>k__BackingField", gameObject);
        foreach (object component in components)
        {
            if (component is BaseComponent baseComponent)
            {
                Set(baseComponent, "_componentCache", cache);
                Set(baseComponent, "<Enabled>k__BackingField", true);
            }
        }
    }

    // One building. `goods`: (good, givable, takeable, amount); `stock`: (good, amount).
    private static Building NewBuilding(string[] providers, object rule, (string Good, bool Givable, bool Takeable, int Amount)[] goods,
        (string Good, int Amount)[] stock, bool publicInput, int capacity, ModProvider modProvider = null)
    {
        Building building = new Building();
        List<object> c = building.Components;
        HaulCandidate candidate = RuntimeHelpers.GetUninitializedObject(typeof(HaulCandidate)) as HaulCandidate;
        Set(candidate, "_providers", new List<IHaulBehaviorProvider>());
        Set(candidate, "_weightedBehaviorsCache", new List<WeightedBehavior>());
        building.Candidate = candidate;
        c.Add(candidate);
        building.Prioritizable = (HaulPrioritizable)RuntimeHelpers.GetUninitializedObject(typeof(HaulPrioritizable));
        c.Add(building.Prioritizable);
        building.Blockable = (BlockableObject)RuntimeHelpers.GetUninitializedObject(typeof(BlockableObject));
        Set(building.Blockable, "_blockers", new HashSet<object>());
        c.Add(building.Blockable);
        Inventories inventories = (Inventories)RuntimeHelpers.GetUninitializedObject(typeof(Inventories));
        Set(inventories, "_inventories", new List<Inventory>());
        Set(inventories, "_enabledInventories", new List<Inventory>());
        c.Add(inventories);
        Inventory inventory = (Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        Set(inventory, "_allowedGoods", new StorableGoodRegistry());
        Set(inventory, "_storage", new GoodRegistry());
        Set(inventory, "_reservedStock", new GoodRegistry());
        Set(inventory, "_reservedCapacity", new GoodRegistry());
        building.Inventory = inventory;
        c.Add(inventory);
        building.Rule = rule;
        if (rule is BaseComponent)
        {
            c.Add(rule);
        }
        foreach (string behavior in new[]
                 {
                     "Timberborn.Emptying.EmptyInventoriesWorkplaceBehavior", "Timberborn.Emptying.RemoveUnwantedStockWorkplaceBehavior",
                     "Timberborn.Emptying.EmptyOutputWorkplaceBehavior", "Timberborn.StockpilePrioritySystem.ObtainGoodWorkplaceBehavior",
                     "Timberborn.StockpilePrioritySystem.SupplyGoodWorkplaceBehavior", "Timberborn.Workshops.FillInputWorkplaceBehavior"
                 })
        {
            c.Add(New(behavior));
        }
        Emptiable emptiable = (Emptiable)RuntimeHelpers.GetUninitializedObject(typeof(Emptiable));
        GoodObtainer obtainer = (GoodObtainer)RuntimeHelpers.GetUninitializedObject(typeof(GoodObtainer));
        GoodSupplier supplier = (GoodSupplier)RuntimeHelpers.GetUninitializedObject(typeof(GoodSupplier));
        object stockpile = New("Timberborn.Stockpiles.Stockpile");
        Set(stockpile, "<Inventory>k__BackingField", inventory);
        object simpleOutput = New("Timberborn.SimpleOutputBuildings.SimpleOutputInventory");
        Set(simpleOutput, "<Inventory>k__BackingField", inventory);
        c.Add(emptiable);
        c.Add(obtainer);
        c.Add(supplier);
        c.Add(stockpile);
        c.Add(simpleOutput);
        if (providers.Contains("Timberborn.Workshops.ManufactoryHaulBehaviorProvider"))
        {
            Manufactory manufactory = (Manufactory)RuntimeHelpers.GetUninitializedObject(typeof(Manufactory));
            Set(manufactory, "<CurrentRecipe>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(RecipeSpec)));
            c.Add(manufactory);
        }
        List<BaseComponent> providerComponents = new List<BaseComponent>();
        foreach (string provider in providers)
        {
            BaseComponent component = (BaseComponent)New(provider);
            FieldInfo calculator = component.GetType().GetField("_inventoryFillCalculator", Any);
            calculator?.SetValue(component, FillCalculator);
            providerComponents.Add(component);
            c.Add(component);
        }
        if (modProvider != null)
        {
            modProvider.Behavior = (WorkplaceBehavior)New("Timberborn.Workshops.FillInputWorkplaceBehavior");
            c.Add(modProvider);
        }
        Attach(c);

        // As the game wires an entity: Awake, then the inventory's initializer, then the finished state.
        candidate.Awake();
        foreach (BaseComponent provider in providerComponents)
        {
            ((IAwakableComponent)provider).Awake();
        }
        inventory.Awake();
        List<StorableGoodAmount> allowed = new List<StorableGoodAmount>();
        foreach ((string good, bool givable, bool takeable, int amount) in goods)
        {
            StorableGood storable = givable && takeable ? StorableGood.CreateGiveableAndTakeable(good)
                : givable ? StorableGood.CreateAsGivable(good) : StorableGood.CreateAsTakeable(good);
            allowed.Add(new StorableGoodAmount(storable, amount));
        }
        inventory.Initialize("Test", capacity, allowed, publicInput, true, false, (IGoodDisallower)rule);
        (rule as IInitializableGoodDisallower)?.Initialize(inventory);
        inventories.AddInventory(inventory);
        foreach ((string good, int amount) in stock)
        {
            inventory.GiveExistingIgnoringCapacity(new GoodAmount(good, amount));
        }
        inventory.Enable();
        return building;
    }

    private static object SingleGood(string good)
    {
        SingleGoodAllower allower = (SingleGoodAllower)RuntimeHelpers.GetUninitializedObject(typeof(SingleGoodAllower));
        Set(allower, "<AllowedGood>k__BackingField", good);
        return allower;
    }

    private static object Recipe(params (string Good, int Limit)[] limits)
    {
        object rule = New("Timberborn.Workshops.RecipeGoodDisallower");
        Set(rule, "_limits", limits.ToDictionary(limit => limit.Good, limit => limit.Limit));
        return rule;
    }

    // An InRangeYielderGoodAllower with these yields in range and a workplace with `workers` workers.
    private static object InRange(out object workplace, int workers, params string[] yields)
    {
        object rule = New("Timberborn.Yielding.InRangeYielderGoodAllower");
        Set(rule, "_allowedGoods", new HashSet<string>(yields));
        Set(rule, "_yieldsCache", new HashSet<string>());
        Set(rule, "_incomingGoods", new List<string>());
        workplace = New("Timberborn.WorkSystem.Workplace");
        SetWorkers(workplace, workers);
        Set(rule, "_workplace", workplace);
        return rule;
    }

    private static void SetWorkers(object workplace, int workers)
    {
        List<Worker> assigned = new List<Worker>();
        for (int i = 0; i < workers; i++)
        {
            assigned.Add((Worker)RuntimeHelpers.GetUninitializedObject(typeof(Worker)));
        }
        Set(workplace, "_assignedWorkers", assigned);
    }

    private sealed class Harness
    {
        public Feature Feature;
        public object District;
        public MethodInfo GameBuild;
        public HashSet<HaulCandidate> Candidates;

        public PatchSpec Patch(string name)
        {
            return Feature.Patches.Find(patch => patch.Name == name);
        }

        public void Hook(string name, object instance)
        {
            Patch(name).Postfix.Invoke(null, new[] { instance });
        }

        public void Tick(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++)
            {
                Patch("TickableSingletonService.TickAll").Prefix.Invoke(null, null);
            }
        }

        public void CandidateSetChanged()
        {
            Patch("DistrictHaulCandidates.OnFinishedBuildingRegistered").Postfix.Invoke(null, null);
        }

        public static long Counter(string name)
        {
            return (long)typeof(HaulCache).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        // One request through the cache and the game's own build of the same list. Same: the cache handed the
        // game's list. The deltas of the cache's counters over the request.
        public (bool Same, long Recomputed, long Reused, long EarlierTick, long Always, List<WorkplaceBehavior> Mine) Request()
        {
            long recomputed = Counter("_candidatesRecomputed"), reused = Counter("_candidatesReused");
            long earlier = Counter("_reusedFromEarlierTick"), always = Counter("_recomputedAlways");
            List<WorkplaceBehavior> mine = new List<WorkplaceBehavior>();
            bool ranGame = (bool)Patch("DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered").Prefix.Invoke(null, new object[] { District, mine });
            List<WorkplaceBehavior> games = new List<WorkplaceBehavior>();
            GameBuild.Invoke(District, new object[] { games });
            bool same = !ranGame && mine.Count == games.Count && mine.Zip(games, ReferenceEquals).All(equal => equal);
            return (same, Counter("_candidatesRecomputed") - recomputed, Counter("_candidatesReused") - reused,
                Counter("_reusedFromEarlierTick") - earlier, Counter("_recomputedAlways") - always, mine);
        }
    }

    private static Harness NewHarness(IEnumerable<Building> buildings, bool verify)
    {
        Harness harness = new Harness { Feature = HaulCache.CreateFeature(new Config { HaulCacheVerify = verify }) };
        foreach (PatchSpec patch in harness.Feature.Patches)
        {
            patch.Target();
        }
        Type districtType = FindType("Timberborn.Hauling.DistrictHaulCandidates");
        harness.District = RuntimeHelpers.GetUninitializedObject(districtType);
        harness.Candidates = new HashSet<HaulCandidate>(buildings.Select(building => building.Candidate));
        Set(harness.District, "_haulCandidates", harness.Candidates);
        Set(harness.District, "_haulingCenters", new HashSet<HaulingCenter>());
        Set(harness.District, "_weightedBehaviors", new List<WeightedBehavior>());
        harness.GameBuild = districtType.GetMethod("GetWorkplaceBehaviorsOrdered");
        HaulCache.ResetForTests();
        HaulCache.VerifyEnabled = verify;
        HaulCache.Activate();
        return harness;
    }

    public static void Run(Action<bool, string> check)
    {
        Program.LoadEveryGameAssembly();
        FieldInfo[] saved =
        {
            typeof(HaulCache).GetField("_active", BindingFlags.Static | BindingFlags.NonPublic),
            typeof(HaulCache).GetField("_verify", BindingFlags.Static | BindingFlags.NonPublic),
            typeof(HaulCache).GetField("_flushEveryTicks", BindingFlags.Static | BindingFlags.NonPublic)
        };
        object[] before = Array.ConvertAll(saved, field => field.GetValue(null));
        Func<MethodBase, IEnumerable<(string Owner, string Patch)>> patchesOn = ForeignPatches.PatchesOn;
        try
        {
            ForeignPatches.PatchesOn = method => null;
            RunKept(check);
            RunForeignPatches(check);
            RunInlining(check);
            RunTiming();
        }
        finally
        {
            ForeignPatches.PatchesOn = patchesOn;
            HaulCache.ResetForTests();
            for (int i = 0; i < saved.Length; i++)
            {
                saved[i].SetValue(null, before[i]);
            }
        }
    }

    private const string EmptiableProvider = "Timberborn.Emptying.EmptiableHaulBehaviorProvider";
    private const string UnwantedStockProvider = "Timberborn.Emptying.UnwantedStockHaulBehaviorProvider";
    private const string SimpleOutputProvider = "Timberborn.SimpleOutputBuildings.SimpleOutputInventoryHaulBehaviorProvider";
    private const string ObtainGoodProvider = "Timberborn.StockpilePrioritySystem.ObtainGoodHaulBehaviorProvider";
    private const string SupplyGoodProvider = "Timberborn.StockpilePrioritySystem.SupplyGoodHaulBehaviorProvider";
    private const string FillInputProvider = "Timberborn.Workshops.FillInputHaulBehaviorProvider";
    private const string ManufactoryProvider = "Timberborn.Workshops.ManufactoryHaulBehaviorProvider";

    private static Building Warehouse(string allowed, (string, int)[] stock, bool supplying)
    {
        Building building = NewBuilding(new[] { EmptiableProvider, UnwantedStockProvider, ObtainGoodProvider, SupplyGoodProvider }, SingleGood(allowed),
            new[] { ("Log", true, true, 100), ("Plank", true, true, 100), ("Water", true, true, 100) }, stock, true, 100);
        if (supplying)
        {
            building.Get<GoodSupplier>().EnableSupplying();
        }
        return building;
    }

    private static void RunKept(Action<bool, string> check)
    {
        Building w1 = Warehouse("Log", new[] { ("Log", 30) }, true);
        Building w2 = Warehouse("Plank", new[] { ("Plank", 60) }, true);
        Building fill = NewBuilding(new[] { FillInputProvider }, Recipe(("Plank", 20), ("Gear", 10)),
            new[] { ("Plank", true, false, 20), ("Gear", false, true, 10) }, new[] { ("Plank", 5) }, false, 30);
        Building works = NewBuilding(new[] { ManufactoryProvider }, Recipe(("Log", 20), ("Plank", 10)),
            new[] { ("Log", true, false, 20), ("Plank", false, true, 10) }, new[] { ("Log", 10), ("Plank", 4) }, false, 30);
        Building flag = NewBuilding(new[] { SimpleOutputProvider }, InRange(out object flagWorkplace, 1, "Log"),
            new[] { ("Log", false, true, 20) }, new[] { ("Log", 8) }, false, 20);
        flag.Workplace = flagWorkplace;
        Building mixed = NewBuilding(new[] { FillInputProvider }, InRange(out object mixedWorkplace, 0, "Berry"),
            new[] { ("Berry", true, true, 20) }, new (string, int)[0], false, 20);
        mixed.Workplace = mixedWorkplace;
        ModProvider modProvider = new ModProvider();
        Building modded = NewBuilding(new string[0], new NullGoodDisallower(), new[] { ("Log", true, true, 10) },
            new (string, int)[0], false, 10, modProvider);
        Building[] all = { w1, w2, fill, works, flag, mixed, modded };
        Harness h = NewHarness(all, verify: true);

        var first = h.Request();
        check(first.Same && first.Recomputed == 7 && first.Mine.Count >= 7,
            $"haul cache: the first request asks all 7 buildings and hands the game's own list ({first.Mine.Count} jobs)");

        h.Tick(3);
        var kept = h.Request();
        check(kept.Same && kept.Reused == 5 && kept.EarlierTick == 5 && kept.Recomputed == 2 && kept.Always == 2,
            "haul cache: three ticks later the 5 buildings of the game's own kinds are served from an earlier tick; " +
            $"the InRange flag beside FillInput and the other mod's provider are asked again (reused {kept.Reused}, " +
            $"from an earlier tick {kept.EarlierTick}, recomputed {kept.Recomputed})");

        // Every hooked change asks exactly the changed building again (plus the 2 that are always asked).
        (string What, Action Change)[] hooked =
        {
            ("stock (Inventory.GiveExisting, then the changed event)", () =>
            {
                w1.Inventory.GiveExisting(new GoodAmount("Log", 5));
                h.Hook("Inventory.InvokeInventoryChangedEvent", w1.Inventory);
            }),
            ("a stock reservation", () =>
            {
                w2.Inventory.ReserveStock(new GoodAmount("Plank", 10));
                h.Hook("Inventory.InvokeInventoryChangedEvent", w2.Inventory);
            }),
            ("a capacity reservation", () =>
            {
                fill.Inventory.ReserveCapacity(new GoodAmount("Plank", 3));
                h.Hook("Inventory.InvokeInventoryChangedEvent", fill.Inventory);
            }),
            ("the capacity rule (SingleGoodAllower.Allow, announced through the changed event; stock becomes unwanted)", () =>
            {
                ((SingleGoodAllower)w1.Rule).Allow("Plank");
                h.Hook("Inventory.InvokeInventoryChangedEvent", w1.Inventory);
            }),
            ("Inventory.Disable", () =>
            {
                fill.Inventory.Disable();
                h.Hook("Inventory.Disable", fill.Inventory);
            }),
            ("Inventory.Enable", () =>
            {
                fill.Inventory.Enable();
                h.Hook("Inventory.Enable", fill.Inventory);
            }),
            ("BlockableObject.Block", () =>
            {
                works.Blockable.Block("test");
                h.Hook("BlockableObject.Block", works.Blockable);
            }),
            ("BlockableObject.Unblock", () =>
            {
                works.Blockable.Unblock("test");
                h.Hook("BlockableObject.Unblock", works.Blockable);
            })
        };
        foreach ((string what, Action change) in hooked)
        {
            h.Tick(2);
            change();
            var after = h.Request();
            check(after.Same && after.Recomputed == 3 && after.Reused == 4 && after.EarlierTick == 4,
                $"haul cache: {what} asks that building again and only it, the other kept ones stay kept across ticks, " +
                $"and the game's list is handed (recomputed {after.Recomputed}, reused {after.Reused})");
        }

        // The five flags whose setters are not hooked: changed with no hook at all, as if the setter had been
        // compiled into its caller.
        (string What, Action Change)[] compared =
        {
            ("HaulPrioritizable.Prioritized", () => w2.Prioritizable.Prioritized = true),
            ("Emptiable.IsMarkedForEmptying", () => w1.Get<Emptiable>().MarkForEmptyingWithoutStatus()),
            ("GoodObtainer.IsObtaining", () => w1.Get<GoodObtainer>().EnableObtaining()),
            ("GoodSupplier.IsSupplying", () => w2.Get<GoodSupplier>().DisableSupplying()),
            ("Manufactory.CurrentRecipe (another recipe)", () =>
                Set(works.Get<Manufactory>(), "<CurrentRecipe>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(RecipeSpec)))),
            ("Manufactory.CurrentRecipe (none)", () => Set(works.Get<Manufactory>(), "<CurrentRecipe>k__BackingField", null))
        };
        foreach ((string what, Action change) in compared)
        {
            h.Tick(2);
            change();
            var after = h.Request();
            check(after.Same && after.Recomputed == 3 && after.Reused == 4,
                $"haul cache: {what} changed with no hook is noticed on the next request, that building alone is asked " +
                $"again (recomputed {after.Recomputed}, reused {after.Reused})");
        }

        // Inputs nothing announces. The flag's workers: kept, and still the game's list (in-stock output fill never
        // reads them). The InRange inventory beside FillInput: its weight flips, and it is asked every time.
        // The other mod's provider: its weight changes silently, and it is asked every time.
        h.Tick(2);
        SetWorkers(flag.Workplace, 0);
        Set(flag.Rule, "_incomingGoods", new List<string> { "Log" });
        var workers = h.Request();
        check(workers.Same && workers.Recomputed == 2 && workers.Reused == 5,
            "haul cache: the lumberjack flag's workers and incoming goods (announced by nothing) do not change its " +
            $"in-stock output fill, so it stays kept and the list is still the game's (reused {workers.Reused})");
        List<WorkplaceBehavior> beforeFlip = workers.Mine;
        SetWorkers(mixed.Workplace, 1);
        modProvider.Weight = 0.95f;
        h.Tick(2);
        var flipped = h.Request();
        check(flipped.Same && flipped.Recomputed == 2 && !flipped.Mine.SequenceEqual(beforeFlip),
            "haul cache: an InRange inventory beside FillInput and another mod's provider change with no event, and " +
            "the list follows the game's because both are asked on every request");

        // A building joining or leaving the district drops everything.
        h.CandidateSetChanged();
        var dropped = h.Request();
        check(dropped.Same && dropped.Recomputed == 7 && dropped.Reused == 0,
            "haul cache: a building joining or leaving a district drops every kept answer");

        // A change with its hook missing (as if a hook had not run): the stale answer survives two ticks, verify
        // catches it and counts it, and the game is handed the cached list all the same.
        long mismatches = Harness.Counter("_verifyMismatches");
        h.Tick();
        h.Request();
        w1.Blockable.Block("missed");
        h.Tick(2);
        var stale = h.Request();
        check(!stale.Same && stale.EarlierTick == 5 && Harness.Counter("_verifyMismatches") == mismatches + 1,
            "haul cache verify: an answer that went stale across ticks is caught and counted (the game is still " +
            "handed the kept list)");
        h.Hook("BlockableObject.Block", w1.Blockable);
        var fixedAgain = h.Request();
        check(fixedAgain.Same && Harness.Counter("_verifyMismatches") == mismatches + 1,
            "haul cache: once the hook runs, the next list is the game's again");

        string line = HaulCache.TakeStatsLine();
        Console.WriteLine("     " + line);
        check(line.Contains("from an earlier tick)") && line.Contains("of them on every request: a haul provider or capacity rule") &&
              line.Contains("verify mismatches 1; the game's own build alongside took"),
            "haul cache: the stats line says how many were kept from an earlier tick and how many are always asked");
    }

    private static void RunForeignPatches(Action<bool, string> check)
    {
        Building w1 = Warehouse("Log", new[] { ("Log", 30) }, true);
        Building w2 = Warehouse("Plank", new[] { ("Plank", 60) }, true);

        // MixedStorage's allocation rule is read and accepted; the answers are kept.
        ForeignPatches.PatchesOn = method => method.Name == "AllowedAmount" && method.DeclaringType.Name == "SingleGoodAllower"
            ? new[] { ("kyler.mixedstorage", "MixedStorage.LimitPatch.Prefix") } : null;
        Harness h = NewHarness(new[] { w1, w2 }, verify: false);
        h.Request();
        h.Tick();
        var withMixed = h.Request();
        check(withMixed.Same && withMixed.Reused == 2 && HaulCache.TakeStatsLine().Contains("reused 2/4"),
            "haul cache: MixedStorage's allocation limit patch was read, and answers are kept beside it");

        // Another mod patches the fill calculator: nothing is kept, every request asks every building.
        ForeignPatches.PatchesOn = method => method.Name == "GetInventoryFillPercentage" ? new[] { ("other.mod", "Other.Patch.Prefix") } : null;
        h = NewHarness(new[] { w1, w2 }, verify: false);
        h.Request();
        h.Tick();
        var standDown = h.Request();
        string line = HaulCache.TakeStatsLine();
        check(standDown.Same && standDown.Reused == 0 && standDown.Recomputed == 2 &&
              line.Contains("nothing kept between requests: another mod patches InventoryFillCalculator.GetInventoryFillPercentage (other.mod, Other.Patch.Prefix)"),
            "haul cache: another mod's unread patch on something the answers depend on keeps nothing, and says so (" + line + ")");

        List<MethodBase> read = HaulCache.ReadMethods();
        ForeignPatches.PatchesOn = method => method.Name == "AllowedAmount" && method.DeclaringType.Name == "SingleGoodAllower"
            ? new[] { ("kyler.mixedstorage", "MixedStorage.SomethingNew.Prefix") } : null;
        check(ForeignPatches.First(read, HaulCache.ReviewedPatches, null) != null,
            "haul cache: a patch from the same mod that was not read keeps nothing");
        ForeignPatches.PatchesOn = method => method.Name == "AllowedAmount" && method.DeclaringType.Name == "SingleGoodAllower"
            ? new[] { ("someone.else", "MixedStorage.LimitPatch.Prefix") } : null;
        check(ForeignPatches.First(read, HaulCache.ReviewedPatches, null) != null,
            "haul cache: the same patch name under another mod's id is not accepted");
        ForeignPatches.PatchesOn = method => method.Name == "LimitedAmount" ? new[] { ("kyler.mixedstorage", "MixedStorage.LimitPatch.Prefix") } : null;
        check(ForeignPatches.First(read, HaulCache.ReviewedPatches, null) != null,
            "haul cache: the reviewed patch is only accepted on the method it was reviewed on");
        ForeignPatches.PatchesOn = method => new[] { (Plugin.HarmonyId + ".DistrictCounts", "LateGamePerformance.DistrictCounts.UpdatePrefix") };
        check(ForeignPatches.First(read, HaulCache.ReviewedPatches, null) == null, "haul cache: this mod's own patches do not count");
        ForeignPatches.PatchesOn = method => null;

        string now = HaulCache.ReadHashNow();
        check(now == HaulCache.ReadHash,
            $"haul cache: the game's haul providers, fill calculator and capacity rules are the code that was read " +
            $"(IL hash {now}, read {HaulCache.ReadHash})");
        check(read.Count == 30 && read.All(method => method != null),
            $"haul cache: all {read.Count} methods the kept answers depend on were found");
    }

    // Mono compiles a method of under 20 bytes of IL into its callers, where a hook on it would not run. Every method
    // the cache hooks for an input is larger; the five flags it compares instead have setters that are not. (The tick
    // hook only counts ticks for the stats line now.)
    private static void RunInlining(Action<bool, string> check)
    {
        List<string> small = new List<string>();
        int hooks = 0;
        foreach (PatchSpec patch in HaulCache.CreateFeature(new Config()).Patches)
        {
            if (patch.Postfix == null)
            {
                continue;
            }
            hooks++;
            MethodBase target = patch.Target();
            int size = target.GetMethodBody().GetILAsByteArray().Length;
            if (size < 20)
            {
                small.Add($"{patch.Name} ({size} bytes)");
            }
        }
        check(hooks == 7 && small.Count == 0,
            $"haul cache: all {hooks} methods hooked for an input are at least 20 bytes of IL, Mono's inlining limit" +
            (small.Count > 0 ? "; too small: " + string.Join(", ", small) : ""));
        int[] setters =
        {
            SetterSize(typeof(HaulPrioritizable), "Prioritized"), SetterSize(typeof(Emptiable), "IsMarkedForEmptying"),
            SetterSize(typeof(GoodObtainer), "IsObtaining"), SetterSize(typeof(GoodSupplier), "IsSupplying"),
            SetterSize(typeof(Manufactory), "CurrentRecipe")
        };
        check(setters.All(size => size < 20),
            $"haul cache: the five flags' setters are small enough to be inlined ({string.Join(", ", setters)} bytes), " +
            "which is why they are compared when read instead of hooked");
    }

    private static int SetterSize(Type type, string property)
    {
        return HarmonyLib.AccessTools.PropertySetter(type, property).GetMethodBody().GetILAsByteArray().Length;
    }

    // For the record, not a check: one request per tick over a district like the logged one (250 buildings), with a
    // few buildings' stock changing between requests, the game's own build against the cache dropping everything
    // every tick (up to 0.4.27) and keeping answers across ticks (0.4.28).
    private static void RunTiming()
    {
        Random random = new Random(5);
        List<Building> buildings = new List<Building>();
        for (int i = 0; i < 250; i++)
        {
            switch (i % 5)
            {
                case 0:
                case 1:
                    buildings.Add(Warehouse(i % 2 == 0 ? "Log" : "Plank", new[] { (i % 2 == 0 ? "Log" : "Plank", 1 + random.Next(90)) }, i % 3 == 0));
                    break;
                case 2:
                    buildings.Add(NewBuilding(new[] { FillInputProvider }, Recipe(("Plank", 20), ("Gear", 10)),
                        new[] { ("Plank", true, false, 20), ("Gear", false, true, 10) }, new[] { ("Plank", random.Next(1, 20)) }, false, 30));
                    break;
                case 3:
                    buildings.Add(NewBuilding(new[] { ManufactoryProvider }, Recipe(("Log", 20), ("Plank", 10)),
                        new[] { ("Log", true, false, 20), ("Plank", false, true, 10) }, new[] { ("Log", random.Next(1, 20)) }, false, 30));
                    break;
                default:
                    buildings.Add(NewBuilding(new[] { SimpleOutputProvider }, InRange(out _, 1, "Log"), new[] { ("Log", false, true, 20) },
                        new[] { ("Log", random.Next(1, 20)) }, false, 20));
                    break;
            }
        }
        double Measure(int flushEveryTicks, bool game)
        {
            Harness h = NewHarness(buildings, verify: false);
            typeof(HaulCache).GetField("_flushEveryTicks", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, flushEveryTicks);
            MethodBase prefix = h.Patch("DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered").Prefix;
            List<WorkplaceBehavior> list = new List<WorkplaceBehavior>();
            object[] gameArguments = { list };
            object[] prefixArguments = { h.District, list };
            long elapsed = 0;
            for (int request = 0; request < 600; request++)
            {
                h.Tick();
                for (int i = 0; i < 8; i++)
                {
                    h.Hook("Inventory.InvokeInventoryChangedEvent", buildings[random.Next(buildings.Count)].Inventory);
                }
                list.Clear();
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                if (game)
                {
                    h.GameBuild.Invoke(h.District, gameArguments);
                }
                else
                {
                    prefix.Invoke(null, prefixArguments);
                }
                if (request >= 100)
                {
                    elapsed += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                }
            }
            return elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 500;
        }
        double gameMs = Measure(0, true), perTickMs = Measure(1, false), keptMs = Measure(0, false);
        Console.WriteLine($"     timing: one hauler list request over 250 buildings, one request per tick and 8 buildings changed per " +
                          $"tick: the game {gameMs:0.000} ms, the cache dropping everything every tick (0.4.27) {perTickMs:0.000} ms, " +
                          $"keeping answers across ticks (0.4.28) {keptMs:0.000} ms");
        HaulCache.TakeStatsLine();
    }
}
