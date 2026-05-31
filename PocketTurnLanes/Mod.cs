using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
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

        public static bool TrafficModDetected { get; private set; }
        public static bool TrafficLaneConnectionFixEnabled { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"{DisplayName} {nameof(OnLoad)} bindingGroup={BindingGroup}");

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
            }

            bool trafficDetected = TrafficIntegration.IsTrafficModEnabled();
            bool trafficRepairEnabled = TrafficIntegration.ShouldEnableLaneConnectionRepair(trafficDetected);
            TrafficModDetected = trafficDetected;
            TrafficLaneConnectionFixEnabled = trafficRepairEnabled;
            log.Info($"[SplitLaneConnectionFix] Traffic integration state trafficDetected={TrafficModDetected} repairSystemsEnabled={TrafficLaneConnectionFixEnabled}");

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
            log.Info(nameof(OnDispose));
        }
    }
}
