using System;
using System.IO;
using System.Text;

namespace LateGamePerformance
{
    // Unity decides whether garbage collection is incremental from boot.config, before any mod loads, so the only
    // way a mod can switch it on is to edit that file for the next launch. This is the edit, kept free of Unity so
    // the tests can run it on a copy.
    //
    // Why it matters: the collector re-marks everything that is still in use on every collection. With a managed
    // heap of 1.6 GB and more, a late game colony pays about 0.6 s for that each time, about once a minute, with
    // every thread stopped. Incremental collection does the same marking a few milliseconds at a time across many
    // frames. Measured on two computers in the same session: a median of 675 ms per collection without it, and
    // only one collection frame over 50 ms (outside saves) with it.
    //
    // The file belongs to the game, so: a backup is written once before the first change and never overwritten,
    // nothing else in the file is touched, line endings are kept, and the change is only ever made because the
    // player ticked or unticked the setting, never at startup.
    internal static class BootConfig
    {
        public const string Key = "gc-max-time-slice";
        public const string Line = Key + "=3";
        public const string BackupSuffix = ".before-incremental-gc.bak";

        public enum Outcome
        {
            Changed,
            AlreadyAsWanted,
            FileMissing,
            Failed
        }

        public static bool HasKey(string text)
        {
            foreach (string line in SplitLines(text))
            {
                if (IsKeyLine(line))
                {
                    return true;
                }
            }
            return false;
        }

        public static string WithIncremental(string text)
        {
            if (HasKey(text))
            {
                return text;
            }
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string separator = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal) ? "" : newline;
            return text + separator + Line + newline;
        }

        public static string WithoutIncremental(string text)
        {
            if (!HasKey(text))
            {
                return text;
            }
            StringBuilder kept = new StringBuilder(text.Length);
            int position = 0;
            while (position < text.Length)
            {
                int end = text.IndexOf('\n', position);
                int next = end < 0 ? text.Length : end + 1;
                string line = text.Substring(position, next - position);
                if (!IsKeyLine(line.TrimEnd('\r', '\n')))
                {
                    kept.Append(line);
                }
                position = next;
            }
            return kept.ToString();
        }

        public static bool FileHasKey(string path)
        {
            try
            {
                return File.Exists(path) && HasKey(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // error is set for Failed only.
        public static Outcome Apply(string path, bool incremental, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    return Outcome.FileMissing;
                }
                string before = File.ReadAllText(path);
                string after = incremental ? WithIncremental(before) : WithoutIncremental(before);
                if (after == before)
                {
                    return Outcome.AlreadyAsWanted;
                }
                string backup = path + BackupSuffix;
                if (!File.Exists(backup))
                {
                    File.WriteAllText(backup, before);
                }
                File.WriteAllText(path, after);
                return Outcome.Changed;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return Outcome.Failed;
            }
        }

        private static bool IsKeyLine(string line)
        {
            string trimmed = line.Trim();
            return trimmed.StartsWith(Key, StringComparison.Ordinal) &&
                   trimmed.Substring(Key.Length).TrimStart().StartsWith("=", StringComparison.Ordinal);
        }

        private static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Split('\n');
        }
    }
}
