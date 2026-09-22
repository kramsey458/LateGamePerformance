using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Timberborn.CharacterMovementSystem;
using Timberborn.Navigation;
using UnityEngine;

namespace LateGamePerformance
{
    // Every tick, every walking beaver is moved along its path by PathFollower.MoveAlongPath, in steps of at most a
    // tenth of a tile (MaxMovementStep): about twenty steps per beaver per tick. Each step reads the beaver's Unity
    // transform twice (the position to move from, and ReachedLastPathCorner's "is it there yet") and writes it once
    // (AddAnimatedPathCorner), and a write moves the whole beaver: the model, the status icons and their colliders.
    // In the 0.4.23 session WalkerMover took 2.1 ms per tick, about 26 us per beaver that moved.
    //
    // The mod runs the game's loop with the position kept in a local instead of in the transform. Between two steps
    // the game's loop only does arithmetic: the static MoveInDirection, the navigation service's stopping-proximity
    // compare (a squared distance), Vector3.Distance, a list add; the one call that reads the transform from outside
    // is the speed provider (WalkerSpeedManager.GetWalkerSpeedAtCurrentPosition looks up the water at the beaver's
    // tile), called when a corner is reached. So the transform is written right before every such call, read back
    // right after it (a provider that moved the beaver is followed, as the game's next read would follow it), and
    // written once after the loop, before the game's own AddSmoothingAnimatedPathCorner, the animator and the
    // MovedAlongPath event, which all see the beaver where the game's loop leaves it.
    //
    // Why the result is the game's to the bit:
    //   - Everything before the loop is the game's own private methods (the speed limit near the target, the
    //     movement speed, the time of the last path point), called with the real transform, as the game calls them.
    //   - The loop is the game's, statement for statement, in the same order, with the same calls: the game's own
    //     MoveInDirection (through a delegate to the private method), InStoppingProximity, Vector3.Distance and
    //     GetMovementSpeed, and the one expression it computes itself, `timeFromLastPathPoint + tickDeltaTime -
    //     remainingTime`, written the same way. Only where the game reads the transform the mod reads its local.
    //   - That local holds exactly what the transform would: a beaver's transform is the root of its entity (the
    //     game instantiates every entity without a parent, EntityService.Instantiate), and for a root transform
    //     Unity returns the floats it was given. A beaver with a parent (a wonder's pilot) is left to the game's own
    //     method. PathFollowVerify checks exactly this in the game.
    //   - Nothing reads the transform in between but the provider, which is handed the right one; Unity runs no code
    //     of anyone's when a position is written. The corners go into a list of the mod's own and are copied into
    //     the game's list after the loop, so the game's list, index and transform are untouched if anything fails.
    //   - A transpiler on MoveAlongPath, or any patch by another mod on the two helpers the loop no longer calls
    //     (AddAnimatedPathCorner, ReachedLastPathCorner), would not run in the mod's loop: then every move is left to
    //     the game's method, with one log line. That is decided by which mods are installed, alike on every computer.
    //
    // PathFollowVerify runs the game's loop first, through the game's own AddAnimatedPathCorner, ReachedLastPathCorner
    // and AddSmoothingAnimatedPathCorner, writing and reading the real transform as the game does, puts the beaver
    // back, then runs the mod's loop for real and compares the position, the corner index and every animated corner
    // bit for bit. The mod's result is the one kept; the animator and the event run once.
    //
    // Simulation feature: always on, the same on every computer. Turns itself off on any exception before the corners
    // are handed over, puts the beaver back and hands the move to the game's own method.
    internal static class PathFollow
    {
        internal delegate Vector3 MoveInDirectionCall(Vector3 position, Vector3 target, float speed, ref float remainingTime,
            out bool reachedTarget);

