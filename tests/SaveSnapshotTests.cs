using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using LateGamePerformance;
using Timberborn.Persistence;
using Timberborn.WorldPersistence;
using Timberborn.WorldSerialization;

// The parallel snapshot builder against the game's real EntitySaver, ObjectSaver, SerializedEntity and
// SerializedWorld: fake persistent parts write varied values (numbers, strings, lists, nested objects through a
// value serializer), some entities are eligible for workers and some are not, and the result must equal the
// sequential snapshot entity for entity and keep the game's order; a throwing part comes back as a failure with
// nothing thrown; parts not on the list run on the calling thread.
internal static class SaveSnapshotTests
{
    private sealed class Part : IPersistentEntity
    {
        private static readonly ComponentKey Key = new ComponentKey("Part");
        private static readonly PropertyKey<int> Number = new PropertyKey<int>("Number");
        private static readonly PropertyKey<string> Text = new PropertyKey<string>("Text");
        private static readonly ListKey<float> Floats = new ListKey<float>("Floats");
        private static readonly PropertyKey<Pair> Nested = new PropertyKey<Pair>("Nested");
        private static readonly PairSerializer Serializer = new PairSerializer();

        private readonly int _seed;
        private readonly string _suffix;
        public bool Throws;
        public int ThreadId;

        public Part(int seed, string suffix)
        {
            _seed = seed;
            _suffix = suffix;
        }

        public void Save(IEntitySaver entitySaver)
        {
            ThreadId = Thread.CurrentThread.ManagedThreadId;
            if (Throws) throw new InvalidOperationException("part refuses");
            if (_seed % 5 == 0) return;   // nothing to say, like a fully grown tree
            IObjectSaver saver = entitySaver.GetComponent(Key, _suffix);
            saver.Set(Number, _seed);
            saver.Set(Text, "value " + _seed);
            saver.Set(Floats, new[] { _seed * 0.5f, _seed * 0.25f });
            saver.Set(Nested, new Pair { A = _seed, B = _seed * 2 }, Serializer);
        }

        public void Load(IEntityLoader entityLoader)
        {
        }
    }

    private struct Pair
    {
        public int A;
        public int B;
    }

    // A Save that reaches other code: an interface call whose implementation could be patched, and static calls
    // three and four deep.
    private interface IHelper
    {
        void Help();
    }

    private sealed class HelperImpl : IHelper
    {
        public void Help()
        {
        }
    }

    private static class Chain
    {
        public static void Level1() => Level2();
        public static void Level2() => Level3();
        public static void Level3() => Level4();
        public static void Level4()
        {
        }
    }

    private sealed class Reaching : IPersistentEntity
    {
        private readonly IHelper _helper = new HelperImpl();

        public void Save(IEntitySaver entitySaver)
        {
            _helper.Help();
            Chain.Level1();
        }

        public void Load(IEntityLoader entityLoader)
        {
        }
    }

    private sealed class PairSerializer : IValueSerializer<Pair>
    {
        private static readonly PropertyKey<int> A = new PropertyKey<int>("A");
        private static readonly PropertyKey<int> B = new PropertyKey<int>("B");

        public void Serialize(Pair value, IValueSaver valueSaver)
        {
            IObjectSaver saver = valueSaver.AsObject();
            saver.Set(A, value.A);
            saver.Set(B, value.B);
        }

        public Obsoletable<Pair> Deserialize(IValueLoader valueLoader)
        {
            return new Pair();
        }
    }

