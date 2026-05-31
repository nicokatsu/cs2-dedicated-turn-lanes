using System;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using PocketTurnLanes.Diagnostics;
using PocketTurnLanes.L10N;

namespace PocketTurnLanes.Options
{
    internal sealed class ModSettingsManager : IDisposable
    {
        private readonly IMod m_Mod;

        public ModSettingsManager(IMod mod)
        {
            m_Mod = mod;
        }

        public DedicatedTurnLaneSettings Settings { get; private set; }

        public bool DiagnosticLoggingEnabled => Settings?.EnableDiagnosticLogging ?? false;

        public void Load()
        {
            Settings = new DedicatedTurnLaneSettings(m_Mod);
            Settings.SetDefaults();

            try
            {
                OptionsLocalizationLoader.LoadFromAssembly(typeof(Mod).Assembly);
                Settings.RegisterInOptionsUI();
                AssetDatabase.global.LoadSettings(
                    DedicatedTurnLaneSettings.SettingsAssetName,
                    Settings,
                    new DedicatedTurnLaneSettings(m_Mod));

                ModLogger.LogEssential($"[Settings] Loaded settings enableDiagnosticLogging={Settings.EnableDiagnosticLogging} settingsAssetName=\"{DedicatedTurnLaneSettings.SettingsAssetName}\".");
            }
            catch (Exception ex)
            {
                ModLogger.LogException(ex, "[Settings] Failed to register or load settings; using in-memory defaults.");
            }
        }

        public void Dispose()
        {
            if (Settings == null)
            {
                return;
            }

            try
            {
                Settings.UnregisterInOptionsUI();
            }
            catch (Exception ex)
            {
                ModLogger.LogException(ex, "[Settings] Failed to unregister Options UI settings.");
            }
            finally
            {
                Settings = null;
            }
        }
    }
}