        // The game's own methods, bound to PathFollower's private ones. The harness, which has no Unity, points the ones
        // that touch the transform or Unity's clock at copies of the game's code that use a stand-in.
        internal static Func<PathFollower, float, Func<float>, float?> SpeedLimitIfCloseToTarget;
        internal static Func<PathFollower, Func<float>, float> MovementSpeed;
        internal static Func<PathFollower, float> TimeFromLastPathPoint;
        internal static MoveInDirectionCall MoveInDirection;
        internal static Action<PathFollower, Vector3, float, float, int> AddAnimatedPathCorner;
        internal static Func<PathFollower, bool> ReachedLastPathCorner;
        internal static Action<PathFollower, float, bool, float, float> AddSmoothingAnimatedPathCorner;
        internal static Action<PathFollower> NotifyAfterMovement;
        internal static Func<Transform, Vector3> ReadPosition = transform => transform.position;
        internal static Action<Transform, Vector3> WritePosition = (transform, position) => transform.position = position;
        internal static Func<Transform, bool> IsRoot = transform => ReferenceEquals(transform.parent, null);
        internal static Action<MovementAnimator, List<AnimatedPathCorner>, string> Animate =
            (animator, corners, animationName) => animator.AnimateMovementAlongPath(corners, animationName);
        // The first patch by another mod the mod's loop would bypass, or null; swapped by the tests, where Harmony's
        // patch registry cannot run.
        internal static Func<string> ForeignPatch = FindForeignPatch;

        private static Func<object, Transform> _transformOf;
        private static Func<object, INavigationService> _navigationOf;
        private static Func<object, MovementAnimator> _animatorOf;
        private static Func<object, List<AnimatedPathCorner>> _cornersOf;
        private static Func<object, IReadOnlyList<PathCorner>> _pathOf;
        private static Func<object, int> _indexOf;
        private static Action<object, int> _setIndex;
        private static Action<object, bool> _setMoved;
        private static float _remainingTimeThreshold;
        private static MethodBase[] _bypassed = new MethodBase[0];
        private static MethodBase _moveAlongPath;

        // The mod's corners, copied into the game's list after the loop; the game's own loop's corners and the list as
        // it was before the move, for verify mode.
        private static readonly List<AnimatedPathCorner> Corners = new List<AnimatedPathCorner>(100);
        private static readonly List<AnimatedPathCorner> GamesCorners = new List<AnimatedPathCorner>(100);
        private static readonly List<AnimatedPathCorner> Before = new List<AnimatedPathCorner>(100);
        private static Vector3 _gamesEnd;
        private static int _gamesIndex;

        private static bool _active;
        private static bool _verify;
        private static bool _checkedPatches;
        private static bool _standDown;

        private static long _moves;
        private static long _steps;
        private static long _writes;
        private static long _gamesWrites;
        private static long _leftToGame;
        private static long _stopwatchTicks;
        private static long _verifyMismatches;

        internal static bool VerifyEnabled
        {
            get => _verify;
            set => _verify = value;
        }

