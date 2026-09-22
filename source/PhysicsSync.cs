using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace LateGamePerformance
{
    // Every frame the game calls Physics.SyncTransforms() (TransformSyncServiceUnityAdapter.LateUpdate), which hands
    // every transform moved since the last sync to the physics engine's collider structures. The game asks physics
    // one question only: the selection raycast, SelectableObjectRaycaster's private TryHitSelectableObject, the one
    // Physics.Raycast in its code, which the cursor (on a click), the building and placement tools, zipline and
    // duplication tools and a few others reach through its public overloads (BeaverBuddies' pointer too). Nothing
    // has a Rigidbody, nothing calls Physics.Simulate and nothing reads a collider otherwise. But every walking beaver
    // and every status icon carries a collider without a Rigidbody, a "static" collider in the physics engine's
    // terms, and moving static colliders is the engine's expensive case. In the logged 360-beaver colony Unity's
    // PreLateUpdate, where this sync runs, grew from 1.4 to 5.5 ms per frame at the evening commute and in drought;
    // this sync is the suspect, not yet measured on its own, and the stats line samples what it costs.
    //
    // DeferPhysicsSync leaves the per-frame sync out and runs it instead right before a selection raycast whenever one
    // was left out since the last sync, so every raycast sees the colliders where the objects are at that moment.
    // Every 64th frame the sync still runs, timed for the stats line, which also keeps the colliders at most about a
    // second behind for anything that asks without a raycast. The physics step in FixedUpdate syncs the same
    // transforms before it simulates, so with the per-frame sync gone the cost would only move there: while this is
    // on, the physics simulation mode is Script (nothing simulates), and the previous mode is put back if the feature
    // turns off. A raycast made after this frame's ticks sees this frame's positions rather than the last frame's, a
    // fraction of a tile for a walking beaver.
    //
    // One caller of the same raycast is not a selection: the audio listener (SoundListener.LateUpdateSingleton) casts a
    // ray from the screen centre through BlockObjectRaycaster on every frame the camera moves (every frame without this
    // mod's SoundListener part), which would bring the sync back on all those frames. Its answer is the first block
    // object along the ray: BlockObjectRaycaster steps through anything else it hits (a beaver, an icon) by casting
    // again, and block objects do not move, so a ray inside the listener's update is left on the last sync. That is
    // sound only; everything else still syncs first.
    //
    // User interface only: the simulation never touches physics.
    internal static class PhysicsSync
    {
        // The pure rule, tested on its own.
        internal sealed class Policy
        {
            public const int SyncEveryFrames = 64;

            private long _frames;

            // A sync was left out since the last one ran: the colliders may be behind their objects.
            public bool Pending { get; private set; }

            // Once per frame, in place of the game's sync: true on the frames that still run it (the first, then every
            // 64th).
            public bool FrameDue()
            {
                bool due = _frames++ % SyncEveryFrames == 0;
                Pending = !due;
                return due;
            }

            // Before a selection raycast: true when the colliders have to be synced first.
            public bool RaycastNeedsSync()
            {
                if (!Pending)
                {
                    return false;
                }
                Pending = false;
                return true;
            }
        }

        private const string AdapterType = "Timberborn.Physics.TransformSyncServiceUnityAdapter";
        private const string RaycasterType = "Timberborn.SelectionSystem.SelectableObjectRaycaster";
        private const string SoundListenerType = "Timberborn.CoreSound.SoundListener";

        // Swappable for the test harness, which has no Unity.
        internal static Action SyncTransforms = () => Physics.SyncTransforms();

        private static Policy _policy = new Policy();
        private static bool _active;
        private static bool _modeChanged;
        private static SimulationMode _previousMode;
        private static long _frames;
        private static long _skipped;
        private static long _sampled;
        private static long _sampledTicks;
        private static long _longestTicks;
        private static long _raycastSyncs;
        private static long _raycastTicks;
        private static bool _inListener;
        private static long _listenerRays;

        public static Feature CreateFeature()
        {
            Type self = typeof(PhysicsSync);
            Feature feature = new Feature { Name = "PhysicsSync" };
            // Both are required: leaving the per-frame sync out is only right while every raycast syncs first.
            feature.Patches.Add(new PatchSpec
            {
                Name = "TransformSyncServiceUnityAdapter.LateUpdate",
                Required = true,
                Target = () => Reflect.Method(AdapterType, "LateUpdate"),
                Prefix = Reflect.Own(self, nameof(LateUpdatePrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "SelectableObjectRaycaster.TryHitSelectableObject",
                Required = true,
                Target = () => Reflect.Overload(RaycasterType, "TryHitSelectableObject", 4),
                Prefix = Reflect.Own(self, nameof(RaycastPrefix))
            });
            // Optional: without it the listener's rays sync first like every other, which is only slower.
            feature.Patches.Add(new PatchSpec
            {
                Name = "SoundListener.LateUpdateSingleton",
                Required = false,
                Target = () => Reflect.Method(SoundListenerType, "LateUpdateSingleton"),
                Prefix = Reflect.Own(self, nameof(ListenerPrefix)),
                Finalizer = Reflect.Own(self, nameof(ListenerFinalizer))
            });
            return feature;
        }

        public static void Activate()
        {
            try
            {
#pragma warning disable CS0618
                bool autoSync = Physics.autoSyncTransforms;
#pragma warning restore CS0618
                _previousMode = Physics.simulationMode;
                Physics.simulationMode = SimulationMode.Script;
                _modeChanged = true;
                _policy = new Policy();
                _active = true;
                Log.Info($"PhysicsSync: colliders are synced before each selection raycast and every {Policy.SyncEveryFrames}th " +
                         $"frame instead of every frame; physics simulation mode {_previousMode} -> Script (nothing in the game " +
                         $"simulates); Physics.autoSyncTransforms = {autoSync}" +
                         (autoSync ? " (queries also sync on their own)" : ""));
            }
            catch (Exception exception)
            {
                TurnOff(exception);
            }
        }

        // For the harness: the rule's state from scratch, without touching Unity.
        internal static void ActivateForTests()
        {
            _policy = new Policy();
            _frames = _skipped = _sampled = _sampledTicks = _longestTicks = _raycastSyncs = _raycastTicks = _listenerRays = 0;
            _inListener = false;
            _active = true;
        }

        internal static bool IsActive => _active;

        public static string TakeStatsLine()
        {
            if (!_active || _frames == 0)
            {
                return null;
            }
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            string cost = _sampled > 0
                ? $"{_sampledTicks * msPerTick / _sampled:0.000} ms (sampled every {Policy.SyncEveryFrames}th frame, longest " +
                  $"{_longestTicks * msPerTick:0.000} ms)"
                : "not measured yet";
            string line = $"PhysicsSync: the per-frame collider sync was left out in {_skipped} of {_frames} frames; one sync " +
                          $"costs about {cost}; {_raycastSyncs} syncs before a selection raycast ({_raycastTicks * msPerTick:0.0} ms " +
                          $"in total); {_listenerRays} audio listener rays left on the last sync";
            _frames = _skipped = _sampled = _sampledTicks = _longestTicks = _raycastSyncs = _raycastTicks = _listenerRays = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool LateUpdatePrefix()
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                _frames++;
                if (!_policy.FrameDue())
                {
                    _skipped++;
                    return false;
                }
                // The game's own call, timed.
                long start = Stopwatch.GetTimestamp();
                SyncTransforms();
                long ticks = Stopwatch.GetTimestamp() - start;
                _sampled++;
                _sampledTicks += ticks;
                _longestTicks = Math.Max(_longestTicks, ticks);
                return false;
            }
            catch (Exception exception)
            {
                TurnOff(exception);
                return true;
            }
        }

        internal static void RaycastPrefix()
        {
            if (!_active)
            {
                return;
            }
            try
            {
                if (_inListener)
                {
                    // The audio listener's ray: block objects only, and they do not move (see the top).
                    _listenerRays++;
                    return;
                }
                if (!_policy.RaycastNeedsSync())
                {
                    return;
                }
                long start = Stopwatch.GetTimestamp();
                SyncTransforms();
                _raycastSyncs++;
                _raycastTicks += Stopwatch.GetTimestamp() - start;
            }
            catch (Exception exception)
            {
                TurnOff(exception);
            }
        }

        internal static void ListenerPrefix()
        {
            _inListener = _active;
        }

        internal static void ListenerFinalizer()
        {
            _inListener = false;
        }
        // ReSharper restore InconsistentNaming

        // Back to the game's way: its simulation mode, colliders synced now, and the per-frame sync from the next frame.
        private static void TurnOff(Exception exception)
        {
            _active = false;
            Log.Warning("PhysicsSync failed and is off for this session; the game syncs the colliders every frame again: " + exception);
            try
            {
                if (_modeChanged)
                {
                    _modeChanged = false;
                    RestoreSimulationMode();
                }
                SyncTransforms();
            }
            catch (Exception restoreException)
            {
                Log.Warning("PhysicsSync: could not put the physics settings back: " + restoreException.Message);
            }
        }

        // On its own so that the harness, which has no Unity, can run TurnOff.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RestoreSimulationMode()
        {
            Physics.simulationMode = _previousMode;
        }
    }
}
