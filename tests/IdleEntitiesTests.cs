using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.MechanicalSystem;
using Timberborn.RangedEffectSystem;
using Timberborn.TickSystem;

// The idle-entity mirror against the game's real TickableEntityBucket, TickableEntity and MeteredTickableComponent.
// A model of the game's own pass (walk the bucket's list by index; an entity does something only if one of its
// tick components is enabled; removals during a pass are deferred) and the mirror-driven pass are run over the
// same scripted history: entities added and removed, components switched on and off, both between passes and in
// the middle of one, from inside another entity's tick. The sequence of effective ticks must be identical.
internal static class IdleEntitiesTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private sealed class Part : TickableComponent
    {
        public override void Tick()
        {
        }
    }

    private sealed class Ent
    {
        public Guid Id;
        public TickableEntity Entity;
        public TickableComponent[] Parts;
        // Whether ticking the entity does anything: a part switched on that does something when ticked (every part
        // but a ranged effect building without a mechanical part, whose tick does nothing).
        public bool Awake => Array.Exists(Parts, part => part.Enabled && !DoesNothing(part));
    }

    private static readonly FieldInfo MechanicalField = typeof(RangedEffectBuilding).GetField("_mechanicalBuilding", Any);

    private static bool DoesNothing(TickableComponent part)
    {
        return part is RangedEffectBuilding && MechanicalField.GetValue(part) == null;
    }

    // What an entity does when it is ticked in a given pass: a change to some other entity. The Direct ones change the
    // game's list behind the mod's hooks, through the list's own methods; only the key-walk history uses them.
    // EnableLast and DisableFirst switch one part of an entity with several; only the ranged effect history uses them.
    private enum Op { None, Enable, Disable, Add, Remove, DirectSwap, DirectAdd, DirectRemove, EnableLast, DisableFirst }

    private struct Action
    {
        public Op Op;
        public int Target;
    }

    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo EnabledField = typeof(BaseComponent).GetField("<Enabled>k__BackingField", Any);
    private static readonly FieldInfo EntityIdField = typeof(EntityComponent).GetField("<EntityId>k__BackingField", Any);
    // SortedList's change counter, for the cases that change the list and put the counter back. It is 'version' in .NET 8
    // as in the game's Mono; '_version' covers a runtime that renames it.
    private static readonly FieldInfo ListVersionField = typeof(SortedList<Guid, TickableEntity>).GetField("version", Any) ??
                                                         typeof(SortedList<Guid, TickableEntity>).GetField("_version", Any);

    // Stand-ins for a list class with the counter under either name, and for one with neither.
    private sealed class CountedList
    {
        internal int version = 7;
    }

    private sealed class UnderscoreCountedList
    {
        internal int _version = 9;
    }

    private sealed class UncountedList
    {
        internal int _size = 3;
    }

    public static void Run(Action<bool, string> check)
    {
        IdleEntities.CreateFeature(new Config());
        IdleEntities.Bind();
        Type bucketType = Type.GetType("Timberborn.TickSystem.TickableEntityBucket, Timberborn.TickSystem", true);
        MethodInfo add = bucketType.GetMethod("Add", Any);
        MethodInfo remove = bucketType.GetMethod("Remove", Any);
        FieldInfo listField = bucketType.GetField("_tickableEntities", Any);
        Random random = new Random(2718);

        // The scripted history, generated once and replayed by both sides.
        const int passes = 300, pool = 120;
        Ent[] ents = new Ent[pool];
        for (int i = 0; i < pool; i++)
        {
            ents[i] = MakeEntity(random, i);
        }
        bool[] initiallyIn = new bool[pool];
        for (int i = 0; i < pool; i++) initiallyIn[i] = i < 70;
        // Between passes: (pass, op, target). During passes: (pass, actor) -> action.
        List<(int pass, Op op, int target)> between = new List<(int, Op, int)>();
        Dictionary<(int, int), Action> during = new Dictionary<(int, int), Action>();
        for (int pass = 0; pass < passes; pass++)
        {
            int n = random.Next(4);
            for (int k = 0; k < n; k++) between.Add((pass, (Op)random.Next(1, 5), random.Next(pool)));
            int m = random.Next(3);
            for (int k = 0; k < m; k++) during[(pass, random.Next(pool))] = new Action { Op = (Op)random.Next(1, 5), Target = random.Next(pool) };
        }

        List<string> reference = Reference(ents, initiallyIn, between, during, passes);
        List<string> mirrored = Mirrored(ents, initiallyIn, between, during, passes, bucketType, add, remove, listField, check, out _);
        int firstDifference = -1;
        for (int i = 0; i < Math.Max(reference.Count, mirrored.Count); i++)
        {
            if (i >= reference.Count || i >= mirrored.Count || reference[i] != mirrored[i])
            {
                firstDifference = i;
                break;
            }
        }
        check(firstDifference < 0, $"idle entities: {mirrored.Count} effective ticks over {passes} passes, in the same order as the game's own loop" +
                                   (firstDifference < 0 ? "" : $" (first difference at {firstDifference}: {At(reference, firstDifference)} vs {At(mirrored, firstDifference)})"));
        check(IdleEntities.IsActive, "idle entities: feature still active after the scripted history");
        string line = IdleEntities.TakeStatsLine();
        check(line != null && line.Contains("passed over"), "idle entities stats: " + line);
        IdleEntities.SceneCreated();
        RemovedThenEnabled(bucketType, add, remove, listField, check);
        KeyWalks(bucketType, add, remove, listField, check);
        SkipEqualsWalk(bucketType, add, remove, listField, check);
        RangedEffects(bucketType, add, remove, listField, check);
        KeyWalkTiming(bucketType, add, listField);
    }

    // Ranged effect buildings without a mechanical part (roofs, lanterns, shrubs; 0.4.28): switched on, but their tick
    // does nothing, so an entity whose switched-on parts are all of that kind is passed over. The game's own Tick is run
    // on them to show it does nothing; a scripted history mixes them with mechanical ones and other parts, switched on
    // and off one by one, between passes and from inside ticks, and the effective ticks must be the game loop's, with
    // the rule on and with it stood down by another mod's patch.
    private static void RangedEffects(Type bucketType, MethodInfo add, MethodInfo remove, FieldInfo listField, Action<bool, string> check)
    {
        // The game's own Tick, on a component whose every field is null: without a mechanical part it returns without
        // touching anything; with one it goes on (and here trips over the empty component, which shows it went on).
        RangedEffectBuilding bare = NewRangedEffect(false);
        bool bareDoesNothing = true;
        try
        {
            bare.Tick();
        }
        catch (Exception)
        {
            bareDoesNothing = false;
        }
        bool mechanicalGoesOn = false;
        try
        {
            NewRangedEffect(true).Tick();
        }
        catch (NullReferenceException)
        {
            mechanicalGoesOn = true;
        }
        check(bareDoesNothing && mechanicalGoesOn,
            "idle entities: the game's RangedEffectBuilding.Tick does nothing without a mechanical part and goes on with one");
        string now = IdleEntities.RangedEffectHashNow();
        check(now == IdleEntities.RangedEffectHash,
            $"idle entities: the game's RangedEffectBuilding is the code that was read (hash {now}, read {IdleEntities.RangedEffectHash}; " +
            "if the game changed, read Tick and Awake again before updating IdleEntities.RangedEffectHash)");

        Func<MethodBase, IEnumerable<(string Owner, string Patch)>> previous = IdleEntities.PatchesOn;
        try
        {
            Random random = new Random(1618);
            const int passes = 300, pool = 90;
            Ent[] ents = new Ent[pool];
            for (int i = 0; i < pool; i++)
            {
                ents[i] = MakeMixedEntity(random, 500 + i);
            }
            bool[] initiallyIn = new bool[pool];
            for (int i = 0; i < pool; i++) initiallyIn[i] = i < 60;
            Op[] ops = { Op.Enable, Op.Disable, Op.Add, Op.Remove, Op.EnableLast, Op.DisableFirst };
            List<(int pass, Op op, int target)> between = new List<(int, Op, int)>();
            Dictionary<(int, int), Action> during = new Dictionary<(int, int), Action>();
            for (int pass = 0; pass < passes; pass++)
            {
                int n = random.Next(4);
                for (int k = 0; k < n; k++) between.Add((pass, ops[random.Next(ops.Length)], random.Next(pool)));
                int m = random.Next(3);
                for (int k = 0; k < m; k++) during[(pass, random.Next(pool))] = new Action { Op = ops[random.Next(ops.Length)], Target = random.Next(pool) };
            }
            bool[][] initial = Snapshot(ents);
            List<string> reference = Reference(ents, initiallyIn, between, during, passes);

            IdleEntities.PatchesOn = _ => null;
            IdleEntities.SceneCreated();
            IdleEntities.TakeStatsLine();
            List<string> on = Mirrored(ents, initiallyIn, between, during, passes, bucketType, add, remove, listField, check, out int onIdle);
            string onLine = IdleEntities.TakeStatsLine();
            IdleEntities.SceneCreated();
            Restore(ents, initial);

            // Another mod patches RangedEffectBuilding.Tick: the rule stands down for the game scene, and every such
            // entity is ticked through the game's own Tick again, where that patch runs.
            IdleEntities.PatchesOn = method => method.Name == "Tick" ? new[] { ("some.other.mod", "Other.Mod.RangedTick.Prefix") } : null;
            List<string> off = Mirrored(ents, initiallyIn, between, during, passes, bucketType, add, remove, listField, check, out int offIdle);
            string offLine = IdleEntities.TakeStatsLine();
            IdleEntities.SceneCreated();
            Restore(ents, initial);

            int firstOn = FirstDifference(reference, on), firstOff = FirstDifference(reference, off);
            check(firstOn < 0 && firstOff < 0,
                $"idle entities: {on.Count} effective ticks over {passes} passes with ranged effect buildings, mechanical and not, " +
                "in the same order as the game's own loop, with the rule on and stood down" +
                (firstOn < 0 ? "" : $" (on: first difference at {firstOn}: {At(reference, firstOn)} vs {At(on, firstOn)})") +
                (firstOff < 0 ? "" : $" (off: first difference at {firstOff}: {At(reference, firstOff)} vs {At(off, firstOff)})"));
            Console.WriteLine("     " + onLine);
            Console.WriteLine("     " + offLine);
            check(offIdle > 1000 && onIdle * 20 < offIdle && onLine != null && onLine.Contains("only had ranged effect buildings with no mechanical part switched on") &&
                  !onLine.Contains("(0 of them") && offLine != null && offLine.Contains("ranged effect buildings are ticked as before: another mod patches RangedEffectBuilding.Tick"),
                $"idle entities: entities whose switched-on parts are only ranged effect buildings without a mechanical part are passed " +
                $"over ({onIdle} visits that did nothing, against {offIdle} with the rule stood down by another mod's patch), and " +
                "the stats line says how many");

            // A component switched on before its Awake has found the mechanical part (the field still null): passed over.
            // Awake then sets the field and switches the component off; switched on again, the entity is ticked.
            IdleEntities.PatchesOn = _ => null;
            object bucket = Activator.CreateInstance(bucketType, true);
            IdleEntities.Activate();
            RangedEffectBuilding early = NewRangedEffect(false);
            EnabledField.SetValue(early, true);
            Ent ent = WithParts(700, early);
            add.Invoke(bucket, new object[] { ent.Entity });
            IdleEntities.AddPostfix(bucket, ent.Entity);
            string visits = "";
            int round = 0;
            IdleEntities.TickEntity = entity => visits += round;
            round = 1;
            IdleEntities.TickAllPrefix(bucket);
            MechanicalField.SetValue(early, RuntimeHelpers.GetUninitializedObject(typeof(MechanicalBuilding)));
            EnabledField.SetValue(early, false);
            IdleEntities.DisabledPostfix(early);
            round = 2;
            IdleEntities.TickAllPrefix(bucket);
            EnabledField.SetValue(early, true);
            IdleEntities.EnabledPostfix(early);
            round = 3;
            IdleEntities.TickAllPrefix(bucket);
            IdleEntities.TickEntity = entity => entity.Tick();
            check(visits == "3" && IdleEntities.IsActive,
                "idle entities: a ranged effect building switched on before its Awake is passed over until Awake finds a " +
                $"mechanical part, switches it off and it is switched on again (ticked in passes '{visits}', expected '3')");
            IdleEntities.TakeStatsLine();
            IdleEntities.SceneCreated();
        }
        finally
        {
            IdleEntities.PatchesOn = previous;
            IdleEntities.TickEntity = entity => entity.Tick();
        }
    }

    private static int FirstDifference(List<string> expected, List<string> got)
    {
        for (int i = 0; i < Math.Max(expected.Count, got.Count); i++)
        {
            if (i >= expected.Count || i >= got.Count || expected[i] != got[i])
            {
                return i;
            }
        }
        return -1;
    }

    // The game's RangedEffectBuilding with every field empty, and, if asked, a mechanical part.
    private static RangedEffectBuilding NewRangedEffect(bool mechanical)
    {
        RangedEffectBuilding building = (RangedEffectBuilding)RuntimeHelpers.GetUninitializedObject(typeof(RangedEffectBuilding));
        if (mechanical)
        {
            MechanicalField.SetValue(building, RuntimeHelpers.GetUninitializedObject(typeof(MechanicalBuilding)));
        }
        return building;
    }

    private static Ent WithParts(int index, params TickableComponent[] parts)
    {
        EntityComponent component = (EntityComponent)RuntimeHelpers.GetUninitializedObject(typeof(EntityComponent));
        Guid id = new Guid(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        EntityIdField.SetValue(component, id);
        List<MeteredTickableComponent> metered = new List<MeteredTickableComponent>();
        foreach (TickableComponent part in parts)
        {
            metered.Add(new MeteredTickableComponent(part, null, false));
        }
        return new Ent { Id = id, Entity = new TickableEntity(component, metered, "entity " + index), Parts = parts };
    }

    // A roof or lantern (a ranged effect building alone), one with another tick part as well (either order), a
    // mechanical one, or an entity without any. Ranged effect buildings are mostly switched on, as finished ones are.
    private static Ent MakeMixedEntity(Random random, int index)
    {
        TickableComponent[] parts;
        switch (random.Next(5))
        {
            case 0:
                parts = new TickableComponent[] { NewRangedEffect(false) };
                break;
            case 1:
                parts = new TickableComponent[] { NewRangedEffect(false), new Part() };
                break;
            case 2:
                parts = new TickableComponent[] { NewRangedEffect(true) };
                break;
            case 3:
                parts = new TickableComponent[] { new Part(), NewRangedEffect(false) };
                break;
            default:
                parts = new TickableComponent[] { new Part() };
                break;
        }
        foreach (TickableComponent part in parts)
        {
            EnabledField.SetValue(part, part is RangedEffectBuilding ? random.Next(4) != 0 : random.Next(4) == 0);
        }
        return WithParts(index, parts);
    }

    // A case the scripted history does not reach: A removes C during a pass (the game defers that to the end of the
    // pass), then B switches on C's tick part. C is still in the game's list, after B, so the game ticks it in that
    // pass; in the next one it is gone.
    private static void RemovedThenEnabled(Type bucketType, MethodInfo add, MethodInfo remove, FieldInfo listField,
        Action<bool, string> check)
    {
        object bucket = Activator.CreateInstance(bucketType, true);
        SortedList<Guid, TickableEntity> gameList = (SortedList<Guid, TickableEntity>)listField.GetValue(bucket);
        IdleEntities.Activate();
        Ent a = MakeEntity(0, true), b = MakeEntity(1, true), c = MakeEntity(2, false);
        Ent[] ents = { a, b, c };
        string[] names = { "A", "B", "C" };
        foreach (Ent ent in ents)
        {
            add.Invoke(bucket, new object[] { ent.Entity });
            IdleEntities.AddPostfix(bucket, ent.Entity);
        }
        List<string> ticks = new List<string>();
        int pass = 0;
        IdleEntities.TickEntity = entity =>
        {
            int index = Array.FindIndex(ents, ent => ent.Entity == entity);
            // The game's Tick does nothing for an entity whose components are all disabled.
            if (!ents[index].Awake) return;
            ticks.Add(pass + ":" + names[index]);
            if (pass == 1 && index == 0)
            {
                // A deletes C: EntityDeletedEvent -> TickableEntityBucket.Remove, deferred while ticking.
                remove.Invoke(bucket, new object[] { c.Entity });
                IdleEntities.RemovePostfix(bucket, c.Entity);
            }
            else if (pass == 1 && index == 1)
            {
                // B switches on C's tick part: BaseComponent.EnableComponent, then the mod's postfix.
                EnabledField.SetValue(c.Parts[0], true);
                IdleEntities.EnabledPostfix(c.Parts[0]);
            }
        };
        for (pass = 1; pass <= 2; pass++)
        {
            IdleEntities.TickAllPrefix(bucket);
        }
        IdleEntities.TickEntity = entity => entity.Tick();
        string got = string.Join(",", ticks);
        check(got == "1:A,1:B,1:C,2:A,2:B" && gameList.Count == 2 && IdleEntities.IsActive,
            "idle entities: an entity removed during a pass and switched on later in it is ticked in that pass, as by the " +
            $"game, and not after (expected 1:A,1:B,1:C,2:A,2:B, got {got}; {gameList.Count} left in the game's list)");
        string line = IdleEntities.TakeStatsLine();
        check(line != null && !line.Contains("rebuilt"), "idle entities: the mirror stayed in step through the removal: " + line);
        IdleEntities.SceneCreated();
    }

    // Each pass starts by comparing the mirror's keys with the game's list. That walk is skipped while the list's own
    // change counter stands where it stood when the keys last matched, with a full walk on every 128th pass of a bucket
    // anyway. A change made to the game's list behind the mod's hooks must still force a rebuild: on the next pass when
    // it moves the counter (anything done through the list's methods does), by the 128th pass when it does not (only a
    // write into the list's private fields could manage that).
    private static void KeyWalks(Type bucketType, MethodInfo add, MethodInfo remove, FieldInfo listField, Action<bool, string> check)
    {
        MethodInfo counterFor = typeof(IdleEntities).GetMethod("ChangeCounter", Statics);
        FieldInfo counterField = typeof(IdleEntities).GetField("_listVersion", Statics);
        check(counterFor != null && counterField != null && Counter("_keyChecks") >= 0,
            "idle entities: the key walk can be skipped by the game's list's change counter (IdleEntities.ChangeCounter, " +
            "_listVersion and _keyChecks exist)");
        if (counterFor != null)
        {
            Func<object, int> CounterOf(Type type) => (Func<object, int>)counterFor.Invoke(null, new object[] { type });
            Func<object, int> plain = CounterOf(typeof(CountedList)), underscored = CounterOf(typeof(UnderscoreCountedList));
            Func<object, int> none = CounterOf(typeof(UncountedList)), real = CounterOf(typeof(SortedList<Guid, TickableEntity>));
            SortedList<Guid, TickableEntity> probe = new SortedList<Guid, TickableEntity>();
            int start = real == null ? 0 : real(probe);
            probe.Add(new Guid(9, 9, 9, 0, 0, 0, 0, 0, 0, 0, 0), null);
            check(plain != null && plain(new CountedList()) == 7 && underscored != null &&
                  underscored(new UnderscoreCountedList()) == 9 && none == null && real != null && real(probe) != start,
                "idle entities: the list's change counter is read as 'version' (the game's Mono, and .NET 8) or '_version', and " +
                "a list with neither gets none");
        }

        object bucket = Activator.CreateInstance(bucketType, true);
        SortedList<Guid, TickableEntity> gameList = (SortedList<Guid, TickableEntity>)listField.GetValue(bucket);
        Ent[] ents = new Ent[24];
        Dictionary<TickableEntity, int> index = new Dictionary<TickableEntity, int>();
        for (int i = 0; i < ents.Length; i++)
        {
            ents[i] = MakeEntity(200 + i, i % 3 == 0);
            index[ents[i].Entity] = i;
        }
        IdleEntities.Activate();
        IdleEntities.TakeStatsLine();
        void HookedAdd(Ent ent)
        {
            add.Invoke(bucket, new object[] { ent.Entity });
            IdleEntities.AddPostfix(bucket, ent.Entity);
        }
        void HookedRemove(Ent ent)
        {
            remove.Invoke(bucket, new object[] { ent.Entity });
            IdleEntities.RemovePostfix(bucket, ent.Entity);
        }
        // Behind the hooks: one entity out of the game's list and another in, so the counts still agree.
        void Swap(int leaving, int joining, bool putCounterBack)
        {
            object version = ListVersionField.GetValue(gameList);
            gameList.Remove(ents[leaving].Id);
            gameList.Add(ents[joining].Id, ents[joining].Entity);
            if (putCounterBack)
            {
                ListVersionField.SetValue(gameList, version);
            }
        }
        for (int i = 0; i < 10; i++)
        {
            HookedAdd(ents[i]);
        }
        List<string> ticks = new List<string>();
        System.Action midPass = null;
        IdleEntities.TickEntity = entity =>
        {
            int i = index[entity];
            if (!ents[i].Awake) return;
            ticks.Add(i.ToString());
            System.Action once = midPass;
            midPass = null;
            once?.Invoke();
        };
        // wrongPasses is set back to 0 at the start of each case below, so each check reports only its own passes.
        int pass = 0, wrongPasses = 0;
        // One pass: whether the mirror was rebuilt at its start, and, unless the list is changed behind the hooks during
        // it, whether its ticks were the game's loop's.
        bool Pass(bool compare = true)
        {
            string expected = GameLoop(gameList, ents, index);
            long rebuilds = Counter("_resyncs");
            ticks.Clear();
            IdleEntities.TickAllPrefix(bucket);
            pass++;
            if (compare && string.Join(",", ticks) != expected)
            {
                wrongPasses++;
            }
            return Counter("_resyncs") != rebuilds;
        }

        // Nothing changes for 20 passes: the first compares the keys, the other 19 find the counter where it was.
        int rebuiltEarly = 0;
        for (int i = 0; i < 20; i++)
        {
            if (Pass()) rebuiltEarly++;
        }
        long compared = Counter("_keyChecks");
        check(compared == 1 && rebuiltEarly == 0 && wrongPasses == 0,
            $"idle entities: with the game's list unchanged the keys are compared on the first of 20 passes only (compared on {compared}, " +
            $"rebuilt {rebuiltEarly} times, {wrongPasses} passes unlike the game's)");

        // Behind the hooks, with the counter put back: only the full walk on the 128th pass since the last one sees it.
        wrongPasses = 0;
        Swap(1, 12, true);
        int rebuiltAt = 0;
        while (rebuiltAt == 0 && pass < 300)
        {
            if (Pass(false)) rebuiltAt = pass;
        }
        bool again = Pass();
        check(rebuiltAt == 129 && !again && wrongPasses == 0,
            "idle entities: a change behind the hooks that leaves the list's counter where it was is found by the full key walk on " +
            $"the 128th pass since the last one (rebuilt at pass {rebuiltAt}, expected 129; then {(again ? "rebuilt again" : "in step")}, " +
            $"{wrongPasses} passes unlike the game's)");

        // Behind the hooks, moving the counter, as anything done through the list's methods does: the next pass rebuilds,
        // as it did when the keys were compared on every pass. Then the same followed by an add through the hooks, which
        // brings the counts back together; then the same from inside a tick in the middle of a pass.
        wrongPasses = 0;
        Swap(2, 13, false);
        bool swapped = Pass();
        Swap(4, 14, false);
        HookedAdd(ents[15]);
        bool swappedThenAdded = Pass();
        midPass = () => Swap(5, 16, false);
        bool swappedMidPass = Pass(false);
        bool passAfter = Pass();
        check(swapped && swappedThenAdded && !swappedMidPass && passAfter && wrongPasses == 0,
            "idle entities: a change behind the hooks that moves the list's counter makes the next pass rebuild the mirror " +
            $"(swapped: {swapped}; swapped, then an add through the hooks: {swappedThenAdded}; swapped in the middle of a pass: " +
            $"{swappedMidPass} in that pass, {passAfter} in the next; {wrongPasses} passes unlike the game's)");

        // Through the hooks, between passes and in the middle of one: the keys are compared once on the next pass, match,
        // and are skipped again after that.
        wrongPasses = 0;
        long before = Counter("_keyChecks");
        HookedAdd(ents[17]);
        HookedRemove(ents[0]);
        bool hooked = Pass();
        long afterHooked = Counter("_keyChecks");
        midPass = () => HookedRemove(ents[3]);
        bool hookedMidPass = Pass();
        bool quiet = Pass();
        long afterQuiet = Counter("_keyChecks");
        bool last = Pass();
        long afterLast = Counter("_keyChecks");
        check(!hooked && !hookedMidPass && !quiet && !last && afterHooked - before == 1 && afterQuiet - afterHooked == 1 &&
              afterLast == afterQuiet && wrongPasses == 0 && !gameList.ContainsKey(ents[3].Id),
            "idle entities: after changes through the hooks the keys are compared on the next pass only, and match (compared on " +
            $"{afterHooked - before} of 1 pass after an add and a removal between passes, {afterQuiet - afterHooked} of the 2 " +
            $"during and after a removal in the middle of a pass, {afterLast - afterQuiet} of 1 after that; rebuilt: " +
            $"{hooked || hookedMidPass || quiet || last}; {wrongPasses} passes unlike the game's)");

        // Another mod's Harmony prefix that skips the game's Add, and one that skips its Remove: Harmony still runs the
        // postfixes, so the mirror gains a key the game's list never got and loses one the list keeps. The counts still
        // agree and the list's counter does not move, yet the next pass must rebuild, as it did when the keys were
        // compared on every pass.
        wrongPasses = 0;
        IdleEntities.AddPostfix(bucket, ents[21].Entity);
        IdleEntities.RemovePostfix(bucket, ents[7].Entity);
        bool skippedOriginals = Pass();
        bool afterSkipped = Pass();
        check(skippedOriginals && !afterSkipped && wrongPasses == 0 && !gameList.ContainsKey(ents[21].Id) &&
              gameList.ContainsKey(ents[7].Id),
            "idle entities: an add and a removal whose game methods another mod skipped (only the postfixes ran) make the next " +
            $"pass rebuild the mirror (rebuilt: {skippedOriginals}; then {(afterSkipped ? "rebuilt again" : "in step")}; " +
            $"{wrongPasses} passes unlike the game's)");

        // A different list object in the bucket's field, with one other key and its counter set to where the old list's
        // stood. The game's field is readonly, so this is only a safeguard: a counter says nothing about another list, so
        // the next pass walks the keys and rebuilds.
        wrongPasses = 0;
        SortedList<Guid, TickableEntity> replacement = new SortedList<Guid, TickableEntity>(gameList);
        replacement.Remove(ents[9].Id);
        replacement.Add(ents[20].Id, ents[20].Entity);
        ListVersionField.SetValue(replacement, ListVersionField.GetValue(gameList));
        listField.SetValue(bucket, replacement);
        gameList = replacement;
        bool replaced = Pass();
        bool afterReplaced = Pass();
        check(replaced && !afterReplaced && wrongPasses == 0,
            "idle entities: another list object in the bucket, with the same count and the old list's counter, makes the next " +
            $"pass rebuild the mirror (rebuilt: {replaced}; then {(afterReplaced ? "rebuilt again" : "in step")}; " +
            $"{wrongPasses} passes unlike the game's)");

        // With no counter to read, every pass compares the keys, and a change the counter would not show is found at once.
        object counter = counterField?.GetValue(null);
        counterField?.SetValue(null, null);
        try
        {
            wrongPasses = 0;
            long walksBefore = Counter("_keyChecks");
            Pass();
            Pass();
            long walks = Counter("_keyChecks") - walksBefore;
            Swap(6, 18, true);
            bool found = Pass();
            check(found && wrongPasses == 0,
                "idle entities: with no change counter a change behind the hooks is found on the next pass " +
                $"(rebuilt: {found}; {wrongPasses} passes unlike the game's)");
            check(walks == 2, $"idle entities: with no change counter the keys are compared on every pass (on {walks} of 2)");
        }
        finally
        {
            counterField?.SetValue(null, counter);
        }
        IdleEntities.TickEntity = entity => entity.Tick();
        string line = IdleEntities.TakeStatsLine();
        Console.WriteLine("     " + line);
        check(line != null && line.Contains("keys walked against the game's list on"),
            "idle entities stats: says on how many passes the keys were walked");
        IdleEntities.SceneCreated();
    }

    // One random history run twice, once with the key walk skipped by the counter and once with the keys compared on every
    // pass, over the game's real bucket. Besides the changes through the hooks it changes the game's list behind them,
    // between passes and from inside ticks (each such change moves the list's counter). The effective ticks and the
    // passes that rebuild the mirror must be the same both times.
    private static void SkipEqualsWalk(Type bucketType, MethodInfo add, MethodInfo remove, FieldInfo listField, Action<bool, string> check)
    {
        FieldInfo counterField = typeof(IdleEntities).GetField("_listVersion", Statics);
        object counter = counterField?.GetValue(null);
        Random random = new Random(31415);
        const int passes = 400, pool = 60;
        Ent[] ents = new Ent[pool];
        Dictionary<TickableEntity, int> index = new Dictionary<TickableEntity, int>();
        for (int i = 0; i < pool; i++)
        {
            ents[i] = MakeEntity(random, 300 + i);
            index[ents[i].Entity] = i;
        }
        // Between passes: (pass, op, target, other). During passes: (pass, actor) -> (op, target, other). About a third of
        // the passes follow a change, so the others can skip the walk.
        List<(int pass, Op op, int target, int other)> between = new List<(int, Op, int, int)>();
        Dictionary<(int, int), (Op op, int target, int other)> during = new Dictionary<(int, int), (Op, int, int)>();
        for (int pass = 0; pass < passes; pass++)
        {
            if (random.Next(3) == 0)
            {
                int n = 1 + random.Next(3);
                for (int k = 0; k < n; k++) between.Add((pass, (Op)random.Next(1, 8), random.Next(pool), random.Next(pool)));
            }
            if (random.Next(3) == 0)
            {
                during[(pass, random.Next(pool))] = ((Op)random.Next(1, 8), random.Next(pool), random.Next(pool));
            }
        }

        List<string> Trace(bool everyPass, out long walks)
        {
            bool[][] initial = Snapshot(ents);
            counterField?.SetValue(null, everyPass ? null : counter);
            IdleEntities.Activate();
            IdleEntities.TakeStatsLine();
            object bucket = Activator.CreateInstance(bucketType, true);
            SortedList<Guid, TickableEntity> list = (SortedList<Guid, TickableEntity>)listField.GetValue(bucket);
            void Apply(Op op, int target, int other)
            {
                Ent ent = ents[target];
                switch (op)
                {
                    case Op.Enable:
                        EnabledField.SetValue(ent.Parts[0], true);
                        IdleEntities.EnabledPostfix(ent.Parts[0]);
                        break;
                    case Op.Disable:
                        foreach (TickableComponent part in ent.Parts)
                        {
                            EnabledField.SetValue(part, false);
                            IdleEntities.DisabledPostfix(part);
                        }
                        break;
                    case Op.Add:
                        if (list.ContainsKey(ent.Id)) break;
                        add.Invoke(bucket, new object[] { ent.Entity });
                        IdleEntities.AddPostfix(bucket, ent.Entity);
                        break;
                    case Op.Remove:
                        if (!list.ContainsKey(ent.Id)) break;
                        remove.Invoke(bucket, new object[] { ent.Entity });
                        IdleEntities.RemovePostfix(bucket, ent.Entity);
                        break;
                    case Op.DirectSwap:
                        if (!list.ContainsKey(ent.Id) || list.ContainsKey(ents[other].Id)) break;
                        list.Remove(ent.Id);
                        list.Add(ents[other].Id, ents[other].Entity);
                        break;
                    case Op.DirectAdd:
                        if (!list.ContainsKey(ent.Id)) list.Add(ent.Id, ent.Entity);
                        break;
                    case Op.DirectRemove:
                        list.Remove(ent.Id);
                        break;
                }
            }
            for (int i = 0; i < 40; i++)
            {
                Apply(Op.Add, i, 0);
            }
            List<string> trace = new List<string>();
            int pass = 0;
            IdleEntities.TickEntity = entity =>
            {
                int i = index[entity];
                if (!ents[i].Awake) return;
                trace.Add(pass + ":" + i);
                if (during.TryGetValue((pass, i), out (Op op, int target, int other) action)) Apply(action.op, action.target, action.other);
            };
            for (pass = 0; pass < passes; pass++)
            {
                foreach ((int p, Op op, int target, int other) in between)
                {
                    if (p == pass) Apply(op, target, other);
                }
                long rebuilds = Counter("_resyncs");
                IdleEntities.TickAllPrefix(bucket);
                if (Counter("_resyncs") != rebuilds) trace.Add("rebuilt in pass " + pass);
            }
            IdleEntities.TickEntity = entity => entity.Tick();
            walks = Counter("_keyChecks");
            IdleEntities.TakeStatsLine();
            IdleEntities.SceneCreated();
            Restore(ents, initial);
            return trace;
        }

        List<string> walked, skipped;
        long walkedCompares, skippedCompares;
        try
        {
            walked = Trace(true, out walkedCompares);
            skipped = Trace(false, out skippedCompares);
        }
        finally
        {
            counterField?.SetValue(null, counter);
        }
        int firstDifference = -1;
        for (int i = 0; i < Math.Max(walked.Count, skipped.Count); i++)
        {
            if (i >= walked.Count || i >= skipped.Count || walked[i] != skipped[i])
            {
                firstDifference = i;
                break;
            }
        }
        int rebuilt = walked.FindAll(entry => entry.StartsWith("rebuilt")).Count;
        check(firstDifference < 0 && rebuilt > 0,
            $"idle entities: {walked.Count - rebuilt} effective ticks and {rebuilt} rebuilds over {passes} passes of a history that " +
            "also changes the game's list behind the hooks, the same with the key walk skipped by the counter as with the keys " +
            "compared on every pass" +
            (firstDifference < 0 ? "" : $" (first difference at {firstDifference}: {At(walked, firstDifference)} vs {At(skipped, firstDifference)})"));
        check(skippedCompares > 0 && skippedCompares < walkedCompares / 2,
            $"idle entities: in that history the counter spared most key walks (compared on {skippedCompares} passes, against " +
            $"{walkedCompares} without it)");
    }

    // For the record, not a check: a tick's 128 passes over 11,700 entities in 128 buckets, 1 in 12 of them with a part
    // switched on (the logged colony had about 11,500, most asleep), with the keys compared on every pass and with the
    // walk skipped while the lists are unchanged.
    private static void KeyWalkTiming(Type bucketType, MethodInfo add, FieldInfo listField)
    {
        FieldInfo counterField = typeof(IdleEntities).GetField("_listVersion", Statics);
        object counter = counterField?.GetValue(null);
        const int bucketCount = 128, entityCount = 11700, rounds = 2000;
        IdleEntities.Activate();
        object[] buckets = new object[bucketCount];
        for (int b = 0; b < bucketCount; b++)
        {
            buckets[b] = Activator.CreateInstance(bucketType, true);
        }
        for (int i = 0; i < entityCount; i++)
        {
            Ent ent = MakeEntity(1000 + i, i % 12 == 0);
            add.Invoke(buckets[i % bucketCount], new object[] { ent.Entity });
            IdleEntities.AddPostfix(buckets[i % bucketCount], ent.Entity);
        }
        IdleEntities.TickEntity = entity => { };
        double MicrosecondsPerTick(bool everyPass)
        {
            counterField?.SetValue(null, everyPass ? null : counter);
            for (int r = 0; r < 20; r++)
            {
                foreach (object bucket in buckets) IdleEntities.TickAllPrefix(bucket);
            }
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            for (int r = 0; r < rounds; r++)
            {
                foreach (object bucket in buckets) IdleEntities.TickAllPrefix(bucket);
            }
            return watch.Elapsed.TotalMilliseconds * 1000 / rounds;
        }
        double walked, skipped;
        try
        {
            walked = Math.Min(MicrosecondsPerTick(true), MicrosecondsPerTick(true));
            skipped = Math.Min(MicrosecondsPerTick(false), MicrosecondsPerTick(false));
            walked = Math.Min(walked, MicrosecondsPerTick(true));
            skipped = Math.Min(skipped, MicrosecondsPerTick(false));
        }
        finally
        {
            counterField?.SetValue(null, counter);
            IdleEntities.TickEntity = entity => entity.Tick();
        }
        Console.WriteLine($"     timing: a tick's {bucketCount} passes over {entityCount} entities (1 in 12 awake), {walked:0} us with the keys " +
                          $"compared on every pass, {skipped:0} us with the walk skipped while the list is unchanged: the walk costs " +
                          $"{walked - skipped:0} us per tick, {(walked - skipped) / bucketCount:0.00} us per pass");
        IdleEntities.TakeStatsLine();
        IdleEntities.SceneCreated();
    }

    // The game's pass over its list with nothing changing during it: the entities with a part switched on, in order.
    private static string GameLoop(SortedList<Guid, TickableEntity> list, Ent[] ents, Dictionary<TickableEntity, int> index)
    {
        List<string> awake = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            int at = index[list.Values[i]];
            if (ents[at].Awake) awake.Add(at.ToString());
        }
        return string.Join(",", awake);
    }

    // A private counter of the mod's, or -1 if this build has none.
    private static long Counter(string name)
    {
        FieldInfo field = typeof(IdleEntities).GetField(name, Statics);
        return field == null ? -1 : Convert.ToInt64(field.GetValue(null));
    }

    private static string At(List<string> list, int i)
    {
        return i < list.Count ? list[i] : "(end)";
    }

    private static Ent MakeEntity(Random random, int index)
    {
        EntityComponent component = (EntityComponent)RuntimeHelpers.GetUninitializedObject(typeof(EntityComponent));
        Guid id = new Guid(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        EntityIdField.SetValue(component, id);
        Part[] parts = new Part[1 + random.Next(2)];
        List<MeteredTickableComponent> metered = new List<MeteredTickableComponent>();
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = new Part();
            // Most parts start disabled, as a tree's or a path's do.
            EnabledField.SetValue(parts[i], random.Next(4) == 0);
            metered.Add(new MeteredTickableComponent(parts[i], null, false));
        }
        return new Ent { Id = id, Entity = new TickableEntity(component, metered, "entity " + index), Parts = parts };
    }

    // One tick part, switched on or off.
    private static Ent MakeEntity(int index, bool enabled)
    {
        EntityComponent component = (EntityComponent)RuntimeHelpers.GetUninitializedObject(typeof(EntityComponent));
        Guid id = new Guid(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        EntityIdField.SetValue(component, id);
        Part part = new Part();
        EnabledField.SetValue(part, enabled);
        List<MeteredTickableComponent> metered = new List<MeteredTickableComponent> { new MeteredTickableComponent(part, null, false) };
        return new Ent { Id = id, Entity = new TickableEntity(component, metered, "entity " + index), Parts = new[] { part } };
    }

    // The game's loop, modelled: TickableEntityBucket.TickAll over a SortedList, deferring removals while ticking;
    // TickableEntity.Tick does something only if a component is enabled.
    private static List<string> Reference(Ent[] ents, bool[] initiallyIn, List<(int pass, Op op, int target)> between,
        Dictionary<(int, int), Action> during, int passes)
    {
        List<string> ticks = new List<string>();
        SortedList<Guid, TickableEntity> list = new SortedList<Guid, TickableEntity>();
        Dictionary<TickableEntity, Ent> byEntity = new Dictionary<TickableEntity, Ent>();
        foreach (Ent ent in ents)
        {
            byEntity[ent.Entity] = ent;
        }
        // Snapshot the initial enabled flags so the mirrored run can start from the same state.
        bool[][] initial = Snapshot(ents);
        for (int i = 0; i < ents.Length; i++)
        {
            if (initiallyIn[i]) list.Add(ents[i].Id, ents[i].Entity);
        }
        List<TickableEntity> toRemove = new List<TickableEntity>();
        bool ticking = false;
        void Apply(Op op, int target)
        {
            Ent ent = ents[target];
            switch (op)
            {
                case Op.Enable:
                    EnabledField.SetValue(ent.Parts[0], true);
                    break;
                case Op.Disable:
                    foreach (TickableComponent part in ent.Parts) EnabledField.SetValue(part, false);
                    break;
                case Op.EnableLast:
                    EnabledField.SetValue(ent.Parts[ent.Parts.Length - 1], true);
                    break;
                case Op.DisableFirst:
                    EnabledField.SetValue(ent.Parts[0], false);
                    break;
                case Op.Add:
                    if (!list.ContainsKey(ent.Id)) list.Add(ent.Id, ent.Entity);
                    break;
                case Op.Remove:
                    if (list.ContainsKey(ent.Id))
                    {
                        if (ticking) toRemove.Add(ent.Entity); else list.Remove(ent.Id);
                    }
                    break;
            }
        }
        for (int pass = 0; pass < passes; pass++)
        {
            foreach ((int p, Op op, int target) in between)
            {
                if (p == pass) Apply(op, target);
            }
            ticking = true;
            for (int i = 0; i < list.Count; i++)
            {
                TickableEntity entity = list.Values[i];
                Ent ent = byEntity[entity];
                if (ent.Awake)
                {
                    ticks.Add(pass + ":" + ent.Id.ToString().Substring(0, 8));
                    if (during.TryGetValue((pass, Array.IndexOf(ents, ent)), out Action action)) Apply(action.Op, action.Target);
                }
            }
            ticking = false;
            foreach (TickableEntity entity in toRemove) list.Remove(byEntity[entity].Id);
            toRemove.Clear();
        }
        Restore(ents, initial);
        return ticks;
    }

    // The mirror-driven loop over the game's real bucket, driven through the mod's patch bodies. idleVisits counts the
    // entities the mod ticked although ticking them did nothing.
    private static List<string> Mirrored(Ent[] ents, bool[] initiallyIn, List<(int pass, Op op, int target)> between,
        Dictionary<(int, int), Action> during, int passes, Type bucketType, MethodInfo add, MethodInfo remove, FieldInfo listField,
        Action<bool, string> check, out int idleVisits)
    {
        int idle = 0;
        List<string> ticks = new List<string>();
        object bucket = Activator.CreateInstance(bucketType, true);
        SortedList<Guid, TickableEntity> gameList = (SortedList<Guid, TickableEntity>)listField.GetValue(bucket);
        Dictionary<TickableEntity, Ent> byEntity = new Dictionary<TickableEntity, Ent>();
        foreach (Ent ent in ents) byEntity[ent.Entity] = ent;
        IdleEntities.Activate();
        void GameAdd(Ent ent)
        {
            if (gameList.ContainsKey(ent.Id)) return;
            add.Invoke(bucket, new object[] { ent.Entity });
            IdleEntities.AddPostfix(bucket, ent.Entity);
        }
        void GameRemove(Ent ent)
        {
            if (!gameList.ContainsKey(ent.Id)) return;
            remove.Invoke(bucket, new object[] { ent.Entity });
            IdleEntities.RemovePostfix(bucket, ent.Entity);
        }
        void Apply(Op op, int target)
        {
            Ent ent = ents[target];
            switch (op)
            {
                case Op.Enable:
                    // What BaseComponent.EnableComponent does, then the mod's postfix.
                    EnabledField.SetValue(ent.Parts[0], true);
                    IdleEntities.EnabledPostfix(ent.Parts[0]);
                    break;
                case Op.Disable:
                    foreach (TickableComponent part in ent.Parts)
                    {
                        EnabledField.SetValue(part, false);
                        IdleEntities.DisabledPostfix(part);
                    }
                    break;
                case Op.EnableLast:
                    EnabledField.SetValue(ent.Parts[ent.Parts.Length - 1], true);
                    IdleEntities.EnabledPostfix(ent.Parts[ent.Parts.Length - 1]);
                    break;
                case Op.DisableFirst:
                    EnabledField.SetValue(ent.Parts[0], false);
                    IdleEntities.DisabledPostfix(ent.Parts[0]);
                    break;
                case Op.Add:
                    GameAdd(ent);
                    break;
                case Op.Remove:
                    GameRemove(ent);
                    break;
            }
        }
        for (int i = 0; i < ents.Length; i++)
        {
            if (initiallyIn[i]) GameAdd(ents[i]);
        }
        int pass = 0;
        IdleEntities.TickEntity = entity =>
        {
            Ent ent = byEntity[entity];
            // The game's own RangedEffectBuilding.Tick, on each switched-on one without a mechanical part: it must do
            // nothing (every other field of these is null, so touching anything would throw).
            foreach (TickableComponent part in ent.Parts)
            {
                if (part.Enabled && DoesNothing(part)) part.Tick();
            }
            // The game's Tick does nothing for an entity whose switched-on components all do nothing.
            if (!ent.Awake)
            {
                idle++;
                return;
            }
            ticks.Add(pass + ":" + ent.Id.ToString().Substring(0, 8));
            if (during.TryGetValue((pass, Array.IndexOf(ents, ent)), out Action action)) Apply(action.Op, action.Target);
        };
        int gameLoops = 0;
        for (pass = 0; pass < passes; pass++)
        {
            foreach ((int p, Op op, int target) in between)
            {
                if (p == pass) Apply(op, target);
            }
            if (IdleEntities.TickAllPrefix(bucket)) gameLoops++;
        }
        check(gameLoops == 0, "idle entities: every pass was taken over by the mirror");
        IdleEntities.TickEntity = entity => entity.Tick();
        idleVisits = idle;
        return ticks;
    }

    private static bool[][] Snapshot(Ent[] ents)
    {
        bool[][] flags = new bool[ents.Length][];
        for (int i = 0; i < ents.Length; i++)
        {
            flags[i] = new bool[ents[i].Parts.Length];
            for (int j = 0; j < flags[i].Length; j++) flags[i][j] = ents[i].Parts[j].Enabled;
        }
        return flags;
    }

    private static void Restore(Ent[] ents, bool[][] flags)
    {
        for (int i = 0; i < ents.Length; i++)
        {
            for (int j = 0; j < flags[i].Length; j++) EnabledField.SetValue(ents[i].Parts[j], flags[i][j]);
        }
    }
}
