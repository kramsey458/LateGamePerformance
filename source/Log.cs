using System;

namespace LateGamePerformance
{
    internal static class Log
    {
        private const string Prefix = "[LateGamePerformance] ";

        // Replaced by the test harness, where UnityEngine.Debug is not callable.
        public static Action<string> Sink = message => UnityEngine.Debug.Log(message);

        public static Action<string> WarningSink = message => UnityEngine.Debug.LogWarning(message);

        public static void Info(string message)
        {
            Sink(Prefix + message);
        }

        public static void Warning(string message)
        {
            WarningSink(Prefix + message);
        }
    }
}
