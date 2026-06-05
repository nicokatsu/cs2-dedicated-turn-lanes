using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private sealed class TrafficLoadValidationContext
        {
            public readonly Dictionary<Entity, TrafficLoadValidationEdge> Edges =
                new Dictionary<Entity, TrafficLoadValidationEdge>();

            public Entity OwnerNode;
        }

        private sealed class TrafficLoadValidationEdge
        {
            public readonly Dictionary<int, NetCompositionLane> CompositionLanes =
                new Dictionary<int, NetCompositionLane>();

            public bool IsRoad;
            public bool IsBike;
            public TrackTypes TrackTypes;
        }

        private bool TryCreateTrafficLoadValidationContext(
            Entity ownerNode,
            string stage,
            out TrafficLoadValidationContext context,
            out string reason)
        {
            context = new TrafficLoadValidationContext
            {
                OwnerNode = ownerNode
            };
            reason = string.Empty;

            if (ownerNode == Entity.Null || !EntityManager.Exists(ownerNode))
            {
                reason = $"ownerNodeMissing ownerNode={FormatEntity(ownerNode)} stage={stage}";
                return false;
            }

            if (!EntityManager.HasComponent<Node>(ownerNode))
            {
                reason = $"ownerNodeHasNoNodeComponent ownerNode={FormatEntity(ownerNode)} stage={stage}";
                return false;
            }

            if (!EntityManager.TryGetBuffer(ownerNode, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                reason = $"ownerNodeHasNoConnectedEdgeBuffer ownerNode={FormatEntity(ownerNode)} stage={stage}";
                return false;
            }

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (!EntityManager.TryGetComponent(edgeEntity, out Composition composition))
                {
                    reason = $"edgeCompositionMissing ownerNode={FormatEntity(ownerNode)} edge={FormatEntity(edgeEntity)} stage={stage}";
                    return false;
                }

                if (i == 0 &&
                    EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) &&
                    EntityManager.TryGetComponent(
                        edge.m_End == ownerNode ? composition.m_EndNode : composition.m_StartNode,
                        out NetCompositionData nodeCompositionData) &&
                    (nodeCompositionData.m_Flags.m_General & CompositionFlags.General.Roundabout) != 0)
                {
                    reason = $"roundaboutUnsupported ownerNode={FormatEntity(ownerNode)} edge={FormatEntity(edgeEntity)} stage={stage}";
                    return false;
                }

                Entity edgeComposition = composition.m_Edge;
                bool isRoad = EntityManager.HasComponent<RoadComposition>(edgeComposition);
                TrackTypes trackTypes = EntityManager.TryGetComponent(edgeComposition, out TrackComposition trackComposition)
                    ? trackComposition.m_TrackType
                    : TrackTypes.None;

                if (!EntityManager.TryGetBuffer(edgeComposition, true, out DynamicBuffer<NetCompositionLane> lanes))
                {
                    continue;
                }

                TrafficLoadValidationEdge validationEdge = new TrafficLoadValidationEdge
                {
                    IsRoad = isRoad,
                    IsBike = false,
                    TrackTypes = trackTypes
                };
                bool isBike = false;
                TrackTypes laneTrackTypes = TrackTypes.None;
                for (int laneIndex = 0; laneIndex < lanes.Length; laneIndex++)
                {
                    NetCompositionLane lane = lanes[laneIndex];
                    if ((lane.m_Flags & (LaneFlags.Road | LaneFlags.BicyclesOnly | LaneFlags.Slave | LaneFlags.Track)) == 0)
                    {
                        continue;
                    }

                    validationEdge.CompositionLanes[laneIndex] = lane;
                    if ((lane.m_Flags & LaneFlags.Track) != 0 &&
                        lane.m_Lane != Entity.Null &&
                        EntityManager.TryGetComponent(lane.m_Lane, out TrackLaneData trackLane))
                    {
                        laneTrackTypes |= trackLane.m_TrackTypes;
                    }

                    if ((lane.m_Flags & LaneFlags.Road) != 0 &&
                        lane.m_Lane != Entity.Null &&
                        EntityManager.TryGetComponent(lane.m_Lane, out CarLaneData carLane))
                    {
                        isBike |= (carLane.m_RoadTypes & RoadTypes.Bicycle) != 0;
                    }

                    if ((lane.m_Flags & LaneFlags.BicyclesOnly) != 0 &&
                        lane.m_Lane != Entity.Null &&
                        EntityManager.TryGetComponent(lane.m_Lane, out NetLaneData netLane))
                    {
                        isBike |= (netLane.m_Flags & LaneFlags.BicyclesOnly) != 0;
                    }
                }

                validationEdge.IsBike = isBike;
                if (validationEdge.TrackTypes == TrackTypes.None && laneTrackTypes != TrackTypes.None)
                {
                    validationEdge.TrackTypes = laneTrackTypes;
                }

                if (!validationEdge.IsRoad &&
                    !validationEdge.IsBike &&
                    validationEdge.TrackTypes == TrackTypes.None)
                {
                    continue;
                }

                context.Edges[edgeEntity] = validationEdge;
            }

            if (context.Edges.Count == 0)
            {
                reason = $"ownerNodeHasNoSupportedTrafficEdges ownerNode={FormatEntity(ownerNode)} connectedEdges={connectedEdges.Length} stage={stage}";
                return false;
            }

            return true;
        }

        private bool TrySanitizeTrafficSourceForLoad(
            TrafficLoadValidationContext context,
            SourceLaneKey sourceKey,
            out int2 carriagewayAndGroup,
            out float3 lanePosition,
            out string reason)
        {
            carriagewayAndGroup = default;
            lanePosition = default;
            reason = string.Empty;

            if (context == null || !context.Edges.TryGetValue(sourceKey.Edge, out TrafficLoadValidationEdge edge))
            {
                reason = $"sourceEdgeNotConnectedToOwner ownerNode={FormatEntity(context?.OwnerNode ?? Entity.Null)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex}";
                return false;
            }

            if (!edge.CompositionLanes.TryGetValue(sourceKey.LaneIndex, out NetCompositionLane lane))
            {
                reason = $"sourceLaneIndexMissing ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} availableLanes={FormatTrafficLoadValidationLaneIndexes(edge)}";
                return false;
            }

            carriagewayAndGroup = new int2(lane.m_Carriageway, lane.m_Group);
            lanePosition = lane.m_Position;
            return true;
        }

        private bool TrySanitizeTrafficGeneratedSnapshotForLoad(
            TrafficLoadValidationContext context,
            SourceLaneKey sourceKey,
            TrafficGeneratedSnapshot generated,
            out TrafficGeneratedSnapshot sanitized,
            out bool methodChanged,
            out string reason)
        {
            sanitized = generated;
            methodChanged = false;
            reason = string.Empty;

            if (generated.SourceEdge != sourceKey.Edge || generated.SourceLaneIndex != sourceKey.LaneIndex)
            {
                reason = $"generatedSourceMismatch expected={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} actual={FormatEntity(generated.SourceEdge)}:{generated.SourceLaneIndex}";
                return false;
            }

            if (!TrySanitizeTrafficSourceForLoad(
                    context,
                    sourceKey,
                    out int2 sourceCarriagewayAndGroup,
                    out float3 sourceLanePosition,
                    out reason))
            {
                return false;
            }

            if (!context.Edges.TryGetValue(generated.TargetEdge, out TrafficLoadValidationEdge targetEdge))
            {
                reason = $"targetEdgeNotConnectedToOwner ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} target={FormatEntity(generated.TargetEdge)}:{generated.TargetLaneIndex}";
                return false;
            }

            if (!targetEdge.CompositionLanes.TryGetValue(generated.TargetLaneIndex, out NetCompositionLane targetLane))
            {
                reason = $"targetLaneIndexMissing ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} target={FormatEntity(generated.TargetEdge)}:{generated.TargetLaneIndex} availableLanes={FormatTrafficLoadValidationLaneIndexes(targetEdge)}";
                return false;
            }

            PathMethod method = generated.Method;
            if (method == 0)
            {
                reason = $"methodZero ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} target={FormatEntity(generated.TargetEdge)}:{generated.TargetLaneIndex}";
                return false;
            }

            if ((method & PathMethod.Road) != 0 && !targetEdge.IsRoad)
            {
                reason = $"roadMethodTargetNotRoad ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} target={FormatEntity(generated.TargetEdge)}:{generated.TargetLaneIndex} method={method}";
                return false;
            }

            if (method == PathMethod.Bicycle && !targetEdge.IsBike)
            {
                reason = $"bicycleMethodTargetNotBike ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} target={FormatEntity(generated.TargetEdge)}:{generated.TargetLaneIndex} method={method}";
                return false;
            }

            if (!generated.IsUnsafe &&
                (method & PathMethod.Track) != 0 &&
                targetEdge.TrackTypes == TrackTypes.None)
            {
                reason = $"safeTrackMethodTargetHasNoTrack ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} target={FormatEntity(generated.TargetEdge)}:{generated.TargetLaneIndex} method={method}";
                return false;
            }

            if (generated.IsUnsafe &&
                (method & PathMethod.Track) != 0 &&
                targetEdge.TrackTypes == TrackTypes.None)
            {
                method &= ~PathMethod.Track;
                methodChanged = method != generated.Method;
                if ((method & PathMethod.Road) == 0)
                {
                    reason = $"unsafeTrackDowngradeLeftNoRoad ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} target={FormatEntity(generated.TargetEdge)}:{generated.TargetLaneIndex} originalMethod={generated.Method}";
                    return false;
                }
            }

            sanitized.SourceEdge = sourceKey.Edge;
            sanitized.SourceLaneIndex = sourceKey.LaneIndex;
            sanitized.Method = method;
            sanitized.LanePositionMap = new float3x2(sourceLanePosition, targetLane.m_Position);
            sanitized.CarriagewayAndGroupIndexMap = new int4(
                sourceCarriagewayAndGroup,
                new int2(targetLane.m_Carriageway, targetLane.m_Group));
            return true;
        }

        private bool TrySanitizeLaneMappingForTrafficLoad(
            TrafficLoadValidationContext context,
            LaneMapping mapping,
            out LaneMapping sanitized,
            out bool methodChanged,
            out string reason)
        {
            sanitized = mapping;
            TrafficGeneratedSnapshot generated = new TrafficGeneratedSnapshot
            {
                SourceEdge = mapping.SourceEdge,
                TargetEdge = mapping.TargetEdge,
                SourceLaneIndex = mapping.SourceLaneIndex,
                TargetLaneIndex = mapping.TargetLaneIndex,
                LanePositionMap = mapping.TrafficLanePositionMap,
                CarriagewayAndGroupIndexMap = mapping.TrafficCarriagewayAndGroupIndexMap,
                Method = mapping.Method,
                IsUnsafe = mapping.IsUnsafe
            };

            if (!TrySanitizeTrafficGeneratedSnapshotForLoad(
                    context,
                    new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex),
                    generated,
                    out TrafficGeneratedSnapshot sanitizedSnapshot,
                    out methodChanged,
                    out reason))
            {
                return false;
            }

            sanitized.SourceEdge = sanitizedSnapshot.SourceEdge;
            sanitized.TargetEdge = sanitizedSnapshot.TargetEdge;
            sanitized.SourceLaneIndex = sanitizedSnapshot.SourceLaneIndex;
            sanitized.TargetLaneIndex = sanitizedSnapshot.TargetLaneIndex;
            sanitized.TrafficLanePositionMap = sanitizedSnapshot.LanePositionMap;
            sanitized.TrafficCarriagewayAndGroupIndexMap = sanitizedSnapshot.CarriagewayAndGroupIndexMap;
            sanitized.Method = sanitizedSnapshot.Method;
            sanitized.HasTrafficMaps = true;
            return true;
        }

        private bool TrySanitizeTrafficMappingDictionaryForLoad(
            Entity ownerNode,
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            HashSet<SourceLaneKey> roadRepairSourceKeys,
            HashSet<SourceLaneKey> allowedEmptySourceKeys,
            string stage,
            bool failOnRoadRepairInvalid,
            out TrafficLoadValidationStats stats,
            out string detail)
        {
            stats = TrafficLoadValidationStats.Create();
            detail = string.Empty;
            if (bySource == null || bySource.Count == 0)
            {
                detail = "emptyPlan";
                return true;
            }

            if (!TryCreateTrafficLoadValidationContext(ownerNode, stage, out TrafficLoadValidationContext context, out string contextReason))
            {
                detail = contextReason;
                stats.InvalidSources = bySource.Count;
                stats.InvalidRoadRepairConnections = CountNonPreservationMappings(bySource);
                stats.AddSample(contextReason);
                return false;
            }

            List<SourceLaneKey> sourceKeys = new List<SourceLaneKey>(bySource.Keys);
            for (int sourceIndex = 0; sourceIndex < sourceKeys.Count; sourceIndex++)
            {
                SourceLaneKey sourceKey = sourceKeys[sourceIndex];
                Dictionary<TargetLaneKey, LaneMapping> mappings = bySource[sourceKey];
                bool sourceHasRoadRepair = SourceHasRoadRepairMapping(mappings) ||
                                           (roadRepairSourceKeys != null && roadRepairSourceKeys.Contains(sourceKey));
                if (!TrySanitizeTrafficSourceForLoad(
                        context,
                        sourceKey,
                        out _,
                        out _,
                        out string sourceReason))
                {
                    stats.InvalidSources++;
                    stats.RemovedSources++;
                    if (sourceHasRoadRepair)
                    {
                        stats.InvalidRoadRepairConnections += math.max(1, mappings.Count);
                    }
                    else
                    {
                        stats.InvalidPreservationConnections += mappings.Count;
                    }

                    stats.AddSample($"{stage}:source {sourceReason}");
                    bySource.Remove(sourceKey);
                    continue;
                }

                stats.ValidSources++;
                Dictionary<TargetLaneKey, LaneMapping> sanitizedMappings =
                    new Dictionary<TargetLaneKey, LaneMapping>(mappings.Count);
                foreach (LaneMapping mapping in mappings.Values)
                {
                    bool roadRepairMapping = !mapping.IsPreservationOnly;
                    if (!TrySanitizeLaneMappingForTrafficLoad(
                            context,
                            mapping,
                            out LaneMapping sanitizedMapping,
                            out bool methodChanged,
                            out string mappingReason))
                    {
                        stats.InvalidConnections++;
                        if (roadRepairMapping)
                        {
                            stats.InvalidRoadRepairConnections++;
                        }
                        else
                        {
                            stats.InvalidPreservationConnections++;
                        }

                        stats.AddSample($"{stage}:mapping {mappingReason} mapping={FormatMapping(mapping)}");
                        continue;
                    }

                    stats.ValidConnections++;
                    if (methodChanged ||
                        !mapping.HasTrafficMaps ||
                        !mapping.TrafficLanePositionMap.Equals(sanitizedMapping.TrafficLanePositionMap) ||
                        !mapping.TrafficCarriagewayAndGroupIndexMap.Equals(sanitizedMapping.TrafficCarriagewayAndGroupIndexMap))
                    {
                        stats.SanitizedConnections++;
                    }

                    sanitizedMappings[new TargetLaneKey(sanitizedMapping.TargetEdge, sanitizedMapping.TargetLaneIndex)] =
                        sanitizedMapping;
                }

                bool allowEmptySource = allowedEmptySourceKeys != null && allowedEmptySourceKeys.Contains(sourceKey);
                if (sanitizedMappings.Count == 0 && !allowEmptySource)
                {
                    stats.RemovedSources++;
                    if (sourceHasRoadRepair)
                    {
                        stats.InvalidRoadRepairConnections++;
                    }

                    stats.AddSample($"{stage}:sourceRemovedBecauseNoValidConnections ownerNode={FormatEntity(ownerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} roadRepair={sourceHasRoadRepair}");
                    bySource.Remove(sourceKey);
                    continue;
                }

                mappings.Clear();
                foreach (KeyValuePair<TargetLaneKey, LaneMapping> pair in sanitizedMappings)
                {
                    mappings[pair.Key] = pair.Value;
                }

                if (sanitizedMappings.Count == 0 && allowEmptySource)
                {
                    stats.EmptySourcesKept++;
                }
            }

            bool failed = failOnRoadRepairInvalid && stats.InvalidRoadRepairConnections > 0;
            detail = stats.Format(bySource.Keys);
            return !failed;
        }

        private bool TrySanitizeTrafficSourceSnapshotForLoad(
            TrafficLoadValidationContext context,
            TrafficSourceSnapshot source,
            bool allowEmptySource,
            string stage,
            out TrafficSourceSnapshot sanitized,
            ref TrafficLoadValidationStats stats)
        {
            sanitized = source;
            SourceLaneKey sourceKey = new SourceLaneKey(source.SourceEdge, source.SourceLaneIndex);
            if (!TrySanitizeTrafficSourceForLoad(
                    context,
                    sourceKey,
                    out int2 sourceCarriagewayAndGroup,
                    out float3 sourceLanePosition,
                    out string sourceReason))
            {
                stats.InvalidSources++;
                stats.AddSample($"{stage}:source {sourceReason}");
                return false;
            }

            stats.ValidSources++;
            List<TrafficGeneratedSnapshot> validConnections = new List<TrafficGeneratedSnapshot>(
                source.Connections?.Length ?? 0);
            TrafficGeneratedSnapshot[] connections =
                source.Connections ?? System.Array.Empty<TrafficGeneratedSnapshot>();
            for (int i = 0; i < connections.Length; i++)
            {
                if (!TrySanitizeTrafficGeneratedSnapshotForLoad(
                        context,
                        sourceKey,
                        connections[i],
                        out TrafficGeneratedSnapshot sanitizedConnection,
                        out bool methodChanged,
                        out string connectionReason))
                {
                    stats.InvalidConnections++;
                    stats.InvalidPreservationConnections++;
                    stats.AddSample($"{stage}:snapshotConnection {connectionReason}");
                    continue;
                }

                if (methodChanged ||
                    !connections[i].LanePositionMap.Equals(sanitizedConnection.LanePositionMap) ||
                    !connections[i].CarriagewayAndGroupIndexMap.Equals(sanitizedConnection.CarriagewayAndGroupIndexMap))
                {
                    stats.SanitizedConnections++;
                }

                stats.ValidConnections++;
                validConnections.Add(sanitizedConnection);
            }

            if (validConnections.Count == 0 && connections.Length > 0 && !allowEmptySource)
            {
                stats.RemovedSources++;
                stats.AddSample($"{stage}:snapshotSourceRemovedBecauseNoValidConnections ownerNode={FormatEntity(context.OwnerNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex}");
                return false;
            }

            sanitized.SourceCarriagewayAndGroup = sourceCarriagewayAndGroup;
            sanitized.SourceLanePosition = sourceLanePosition;
            sanitized.Connections = validConnections.ToArray();
            if (validConnections.Count == 0)
            {
                stats.EmptySourcesKept++;
            }

            return true;
        }

        private static bool SourceHasRoadRepairMapping(Dictionary<TargetLaneKey, LaneMapping> mappings)
        {
            if (mappings == null)
            {
                return false;
            }

            foreach (LaneMapping mapping in mappings.Values)
            {
                if (!mapping.IsPreservationOnly)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountNonPreservationMappings(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource)
        {
            int count = 0;
            foreach (Dictionary<TargetLaneKey, LaneMapping> mappings in bySource.Values)
            {
                foreach (LaneMapping mapping in mappings.Values)
                {
                    if (!mapping.IsPreservationOnly)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static string FormatTrafficLoadValidationLaneIndexes(TrafficLoadValidationEdge edge)
        {
            if (edge == null || edge.CompositionLanes.Count == 0)
            {
                return "<none>";
            }

            List<int> indexes = new List<int>(edge.CompositionLanes.Keys);
            indexes.Sort();
            return string.Join(",", indexes);
        }
    }
}
