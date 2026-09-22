using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using LateGamePerformance;
using Timberborn.CharacterMovementSystem;
using Timberborn.Navigation;
using UnityEngine;
using Random = System.Random;

// The path follower against the game's own code. The game's PathFollower cannot run here (its transform and clock
// are Unity's), so the test copies the IL of MoveAlongPath and of every private method it calls, as compiled in the
// installed game, into dynamic methods, with only the Unity calls sent elsewhere: Transform.position to a stand-in
// that stores exactly the floats it is given (what a root transform does), Time.time to a clock of the test's, and
// the animator to a recorder. Everything else, the loop, MoveInDirection, the speed limit near the target, the
// smoothing corner, the MovedAlongPath event and the real NavigationService's stopping distance, is the game's code.
// The mod's prefix runs on a second PathFollower with its game-method delegates pointed at those same copies, and
// after every move the two must agree bit for bit: the position, the corner index, every animated corner, what the
// animator was handed and every event.
internal static class PathFollowTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // The stand-in for Unity: positions per transform (a lossy one rounds what it is given, as a transform with a parent
    // could), transforms that have a parent, the clock, and what the animator was handed.
    private static readonly Dictionary<Transform, Vector3> Positions = new Dictionary<Transform, Vector3>(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<Transform> Lossy = new HashSet<Transform>(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<Transform> Parented = new HashSet<Transform>(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<MovementAnimator, List<string>> Animations =
        new Dictionary<MovementAnimator, List<string>>(ReferenceEqualityComparer.Instance);
    private static float _now;
    // Transform writes made by the game's copied code and by the mod's own loop.
    private static long _gamesWrites;
    private static long _modsReads;
    private static long _modsWrites;

    public static Vector3 GetPosition(Transform transform)
    {
        return Positions[transform];
    }

    public static void SetPosition(Transform transform, Vector3 position)
    {
        _gamesWrites++;
        Positions[transform] = Lossy.Contains(transform)
            ? new Vector3(Mathf.Round(position.x * 64f) / 64f, Mathf.Round(position.y * 64f) / 64f, Mathf.Round(position.z * 64f) / 64f)
            : position;
    }

    public static float Now()
    {
        return _now;
    }

    public static void Animate(MovementAnimator animator, IEnumerable<AnimatedPathCorner> corners, string animationName)
    {
        if (!Animations.TryGetValue(animator, out List<string> list))
        {
            Animations[animator] = list = new List<string>();
        }
        list.Add(animationName + ": " + string.Join(" | ", corners.Select(Describe)));
    }

    private sealed class Beaver
    {
        public PathFollower Follower;
        public Transform Transform;
        public MovementAnimator Animator;
        public readonly List<string> Events = new List<string>();
        public int SpeedAsked;

        public float Speed()
        {
            // Depends on where the transform is, as the game's WalkerSpeedManager does (the water at the beaver's tile).
            SpeedAsked++;
            Vector3 position = Positions[Transform];
            return 0.8f + 0.5f * Mathf.Abs(Mathf.Sin(position.x * 1.7f + position.z * 0.3f));
        }
    }

    private sealed class GameCode
    {
        public Action<PathFollower, float, string, Func<float>> MoveAlongPath;
        public Func<PathFollower, float, Func<float>, float?> SpeedLimitIfCloseToTarget;
        public Func<PathFollower, float> TimeFromLastPathPoint;
        public Action<PathFollower, Vector3, float, float, int> AddAnimatedPathCorner;
        public Func<PathFollower, bool> ReachedLastPathCorner;
        public Action<PathFollower, float, bool, float, float> AddSmoothingAnimatedPathCorner;
        public int UnityCalls;
    }

    public static void Run(Action<bool, string> check)
    {
        GameCode game = CopyGameCode();
        check(game.UnityCalls == 9,
            $"path follow: the game's MoveAlongPath and the methods it calls copied from the installed game, with its {game.UnityCalls} " +
            "Unity calls (transform, clock, animator) sent to a stand-in");
        INavigationService navigation = (INavigationService)RuntimeHelpers.GetUninitializedObject(
            typeof(INavigationService).Assembly.GetType("Timberborn.Navigation.NavigationService", true));

        PathFollow.CreateFeature(new Config()).Patches[0].Target();
        PathFollow.Activate();
        PathFollow.ResetForTests();
        PathFollow.ForeignPatch = () => null;
        PathFollow.SpeedLimitIfCloseToTarget = game.SpeedLimitIfCloseToTarget;
        PathFollow.TimeFromLastPathPoint = game.TimeFromLastPathPoint;
        PathFollow.AddAnimatedPathCorner = game.AddAnimatedPathCorner;
        PathFollow.ReachedLastPathCorner = game.ReachedLastPathCorner;
        PathFollow.AddSmoothingAnimatedPathCorner = game.AddSmoothingAnimatedPathCorner;
        PathFollow.ReadPosition = transform =>
        {
            _modsReads++;
            return GetPosition(transform);
        };
        PathFollow.WritePosition = (transform, position) =>
        {
            _modsWrites++;
            _gamesWrites--;
            SetPosition(transform, position);
        };
        PathFollow.IsRoot = transform => !Parented.Contains(transform);
        PathFollow.Animate = (animator, corners, animationName) => Animate(animator, corners, animationName);

        // Random walks: paths of 2 to 17 corners at map coordinates, with steps of every length (none, shorter than the
        // game's 0.0001 threshold, up to a few tiles), corners with instant speed, speed changes and groups; tick lengths
        // from a fortieth of a second to half a second; ticks sharing a frame (the same clock); new paths mid-walk;
        // verify mode on in every fourth walk.
        Random random = new Random(2026);
        int moves = 0, steps = 0, differences = 0, walks = 0, speedQuestions = 0, verifiedMoves = 0, instant = 0;
        long totalGamesWrites = 0, totalModsWrites = 0;
        string firstDifference = null;
        float[] tickLengths = { 0.025f, 0.0857143f, 0.1f, 0.2f, 0.5f };
        for (int walk = 0; walk < 1500; walk++)
        {
            bool verify = walk % 4 == 3;
            PathFollow.VerifyEnabled = verify;
            float dt = tickLengths[random.Next(tickLengths.Length)];
            Vector3 start = new Vector3(Next(random, 0, 250), Next(random, 0, 20), Next(random, 0, 250));
            Beaver gameBeaver = MakeBeaver(navigation, start), modBeaver = MakeBeaver(navigation, start);
            List<PathCorner> path = MakePath(random, start + new Vector3(Next(random, -0.3, 0.3), 0f, Next(random, -0.3, 0.3)));
            instant += path.Count(corner => corner.Speed == float.MaxValue);
            gameBeaver.Follower.StartMovingAlongPath(path);
            modBeaver.Follower.StartMovingAlongPath(path);
            walks++;
            for (int tick = 0; tick < 120; tick++)
            {
                if (random.Next(3) > 0)
                {
                    _now += dt;
                }
                if (random.Next(40) == 0)
                {
                    // A new destination from where the beaver stands (Walker.FindPath).
                    path = MakePath(random, Positions[gameBeaver.Transform]);
                    gameBeaver.Follower.StartMovingAlongPath(path);
                    modBeaver.Follower.StartMovingAlongPath(path);
                }
                bool gameArrived = game.ReachedLastPathCorner(gameBeaver.Follower);
                if (gameArrived != game.ReachedLastPathCorner(modBeaver.Follower))
                {
                    differences++;
                    firstDifference ??= $"walk {walk} tick {tick}: arrived differs";
                    break;
                }
                if (gameArrived)
                {
                    break;
                }
                long gamesWrites = _gamesWrites;
                game.MoveAlongPath(gameBeaver.Follower, dt, "Walking", gameBeaver.Speed);
                gamesWrites = _gamesWrites - gamesWrites;
                long reads = _modsReads, writes = _modsWrites;
                bool ranGame = PathFollow.MovePrefix(modBeaver.Follower, dt, "Walking", modBeaver.Speed);
                reads = _modsReads - reads;
                writes = _modsWrites - writes;
                moves++;
                steps += CornerCount(modBeaver) - 1;
                string difference = ranGame ? "the prefix handed the move to the game" : Difference(gameBeaver, modBeaver);
                if (difference == null && !verify)
                {
                    // Read once at the start and after each speed question at a corner, written before each such question
                    // and once at the end.
                    totalGamesWrites += gamesWrites;
                    totalModsWrites += writes;
                    if (reads != writes || writes < 1)
                    {
                        difference = $"the mod read the transform {reads} times and wrote it {writes} times";
                    }
                }
                if (verify)
                {
                    verifiedMoves++;
                }
                if (difference != null)
                {
                    differences++;
                    firstDifference ??= $"walk {walk} tick {tick}: {difference}";
                    break;
                }
            }
            speedQuestions += gameBeaver.SpeedAsked;
        }
        PathFollow.VerifyEnabled = false;
        check(differences == 0 && moves > 20000 && instant > 100,
            $"path follow: {moves} moves in {walks} random walks ({steps} animated corners, {speedQuestions} speed questions, " +
            $"{verifiedMoves} moves in verify mode) identical to the game's own MoveAlongPath bit for bit: position, corner " +
            $"index, animated corners, what the animator is handed, the MovedAlongPath events" +
            (firstDifference == null ? "" : "; first difference: " + firstDifference));
        check(totalModsWrites * 2 < totalGamesWrites,
            $"path follow: the transform is written {totalModsWrites} times where the game's loop writes it {totalGamesWrites} " +
            "times, and read as often as written");
        string stats = PathFollow.TakeStatsLine();
        Console.WriteLine("     " + stats);
        check(stats != null && stats.StartsWith($"PathFollow: {moves} moves along a path") && !stats.Contains("verify mismatches"),
            "path follow stats: " + stats);
        PathFollow.VerifyEnabled = true;
        check(PathFollow.TakeStatsLine().EndsWith("verify mismatches 0"),
            "path follow verify: the game's own loop, run beside the mod's on the stand-in transform, agreed on every move in verify mode");
        PathFollow.VerifyEnabled = false;

        // A beaver whose transform has a parent is left to the game's own method, untouched.
        Beaver seated = MakeBeaver(navigation, new Vector3(10f, 2f, 10f));
        seated.Follower.StartMovingAlongPath(MakePath(new Random(5), new Vector3(10f, 2f, 10f)));
        Parented.Add(seated.Transform);
        bool leftToGame = PathFollow.MovePrefix(seated.Follower, 0.1f, "Walking", seated.Speed);
        check(leftToGame && Positions[seated.Transform] == new Vector3(10f, 2f, 10f) && CornerCount(seated) == 0 &&
              PathFollow.TakeStatsLine().Contains("1 moves left to the game"),
            "path follow: a beaver whose transform has a parent is left to the game's own method, untouched");

        // Another mod's patch on a helper the mod's loop does not call: every move goes to the game's method.
        PathFollow.ResetForTests();
        PathFollow.ForeignPatch = () => "PathFollower.ReachedLastPathCorner (another.mod)";
        Beaver other = MakeBeaver(navigation, new Vector3(20f, 2f, 20f));
        other.Follower.StartMovingAlongPath(MakePath(new Random(6), new Vector3(20f, 2f, 20f)));
        bool first = PathFollow.MovePrefix(other.Follower, 0.1f, "Walking", other.Speed);
        bool second = PathFollow.MovePrefix(other.Follower, 0.1f, "Walking", other.Speed);
        check(first && second && CornerCount(other) == 0 && PathFollow.TakeStatsLine().Contains("2 moves left to the game"),
            "path follow: with another mod's patch on a helper the loop does not call, every move is the game's own method");
        PathFollow.ResetForTests();
        PathFollow.ForeignPatch = () => null;

        // Verify only measures. On a transform that does not return what it was given (the stand-in rounds to 1/64,
        // as a transform with a parent can), the game's own loop, which reads the transform back at every step, walks
        // a different way; that is counted and logged, and the beaver still gets the mod's move, the same as with
        // verify off. The animator and the event run once.
        Vector3 onGrid = new Vector3(40f, 1f, 40f);
        List<PathCorner> offGrid = new List<PathCorner>
        {
            new PathCorner(onGrid, 1f, 0), new PathCorner(new Vector3(40.337f, 1.011f, 41.113f), 1f, 0),
            new PathCorner(new Vector3(41.771f, 1.013f, 41.9f), 1f, 0), new PathCorner(new Vector3(45.3f, 1.2f, 43.37f), 1f, 0)
        };
        Beaver plain = MakeBeaver(navigation, onGrid), verified = MakeBeaver(navigation, onGrid);
        Lossy.Add(plain.Transform);
        Lossy.Add(verified.Transform);
        plain.Follower.StartMovingAlongPath(offGrid);
        verified.Follower.StartMovingAlongPath(offGrid);
        PathFollow.VerifyEnabled = false;
        bool plainRan = PathFollow.MovePrefix(plain.Follower, 0.5f, "Walking", plain.Speed);
        PathFollow.VerifyEnabled = true;
        bool verifiedRan = PathFollow.MovePrefix(verified.Follower, 0.5f, "Walking", verified.Speed);
        string verifyStats = PathFollow.TakeStatsLine();
        PathFollow.VerifyEnabled = false;
        Console.WriteLine("     " + verifyStats);
        check(!plainRan && !verifiedRan && Difference(plain, verified) == null && Animations[verified.Animator].Count == 1 &&
              verified.Events.Count == 1 && verifyStats.EndsWith("verify mismatches 1"),
            "path follow verify: a transform that does not give back what it was given is counted as a mismatch, and the " +
            "beaver gets the mod's move all the same, the same as with verify off; the animator and the event run once");

        // A failure inside the mod's loop after the transform was written (for the speed question at the first corner
        // reached): the beaver, its corner index and its corners are put back, the feature turns itself off and the move
        // goes to the game's own method, which then moves it exactly as without the mod.
        List<PathCorner> corners = new List<PathCorner>
        {
            new PathCorner(new Vector3(60f, 1f, 60f), 1f, 0), new PathCorner(new Vector3(60.1f, 1f, 60f), 1f, 0),
            new PathCorner(new Vector3(62f, 1f, 61f), 1f, 1), new PathCorner(new Vector3(66f, 1f, 64f), 1f, 1),
            new PathCorner(new Vector3(70f, 1f, 66f), 1f, 0), new PathCorner(new Vector3(75f, 1f, 70f), 1f, 0)
        };
        Beaver failing = MakeBeaver(navigation, new Vector3(60f, 1f, 60f)), reference = MakeBeaver(navigation, new Vector3(60f, 1f, 60f));
        failing.Follower.StartMovingAlongPath(corners);
        reference.Follower.StartMovingAlongPath(corners);
        // A first short move, through the mod, so that there are corners to put back.
        game.MoveAlongPath(reference.Follower, 0.02f, "Walking", reference.Speed);
        bool firstMoveRan = PathFollow.MovePrefix(failing.Follower, 0.02f, "Walking", failing.Speed);
        string beforeFailure = firstMoveRan ? "the first move was handed to the game" : Difference(reference, failing);
        PathFollow.TakeStatsLine();
        PathFollow.MoveInDirectionCall moveInDirection = PathFollow.MoveInDirection;
        int calls = 0;
        PathFollow.MoveInDirection = (Vector3 position, Vector3 target, float speed, ref float remainingTime, out bool reachedTarget) =>
        {
            if (++calls == 3)
            {
                throw new InvalidOperationException("forced by the test");
            }
            return moveInDirection(position, target, speed, ref remainingTime, out reachedTarget);
        };
        Vector3 position0 = Positions[failing.Transform];
        long writesBefore = _modsWrites;
        string cornersBefore = string.Join(" | ", Corners(failing).Select(Describe));
        int indexBefore = (int)IndexField.GetValue(failing.Follower);
        bool handedToGame = PathFollow.MovePrefix(failing.Follower, 0.5f, "Walking", failing.Speed);
        bool putBack = calls == 3 && _modsWrites > writesBefore + 1 && Same(Positions[failing.Transform], position0) &&
                       (int)IndexField.GetValue(failing.Follower) == indexBefore &&
                       string.Join(" | ", Corners(failing).Select(Describe)) == cornersBefore;
        PathFollow.MoveInDirection = moveInDirection;
        game.MoveAlongPath(failing.Follower, 0.5f, "Walking", failing.Speed);
        game.MoveAlongPath(reference.Follower, 0.5f, "Walking", reference.Speed);
        check(beforeFailure == null && handedToGame && putBack && !PathFollow.IsActive && Difference(reference, failing) == null &&
              TurnedOff.Names().Contains("PathFollow"),
            "path follow: a failure after the transform was written puts the beaver, its index and its corners back, turns " +
            "the feature off (reported) and hands the move to the game's own method, which then moves it as without the mod");
        bool offRuns = PathFollow.MovePrefix(failing.Follower, 0.1f, "Walking", failing.Speed);
        check(offRuns && !PathFollow.IsActive && PathFollow.TakeStatsLine() == null,
            "path follow: once off it stays off, hands every move to the game and has nothing to say");

        // The settings page's "verify every feature" box reaches both new checks.
        Plugin.SetVerifyAll(true);
        bool allOn = PathFollow.VerifyEnabled && Reachability.VerifyEnabled;
        Plugin.SetVerifyAll(false);
        check(allOn && !PathFollow.VerifyEnabled && !Reachability.VerifyEnabled,
            "verify every feature: switches the path follow and reachability checks on and back to their settings");
    }

    private static Beaver MakeBeaver(INavigationService navigation, Vector3 position)
    {
        Beaver beaver = new Beaver
        {
            Transform = (Transform)RuntimeHelpers.GetUninitializedObject(typeof(Transform)),
            Animator = (MovementAnimator)RuntimeHelpers.GetUninitializedObject(typeof(MovementAnimator))
        };
        beaver.Follower = new PathFollower(navigation, beaver.Animator, beaver.Transform);
        Positions[beaver.Transform] = position;
        beaver.Follower.MovedAlongPath += (_, e) => beaver.Events.Add(
            $"{Describe(e.From)} -> {Describe(e.To)} -> {(e.Next.HasValue ? Describe(e.Next.Value) : "none")}");
        return beaver;
    }

    private static List<PathCorner> MakePath(Random random, Vector3 from)
    {
        float[] speeds = { 1f, 1f, 1f, 1f, 0.5f, 1.35f, 2f };
        int count = random.Next(2, 18);
        List<PathCorner> path = new List<PathCorner> { new PathCorner(from, 1f, 0) };
        Vector3 position = from;
        for (int i = 1; i < count; i++)
        {
            int kind = random.Next(12);
            Vector3 step = kind == 0 ? Vector3.zero
                : kind == 1 ? new Vector3(0.00004f, 0f, 0.00003f)
                : kind == 2 ? new Vector3(Next(random, -0.12, 0.12), 0f, Next(random, -0.12, 0.12))
                : new Vector3(Next(random, -2.5, 2.5), random.Next(3) == 0 ? Next(random, -1, 1) : 0f, Next(random, -2.5, 2.5));
            position += step;
            float speed = random.Next(14) == 0 ? float.MaxValue : speeds[random.Next(speeds.Length)];
            path.Add(new PathCorner(position, speed, random.Next(4)));
        }
        return path;
    }

    private static float Next(Random random, double min, double max)
    {
        return (float)(min + random.NextDouble() * (max - min));
    }

    private static readonly FieldInfo CornersField = typeof(PathFollower).GetField("_animatedPathCorners", Any);
    private static readonly FieldInfo IndexField = typeof(PathFollower).GetField("_nextCornerIndex", Any);
    private static readonly FieldInfo MovedField = typeof(PathFollower).GetField("_movedAlongPath", Any);

    private static List<AnimatedPathCorner> Corners(Beaver beaver)
    {
        return (List<AnimatedPathCorner>)CornersField.GetValue(beaver.Follower);
    }

    private static int CornerCount(Beaver beaver)
    {
        return Corners(beaver).Count;
    }

    // What differs between two beavers, bit for bit, or null.
    private static string Difference(Beaver expected, Beaver actual)
    {
        Vector3 a = Positions[expected.Transform], b = Positions[actual.Transform];
        if (!Same(a, b))
        {
            return $"position {Describe(b)}, the game's {Describe(a)}";
        }
        if ((int)IndexField.GetValue(expected.Follower) != (int)IndexField.GetValue(actual.Follower) ||
            (bool)MovedField.GetValue(expected.Follower) != (bool)MovedField.GetValue(actual.Follower))
        {
            return "corner index or moved flag";
        }
        string cornersA = string.Join(" | ", Corners(expected).Select(Describe)), cornersB = string.Join(" | ", Corners(actual).Select(Describe));
        if (cornersA != cornersB)
        {
            return $"animated corners {cornersB}, the game's {cornersA}";
        }
        Animations.TryGetValue(expected.Animator, out List<string> animA);
        Animations.TryGetValue(actual.Animator, out List<string> animB);
        if (!(animA ?? new List<string>()).SequenceEqual(animB ?? new List<string>()))
        {
            return "what the animator was handed";
        }
        if (!expected.Events.SequenceEqual(actual.Events))
        {
            return "MovedAlongPath events";
        }
        return null;
    }

    private static bool Same(Vector3 a, Vector3 b)
    {
        return BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x) &&
               BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y) &&
               BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z);
    }

    // Bit exact text: round-trip formats, and the float's bits for the parts that must match exactly.
    private static string Describe(Vector3 v)
    {
        return string.Format(CultureInfo.InvariantCulture, "({0:x8},{1:x8},{2:x8})", BitConverter.SingleToInt32Bits(v.x),
            BitConverter.SingleToInt32Bits(v.y), BitConverter.SingleToInt32Bits(v.z));
    }

    private static string Describe(AnimatedPathCorner corner)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0} t{1:x8} s{2:x8} d{3:x8} g{4}", Describe(corner.Position),
            BitConverter.SingleToInt32Bits(corner.Time), BitConverter.SingleToInt32Bits(corner.Speed),
            BitConverter.SingleToInt32Bits(corner.DistanceToPathCorner), corner.GroupId);
    }

    private static string Describe(PathCorner corner)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0} s{1:x8} g{2}", Describe(corner.Position),
            BitConverter.SingleToInt32Bits(corner.Speed), corner.GroupId);
    }

    // The copy: every instance method MoveAlongPath reaches (the static MoveInDirection is pure and called as it is),
    // each as a static dynamic method taking the follower first, calling the other copies, with Transform.position,
    // Time.time and MovementAnimator.AnimateMovementAlongPath sent to the stand-in above.
    private static GameCode CopyGameCode()
    {
        Type follower = typeof(PathFollower);
        string[] names =
        {
            "MoveAlongPath", "ReachedLastPathCorner", "GetSpeedLimitIfCloseToTarget", "GetMovementSpeed", "GetRemainingDistance",
            "GetTimeFromLastPathPoint", "AddAnimatedPathCorner", "AddSmoothingAnimatedPathCorner", "NotifyAfterMovement"
        };
        Dictionary<MethodBase, DynamicMethod> copies = new Dictionary<MethodBase, DynamicMethod>();
        foreach (string name in names)
        {
            MethodInfo original = follower.GetMethod(name, Any);
            Type[] parameters = new[] { follower }.Concat(original.GetParameters().Select(p => p.ParameterType)).ToArray();
            copies[original] = new DynamicMethod("Game." + name, original.ReturnType, parameters, follower, true);
        }
        Dictionary<MethodBase, MethodInfo> unity = new Dictionary<MethodBase, MethodInfo>
        {
            [typeof(Transform).GetProperty("position").GetGetMethod()] = typeof(PathFollowTests).GetMethod(nameof(GetPosition)),
            [typeof(Transform).GetProperty("position").GetSetMethod()] = typeof(PathFollowTests).GetMethod(nameof(SetPosition)),
            [typeof(Time).GetProperty("time").GetGetMethod()] = typeof(PathFollowTests).GetMethod(nameof(Now)),
            [typeof(MovementAnimator).GetMethod("AnimateMovementAlongPath")] = typeof(PathFollowTests).GetMethod(nameof(Animate))
        };
        GameCode code = new GameCode();
        foreach (KeyValuePair<MethodBase, DynamicMethod> copy in copies)
        {
            code.UnityCalls += IlCopy.Emit(copy.Key, copy.Value.GetILGenerator(), copies, unity);
        }
        MethodInfo Original(string name) => follower.GetMethod(name, Any);
        T Copy<T>(string name) where T : Delegate => (T)copies[Original(name)].CreateDelegate(typeof(T));
        code.MoveAlongPath = Copy<Action<PathFollower, float, string, Func<float>>>("MoveAlongPath");
        code.SpeedLimitIfCloseToTarget = Copy<Func<PathFollower, float, Func<float>, float?>>("GetSpeedLimitIfCloseToTarget");
        code.TimeFromLastPathPoint = Copy<Func<PathFollower, float>>("GetTimeFromLastPathPoint");
        code.AddAnimatedPathCorner = Copy<Action<PathFollower, Vector3, float, float, int>>("AddAnimatedPathCorner");
        code.ReachedLastPathCorner = Copy<Func<PathFollower, bool>>("ReachedLastPathCorner");
        code.AddSmoothingAnimatedPathCorner = Copy<Action<PathFollower, float, bool, float, float>>("AddSmoothingAnimatedPathCorner");
        return code;
    }
}

