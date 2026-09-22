using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Timberborn.EntitySystem;
using Timberborn.SerializationSystem;
using Timberborn.Persistence;
using Timberborn.TemplateSystem;
using Timberborn.Versioning;
using Timberborn.WorldPersistence;
using Timberborn.WorldSerialization;

namespace LateGamePerformance
{
    // The snapshot a save starts with, taken on worker threads where that is safe.
    //
    // Every save begins with SerializedWorldFactory.Create: for each of the colony's entities (11,000 in the
    // logged colony) every persistent component writes its state into a tree of dictionaries. That is the part
    // of a save still on the main thread since 0.4.13 moved the JSON, compression and file write off it: 200 to
    // 285 ms per autosave in the logged colony. Nearly all of those entities are trees, crops, paths, levees and
    // platforms, whose persistent components only read their own managed fields (growth progress, coordinates,
    // a yield, an inventory's goods) and write into their own entity's dictionaries; nothing they touch is
    // shared or belongs to Unity. Those entities are snapshotted on worker threads. Everything else (beavers,
    // whose Character reads a transform; workplaces, whose Worker uses the reference serializer's cache; any
    // component this mod has not read, including other mods') stays on the main thread, which does its share
    // while the workers do theirs. The entities are then assembled in the game's own order, and the singletons
    // are saved by the game's own code afterwards, so the save is the one the game would have written.
    //
    // The list of components allowed on workers is exactly the ones whose Save this mod has read (see
    // TECHNICAL.md). An entity is eligible only if every persistent component on it is on that list. If a worker
    // throws for any reason, everything is discarded and the game's own Create runs; the feature is then off for
    // the session. SaveSnapshotVerify also runs the game's own snapshot and compares every entity, and uses the
    // game's. Not part of the simulation: what is saved does not change, only which thread writes it down.
    internal static class SaveSnapshot
    {
        private const string FactoryType = "Timberborn.WorldPersistence.SerializedWorldFactory";

        // Persistent components whose Save reads only managed state of their own entity or of stateless
        // serializers. Read in the game's 1.1.2.4 source; a type not here keeps its entity on the main thread.
        internal static readonly HashSet<string> Allowed = new HashSet<string>
        {
            "Timberborn.BlockSystem.BlockObject",
            "Timberborn.BlockSystem.BlockObjectState",
            "Timberborn.Yielding.Yielder",
            "Timberborn.Growing.Growable",
            "Timberborn.NaturalResourcesLifecycle.LivingNaturalResource",
            "Timberborn.NaturalResources.CoordinatesOffsetter",
            "Timberborn.NaturalResourcesMoisture.LivingWaterNaturalResource",
            "Timberborn.NaturalResourcesMoisture.WateredNaturalResource",
            "Timberborn.NaturalResourcesMoisture.AridNaturalResource",
            "Timberborn.NaturalResourcesContamination.ContaminatedNaturalResource",
            "Timberborn.Gathering.GatherableYieldGrower",
            "Timberborn.Cutting.DeadCuttableYieldRemover",
            "Timberborn.ConstructionSites.ConstructionSite",
            "Timberborn.Demolishing.Demolishable",
            "Timberborn.BuilderPrioritySystem.BuilderPrioritizable",
            "Timberborn.Pollination.Pollinatee",
            "Timberborn.BlockObstacles.LayeredBlockObstacle",
            "Timberborn.DecalSystem.FlippableDecal",
            "Timberborn.DecalSystem.DecalSupplier",
            "Timberborn.Hauling.HaulPrioritizable",
            "Timberborn.Buildings.PausableBuilding",
            "Timberborn.Emptying.Emptiable",
            "Timberborn.ActivatorSystem.TimedComponentActivator",
            "Timberborn.EntityNaming.NamedEntity",
            "Timberborn.InventorySystem.Inventory",
            "Timberborn.Ruins.RuinModels",
            "Timberborn.MapEditorPlacementRandomizing.BlockObjectPlacementRandomizer"
        };

        // One entity's share of the snapshot: what the game does per entity in SaveEntities/SaveEntity.
        internal sealed class Item
        {
            public Guid Id;
            public string Template;
            public IPersistentEntity[] Parts;
            public bool OnWorkers;
        }

        private static Func<object, TemplateNameRetriever> _retriever;
        private static Func<object, EntityRegistry> _registry;
        private static Action<object, SerializedWorld> _saveSingletons;
        private static Action<object, SerializedWorld> _saveEntities;

        // Swappable for the test harness (the game's version property reads Unity).
        internal static Func<int> Workers = () => Math.Max(1, RouteMaps.WorkerCount);
        internal static Func<Timberborn.Versioning.Version> CurrentVersion = () => GameVersions.CurrentVersion;

        private static bool _active;
        private static bool _verify;
        private static long _saves;
        private static long _onWorkers;
        private static long _onMain;
        private static long _mainStopwatchTicks;
        private static long _workerStopwatchTicks;
        private static long _verifyMismatches;
        private static readonly Dictionary<string, int> Unlisted = new Dictionary<string, int>();
        private static bool _unlistedLogged;

