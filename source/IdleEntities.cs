using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Timberborn.BaseComponentSystem;
using Timberborn.TickSystem;

namespace LateGamePerformance
{
    // Entities with nothing to tick are passed over in the tick loop.
    //
    // Every tree, crop, path, levee and platform is a tickable entity: it carries a tick component that is
    // switched off nearly all the time (the "demolition blocked" status only while marked for demolition, the
    // "unreachable" status only while selected). The game still visits every one of them 128 times a day: a
    // native call to ask whether the object is active, a try block, a walk over its components to find each
    // one disabled. About 10,000 of the 11,500 entity ticks in the logged 354-beaver colony were such visits.
    //
    // This keeps, per bucket, a second sorted list with exactly the game's keys, holding one flag per entity:
    // whether any of its tick components is enabled. The replacement of TickableEntityBucket.TickAll walks the
    // game's list by the game's own index and, for each position, reads that flag instead of asking Unity and
    // walking the components; only entities with a component switched on are ticked. A component can only be
    // switched through BaseComponent.EnableComponent and DisableComponent (the setter is private), so those two
    // are hooked and flip the flag; TickableEntityBucket.Add and Remove keep the keys in step, with the same
    // deferral of removals during a pass that the game has. Each pass first makes sure the keys still match the
    // game's list, rebuilding the mirror if not; the walk over the keys for that is skipped while the list's own
    // change counter has not moved since they last matched and no Add or Remove came through the hooks (every 128th
    // pass walks them anyway). An entity whose tick components are all disabled does nothing when ticked, and the
    // index is the game's, so everything the game would do, including its own quirk of ticking an entity again when
    // something is inserted before it mid-pass, happens exactly as before.
    // The game's own list is untouched; BeaverBuddies reads it for its per-tick hash.
    //
    // Simulation feature: always on, the same on every computer. Self-disables on any bookkeeping exception and
    // hands the loop back to the game.
    internal static class IdleEntities
    {
        private const string BucketType = "Timberborn.TickSystem.TickableEntityBucket";

        private sealed class Record
        {
            public TickableEntity Entity;
            public Bucket Bucket;
            public BaseComponent[] Components;
            public bool Awake;
        }

        private sealed class Bucket
        {
            // The same keys as the game's _tickableEntities, so the same index means the same entity.
            public readonly SortedList<Guid, Record> Mirror = new SortedList<Guid, Record>();
            public bool Ticking;
            // The game's list and its change counter when its keys were last found equal to the mirror's (the list is
            // null once a postfix has changed the mirror's keys since), and the passes since the keys were last walked
            // (see Unchanged).
            public SortedList<Guid, TickableEntity> CheckedList;
            public int CheckedVersion;
            public int PassesSinceWalk;
        }

        // A bucket's keys are walked on at least every 128th of its passes, even with the list's counter unmoved.
        private const int WalkEvery = 128;

        private static Func<object, SortedList<Guid, TickableEntity>> _entities;
        private static Func<object, List<TickableEntity>> _toRemove;
        private static Action<object, bool> _setTicking;
        private static Func<object, object> _components;
        private static Func<object, object> _inner;
        // SortedList's own change counter; null when the runtime's list has none this knows, and then the keys are
        // walked on every pass.
        private static Func<object, int> _listVersion;

        private static readonly Dictionary<object, Bucket> Buckets = new Dictionary<object, Bucket>();
        private static readonly Dictionary<TickableEntity, Record> Records = new Dictionary<TickableEntity, Record>();
        private static readonly Dictionary<BaseComponent, Record> ByComponent = new Dictionary<BaseComponent, Record>();

        // Swappable for the test harness, where the game's Tick cannot run without Unity.
        internal static Action<TickableEntity> TickEntity = entity => entity.Tick();

        private static bool _active;
        private static long _passes;
        private static long _ticked;
        private static long _leftOut;
        private static long _resyncs;
        private static long _keyChecks;

