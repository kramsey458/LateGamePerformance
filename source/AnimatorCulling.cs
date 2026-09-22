using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LateGamePerformance
{
    // Every frame the game advances every Timbermesh animator in the colony and writes its pose: node animators
    // move child transforms, vertex animators set a material time. That is done whether or not the object is on
    // screen, and however small it is on screen: 1.3 ms per frame in a 360-beaver colony seen from above, the
    // largest per-frame system of the game. For an animator none of whose renderers is visible (Unity counts
    // shadow casters as visible), this keeps the game's own time-keeping (Time, RepeatedTime, PlayingFinished,
    // the AnimationChanged event, the wonder's saved animation time) exactly as it is and leaves out only the
    // pose writes. When the object comes back into view its pose is written again from the time it would have
    // had anyway; on the single frame in which it first reappears it can still show the pose it had when it left
    // the screen, because Unity decides visibility after the update. An animation that plays once and finishes
    // off screen gets its final pose written at that moment, since the game never updates a finished animation
    // again.
    //
    // Since 0.4.25 the same is done by distance (AnimatorLod): an animator farther from the camera than
    // AnimatorLodDistance has its pose written every second frame, and beyond twice that distance every fourth,
    // spread over the frames so no frame writes them all. Time still advances every frame, so the pose written is
    // the one the game would write on that frame; at that size on screen a pose a frame or three old is not
    // visible. Anything close to the camera is written every frame as before.
    //
    // Rendering only. The simulation reads animator time, never a node transform or a material.
    internal static class AnimatorCulling
    {
        private const string AnimatorType = "Timberborn.TimbermeshAnimations.TimbermeshAnimator";
        private const int RefreshRenderersEveryFrames = 600;
        private const int RefreshPositionEveryFrames = 60;

        private sealed class Entry
        {
            public Renderer[] Renderers;
            public int RefreshFrame;
            public Vector3 Position;
            public int PositionFrame = -1;
            public int Hash;
        }

        private static Func<object, bool> _enabled;
        private static Func<object, bool> _playingFinished;
        private static Func<object, float> _speed;
        private static Func<object, object> _currentAnimation;
        private static Action<object, float> _updateTime;
        private static Action<object> _writePose;
        private static readonly ConditionalWeakTable<object, Entry> Entries = new ConditionalWeakTable<object, Entry>();

        // Swappable for the test harness, which has no Unity.
        internal static Func<int> FrameCount = () => Time.frameCount;

        private static bool _active;
        private static bool _lod;
        private static float _lodDistance = 80f;
        private static int _cameraFrame = -1;
        private static bool _hasCamera;
        private static Vector3 _cameraPosition;
        private static long _updated;
        private static long _skipped;
        private static long _skippedFar;

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
                    _writePose = Reflect.InstanceCall<Action<object>>(Reflect.Method(AnimatorType, "UpdateAnimationUpdaters"));
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

        public static void ConfigureLod(bool enabled, float distance)
        {
            _lod = enabled && distance > 0;
            _lodDistance = Math.Max(1f, distance);
        }

        // How many frames apart the pose of an object at this distance is written: every frame up to the distance,
        // every second frame beyond it, every fourth beyond twice it.
        internal static int PoseInterval(float distance, float lodDistance)
        {
            if (distance < lodDistance)
            {
                return 1;
            }
            return distance < 2f * lodDistance ? 2 : 4;
        }

        // Whether this frame is one of the object's own. The hash spreads objects over the frames.
        internal static bool PoseDue(int frame, int hash, int interval)
        {
            return interval <= 1 || (frame + (hash & 0x7fffffff)) % interval == 0;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _updated + _skipped + _skippedFar == 0)
            {
                return null;
            }
            string line = $"AnimatorCulling: {_skipped} of {_updated + _skipped + _skippedFar} animator updates left out because no " +
                          $"part of the object was on screen, and {_skippedFar} pose writes left out for objects far from the " +
                          "camera; their time kept running";
            _updated = _skipped = _skippedFar = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
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
                int frame = FrameCount();
                Entry entry = Entries.GetValue(__instance, _ => new Entry { Hash = RuntimeHelpers.GetHashCode(_) });
                if (!AnyRendererVisible(__instance, entry, frame))
                {
                    SkipPose(__instance, deltaTime);
                    _skipped++;
                    return false;
                }
                if (_lod && !PoseDueByDistance(__instance, entry, frame))
                {
                    SkipPose(__instance, deltaTime);
                    _skippedFar++;
                    return false;
                }
                _updated++;
                return true;
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("AnimatorCulling failed and is off for this session: " + exception);
                return true;
            }
        }
        // ReSharper restore InconsistentNaming

        // The game's time-keeping without the pose, plus the final pose of an animation that just finished.
        private static void SkipPose(object animator, float deltaTime)
        {
            _updateTime(animator, deltaTime);
            if (_playingFinished(animator))
            {
                // Played once and just finished: the game will not touch it again, so its last pose is written
                // now (the wonder, the working-hours bell).
                _writePose(animator);
            }
        }

        private static bool PoseDueByDistance(object animator, Entry entry, int frame)
        {
            if (frame != _cameraFrame)
            {
                _cameraFrame = frame;
                Camera camera = Camera.main;
                _hasCamera = camera != null;
                if (_hasCamera)
                {
                    _cameraPosition = camera.transform.position;
                }
            }
            if (!_hasCamera)
            {
                return true;
            }
            if (entry.PositionFrame < 0 || frame - entry.PositionFrame >= RefreshPositionEveryFrames)
            {
                entry.Position = ((Component)animator).transform.position;
                entry.PositionFrame = frame;
            }
            float distance = Vector3.Distance(entry.Position, _cameraPosition);
            return PoseDue(frame, entry.Hash, PoseInterval(distance, _lodDistance));
        }

        private static bool AnyRendererVisible(object animator, Entry entry, int frame)
        {
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
