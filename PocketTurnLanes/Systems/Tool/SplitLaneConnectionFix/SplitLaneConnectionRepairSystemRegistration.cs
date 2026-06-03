using System;
using System.Linq;
using System.Reflection;
using Game;
using PocketTurnLanes.Tool.Traffic;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    internal static class SplitLaneConnectionRepairSystemRegistration
    {
        public static void Register(UpdateSystem updateSystem, bool repairEnabled, bool trafficDetected)
        {
            if (!repairEnabled)
            {
                Mod.LogEssential("[SplitLaneConnectionFix] Split-node lane connection repair system will not be registered.");
                return;
            }

            Type trafficLaneSystemType = TrafficIntegration.FindRuntimeType("Traffic.Systems.TrafficLaneSystem");
            Type syncCustomLaneConnectionsSystemType = TrafficIntegration.FindRuntimeType("Traffic.Systems.LaneConnections.SyncCustomLaneConnectionsSystem");
            Type modificationDataSyncSystemType = TrafficIntegration.FindRuntimeType("Traffic.Systems.ModificationDataSyncSystem");

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic type lookup trafficDetectedOnLoad={trafficDetected} TrafficLaneSystem={TrafficIntegration.FormatRuntimeType(trafficLaneSystemType)} SyncCustomLaneConnectionsSystem={TrafficIntegration.FormatRuntimeType(syncCustomLaneConnectionsSystemType)} ModificationDataSyncSystem={TrafficIntegration.FormatRuntimeType(modificationDataSyncSystemType)}.");

            bool registeredBeforeLaneSystem = TryRegisterUpdateOrder(
                updateSystem,
                typeof(SplitLaneConnectionFixSystem),
                nameof(UpdateSystem.UpdateBefore),
                trafficLaneSystemType,
                SystemUpdatePhase.Modification4,
                out string beforeError);
            string afterError = "notAttemptedBecauseBeforeTrafficLaneSystemFailed";
            bool registeredAfterSync = registeredBeforeLaneSystem && TryRegisterUpdateOrder(
                updateSystem,
                typeof(SplitLaneConnectionFixSystem),
                nameof(UpdateSystem.UpdateAfter),
                syncCustomLaneConnectionsSystemType,
                SystemUpdatePhase.Modification4,
                out afterError);

            RegisterSplitLaneConnectionCleanup(updateSystem, modificationDataSyncSystemType);

            if (registeredBeforeLaneSystem && registeredAfterSync)
            {
                Mod.LogEssential($"[SplitLaneConnectionFix] Pre-lane writer scheduled in {SystemUpdatePhase.Modification4} after Traffic SyncCustomLaneConnectionsSystem and before TrafficLaneSystem so lane visuals refresh in the same lane-generation pass.");
                return;
            }

            if (registeredBeforeLaneSystem)
            {
                Mod.LogEssential($"[SplitLaneConnectionFix] Pre-lane writer scheduled before TrafficLaneSystem in {SystemUpdatePhase.Modification4}, but could not constrain after SyncCustomLaneConnectionsSystem. afterError={afterError}");
                return;
            }

            updateSystem.UpdateAt<SplitLaneConnectionFixSystem>(SystemUpdatePhase.Modification3);
            Mod.LogEssential($"[SplitLaneConnectionFix] TrafficLaneSystem ordering was not available; pre-lane writer scheduled at {SystemUpdatePhase.Modification3} fallback so Updated markers are present before TrafficLaneSystem when Traffic is loaded. trafficDetectedOnLoad={trafficDetected} beforeError={beforeError} afterError={afterError}");
        }

        private static void RegisterSplitLaneConnectionCleanup(UpdateSystem updateSystem, Type modificationDataSyncSystemType)
        {
            if (TryRegisterUpdateOrder(
                    updateSystem,
                    typeof(SplitLaneConnectionCleanupSystem),
                    nameof(UpdateSystem.UpdateAfter),
                    modificationDataSyncSystemType,
                    SystemUpdatePhase.Modification4B,
                    out string cleanupAfterError))
            {
                Mod.LogEssential($"[SplitLaneConnectionFix] Post-lane cleanup scheduled in {SystemUpdatePhase.Modification4B} after Traffic ModificationDataSyncSystem; direct connector cleanup runs after TrafficLaneSystem has generated lanes.");
                return;
            }

            updateSystem.UpdateAt<SplitLaneConnectionCleanupSystem>(SystemUpdatePhase.Modification4B);
            Mod.LogEssential($"[SplitLaneConnectionFix] Post-lane cleanup scheduled at {SystemUpdatePhase.Modification4B} fallback. cleanupAfterError={cleanupAfterError}");
        }

        private static bool TryRegisterUpdateOrder(UpdateSystem updateSystem, Type systemType, string methodName, Type otherSystemType, SystemUpdatePhase phase, out string error)
        {
            error = string.Empty;
            if (systemType == null)
            {
                error = "systemTypeNotFound";
                return false;
            }

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

                method.MakeGenericMethod(systemType, otherSystemType)
                    .Invoke(updateSystem, new object[] { phase });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }
    }
}
