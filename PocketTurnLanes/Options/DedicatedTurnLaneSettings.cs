using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using PocketTurnLanes.Diagnostics;
using PocketTurnLanes.Tool.Traffic;

namespace PocketTurnLanes.Options
{
    [FileLocation(Mod.ModId)]
    [SettingsUITabOrder(GeneralTab)]
    [SettingsUIGroupOrder(TrafficGroup, DiagnosticsGroup)]
    [SettingsUIShowGroupName(TrafficGroup, DiagnosticsGroup)]
    public class DedicatedTurnLaneSettings : ModSetting
    {
        public const string SettingsAssetName = Mod.DisplayName + " Settings";
        public const string GeneralTab = "General";
        public const string TrafficGroup = "Traffic";
        public const string DiagnosticsGroup = "Diagnostics";

        public DedicatedTurnLaneSettings(IMod mod)
            : base(mod)
        {
        }

        [SettingsUISection(GeneralTab, TrafficGroup)]
        [SettingsUIValueVersion(typeof(TrafficIntegrationState), nameof(TrafficIntegrationState.GetVersion))]
        [SettingsUIWarning(typeof(TrafficIntegrationState), nameof(TrafficIntegrationState.ShowTrafficMissingWarning))]
        public string TrafficStatus => TrafficIntegrationState.StatusText;

        [SettingsUISection(GeneralTab, DiagnosticsGroup)]
        [SettingsUISetter(typeof(DedicatedTurnLaneSettings), nameof(OnDiagnosticLoggingChanged))]
        public bool EnableDiagnosticLogging { get; set; }

        public override void SetDefaults()
        {
            EnableDiagnosticLogging = false;
        }

        private void OnDiagnosticLoggingChanged(bool enabled)
        {
            ModLogger.LogEssential($"[Settings] Diagnostic logging set to {enabled}. Saving setting immediately.");
            AssetDatabase.global.SaveSpecificSetting(SettingsAssetName).ContinueWith(
                task => ModLogger.LogException(task.Exception, "[Settings] Failed to save diagnostic logging setting."),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
