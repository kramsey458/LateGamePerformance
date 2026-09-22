using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace LateGamePerformance
{
    // Where Unity's own share of a frame goes. Most of a frame in a large colony is neither the simulation nor the
    // game's scripts but the engine: culling, draw submission, animation, the user interface. The engine keeps
    // counters for those even in a release build (Unity's ProfilerRecorder), and this samples a configurable list
    // of them every frame while the Diagnostics box is on, reports averages and maxima in the Diagnostics line,
    // and once per session writes every counter the build offers to a file, so the list can be extended with
    // names that exist. Measurement only.
    internal static class UnityMarkers
    {
        internal const string DefaultSpec =
            "Camera.Render;Culling;Shadows.Draw;RenderForward;Render.OpaqueGeometry;Gfx.WaitForPresentOnGfxThread;" +
            "Gfx.WaitForRenderThread;UIR.ImmediateRenderer;Animators.Update;ParticleSystem.Update;" +
            "Draw Calls Count;SetPass Calls Count;Shadow Casters Count;Standard Draw Calls Count;" +
            "SRP Batcher Draw Calls Count;Standard Instanced Draw Calls Count;Visible Skinned Meshes Count;GC.Collect";

        private sealed class Entry
        {
            public string Name;
            public ProfilerRecorder Recorder;
            public bool IsTime;
            public bool Found;
            public double Sum;
            public double Max;
            public long Samples;
        }

        // Swappable for the test harness, which has no Unity.
        internal static Func<string> OutputFolder = () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Timberborn", "LateGamePerformance");

        private static string[] _names = Parse(DefaultSpec);
        private static Entry[] _entries;
        private static bool _running;
        private static bool _dumped;
        private static bool _failed;

        internal static void Configure(string spec)
        {
            _names = Parse(spec);
        }

        internal static string[] Parse(string spec)
        {
            List<string> names = new List<string>();
            foreach (string part in (spec ?? "").Split(';'))
            {
                string name = part.Trim();
                if (name.Length > 0 && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
            return names.ToArray();
        }

        // Once per frame, from the timing hook. Costs one flag read while the Diagnostics box is off.
        internal static void Sample()
        {
            if (_failed)
            {
                return;
            }
            try
            {
                if (!Diagnostics.Enabled)
                {
                    if (_running)
                    {
                        Stop();
                    }
                    return;
                }
                if (!_running)
                {
                    Start();
                }
                for (int i = 0; i < _entries.Length; i++)
                {
                    Entry entry = _entries[i];
                    if (!entry.Found || !entry.Recorder.Valid)
                    {
                        continue;
                    }
                    double value = entry.Recorder.LastValueAsDouble;
                    if (entry.IsTime)
                    {
                        value /= 1_000_000.0;
                    }
                    entry.Sum += value;
                    if (value > entry.Max)
                    {
                        entry.Max = value;
                    }
                    entry.Samples++;
                }
            }
            catch (Exception exception)
            {
                _failed = true;
                Log.Warning("UnityMarkers failed and is off for this session: " + exception);
            }
        }

        internal static string TakeStatsText()
        {
            if (_entries == null)
            {
                return null;
            }
            StringBuilder text = new StringBuilder("UnityMarkers per frame:");
            bool any = false;
            List<string> missing = new List<string>();
            foreach (Entry entry in _entries)
            {
                if (!entry.Found)
                {
                    missing.Add(entry.Name);
                    continue;
                }
                if (entry.Samples == 0)
                {
                    continue;
                }
                any = true;
                double average = entry.Sum / entry.Samples;
                text.Append(entry.IsTime
                    ? string.Format(CultureInfo.InvariantCulture, " {0} {1:0.00} ms (max {2:0.0});", entry.Name, average, entry.Max)
                    : string.Format(CultureInfo.InvariantCulture, " {0} {1:0} (max {2:0});", entry.Name, average, entry.Max));
                entry.Sum = entry.Max = 0;
                entry.Samples = 0;
            }
            if (!any)
            {
                return null;
            }
            if (missing.Count > 0)
            {
                text.Append(" not in this build: ").Append(string.Join(", ", missing)).Append(';');
            }
            return text.ToString().TrimEnd(';');
        }

        private static void Start()
        {
            List<ProfilerRecorderHandle> handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            Dictionary<string, ProfilerRecorderHandle> byName = new Dictionary<string, ProfilerRecorderHandle>(StringComparer.Ordinal);
            StringBuilder dump = _dumped ? null : new StringBuilder();
            dump?.AppendLine("Every profiler counter this build of the game offers, as 'category | name | unit | data type'. " +
                             "Put names from here into UnityMarkers in LateGamePerformance.cfg, separated by ';'.");
            foreach (ProfilerRecorderHandle handle in handles)
            {
                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                if (!byName.ContainsKey(description.Name))
                {
                    byName[description.Name] = handle;
                }
                dump?.Append(description.Category.Name).Append(" | ").Append(description.Name).Append(" | ")
                    .Append(description.UnitType).Append(" | ").Append(description.DataType).AppendLine();
            }
            if (dump != null)
            {
                _dumped = true;
                try
                {
                    string folder = OutputFolder();
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "unity-markers.txt");
                    File.WriteAllText(path, dump.ToString());
                    Log.Info($"UnityMarkers: {handles.Count} profiler counters of this build are listed in {path}");
                }
                catch (Exception exception)
                {
                    Log.Warning("UnityMarkers: could not write the counter list: " + exception.Message);
                }
            }
            _entries = new Entry[_names.Length];
            for (int i = 0; i < _names.Length; i++)
            {
                Entry entry = new Entry { Name = _names[i] };
                if (byName.TryGetValue(entry.Name, out ProfilerRecorderHandle handle))
                {
                    ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                    entry.IsTime = description.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds;
                    entry.Recorder = new ProfilerRecorder(handle, 1,
                        ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
                    entry.Found = true;
                }
                _entries[i] = entry;
            }
            _running = true;
        }

        private static void Stop()
        {
            foreach (Entry entry in _entries)
            {
                if (entry.Found)
                {
                    entry.Recorder.Dispose();
                    entry.Found = false;
                }
            }
            _running = false;
        }
    }
}
