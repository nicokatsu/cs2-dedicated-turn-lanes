using System;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using PocketTurnLanes.Diagnostics;
using PocketTurnLanes.Options;
using PocketTurnLanes.Systems.Overlay;
using PocketTurnLanes.Systems.Tool.IntersectionTool;
using PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix;
using PocketTurnLanes.Systems.UI;
using PocketTurnLanes.Tool.Traffic;

namespace PocketTurnLanes
{
    public class Mod : IMod
    {
#if DEDICATED_TURN_LANES_DEV
        public const string ModChannel = "Dev";
        public const string ModId = "DedicatedTurnLanesDev";
        public const string DisplayName = "Dedicated Turn Lanes Dev";
#elif DEDICATED_TURN_LANES_ALPHA
        public const string ModChannel = "Alpha";
        public const string ModId = "DedicatedTurnLanesAlpha";
        public const string DisplayName = "Dedicated Turn Lanes Alpha";
#else
        public const string ModChannel = "Stable";
        public const string ModId = "DedicatedTurnLanes";
        public const string DisplayName = "Dedicated Turn Lanes";
#endif
        public const string BindingGroup = ModId;

        public static readonly ILog log = LogManager.GetLogger(ModId).SetShowsErrorsInUI(false);

        private ModSettingsManager m_SettingsManager;

        public static bool TrafficModDetected { get; private set; }
        public static bool TrafficLaneConnectionFixEnabled { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            LogEssential($"{DisplayName} {nameof(OnLoad)} channel={ModChannel} modId={ModId} bindingGroup={BindingGroup}");

            InitializeSettings();
            LogCurrentModAsset();
            InitializeTrafficIntegration();
            CreateSystems(updateSystem);
            RegisterSystemUpdates(updateSystem);
        }

        private void InitializeSettings()
        {
            m_SettingsManager = new ModSettingsManager(this);
            ModLogger.SetDiagnosticLoggingProvider(() => m_SettingsManager?.DiagnosticLoggingEnabled ?? false);
            m_SettingsManager.Load();
        }

        private void LogCurrentModAsset()
        {
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                LogEssential($"Current mod asset at {asset.path}");
            }
        }

        private static void InitializeTrafficIntegration()
        {
            TrafficIntegrationStatus trafficStatus = TrafficIntegration.DetectTrafficMod();
            bool trafficDetected = trafficStatus.DetectedOnLoad;
            bool trafficRepairEnabled = TrafficIntegration.ShouldEnableLaneConnectionRepair(trafficDetected);
            trafficStatus.RepairSystemsEnabled = trafficRepairEnabled;
            trafficStatus.GuardedFallback = trafficRepairEnabled && !trafficDetected;
            TrafficIntegrationState.Update(trafficStatus);
            TrafficModDetected = trafficDetected;
            TrafficLaneConnectionFixEnabled = trafficRepairEnabled;
            LogEssential($"[SplitLaneConnectionFix] Traffic integration state trafficDetected={TrafficModDetected} repairSystemsEnabled={TrafficLaneConnectionFixEnabled} guardedFallback={trafficStatus.GuardedFallback}");
        }

        private static void CreateSystems(UpdateSystem updateSystem)
        {
            updateSystem.World.GetOrCreateSystemManaged<Game.Tools.NetToolSystem>();
            updateSystem.World.GetOrCreateSystemManaged<IntersectionOverlaySystem>();
            updateSystem.World.GetOrCreateSystemManaged<DedicatedTurnLanesToolEntryPrefabSystem>();
            updateSystem.World.GetOrCreateSystemManaged<IntersectionToolSystem>();
            updateSystem.World.GetOrCreateSystemManaged<PocketTurnLaneUISystem>();

            if (TrafficLaneConnectionFixEnabled)
            {
                updateSystem.World.GetOrCreateSystemManaged<SplitLaneConnectionFixSystem>();
                updateSystem.World.GetOrCreateSystemManaged<SplitLaneConnectionCleanupSystem>();
            }
        }

        private static void RegisterSystemUpdates(UpdateSystem updateSystem)
        {
            updateSystem.UpdateAt<DedicatedTurnLanesToolEntryPrefabSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<IntersectionToolSystem>(SystemUpdatePhase.ToolUpdate);
            SplitLaneConnectionRepairSystemRegistration.Register(updateSystem, TrafficLaneConnectionFixEnabled, TrafficModDetected);
            updateSystem.UpdateAt<PocketTurnLaneUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            LogEssential(nameof(OnDispose));

            m_SettingsManager?.Dispose();
            m_SettingsManager = null;
            ModLogger.SetDiagnosticLoggingProvider(null);
        }

        internal static void UpdateTrafficRuntimeStatus(bool runtimeReady, string lastRuntimeError, int waitFrames)
        {
            TrafficIntegrationState.UpdateRuntime(runtimeReady, lastRuntimeError, waitFrames);
        }

        internal static void LogEssential(string message)
        {
            ModLogger.LogEssential(message);
        }

        internal static void LogDiagnostic(string message)
        {
            ModLogger.LogDiagnostic(message);
        }

        internal static void LogDiagnostic(Exception exception, string message)
        {
            ModLogger.LogDiagnostic(exception, message);
        }

        internal static void LogException(Exception exception, string message)
        {
            ModLogger.LogException(exception, message);
        }
    }
}
