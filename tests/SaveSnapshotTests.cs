using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using LateGamePerformance;
using Timberborn.EntitySystem;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
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
        SerializedEntity[] parallel = SaveSnapshot.Build(items, 7, out Exception failure, out SaveSnapshot.BuildTimes times);
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
        int eligibleCount = items.Count(item => item.OnWorkers);
        check(times.Workers > 0 && times.MainShare > 0 && times.Waiting >= 0 && times.TakenByMain >= 0 && times.TakenByMain <= eligibleCount,
            $"save snapshot: worker time, the main thread's share and its wait were measured (the main thread took {times.TakenByMain} of {eligibleCount} eligible entities)");

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

        // 0.4.28.
        RunConditional(check);
        RunAutomatable(check);
        RunJudgingPasses(check);
        RunSingletons(check);
        RunParallelTickWait(check);
        SaveGuard.PatchesOn = _ => null;
        SaveGuard.PatchedMethods = () => new MethodBase[0];
        SaveGuard.PatchCount = () => 0;
        SaveGuard.FetchPatchedMethodsEveryTimeForTests = false;
        SaveGuard.ResetForTests();
        SaveSnapshot.ResetForTests();
        ParallelTickWait.ResetForTests();
    }

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void NeutralRegistry()
    {
        SaveGuard.PatchesOn = _ => null;
        SaveGuard.PatchedMethods = () => new MethodBase[0];
        SaveGuard.PatchCount = () => 0;
        SaveGuard.FetchPatchedMethodsEveryTimeForTests = false;
        SaveGuard.ResetForTests();
    }

    private sealed class Switch
    {
        public bool IsOn { get; set; }
    }

    // A Save that writes, and would go somewhere unsafe, only in one state: the shape of the game's Automatable.
    private sealed class Switched : IPersistentEntity
    {
        private static readonly ComponentKey Key = new ComponentKey("Switched");
        private static readonly PropertyKey<int> Number = new PropertyKey<int>("Number");
        private readonly Switch _switch = new Switch();

        public bool On
        {
            set => _switch.IsOn = value;
        }

        public void Save(IEntitySaver entitySaver)
        {
            if (_switch.IsOn)
            {
                entitySaver.GetComponent(Key).Set(Number, 1);
            }
        }

        public void Load(IEntityLoader entityLoader)
        {
        }
    }

    // A listed type allowed only in some states: the state is read per entity, by the expression its Save branches on.
    private static void RunConditional(Action<bool, string> check)
    {
        NeutralRegistry();
        SaveSnapshot.ResetForTests();
        string name = typeof(Switched).FullName;
        SaveSnapshot.Allowed[name] = SaveSnapshot.SaveHash(typeof(Switched));
        SaveSnapshot.Conditional[name] = ("switched on", type => SaveSnapshot.Getter(type, "_switch", "IsOn"));
        Switched off = new Switched(), on = new Switched { On = true };
        bool offGoes = SaveSnapshot.MayGoToWorker(off), onStays = !SaveSnapshot.MayGoToWorker(on);
        check(offGoes && onStays && SaveSnapshot.IsAllowed(typeof(Switched)),
            "save snapshot: a conditional type goes to a worker only in the state its Save was read as safe in, checked per entity");
        SaveSnapshot.Conditional[name] = ("switched on", type => SaveSnapshot.Getter(type, "_missing", "IsOn"));
        SaveSnapshot.ForgetVerdictsForTests();
        bool refused = !SaveSnapshot.IsAllowed(typeof(Switched)) && !SaveSnapshot.MayGoToWorker(off);
        SaveSnapshot.Conditional[name] = ("switched on", type => SaveSnapshot.Getter(type, "_switch", "Missing"));
        SaveSnapshot.ForgetVerdictsForTests();
        refused &= !SaveSnapshot.IsAllowed(typeof(Switched));
        check(refused, "save snapshot: a conditional type whose check cannot be built (field or property gone) stays on the main thread");
        SaveSnapshot.Conditional.Remove(name);
        SaveSnapshot.Allowed.Remove(name);
        SaveSnapshot.ResetForTests();
    }

    // The game's own Automatable and WorkshopRandomNeedApplier (0.4.28).
    private static void RunAutomatable(Action<bool, string> check)
    {
        NeutralRegistry();
        SaveSnapshot.ResetForTests();
        SaveSnapshot.Activate();
        Type automatableType = Program.FindType("Timberborn.Automation.Automatable");
        Type connectionType = Program.FindType("Timberborn.Automation.AutomatorConnection");
        Type automatorType = Program.FindType("Timberborn.Automation.Automator");
        object automatable = RuntimeHelpers.GetUninitializedObject(automatableType);
        object connection = RuntimeHelpers.GetUninitializedObject(connectionType);
        automatableType.GetField("_inputConnection", Any).SetValue(automatable, connection);
        PropertyInfo isAutomated = automatableType.GetProperty("IsAutomated");
        bool unconnectedGoes = SaveSnapshot.MayGoToWorker(automatable) && !(bool)isAutomated.GetValue(automatable);
        SerializedEntity written = new SerializedEntity(Guid.NewGuid(), "Lodge");
        Exception onWorker = null;
        Thread worker = new Thread(() =>
        {
            try
            {
                ((IPersistentEntity)automatable).Save(new EntitySaver(written));
            }
            catch (Exception exception)
            {
                onWorker = exception;
            }
        });
        worker.Start();
        worker.Join();
        connectionType.GetField("<Transmitter>k__BackingField", Any).SetValue(connection, RuntimeHelpers.GetUninitializedObject(automatorType));
        bool connectedStays = !SaveSnapshot.MayGoToWorker(automatable) && (bool)isAutomated.GetValue(automatable);
        string line = SaveSnapshot.TakeUnlistedLine();
        check(unconnectedGoes && connectedStays && onWorker == null && !written.HasComponents() && line != null &&
              line.Contains("Timberborn.Automation.Automatable (automation input connected) (1)"),
            "save snapshot: the game's Automatable goes to a worker only while its input is not connected (its Save then writes nothing, on a worker too); a connected one stays and is named in the session's line");

        // What SaveGuard follows from them: Automatable's Save reaches the connection's IsConnected, a reference compare
        // (its only call is the Transmitter getter, nothing of Unity's), and the reference serializer.
        SaveGuard.ReadForTests(SaveGuard.SaveMethod(automatableType), out List<MethodBase> reached, out _);
        MethodBase isConnected = connectionType.GetProperty("IsConnected").GetGetMethod(true);
        SaveGuard.ReadForTests(isConnected, out List<MethodBase> connectionCalls, out _);
        check(reached.Contains(isConnected) && reached.Any(m => m.Name == "Of" && m.DeclaringType?.Name == "ReferenceSerializer") &&
              connectionCalls.Count == 1 && connectionCalls[0].Name == "get_Transmitter",
            "save guard: Automatable's Save reaches IsConnected (only the Transmitter getter behind it) and the reference serializer");
        SaveGuard.PatchesOn = m => m == isConnected ? new[] { ("some.other.mod", "Other.Mod.ConnectionPatch.Postfix") } : null;
        SaveSnapshot.ForgetVerdictsForTests();
        bool automatableRefused = !SaveSnapshot.IsAllowed(automatableType);
        Type applierType = Program.FindType("Timberborn.NeedApplication.WorkshopRandomNeedApplier");
        MethodBase progress = Program.FindType("Timberborn.TimeSystem.TimeTrigger").GetProperty("Progress", Any).GetGetMethod(true);
        NeutralRegistry();
        SaveSnapshot.ForgetVerdictsForTests();
        bool applierAllowed = SaveSnapshot.IsAllowed(applierType);
        SaveGuard.PatchedMethods = () => new[] { progress };
        SaveGuard.PatchesOn = m => m == progress ? new[] { ("some.other.mod", "Other.Mod.TriggerPatch.Postfix") } : null;
        SaveGuard.RegistryChanged();
        SaveSnapshot.ForgetVerdictsForTests();
        bool applierRefused = !SaveSnapshot.IsAllowed(applierType);
        check(automatableRefused && applierAllowed && applierRefused,
            "save guard: another mod's patch on what Automatable's or WorkshopRandomNeedApplier's Save reaches (the connection's getter; TimeTrigger.Progress behind the interface) keeps them on the main thread");
        NeutralRegistry();
        SaveSnapshot.ResetForTests();
    }

    // A registry like the one in the game: every method of a few of the game's assemblies the listed Saves reach into
    // counts as patched, some by another mod (TimeTrigger.Progress among them, which several listed Saves reach
    // through an interface).
    private static List<MethodBase> PatchedForTests(out Func<MethodBase, IEnumerable<(string Owner, string Patch)>> patchesOn)
    {
        List<MethodBase> patched = new List<MethodBase>();
        foreach (string typeName in new[]
                 {
                     "Timberborn.TimeSystem.TimeTrigger", "Timberborn.TimeSystem.DayNightCycle", "Timberborn.InventorySystem.Inventory",
                     "Timberborn.Automation.AutomatorConnection", "Timberborn.Automation.Automator", "Timberborn.WorkSystem.Workplace",
                     "Timberborn.Growing.Growable", "Timberborn.BlockSystem.BlockObject"
                 })
        {
            Type type = Program.FindType(typeName);
            patched.AddRange(type.GetMethods(Any | BindingFlags.DeclaredOnly));
        }
        patched.Add(typeof(HelperImpl).GetMethod("Help"));
        HashSet<MethodBase> foreign = new HashSet<MethodBase>(patched.Where((m, i) => i % 5 == 0 || m.Name == "get_Progress" || m.Name == "Help"));
        patchesOn = m => foreign.Contains(m) ? new[] { ("some.other.mod", "Other.Mod.Patch.Postfix") } : null;
        return patched;
    }

    // SaveGuard's list of patched methods is taken once per judging pass (0.4.28): every verdict, and every message, is
    // what walking Harmony's whole list at every reached virtual method gave before; and every listed type judged ahead
    // of the first save is judged as the save would have judged it.
    private static void RunJudgingPasses(Action<bool, string> check)
    {
        List<MethodBase> patched = PatchedForTests(out Func<MethodBase, IEnumerable<(string Owner, string Patch)>> patchesOn);
        Dictionary<string, string> Refusals(bool everyTime, out int fetches, out double ms)
        {
            int count = 0;
            SaveGuard.PatchesOn = patchesOn;
            SaveGuard.PatchedMethods = () =>
            {
                count++;
                return patched;
            };
            SaveGuard.PatchCount = () => 0;
            SaveGuard.FetchPatchedMethodsEveryTimeForTests = everyTime;
            SaveGuard.ResetForTests();
            SaveGuard.RegistryChanged();
            Stopwatch clock = Stopwatch.StartNew();
            Dictionary<string, string> result = new Dictionary<string, string>();
            foreach (string name in SaveSnapshot.Allowed.Keys.Concat(new[] { typeof(Reaching).FullName }))
            {
                Type type = name == typeof(Reaching).FullName ? typeof(Reaching) : Program.FindType(name);
                if (type != null)
                {
                    result[name] = SaveGuard.Refusal(type) ?? "allowed";
                }
            }
            ms = clock.Elapsed.TotalMilliseconds;
            fetches = count;
            return result;
        }
        Dictionary<string, string> before = Refusals(true, out int fetchesBefore, out double msBefore);
        Dictionary<string, string> now = Refusals(false, out int fetchesNow, out double msNow);
        int refusedCount = now.Values.Count(v => v != "allowed");
        check(before.Count == now.Count && before.All(entry => now[entry.Key] == entry.Value) && refusedCount > 0 && refusedCount < now.Count &&
              fetchesNow == 1 && fetchesBefore > 1,
            $"save guard: with the patched methods taken once per pass ({fetchesNow} fetch, {msNow:0} ms) every verdict and message over {now.Count} types " +
            $"is the one fetching at every reached virtual method gave ({fetchesBefore} fetches, {msBefore:0} ms); {refusedCount} refused");
        SaveGuard.FetchPatchedMethodsEveryTimeForTests = false;

        // Judged ahead at the first tick of a scene, then the first save: the same verdicts as judging at the save, and
        // the save judges nothing again while the number of patches is unchanged.
        SaveSnapshot.ResetForTests();
        SaveGuard.ResetForTests();
        SaveSnapshot.Activate();
        SaveSnapshot.SceneCreated();
        MethodInfo tickPrefix = typeof(SaveSnapshot).GetMethod("TickAllPrefix", BindingFlags.NonPublic | BindingFlags.Static);
        int asked = 0;
        SaveGuard.PatchesOn = m =>
        {
            asked++;
            return patchesOn(m);
        };
        tickPrefix.Invoke(null, null);
        int askedAhead = asked;
        tickPrefix.Invoke(null, null);
        bool secondTickQuiet = asked == askedAhead;
        Dictionary<string, bool> ahead = new Dictionary<string, bool>();
        foreach (string name in SaveSnapshot.Allowed.Keys)
        {
            Type type = Program.FindType(name);
            if (type != null)
            {
                ahead[name] = SaveSnapshot.IsAllowed(type);
            }
        }
        bool saveQuiet = !SaveGuard.RegistryChanged() && asked == askedAhead;
        SaveSnapshot.ForgetVerdictsForTests();
        SaveGuard.ResetForTests();
        SaveGuard.RegistryChanged();
        bool same = ahead.All(entry => SaveSnapshot.IsAllowed(Program.FindType(entry.Key)) == entry.Value);
        bool found = SaveSnapshot.Allowed.Keys.All(name => SaveSnapshot.FindListedType(name) == Program.FindType(name));
        check(askedAhead > 0 && secondTickQuiet && saveQuiet && same && ahead.Count(entry => !entry.Value) == refusedCount - (now[typeof(Reaching).FullName] != "allowed" ? 1 : 0) && found,
            $"save snapshot: the first tick of a scene judges all {ahead.Count} listed types ahead of the first save, with the verdicts the save would have reached, and nothing is judged again while the patches are unchanged");
        SaveGuard.PatchCount = () => 1;
        int beforeChange = asked;
        SaveSnapshot.SceneCreated();
        tickPrefix.Invoke(null, null);
        check(asked > beforeChange, "save snapshot: after the number of patches changed, the next scene's first tick judges again");
        NeutralRegistry();
        SaveSnapshot.ResetForTests();
    }

    private sealed class Repository : ISingletonRepository
    {
        public readonly List<object> Singletons = new List<object>();

        public IEnumerable<T> GetSingletons<T>()
        {
            return Singletons.OfType<T>();
        }
    }

    private static readonly List<string> SingletonCalls = new List<string>();

    private abstract class Recorded : ISaveableSingleton
    {
        private readonly string _key;
        private readonly int _value;
        private readonly int _sleepMilliseconds;
        public bool Throws;

        protected Recorded(string key, int value, int sleepMilliseconds)
        {
            _key = key;
            _value = value;
            _sleepMilliseconds = sleepMilliseconds;
        }

        public void Save(ISingletonSaver singletonSaver)
        {
            SingletonCalls.Add(_key);
            if (Throws)
            {
                throw new InvalidOperationException("singleton refuses");
            }
            if (_sleepMilliseconds > 0)
            {
                Thread.Sleep(_sleepMilliseconds);
            }
            IObjectSaver saver = singletonSaver.GetSingleton(new SingletonKey(_key));
            saver.Set(new PropertyKey<int>("Value"), _value);
            saver.Set(new PropertyKey<string>("Text"), "value " + _value);
        }
    }

    private sealed class QuickSingleton : Recorded
    {
        public QuickSingleton() : base("Quick", 1, 0)
        {
        }
    }

    private sealed class SlowSingleton : Recorded
    {
        public SlowSingleton() : base("Slow", 2, 40)
        {
        }
    }

    private sealed class MiddleSingleton : Recorded
    {
        public MiddleSingleton() : base("Middle", 3, 10)
        {
        }
    }

    private static bool SameSingletons(SerializedWorld a, SerializedWorld b)
    {
        List<SerializedSingleton> x = a.Singletons().ToList(), y = b.Singletons().ToList();
        if (x.Count != y.Count)
        {
            return false;
        }
        for (int i = 0; i < x.Count; i++)
        {
            if (x[i].Name != y[i].Name || !SaveSnapshot.DeepEquals(x[i].Value, y[i].Value))
            {
                return false;
            }
        }
        return true;
    }

    // SaveSingletons written out to time each singleton, against the game's own SerializedWorldFactory, SingletonSaver
    // and SaveSingletons; and the whole prefix on a factory with no entities, for the stats line.
    private static void RunSingletons(Action<bool, string> check)
    {
        NeutralRegistry();
        SaveSnapshot.ResetForTests();
        SaveSnapshot.CreateFeature(new Config()).Patches[0].Target();
        Repository repository = new Repository();
        QuickSingleton quick = new QuickSingleton();
        SlowSingleton slow = new SlowSingleton();
        repository.Singletons.AddRange(new object[] { quick, "not a singleton", slow, new MiddleSingleton() });
        SerializedWorldFactory factory = new SerializedWorldFactory(new TemplateNameRetriever(), new EntityRegistry(), repository);
        Timberborn.Versioning.Version version = Timberborn.Versioning.Version.Create("1.1.2.4");
        MethodInfo gameLoop = typeof(SerializedWorldFactory).GetMethod("SaveSingletons", Any, null, new[] { typeof(SerializedWorld) }, null);
        SerializedWorld games = new SerializedWorld(version);
        SingletonCalls.Clear();
        gameLoop.Invoke(factory, new object[] { games });
        List<string> gameCalls = new List<string>(SingletonCalls);
        SerializedWorld ours = new SerializedWorld(version);
        SingletonCalls.Clear();
        SaveSnapshot.SaveSingletons(factory, ours);
        check(gameCalls.SequenceEqual(new[] { "Quick", "Slow", "Middle" }) && SingletonCalls.SequenceEqual(gameCalls) && SameSingletons(games, ours) &&
              SaveSnapshot.SingletonLoopHashNow() == SaveSnapshot.SingletonLoopHash,
            "save snapshot: the singletons, written out here to be timed, are saved by the same calls in the same order into the same world as by the game's SaveSingletons, which is the one that was read");

        // The whole prefix, on a factory with no entities: the snapshot is the one used and the stats line splits it.
        SaveSnapshot.ResetForTests();
        SaveSnapshot.Activate();
        SerializedWorld result = null;
        SingletonCalls.Clear();
        bool gameRuns = SaveSnapshot.CreatePrefix(factory, ref result);
        string line = SaveSnapshot.TakeStatsLine();
        Console.WriteLine("     " + line);
        int slowAt = line?.IndexOf("SlowSingleton ", StringComparison.Ordinal) ?? -1;
        int middleAt = line?.IndexOf("MiddleSingleton ", StringComparison.Ordinal) ?? -1;
        int quickAt = line?.IndexOf("QuickSingleton ", StringComparison.Ordinal) ?? -1;
        check(!gameRuns && result != null && SameSingletons(games, result) && SingletonCalls.SequenceEqual(gameCalls) &&
              line.StartsWith("SaveSnapshot: 1 snapshot(s); per snapshot 0 entities on worker threads and 0 on the main thread; the main thread spent ") &&
              line.Contains(" ms per snapshot: collecting ") && line.Contains(", its share of the entities ") && line.Contains(", waiting for the workers ") &&
              line.Contains(", assembling ") && line.Contains(", singletons ") && line.Contains("; slowest singletons per snapshot: SlowSingleton ") &&
              slowAt >= 0 && slowAt < middleAt && middleAt < quickAt,
            "save snapshot: the prefix's snapshot is used, and the stats line splits the main thread's time and names the slowest singletons, slowest first");

        // Another mod patches SaveSingletons: the game's own method saves them, timed as a whole.
        MethodBase two = typeof(SerializedWorldFactory).GetMethod("SaveSingletons", Any, null,
            new[] { typeof(SerializedWorld), typeof(IEnumerable<ISaveableSingleton>) }, null);
        SaveGuard.PatchesOn = m => m == two ? new[] { ("some.other.mod", "Other.Mod.SingletonsPatch.Prefix") } : null;
        SaveGuard.PatchCount = () => 5;
        SaveSnapshot.ResetForTests();
        SaveSnapshot.Activate();
        result = null;
        SingletonCalls.Clear();
        gameRuns = SaveSnapshot.CreatePrefix(factory, ref result);
        line = SaveSnapshot.TakeStatsLine();
        check(!gameRuns && result != null && SameSingletons(games, result) && SingletonCalls.SequenceEqual(gameCalls) && line != null &&
              !line.Contains("slowest singletons"),
            "save snapshot: when another mod patches SaveSingletons, the game's own method saves the singletons, timed as a whole");

        // A singleton whose Save throws: the game's Create takes over (and throws the same), the line names it.
        NeutralRegistry();
        SaveSnapshot.ResetForTests();
        SaveSnapshot.Activate();
        slow.Throws = true;
        result = null;
        SingletonCalls.Clear();
        gameRuns = SaveSnapshot.CreatePrefix(factory, ref result);
        slow.Throws = false;
        check(gameRuns && result == null && !SaveSnapshot.IsActive && SingletonCalls.SequenceEqual(new[] { "Quick", "Slow" }),
            "save snapshot: a singleton that throws hands the save to the game's own Create and turns the feature off, naming the singleton");
        NeutralRegistry();
        SaveSnapshot.ResetForTests();
    }

    // The wait for the game's parallel tick at the start of a snapshot (0.4.28), against the game's own
    // TickableSingletonService and Parallelizer types (their fields as the game declares them; the Parallelizer's
    // threads need Unity, so its pending count and failure queue are set here).
    private static void RunParallelTickWait(Action<bool, string> check)
    {
        Type serviceType = Program.FindType("Timberborn.TickSystem.TickableSingletonService");
        Type parallelizerType = Program.FindType("Timberborn.Multithreading.Parallelizer");
        FieldInfo pending = parallelizerType.GetField("_pendingTasks", Any);
        FieldInfo failures = parallelizerType.GetField("_exceptions", Any);
        object parallelizer = RuntimeHelpers.GetUninitializedObject(parallelizerType);
        object queue = Activator.CreateInstance(failures.FieldType);
        failures.SetValue(parallelizer, queue);
        object service = RuntimeHelpers.GetUninitializedObject(serviceType);
        serviceType.GetField("_parallelizer", Any).SetValue(service, parallelizer);

        ParallelTickWait.ResetForTests();
        Feature feature = ParallelTickWait.CreateFeature();
        feature.Patches[0].Target();
        ParallelTickWait.Activate();
        ParallelTickWait.CreatePrefix();
        string without = ParallelTickWait.TakeStatsLine();
        ParallelTickWait.LoadPostfix(service);
        pending.SetValue(parallelizer, 0);
        Stopwatch clock = Stopwatch.StartNew();
        ParallelTickWait.CreatePrefix();
        double idleMs = clock.Elapsed.TotalMilliseconds;
        pending.SetValue(parallelizer, 2);
        Thread tasks = new Thread(() =>
        {
            Thread.Sleep(40);
            pending.SetValue(parallelizer, 1);
            Thread.Sleep(5);
            pending.SetValue(parallelizer, 0);
        });
        clock.Restart();
        tasks.Start();
        ParallelTickWait.CreatePrefix();
        double waitedMs = clock.Elapsed.TotalMilliseconds;
        tasks.Join();
        string line = ParallelTickWait.TakeStatsLine();
        Console.WriteLine("     " + line);
        check(without != null && without.Contains("1 without the game's parallelizer at hand") && idleMs < 20 && waitedMs >= 40 &&
              (int)pending.GetValue(parallelizer) == 0 && line != null &&
              line.StartsWith("ParallelTickWait: 2 snapshot(s); the game's parallel tick was still running at the start of 1 of them, waited "),
            $"parallel tick wait: nothing to wait for costs nothing ({idleMs:0.0} ms); tasks still running are waited for until the game's pending count is zero ({waitedMs:0} ms)");

        // A task failed: stop waiting at once and leave the failure in the game's queue for its next tick to report.
        pending.SetValue(parallelizer, 1);
        object failure = Activator.CreateInstance(failures.FieldType.GetGenericArguments()[0], new InvalidOperationException("task failed"), "Parallelizer-0");
        failures.FieldType.GetMethod("Enqueue").Invoke(queue, new[] { failure });
        clock.Restart();
        ParallelTickWait.CreatePrefix();
        double failedMs = clock.Elapsed.TotalMilliseconds;
        bool leftInQueue = !(bool)failures.FieldType.GetProperty("IsEmpty").GetValue(queue);
        // A parallel tick that never ends: given up on after the limit.
        failures.SetValue(parallelizer, Activator.CreateInstance(failures.FieldType));
        ParallelTickWait.GiveUpAfterSeconds = 0.05;
        clock.Restart();
        ParallelTickWait.CreatePrefix();
        double gaveUpMs = clock.Elapsed.TotalMilliseconds;
        line = ParallelTickWait.TakeStatsLine();
        check(failedMs < 1000 && leftInQueue && (int)pending.GetValue(parallelizer) == 1 && gaveUpMs >= 45 && gaveUpMs < 2000 &&
              ParallelTickWait.IsActive && line.Contains("stopped waiting 1 time(s) because a task had failed and 1 time(s) after"),
            $"parallel tick wait: a failed task ends the wait at once and stays in the game's queue ({failedMs:0} ms); a tick that never ends is given up on after the limit ({gaveUpMs:0} ms); the save goes on either way");
        // A parallelizer whose scene unloaded (its threads closed) is not waited for.
        parallelizerType.GetField("_closed", Any).SetValue(parallelizer, true);
        clock.Restart();
        ParallelTickWait.CreatePrefix();
        double closedMs = clock.Elapsed.TotalMilliseconds;
        line = ParallelTickWait.TakeStatsLine();
        check(closedMs < 20 && line.Contains("1 without the game's parallelizer at hand"),
            "parallel tick wait: the parallelizer of a scene that has unloaded is not waited for");

        // Harmony's order on SerializedWorldFactory.Create: the wait first, SaveTiming's stage timer, SaveSnapshot last.
        MethodInfo waitPrefix = typeof(ParallelTickWait).GetMethod("CreatePrefix", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo stagePrefix = typeof(SaveTiming).GetMethod("StagePrefix", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo snapshotPrefix = typeof(SaveSnapshot).GetMethod("CreatePrefix", BindingFlags.NonPublic | BindingFlags.Static);
        List<MethodInfo> order = HarmonyLib.PatchProcessor.GetSortedPatchMethods(feature.Patches[1].Target(), new[]
        {
            new HarmonyLib.Patch(new HarmonyLib.HarmonyMethod(snapshotPrefix), 0, Plugin.HarmonyId + ".SaveSnapshot"),
            new HarmonyLib.Patch(new HarmonyLib.HarmonyMethod(stagePrefix), 1, Plugin.HarmonyId + ".SaveTiming"),
            new HarmonyLib.Patch(new HarmonyLib.HarmonyMethod(waitPrefix), 2, Plugin.HarmonyId + ".ParallelTickWait")
        });
        check(order.Count == 3 && order[0] == waitPrefix && order[1] == stagePrefix && order[2] == snapshotPrefix,
            "parallel tick wait: on SerializedWorldFactory.Create it runs before SaveTiming's stage timer and SaveSnapshot's replacing prefix, whatever the load order");
        ParallelTickWait.ResetForTests();
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
