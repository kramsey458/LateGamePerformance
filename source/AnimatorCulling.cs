using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    // AnimatorLodDistance has its pose written every second frame, beyond twice that distance every fourth, and
    // (0.4.28) beyond three times it every eighth, spread over the frames so no frame writes them all. Time still
    // advances every frame, so the pose written is the one the game would write on that frame; at that size on
    // screen a pose a few frames old is not visible. Anything close to the camera is written every frame as before.
    //
    // Since 0.4.28 the decision is made in one replacing prefix on AnimatorRegistry.UpdateSingleton instead of one
    // prefix per animator: it walks the registry's own list the way the game does (the same Time.deltaTime, the same
    // (bool) and isActiveAndEnabled checks in the same order, then UpdateAnimation's own first check) and calls the
    // game's UpdateAnimation for every animator whose pose is due, and the game's UpdateTime alone for the others.
    // What it knows about each animator sits in arrays aligned with that list, so no weak table is looked up per
    // animator per frame; when the game's list changes (an animator added at the end, or removed and the rest moved
    // down) the entries are matched up again by reference that frame. A destroyed renderer is noticed only when it
    // is asked (Unity throws) or at the periodic renderer refresh, not by a walk of every renderer every frame.
    // An animator the game's first check would leave alone (not enabled, speed 0, no animation, finished) is not
    // called at all: the game's call returns at that check.
    //
    // Rendering only. The simulation reads animator time (the wonder, the clutch, particle triggers, the character
    // model's animate executor), never a node transform or a material, and time is kept exactly as the game keeps it.
    internal static class AnimatorCulling
    {
        private const string AnimatorType = "Timberborn.TimbermeshAnimations.TimbermeshAnimator";
        private const string RegistryType = "Timberborn.TimbermeshAnimations.AnimatorRegistry";
        private const int RefreshRenderersEveryFrames = 600;
        private const int RefreshPositionEveryFrames = 60;

        // What one animator's frame came to. A far animator's outcome is its pose interval (2, 4 or 8).
        internal const int Idle = 0;
        internal const int Written = 1;
        internal const int OffScreen = -1;

        internal sealed class Entry
        {
            public Renderer[] Renderers;
            public int RenderersDue;
            public Vector3 Position;
            public bool HasPosition;
            public int PositionDue;
            public int Hash;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        // One entry per slot of the game's list, in the same order. The game only appends (Start) and removes
        // (OnDestroy, which moves the rest down), so a slot whose animator is not the one it held is matched up again:
        // the first such slot in a frame notes where every entry from there on was, and each animator takes its own
        // entry back from that note (or a new one). Slots past the end and the note let go of what they held once the
        // frame's walk is over, so nothing keeps a removed animator alive. The pure part, tested on its own.
        internal sealed class AlignedEntries
        {
            private object[] _keys = new object[64];
            private Entry[] _entries = new Entry[64];
            private int _count;
            private readonly Dictionary<object, Entry> _moved = new Dictionary<object, Entry>(ReferenceComparer.Instance);
            private bool _noted;

            public int Count => _count;

            public long Rematched { get; private set; }

            public Entry At(int index, object animator)
            {
                if (index < _count && ReferenceEquals(_keys[index], animator))
                {
                    return _entries[index];
                }
                return Rematch(index, animator);
            }

            private Entry Rematch(int index, object animator)
            {
                if (!_noted)
                {
                    // The first change this frame: where every entry from here on was, before any slot is overwritten.
                    for (int i = index; i < _count; i++)
                    {
                        if (_keys[i] != null)
                        {
                            _moved[_keys[i]] = _entries[i];
                        }
                    }
                    _noted = true;
                }
                if (!_moved.TryGetValue(animator, out Entry entry))
                {
                    entry = new Entry { Hash = RuntimeHelpers.GetHashCode(animator) };
                }
                if (index >= _keys.Length)
                {
                    int size = Math.Max(index + 1, _keys.Length * 2);
                    Array.Resize(ref _keys, size);
                    Array.Resize(ref _entries, size);
                }
                _keys[index] = animator;
                _entries[index] = entry;
                if (index >= _count)
                {
                    _count = index + 1;
                }
                Rematched++;
                return entry;
            }

            // The walk is over and the game's list is this long.
            public void EndFrame(int count)
            {
                for (int i = count; i < _count; i++)
                {
                    _keys[i] = null;
                    _entries[i] = null;
                }
                if (count < _count)
                {
                    _count = count;
                }
                if (_noted)
                {
                    _moved.Clear();
                    _noted = false;
                }
            }

            public void Clear()
            {
                Array.Clear(_keys, 0, _keys.Length);
                Array.Clear(_entries, 0, _entries.Length);
                _count = 0;
                _moved.Clear();
                _noted = false;
            }
        }

        private static Func<object, IList> _registryAnimators;
        private static Func<object, bool> _playingFinished;
        // TimbermeshAnimator.UpdateAnimation's own first check, in its order: Enabled && _speed != 0f &&
        // _currentAnimation != null && !PlayingFinished.
        internal static Func<object, bool> WouldAnimate;
        // The game's methods. Internal so the harness can count what is called.
        internal static Action<object, float> UpdateAnimation;
        internal static Action<object, float> UpdateTime;
        internal static Action<object> WritePose;

        // Swappable for the test harness, which has no Unity.
        internal static Func<int> FrameCount = () => Time.frameCount;
        internal static Func<float> DeltaTime = () => Time.deltaTime;
        // The game's own check in AnimatorRegistry.UpdateSingleton.
        internal static Func<object, bool> IsLive = animator =>
        {
            Behaviour behaviour = (Behaviour)animator;
            return behaviour && behaviour.isActiveAndEnabled;
        };
        internal static Func<Vector3?> CameraPosition = () =>
        {
            Camera camera = Camera.main;
            return camera != null ? camera.transform.position : (Vector3?)null;
        };
        internal static Func<object, Vector3> PositionOf = animator => ((Component)animator).transform.position;
        internal static Func<object, Entry, int, bool> Visible = AnyRendererVisible;

        private static readonly AlignedEntries Entries = new AlignedEntries();
        private static bool _active;
        private static bool _subscribed;
        private static bool _lod;
        private static float _lodDistance = 80f;
        private static bool _hasCamera;
        private static Vector3 _cameraPosition;
        private static long _written;
        private static long _offScreen;
        private static long _far2;
        private static long _far4;
        private static long _far8;

        public static Feature CreateFeature()
        {
            Feature feature = new Feature { Name = "AnimatorCulling" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "AnimatorRegistry.UpdateSingleton",
                Required = true,
                Target = () =>
                {
                    Type animator = Reflect.GameType(AnimatorType);
                    _registryAnimators = Reflect.FieldGetter<IList>(Reflect.GameType(RegistryType), "_animators");
                    _playingFinished = Reflect.PropertyGetter<bool>(animator, "PlayingFinished");
                    WouldAnimate = GuardOf(animator);
                    UpdateAnimation = Reflect.InstanceCall<Action<object, float>>(Reflect.Method(AnimatorType, "UpdateAnimation"));
                    UpdateTime = Reflect.InstanceCall<Action<object, float>>(Reflect.Method(AnimatorType, "UpdateTime"));
                    WritePose = Reflect.InstanceCall<Action<object>>(Reflect.Method(AnimatorType, "UpdateAnimationUpdaters"));
                    return Reflect.Method(RegistryType, "UpdateSingleton");
                },
                Prefix = Reflect.Own(typeof(AnimatorCulling), nameof(RegistryPrefix))
            });
            return feature;
        }

        public static void Activate()
        {
            ActivateForTests();
            try
            {
                // What is kept about the animators of a game scene is let go of when that scene is unloaded, so going
                // back to the main menu or loading another save does not keep the old scene alive.
                if (!_subscribed)
                {
                    SceneManager.sceneUnloaded += SceneUnloaded;
                    _subscribed = true;
                }
            }
            catch (Exception exception)
            {
                Log.Warning("AnimatorCulling: could not follow scene changes; its entries are replaced on the next " +
                            "game scene's first frame instead. " + exception.Message);
            }
        }

        internal static void ActivateForTests()
        {
            Entries.Clear();
            _written = _offScreen = _far2 = _far4 = _far8 = 0;
            _active = true;
        }

        internal static bool IsActive => _active;

        internal static AlignedEntries EntriesForTests => Entries;

        public static void ConfigureLod(bool enabled, float distance)
        {
            _lod = enabled && distance > 0;
            _lodDistance = Math.Max(1f, distance);
        }

        private static void SceneUnloaded(Scene scene)
        {
            Entries.Clear();
        }

        // How many frames apart the pose of an object at this distance is written: every frame up to the distance,
        // every second frame beyond it, every fourth beyond twice it, every eighth beyond three times it.
        internal static int PoseInterval(float distance, float lodDistance)
        {
            if (distance < lodDistance)
            {
                return 1;
            }
            if (distance < 2f * lodDistance)
            {
                return 2;
            }
            return distance < 3f * lodDistance ? 4 : 8;
        }

        // Whether this frame is one of the object's own. The hash spreads objects over the frames.
        internal static bool PoseDue(int frame, int hash, int interval)
        {
            return interval <= 1 || (frame + (hash & 0x7fffffff)) % interval == 0;
        }

        // A refresh after about the given number of frames, between half of it and all of it by the object's hash, so
        // that the animators of a scene loaded in one frame do not all refresh on the same later frame.
        internal static int NextRefresh(int frame, int hash, int everyFrames)
        {
            int half = everyFrames / 2;
            return frame + half + (hash & 0x7fffffff) % (everyFrames - half);
        }

        public static string TakeStatsLine()
        {
            long total = _written + _offScreen + _far2 + _far4 + _far8;
            if (!_active || total == 0)
            {
                return null;
            }
            string line = $"AnimatorCulling: {total} animator updates; the pose was written in {_written}, left out in " +
                          $"{_offScreen} because no part of the object was on screen, and left out for objects far from the " +
                          $"camera in {_far2} (written every 2nd frame), {_far4} (every 4th) and {_far8} (every 8th); their " +
                          "time kept running";
            _written = _offScreen = _far2 = _far4 = _far8 = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool RegistryPrefix(object __instance)
        {
            if (!_active)
            {
                return true;
            }
            IList animators;
            float deltaTime;
            int frame;
            try
            {
                animators = _registryAnimators(__instance);
                frame = FrameCount();
                ReadCamera();
                deltaTime = DeltaTime();
            }
            catch (Exception exception)
            {
                // Nothing done yet: the game's own loop runs.
                TurnOff(exception);
                return true;
            }
            try
            {
                // AnimatorRegistry.UpdateSingleton, with the pose left out where it is not due. From an animator on
                // which this mod's part fails, the rest of the frame is the game's own calls.
                for (int i = 0; i < animators.Count; i++)
                {
                    object animator = animators[i];
                    Entry entry = _active ? EntryAt(i, animator) : null;
                    if (!IsLive(animator))
                    {
                        continue;
                    }
                    int outcome = entry != null ? Outcome(animator, entry, frame) : Written;
                    switch (outcome)
                    {
                        case Idle:
                            continue;
                        case Written:
                            UpdateAnimation(animator, deltaTime);
                            if (entry != null)
                            {
                                _written++;
                            }
                            continue;
                        case OffScreen:
                            _offScreen++;
                            break;
                        case 2:
                            _far2++;
                            break;
                        case 4:
                            _far4++;
                            break;
                        default:
                            _far8++;
                            break;
                    }
                    SkipPose(animator, deltaTime);
                }
            }
            finally
            {
                Entries.EndFrame(animators.Count);
            }
            return false;
        }
        // ReSharper restore InconsistentNaming

        private static Entry EntryAt(int index, object animator)
        {
            if (animator == null)
            {
                return null;
            }
            try
            {
                return Entries.At(index, animator);
            }
            catch (Exception exception)
            {
                TurnOff(exception);
                return null;
            }
        }

        private static int Outcome(object animator, Entry entry, int frame)
        {
            try
            {
                if (!WouldAnimate(animator))
                {
                    return Idle;
                }
                if (_hasCamera)
                {
                    int interval = PoseInterval(DistanceToCamera(animator, entry, frame), _lodDistance);
                    if (!PoseDue(frame, entry.Hash, interval))
                    {
                        return interval;
                    }
                }
                return Visible(animator, entry, frame) ? Written : OffScreen;
            }
            catch (Exception exception)
            {
                TurnOff(exception);
                return Written;
            }
        }

        // The game's time-keeping without the pose, plus the final pose of an animation that just finished.
        private static void SkipPose(object animator, float deltaTime)
        {
            UpdateTime(animator, deltaTime);
            if (_playingFinished(animator))
            {
                // Played once and just finished: the game will not touch it again, so its last pose is written
                // now (the wonder, the working-hours bell).
                WritePose(animator);
            }
        }

        private static void ReadCamera()
        {
            _hasCamera = false;
            if (!_lod)
            {
                return;
            }
            Vector3? camera = CameraPosition();
            if (camera.HasValue)
            {
                _hasCamera = true;
                _cameraPosition = camera.Value;
            }
        }

        private static float DistanceToCamera(object animator, Entry entry, int frame)
        {
            if (!entry.HasPosition || frame >= entry.PositionDue)
            {
                entry.Position = PositionOf(animator);
                entry.HasPosition = true;
                entry.PositionDue = NextRefresh(frame, entry.Hash, RefreshPositionEveryFrames);
            }
            return Vector3.Distance(entry.Position, _cameraPosition);
        }

        private static bool AnyRendererVisible(object animator, Entry entry, int frame)
        {
            if (entry.Renderers == null || frame >= entry.RenderersDue)
            {
                FetchRenderers(animator, entry, frame);
            }
            try
            {
                return AnyVisible(entry.Renderers);
            }
            catch (Exception)
            {
                // A renderer was destroyed since the list was made (Unity throws when a destroyed one is asked):
                // make the list again and ask once more.
            }
            FetchRenderers(animator, entry, frame);
            return AnyVisible(entry.Renderers);
        }

        private static bool AnyVisible(Renderer[] renderers)
        {
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

        private static void FetchRenderers(object animator, Entry entry, int frame)
        {
            entry.Renderers = ((Component)animator).GetComponentsInChildren<Renderer>(true);
            entry.RenderersDue = NextRefresh(frame, entry.Hash, RefreshRenderersEveryFrames);
        }

        private static void TurnOff(Exception exception)
        {
            _active = false;
            Entries.Clear();
            Log.Warning("AnimatorCulling failed and is off for this session: " + exception);
        }

        private static Func<object, bool> GuardOf(Type animator)
        {
            FieldInfo speed = AccessTools.Field(animator, "_speed");
            FieldInfo current = AccessTools.Field(animator, "_currentAnimation");
            PropertyInfo enabled = AccessTools.Property(animator, "Enabled");
            PropertyInfo finished = AccessTools.Property(animator, "PlayingFinished");
            if (speed == null || speed.FieldType != typeof(float) || current == null || current.FieldType.IsValueType ||
                enabled == null || enabled.PropertyType != typeof(bool) || finished == null || finished.PropertyType != typeof(bool))
            {
                throw new MissingMemberException(animator.Name, "Enabled, _speed, _currentAnimation or PlayingFinished");
            }
            ParameterExpression instance = Expression.Parameter(typeof(object), "animator");
            Expression typed = Expression.Convert(instance, animator);
            Expression body = Expression.AndAlso(
                Expression.Property(typed, enabled),
                Expression.AndAlso(
                    Expression.NotEqual(Expression.Field(typed, speed), Expression.Constant(0f)),
                    Expression.AndAlso(
                        Expression.ReferenceNotEqual(Expression.Field(typed, current), Expression.Constant(null, current.FieldType)),
                        Expression.Not(Expression.Property(typed, finished)))));
            return Expression.Lambda<Func<object, bool>>(body, instance).Compile();
        }
    }
}
