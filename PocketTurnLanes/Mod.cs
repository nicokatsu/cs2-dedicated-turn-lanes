using System;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using PocketTurnLanes.Diagnostics;
using PocketTurnLanes.Options;
using PocketTurnLanes.Systems.Overlay;
using PocketTurnLanes.Systems.Tool;
using PocketTurnLanes.Systems.UI;
using PocketTurnLanes.Tool;

namespace PocketTurnLanes
{
    public class Mod : IMod
    {
        public const string ModId = "DedicatedTurnLanes";
        public const string DisplayName = "Dedicated Turn Lanes";
        public const string BindingGroup = ModId;

        public static readonly ILog log = LogManager.GetLogger(ModId).SetShowsErrorsInUI(false);

        private ModSettingsManager m_SettingsManager;

        public static bool TrafficModDetected { get; private set; }
        public static bool TrafficLaneConnectionFixEnabled { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            LogEssential($"{DisplayName} {nameof(OnLoad)} bindingGroup={BindingGroup}");

            m_SettingsManager = new ModSettingsManager(this);
            ModLogger.SetDiagnosticLoggingProvider(() => m_SettingsManager?.DiagnosticLoggingEnabled ?? false);
            m_SettingsManager.Load();

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                LogEssential($"Current mod asset at {asset.path}");
            }

            TrafficIntegrationStatus trafficStatus = TrafficIntegration.DetectTrafficMod();
            bool trafficDetected = trafficStatus.DetectedOnLoad;
            bool trafficRepairEnabled = TrafficIntegration.ShouldEnableLaneConnectionRepair(trafficDetected);
            trafficStatus.RepairSystemsEnabled = trafficRepairEnabled;
            trafficStatus.GuardedFallback = trafficRepairEnabled && !trafficDetected;
            TrafficIntegrationState.Update(trafficStatus);
            TrafficModDetected = trafficDetected;
            TrafficLaneConnectionFixEnabled = trafficRepairEnabled;
            LogEssential($"[SplitLaneConnectionFix] Traffic integration state trafficDetected={TrafficModDetected} repairSystemsEnabled={TrafficLaneConnectionFixEnabled} guardedFallback={trafficStatus.GuardedFallback}");

            updateSystem.World.GetOrCreateSystemManaged<Game.Tools.NetToolSystem>();
            updateSystem.World.GetOrCreateSystemManaged<IntersectionOverlaySystem>();
            updateSystem.World.GetOrCreateSystemManaged<IntersectionToolSystem>();
            updateSystem.World.GetOrCreateSystemManaged<PocketTurnLaneUISystem>();

            if (TrafficLaneConnectionFixEnabled)
            {
                updateSystem.World.GetOrCreateSystemManaged<SplitLaneConnectionFixSystem>();
                updateSystem.World.GetOrCreateSystemManaged<SplitLaneConnectionCleanupSystem>();
            }

            updateSystem.UpdateAt<IntersectionToolSystem>(SystemUpdatePhase.ToolUpdate);
            TrafficIntegration.RegisterLaneConnectionSystems(updateSystem, trafficRepairEnabled, trafficDetected);
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
