using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LateGamePerformance
{
    // Every frame the game advances every Timbermesh animator in the colony and writes its pose: node animators
    // move child transforms, vertex animators set a material time. That is done whether or not the object is on
    // screen, about 1.1 ms per frame in a 354-beaver colony and twice that in the working day when every beaver is
    // out walking. For an animator none of whose renderers is visible (Unity counts shadow casters as visible),
    // this keeps the game's own time-keeping (Time, RepeatedTime, PlayingFinished, the AnimationChanged event, the
    // wonder's saved animation time) exactly as it is and leaves out only the pose writes. When the object comes
    // back into view its pose is written again on that frame's update, from the time it would have had anyway.
    //
    // Rendering only. The simulation reads animator time, never a node transform or a material.
    internal static class AnimatorCulling
    {
        private const string AnimatorType = "Timberborn.TimbermeshAnimations.TimbermeshAnimator";
        private const int RefreshRenderersEveryFrames = 600;

        private sealed class Entry
        {
            public Renderer[] Renderers;
            public int RefreshFrame;
        }

        private static Func<object, bool> _enabled;
        private static Func<object, bool> _playingFinished;
        private static Func<object, float> _speed;
        private static Func<object, object> _currentAnimation;
        private static Action<object, float> _updateTime;
        private static readonly ConditionalWeakTable<object, Entry> Entries = new ConditionalWeakTable<object, Entry>();

        // Swappable for the test harness, which has no Unity.
        internal static Func<int> FrameCount = () => Time.frameCount;

        private static bool _active;
        private static long _updated;
        private static long _skipped;

        public static Feature CreateFeature()
        {
            Feature feature = new Feature { Name = "AnimatorCulling" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "TimbermeshAnimator.UpdateAnimation",
                Required = true,
                Target = () =>
                {
                    Type animator = Reflect.GameType(AnimatorType);
                    _enabled = Reflect.PropertyGetter<bool>(animator, "Enabled");
                    _playingFinished = Reflect.PropertyGetter<bool>(animator, "PlayingFinished");
                    _speed = Reflect.FieldGetter<float>(animator, "_speed");
                    _currentAnimation = Reflect.FieldGetter<object>(animator, "_currentAnimation");
                    _updateTime = Reflect.InstanceCall<Action<object, float>>(Reflect.Method(AnimatorType, "UpdateTime"));
                    return Reflect.Method(AnimatorType, "UpdateAnimation");
                },
                Prefix = Reflect.Own(typeof(AnimatorCulling), nameof(UpdatePrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            _active = true;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _updated + _skipped == 0)
            {
                return null;
            }
            string line = $"AnimatorCulling: {_skipped} of {_updated + _skipped} animator updates left out because no part of " +
                          "the object was on screen; their time kept running";
            _updated = _skipped = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        private static bool UpdatePrefix(object __instance, float deltaTime)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                // The game's own guard: when it would do nothing, let it do nothing.
                if (!_enabled(__instance) || _speed(__instance) == 0f || _currentAnimation(__instance) == null ||
                    _playingFinished(__instance))
                {
                    return true;
                }
                if (AnyRendererVisible(__instance))
                {
                    _updated++;
                    return true;
                }
                _updateTime(__instance, deltaTime);
                _skipped++;
                return false;
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("AnimatorCulling failed and is off for this session: " + exception);
                return true;
            }
        }
        // ReSharper restore InconsistentNaming

        private static bool AnyRendererVisible(object animator)
        {
            int frame = FrameCount();
            Entry entry = Entries.GetValue(animator, _ => new Entry());
            Renderer[] renderers = entry.Renderers;
            if (renderers == null || frame >= entry.RefreshFrame || AnyDestroyed(renderers))
            {
                renderers = ((Component)animator).GetComponentsInChildren<Renderer>(true);
                entry.Renderers = renderers;
                entry.RefreshFrame = frame + RefreshRenderersEveryFrames;
            }
            if (renderers.Length == 0)
            {
                // Nothing to look at, so nothing to judge by: keep the game's behaviour.
                return true;
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].isVisible)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AnyDestroyed(Renderer[] renderers)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
