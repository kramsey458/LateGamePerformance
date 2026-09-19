using Bindito.Core;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace LateGamePerformance
{
    // The in-game settings page (Mod Settings mod). Only holds what is worth changing without editing the .cfg.
    //
    // This class is the only place that touches Mod Settings types. It pushes values into plain static fields on
    // the features, so nothing else in the mod depends on that assembly being loadable.
    public class PerformanceSettings : ModSettingsOwner
    {
        public ModSetting<bool> RecordTimings { get; } = new ModSetting<bool>(false,
            ModSettingDescriptor.Create("Record per-component timings")
                .SetTooltip("Times every part of each tick and writes a report every few thousand ticks to " +
                            "Documents\\Timberborn\\LateGamePerformance. Applies the next time a save is loaded. " +
                            "Slows the game a little while on; turn it off after a profiling session. " +
                            "Does not affect the simulation, so multiplayer peers may differ."));

        public PerformanceSettings(ISettings settings, ModSettingsOwnerRegistry modSettingsOwnerRegistry,
            ModRepository modRepository) : base(settings, modSettingsOwnerRegistry, modRepository)
        {
        }

        // Ticking the box in-game is allowed; it still only applies from the next save load.
        public override ModSettingsContext ChangeableOn => ModSettingsContext.All;

        // Must equal the Id in manifest.json.
        protected override string ModId => Plugin.ModId;

        protected override void OnAfterLoad()
        {
            // The main menu loads this before any save does, so the value is already in place when a game
            // scene creates the metrics service.
            MetricsDump.RequestedFromMenu = RecordTimings.Value;
            RecordTimings.ValueChanged += (_, value) => MetricsDump.RequestedFromMenu = value;
        }
    }

    [Context("MainMenu")]
    [Context("Game")]
    public class PerformanceSettingsConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<PerformanceSettings>().AsSingleton();
        }
    }
}
