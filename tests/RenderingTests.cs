using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.BlockingSystem;
using Timberborn.MechanicalSystem;
using Timberborn.TimbermeshAnimations;
using Timberborn.TimeSystem;
using Vector3 = UnityEngine.Vector3;

// The frame-side pieces of 0.4.28 that need no game scene: the physics sync rule and its hooks, the animator entries
// aligned with the game's list, the animator loop against the game's real TimbermeshAnimator time-keeping (a twin set
// of animators driven the way AnimatorRegistry.UpdateSingleton drives them must keep exactly the same times), and the
// shaft animator speeds against a model of the game's ModularShaftAnimator.UpdateAnimation on the game's real
// shaft, mechanical node, graph and blockable object.
internal static class RenderingTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        RunSettings(check);
        RunPhysicsSync(check);
        RunAlignedEntries(check);
        RunAnimatorFrames(check);
        RunShaftAnimators(check);
    }

    private static void RunSettings(Action<bool, string> check)
    {
        Config config = new Config();
        bool defaults = config.DeferPhysicsSync && config.ShaftAnimators;
        config.Apply(Config.Parse(new[] { "DeferPhysicsSync = false", "shaftanimators = FALSE" }));
        string shipped = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "packaging", "LateGamePerformance.cfg"));
        check(defaults && !config.DeferPhysicsSync && !config.ShaftAnimators &&
              System.Text.RegularExpressions.Regex.IsMatch(shipped, @"(?m)^DeferPhysicsSync = true\s*$") &&
              System.Text.RegularExpressions.Regex.IsMatch(shipped, @"(?m)^ShaftAnimators = true\s*$") &&
              config.ToString().Contains("DeferPhysicsSync=False, ShaftAnimators=False"),
            "config: DeferPhysicsSync and ShaftAnimators are ways out, on by default and in the shipped file");
    }

    private static void RunPhysicsSync(Action<bool, string> check)
    {
        // The rule: the first frame and every 64th after it sync; a raycast after a frame that did not syncs first,
        // once, and a raycast after a frame that synced does not.
        PhysicsSync.Policy policy = new PhysicsSync.Policy();
        int due = 0, raycastSyncs = 0;
        bool firstDue = policy.FrameDue();
        due += firstDue ? 1 : 0;
        bool noneAfterSync = !policy.RaycastNeedsSync();
        for (int frame = 1; frame < 640; frame++)
        {
            due += policy.FrameDue() ? 1 : 0;
            if (frame % 10 == 0)
            {
                raycastSyncs += policy.RaycastNeedsSync() ? 1 : 0;
                raycastSyncs += policy.RaycastNeedsSync() ? 1 : 0;   // a second raycast in the same frame
            }
        }
        // Raycasts on frames 10, 20, ..., 630: each needs one sync, except on frame 320, which synced itself.
        check(firstDue && noneAfterSync && due == 10 && raycastSyncs == 62,
            $"physics sync: the first frame and every 64th sync ({due} of 640); a raycast after a frame that left the " +
            $"sync out syncs first, once ({raycastSyncs} of 63 raycast frames)");

        // The two prefixes as Harmony calls them.
        Action original = PhysicsSync.SyncTransforms;
        int syncs = 0;
        PhysicsSync.SyncTransforms = () => syncs++;
        PhysicsSync.ActivateForTests();
        bool gameNeverRuns = true;
        for (int frame = 0; frame < 128; frame++)
        {
            // The audio listener's ray on every frame (a moving camera), inside its update.
            PhysicsSync.ListenerPrefix();
            PhysicsSync.RaycastPrefix();
            PhysicsSync.ListenerFinalizer();
            gameNeverRuns &= !PhysicsSync.LateUpdatePrefix();
            if (frame % 16 == 5)
            {
                PhysicsSync.RaycastPrefix();
            }
        }
        string line = PhysicsSync.TakeStatsLine();
        Console.WriteLine("     " + line);
        check(gameNeverRuns && syncs == 2 + 8 && line.StartsWith("PhysicsSync: the per-frame collider sync was left out in 126 of 128 frames") &&
              line.Contains("sampled every 64th frame") && line.Contains("; 8 syncs before a selection raycast") &&
              line.EndsWith("; 128 audio listener rays left on the last sync"),
            $"physics sync: over 128 frames the game's sync is left out, 2 run on their frames and 8 before selection raycasts, " +
            $"none for the audio listener's rays ({syncs})");

        // A sync that throws (on frame 128, one that syncs) turns the feature off, syncs once more and hands the frame
        // to the game.
        int calls = 0;
        PhysicsSync.SyncTransforms = () => { if (calls++ == 0) throw new InvalidOperationException("forced by the test"); };
        bool handedBack = PhysicsSync.LateUpdatePrefix();
        bool offAfter = !PhysicsSync.IsActive && PhysicsSync.LateUpdatePrefix() && PhysicsSync.TakeStatsLine() == null;
        PhysicsSync.RaycastPrefix();
        check(handedBack && offAfter && calls == 2,
            $"physics sync: a sync that throws turns the feature off, syncs once more, and the game syncs every frame again ({calls} calls)");
        PhysicsSync.SyncTransforms = original;
    }

    private static void RunAlignedEntries(Action<bool, string> check)
    {
        // The game's list: appends at the end, removals anywhere (the rest moves down). Walked every frame in order.
        AnimatorCulling.AlignedEntries entries = new AnimatorCulling.AlignedEntries();
        List<object> list = new List<object>();
        Dictionary<object, AnimatorCulling.Entry> first = new Dictionary<object, AnimatorCulling.Entry>();
        HashSet<object> removed = new HashSet<object>();
        Random random = new Random(64);
        bool kept = true, hashes = true, quietWhenUnchanged = true;
        for (int frame = 0; frame < 600; frame++)
        {
            bool changed = false;
            if (frame % 7 == 0 || frame < 3)
            {
                int adds = random.Next(0, 6);
                for (int k = 0; k < adds; k++, changed = true) list.Add(new object());
                int removes = list.Count > 0 ? random.Next(0, 4) : 0;
                for (int k = 0; k < removes && list.Count > 0; k++, changed = true)
                {
                    int at = random.Next(list.Count);
                    removed.Add(list[at]);
                    list.RemoveAt(at);
                }
            }
            long before = entries.Rematched;
            for (int i = 0; i < list.Count; i++)
            {
                AnimatorCulling.Entry entry = entries.At(i, list[i]);
                if (first.TryGetValue(list[i], out AnimatorCulling.Entry seen))
                {
                    kept &= ReferenceEquals(seen, entry);
                }
                else
                {
                    first[list[i]] = entry;
                }
                hashes &= entry.Hash == RuntimeHelpers.GetHashCode(list[i]);
            }
            entries.EndFrame(list.Count);
            quietWhenUnchanged &= changed || entries.Rematched == before;
        }
        object[] keys = (object[])typeof(AnimatorCulling.AlignedEntries).GetField("_keys", Any).GetValue(entries);
        IDictionary moved = (IDictionary)typeof(AnimatorCulling.AlignedEntries).GetField("_moved", Any).GetValue(entries);
        bool letGo = entries.Count == list.Count && moved.Count == 0 &&
                     keys.Take(list.Count).SequenceEqual(list) && keys.Skip(list.Count).All(key => key == null) &&
                     !keys.Any(removed.Contains);
        check(kept && hashes && quietWhenUnchanged && letGo && removed.Count > 50,
            $"animator entries: each animator keeps its entry through {removed.Count} removals and the adds of 600 frames, " +
            "nothing is matched up again in a frame without a change, and nothing removed is held");
        entries.Clear();
        check(entries.Count == 0 && entries.At(0, list[0]) != first[list[0]], "animator entries: cleared, every animator gets a new entry");
    }

    // One animator of the test: the mod's and its twin that the game's own loop drives.
    private sealed class Pair
    {
        public object Mod;
        public object Game;
        public float Distance;
        public bool Visible;
        public bool Live = true;
    }

    private static void RunAnimatorFrames(Action<bool, string> check)
    {
        Feature feature = AnimatorCulling.CreateFeature();
        feature.Patches[0].Target();
        Assembly animations = typeof(IAnimator).Assembly;
        Type animatorType = animations.GetType("Timberborn.TimbermeshAnimations.TimbermeshAnimator", true);
        Type registryType = animations.GetType("Timberborn.TimbermeshAnimations.AnimatorRegistry", true);
        Type metadataType = animations.GetType("Timberborn.TimbermeshAnimations.AnimationMetadata", true);
        Type updaterType = animations.GetType("Timberborn.TimbermeshAnimations.IAnimationUpdater", true);
        object registry = Activator.CreateInstance(registryType, true);
        IList modList = (IList)registryType.GetField("_animators", Any).GetValue(registry);
        List<object> gameList = new List<object>();

        FieldInfo speedField = animatorType.GetField("_speed", Any);
        FieldInfo enabledField = animatorType.GetField("<Enabled>k__BackingField", Any);
        PropertyInfo time = animatorType.GetProperty("Time");
        PropertyInfo repeated = animatorType.GetProperty("RepeatedTime");
        PropertyInfo finished = animatorType.GetProperty("PlayingFinished");
        object Make(float length, bool looped, float speed, bool enabled)
        {
            object animator = RuntimeHelpers.GetUninitializedObject(animatorType);
            animatorType.GetField("_currentAnimation", Any).SetValue(animator, Activator.CreateInstance(metadataType, "Work", length));
            speedField.SetValue(animator, speed);
            animatorType.GetField("_looped", Any).SetValue(animator, looped);
            enabledField.SetValue(animator, enabled);
            animatorType.GetField("_animationUpdaters", Any).SetValue(animator, Array.CreateInstance(updaterType, 0));
            return animator;
        }

        List<Pair> pairs = new List<Pair>();
        Random random = new Random(808);
        Pair Add(float distance, bool visible, float length, bool looped, float speed = 1f, bool enabled = true)
        {
            Pair pair = new Pair
            {
                Mod = Make(length, looped, speed, enabled), Game = Make(length, looped, speed, enabled), Distance = distance,
                Visible = visible
            };
            pairs.Add(pair);
            modList.Add(pair.Mod);
            gameList.Add(pair.Game);
            return pair;
        }
        // Near, 1x-2x, 2x-3x and beyond 3x the distance of 80; on and off screen; looped and played once.
        float[] distances = { 10f, 79.9f, 80f, 120f, 159.9f, 160f, 200f, 239.9f, 240f, 400f, 5000f };
        foreach (float distance in distances)
        {
            Add(distance, true, 1.3f, true);
            Add(distance, false, 2.1f, true);
            Add(distance, true, 0.9f, false);
            Add(distance, false, 0.7f, false);
        }
        Pair disabled = Add(300f, true, 1f, true, enabled: false);
        Pair still = Add(300f, true, 1f, true, speed: 0f);
        Pair dead = Add(10f, true, 1f, true);
        dead.Live = false;

        // Unity's parts, and counters on the game's methods.
        Action<object, float> update = AnimatorCulling.UpdateAnimation;
        Action<object, float> updateTime = AnimatorCulling.UpdateTime;
        Action<object> writePose = AnimatorCulling.WritePose;
        Func<int> frameCount = AnimatorCulling.FrameCount;
        Func<float> deltaTime = AnimatorCulling.DeltaTime;
        Func<object, bool> isLive = AnimatorCulling.IsLive;
        Func<Vector3?> cameraPosition = AnimatorCulling.CameraPosition;
        Func<object, Vector3> positionOf = AnimatorCulling.PositionOf;
        Func<object, AnimatorCulling.Entry, int, bool> visible = AnimatorCulling.Visible;
        Dictionary<object, Pair> byMod = new Dictionary<object, Pair>(new IdentityComparer());
        Dictionary<object, int> updates = new Dictionary<object, int>(new IdentityComparer());
        Dictionary<object, int> poses = new Dictionary<object, int>(new IdentityComparer());
        Dictionary<object, AnimatorCulling.Entry> entryOf = new Dictionary<object, AnimatorCulling.Entry>(new IdentityComparer());
        int frame = 1000;
        float delta = 0.016f;
        Exception visibleFailure = null;
        AnimatorCulling.FrameCount = () => frame;
        AnimatorCulling.DeltaTime = () => delta;
        AnimatorCulling.IsLive = animator => byMod[animator].Live;
        AnimatorCulling.CameraPosition = () => Vector3.zero;
        AnimatorCulling.PositionOf = animator => new Vector3(byMod[animator].Distance, 0f, 0f);
        bool sameEntry = true;
        AnimatorCulling.Visible = (animator, entry, f) =>
        {
            if (visibleFailure != null) throw visibleFailure;
            if (entryOf.TryGetValue(animator, out AnimatorCulling.Entry seen)) sameEntry &= ReferenceEquals(seen, entry);
            else entryOf[animator] = entry;
            return byMod[animator].Visible;
        };
        AnimatorCulling.UpdateAnimation = (animator, dt) => { Bump(updates, animator); update(animator, dt); };
        AnimatorCulling.WritePose = animator => { Bump(poses, animator); writePose(animator); };
        AnimatorCulling.ConfigureLod(true, 80f);
        AnimatorCulling.ActivateForTests();

        bool sameTimes = true, expectedCalls = true, prefixReplaces = true;
        int finalPoses = 0, leftOut = 0, frames = 0;
        long written = 0, offScreen = 0, far2 = 0, far4 = 0, far8 = 0;
        string statsLine = null;
        for (int f = 0; f < 160; f++, frame++)
        {
            frames++;
            delta = 0.012f + (float)random.NextDouble() * 0.01f;
            foreach (Pair pair in pairs) byMod[pair.Mod] = pair;
            if (f == 40)
            {
                // Removals in the middle of the list and new animators at its end, in both lists.
                foreach (int at in new[] { 30, 5, 0 })
                {
                    pairs.RemoveAt(at);
                    modList.RemoveAt(at);
                    gameList.RemoveAt(at);
                }
                Add(90f, true, 1.5f, true);
                Add(250f, false, 0.5f, false);
                foreach (Pair pair in pairs) byMod[pair.Mod] = pair;
            }
            if (f == 70)
            {
                // Switched off by the game between frames, in both.
                enabledField.SetValue(pairs[3].Mod, false);
                enabledField.SetValue(pairs[3].Game, false);
            }
            // What each animator should see this frame, decided before the frame from the game's own state.
            Dictionary<object, bool> wouldAnimate = pairs.ToDictionary(pair => pair.Mod, pair => pair.Live && AnimatorCulling.WouldAnimate(pair.Mod), new IdentityComparer());
            Dictionary<object, bool> wasFinished = pairs.ToDictionary(pair => pair.Mod, pair => (bool)finished.GetValue(pair.Mod), new IdentityComparer());
            updates.Clear();
            poses.Clear();
            if (f == 120)
            {
                visibleFailure = new InvalidOperationException("forced by the test");
            }
            bool runOriginal = AnimatorCulling.RegistryPrefix(registry);
            if (runOriginal)
            {
                // Harmony runs the game's method: its own loop over the same list.
                foreach (Pair pair in pairs)
                {
                    if (pair.Live) update(pair.Mod, delta);
                }
            }
            prefixReplaces &= f <= 120 ? !runOriginal : runOriginal;
            foreach (Pair pair in pairs)
            {
                if (pair.Live) update(pair.Game, delta);
            }
            foreach (Pair pair in pairs)
            {
                // Time is the game's whatever the mod did with the pose, on the failing frame too.
                sameTimes &= ((float)time.GetValue(pair.Mod)).Equals((float)time.GetValue(pair.Game)) &&
                             ((float)repeated.GetValue(pair.Mod)).Equals((float)repeated.GetValue(pair.Game)) &&
                             ((bool)finished.GetValue(pair.Mod)) == (bool)finished.GetValue(pair.Game);
                if (f >= 120 || !wouldAnimate[pair.Mod])
                {
                    // Idle, disabled, still, finished and dead animators: not called at all while the mod walks.
                    expectedCalls &= f >= 120 || updates.GetValueOrDefault(pair.Mod) + poses.GetValueOrDefault(pair.Mod) == 0;
                    continue;
                }
                float distance = Vector3.Distance(new Vector3(pair.Distance, 0f, 0f), Vector3.zero);
                int interval = AnimatorCulling.PoseInterval(distance, 80f);
                bool dueByDistance = AnimatorCulling.PoseDue(frame, RuntimeHelpers.GetHashCode(pair.Mod), interval);
                bool due = dueByDistance && pair.Visible;
                bool justFinished = !wasFinished[pair.Mod] && (bool)finished.GetValue(pair.Mod);
                int expectedPoses = !due && justFinished ? 1 : 0;
                expectedCalls &= updates.GetValueOrDefault(pair.Mod) == (due ? 1 : 0) && poses.GetValueOrDefault(pair.Mod) == expectedPoses;
                finalPoses += expectedPoses;
                leftOut += due ? 0 : 1;
                if (f <= 99)
                {
                    // What the stats line taken after frame 99 has to say.
                    written += due ? 1 : 0;
                    offScreen += dueByDistance && !pair.Visible ? 1 : 0;
                    far2 += !dueByDistance && interval == 2 ? 1 : 0;
                    far4 += !dueByDistance && interval == 4 ? 1 : 0;
                    far8 += !dueByDistance && interval == 8 ? 1 : 0;
                }
            }
            if (f == 99)
            {
                statsLine = AnimatorCulling.TakeStatsLine();
            }
        }
        Console.WriteLine("     " + statsLine);
        check(statsLine == $"AnimatorCulling: {written + offScreen + far2 + far4 + far8} animator updates; the pose was written in " +
                           $"{written}, left out in {offScreen} because no part of the object was on screen, and left out for " +
                           $"objects far from the camera in {far2} (written every 2nd frame), {far4} (every 4th) and {far8} " +
                           "(every 8th); their time kept running" && far8 > 0 && written > 0,
            "animator loop: the stats line counts the poses written and left out, off screen and per distance tier");
        check(sameTimes, $"animator loop: over {frames} frames with removals, additions and a mid-run failure every animator's Time, " +
                         "RepeatedTime and PlayingFinished stay bit for bit what the game's own loop gives its twin");
        check(expectedCalls && sameEntry && leftOut > 1000 && finalPoses > 5,
            $"animator loop: the game's update runs exactly for the animators whose pose is due and on screen; the rest " +
            $"advance their time only ({leftOut} pose writes left out), and one that finishes on such a frame gets its " +
            $"final pose ({finalPoses} times); idle, disabled, still and dead animators are not called");
        check(prefixReplaces && !AnimatorCulling.IsActive && AnimatorCulling.EntriesForTests.Count == 0,
            "animator loop: the prefix replaces the game's loop; a failure mid-frame hands the rest of that frame to the " +
            "game's own calls (each animator's time advanced once, as above) and every later frame to the game's loop");
        check(AnimatorCulling.TakeStatsLine() == null, "animator loop: nothing to report once it turned itself off");

        // The tiers and their spread: every eighth frame beyond three times the distance.
        check(AnimatorCulling.PoseInterval(239.9f, 80f) == 4 && AnimatorCulling.PoseInterval(240f, 80f) == 8 &&
              AnimatorCulling.PoseInterval(5000f, 80f) == 8,
            "animator distance: every eighth frame from three times the distance on");
        bool spread = true;
        for (int k = 0; k < 2000; k++)
        {
            int hash = random.Next(int.MinValue, int.MaxValue), start = random.Next(0, 1 << 20), dueFrames = 0;
            for (int g = start; g < start + 8; g++) dueFrames += AnimatorCulling.PoseDue(g, hash, 8) ? 1 : 0;
            spread &= dueFrames == 1;
        }
        bool refreshSpread = true;
        HashSet<int> refreshFrames = new HashSet<int>();
        for (int k = 0; k < 2000; k++)
        {
            int next = AnimatorCulling.NextRefresh(100, random.Next(int.MinValue, int.MaxValue), 600);
            refreshSpread &= next >= 400 && next < 700;
            refreshFrames.Add(next);
        }
        check(spread && refreshSpread && refreshFrames.Count > 250,
            $"animator distance: at every eighth frame an object is due once in any eight; renderer lists are refreshed " +
            $"300 to 599 frames apart, spread by the object ({refreshFrames.Count} different frames for 2000 objects)");

        AnimatorCulling.FrameCount = frameCount;
        AnimatorCulling.DeltaTime = deltaTime;
        AnimatorCulling.IsLive = isLive;
        AnimatorCulling.CameraPosition = cameraPosition;
        AnimatorCulling.PositionOf = positionOf;
        AnimatorCulling.Visible = visible;
        AnimatorCulling.UpdateAnimation = update;
        AnimatorCulling.UpdateTime = updateTime;
        AnimatorCulling.WritePose = writePose;
    }

    private static void Bump(Dictionary<object, int> counts, object key)
    {
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    // An animator that writes down what it is told, into a log shared by a shaft's animators.
    private sealed class LoggingAnimator : IAnimator
    {
        private readonly List<string> _log;
        private readonly int _id;

        public LoggingAnimator(List<string> log, int id)
        {
            _log = log;
            _id = id;
        }

        public bool PlayBackwards { set { } }
        public bool Enabled { get => false; set => _log.Add($"{_id} enabled {value}"); }
        public float Speed { set => _log.Add($"{_id} speed {BitConverter.SingleToInt32Bits(value):X8}"); }
        public float Time => 0f;
        public float RepeatedTime => 0f;
        public string AnimationName => null;
        public float AnimationLength => 0f;
        public bool PlayingFinished => false;
        public event EventHandler AnimationChanged { add { } remove { } }
        public void Play(string animationName, bool looped = true) { }
        public void Stop() { }
        public void SetTime(float time) { }
    }

    // The day-night cycle the mechanical graph asks for the battery's share; only the tick length is read.
    public class DayNightCycleProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            return targetMethod.Name == "get_FixedDeltaTimeInHours" ? 24f / 768f : throw new NotSupportedException(targetMethod.Name);
        }
    }

    private static void RunShaftAnimators(Action<bool, string> check)
    {
        Feature feature = ShaftAnimators.CreateFeature();
        feature.Patches.ForEach(patch => patch.Target());
        Type shaftType = Type.GetType("Timberborn.ModularShafts.ModularShaftAnimator, Timberborn.ModularShafts", true);
        MethodInfo gameUpdate = shaftType.GetMethod("UpdateAnimation", Any);
        PropertyInfo isAnimated = shaftType.GetProperty("IsAnimated");
        IDayNightCycle cycle = DispatchProxy.Create<IDayNightCycle, DayNightCycleProxy>();
        // A singleton in the game: every shaft has the same one.
        object manager = RuntimeHelpers.GetUninitializedObject(typeof(NonlinearAnimationManager));

        // A shaft of the game's classes with a graph of the given supply and demand, blocked or not.
        object Shaft(int supply, int demand, int batteryCharge, bool blocked, List<string> log, int animators)
        {
            MechanicalGraph graph = (MechanicalGraph)RuntimeHelpers.GetUninitializedObject(typeof(MechanicalGraph));
            typeof(MechanicalGraph).GetField("<PowerSupply>k__BackingField", Any).SetValue(graph, supply);
            typeof(MechanicalGraph).GetField("<PowerDemand>k__BackingField", Any).SetValue(graph, demand);
            typeof(MechanicalGraph).GetField("<BatteryCharge>k__BackingField", Any).SetValue(graph, batteryCharge);
            typeof(MechanicalGraph).GetField("_dayNightCycle", Any).SetValue(graph, cycle);
            BlockableObject blockable = (BlockableObject)RuntimeHelpers.GetUninitializedObject(typeof(BlockableObject));
            HashSet<object> blockers = new HashSet<object>();
            if (blocked) blockers.Add(new object());
            typeof(BlockableObject).GetField("_blockers", Any).SetValue(blockable, blockers);
            MechanicalNode node = (MechanicalNode)RuntimeHelpers.GetUninitializedObject(typeof(MechanicalNode));
            typeof(MechanicalNode).GetField("_blockableObject", Any).SetValue(node, blockable);
            typeof(MechanicalNode).GetField("<Graph>k__BackingField", Any).SetValue(node, graph);
            object shaft = RuntimeHelpers.GetUninitializedObject(shaftType);
            shaftType.GetField("_mechanicalNode", Any).SetValue(shaft, node);
            shaftType.GetField("_animators", Any).SetValue(shaft,
                Enumerable.Range(0, animators).Select(id => (IAnimator)new LoggingAnimator(log, id)).ToList());
            shaftType.GetField("_nonlinearAnimationManager", Any).SetValue(shaft, manager);
            return shaft;
        }

        // ModularShaftAnimator.UpdateAnimation as the game has it, with the multiplier asked once per animator.
        void GamesUpdate(object shaft, Func<float> multiplier)
        {
            MechanicalNode node = (MechanicalNode)shaftType.GetField("_mechanicalNode", Any).GetValue(shaft);
            isAnimated.SetValue(shaft, node.ActiveAndPowered && node.PowerEfficiency > 0f);
            foreach (IAnimator animator in (List<IAnimator>)shaftType.GetField("_animators", Any).GetValue(shaft))
            {
                animator.Enabled = (bool)isAnimated.GetValue(shaft);
                if ((bool)isAnimated.GetValue(shaft))
                {
                    animator.Speed = node.PowerEfficiency * multiplier();
                }
            }
        }

        // Full power, a battery-backed three quarters, unpowered, blocked; one to four animators each.
        // Full power; three quarters with an empty battery; 0.42 with a battery's share; unpowered; blocked; no demand.
        (int supply, int demand, int battery, bool blocked)[] kinds =
            { (10, 5, 0, false), (3, 4, 0, false), (10, 100, 1, false), (0, 0, 0, false), (8, 2, 0, true), (6, 0, 0, false) };
        List<string> modLog = new List<string>(), gameLog = new List<string>();
        List<object> modShafts = new List<object>(), gameShafts = new List<object>();
        for (int i = 0; i < 20; i++)
        {
            var kind = kinds[i % kinds.Length];
            modShafts.Add(Shaft(kind.supply, kind.demand, kind.battery, kind.blocked, modLog, 1 + i % 4));
            gameShafts.Add(Shaft(kind.supply, kind.demand, kind.battery, kind.blocked, gameLog, 1 + i % 4));
        }
        Func<NonlinearAnimationManager, float> original = ShaftAnimators.SpeedMultiplierOf;
        float[] multipliers = { (float)Math.Pow(7, 0.5) / 7f, 1f, (float)Math.Pow(3, 0.5) / 3f };
        int modAsked = 0, gameAsked = 0;
        ShaftAnimators.Activate();
        bool replaced = true, sameAnimated = true;
        foreach (float multiplier in multipliers)
        {
            // The speed buttons change the time scale between ticks, never inside one.
            ShaftAnimators.SpeedMultiplierOf = manager => { modAsked++; return multiplier; };
            ShaftAnimators.TickPrefix();
            foreach (object shaft in modShafts) replaced &= !ShaftAnimators.UpdateAnimationPrefix(shaft);
            ShaftAnimators.TickFinalizer();
            foreach (object shaft in gameShafts) GamesUpdate(shaft, () => { gameAsked++; return multiplier; });
        }
        for (int i = 0; i < modShafts.Count; i++) sameAnimated &= (bool)isAnimated.GetValue(modShafts[i]) == (bool)isAnimated.GetValue(gameShafts[i]);
        check(replaced && sameAnimated && modLog.SequenceEqual(gameLog) && modLog.Count > 100 && modAsked == 3 && gameAsked > 20,
            $"shaft animators: the same Enabled and Speed assignments, bit for bit and in order, and the same IsAnimated as the " +
            $"game's method over 3 ticks at different speeds, with the multiplier asked {modAsked} times instead of {gameAsked}");

        // Outside a tick the game's own method runs; an unpowered or blocked shaft never asks for the multiplier, so
        // the game's real method can run here and must agree with the mod's.
        bool outside = ShaftAnimators.UpdateAnimationPrefix(modShafts[0]);
        List<string> realLog = new List<string>(), mineLog = new List<string>();
        object real = Shaft(0, 0, 0, false, realLog, 3), mine = Shaft(0, 0, 0, false, mineLog, 3);
        object realBlocked = Shaft(8, 2, 0, true, realLog, 2), mineBlocked = Shaft(8, 2, 0, true, mineLog, 2);
        isAnimated.SetValue(real, true);
        isAnimated.SetValue(mine, true);
        gameUpdate.Invoke(real, null);
        gameUpdate.Invoke(realBlocked, null);
        ShaftAnimators.TickPrefix();
        bool mineReplaced = !ShaftAnimators.UpdateAnimationPrefix(mine) & !ShaftAnimators.UpdateAnimationPrefix(mineBlocked);
        ShaftAnimators.TickFinalizer();
        check(outside && mineReplaced && realLog.SequenceEqual(mineLog) && realLog.Count == 5 &&
              !(bool)isAnimated.GetValue(real) && !(bool)isAnimated.GetValue(mine),
            "shaft animators: outside a tick the game's method runs; unpowered and blocked shafts come out as the game's real method leaves them");

        string line = ShaftAnimators.TakeStatsLine();
        Console.WriteLine("     " + line);
        check(line != null && line.StartsWith("ShaftAnimators: 4 shaft animator ticks updated 62 shafts"),
            "shaft animators: the stats line counts ticks and shafts");

        // A shaft the mod cannot read turns the feature off and hands the call to the game.
        object broken = Shaft(10, 5, 0, false, new List<string>(), 1);
        shaftType.GetField("_mechanicalNode", Any).SetValue(broken, null);
        ShaftAnimators.TickPrefix();
        bool handedBack = ShaftAnimators.UpdateAnimationPrefix(broken);
        ShaftAnimators.TickFinalizer();
        check(handedBack && !ShaftAnimators.IsActive && ShaftAnimators.UpdateAnimationPrefix(modShafts[0]) && ShaftAnimators.TakeStatsLine() == null,
            "shaft animators: a failure turns the feature off and the game's method runs from then on");
        ShaftAnimators.SpeedMultiplierOf = original;
    }
}
