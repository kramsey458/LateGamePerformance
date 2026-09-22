using Bindito.Core;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace LateGamePerformance
{
    // The in-game settings page (Mod Settings mod). Three boxes: the one decision that is the player's to make,
    // because it edits a file in the game's folder, and the two measurements a tester is asked to switch on for a
    // session. Nothing that affects the simulation is here (see Config); the other .cfg keys are for tracking a
    // problem down and a player never needs to see them. The two measurement boxes start from the .cfg value and
    // remember what the player last chose.
    //
    // This class is the only place that touches Mod Settings types. It pushes values into plain static fields on
    // the features, so nothing else in the mod depends on that assembly being loadable.
    public class PerformanceSettings : ModSettingsOwner
    {
        public ModSetting<bool> IncrementalGc { get; } = new ModSetting<bool>(false,
            ModSettingDescriptor.Create("Incremental garbage collection")
                .SetTooltip("Without it, the game freezes for most of a second about once a minute in a large colony " +
                            "while memory is cleaned up; with it, that work is spread over many frames. Ticking this " +
                            "adds one line (gc-max-time-slice=3) to boot.config in the game's install folder, after " +
                            "saving a backup beside it; unticking removes the line. Takes effect the next time the " +
                            "game starts. Steam's 'verify integrity of game files' undoes it. Player.log says whether " +
                            "it worked. Does not affect the simulation, so multiplayer peers may differ."));

        public ModSetting<bool> DiagnosticsTimers { get; } = new ModSetting<bool>(Plugin.Current.Diagnostics,
            ModSettingDescriptor.Create("Diagnostics timers")
                .SetTooltip("Time the game's own route map fills, need selection, walker path finding and terrain and " +
                            "road path searches, and log a Diagnostics: line with the stats every 1000 ticks. Adds a " +
                            "little overhead to hot code; switch it on for one session when asked for numbers. Does " +
                            "not affect the simulation, so multiplayer peers may differ."));

        public ModSetting<bool> VerifyTerrainSearches { get; } = new ModSetting<bool>(Plugin.Current.TerrainSearchVerify,
            ModSettingDescriptor.Create("Verify terrain path searches")
                .SetTooltip("Run the game's own terrain path search alongside this mod's resumed one and count every " +
                            "difference in the TerrainSearch: line (identical, within rounding, equally short other " +
                            "route, different distance). Never changes what the game is told; slower. For one test " +
                            "session. Does not affect the simulation, so multiplayer peers may differ."));

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
            IncrementalGc.ValueChanged += (_, value) => GcReport.ApplyIncremental(value);
            Diagnostics.Enabled = DiagnosticsTimers.Value;
            DiagnosticsTimers.ValueChanged += (_, value) => Diagnostics.Enabled = value;
            TerrainSearch.VerifyEnabled = VerifyTerrainSearches.Value;
            VerifyTerrainSearches.ValueChanged += (_, value) => TerrainSearch.VerifyEnabled = value;
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
