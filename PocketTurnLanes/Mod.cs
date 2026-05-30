using System;
using System.Linq;
using System.Reflection;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using PocketTurnLanes.Systems.Overlay;
using PocketTurnLanes.Systems.Tool;
using PocketTurnLanes.Systems.UI;

namespace PocketTurnLanes
{
    public class Mod : IMod
    {
        public const string ModId = "DedicatedTurnLanes";
        public const string DisplayName = "Dedicated Turn Lanes";
        public const string BindingGroup = ModId;

        public static ILog log = LogManager.GetLogger($"{ModId}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);

        public static bool TrafficLaneConnectionFixEnabled { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"{DisplayName} {nameof(OnLoad)} bindingGroup={BindingGroup}");

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info($"Current mod asset at {asset.path}");
            }

            bool trafficEnabled = IsTrafficModEnabled();
            TrafficLaneConnectionFixEnabled = trafficEnabled;

            updateSystem.World.GetOrCreateSystemManaged<Game.Tools.NetToolSystem>();
            updateSystem.World.GetOrCreateSystemManaged<IntersectionOverlaySystem>();
            updateSystem.World.GetOrCreateSystemManaged<IntersectionToolSystem>();
            updateSystem.World.GetOrCreateSystemManaged<PocketTurnLaneUISystem>();

            if (TrafficLaneConnectionFixEnabled)
            {
                updateSystem.World.GetOrCreateSystemManaged<SplitLaneConnectionFixSystem>();
            }

            updateSystem.UpdateAt<IntersectionToolSystem>(SystemUpdatePhase.ToolUpdate);
            RegisterSplitLaneConnectionFix(updateSystem, trafficEnabled);
            updateSystem.UpdateAt<PocketTurnLaneUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }

        private static void RegisterSplitLaneConnectionFix(UpdateSystem updateSystem, bool trafficEnabled)
        {
            if (!trafficEnabled)
            {
                log.Info("[SplitLaneConnectionFix] Traffic mod is not enabled; split-node lane connection repair system will not be registered.");
                return;
            }

            Type trafficLaneSystemType = FindType("Traffic.Systems.TrafficLaneSystem");
            Type syncCustomLaneConnectionsSystemType = FindType("Traffic.Systems.LaneConnections.SyncCustomLaneConnectionsSystem");

            bool registeredBeforeLaneSystem = TryRegisterUpdateOrder(
                updateSystem,
                nameof(UpdateSystem.UpdateBefore),
                trafficLaneSystemType,
                SystemUpdatePhase.Modification4,
                out string beforeError);
            bool registeredAfterSync = TryRegisterUpdateOrder(
                updateSystem,
                nameof(UpdateSystem.UpdateAfter),
                syncCustomLaneConnectionsSystemType,
                SystemUpdatePhase.Modification4,
                out string afterError);

            if (registeredBeforeLaneSystem && registeredAfterSync)
            {
                log.Info($"[SplitLaneConnectionFix] Scheduled in {SystemUpdatePhase.Modification4} after Traffic SyncCustomLaneConnectionsSystem and before TrafficLaneSystem so lane visuals refresh in the same lane-generation pass.");
                return;
            }

            if (registeredBeforeLaneSystem)
            {
                log.Warn($"[SplitLaneConnectionFix] Scheduled before TrafficLaneSystem in {SystemUpdatePhase.Modification4}, but could not constrain after SyncCustomLaneConnectionsSystem. afterError={afterError}");
                return;
            }

            updateSystem.UpdateAt<SplitLaneConnectionFixSystem>(SystemUpdatePhase.Modification4);
            log.Warn($"[SplitLaneConnectionFix] Traffic is enabled but ordered Traffic systems were not found; scheduled at {SystemUpdatePhase.Modification4} as a fallback. beforeError={beforeError} afterError={afterError}");
        }

        private static bool TryRegisterUpdateOrder(UpdateSystem updateSystem, string methodName, Type otherSystemType, SystemUpdatePhase phase, out string error)
        {
            error = string.Empty;
            if (otherSystemType == null)
            {
                error = "targetTypeNotFound";
                return false;
            }

            try
            {
                MethodInfo method = typeof(UpdateSystem)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(candidate =>
                        candidate.Name == methodName &&
                        candidate.IsGenericMethodDefinition &&
                        candidate.GetGenericArguments().Length == 2 &&
                        candidate.GetParameters().Length == 1 &&
                        candidate.GetParameters()[0].ParameterType == typeof(SystemUpdatePhase));

                if (method == null)
                {
                    error = $"methodNotFound:{methodName}";
                    return false;
                }

                method.MakeGenericMethod(typeof(SplitLaneConnectionFixSystem), otherSystemType)
                    .Invoke(updateSystem, new object[] { phase });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool IsTrafficModEnabled()
        {
            try
            {
                string[] enabledMods = GameManager.instance.modManager.ListModsEnabled()?.ToArray() ?? Array.Empty<string>();
                bool enabled = enabledMods.Any(IsTrafficModId);
                log.Info($"[SplitLaneConnectionFix] Traffic enabled check result={enabled} enabledMods={string.Join(" | ", enabledMods)}");
                return enabled;
            }
            catch (Exception ex)
            {
                log.Warn(ex, "[SplitLaneConnectionFix] Could not inspect enabled mods for Traffic; assuming Traffic is not enabled.");
                return false;
            }
        }

        private static bool IsTrafficModId(string modId)
        {
            return !string.IsNullOrEmpty(modId) &&
                   (modId.Equals("Traffic", StringComparison.OrdinalIgnoreCase) ||
                    modId.StartsWith("Traffic,", StringComparison.OrdinalIgnoreCase) ||
                    modId.IndexOf("Traffic, Version", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    modId.IndexOf("80095", StringComparison.OrdinalIgnoreCase) >= 0);
        }

    }
}