        public static Feature CreateFeature(Config config)
        {
            Type self = typeof(IdleEntities);
            Feature feature = new Feature { Name = "IdleEntities" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableEntityBucket.TickAll",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return Reflect.Method(BucketType, "TickAll");
                },
                Prefix = Reflect.Own(self, nameof(TickAllPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableEntityBucket.Add",
                Required = true,
                Target = () => Reflect.Method(BucketType, "Add"),
                Postfix = Reflect.Own(self, nameof(AddPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "TickableEntityBucket.Remove",
                Required = true,
                Target = () => Reflect.Method(BucketType, "Remove"),
                Postfix = Reflect.Own(self, nameof(RemovePostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "BaseComponent.EnableComponent",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(typeof(BaseComponent), "EnableComponent"),
                Postfix = Reflect.Own(self, nameof(EnabledPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "BaseComponent.DisableComponent",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(typeof(BaseComponent), "DisableComponent"),
                Postfix = Reflect.Own(self, nameof(DisabledPostfix))
            });
            return feature;
        }

        internal static void Bind()
        {
            if (_entities != null)
            {
                return;
            }
            Type bucket = Reflect.GameType(BucketType);
            _entities = Reflect.FieldGetter<SortedList<Guid, TickableEntity>>(bucket, "_tickableEntities");
            _toRemove = Reflect.FieldGetter<List<TickableEntity>>(bucket, "_entitiesToRemove");
            _setTicking = Reflect.FieldSetter<bool>(bucket, "_isTicking");
            _components = Reflect.FieldGetter<object>(typeof(TickableEntity), "_tickableComponents");
            _inner = Reflect.FieldGetter<object>(typeof(MeteredTickableComponent), "_tickableComponent");
            _listVersion = ChangeCounter(typeof(SortedList<Guid, TickableEntity>));
        }

        // The list's change counter, which its every insert, removal and clear moves. It is 'version' in the game's Mono
        // and in .NET 8; '_version', the name List<T> uses, is tried in case a runtime renames it. Null if neither is
        // there, and then the keys are walked on every pass, as before.
        internal static Func<object, int> ChangeCounter(Type listType)
        {
            foreach (string name in new[] { "version", "_version" })
            {
                FieldInfo field = AccessTools.Field(listType, name);
                if (field != null && !field.IsStatic && field.FieldType == typeof(int))
                {
                    try
                    {
                        return Reflect.FieldGetter<int>(listType, name);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        public static void Activate()
        {
            SceneCreated();
            _active = true;
        }

        internal static bool IsActive => _active;

        // A new game or map: the previous scene's buckets and entities are gone.
        public static void SceneCreated()
        {
            Buckets.Clear();
            Records.Clear();
            ByComponent.Clear();
        }

        public static string TakeStatsLine()
        {
            if (!_active || _passes == 0)
            {
                return null;
            }
            double ticks = _passes / 128.0;
            string line = $"IdleEntities: per tick {_ticked / ticks:0} entities ticked and {_leftOut / ticks:0} passed over because " +
                          "none of their tick parts was switched on" +
                          $"; keys walked against the game's list on {_keyChecks} of {_passes} passes" +
                          (_listVersion == null ? " (the list's change counter was not found)" : "") +
                          (_resyncs > 0 ? $"; the mirror had to be rebuilt {_resyncs} time(s), which should not happen" : "");
            _passes = _ticked = _leftOut = _resyncs = _keyChecks = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        // Last, so that another mod's prefix on the same method (BeaverBuddies hashes the game's list before the
        // pass) still runs before the pass, and one that skips the pass skips this too.
        [HarmonyPriority(Priority.Last)]
        internal static bool TickAllPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            Bucket bucket;
            SortedList<Guid, TickableEntity> all;
            try
            {
                all = _entities(__instance);
                if (!Buckets.TryGetValue(__instance, out bucket))
                {
                    if (all.Count == 0)
                    {
                        return true;
                    }
                    bucket = new Bucket();
                    Buckets[__instance] = bucket;
                }
                int version = _listVersion != null ? _listVersion(all) : 0;
                if (bucket.Mirror.Count != all.Count || !(Unchanged(bucket, all, version) || SameKeys(bucket, all)))
                {
                    Resync(bucket, all);
                }
                // The keys match now; until the counter moves or a postfix changes the mirror they still will.
                bucket.CheckedList = _listVersion != null ? all : null;
                bucket.CheckedVersion = version;
                _setTicking(__instance, true);
                bucket.Ticking = true;
            }
            catch (Exception exception)
            {
                Fail(exception);
                return true;
            }
            SortedList<Guid, Record> mirror = bucket.Mirror;
            int total = all.Count;
            int ticked = 0;
            // The game's loop, index for index; the flag replaces the visit. No try/finally: if an entity's Tick
            // throws, the game leaves the bucket mid-pass (_isTicking set, removals pending) and so does this.
            // Should the mirror stop being trusted mid-pass (a bookkeeping failure while an entity was added from
            // inside a tick), every remaining entity is ticked, which is exactly the game's loop.
            for (int i = 0; i < all.Count; i++)
            {
                if (!_active || mirror.Count != all.Count || mirror.Values[i].Awake)
                {
                    ticked++;
                    TickEntity(all.Values[i]);
                }
            }
            // What the game does at the end of its own pass, on both lists.
            bucket.Ticking = false;
            try
            {
                _setTicking(__instance, false);
                List<TickableEntity> toRemove = _toRemove(__instance);
                for (int i = 0; i < toRemove.Count; i++)
                {
                    Guid id = toRemove[i].EntityId;
                    all.Remove(id);
                    mirror.Remove(id);
                }
                toRemove.Clear();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
            _passes++;
            _ticked += ticked;
            _leftOut += total - ticked;
            return false;
        }

        internal static void AddPostfix(object __instance, TickableEntity tickableEntity)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (!Buckets.TryGetValue(__instance, out Bucket bucket))
                {
                    bucket = new Bucket();
                    Buckets[__instance] = bucket;
                }
                Record record = Register(bucket, tickableEntity);
                // The game's Add inserts at once, even during a pass; so does the mirror, at the same index.
                bucket.Mirror[tickableEntity.EntityId] = record;
                // The next pass walks the keys (see Unchanged), even should another mod have skipped the game's Add.
                bucket.CheckedList = null;
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void RemovePostfix(object __instance, TickableEntity tickableEntity)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (!Buckets.TryGetValue(__instance, out Bucket bucket))
                {
                    return;
                }
                if (bucket.Ticking && Records.TryGetValue(tickableEntity, out Record pending))
                {
                    // The game still ticks it for the rest of this pass, but once forgotten no part switched on
                    // from here on would wake it. Awake, it goes through the game's own Tick, which checks every part
                    // as the game's loop does; with nothing switched on that does nothing.
                    pending.Awake = true;
                }
                Forget(tickableEntity);
                if (!bucket.Ticking)
                {
                    bucket.Mirror.Remove(tickableEntity.EntityId);
                    // As in Add: the next pass walks the keys, even should another mod have skipped the game's Remove.
                    bucket.CheckedList = null;
                }
                // During a pass the game defers the removal to the end of TickAll (_entitiesToRemove); the mirror
                // removes the same keys there.
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void EnabledPostfix(BaseComponent __instance)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (ByComponent.TryGetValue(__instance, out Record record))
                {
                    record.Awake = true;
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal static void DisabledPostfix(BaseComponent __instance)
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (ByComponent.TryGetValue(__instance, out Record record) && record.Awake)
                {
                    record.Awake = AnyEnabled(record.Components);
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
        // ReSharper restore InconsistentNaming

        private static Record Register(Bucket bucket, TickableEntity entity)
        {
            if (Records.TryGetValue(entity, out Record existing))
            {
                return existing;
            }
            Record record = new Record { Entity = entity, Bucket = bucket, Components = Collect(entity) };
            record.Awake = AnyEnabled(record.Components);
            Records[entity] = record;
            foreach (BaseComponent component in record.Components)
            {
                ByComponent[component] = record;
            }
            return record;
        }

        private static void Forget(TickableEntity entity)
        {
            if (Records.TryGetValue(entity, out Record record))
            {
                Records.Remove(entity);
                foreach (BaseComponent component in record.Components)
                {
                    ByComponent.Remove(component);
                }
            }
        }

        // Whether the game's list is the one whose keys last matched the mirror's, with its change counter where it stood
        // then and the mirror untouched by the postfixes since; if so, the walk over the keys is skipped. Every change to a
        // SortedList's keys (an insert, a removal, a clear) moves its counter, so the game's keys are as they were. The
        // mirror's keys change in Add's postfix and in Remove's outside a pass, which both clear CheckedList so that the
        // next pass walks whatever the game's list did (another mod's prefix may have skipped the game's Add or Remove
        // while these postfixes still ran), and in the end-of-pass removals, where each key goes from both lists (moving
        // the counter) or is in neither. So the keys still match, the walk would have found them equal, and skipping it
        // changes nothing. The walk runs anyway on every 128th pass of a bucket, which catches a change that bypasses the
        // counter (only a write to the list's private fields could) or a counter that went all the way round.
        private static bool Unchanged(Bucket bucket, SortedList<Guid, TickableEntity> all, int version)
        {
            return _listVersion != null && ReferenceEquals(bucket.CheckedList, all) && version == bucket.CheckedVersion &&
                   ++bucket.PassesSinceWalk < WalkEvery;
        }

        private static bool SameKeys(Bucket bucket, SortedList<Guid, TickableEntity> all)
        {
            _keyChecks++;
            bucket.PassesSinceWalk = 0;
            IList<Guid> mine = bucket.Mirror.Keys;
            IList<Guid> theirs = all.Keys;
            for (int i = 0; i < theirs.Count; i++)
            {
                if (mine[i] != theirs[i])
                {
                    return false;
                }
            }
            return true;
        }

        // Should never be needed: the mirror follows every Add and Remove. If it ever is, it is rebuilt from the
        // game's list and counted, so the stats line shows it.
        private static void Resync(Bucket bucket, SortedList<Guid, TickableEntity> all)
        {
            // Records of entities this bucket no longer holds go too.
            List<TickableEntity> stale = new List<TickableEntity>();
            foreach (KeyValuePair<TickableEntity, Record> pair in Records)
            {
                if (pair.Value.Bucket == bucket && !all.ContainsKey(pair.Key.EntityId))
                {
                    stale.Add(pair.Key);
                }
            }
            foreach (TickableEntity entity in stale)
            {
                Forget(entity);
            }
            bucket.Mirror.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                TickableEntity entity = all.Values[i];
                Record record = Register(bucket, entity);
                record.Awake = AnyEnabled(record.Components);
                bucket.Mirror.Add(entity.EntityId, record);
            }
            bucket.PassesSinceWalk = 0;
            _resyncs++;
        }

        private static BaseComponent[] Collect(TickableEntity entity)
        {
            List<BaseComponent> components = new List<BaseComponent>();
            foreach (object metered in (IEnumerable)_components(entity))
            {
                components.Add((BaseComponent)_inner(metered));
            }
            return components.ToArray();
        }

        private static bool AnyEnabled(BaseComponent[] components)
        {
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i].Enabled)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Fail(object reason)
        {
            _active = false;
            TurnedOff.Report("IdleEntities",
                "IdleEntities failed and turned itself off for this session; the game's own tick loop runs: " + reason);
        }
    }
}
