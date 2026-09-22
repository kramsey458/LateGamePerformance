using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace LateGamePerformance
{
    // Every tick, every walking beaver is moved along its path by WalkerMover.Move, which hands the path follower a
    // method to ask for the walking speed at the current position: `_walkerSpeedManager
    // .GetWalkerSpeedAtCurrentPosition`, a method group that C# turns into a new delegate object on every call.
    // Several hundred walking beavers, eleven ticks a second: about a hundred kilobytes of garbage a second for
    // the same delegate over and over. The mod makes that delegate once per beaver and passes the same one every
    // tick; the calls, their arguments and their order are the game's own. Nothing the simulation computes
    // changes. MoveAlongPath is called through its Harmony-patched entry point, so the path follower's own
    // replacement (PathFollow) and any other mod's patches on it run as they would for the game's call; the kept
    // delegate is the speed provider PathFollow asks at every corner.
    internal static class WalkerMove
    {
        private const string MoverType = "Timberborn.WalkingSystem.WalkerMover";

        private static Func<object, object> _enterer;
        private static Func<object, object> _walker;
        private static Func<object, object> _speedManager;
        private static Func<object, object> _tickService;
        private static Func<object, bool> _isInside;
        private static Action<object> _exit;
        private static Func<object, object> _pathFollower;
        private static Func<object, float> _tickInterval;
        private static Action<object, float, string, Func<float>> _moveAlongPath;
        private static System.Reflection.MethodInfo _speedMethod;
        private static string _walkingAnimation;
        private static readonly ConditionalWeakTable<object, Func<float>> Speeds = new ConditionalWeakTable<object, Func<float>>();

        private static bool _active;
        private static long _moves;
        private static long _delegatesMade;

        public static Feature CreateFeature()
        {
            Feature feature = new Feature { Name = "WalkerMove" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "WalkerMover.Move",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return Reflect.Method(MoverType, "Move");
                },
                Prefix = Reflect.Own(typeof(WalkerMove), nameof(MovePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static bool IsActive => _active;

        internal static void Bind()
        {
            Type mover = Reflect.GameType(MoverType);
            Type enterer = Reflect.GameType("Timberborn.EnterableSystem.Enterer");
            Type walker = Reflect.GameType("Timberborn.WalkingSystem.Walker");
            Type follower = Reflect.GameType("Timberborn.CharacterMovementSystem.PathFollower");
            Type speedManager = Reflect.GameType("Timberborn.WalkingSystem.WalkerSpeedManager");
            Type tickService = Reflect.GameType("Timberborn.TickSystem.ITickService");
            if (mover == null || enterer == null || walker == null || follower == null || speedManager == null || tickService == null)
            {
                throw new TypeLoadException("walking types not found");
            }
            _enterer = Reflect.FieldGetter<object>(mover, "_enterer");
            _walker = Reflect.FieldGetter<object>(mover, "_walker");
            _speedManager = Reflect.FieldGetter<object>(mover, "_walkerSpeedManager");
            _tickService = Reflect.FieldGetter<object>(mover, "_tickService");
            _isInside = Reflect.PropertyGetter<bool>(enterer, "IsInside");
            _exit = Reflect.InstanceCall<Action<object>>(AccessTools.Method(enterer, "Exit"));
            _pathFollower = Reflect.PropertyGetter<object>(walker, "PathFollower");
            _tickInterval = Reflect.PropertyGetter<float>(tickService, "TickIntervalInSeconds");
            _moveAlongPath = Reflect.InstanceCall<Action<object, float, string, Func<float>>>(
                AccessTools.Method(follower, "MoveAlongPath", new[] { typeof(float), typeof(string), typeof(Func<float>) }));
            _speedMethod = AccessTools.Method(speedManager, "GetWalkerSpeedAtCurrentPosition");
            System.Reflection.FieldInfo animation = AccessTools.Field(mover, "WalkingAnimation");
            _walkingAnimation = animation?.GetValue(null) as string;
            if (_speedMethod == null || _speedMethod.ReturnType != typeof(float) || _walkingAnimation == null)
            {
                throw new MissingMemberException("WalkerMover", "WalkingAnimation / GetWalkerSpeedAtCurrentPosition");
            }
        }

        public static string TakeStatsLine()
        {
            if (!_active && _moves == 0)
            {
                return null;
            }
            string line = $"WalkerMove: {_moves} beaver moves with a kept speed delegate ({_delegatesMade} delegates made)";
            _moves = _delegatesMade = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool MovePrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            object enterer, tickService, walker;
            Func<float> speed;
            try
            {
                enterer = _enterer(__instance);
                tickService = _tickService(__instance);
                walker = _walker(__instance);
                if (!Speeds.TryGetValue(__instance, out speed))
                {
                    speed = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), _speedManager(__instance), _speedMethod);
                    Speeds.Add(__instance, speed);
                    _delegatesMade++;
                }
            }
            catch (Exception exception)
            {
                // Nothing has happened to the beaver yet; the game's own method runs.
                _active = false;
                Log.Warning("WalkerMove failed and is off for this session: " + exception);
                return true;
            }
            // The game's method from here, call for call; what it throws, the game would have thrown.
            _moves++;
            if (_isInside(enterer))
            {
                _exit(enterer);
            }
            else
            {
                _moveAlongPath(_pathFollower(walker), _tickInterval(tickService), _walkingAnimation, speed);
            }
            return false;
        }
        // ReSharper restore InconsistentNaming
    }
}
