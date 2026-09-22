using System;
using Timberborn.CameraSystem;
using Timberborn.SoundSystem;
using UnityEngine;

namespace LateGamePerformance
{
    // Every frame the game places the audio listener: a ray from the screen centre is cast against the terrain and
    // against every block object to find what is under it, and the listener is moved a tenth of the way there.
    // About 0.3 ms per frame and 50 KB/s of garbage in a large colony, for an answer that only changes when the
    // camera moves or something is built under the screen centre. The listener is now placed when the camera
    // moved, while it is still gliding towards its target, and otherwise once every ten frames so that a change
    // under the screen centre is picked up within a fraction of a second.
    //
    // Sound only: nothing the simulation computes depends on the listener.
    internal static class SoundListenerSkip
    {
        // The pure rule, tested on its own.
        internal sealed class Policy
        {
            public const int RefreshEveryFrames = 10;
            public const float SettledDistance = 0.01f;

            private int _framesSinceRun = RefreshEveryFrames;
            private bool _settled;

            public bool ShouldRun(bool cameraMoved)
            {
                _framesSinceRun++;
                if (cameraMoved || !_settled || _framesSinceRun >= RefreshEveryFrames)
                {
                    _framesSinceRun = 0;
                    return true;
                }
                return false;
            }

            // How far the last placement moved the listener; a tiny step means it has reached its target.
            public void Ran(float listenerMoved)
            {
                _settled = listenerMoved <= SettledDistance;
            }
        }

        private const string SoundListenerType = "Timberborn.CoreSound.SoundListener";

        private static Func<object, object> _cameraService;
        private static Func<object, object> _soundSystem;
        private static Policy _policy = new Policy();
        private static bool _active;
        private static bool _hasCamera;
        private static Vector3 _cameraPosition;
        private static Quaternion _cameraRotation;
        private static int _screenWidth;
        private static int _screenHeight;
        private static bool _measuring;
        private static Vector3 _listenerBefore;
        private static long _ran;
        private static long _skipped;

        public static Feature CreateFeature()
        {
            Type self = typeof(SoundListenerSkip);
            Feature feature = new Feature { Name = "SoundListener" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoundListener.LateUpdateSingleton",
                Required = true,
                Target = () =>
                {
                    Type listener = Reflect.GameType(SoundListenerType);
                    _cameraService = Reflect.FieldGetter<object>(listener, "_cameraService");
                    _soundSystem = Reflect.FieldGetter<object>(listener, "_soundSystem");
                    return Reflect.Method(SoundListenerType, "LateUpdateSingleton");
                },
                Prefix = Reflect.Own(self, nameof(LateUpdatePrefix)),
                Postfix = Reflect.Own(self, nameof(LateUpdatePostfix))
            });
            return feature;
        }

        public static void Activate()
        {
            _policy = new Policy();
            _hasCamera = false;
            _active = true;
        }

        // A new scene has a new camera and listener.
        public static void SceneCreated()
        {
            _policy = new Policy();
            _hasCamera = false;
            _measuring = false;
        }

        public static string TakeStatsLine()
        {
            if (!_active || _ran + _skipped == 0)
            {
                return null;
            }
            string line = $"SoundListener: placed the audio listener in {_ran} of {_ran + _skipped} frames; in the rest " +
                          "the camera had not moved and the listener had settled";
            _ran = _skipped = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.Last)]
        private static bool LateUpdatePrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                Transform camera = ((CameraService)_cameraService(__instance)).Transform;
                Vector3 position = camera.position;
                Quaternion rotation = camera.rotation;
                int width = Screen.width;
                int height = Screen.height;
                bool moved = !_hasCamera || position != _cameraPosition || rotation != _cameraRotation ||
                             width != _screenWidth || height != _screenHeight;
                _cameraPosition = position;
                _cameraRotation = rotation;
                _screenWidth = width;
                _screenHeight = height;
                _hasCamera = true;
                if (!_policy.ShouldRun(moved))
                {
                    _skipped++;
                    return false;
                }
                _listenerBefore = ((ISoundSystem)_soundSystem(__instance)).ListenerPosition;
                _measuring = true;
                return true;
            }
            catch (Exception exception)
            {
                _active = false;
                _measuring = false;
                Log.Warning("SoundListener failed and is off for this session: " + exception);
                return true;
            }
        }

        private static void LateUpdatePostfix(object __instance)
        {
            if (!_measuring)
            {
                return;
            }
            _measuring = false;
            try
            {
                Vector3 after = ((ISoundSystem)_soundSystem(__instance)).ListenerPosition;
                _policy.Ran(Vector3.Distance(_listenerBefore, after));
                _ran++;
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("SoundListener failed and is off for this session: " + exception);
            }
        }
        // ReSharper restore InconsistentNaming
    }
}
