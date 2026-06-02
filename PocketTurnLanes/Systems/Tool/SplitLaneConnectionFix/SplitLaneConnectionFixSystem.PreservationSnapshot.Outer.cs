using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void EnsurePreservationSnapshotCapturedForOuter(ref Request request, Entity outerEdge, string phase)
        {
            if (request.PreservationSnapshotCapturedForOuter)
            {
                return;
            }

            PreparePreservationSnapshotForOuter(ref request, outerEdge);
            request.PreservationSnapshotCapturedForOuter = true;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] CapturePreservationSnapshotForOuter stage complete phase={phase} splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] preservationForwardSource=({FormatLaneOrder(request.PreservationForwardSourceLanes)}) preservationForwardTarget=({FormatLaneOrder(request.PreservationForwardTargetLanes)}) preservationReverseSource=({FormatLaneOrder(request.PreservationReverseSourceLanes)}) preservationReverseTarget=({FormatLaneOrder(request.PreservationReverseTargetLanes)}) preservationSkippedReason={request.PreservationSkippedReason}.");
        }

        private static void ResetPreservationSnapshotForOuter(ref Request request)
        {
            request.PreservationSnapshotCapturedForOuter = false;
            request.PreservationForwardSourceLanes = null;
            request.PreservationForwardTargetLanes = null;
            request.PreservationReverseSourceLanes = null;
            request.PreservationReverseTargetLanes = null;
            request.PreservationForwardMappings = null;
            request.PreservationReverseMappings = null;
            request.PreservationSkippedReason = null;
        }

        private void PreparePreservationSnapshotForOuter(ref Request request, Entity outerEdge)
        {
            m_PreservationSourceLanes.Clear();
            m_PreservationTargetLanes.Clear();
            m_PreservationReverseSourceLanes.Clear();
            m_PreservationReverseTargetLanes.Clear();

            CollectEdgePreservationLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_PreservationSourceLanes);
            CollectEdgePreservationLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_PreservationTargetLanes);
            CollectEdgePreservationLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_PreservationReverseSourceLanes);
            CollectEdgePreservationLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_PreservationReverseTargetLanes);

            List<string> preservationSkipped = new List<string>(8);
            PreservationMappingStats preservationForwardStats = BuildPreservationMappingsFromExistingConnectors(
                request.SplitNode,
                outerEdge,
                request.PocketEdge,
                m_PreservationSourceLanes,
                m_PreservationTargetLanes,
                m_PreservationReverseTargetLanes,
                "preservationForward",
                preservationSkipped,
                out LaneMapping[] preservationForwardMappings);
            PreservationMappingStats preservationReverseStats = BuildPreservationMappingsFromExistingConnectors(
                request.SplitNode,
                request.PocketEdge,
                outerEdge,
                m_PreservationReverseSourceLanes,
                m_PreservationReverseTargetLanes,
                m_PreservationTargetLanes,
                "preservationReverse",
                preservationSkipped,
                out LaneMapping[] preservationReverseMappings);

            request.PreservationForwardSourceLanes = m_PreservationSourceLanes.ToArray();
            request.PreservationForwardTargetLanes = m_PreservationTargetLanes.ToArray();
            request.PreservationReverseSourceLanes = m_PreservationReverseSourceLanes.ToArray();
            request.PreservationReverseTargetLanes = m_PreservationReverseTargetLanes.ToArray();
            request.PreservationForwardMappings = preservationForwardMappings;
            request.PreservationReverseMappings = preservationReverseMappings;
            request.PreservationSkippedReason = preservationSkipped.Count == 0 ? "none" : FormatStringList(preservationSkipped);

            LogPreservationSnapshotAuditForOuter(request, outerEdge, preservationForwardStats, preservationReverseStats);
        }

        private PreservationMappingStats BuildPreservationMappingsFromExistingConnectors(
            Entity splitNode,
            Entity sourceEdge,
            Entity targetEdge,
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            IReadOnlyList<LaneEndpoint> targetEndpoints,
            IReadOnlyList<LaneEndpoint> sameEdgeTargetEndpoints,
            string direction,
            List<string> skipped,
            out LaneMapping[] mappings)
        {
            mappings = Array.Empty<LaneMapping>();
            PreservationMappingStats stats = default;
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                skipped.Add($"{direction}:preservationSkippedReason=noSubLaneBuffer");
                return stats;
            }

            CollectSplitPairPreservationConnectorLanes(splitNode, sourceEdge, targetEdge, subLanes, m_ConnectorLanes);
            stats.Connectors = m_ConnectorLanes.Count;
            if (m_ConnectorLanes.Count == 0)
            {
                return stats;
            }

            m_PreservationMappings.Clear();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                bool sameEdgeUturn = connector.SourceEdge == connector.TargetEdge;

                if (!TrafficLaneEndpointHelpers.TryFind(sourceEndpoints, connector.SourceLaneIndex, out LaneEndpoint sourceEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:preservationSkippedReason=sourceEndpointMissing edge={FormatEntity(sourceEdge)} lane={connector.SourceLaneIndex} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                IReadOnlyList<LaneEndpoint> candidateTargetEndpoints = sameEdgeUturn
                    ? sameEdgeTargetEndpoints
                    : targetEndpoints;
                if (!TrafficLaneEndpointHelpers.TryFind(candidateTargetEndpoints, connector.TargetLaneIndex, out LaneEndpoint targetEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:preservationSkippedReason=targetEndpointMissing edge={FormatEntity(connector.TargetEdge)} lane={connector.TargetLaneIndex} sameEdgeUturn={sameEdgeUturn} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                PathMethod method = TrafficPathMethods.RestrictPreservedTrafficPathMethodToEndpoints(
                    TrafficPathMethods.GetLayerPreservationPathMethod(connector.PathMethods, preserveUturn: sameEdgeUturn),
                    sourceEndpoint,
                    targetEndpoint);
                if (method == 0)
                {
                    stats.Skipped++;
                    continue;
                }

                bool unsafeConnection = (connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0;
                m_PreservationMappings.Add(new LaneMapping
                {
                    SourceEdge = connector.SourceEdge,
                    TargetEdge = connector.TargetEdge,
                    SourceLaneIndex = connector.SourceLaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    Method = method,
                    IsBranch = false,
                    IsPreservationOnly = true,
                    IsUnsafe = unsafeConnection,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods,
                    HasPreservedPathMethods = true
                });
                stats.Mappings++;
                if (sameEdgeUturn)
                {
                    stats.UturnConnections++;
                }

                if ((method & ~PathMethod.Road) != 0)
                {
                    stats.NonRoadConnections++;
                }

                if (unsafeConnection)
                {
                    stats.UnsafeConnections++;
                }

                if ((method & PathMethod.Track) != 0)
                {
                    stats.TrackConnections++;
                    if (TrafficPathMethods.IsTrackOnlyEndpoint(targetEndpoint))
                    {
                        stats.TrackOnlyTargets++;
                    }

                    if ((method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                    {
                        stats.SharedTrackConnections++;
                    }
                }
            }

            mappings = m_PreservationMappings.ToArray();
            return stats;
        }

        private void LogPreservationSnapshotAuditForOuter(
            Request request,
            Entity outerEdge,
            PreservationMappingStats preservationForwardStats,
            PreservationMappingStats preservationReverseStats)
        {
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Outer preservation snapshot audit splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} preservationPolicy=captureAllSplitPairSources finalUturnPolicy=outerAuditSuppress preservationForwardSource=({FormatLaneOrder(request.PreservationForwardSourceLanes)}) preservationForwardTarget=({FormatLaneOrder(request.PreservationForwardTargetLanes)}) preservationReverseSource=({FormatLaneOrder(request.PreservationReverseSourceLanes)}) preservationReverseTarget=({FormatLaneOrder(request.PreservationReverseTargetLanes)}) preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] preservationSkippedReason={request.PreservationSkippedReason} preservationForwardStats=connectors:{preservationForwardStats.Connectors},mappings:{preservationForwardStats.Mappings},endpointMisses:{preservationForwardStats.EndpointMisses},skipped:{preservationForwardStats.Skipped},uturnCaptured:{preservationForwardStats.UturnConnections},nonRoad:{preservationForwardStats.NonRoadConnections},unsafe:{preservationForwardStats.UnsafeConnections},preservationTrackConnections:{preservationForwardStats.TrackConnections},preservationTrackOnlyTargets:{preservationForwardStats.TrackOnlyTargets},preservationSharedTrackConnections:{preservationForwardStats.SharedTrackConnections} preservationReverseStats=connectors:{preservationReverseStats.Connectors},mappings:{preservationReverseStats.Mappings},endpointMisses:{preservationReverseStats.EndpointMisses},skipped:{preservationReverseStats.Skipped},uturnCaptured:{preservationReverseStats.UturnConnections},nonRoad:{preservationReverseStats.NonRoadConnections},unsafe:{preservationReverseStats.UnsafeConnections},preservationTrackConnections:{preservationReverseStats.TrackConnections},preservationTrackOnlyTargets:{preservationReverseStats.TrackOnlyTargets},preservationSharedTrackConnections:{preservationReverseStats.SharedTrackConnections}.");
        }
    }
}
