using System;
using Bindito.Core;
using Timberborn.CoreUI;
using Timberborn.SettingsSystem;
using Timberborn.SingletonSystem;

namespace LateGamePerformance
{
    // A line in Player.log is not something a player sees. When garbage collection is not incremental, this says
    // so in the main menu and offers to switch it on, which is the same edit as ticking the setting. It asks
    // once: "Not now" is remembered (in the game's own settings store, so it survives updating the mod), and
    // there is nothing to configure. It stays quiet when the line is already in boot.config and only a restart
    // is missing. The checkbox on the settings page remains for a player who changes their mind.
    //
    // One of the three classes that touch UI types, with PerformanceSettings and TurnedOffNotice; nothing else in
    // the mod depends on it.
    public class GcNotice : IPostLoadableSingleton
    {
        internal const string DeclinedKey = "LateGamePerformance.IncrementalGcNoticeDeclined";

        private static bool _shownThisLaunch;

        private readonly DialogBoxShower _dialogBoxShower;
        private readonly PerformanceSettings _performanceSettings;
        private readonly ISettings _settings;

        public GcNotice(DialogBoxShower dialogBoxShower, PerformanceSettings performanceSettings, ISettings settings)
        {
            _dialogBoxShower = dialogBoxShower;
            _performanceSettings = performanceSettings;
            _settings = settings;
        }

        // The decision, free of UI so the tests can check it.
        internal static bool ShouldAsk(bool shownThisLaunch, bool declinedBefore, bool incremental, bool lineInBootConfig)
        {
            return !shownThisLaunch && !declinedBefore && !incremental && !lineInBootConfig;
        }

        public void PostLoad()
        {
            try
            {
                if (!ShouldAsk(_shownThisLaunch, _settings.GetSafeBool(DeclinedKey), GcReport.IsIncremental(),
                        BootConfig.FileHasKey(GcReport.BootConfigPath())))
                {
                    return;
                }
                _shownThisLaunch = true;
                _dialogBoxShower.Create()
                    .SetMessage("Late Game Performance\n\n" +
                                "Garbage collection is not incremental on this computer. In a large colony the game " +
                                "then freezes for most of a second about once a minute while memory is cleaned up, " +
                                "and in multiplayer the other player waits or you fall behind.\n\n" +
                                "Turning it on adds one line to boot.config in the game's folder (a backup is kept) " +
                                "and takes effect the next time the game starts. It does not change the simulation.\n\n" +
                                "You will not be asked again. It can be changed at any time under Mods > Late Game " +
                                "Performance.")
                    .SetConfirmButton(TurnOn, "Turn it on")
                    .SetCancelButton(Decline, "Not now")
                    .Show();
            }
            catch (Exception exception)
            {
                Log.Warning("GC notice could not be shown: " + exception.Message);
            }
        }

        // Setting the value runs the same handler as ticking the box. If the box is already ticked (an earlier
        // attempt could not write the file), setting it again would change nothing, so the edit is asked for directly.
        private void TurnOn()
        {
            if (_performanceSettings.IncrementalGc.Value)
            {
                GcReport.ApplyIncremental(true);
            }
            else
            {
                _performanceSettings.IncrementalGc.SetValue(true);
            }
        }

        private void Decline()
        {
            try
            {
                _settings.SetBool(DeclinedKey, true);
                Log.Info("GC: incremental garbage collection was declined in the main menu; not asking again. It can " +
                         "be turned on under Mods > Late Game Performance.");
            }
            catch (Exception exception)
            {
                Log.Warning("GC notice: the answer could not be saved, so it will be asked again: " + exception.Message);
            }
        }
    }

    [Context("MainMenu")]
    public class GcNoticeConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<GcNotice>().AsSingleton();
        }
    }
}
