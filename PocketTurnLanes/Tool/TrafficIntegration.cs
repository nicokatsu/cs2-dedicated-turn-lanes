using System;
using System.Linq;
using System.Reflection;
using Game;
using Game.SceneFlow;
using PocketTurnLanes.Systems.Tool;

namespace PocketTurnLanes.Tool
{
    internal static class TrafficIntegration
    {
        public static bool IsTrafficModEnabled()
        {
            try
            {
                string[] enabledMods = GameManager.instance.modManager.ListModsEnabled()?.ToArray() ?? Array.Empty<string>();
                bool enabled = enabledMods.Any(IsTrafficModId);
                Mod.log.Info($"[SplitLaneConnectionFix] Traffic enabled check result={enabled} enabledMods={string.Join(" | ", enabledMods)}");
                return enabled;
            }
            catch (Exception ex)
            {
                Mod.log.Warn(ex, "[SplitLaneConnectionFix] Could not inspect enabled mods for Traffic; assuming Traffic is not enabled.");
                return false;
            }
        }

        public static void RegisterLaneConnectionSystems(UpdateSystem updateSystem, bool trafficEnabled)
        {
            if (!trafficEnabled)
            {
                Mod.log.Info("[SplitLaneConnectionFix] Traffic mod is not enabled; split-node lane connection repair system will not be registered.");
                return;
            }

            Type trafficLaneSystemType = FindType("Traffic.Systems.TrafficLaneSystem");
            Type syncCustomLaneConnectionsSystemType = FindType("Traffic.Systems.LaneConnections.SyncCustomLaneConnectionsSystem");
            Type modificationDataSyncSystemType = FindType("Traffic.Systems.ModificationDataSyncSystem");

            Mod.log.Info($"[SplitLaneConnectionFix] Traffic type lookup TrafficLaneSystem={FormatType(trafficLaneSystemType)} SyncCustomLaneConnectionsSystem={FormatType(syncCustomLaneConnectionsSystemType)} ModificationDataSyncSystem={FormatType(modificationDataSyncSystemType)}.");

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
                Mod.log.Info($"[SplitLaneConnectionFix] Pre-lane writer scheduled in {SystemUpdatePhase.Modification4} after Traffic SyncCustomLaneConnectionsSystem and before TrafficLaneSystem so lane visuals refresh in the same lane-generation pass.");
                return;
            }

            if (registeredBeforeLaneSystem)
            {
                Mod.log.Warn($"[SplitLaneConnectionFix] Pre-lane writer scheduled before TrafficLaneSystem in {SystemUpdatePhase.Modification4}, but could not constrain after SyncCustomLaneConnectionsSystem. afterError={afterError}");
                return;
            }

            updateSystem.UpdateAt<SplitLaneConnectionFixSystem>(SystemUpdatePhase.Modification3);
            Mod.log.Warn($"[SplitLaneConnectionFix] Traffic is enabled but TrafficLaneSystem ordering was not available; pre-lane writer scheduled at {SystemUpdatePhase.Modification3} fallback so Updated markers are present before TrafficLaneSystem. beforeError={beforeError} afterError={afterError}");
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
                Mod.log.Info($"[SplitLaneConnectionFix] Post-lane cleanup scheduled in {SystemUpdatePhase.Modification4B} after Traffic ModificationDataSyncSystem; direct connector cleanup runs after TrafficLaneSystem has generated lanes.");
                return;
            }

            updateSystem.UpdateAt<SplitLaneConnectionCleanupSystem>(SystemUpdatePhase.Modification4B);
            Mod.log.Warn($"[SplitLaneConnectionFix] Post-lane cleanup scheduled at {SystemUpdatePhase.Modification4B} fallback. cleanupAfterError={cleanupAfterError}");
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

        private static string FormatType(Type type)
        {
            return type == null
                ? "missing"
                : $"{type.FullName}, assembly={type.Assembly.GetName().Name}";
        }

        private static bool IsTrafficModId(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return false;
            }

            string value = modId.Trim();
            if (value.Equals("Traffic", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("80095", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                AssemblyName assemblyName = new AssemblyName(value);
                return assemblyName.Name.Equals("Traffic", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