        public static Feature CreateFeature(Config config)
        {
            _verify = config.PathFollowVerify;
            Feature feature = new Feature { Name = "PathFollow" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "PathFollower.MoveAlongPath",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return _moveAlongPath;
                },
                Prefix = Reflect.Own(typeof(PathFollow), nameof(MovePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static bool IsActive => _active;

        internal static void ResetForTests()
        {
            _checkedPatches = _standDown = false;
            _moves = _steps = _writes = _gamesWrites = _leftToGame = _stopwatchTicks = _verifyMismatches = 0;
        }

        // Resolves everything this feature touches; throws if the game no longer matches.
        internal static void Bind()
        {
            Type follower = typeof(PathFollower);
            _moveAlongPath = AccessTools.Method(follower, "MoveAlongPath", new[] { typeof(float), typeof(string), typeof(Func<float>) });
            if (_moveAlongPath == null)
            {
                throw new MissingMethodException(follower.Name, "MoveAlongPath");
            }
            _transformOf = Reflect.FieldGetter<Transform>(follower, "_transform");
            _navigationOf = Reflect.FieldGetter<INavigationService>(follower, "_navigationService");
            _animatorOf = Reflect.FieldGetter<MovementAnimator>(follower, "_movementAnimator");
            _cornersOf = Reflect.FieldGetter<List<AnimatedPathCorner>>(follower, "_animatedPathCorners");
            _pathOf = Reflect.FieldGetter<IReadOnlyList<PathCorner>>(follower, "_pathCorners");
            _indexOf = Reflect.FieldGetter<int>(follower, "_nextCornerIndex");
            _setIndex = Reflect.FieldSetter<int>(follower, "_nextCornerIndex");
            _setMoved = Reflect.FieldSetter<bool>(follower, "_movedAlongPath");
            SpeedLimitIfCloseToTarget = Open<Func<PathFollower, float, Func<float>, float?>>("GetSpeedLimitIfCloseToTarget");
            MovementSpeed = Open<Func<PathFollower, Func<float>, float>>("GetMovementSpeed");
            TimeFromLastPathPoint = Open<Func<PathFollower, float>>("GetTimeFromLastPathPoint");
            MoveInDirection = Open<MoveInDirectionCall>("MoveInDirection");
            AddAnimatedPathCorner = Open<Action<PathFollower, Vector3, float, float, int>>("AddAnimatedPathCorner");
            ReachedLastPathCorner = Open<Func<PathFollower, bool>>("ReachedLastPathCorner");
            AddSmoothingAnimatedPathCorner = Open<Action<PathFollower, float, bool, float, float>>("AddSmoothingAnimatedPathCorner");
            NotifyAfterMovement = Open<Action<PathFollower>>("NotifyAfterMovement");
            FieldInfo threshold = AccessTools.Field(follower, "RemainingTimeThreshold");
            if (threshold == null || !threshold.IsStatic || threshold.FieldType != typeof(float))
            {
                throw new MissingFieldException(follower.Name, "RemainingTimeThreshold");
            }
            _remainingTimeThreshold = (float)threshold.GetValue(null);
            _bypassed = new MethodBase[]
            {
                AccessTools.Method(follower, "AddAnimatedPathCorner"), AccessTools.Method(follower, "ReachedLastPathCorner")
            };
        }

        private static T Open<T>(string name) where T : Delegate
        {
            MethodInfo method = AccessTools.Method(typeof(PathFollower), name);
            if (method == null)
            {
                throw new MissingMethodException(nameof(PathFollower), name);
            }
            return (T)Delegate.CreateDelegate(typeof(T), method);
        }

        public static string TakeStatsLine()
        {
            if (!_active && _moves == 0 && _leftToGame == 0)
            {
                return null;
            }
            double ms = _stopwatchTicks * 1000.0 / Stopwatch.Frequency;
            string line = string.Format(CultureInfo.InvariantCulture,
                "PathFollow: {0} moves along a path, {1:0.0} steps each, in {2:0.0} ms ({3:0.00} us each); the transform " +
                "was written {4} times where the game writes it {5} times; {6} moves left to the game (a parent " +
                "transform or another mod's patch){7}",
                _moves, _moves > 0 ? (double)_steps / _moves : 0, ms, _moves > 0 ? ms * 1000.0 / _moves : 0, _writes,
                _gamesWrites, _leftToGame, _verify ? $"; verify mismatches {_verifyMismatches}" : "");
            _moves = _steps = _writes = _gamesWrites = _leftToGame = _stopwatchTicks = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool MovePrefix(PathFollower __instance, float tickDeltaTime, string animationName,
            Func<float> movementSpeedProvider)
        {
            if (!_active)
            {
                return true;
            }
            if (!_checkedPatches)
            {
                // Once, at the first move, when every mod has patched what it patches.
                _checkedPatches = true;
                string patched = ForeignPatch();
                if (patched != null)
                {
                    _standDown = true;
                    Log.Info($"PathFollow: another mod patches {patched}, which the mod's loop would not run. The game's " +
                             "own method moves every beaver. The result is the same.");
                }
            }
            if (_standDown)
            {
                _leftToGame++;
                return true;
            }
            Transform transform;
            try
            {
                transform = _transformOf(__instance);
                if (!IsRoot(transform))
                {
                    _leftToGame++;
                    return true;
                }
            }
            catch (Exception exception)
            {
                // Nothing has been touched.
                return Fail(exception, __instance, null, false, default, 0);
            }

            long started = Stopwatch.GetTimestamp();
            List<AnimatedPathCorner> corners = _cornersOf(__instance);
            Vector3 start = default;
            int startIndex = 0;
            bool touched = false;
            float num;
            float timeFromLastPathPoint;
            bool reachedTarget;
            Vector3 position;
            int index;
            int steps;
            try
            {
                // MoveAlongPath up to the loop: the game's own methods, with the real transform.
                float? speedLimit = SpeedLimitIfCloseToTarget(__instance, tickDeltaTime, movementSpeedProvider);
                num = speedLimit ?? MovementSpeed(__instance, movementSpeedProvider);
                IReadOnlyList<PathCorner> path = _pathOf(__instance);
                index = _indexOf(__instance);
                int groupId = path[index - 1].GroupId;
                timeFromLastPathPoint = TimeFromLastPathPoint(__instance);
                if (_verify)
                {
                    Before.Clear();
                    Before.AddRange(corners);
                }
                start = ReadPosition(transform);
                startIndex = index;
                touched = true;
                if (_verify)
                {
                    GamesWay(__instance, transform, tickDeltaTime, movementSpeedProvider, speedLimit, num, groupId,
                        timeFromLastPathPoint);
                    // Back to where the game's loop starts.
                    WritePosition(transform, start);
                    _setIndex(__instance, startIndex);
                }
                // AddAnimatedPathCorner(_transform.position, ...), whose write puts back what was read.
                position = start;
                Corners.Clear();
                AddCorner(Corners, path, index, position, timeFromLastPathPoint, num, groupId);
                steps = Walk(__instance, transform, tickDeltaTime, movementSpeedProvider, speedLimit, timeFromLastPathPoint,
                    ref position, ref index, ref num, out reachedTarget);
            }
            catch (Exception exception)
            {
                return Fail(exception, __instance, transform, touched, start, startIndex);
            }

            // The corners are handed over; from here on it is the game's own code in the game's order, and what throws
            // here, the game would have thrown at the same point.
            WritePosition(transform, position);
            _setIndex(__instance, index);
            corners.Clear();
            corners.AddRange(Corners);
            _writes++;
            _gamesWrites += steps + 1;
            AddSmoothingAnimatedPathCorner(__instance, tickDeltaTime, reachedTarget, timeFromLastPathPoint, num);
            if (_verify)
            {
                Compare(__instance, transform, corners);
            }
            Animate(_animatorOf(__instance), corners, animationName);
            _setMoved(__instance, true);
            _moves++;
            _steps += steps;
            _stopwatchTicks += Stopwatch.GetTimestamp() - started;
            NotifyAfterMovement(__instance);
            return false;
        }
        // ReSharper restore InconsistentNaming

        // The game's loop with the position in a local. Returns the steps taken; reachedTarget as the game's loop
        // leaves it (AddSmoothingAnimatedPathCorner reads it).
        private static int Walk(PathFollower follower, Transform transform, float tickDeltaTime, Func<float> movementSpeedProvider,
            float? speedLimit, float timeFromLastPathPoint, ref Vector3 position, ref int index, ref float num,
            out bool reachedTarget)
        {
            INavigationService navigation = _navigationOf(follower);
            IReadOnlyList<PathCorner> path = _pathOf(follower);
            float remainingTime = tickDeltaTime;
            reachedTarget = false;
            int steps = 0;
            // ReachedLastPathCorner(), with the local for _transform.position.
            while (remainingTime > _remainingTimeThreshold &&
                   !navigation.InStoppingProximity(path[path.Count - 1].Position, position))
            {
                if (reachedTarget)
                {
                    index = index + 1 < path.Count ? index + 1 : index;
                    if (speedLimit.HasValue)
                    {
                        num = speedLimit.GetValueOrDefault();
                    }
                    else
                    {
                        // GetMovementSpeed reads the corner index, and the speed provider the transform: both as the
                        // game has them at this point. Read back afterwards, as the game's next reads would be.
                        _setIndex(follower, index);
                        WritePosition(transform, position);
                        _writes++;
                        num = MovementSpeed(follower, movementSpeedProvider);
                        path = _pathOf(follower);
                        index = _indexOf(follower);
                        position = ReadPosition(transform);
                    }
                }
                int groupId = path[index - 1].GroupId;
                if (num < float.MaxValue)
                {
                    Vector3 target = path[index].Position;
                    Vector3 next = MoveInDirection(position, target, num, ref remainingTime, out reachedTarget);
                    float time = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                    AddCorner(Corners, path, index, next, time, num, groupId);
                    position = next;
                }
                else
                {
                    Vector3 target = path[index].Position;
                    float time = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                    AddCorner(Corners, path, index, target, time, num, groupId);
                    position = target;
                    reachedTarget = true;
                }
                steps++;
            }
            return steps;
        }

        // AddAnimatedPathCorner without the transform write.
        private static void AddCorner(List<AnimatedPathCorner> corners, IReadOnlyList<PathCorner> path, int index, Vector3 position,
            float time, float speed, int groupId)
        {
            float distanceToPathCorner = Vector3.Distance(position, path[index].Position);
            corners.Add(new AnimatedPathCorner(position, time, speed, distanceToPathCorner, groupId));
        }

        // Verify mode: MoveAlongPath's loop and its smoothing corner as the game runs them, through the game's own
        // AddAnimatedPathCorner, ReachedLastPathCorner and AddSmoothingAnimatedPathCorner, with every transform read and
        // write the game makes and the corner index in its field. The corners are kept in GamesCorners; the caller
        // puts the beaver and the index back.
        private static void GamesWay(PathFollower follower, Transform transform, float tickDeltaTime, Func<float> movementSpeedProvider,
            float? speedLimit, float num, int groupId, float timeFromLastPathPoint)
        {
            List<AnimatedPathCorner> corners = _cornersOf(follower);
            corners.Clear();
            float remainingTime = tickDeltaTime;
            bool reachedTarget = false;
            AddAnimatedPathCorner(follower, ReadPosition(transform), timeFromLastPathPoint, num, groupId);
            while (remainingTime > _remainingTimeThreshold && !ReachedLastPathCorner(follower))
            {
                if (reachedTarget)
                {
                    int current = _indexOf(follower);
                    _setIndex(follower, current + 1 < _pathOf(follower).Count ? current + 1 : current);
                    num = speedLimit ?? MovementSpeed(follower, movementSpeedProvider);
                }
                IReadOnlyList<PathCorner> path = _pathOf(follower);
                int index = _indexOf(follower);
                int groupId2 = path[index - 1].GroupId;
                if (num < float.MaxValue)
                {
                    Vector3 target = path[index].Position;
                    Vector3 next = MoveInDirection(ReadPosition(transform), target, num, ref remainingTime, out reachedTarget);
                    float time = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                    AddAnimatedPathCorner(follower, next, time, num, groupId2);
                }
                else
                {
                    Vector3 target = path[index].Position;
                    float time = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                    AddAnimatedPathCorner(follower, target, time, num, groupId2);
                    reachedTarget = true;
                }
            }
            AddSmoothingAnimatedPathCorner(follower, tickDeltaTime, reachedTarget, timeFromLastPathPoint, num);
            GamesCorners.Clear();
            GamesCorners.AddRange(corners);
            _gamesEnd = ReadPosition(transform);
            _gamesIndex = _indexOf(follower);
        }

        // Counted and logged only: the mod's move is the one the game keeps. A failure in here only switches the
        // comparison off.
        private static void Compare(PathFollower follower, Transform transform, List<AnimatedPathCorner> corners)
        {
            try
            {
                Vector3 end = ReadPosition(transform);
                int index = _indexOf(follower);
                string difference = null;
                if (!Same(end, _gamesEnd))
                {
                    difference = $"the beaver ends at {Describe(end)}, the game's loop at {Describe(_gamesEnd)}";
                }
                else if (index != _gamesIndex)
                {
                    difference = $"the next corner is {index}, the game's loop says {_gamesIndex}";
                }
                else if (corners.Count != GamesCorners.Count)
                {
                    difference = $"{corners.Count} animated corners, the game's loop made {GamesCorners.Count}";
                }
                else
                {
                    for (int i = 0; i < corners.Count && difference == null; i++)
                    {
                        AnimatedPathCorner mine = corners[i], games = GamesCorners[i];
                        if (!Same(mine.Position, games.Position) || !Same(mine.Time, games.Time) || !Same(mine.Speed, games.Speed) ||
                            !Same(mine.DistanceToPathCorner, games.DistanceToPathCorner) || mine.GroupId != games.GroupId)
                        {
                            difference = $"animated corner {i} of {corners.Count} is {Describe(mine)}, the game's loop made {Describe(games)}";
                        }
                    }
                }
                if (difference != null)
                {
                    _verifyMismatches++;
                    if (_verifyMismatches <= 10)
                    {
                        Log.Warning("PathFollow verify: a move differs from the game's own loop: " + difference + ".");
                    }
                }
            }
            catch (Exception exception)
            {
                _verify = false;
                Log.Warning("PathFollow verify failed and is off (the moves are not affected): " + exception);
            }
        }

        private static bool Same(float a, float b)
        {
            return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        }

        private static bool Same(Vector3 a, Vector3 b)
        {
            return Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z);
        }

        private static string Describe(Vector3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:R}, {1:R}, {2:R})", v.x, v.y, v.z);
        }

        private static string Describe(AnimatedPathCorner corner)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} at {1:R} speed {2:R} distance {3:R} group {4}",
                Describe(corner.Position), corner.Time, corner.Speed, corner.DistanceToPathCorner, corner.GroupId);
        }

        // Puts the beaver, its corner index and (in verify mode) its corners back as they were before the move, so the
        // game's own method, which runs now, starts from where it would have.
        private static bool Fail(Exception exception, PathFollower follower, Transform transform, bool touched, Vector3 start,
            int startIndex)
        {
            _active = false;
            string putBack = "";
            if (touched)
            {
                try
                {
                    WritePosition(transform, start);
                    _setIndex(follower, startIndex);
                    if (_verify)
                    {
                        List<AnimatedPathCorner> corners = _cornersOf(follower);
                        corners.Clear();
                        corners.AddRange(Before);
                    }
                }
                catch (Exception again)
                {
                    putBack = " (putting the beaver back failed too: " + again.Message + ")";
                }
            }
            TurnedOff.Report("PathFollow",
                "PathFollow failed and turned itself off for this session; the game's own method moves the beavers" + putBack +
                ": " + exception);
            return true;
        }

        private static string FindForeignPatch()
        {
            // The mod's loop calls neither of these, and replaces MoveAlongPath's body, which a transpiler would change.
            foreach (MethodBase method in _bypassed)
            {
                string owner = ForeignOwner(method, false);
                if (owner != null)
                {
                    return $"PathFollower.{method.Name} ({owner})";
                }
            }
            string transpiler = ForeignOwner(_moveAlongPath, true);
            return transpiler == null ? null : $"PathFollower.MoveAlongPath with a transpiler ({transpiler})";
        }

        private static string ForeignOwner(MethodBase method, bool transpilersOnly)
        {
            Patches info = method == null ? null : Harmony.GetPatchInfo(method);
            if (info == null)
            {
                return null;
            }
            List<Patch> patches = new List<Patch>(info.Transpilers);
            if (!transpilersOnly)
            {
                patches.AddRange(info.Prefixes);
                patches.AddRange(info.Postfixes);
                patches.AddRange(info.Finalizers);
            }
            foreach (Patch patch in patches)
            {
                if (!patch.owner.StartsWith(Plugin.HarmonyId, StringComparison.Ordinal))
                {
                    return patch.owner;
                }
            }
            return null;
        }
    }
}
