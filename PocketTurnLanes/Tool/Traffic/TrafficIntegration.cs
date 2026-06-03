using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.SceneFlow;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficIntegration
    {
        private const string TrafficAssemblyName = "Traffic";
        private const string TrafficWorkshopId = "80095";
        private static readonly bool TrafficDependencyDeclared = true;
        private static readonly string[] TrafficRuntimeTypeNames =
        {
            "Traffic.Mod",
            "Traffic.Systems.TrafficLaneSystem",
            "Traffic.Systems.LaneConnections.SyncCustomLaneConnectionsSystem",
            "Traffic.Systems.ModificationDataSyncSystem",
            "Traffic.Components.LaneConnections.ModifiedLaneConnections"
        };

        public static bool IsTrafficModEnabled()
        {
            return DetectTrafficMod().DetectedOnLoad;
        }

        public static TrafficIntegrationStatus DetectTrafficMod()
        {
            TryGetEnabledMods(out string[] enabledMods, out string enabledModsError);
            string[] enabledModMatches = enabledMods.Where(IsTrafficModId).ToArray();
            bool assemblyLoaded = TryFindTrafficAssembly(out string assemblyDetail);
            int foundRuntimeTypes = CountTrafficRuntimeTypes(out string runtimeTypeDetail);
            bool enabled = enabledModMatches.Length > 0 || assemblyLoaded || foundRuntimeTypes > 0;
            TrafficIntegrationStatus status = new TrafficIntegrationStatus
            {
                DetectionCompleted = true,
                DetectedOnLoad = enabled,
                RuntimeReady = false,
                LastRuntimeError = enabled ? "Traffic API has not been initialized yet" : "Traffic was not detected during OnLoad",
                EnabledModMatches = FormatList(enabledModMatches),
                EnabledModsCount = enabledMods.Length,
                EnabledModsError = enabledModsError,
                LoadedAssembly = assemblyDetail,
                RuntimeTypesFound = foundRuntimeTypes,
                RuntimeTypesExpected = TrafficRuntimeTypeNames.Length,
                RuntimeTypes = runtimeTypeDetail
            };

            Mod.LogEssential($"[SplitLaneConnectionFix] Traffic enabled check result={enabled} enabledModMatches={status.EnabledModMatches} runtimeTypesFound={foundRuntimeTypes}/{TrafficRuntimeTypeNames.Length} loadedAssembly={assemblyLoaded}.");
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic enabled check detail enabledModsCount={enabledMods.Length} enabledMods={FormatList(enabledMods)} enabledModsError={enabledModsError} loadedAssembly={assemblyDetail} runtimeTypes={runtimeTypeDetail}");
            return status;
        }

        public static bool ShouldEnableLaneConnectionRepair(bool trafficDetected)
        {
            if (trafficDetected)
            {
                Mod.LogEssential("[SplitLaneConnectionFix] Traffic lane connection repair systems enabled because Traffic was detected during OnLoad.");
                return true;
            }

            if (TrafficDependencyDeclared)
            {
                Mod.LogEssential($"[SplitLaneConnectionFix] Traffic was not positively detected during OnLoad; registering split-node lane connection repair systems in guarded fallback mode because dependency {TrafficWorkshopId} is declared. Runtime reflection will wait briefly and skip queued repairs if Traffic remains unavailable.");
                return true;
            }

            Mod.LogEssential("[SplitLaneConnectionFix] Traffic was not positively detected during OnLoad and no Traffic dependency is declared; split-node lane connection repair systems will stay disabled.");
            return false;
        }

        public static Type FindRuntimeType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // Some generated/dynamic assemblies can reject type lookup; continue scanning exact Traffic evidence.
                }
            }

            return null;
        }

        public static string FormatRuntimeType(Type type)
        {
            return type == null
                ? "missing"
                : $"{type.FullName}, assembly={type.Assembly.GetName().Name}";
        }

        private static bool TryGetEnabledMods(out string[] enabledMods, out string error)
        {
            try
            {
                enabledMods = GameManager.instance.modManager.ListModsEnabled()?.ToArray() ?? Array.Empty<string>();
                error = "none";
                return true;
            }
            catch (Exception ex)
            {
                enabledMods = Array.Empty<string>();
                error = $"{ex.GetType().Name}:{ex.Message}";
                Mod.LogException(ex, "[SplitLaneConnectionFix] Could not inspect enabled mods for Traffic; falling back to loaded Traffic assembly/type evidence.");
                return false;
            }
        }

        private static bool TryFindTrafficAssembly(out string detail)
        {
            List<string> matches = new List<string>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    AssemblyName assemblyName = assembly.GetName();
                    if (!assemblyName.Name.Equals(TrafficAssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.Add(FormatAssembly(assembly));
                }
                catch
                {
                    // Ignore assemblies that cannot expose metadata; exact Traffic type lookup covers the useful case.
                }
            }

            detail = FormatList(matches);
            return matches.Count > 0;
        }

        private static int CountTrafficRuntimeTypes(out string detail)
        {
            List<string> foundTypes = new List<string>();
            for (int i = 0; i < TrafficRuntimeTypeNames.Length; i++)
            {
                Type type = FindRuntimeType(TrafficRuntimeTypeNames[i]);
                if (type != null)
                {
                    foundTypes.Add($"{type.FullName}@{type.Assembly.GetName().Name}");
                }
            }

            detail = FormatList(foundTypes);
            return foundTypes.Count;
        }

        private static string FormatAssembly(Assembly assembly)
        {
            try
            {
                AssemblyName assemblyName = assembly.GetName();
                string location = assembly.IsDynamic ? "<dynamic>" : assembly.Location;
                return $"{assemblyName.Name}, Version={assemblyName.Version}, Location={location}";
            }
            catch (Exception ex)
            {
                return $"<assembly-format-error:{ex.GetType().Name}:{ex.Message}>";
            }
        }

        private static string FormatList(IEnumerable<string> values)
        {
            string[] nonEmptyValues = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return nonEmptyValues.Length == 0
                ? "<none>"
                : string.Join(" | ", nonEmptyValues);
        }

        private static bool IsTrafficModId(string modId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return false;
            }

            string value = modId.Trim();
            if (value.Equals(TrafficAssemblyName, StringComparison.OrdinalIgnoreCase) ||
                value.Equals(TrafficWorkshopId, StringComparison.OrdinalIgnoreCase) ||
                ContainsDelimitedToken(value, TrafficWorkshopId))
            {
                return true;
            }

            try
            {
                AssemblyName assemblyName = new AssemblyName(value);
                return assemblyName.Name.Equals(TrafficAssemblyName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsDelimitedToken(string value, string token)
        {
            int index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                bool validBefore = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
                int afterIndex = index + token.Length;
                bool validAfter = afterIndex >= value.Length || !char.IsLetterOrDigit(value[afterIndex]);
                if (validBefore && validAfter)
                {
                    return true;
                }

                index = value.IndexOf(token, index + token.Length, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