    public static void Run(Action<bool, string> check)
    {
        Random random = new Random(4321);
        SaveSnapshot.CurrentVersion = () => Timberborn.Versioning.Version.Create("1.1.2.4");
        List<SaveSnapshot.Item> items = MakeItems(random, 3000, out List<Part> parts);
        SerializedEntity[] parallel = SaveSnapshot.Build(items, 7, out Exception failure, out long workerTicks);
        check(failure == null && parallel != null, "save snapshot: parallel build succeeded");
        int mainThread = Thread.CurrentThread.ManagedThreadId;
        bool mainOnMain = true, workersElsewhere = 0 < items.Count;
        int elsewhere = 0;
        for (int i = 0; i < items.Count; i++)
        {
            foreach (IPersistentEntity part in items[i].Parts)
            {
                Part p = (Part)part;
                if (!items[i].OnWorkers) mainOnMain &= p.ThreadId == mainThread;
                else if (p.ThreadId != mainThread) elsewhere++;
            }
        }
        check(mainOnMain && elsewhere > 0, $"save snapshot: parts not on the list ran on the calling thread; {elsewhere} eligible parts ran on workers");
        SerializedEntity[] sequential = Sequential(items);
        int same = 0, nulls = 0;
        for (int i = 0; i < items.Count; i++)
        {
            bool bothNull = parallel[i] == null && sequential[i] == null;
            if (bothNull) nulls++;
            if (bothNull || (parallel[i] != null && sequential[i] != null && SaveSnapshot.DeepEquals(parallel[i], sequential[i]))) same++;
        }
        check(same == items.Count, $"save snapshot: all {items.Count} entities equal the sequential snapshot ({nulls} with nothing to write)");
        SerializedWorld world = SaveSnapshot.Assemble(items, parallel);
        List<SerializedEntity> ordered = new List<SerializedEntity>(world.Entities());
        bool inOrder = ordered.Count == items.Count - nulls;
        int next = 0;
        foreach (SerializedEntity entity in ordered)
        {
            while (next < items.Count && parallel[next] == null) next++;
            inOrder &= next < items.Count && ReferenceEquals(entity, parallel[next]);
            next++;
        }
        check(inOrder, "save snapshot: the world holds the entities in the game's order");
        check(workerTicks > 0, "save snapshot: worker time was measured");

        parts[random.Next(parts.Count)].Throws = true;
        SerializedEntity[] failed = SaveSnapshot.Build(items, 7, out failure, out _);
        check(failed == null && failure is InvalidOperationException, "save snapshot: a throwing part comes back as a failure with nothing thrown");
        foreach (Part part in parts) part.Throws = false;

        SerializedEntity[] single = SaveSnapshot.Build(items, 1, out failure, out _);
        check(failure == null && Equal(single, sequential), "save snapshot: one worker gives the same result");
        SerializedEntity[] many = SaveSnapshot.Build(items, 64, out failure, out _);
        check(failure == null && Equal(many, sequential), "save snapshot: more workers than a machine has still give the same result");
        check(SaveSnapshot.Allowed.ContainsKey("Timberborn.BlockSystem.BlockObject") && SaveSnapshot.Allowed.Count >= 90,
            "save snapshot: the allowed list holds the audited components");
        // The hash guard (0.4.25): a listed type is used only while its Save is the one that was read. Harmony's registry
        // cannot run here, so the guard sees an empty one.
        SaveGuard.PatchesOn = _ => null;
        SaveGuard.PatchedMethods = () => new MethodBase[0];
        SaveGuard.PatchCount = () => 0;
        SaveGuard.ResetForTests();
        SaveSnapshot.ForgetVerdictsForTests();
        Program.LoadEveryGameAssembly();
        int listed = 0, found = 0, allowed = 0;
        foreach (KeyValuePair<string, string> entry in SaveSnapshot.Allowed)
        {
            listed++;
            Type type = Program.FindType(entry.Key);
            if (type == null)
            {
                Console.WriteLine("     not installed here: " + entry.Key);
                continue;
            }
            found++;
            if (SaveSnapshot.IsAllowed(type)) allowed++;
        }
        check(found == listed && allowed == found, $"save snapshot: every listed component's saving code is still the one that was read ({allowed} of {listed})");
        string hash = SaveSnapshot.SaveHash(typeof(Part));
        check(hash.Length == 16 && hash == SaveSnapshot.SaveHash(typeof(Part)) && SaveSnapshot.SaveHash(typeof(string)) == "none" &&
              hash != SaveSnapshot.SaveHash(Program.FindType("Timberborn.BlockSystem.BlockObject")),
            "save snapshot: a Save method hashes to 16 hex digits, the same every time, and a type without one to none");
        check(!SaveSnapshot.IsAllowed(typeof(string)) && !SaveSnapshot.IsAllowed(typeof(Part)), "save snapshot: a type not on the list stays on the main thread");
        SaveSnapshot.Allowed[typeof(Part).FullName] = "0000000000000000";
        SaveSnapshot.ForgetVerdictsForTests();
        bool refused = !SaveSnapshot.IsAllowed(typeof(Part));
        SaveSnapshot.Allowed[typeof(Part).FullName] = hash;
        bool stillRefused = !SaveSnapshot.IsAllowed(typeof(Part));   // the verdict is kept for the session
        SaveSnapshot.ForgetVerdictsForTests();
        bool allowedNow = SaveSnapshot.IsAllowed(typeof(Part));
        SaveSnapshot.Allowed.Remove(typeof(Part).FullName);
        SaveSnapshot.ForgetVerdictsForTests();
        check(refused && stillRefused && allowedNow, "save snapshot: a listed type whose Save changed is refused once per session, with a log line, and allowed again once its hash is renewed");

        // The patch guard (0.4.26): what a Save calls is read from its IL, and a patch by another mod on the Save, on a
        // method it calls, on a serializer it loads or on a shared helper keeps the type, or every entity, on the main thread.
        check(SaveGuard.ParkedReason == null, "save guard: the shared saving helpers are the ones that were read, and nothing patches them");
        MethodBase partSave = SaveGuard.SaveMethod(typeof(Part));
        SaveGuard.ReadForTests(partSave, out List<MethodBase> callees, out List<Type> serializers);
        MethodBase currentThread = typeof(Thread).GetProperty("CurrentThread").GetGetMethod();
        check(callees.Contains(currentThread) && callees.Any(m => m.Name == "GetComponent" && m.DeclaringType == typeof(IEntitySaver)) &&
              callees.Any(m => m.Name == "Set" && m.DeclaringType == typeof(IObjectSaver) && m.IsGenericMethod) &&
              callees.Any(m => m.Name == "Set" && m.DeclaringType == typeof(IObjectSaver) && !m.IsGenericMethod) &&
              callees.Any(m => m is ConstructorInfo && m.DeclaringType == typeof(InvalidOperationException)),
            $"save guard: the calls a Save makes are read from its IL, interface, generic and constructor calls included ({callees.Count} callees)");
        check(serializers.SequenceEqual(new[] { typeof(PairSerializer) }), "save guard: the serializer a Save loads from a field is found");
        int gameCallees = 0, gameSerializers = 0;
        foreach (KeyValuePair<string, string> entry in SaveSnapshot.Allowed)
        {
            Type type = Program.FindType(entry.Key);
            if (type == null) continue;
            SaveGuard.ReadForTests(SaveGuard.SaveMethod(type), out List<MethodBase> theirs, out List<Type> theirSerializers);
            gameCallees += theirs.Count;
            gameSerializers += theirSerializers.Count;
        }
        check(gameCallees > 200 && gameSerializers > 5, $"save guard: every listed Save's IL reads cleanly ({gameCallees} calls, {gameSerializers} serializers over the list)");
        SaveSnapshot.Allowed[typeof(Part).FullName] = hash;
        SaveGuard.PatchesOn = m => m == partSave ? new[] { ("some.other.mod", "Other.Mod.SavePatch.Postfix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool refusedForPatch = !SaveSnapshot.IsAllowed(typeof(Part));
        SaveGuard.ReviewedPatches.Add(("Part.Save", "some.other.mod", "Other.Mod.SavePatch.Postfix"));
        SaveSnapshot.ForgetVerdictsForTests();
        bool allowedReviewed = SaveSnapshot.IsAllowed(typeof(Part));
        SaveGuard.ReviewedPatches.RemoveAt(SaveGuard.ReviewedPatches.Count - 1);
        SaveGuard.PatchesOn = m => m == partSave ? new[] { (Plugin.HarmonyId, "LateGamePerformance.Something.Prefix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool allowedOwn = SaveSnapshot.IsAllowed(typeof(Part));
        SaveGuard.PatchesOn = m => m == currentThread ? new[] { ("some.other.mod", "Other.Mod.ThreadPatch.Prefix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool refusedCallee = !SaveSnapshot.IsAllowed(typeof(Part));
        MethodBase serialize = typeof(PairSerializer).GetMethod("Serialize");
        SaveGuard.PatchesOn = m => m == serialize ? new[] { ("some.other.mod", "Other.Mod.PairPatch.Postfix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool refusedSerializer = !SaveSnapshot.IsAllowed(typeof(Part));
        check(refusedForPatch && allowedReviewed && allowedOwn && refusedCallee && refusedSerializer,
            "save guard: another mod's patch on a Save, on a method it calls or on its serializer keeps the type on the main thread; a reviewed one or this mod's own does not");
        MethodBase saverConstructor = typeof(EntitySaver).GetConstructors()[0];
        SaveGuard.PatchesOn = m => m == saverConstructor ? new[] { ("some.other.mod", "Other.Mod.SaverPatch.Prefix") } : null;
        SaveGuard.PatchCount = () => 1;
        bool registryChanged = SaveGuard.RegistryChanged();
        string parked = SaveGuard.ParkedReason;
        check(registryChanged && parked != null && parked.Contains("EntitySaver..ctor") && parked.Contains("some.other.mod"),
            "save guard: another mod's patch on a shared saving helper leaves the whole save to the game, naming the patch");
        SaveGuard.PatchesOn = _ => null;
        SaveGuard.PatchCount = () => 2;
        check(SaveGuard.RegistryChanged() && SaveGuard.ParkedReason == null && !SaveGuard.RegistryChanged(),
            "save guard: when the number of patched methods changes everything is judged again; unchanged, nothing is");
        SaveGuard.PatchCount = () => throw new InvalidOperationException("no registry");
        check(SaveGuard.RegistryChanged() && SaveGuard.ParkedReason == null, "save guard: a registry that cannot be counted still lets the helpers be judged");
        SaveGuard.PatchCount = () => 0;
        SaveSnapshot.Allowed.Remove(typeof(Part).FullName);
        SaveSnapshot.ForgetVerdictsForTests();
        SaveGuard.ResetForTests();

        // Reached code (0.4.26 review): a patched implementation of an interface the Save calls, and a patch three
        // calls deep, are refused; four deep is beyond the bound, and this mod's own patch on an implementation is fine.
        SaveSnapshot.Allowed[typeof(Reaching).FullName] = SaveSnapshot.SaveHash(typeof(Reaching));
        MethodBase help = typeof(HelperImpl).GetMethod("Help");
        SaveGuard.PatchedMethods = () => new[] { help };
        SaveGuard.PatchesOn = m => m == help ? new[] { ("some.other.mod", "Other.Mod.HelpPatch.Prefix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool refusedImplementation = !SaveSnapshot.IsAllowed(typeof(Reaching));
        SaveGuard.PatchesOn = m => m == help ? new[] { (Plugin.HarmonyId, "LateGamePerformance.Something.Prefix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool allowedOwnImplementation = SaveSnapshot.IsAllowed(typeof(Reaching));
        SaveGuard.PatchedMethods = () => new MethodBase[0];
        MethodBase level3 = typeof(Chain).GetMethod("Level3"), level4 = typeof(Chain).GetMethod("Level4");
        SaveGuard.PatchesOn = m => m == level3 ? new[] { ("some.other.mod", "Other.Mod.DeepPatch.Prefix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool refusedDeep = !SaveSnapshot.IsAllowed(typeof(Reaching));
        SaveGuard.PatchesOn = m => m == level4 ? new[] { ("some.other.mod", "Other.Mod.DeeperPatch.Prefix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool beyondBound = SaveSnapshot.IsAllowed(typeof(Reaching));
        check(refusedImplementation && allowedOwnImplementation && refusedDeep && beyondBound,
            "save guard: a patched implementation of an interface a Save calls, or a patch three calls deep, keeps the type on the main thread; four deep is beyond the bound");
        SaveGuard.PatchesOn = _ => null;
        SaveSnapshot.Allowed.Remove(typeof(Reaching).FullName);
        SaveSnapshot.ForgetVerdictsForTests();
        SaveGuard.ResetForTests();
        // The deep comparison tells a changed value apart, which the game's reference comparison of lists cannot.
        SerializedEntity x = new SerializedEntity(Guid.Empty, "t"), y = new SerializedEntity(Guid.Empty, "t");
        new Part(7, "s").Save(new EntitySaver(x));
        new Part(8, "s").Save(new EntitySaver(y));
        SerializedEntity z = new SerializedEntity(Guid.Empty, "t");
        new Part(7, "s").Save(new EntitySaver(z));
        check(!SaveSnapshot.DeepEquals(x, y) && SaveSnapshot.DeepEquals(x, z) && !x.Equals(z),
            "save snapshot: the deep comparison tells different values apart and equal ones together, where the game's Equals does not");
    }

    private static bool Equal(SerializedEntity[] a, SerializedEntity[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if ((a[i] == null) != (b[i] == null)) return false;
            if (a[i] != null && !SaveSnapshot.DeepEquals(a[i], b[i])) return false;
        }
        return true;
    }

    private static List<SaveSnapshot.Item> MakeItems(Random random, int count, out List<Part> parts)
    {
        parts = new List<Part>();
        List<SaveSnapshot.Item> items = new List<SaveSnapshot.Item>();
        for (int i = 0; i < count; i++)
        {
            int partCount = 1 + random.Next(3);
            IPersistentEntity[] entityParts = new IPersistentEntity[partCount];
            for (int k = 0; k < partCount; k++)
            {
                Part part = new Part(random.Next(1000), "p" + k);
                parts.Add(part);
                entityParts[k] = part;
            }
            items.Add(new SaveSnapshot.Item
            {
                Id = new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), Template = "Template" + random.Next(20), Parts = entityParts,
                OnWorkers = random.Next(10) < 9
            });
        }
        return items;
    }

    private static SerializedEntity[] Sequential(List<SaveSnapshot.Item> items)
    {
        SerializedEntity[] results = new SerializedEntity[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            SerializedEntity entity = new SerializedEntity(items[i].Id, items[i].Template);
            foreach (IPersistentEntity part in items[i].Parts) part.Save(new EntitySaver(entity));
            results[i] = entity.HasComponents() ? entity : null;
        }
        return results;
    }
}
