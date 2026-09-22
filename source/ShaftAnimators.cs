using System;
using System.Collections.Generic;
using HarmonyLib;
using Timberborn.MechanicalSystem;
using Timberborn.TimbermeshAnimations;
using Timberborn.TimeSystem;

namespace LateGamePerformance
{
    // Every tick ModularShaftAnimatorUpdater goes through every finished shaft and ModularShaftAnimator.UpdateAnimation
    // switches its animators on or off and, while it is powered, sets each animator's speed to the node's power
    // efficiency times NonlinearAnimationManager.SpeedMultiplier. That getter reads Time.timeScale three times and takes
    // a Mathf.Pow on every call, and it is called once per animator (49 us per tick for the singleton in the logged
    // colony). The time scale cannot change inside one Tick call: the game sets it only in SpeedManager's
    // LateUpdateSingleton (from the speed buttons), the main menu and the scene loader, and a shaft's update only
    // assigns IsAnimated and its animators' Enabled and Speed. So the multiplier is taken once per Tick call, from the
    // game's own getter on the first powered animator, and used for every animator of that call: the same float, the
    // same assignments in the same order, the same IsAnimated. Outside the tick (a shaft's model rebuilt, a shaft
    // finished) the game's own method runs.
    //
    // Presentation only: an animator's speed is how fast a model turns on screen, and it already differs between
    // players at different game speeds.
    internal static class ShaftAnimators
    {
        private const string UpdaterType = "Timberborn.ModularShafts.ModularShaftAnimatorUpdater";
        private const string AnimatorType = "Timberborn.ModularShafts.ModularShaftAnimator";

        private static Func<object, MechanicalNode> _node;
        private static Func<object, List<IAnimator>> _animators;
        private static Func<object, NonlinearAnimationManager> _manager;
        private static Action<object, bool> _setIsAnimated;

        // Swappable for the test harness: the game's getter reads Time.timeScale, which needs Unity.
        internal static Func<NonlinearAnimationManager, float> SpeedMultiplierOf = manager => manager.SpeedMultiplier;

        private static bool _active;
        private static bool _inTick;
        private static NonlinearAnimationManager _multiplierOf;
        private static float _multiplier;
        private static long _ticks;
        private static long _shafts;
        private static long _speeds;

        public static Feature CreateFeature()
        {
            Type self = typeof(ShaftAnimators);
            Feature feature = new Feature { Name = "ShaftAnimators" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "ModularShaftAnimatorUpdater.Tick",
                Required = true,
                Target = () => Reflect.Method(UpdaterType, "Tick"),
                Prefix = Reflect.Own(self, nameof(TickPrefix)),
                Finalizer = Reflect.Own(self, nameof(TickFinalizer))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "ModularShaftAnimator.UpdateAnimation",
                Required = true,
                Target = () =>
                {
                    Type shaft = Reflect.GameType(AnimatorType);
                    _node = Reflect.FieldGetter<MechanicalNode>(shaft, "_mechanicalNode");
                    _animators = Reflect.FieldGetter<List<IAnimator>>(shaft, "_animators");
                    _manager = Reflect.FieldGetter<NonlinearAnimationManager>(shaft, "_nonlinearAnimationManager");
                    _setIsAnimated = Reflect.InstanceCall<Action<object, bool>>(Reflect.Setter(shaft, "IsAnimated"));
                    return Reflect.Method(AnimatorType, "UpdateAnimation");
                },
                Prefix = Reflect.Own(self, nameof(UpdateAnimationPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _inTick = false;
            _multiplierOf = null;
            _active = true;
        }

        internal static bool IsActive => _active;

        public static string TakeStatsLine()
        {
            if (!_active || _ticks == 0)
            {
                return null;
            }
            string line = $"ShaftAnimators: {_ticks} shaft animator ticks updated {_shafts} shafts and set {_speeds} animator " +
                          "speeds from one speed multiplier per tick (the game computes it for each animator)";
            _ticks = _shafts = _speeds = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        internal static void TickPrefix()
        {
            // A new tick: the multiplier is taken again, from the first powered animator.
            _multiplierOf = null;
            _inTick = _active;
            if (_active)
            {
                _ticks++;
            }
        }

        internal static void TickFinalizer()
        {
            _inTick = false;
            _multiplierOf = null;
        }

        [HarmonyPriority(Priority.Last)]
        internal static bool UpdateAnimationPrefix(object __instance)
        {
            if (!_active || !_inTick)
            {
                return true;
            }
            try
            {
                // The game's method, line for line, with the multiplier taken once per tick.
                MechanicalNode node = _node(__instance);
                bool animated = node.ActiveAndPowered && node.PowerEfficiency > 0f;
                _setIsAnimated(__instance, animated);
                List<IAnimator> animators = _animators(__instance);
                foreach (IAnimator animator in animators)
                {
                    animator.Enabled = animated;
                    if (animated)
                    {
                        float efficiency = node.PowerEfficiency;
                        animator.Speed = efficiency * Multiplier(_manager(__instance));
                        _speeds++;
                    }
                }
                _shafts++;
                return false;
            }
            catch (Exception exception)
            {
                // Every assignment above is one the game's method makes again, with the same values.
                _active = false;
                _inTick = false;
                Log.Warning("ShaftAnimators failed and is off for this session: " + exception);
                return true;
            }
        }
        // ReSharper restore InconsistentNaming

        private static float Multiplier(NonlinearAnimationManager manager)
        {
            if (!ReferenceEquals(manager, _multiplierOf) || manager == null)
            {
                _multiplier = SpeedMultiplierOf(manager);
                _multiplierOf = manager;
            }
            return _multiplier;
        }
    }
}
