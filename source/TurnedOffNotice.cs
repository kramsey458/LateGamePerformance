using System;
using Bindito.Core;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;

namespace LateGamePerformance
{
    // Says in the game that a simulation feature turned itself off (see TurnedOff): once when a game scene starts
    // with one already off, and again whenever another one turns itself off. It looks from the frame loop, not from
    // inside the tick where the failure happened, and only shows a dialog, which changes nothing in the simulation.
    public class TurnedOffNotice : IUpdatableSingleton
    {
        private readonly DialogBoxShower _dialogBoxShower;
        private int _shownVersion;

        public TurnedOffNotice(DialogBoxShower dialogBoxShower)
        {
            _dialogBoxShower = dialogBoxShower;
        }

        public void UpdateSingleton()
        {
            int version = TurnedOff.Version;
            if (version == _shownVersion)
            {
                return;
            }
            _shownVersion = version;
            try
            {
                _dialogBoxShower.Create().SetMessage(TurnedOff.NoticeText(TurnedOff.Names())).Show();
            }
            catch (Exception exception)
            {
                Log.Warning("The notice that a feature turned itself off could not be shown: " + exception.Message);
            }
        }
    }

    [Context("Game")]
    public class TurnedOffNoticeConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<TurnedOffNotice>().AsSingleton();
        }
    }
}
