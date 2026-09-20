using System;
using Bindito.Core;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;

namespace LateGamePerformance
{
    // A line in Player.log is not something a player sees. When garbage collection is not incremental, this says
    // so once per launch in the main menu and offers to switch it on, which is the same edit as ticking the
    // setting. It stays quiet when the line is already in boot.config and only a restart is missing, and when the
    // player has unticked "Warn when garbage collection is not incremental".
    //
    // The only class besides PerformanceSettings that touches UI types; nothing else in the mod depends on it.
    public class GcNotice : IPostLoadableSingleton
    {
        private static bool _shownThisLaunch;

        private readonly DialogBoxShower _dialogBoxShower;
        private readonly PerformanceSettings _settings;

        public GcNotice(DialogBoxShower dialogBoxShower, PerformanceSettings settings)
        {
            _dialogBoxShower = dialogBoxShower;
            _settings = settings;
        }

        // Setting the value runs the same handler as ticking the box. If the box is already ticked (an earlier
        // attempt could not write the file), setting it again would change nothing, so the edit is asked for directly.
        private void TurnOn()
        {
            if (_settings.IncrementalGc.Value)
            {
                GcReport.ApplyIncremental(true);
            }
            else
            {
                _settings.IncrementalGc.SetValue(true);
            }
        }

        public void PostLoad()
        {
            try
            {
                if (_shownThisLaunch || !_settings.WarnWhenNotIncremental.Value || GcPacing.IsIncremental() ||
                    BootConfig.FileHasKey(GcReport.BootConfigPath()))
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
                                "To stop this message, untick 'Warn when garbage collection is not incremental' in " +
                                "this mod's settings.")
                    .SetConfirmButton(TurnOn, "Turn it on")
                    .SetCancelButton(() => { }, "Not now")
                    .Show();
            }
            catch (Exception exception)
            {
                Log.Warning("GC notice could not be shown: " + exception.Message);
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