        public static Feature CreateFeature(Config config)
        {
            _verify = config.SaveSnapshotVerify;
            Feature feature = new Feature { Name = "SaveSnapshot" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "SerializedWorldFactory.Create",
                Required = true,
                Target = () =>
                {
                    Type factory = Reflect.GameType(FactoryType);
                    _retriever = Reflect.FieldGetter<TemplateNameRetriever>(factory, "_templateNameRetriever");
                    _registry = Reflect.FieldGetter<EntityRegistry>(factory, "_entityRegistry");
                    _saveSingletons = Reflect.InstanceCall<Action<object, SerializedWorld>>(Reflect.Overload(FactoryType, "SaveSingletons", 1));
                    _saveEntities = Reflect.InstanceCall<Action<object, SerializedWorld>>(Reflect.Overload(FactoryType, "SaveEntities", 1));
                    return Reflect.Overload(FactoryType, "Create", 0);
                },
                Prefix = Reflect.Own(typeof(SaveSnapshot), nameof(CreatePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        internal static bool IsActive => _active;

        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _saves == 0)
            {
                return null;
            }
            double mainMs = _mainStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            double workerMs = _workerStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = $"SaveSnapshot: {_saves} snapshot(s); per snapshot {_onWorkers / _saves} entities on worker threads and " +
                          $"{_onMain / _saves} on the main thread; the main thread spent {mainMs / _saves:0} ms per snapshot " +
                          $"(workers {workerMs / _saves:0} ms alongside)" +
                          (_verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _saves = _onWorkers = _onMain = _mainStopwatchTicks = _workerStopwatchTicks = _verifyMismatches = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        internal static bool CreatePrefix(object __instance, ref SerializedWorld __result)
        {
            if (!_active)
            {
                return true;
            }
            long stamp = Stopwatch.GetTimestamp();
            List<Item> items;
            try
            {
                items = Collect(_retriever(__instance), _registry(__instance));
            }
            catch (Exception exception)
            {
                // Including the game's own ArgumentException for an entity with an ISaveableSingleton: the
                // original throws it too.
                Fail(exception);
                return true;
            }
            SerializedEntity[] entities = Build(items, Workers(), out Exception failure, out long workerTicks);
            if (failure != null)
            {
                Fail(failure);
                return true;
            }
            SerializedWorld world;
            try
            {
                world = Assemble(items, entities);
                _saveSingletons(__instance, world);
            }
            catch (Exception exception)
            {
                Fail(exception);
                return true;
            }
            _mainStopwatchTicks += Stopwatch.GetTimestamp() - stamp;
            _workerStopwatchTicks += workerTicks;
            _saves++;
            if (_verify)
            {
                world = Verify(__instance, world, items, entities) ?? world;
            }
            __result = world;
            return false;
        }
        // ReSharper restore InconsistentNaming

        // The main-thread pass the game makes anyway, minus the writing: template names (a Unity name lookup,
        // main thread only), the persistent components, and whether every one of them is on the list.
        private static List<Item> Collect(TemplateNameRetriever retriever, EntityRegistry registry)
        {
            List<Item> items = new List<Item>(registry.Entities.Count);
            List<IPersistentEntity> parts = new List<IPersistentEntity>();
            foreach (EntityComponent entity in registry.Entities)
            {
                string template = retriever.GetTemplateName(entity);
                parts.Clear();
                bool onWorkers = true;
                foreach (object component in entity.AllComponents)
                {
                    if (component is ISaveableSingleton)
                    {
                        throw new ArgumentException("An entity must not implement ISaveableSingleton: " + component.GetType().Name);
                    }
                    if (component is IPersistentEntity persistent)
                    {
                        parts.Add(persistent);
                        if (onWorkers && !Allowed.Contains(component.GetType().FullName))
                        {
                            onWorkers = false;
                            Note(component.GetType().FullName);
                        }
                    }
                }
                items.Add(new Item
                {
                    Id = entity.GetComponent<EntityComponent>().EntityId, Template = template, Parts = parts.ToArray(),
                    OnWorkers = onWorkers && parts.Count > 0
                });
            }
            return items;
        }

        // Every item's SerializedEntity (null where nothing was written), workers over the eligible ones in fixed
        // strides while the main thread does the rest, then a join. Never throws: a failure comes back instead.
        internal static SerializedEntity[] Build(List<Item> items, int workers, out Exception failure, out long workerTicks)
        {
            SerializedEntity[] results = new SerializedEntity[items.Count];
            List<int> eligible = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].OnWorkers)
                {
                    eligible.Add(i);
                }
            }
            Exception firstFailure = null;
            long ticks = 0;
            int count = Math.Min(Math.Max(1, workers), Math.Max(1, eligible.Count));
            Task[] tasks = new Task[eligible.Count > 0 ? count : 0];
            for (int w = 0; w < tasks.Length; w++)
            {
                int worker = w;
                tasks[w] = Task.Run(() =>
                {
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        for (int k = worker; k < eligible.Count; k += count)
                        {
                            int index = eligible[k];
                            results[index] = Snapshot(items[index]);
                        }
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref firstFailure, exception, null);
                    }
                    Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - started);
                });
            }
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (!items[i].OnWorkers)
                    {
                        results[i] = Snapshot(items[i]);
                    }
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref firstFailure, exception, null);
            }
            try
            {
                Task.WaitAll(tasks);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref firstFailure, exception, null);
            }
            failure = firstFailure;
            workerTicks = ticks;
            _onWorkers += eligible.Count;
            _onMain += items.Count - eligible.Count;
            return failure == null ? results : null;
        }

        // SerializedWorldFactory.SaveEntity, for one entity.
        private static SerializedEntity Snapshot(Item item)
        {
            SerializedEntity entity = new SerializedEntity(item.Id, item.Template);
            IPersistentEntity[] parts = item.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i].Save(new EntitySaver(entity));
            }
            return entity.HasComponents() ? entity : null;
        }

        internal static SerializedWorld Assemble(List<Item> items, SerializedEntity[] entities)
        {
            SerializedWorld world = new SerializedWorld(CurrentVersion());
            for (int i = 0; i < items.Count; i++)
            {
                if (entities[i] != null)
                {
                    world.AddEntity(entities[i]);
                }
            }
            return world;
        }

        // The game's own snapshot as well, compared entity by entity; the game's is the one used.
        private static SerializedWorld Verify(object factory, SerializedWorld ours, List<Item> items, SerializedEntity[] entities)
        {
            try
            {
                SerializedWorld theirs = new SerializedWorld(CurrentVersion());
                _saveEntities(factory, theirs);
                _saveSingletons(factory, theirs);
                List<SerializedEntity> a = new List<SerializedEntity>(ours.Entities());
                List<SerializedEntity> b = new List<SerializedEntity>(theirs.Entities());
                long mismatches = 0;
                if (a.Count != b.Count)
                {
                    mismatches++;
                    Log.Warning($"SaveSnapshot verify: {a.Count} entities against the game's {b.Count}");
                }
                for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
                {
                    if (!DeepEquals(a[i], b[i]))
                    {
                        if (mismatches++ < 5)
                        {
                            Log.Warning($"SaveSnapshot verify: entity {a[i].TemplateName} {a[i].Id} differs from the game's snapshot");
                        }
                    }
                }
                _verifyMismatches += mismatches;
                return theirs;
            }
            catch (Exception exception)
            {
                _verify = false;
                Log.Warning("SaveSnapshot verify failed and is off for this session; the snapshot itself is unaffected: " + exception);
                return null;
            }
        }

        // The game's SerializedObject.Equals compares list properties by reference, so two snapshots that hold
        // the same values never compare equal through it; this compares values all the way down.
        internal static bool DeepEquals(SerializedEntity a, SerializedEntity b)
        {
            if (a.Id != b.Id || a.TemplateName != b.TemplateName)
            {
                return false;
            }
            List<string> names = new List<string>(a.Components());
            List<string> otherNames = new List<string>(b.Components());
            if (names.Count != otherNames.Count)
            {
                return false;
            }
            foreach (string name in names)
            {
                if (!b.HasComponent(name) || !DeepEquals(a.GetComponent(name).Value, b.GetComponent(name).Value))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool DeepEquals(SerializedObject a, SerializedObject b)
        {
            List<string> names = new List<string>(a.Properties());
            List<string> otherNames = new List<string>(b.Properties());
            if (names.Count != otherNames.Count)
            {
                return false;
            }
            foreach (string name in names)
            {
                if (!b.Has(name) || !DeepEquals(a.GetSerialized(name), b.GetSerialized(name)))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool DeepEquals(object a, object b)
        {
            if (a is SerializedObject objectA)
            {
                return b is SerializedObject objectB && DeepEquals(objectA, objectB);
            }
            if (a is object[] arrayA)
            {
                if (!(b is object[] arrayB) || arrayA.Length != arrayB.Length)
                {
                    return false;
                }
                for (int i = 0; i < arrayA.Length; i++)
                {
                    if (!DeepEquals(arrayA[i], arrayB[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            return Equals(a, b);
        }

        private static void Note(string typeName)
        {
            Unlisted.TryGetValue(typeName, out int count);
            Unlisted[typeName] = count + 1;
        }

        // Once per session, after the first snapshot: which components kept entities on the main thread, so a
        // later version can read them and extend the list.
        internal static string TakeUnlistedLine()
        {
            if (!_active || _unlistedLogged || Unlisted.Count == 0)
            {
                return null;
            }
            _unlistedLogged = true;
            List<KeyValuePair<string, int>> top = new List<KeyValuePair<string, int>>(Unlisted);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            List<string> parts = new List<string>();
            for (int i = 0; i < Math.Min(12, top.Count); i++)
            {
                parts.Add($"{top[i].Key} ({top[i].Value})");
            }
            return "SaveSnapshot: components that keep an entity on the main thread, by how many entities carry them: " +
                   string.Join(", ", parts);
        }

        private static void Fail(Exception exception)
        {
            _active = false;
            Log.Warning("SaveSnapshot failed and is off for this session; the game takes its own snapshot: " + exception);
        }
    }
}
