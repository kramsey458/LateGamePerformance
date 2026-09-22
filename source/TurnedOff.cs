using System.Collections.Generic;
using System.Threading;

namespace LateGamePerformance
{
    // The simulation features that turned themselves off during this session after an error, in the order they did.
    //
    // Such a feature stays off until the game is restarted (a new game scene does not bring it back), and from then
    // on this computer runs the game's own code for it where the other players may run the mod's, so a multiplayer
    // game can drift apart. A warning in Player.log is not something anyone reads in time, so it is said three ways:
    // the warning itself, a dialog in the game asking every player to restart (TurnedOffNotice), and the
    // Simulation features line said again, with OFF, right away, then on every stats interval and in every new game
    // scene (Plugin).
    //
    // Only failures report here. A feature that stands down by design (DistrictCounts beside another mod's patch it
    // has not read) does the same on every computer with the same mods, and says so in its own line.
    internal static class TurnedOff
    {
        private static readonly object Lock = new object();
        private static readonly List<string> Features = new List<string>();
        private static int _version;

        // Goes up each time another feature turns itself off.
        public static int Version => Volatile.Read(ref _version);

        // Logs the feature's warning, remembers the feature and, the first time it reports, says the Simulation
        // features line again with it OFF (the stats intervals that repeat it can be switched off). Any thread.
        public static void Report(string feature, string warning)
        {
            Log.Warning(warning);
            bool added = false;
            lock (Lock)
            {
                if (!Features.Contains(feature))
                {
                    Features.Add(feature);
                    Interlocked.Increment(ref _version);
                    added = true;
                }
            }
            if (added)
            {
                Log.Info(Plugin.TurnedOffLine());
            }
        }

        public static string[] Names()
        {
            lock (Lock)
            {
                return Features.ToArray();
            }
        }

        // The in-game notice, free of UI so the tests can read it.
        public static string NoticeText(string[] features)
        {
            return "Late Game Performance\n\n" +
                   "An error stopped part of this mod on this computer, and it has turned itself off for the rest of " +
                   "this session: " + string.Join(", ", features) + ". This computer now runs the game's own code for " +
                   "it (Player.log has the details).\n\n" +
                   "In multiplayer the other players' computers may still run the mod's code, and the games can drift " +
                   "apart. Every player should quit and restart the game before playing on together; the feature is " +
                   "back after a restart.\n\n" +
                   "In single player nothing is lost but speed.";
        }

        internal static void ResetForTests()
        {
            lock (Lock)
            {
                Features.Clear();
            }
        }
    }
}
