using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Timberborn.Common;

namespace LateGamePerformance
{
    // Every time a beaver changes behaviour, the game writes a line into that beaver's behaviour log: the
    // behaviour's name and the day, formatted into a string, kept in a ring of the last ten (BehaviorManager
    // .SetRunningBehavior). The log is read in two places only: it is saved with the beaver, and the debug
    // fragment of the entity panel shows it. Everything else about the change happens without it. At a few
    // thousand changes a second in a large colony, those strings are a steady stream of garbage that nothing reads
    // until the next save.
    //
    // The mod keeps the name and the day as they are (a reference and a float) in a ring of its own per beaver,
    // and formats them into the game's ring only when the game is about to read it: before Save, and before the
    // panel reads the log. The strings come out identical, because the same format is applied to the same name
    // and the same float, and the game's ring ends up holding exactly what it would have held after the same
    // sequence of additions (the last ten, in order). Nothing the simulation computes changes; the save file is
    // byte for byte the same.
    internal static class BehaviorLog
    {
        private const string ManagerType = "Timberborn.BehaviorSystem.BehaviorManager";
        internal const int Capacity = 10;

        // The last Capacity changes not yet written into the game's ring, oldest first.
        internal sealed class Ring
        {
            public readonly string[] Names = new string[Capacity];
            public readonly float[] Days = new float[Capacity];
            public int Start;
            public int Count;

            public void Push(string name, float day)
            {
                if (Count == Capacity)
                {
                    // The game's ring would have dropped this one by now as well.
                    Start = (Start + 1) % Capacity;
                }
                else
                {
                    Count++;
                }
                int slot = (Start + Count - 1) % Capacity;
                Names[slot] = name;
                Days[slot] = day;
            }

            // Writes the pending lines into the game's ring, in order, as the game would have.
            public int Flush(CyclicBuffer<string> buffer)
            {
                int written = Count;
                for (int k = 0; k < Count; k++)
                {
                    int slot = (Start + k) % Capacity;
                    buffer.Add(Format(Names[slot], Days[slot]));
                    Names[slot] = null;
                }
                Start = 0;
                Count = 0;
                return written;
            }
        }

        private static readonly ConditionalWeakTable<object, Ring> Rings = new ConditionalWeakTable<object, Ring>();
        private static Func<object, object> _runningBehavior;
        private static Action<object, object> _setRunningBehavior;
        private static Func<object, CyclicBuffer<string>> _buffer;
        private static Func<object, object> _dayNightCycle;
        private static Func<object, float> _partialDay;
        private static Func<object, string> _componentName;

        private static bool _active;
        private static long _deferred;
        private static long _written;
        private static long _flushes;

        public static Feature CreateFeature()
        {
            Type self = typeof(BehaviorLog);
            Feature feature = new Feature { Name = "BehaviorLog" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "BehaviorManager.SetRunningBehavior",
                Required = true,
                Target = () =>
                {
                    Bind();
                    return Reflect.Method(ManagerType, "SetRunningBehavior");
                },
                Prefix = Reflect.Own(self, nameof(SetRunningBehaviorPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "BehaviorManager.Save",
                Required = true,
                Target = () => Reflect.Method(ManagerType, "Save"),
                Prefix = Reflect.Own(self, nameof(FlushPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "BehaviorManager.TimestampedBehaviorLog",
                Required = true,
                Target = () => AccessTools.PropertyGetter(Reflect.GameType(ManagerType), "TimestampedBehaviorLog"),
                Prefix = Reflect.Own(self, nameof(FlushPrefix))
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
            Type manager = Reflect.GameType(ManagerType);
            Type behavior = Reflect.GameType("Timberborn.BehaviorSystem.Behavior");
            Type cycle = Reflect.GameType("Timberborn.TimeSystem.IDayNightCycle");
            if (manager == null || behavior == null || cycle == null)
            {
                throw new TypeLoadException("behaviour types not found");
            }
            _runningBehavior = Reflect.FieldGetter<object>(manager, "_runningBehavior");
            _setRunningBehavior = Reflect.FieldSetter<object>(manager, "_runningBehavior");
            _buffer = Reflect.FieldGetter<CyclicBuffer<string>>(manager, "_timestampedBehaviorLog");
            _dayNightCycle = Reflect.FieldGetter<object>(manager, "_dayNightCycle");
            _partialDay = Reflect.PropertyGetter<float>(cycle, "PartialDayNumber");
            _componentName = Reflect.PropertyGetter<string>(behavior, "ComponentName");
        }

        // The game's line: $"{behavior.ComponentName} {_dayNightCycle.PartialDayNumber:0.00}", which the compiler
        // turns into this very call with the current culture.
        internal static string Format(string name, float day)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0} {1:0.00}", name, day);
        }

        public static string TakeStatsLine()
        {
            if (!_active && _deferred == 0)
            {
                return null;
            }
            string line = $"BehaviorLog: {_deferred} behaviour changes noted without a string; {_written} lines written into " +
                          $"the game's logs before {_flushes} reads (saves and the entity panel's debug view)";
            _deferred = _written = _flushes = 0;
            return line;
        }

        // ReSharper disable InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool SetRunningBehaviorPrefix(object __instance, object behavior)
        {
            if (!_active)
            {
                return true;
            }
            try
            {
                if (!ReferenceEquals(_runningBehavior(__instance), behavior))
                {
                    // The game's own condition, its own name and its own day, noted rather than formatted.
                    Rings.GetValue(__instance, _ => new Ring()).Push(_componentName(behavior), _partialDay(_dayNightCycle(__instance)));
                    _deferred++;
                }
                _setRunningBehavior(__instance, behavior);
                return false;
            }
            catch (Exception exception)
            {
                // Nothing was written yet; the game's method does it all. Lines already noted are still written
                // out before the next read, because the flush does not depend on the feature being on.
                _active = false;
                Log.Warning("BehaviorLog failed and is off for this session: " + exception);
                return true;
            }
        }

        internal static void FlushPrefix(object __instance)
        {
            try
            {
                if (Rings.TryGetValue(__instance, out Ring ring) && ring.Count > 0)
                {
                    _written += ring.Flush(_buffer(__instance));
                    _flushes++;
                }
            }
            catch (Exception exception)
            {
                _active = false;
                Log.Warning("BehaviorLog failed while writing a beaver's log and is off for this session: " + exception);
            }
        }
        // ReSharper restore InconsistentNaming
    }
}
