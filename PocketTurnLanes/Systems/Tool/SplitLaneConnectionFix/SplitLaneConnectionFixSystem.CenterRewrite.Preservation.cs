using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private CenterPreservationStats AddCenterRuntimePreservationMappings(
            Entity centerNode,
            CenterRewritePlan plan,
            IReadOnlyList<ConnectorLane> allApproachConnectors,
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            Dictionary<Entity, List<LaneEndpoint>> roadTargetEndpointCache)
        {
            CenterPreservationStats stats = default;
            if (allApproachConnectors == null || allApproachConnectors.Count == 0)
            {
                return stats;
            }

            Dictionary<Entity, List<LaneEndpoint>> preservationTargetEndpointCache = new Dictionary<Entity, List<LaneEndpoint>>();
            for (int i = 0; i < allApproachConnectors.Count; i++)
            {
                ConnectorLane connector = allApproachConnectors[i];
                SourceLaneKey sourceKey = new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex);
                if (!bySource.ContainsKey(sourceKey))
                {
                    continue;
                }

                CenterRewriteMovement movement = ClassifyCenterRewriteMovement(
                    centerNode,
                    connector.SourceEdge,
                    connector.TargetEdge,
                    connector.CarFlags,
                    plan.BigTurn,
                    plan.SmallTurn);
                PathMethod preservedMethod = GetCenterPreservedConnectorMethod(connector.PathMethods, movement);
                if (preservedMethod == 0)
                {
                    continue;
                }

                if (!sourceEndpoints.TryGetValue(sourceKey, out LaneEndpoint sourceEndpoint))
                {
                    stats.Skipped++;
                    continue;
                }

                LaneEndpoint targetEndpoint;
                if (!TryFindCenterPreservationTargetEndpoint(
                        centerNode,
                        connector.TargetEdge,
                        connector.TargetLaneIndex,
                        roadTargetEndpointCache,
                        preservationTargetEndpointCache,
                        out targetEndpoint))
                {
                    stats.Skipped++;
                    continue;
                }

                bool unsafeConnection = (connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0;
                LaneMapping mapping = new LaneMapping
                {
                    SourceEdge = sourceEndpoint.Edge,
                    TargetEdge = connector.TargetEdge,
                    SourceLaneIndex = sourceEndpoint.LaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    TrafficLanePositionMap = new float3x2(sourceEndpoint.LanePosition, targetEndpoint.LanePosition),
                    TrafficCarriagewayAndGroupIndexMap = new int4(sourceEndpoint.CarriagewayAndGroup, targetEndpoint.CarriagewayAndGroup),
                    Method = preservedMethod,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods,
                    IsTrackPreservation = true,
                    IsUnsafe = unsafeConnection,
                    HasTrafficMaps = true,
                    HasPreservedPathMethods = true
                };
                AddOrMergeCenterTrafficMapping(bySource, mapping);
                targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = targetEndpoint;
                stats.Connections++;

                if (movement == CenterRewriteMovement.Uturn || connector.SourceEdge == connector.TargetEdge)
                {
                    stats.UturnConnections++;
                }

                if ((preservedMethod & ~PathMethod.Road) != 0)
                {
                    stats.NonRoadConnections++;
                }

                if (unsafeConnection)
                {
                    stats.UnsafeConnections++;
                }
            }

            return stats;
        }

        private static PathMethod GetCenterPreservedConnectorMethod(PathMethod method, CenterRewriteMovement movement)
        {
            return GetLayerPreservationPathMethod(method, movement == CenterRewriteMovement.Uturn);
        }

        private CenterPreservationStats CopyExistingCenterPreservedGeneratedConnections(
            TrafficApi trafficApi,
            CenterRewritePlan plan,
            object modifiedBuffer)
        {
            CenterPreservationStats stats = default;
            if (trafficApi == null ||
                plan == null ||
                modifiedBuffer == null ||
                plan.BySource.Count == 0)
            {
                return stats;
            }

            List<TrafficSourceSnapshot> sourceSnapshots = new List<TrafficSourceSnapshot>(plan.BySource.Count);
            TrafficSnapshotReadStats readStats = default;
            ReadTrafficSourceSnapshotsFromBuffer(
                trafficApi,
                modifiedBuffer,
                source => plan.BySource.ContainsKey(new SourceLaneKey(source.SourceEdge, source.SourceLaneIndex)),
                null,
                sourceSnapshots,
                ref readStats);
            stats.Skipped += readStats.MissingGeneratedBuffers;

            for (int i = 0; i < sourceSnapshots.Count; i++)
            {
                TrafficGeneratedSnapshot[] connections =
                    sourceSnapshots[i].Connections ?? System.Array.Empty<TrafficGeneratedSnapshot>();
                for (int generatedIndex = 0; generatedIndex < connections.Length; generatedIndex++)
                {
                    TrafficGeneratedSnapshot generated = connections[generatedIndex];
                    SourceLaneKey sourceKey = new SourceLaneKey(generated.SourceEdge, generated.SourceLaneIndex);
                    if (!plan.BySource.ContainsKey(sourceKey))
                    {
                        continue;
                    }

                    PathMethod originalMethod = generated.Method;
                    bool isUturn = generated.SourceEdge == generated.TargetEdge;
                    PathMethod preservedMethod = GetLayerPreservationPathMethod(originalMethod, isUturn);
                    if (preservedMethod == 0)
                    {
                        continue;
                    }

                    LaneMapping mapping = CreateLaneMappingFromTrafficSnapshot(generated, preservedMethod);
                    AddOrMergeCenterTrafficMapping(plan.BySource, mapping);
                    stats.Connections++;
                    if (isUturn)
                    {
                        stats.UturnConnections++;
                    }

                    if ((preservedMethod & ~PathMethod.Road) != 0)
                    {
                        stats.NonRoadConnections++;
                    }

                    if (mapping.IsUnsafe)
                    {
                        stats.UnsafeConnections++;
                    }
                }
            }

            return stats;
        }

    }
}
