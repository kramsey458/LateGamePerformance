using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
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
        public Part[] Parts;
        public bool Awake => Array.Exists(Parts, part => part.Enabled);
    }

    // What an entity does when it is ticked in a given pass: a change to some other entity.
    private enum Op { None, Enable, Disable, Add, Remove }

    private struct Action
    {
        public Op Op;
        public int Target;
    }

    private static readonly FieldInfo EnabledField = typeof(BaseComponent).GetField("<Enabled>k__BackingField", Any);
    private static readonly FieldInfo EntityIdField = typeof(EntityComponent).GetField("<EntityId>k__BackingField", Any);

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
        List<string> mirrored = Mirrored(ents, initiallyIn, between, during, passes, bucketType, add, remove, listField, check);
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
            foreach (Part part in ent.Parts) EnabledField.SetValue(part, ((Part)part).Enabled);
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
                    foreach (Part part in ent.Parts) EnabledField.SetValue(part, false);
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

    // The mirror-driven loop over the game's real bucket, driven through the mod's patch bodies.
    private static List<string> Mirrored(Ent[] ents, bool[] initiallyIn, List<(int pass, Op op, int target)> between,
        Dictionary<(int, int), Action> during, int passes, Type bucketType, MethodInfo add, MethodInfo remove, FieldInfo listField,
        Action<bool, string> check)
    {
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
                    foreach (Part part in ent.Parts)
                    {
                        EnabledField.SetValue(part, false);
                        IdleEntities.DisabledPostfix(part);
                    }
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
            // The game's Tick does nothing for an entity whose components are all disabled.
            if (!ent.Awake) return;
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