// Copies a method's IL into an ILGenerator, instruction for instruction, sending some calls elsewhere: to other copies
// (an instance method becomes a static one with the instance first, so the stack is the same) or to stand-ins with the
// same stack shape. Short branches are emitted in their long form. Methods with exception handlers are refused.
internal static class IlCopy
{
    private static readonly Dictionary<short, OpCode> Table = BuildTable();
    private static readonly Dictionary<string, OpCode> ByName = BuildByName();

    // Returns how many calls went to `redirects`.
    public static int Emit(MethodBase original, ILGenerator il, IReadOnlyDictionary<MethodBase, DynamicMethod> copies,
        IReadOnlyDictionary<MethodBase, MethodInfo> redirects)
    {
        MethodBody body = original.GetMethodBody();
        if (body == null || body.ExceptionHandlingClauses.Count > 0)
        {
            throw new NotSupportedException(original.Name + ": no body, or exception handlers");
        }
        foreach (LocalVariableInfo local in body.LocalVariables)
        {
            il.DeclareLocal(local.LocalType, local.IsPinned);
        }
        byte[] code = body.GetILAsByteArray();
        Module module = original.Module;
        List<(int Offset, OpCode OpCode, object Operand)> instructions = new List<(int, OpCode, object)>();
        Dictionary<int, Label> labels = new Dictionary<int, Label>();
        int i = 0;
        while (i < code.Length)
        {
            int offset = i;
            short value = code[i++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | code[i++]);
            }
            OpCode opCode = Table[value];
            object operand = null;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                    operand = i + 1 + (sbyte)code[i];
                    i += 1;
                    break;
                case OperandType.InlineBrTarget:
                    operand = i + 4 + BitConverter.ToInt32(code, i);
                    i += 4;
                    break;
                case OperandType.ShortInlineI:
                    operand = opCode == OpCodes.Ldc_I4_S ? (object)(sbyte)code[i] : code[i];
                    i += 1;
                    break;
                case OperandType.ShortInlineVar:
                    operand = code[i];
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    operand = BitConverter.ToInt16(code, i);
                    i += 2;
                    break;
                case OperandType.InlineI:
                    operand = BitConverter.ToInt32(code, i);
                    i += 4;
                    break;
                case OperandType.InlineI8:
                    operand = BitConverter.ToInt64(code, i);
                    i += 8;
                    break;
                case OperandType.ShortInlineR:
                    operand = BitConverter.ToSingle(code, i);
                    i += 4;
                    break;
                case OperandType.InlineR:
                    operand = BitConverter.ToDouble(code, i);
                    i += 8;
                    break;
                case OperandType.InlineMethod:
                    operand = module.ResolveMethod(BitConverter.ToInt32(code, i));
                    i += 4;
                    break;
                case OperandType.InlineField:
                    operand = module.ResolveField(BitConverter.ToInt32(code, i));
                    i += 4;
                    break;
                case OperandType.InlineType:
                    operand = module.ResolveType(BitConverter.ToInt32(code, i));
                    i += 4;
                    break;
                case OperandType.InlineTok:
                    operand = module.ResolveMember(BitConverter.ToInt32(code, i));
                    i += 4;
                    break;
                case OperandType.InlineString:
                    operand = module.ResolveString(BitConverter.ToInt32(code, i));
                    i += 4;
                    break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(code, i);
                    int next = i + 4 + 4 * count;
                    int[] targets = new int[count];
                    for (int k = 0; k < count; k++)
                    {
                        targets[k] = next + BitConverter.ToInt32(code, i + 4 + 4 * k);
                    }
                    operand = targets;
                    i = next;
                    break;
                default:
                    throw new NotSupportedException($"{original.Name}: operand type {opCode.OperandType}");
            }
            instructions.Add((offset, opCode, operand));
            if (opCode.OperandType == OperandType.ShortInlineBrTarget || opCode.OperandType == OperandType.InlineBrTarget)
            {
                labels[(int)operand] = default;
            }
            else if (operand is int[] switchTargets)
            {
                foreach (int target in switchTargets)
                {
                    labels[target] = default;
                }
            }
        }
        foreach (int target in labels.Keys.ToList())
        {
            labels[target] = il.DefineLabel();
        }

        int redirected = 0;
        foreach ((int offset, OpCode opCode, object operand) in instructions)
        {
            if (labels.TryGetValue(offset, out Label here))
            {
                il.MarkLabel(here);
            }
            switch (operand)
            {
                case null:
                    il.Emit(opCode);
                    break;
                case int target when opCode.OperandType == OperandType.ShortInlineBrTarget || opCode.OperandType == OperandType.InlineBrTarget:
                    il.Emit(opCode.OperandType == OperandType.ShortInlineBrTarget ? ByName[opCode.Name.Substring(0, opCode.Name.Length - 2)] : opCode,
                        labels[target]);
                    break;
                case int[] switchTargets:
                    il.Emit(opCode, switchTargets.Select(target => labels[target]).ToArray());
                    break;
                case sbyte shortInteger:
                    il.Emit(opCode, shortInteger);
                    break;
                case byte index:
                    il.Emit(opCode, index);
                    break;
                case short longIndex:
                    il.Emit(opCode, longIndex);
                    break;
                case int integer:
                    il.Emit(opCode, integer);
                    break;
                case long longInteger:
                    il.Emit(opCode, longInteger);
                    break;
                case float single:
                    il.Emit(opCode, single);
                    break;
                case double real:
                    il.Emit(opCode, real);
                    break;
                case string text:
                    il.Emit(opCode, text);
                    break;
                case FieldInfo field:
                    il.Emit(opCode, field);
                    break;
                case Type type:
                    il.Emit(opCode, type);
                    break;
                case MethodBase method when redirects.TryGetValue(method, out MethodInfo standIn):
                    il.Emit(OpCodes.Call, standIn);
                    redirected++;
                    break;
                case MethodBase method when copies.TryGetValue(method, out DynamicMethod copy):
                    il.Emit(OpCodes.Call, copy);
                    break;
                case ConstructorInfo constructor:
                    il.Emit(opCode, constructor);
                    break;
                case MethodInfo method:
                    il.Emit(opCode, method);
                    break;
                default:
                    throw new NotSupportedException($"{original.Name}: operand {operand}");
            }
        }
        return redirected;
    }

    private static Dictionary<short, OpCode> BuildTable()
    {
        Dictionary<short, OpCode> table = new Dictionary<short, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode opCode = (OpCode)field.GetValue(null);
            table[opCode.Value] = opCode;
        }
        return table;
    }

    private static Dictionary<string, OpCode> BuildByName()
    {
        Dictionary<string, OpCode> byName = new Dictionary<string, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode opCode = (OpCode)field.GetValue(null);
            byName[opCode.Name] = opCode;
        }
        return byName;
    }
}
