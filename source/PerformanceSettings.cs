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

        public ModSetting<bool> IncrementalGc { get; } = new ModSetting<bool>(false,
            ModSettingDescriptor.Create("Incremental garbage collection")
                .SetTooltip("Without it, the game freezes for most of a second about once a minute in a large colony " +
                            "while memory is cleaned up; with it, that work is spread over many frames. Ticking this " +
                            "adds one line (gc-max-time-slice=3) to boot.config in the game's install folder, after " +
                            "saving a backup beside it; unticking removes the line. Takes effect the next time the " +
                            "game starts. Steam's 'verify integrity of game files' undoes it. Player.log says whether " +
                            "it worked. Does not affect the simulation, so multiplayer peers may differ."));

        public ModSetting<bool> WarnWhenNotIncremental { get; } = new ModSetting<bool>(true,
            ModSettingDescriptor.Create("Warn when garbage collection is not incremental")
                .SetTooltip("Shows a message in the main menu, once per launch, while garbage collection is not " +
                            "incremental and the setting above has not been ticked. A game update or Steam's " +
                            "'verify integrity of game files' can switch it off again without telling you."));

        public ModSetting<bool> AdaptiveGcPacing { get; } = new ModSetting<bool>(false,
            ModSettingDescriptor.Create("Adaptive garbage collection pacing (experimental)")
                .SetTooltip("Only with incremental garbage collection on. Instead of a fixed 3 ms of clean-up work per " +
                            "frame, does 6 ms while frames are fast and 8 ms while paused, and never less than 3 ms, " +
                            "so a clean-up cycle finishes sooner. One session each way showed no difference that " +
                            "could be told from noise, so this stays experimental. Applies at once; unticking " +
                            "restores the fixed slice. The Timing line in Player.log shows what it did. Does not " +
                            "affect the simulation, so multiplayer peers may differ."));

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

            // If the line is already there (added by hand), show the box ticked. If the box is ticked and the line
            // is gone, a game update or Steam's file check restored boot.config: ticking the box was the player's
            // standing consent, so the line is put back, once per launch, and the log says so. Nothing is written
            // for a player who never ticked it.
            try
            {
                bool inFile = BootConfig.FileHasKey(GcReport.BootConfigPath());
                if (!IncrementalGc.Value && inFile)
                {
                    IncrementalGc.SetValue(true);
                }
                else if (IncrementalGc.Value && !inFile)
                {
                    GcReport.ReapplyIncremental();
                }
            }
            catch (System.Exception)
            {
                // The startup GC line still says what state it is in.
            }
            GcPacing.Enabled = AdaptiveGcPacing.Value;
            AdaptiveGcPacing.ValueChanged += (_, value) => GcPacing.Enabled = value;
            IncrementalGc.ValueChanged += (_, value) => GcReport.ApplyIncremental(value);
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
