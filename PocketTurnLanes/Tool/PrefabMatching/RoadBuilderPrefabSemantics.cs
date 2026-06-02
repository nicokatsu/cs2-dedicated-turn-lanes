using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Colossal.Entities;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal sealed class RoadBuilderPrefabSemantics
    {
        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;

        internal RoadBuilderPrefabSemantics(EntityManager entityManager, PrefabSystem prefabSystem)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
        }

        public static bool LooksLikeRoadPrefabName(string prefabName)
        {
            return !string.IsNullOrEmpty(prefabName) &&
                   prefabName.Length > 2 &&
                   prefabName[0] == 'r' &&
                   prefabName.IndexOf("-765611", StringComparison.Ordinal) > 0;
        }

        public void ApplyConfigSemantics(Entity prefabEntity, ref RoadLaneProfile profile)
        {
            if (!TryBuildConfigLaneProfile(prefabEntity, out RoadLaneProfile configProfile, out string detail))
            {
                return;
            }

            bool changed = false;
            if (configProfile.BusLaneLayout.HasAny)
            {
                profile.BusLaneLayout = configProfile.BusLaneLayout;
                profile.BusLaneDetail = configProfile.BusLaneDetail;
                changed = true;
            }

            if (!configProfile.TramTrackCounts.IsEmpty)
            {
                profile.TramTrackCounts = configProfile.TramTrackCounts;
                profile.TramTrackLayout = configProfile.TramTrackLayout;
                profile.TramTrackDetail = configProfile.TramTrackDetail;
                profile.IndependentTramCounts = configProfile.IndependentTramCounts;
                profile.IndependentTramLayout = configProfile.IndependentTramLayout;
                profile.IndependentTramDetail = configProfile.IndependentTramDetail;
                profile.PublicTransportTramCounts = configProfile.PublicTransportTramCounts;
                profile.PublicTransportTramLayout = configProfile.PublicTransportTramLayout;
                profile.PublicTransportTramDetail = configProfile.PublicTransportTramDetail;
                changed = true;
            }

            if (changed)
            {
                profile.Source = $"{profile.Source}+RoadBuilderConfig";
                if (profile.BusLaneDetail != "<none>")
                {
                    profile.BusLaneDetail = $"{profile.BusLaneDetail} {detail}";
                }

                if (profile.TramTrackDetail != "<none>")
                {
                    profile.TramTrackDetail = $"{profile.TramTrackDetail} {detail}";
                }
            }
        }

        public void GetComponentProfile(
            Entity prefabEntity,
            out bool hasRoadBuilderComponent,
            out bool isDiscarded,
            out string detail)
        {
            hasRoadBuilderComponent = false;
            isDiscarded = false;
            detail = "roadBuilderComponents=none";

            if (prefabEntity == Entity.Null || !m_EntityManager.Exists(prefabEntity))
            {
                detail = "roadBuilderComponents=missing-prefab";
                return;
            }

            string componentSample = "<none>";
            int componentSampleCount = 0;
            NativeArray<ComponentType> componentTypes = default;
            try
            {
                componentTypes = m_EntityManager.GetComponentTypes(prefabEntity, Allocator.Temp);
                for (int i = 0; i < componentTypes.Length; i++)
                {
                    string typeName = GetComponentTypeName(componentTypes[i]);
                    if (typeName.IndexOf("RoadBuilder", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    hasRoadBuilderComponent = true;
                    ReplacementPrefabDiagnostics.AppendLogSample(ref componentSample, ref componentSampleCount, typeName, 8);
                    if (typeName.EndsWith(".DiscardedRoadBuilderPrefab", StringComparison.Ordinal) ||
                        typeName.EndsWith(".RoadBuilderToBeDeletedComponent", StringComparison.Ordinal))
                    {
                        isDiscarded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                detail = $"roadBuilderComponents=error {ex.GetType().Name}:{ex.Message}";
                return;
            }
            finally
            {
                if (componentTypes.IsCreated)
                {
                    componentTypes.Dispose();
                }
            }

            detail = $"roadBuilderComponents={(hasRoadBuilderComponent ? "matched" : "none")} discarded={isDiscarded} sample={componentSample}";
        }

        public bool TryGetPrefabVisibility(
            Entity prefabEntity,
            out bool isInPlayset,
            out string detail)
        {
            isInPlayset = true;

            if (!m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-prefab prefabEntity={FormatEntity(prefabEntity)}";
                return false;
            }

            object config = TryGetPropertyValue(prefabBase, "Config");
            if (config == null)
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-config prefab={prefabBase.name} prefabType={prefabBase.GetType().FullName}";
                return false;
            }

            Type configType = config.GetType();
            if (configType.FullName?.StartsWith("RoadBuilder.Domain.Configurations.", StringComparison.Ordinal) != true)
            {
                detail = $"roadBuilderVisibility=unknown reason=not-roadbuilder-config prefab={prefabBase.name} configType={configType.FullName}";
                return false;
            }

            string configDetail = GetConfigVisibilityDetail(config);
            Type extensionType = configType.Assembly.GetType("RoadBuilder.Utilities.NetworkConfigExtensionsUtil");
            if (extensionType == null)
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-extension-type prefab={prefabBase.name} {configDetail}";
                return false;
            }

            MethodInfo isInPlaysetMethod = FindIsInPlaysetMethod(extensionType, configType);
            if (isInPlaysetMethod == null)
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-is-in-playset-method prefab={prefabBase.name} {configDetail}";
                return false;
            }

            try
            {
                object result = isInPlaysetMethod.Invoke(null, new[] { config });
                if (result is bool visible)
                {
                    isInPlayset = visible;
                    detail = $"roadBuilderVisibility=matched visible={visible} prefab={prefabBase.name} {configDetail}";
                    return true;
                }

                detail = $"roadBuilderVisibility=unknown reason=non-bool-result resultType={result?.GetType().FullName ?? "<null>"} prefab={prefabBase.name} {configDetail}";
                return false;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                detail = $"roadBuilderVisibility=unknown reason=invoke-error error={inner.GetType().Name}:{inner.Message} prefab={prefabBase.name} {configDetail}";
                return false;
            }
            catch (Exception ex)
            {
                detail = $"roadBuilderVisibility=unknown reason=reflection-error error={ex.GetType().Name}:{ex.Message} prefab={prefabBase.name} {configDetail}";
                return false;
            }
        }

        private bool TryBuildConfigLaneProfile(
            Entity prefabEntity,
            out RoadLaneProfile profile,
            out string detail)
        {
            profile = CreateEmptyRoadLaneProfile("RoadBuilderConfig");
            detail = "roadBuilderConfig=none";

            if (!TryGetConfigLanes(prefabEntity, out List<RoadBuilderLaneConfig> lanes, out detail))
            {
                return false;
            }

            float totalWidth = 0f;
            for (int i = 0; i < lanes.Count; i++)
            {
                totalWidth += math.max(0f, lanes[i].Width);
            }

            if (totalWidth <= 0.01f)
            {
                detail = $"roadBuilderConfig=invalid-width lanes={lanes.Count}";
                return false;
            }

            int busLanes = 0;
            int tramLanes = 0;
            int independentTramLanes = 0;
            int publicTransportTramLanes = 0;
            string semanticSample = "<none>";
            int semanticSampleCount = 0;
            float offset = -totalWidth * 0.5f;

            for (int i = 0; i < lanes.Count; i++)
            {
                RoadBuilderLaneConfig lane = lanes[i];
                float laneWidth = math.max(0f, lane.Width);
                float centerOffset = offset + laneWidth * 0.5f;
                offset += laneWidth;

                if (!lane.IsBus && !lane.IsTram)
                {
                    continue;
                }

                // RoadBuilder writes LaneConfig.Invert into NetSectionInfo.m_Invert; for these lane groups that maps
                // to the runtime forward lane side, opposite to a raw LaneFlags.Invert interpretation.
                bool forward = lane.Invert;
                if (lane.IsBus)
                {
                    busLanes++;
                    AddDirectionalOffset(forward, centerOffset, ref profile.BusLaneLayout);
                    if (profile.BusLaneDetail == "<none>")
                    {
                        profile.BusLaneDetail = "roadBuilderConfig";
                    }
                }

                if (lane.IsTram)
                {
                    tramLanes++;
                    AddDirectionalLane(forward, ref profile.TramTrackCounts);
                    AddDirectionalOffset(forward, centerOffset, ref profile.TramTrackLayout);
                    if (profile.TramTrackDetail == "<none>")
                    {
                        profile.TramTrackDetail = "roadBuilderConfig";
                    }
                }

                if (lane.IsIndependentTram)
                {
                    independentTramLanes++;
                    AddDirectionalLane(forward, ref profile.IndependentTramCounts);
                    AddDirectionalOffset(forward, centerOffset, ref profile.IndependentTramLayout);
                    if (profile.IndependentTramDetail == "<none>")
                    {
                        profile.IndependentTramDetail = "roadBuilderConfig";
                    }
                }

                if (lane.IsPublicTransportTram)
                {
                    publicTransportTramLanes++;
                    AddDirectionalLane(forward, ref profile.PublicTransportTramCounts);
                    AddDirectionalOffset(forward, centerOffset, ref profile.PublicTransportTramLayout);
                    if (profile.PublicTransportTramDetail == "<none>")
                    {
                        profile.PublicTransportTramDetail = "roadBuilderConfig";
                    }
                }

                ReplacementPrefabDiagnostics.AppendLogSample(
                    ref semanticSample,
                    ref semanticSampleCount,
                    $"{i}:{lane.Semantic}/{(forward ? "F" : "B")}@{centerOffset:0.##}m width={laneWidth:0.##}m group={ShortPrefabName(lane.GroupPrefabName)} section={ShortPrefabName(lane.SectionPrefabName)} transport={GetOption(lane.GroupOptions, "Transport Option")}",
                    16);
            }

            detail = $"roadBuilderConfig=matched prefab={GetPrefabNameFromPrefab(prefabEntity)} lanes={lanes.Count} totalWidth={totalWidth:0.##}m busLanes={busLanes} tramLanes={tramLanes} independentTramLanes={independentTramLanes} publicTransportTramLanes={publicTransportTramLanes} busLayout={profile.BusLaneLayout} tramLayout={profile.TramTrackLayout} semanticSample={semanticSample}";
            return profile.BusLaneLayout.HasAny || !profile.TramTrackCounts.IsEmpty;
        }

        private bool TryGetConfigLanes(
            Entity prefabEntity,
            out List<RoadBuilderLaneConfig> lanes,
            out string detail)
        {
            lanes = null;
            detail = "roadBuilderConfig=none";

            if (!m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                detail = $"roadBuilderConfig=missing-prefab prefabEntity={FormatEntity(prefabEntity)}";
                return false;
            }

            object config = TryGetPropertyValue(prefabBase, "Config");
            if (config == null ||
                config.GetType().FullName?.StartsWith("RoadBuilder.Domain.Configurations.", StringComparison.Ordinal) != true)
            {
                detail = $"roadBuilderConfig=not-roadbuilder prefabType={prefabBase.GetType().FullName}";
                return false;
            }

            object rawLanes = TryGetPropertyValue(config, "Lanes");
            if (!(rawLanes is IEnumerable enumerable))
            {
                detail = $"roadBuilderConfig=missing-lanes configType={config.GetType().FullName}";
                return false;
            }

            lanes = new List<RoadBuilderLaneConfig>();
            foreach (object laneObject in enumerable)
            {
                if (laneObject == null)
                {
                    continue;
                }

                RoadBuilderLaneConfig lane = new RoadBuilderLaneConfig
                {
                    GroupPrefabName = TryGetPropertyValue(laneObject, "GroupPrefabName") as string ?? string.Empty,
                    SectionPrefabName = TryGetPropertyValue(laneObject, "SectionPrefabName") as string ?? string.Empty,
                    GroupOptions = ReadGroupOptions(TryGetPropertyValue(laneObject, "GroupOptions")),
                    Invert = TryGetPropertyValue(laneObject, "Invert") is bool invert && invert
                };
                lane.Width = GetLaneWidth(lane);
                ClassifyLane(ref lane);
                lanes.Add(lane);
            }

            detail = $"roadBuilderConfig=loaded prefab={prefabBase.name} lanes={lanes.Count} configType={config.GetType().FullName}";
            return lanes.Count > 0;
        }

        private static RoadLaneProfile CreateEmptyRoadLaneProfile(string source)
        {
            return new RoadLaneProfile
            {
                DrivableLaneEnvelopeDetail = "<none>",
                MarkedParkingDetail = "<none>",
                TramTrackDetail = "<none>",
                IndependentTramDetail = "<none>",
                PublicTransportTramDetail = "<none>",
                BusLaneDetail = "<none>",
                Source = source
            };
        }

        private string GetPrefabNameFromPrefab(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null)
            {
                return "<null prefab>";
            }

            if (m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                return prefabBase.name;
            }

            return $"<unresolved {FormatEntity(prefabEntity)}>";
        }

        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }

        private static MethodInfo FindIsInPlaysetMethod(Type extensionType, Type configType)
        {
            MethodInfo[] methods = extensionType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "IsInPlayset", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 &&
                    parameters[0].ParameterType.IsAssignableFrom(configType))
                {
                    return method;
                }
            }

            return null;
        }

        private static string GetConfigVisibilityDetail(object config)
        {
            Type configType = config.GetType();
            string configId = TryGetPropertyValue(config, "ID")?.ToString() ?? "<missing>";
            string configName = TryGetPropertyValue(config, "Name")?.ToString() ?? "<missing>";
            string playsets = FormatPlaysets(TryGetPropertyValue(config, "Playsets"));
            string currentPlayset = TryGetStaticPropertyValue(
                    configType.Assembly.GetType("RoadBuilder.Utilities.PdxModsUtil"),
                    "CurrentPlayset")
                ?.ToString() ?? "<missing>";
            object settings = TryGetStaticPropertyValue(
                configType.Assembly.GetType("RoadBuilder.Mod"),
                "Settings");
            string noPlaysetIsolation = TryGetPropertyValue(settings, "NoPlaysetIsolation")?.ToString() ?? "<missing>";

            return $"configId={configId} configName={configName} playsets={playsets} currentPlayset={currentPlayset} noPlaysetIsolation={noPlaysetIsolation} configType={configType.FullName}";
        }

        private static object TryGetStaticPropertyValue(Type type, string propertyName)
        {
            if (type == null)
            {
                return null;
            }

            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(null);
        }

        private static string FormatPlaysets(object rawPlaysets)
        {
            if (rawPlaysets == null)
            {
                return "<null>";
            }

            if (rawPlaysets is string playset)
            {
                return string.IsNullOrEmpty(playset) ? "<empty>" : playset;
            }

            if (rawPlaysets is IEnumerable enumerable)
            {
                string samples = "<none>";
                int sampleCount = 0;
                foreach (object item in enumerable)
                {
                    ReplacementPrefabDiagnostics.AppendLogSample(ref samples, ref sampleCount, item?.ToString() ?? "<null>", 16);
                }

                return samples;
            }

            return rawPlaysets.ToString() ?? "<unknown>";
        }

        private static object TryGetPropertyValue(object instance, string propertyName)
        {
            if (instance == null)
            {
                return null;
            }

            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance);
        }

        private static Dictionary<string, string> ReadGroupOptions(object rawOptions)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (rawOptions == null)
            {
                return result;
            }

            if (rawOptions is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    string key = entry.Key?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(key))
                    {
                        result[key] = entry.Value?.ToString() ?? string.Empty;
                    }
                }

                return result;
            }

            if (rawOptions is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    string key = TryGetPropertyValue(item, "Key")?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(key))
                    {
                        result[key] = TryGetPropertyValue(item, "Value")?.ToString() ?? string.Empty;
                    }
                }
            }

            return result;
        }

        private static string GetOption(Dictionary<string, string> options, string optionName)
        {
            if (options == null)
            {
                return string.Empty;
            }

            return options.TryGetValue(optionName, out string value) ? value ?? string.Empty : string.Empty;
        }

        private static float GetLaneWidth(RoadBuilderLaneConfig lane)
        {
            if (TryGetOptionMeters(lane.GroupOptions, "Lane Width", out float laneWidth) ||
                TryGetOptionMeters(lane.GroupOptions, "Median Width", out laneWidth) ||
                TryGetOptionMeters(lane.GroupOptions, "Width", out laneWidth) ||
                TryGetOptionMeters(lane.GroupOptions, "Shoulder Width", out laneWidth) ||
                TryGetOptionMeters(lane.GroupOptions, "Parking Width", out laneWidth))
            {
                return laneWidth;
            }

            if (TryExtractMeterValue(lane.SectionPrefabName, out laneWidth))
            {
                return laneWidth;
            }

            if (PrefabNameContains(lane.GroupPrefabName, "MedianGroupPrefab"))
            {
                return 1f;
            }

            if (PrefabNameContains(lane.GroupPrefabName, "SidewalkGroupPrefab"))
            {
                return 3f;
            }

            if (PrefabNameContains(lane.GroupPrefabName, "TramGroupPrefab") ||
                PrefabNameContains(lane.GroupPrefabName, "BusGroupPrefab") ||
                PrefabNameContains(lane.GroupPrefabName, "CarGroupPrefab") ||
                PrefabNameContains(lane.GroupPrefabName, "HarborCarGroupPrefab"))
            {
                return 3f;
            }

            return 0f;
        }

        private static bool TryGetOptionMeters(
            Dictionary<string, string> options,
            string optionName,
            out float meters)
        {
            meters = 0f;
            return options != null &&
                   options.TryGetValue(optionName, out string rawValue) &&
                   TryParseMeters(rawValue, out meters);
        }

        private static bool TryExtractMeterValue(string text, out float meters)
        {
            meters = 0f;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            Match match = Regex.Match(text, @"(?<!\d)(\d+(?:[\.,]\d+)?)\s*m\b", RegexOptions.IgnoreCase);
            return match.Success && TryParseMeters(match.Groups[1].Value, out meters);
        }

        private static bool TryParseMeters(string rawValue, out float meters)
        {
            meters = 0f;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            string value = rawValue.Trim();
            if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 1);
            }

            value = value.Replace(',', '.');
            return float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out meters);
        }

        private static void ClassifyLane(ref RoadBuilderLaneConfig lane)
        {
            string transportOption = GetOption(lane.GroupOptions, "Transport Option");
            bool carGroup = PrefabNameContains(lane.GroupPrefabName, "CarGroupPrefab") ||
                            PrefabNameContains(lane.GroupPrefabName, "HarborCarGroupPrefab");
            bool busGroup = PrefabNameContains(lane.GroupPrefabName, "BusGroupPrefab");
            bool tramGroup = PrefabNameContains(lane.GroupPrefabName, "TramGroupPrefab");
            bool sectionPublicTransport = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Public Transport Lane");
            bool sectionTransportOption = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Transport Option");
            bool sectionTramOption = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Tram Option") ||
                                     StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Transport Tram");
            bool sectionTramTrack = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Tram Track");
            bool transportIsBus = string.Equals(transportOption, "Transport", StringComparison.OrdinalIgnoreCase);
            bool transportIsTram = string.Equals(transportOption, "Tram", StringComparison.OrdinalIgnoreCase);

            lane.IsBus = busGroup ||
                         sectionPublicTransport ||
                         (carGroup && (transportIsBus || transportIsTram)) ||
                         sectionTransportOption;
            lane.IsIndependentTram = tramGroup ||
                                     (sectionTramTrack && !sectionPublicTransport && !sectionTransportOption);
            lane.IsPublicTransportTram = !lane.IsIndependentTram &&
                                         (transportIsTram || sectionTramOption) &&
                                         (lane.IsBus || carGroup || busGroup || sectionPublicTransport || sectionTransportOption);
            lane.IsTram = lane.IsIndependentTram || lane.IsPublicTransportTram;

            if (lane.IsIndependentTram)
            {
                lane.Semantic = "tram-independent";
            }
            else if (lane.IsPublicTransportTram && lane.IsBus)
            {
                lane.Semantic = "pt-tram";
            }
            else if (lane.IsBus)
            {
                lane.Semantic = "pt";
            }
            else if (lane.IsPublicTransportTram)
            {
                lane.Semantic = "tram-pt";
            }
            else
            {
                lane.Semantic = "none";
            }
        }

        private static void AddDirectionalLane(bool forward, ref RoadLaneCounts counts)
        {
            if (forward)
            {
                counts.Forward++;
            }
            else
            {
                counts.Backward++;
            }
        }

        private static void AddDirectionalOffset(
            bool forward,
            float lateralOffset,
            ref DirectionalLaneOffsetProfile profile)
        {
            if (forward)
            {
                profile.ForwardCount++;
                profile.ForwardOffsetSum += lateralOffset;
            }
            else
            {
                profile.BackwardCount++;
                profile.BackwardOffsetSum += lateralOffset;
            }
        }

        private static bool PrefabNameContains(string prefabName, string suffix)
        {
            return !string.IsNullOrEmpty(prefabName) &&
                   prefabName.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StringContainsOrdinalIgnoreCase(string value, string pattern)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ShortPrefabName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return "<none>";
            }

            int lastDot = prefabName.LastIndexOf('.');
            return lastDot >= 0 && lastDot < prefabName.Length - 1
                ? prefabName.Substring(lastDot + 1)
                : prefabName;
        }

        private static string GetComponentTypeName(ComponentType componentType)
        {
            try
            {
                Type managedType = componentType.GetManagedType();
                return managedType?.FullName ?? componentType.ToString();
            }
            catch
            {
                return componentType.ToString();
            }
        }

        private struct RoadBuilderLaneConfig
        {
            public string GroupPrefabName;
            public string SectionPrefabName;
            public Dictionary<string, string> GroupOptions;
            public bool Invert;
            public float Width;
            public bool IsBus;
            public bool IsTram;
            public bool IsIndependentTram;
            public bool IsPublicTransportTram;
            public string Semantic;
        }
    }
}
