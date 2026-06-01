using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void EnsureTrackSnapshotCaptured(ref Request request, Entity outerEdge, string phase)
        {
            if (request.TrackSnapshotCaptured)
            {
                return;
            }

            PrepareTrackPreservationMappings(ref request, outerEdge);
            request.TrackSnapshotCaptured = true;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] CaptureTrackSnapshot stage complete phase={phase} splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}) trackSkippedReason={request.TrackSkippedReason}.");
        }

        private static void ResetTrackSnapshot(ref Request request)
        {
            request.TrackSnapshotCaptured = false;
            request.TrackForwardSourceLanes = null;
            request.TrackForwardTargetLanes = null;
            request.TrackReverseSourceLanes = null;
            request.TrackReverseTargetLanes = null;
            request.TrackForwardMappings = null;
            request.TrackReverseMappings = null;
            request.TrackSkippedReason = null;
        }

        private void PrepareTrackPreservationMappings(ref Request request, Entity outerEdge)
        {
            m_TrackSourceLanes.Clear();
            m_TrackTargetLanes.Clear();
            m_TrackReverseSourceLanes.Clear();
            m_TrackReverseTargetLanes.Clear();

            CollectEdgeTrackLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_TrackSourceLanes);
            CollectEdgeTrackLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TrackTargetLanes);
            CollectEdgeTrackLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_TrackReverseSourceLanes);
            CollectEdgeTrackLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TrackReverseTargetLanes);

            List<string> skipped = new List<string>(8);
            TrackMappingStats forwardStats = BuildTrackMappingsFromExistingConnectors(
                request.SplitNode,
                outerEdge,
                request.PocketEdge,
                m_TrackSourceLanes,
                m_TrackTargetLanes,
                "trackForward",
                skipped,
                out LaneMapping[] trackForwardMappings);
            TrackMappingStats reverseStats = BuildTrackMappingsFromExistingConnectors(
                request.SplitNode,
                request.PocketEdge,
                outerEdge,
                m_TrackReverseSourceLanes,
                m_TrackReverseTargetLanes,
                "trackReverse",
                skipped,
                out LaneMapping[] trackReverseMappings);

            request.TrackForwardSourceLanes = m_TrackSourceLanes.ToArray();
            request.TrackForwardTargetLanes = m_TrackTargetLanes.ToArray();
            request.TrackReverseSourceLanes = m_TrackReverseSourceLanes.ToArray();
            request.TrackReverseTargetLanes = m_TrackReverseTargetLanes.ToArray();
            request.TrackForwardMappings = trackForwardMappings;
            request.TrackReverseMappings = trackReverseMappings;
            request.TrackSkippedReason = skipped.Count == 0 ? "none" : FormatStringList(skipped);

            LogSplitTrackEndpointAudit(request, outerEdge, forwardStats, reverseStats);
        }

        private TrackMappingStats BuildTrackMappingsFromExistingConnectors(
            Entity splitNode,
            Entity sourceEdge,
            Entity targetEdge,
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            IReadOnlyList<LaneEndpoint> targetEndpoints,
            string direction,
            List<string> skipped,
            out LaneMapping[] mappings)
        {
            mappings = Array.Empty<LaneMapping>();
            TrackMappingStats stats = default;
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                skipped.Add($"{direction}:trackSkippedReason=noSubLaneBuffer");
                return stats;
            }

            CollectTrackConnectorLanes(splitNode, sourceEdge, targetEdge, subLanes, m_TrackConnectorLanes);
            stats.Connectors = m_TrackConnectorLanes.Count;
            if (m_TrackConnectorLanes.Count == 0)
            {
                return stats;
            }

            m_TrackMappings.Clear();
            HashSet<ConnectionKey> used = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                ConnectionKey key = new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex);
                if (used.Contains(key))
                {
                    stats.Skipped++;
                    skipped.Add($"{direction}:trackSkippedReason=duplicateConnector source={connector.SourceLaneIndex} target={connector.TargetLaneIndex} entity={FormatEntity(connector.Entity)}");
                    continue;
                }

                if ((connector.PathMethods & PathMethod.Track) == 0)
                {
                    stats.Skipped++;
                    skipped.Add($"{direction}:trackSkippedReason=connectorMissingTrackMethod source={connector.SourceLaneIndex} target={connector.TargetLaneIndex} methods=[{connector.PathMethods}] entity={FormatEntity(connector.Entity)}");
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceEndpoints, connector.SourceLaneIndex, out LaneEndpoint sourceEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:trackSkippedReason=sourceEndpointMissing edge={FormatEntity(sourceEdge)} lane={connector.SourceLaneIndex} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                if (!TryFindLaneEndpoint(targetEndpoints, connector.TargetLaneIndex, out LaneEndpoint targetEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:trackSkippedReason=targetEndpointMissing edge={FormatEntity(targetEdge)} lane={connector.TargetLaneIndex} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                PathMethod method = GetTrackPreservationMethod(connector, sourceEndpoint, targetEndpoint);
                if ((method & PathMethod.Track) == 0)
                {
                    stats.Skipped++;
                    skipped.Add($"{direction}:trackSkippedReason=methodWithoutTrack source={connector.SourceLaneIndex} target={connector.TargetLaneIndex} computed=[{method}] connectorMethods=[{connector.PathMethods}]");
                    continue;
                }

                used.Add(key);
                if (IsTrackOnlyEndpoint(targetEndpoint))
                {
                    stats.TrackOnlyTargets++;
                }

                if ((method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                {
                    stats.SharedTrackConnections++;
                }

                m_TrackMappings.Add(new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = connector.SourceLaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    Method = method,
                    IsBranch = false,
                    IsTrackPreservation = true,
                    IsUnsafe = (connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods
                });
            }

            stats.Mappings = m_TrackMappings.Count;
            mappings = m_TrackMappings.ToArray();
            return stats;
        }

        private static PathMethod GetTrackPreservationMethod(ConnectorLane connector, LaneEndpoint sourceEndpoint, LaneEndpoint targetEndpoint)
        {
            PathMethod method = connector.PathMethods & (PathMethod.Road | PathMethod.Track);
            if (!SupportsRoadPath(sourceEndpoint) || !SupportsRoadPath(targetEndpoint))
            {
                method &= ~PathMethod.Road;
            }

            return (method & PathMethod.Track) != 0 ? method : 0;
        }

        private void LogSplitTrackEndpointAudit(Request request, Entity outerEdge, TrackMappingStats forwardStats, TrackMappingStats reverseStats)
        {
            string trackForwardSource = FormatEdgeTrackLaneEndpointAudit(
                outerEdge,
                request.SplitNode,
                EndpointRole.SourceEndAtNode);
            string trackForwardTarget = FormatEdgeTrackLaneEndpointAudit(
                request.PocketEdge,
                request.SplitNode,
                EndpointRole.TargetStartAtNode);
            string trackReverseSource = FormatEdgeTrackLaneEndpointAudit(
                request.PocketEdge,
                request.SplitNode,
                EndpointRole.SourceEndAtNode);
            string trackReverseTarget = FormatEdgeTrackLaneEndpointAudit(
                outerEdge,
                request.SplitNode,
                EndpointRole.TargetStartAtNode);
            string splitTrackConnectors = FormatSplitNodeTrackConnectorAudit(
                request.SplitNode,
                outerEdge,
                request.PocketEdge);

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Split track endpoint audit splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackForwardSource=({trackForwardSource}) trackForwardTarget=({trackForwardTarget}) trackReverseSource=({trackReverseSource}) trackReverseTarget=({trackReverseTarget}) trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackSkippedReason={request.TrackSkippedReason} trackForwardStats=connectors:{forwardStats.Connectors},mappings:{forwardStats.Mappings},endpointMisses:{forwardStats.EndpointMisses},skipped:{forwardStats.Skipped},trackOnlyTargets:{forwardStats.TrackOnlyTargets},sharedTrackConnections:{forwardStats.SharedTrackConnections} trackReverseStats=connectors:{reverseStats.Connectors},mappings:{reverseStats.Mappings},endpointMisses:{reverseStats.EndpointMisses},skipped:{reverseStats.Skipped},trackOnlyTargets:{reverseStats.TrackOnlyTargets},sharedTrackConnections:{reverseStats.SharedTrackConnections} splitTrackConnectors=({splitTrackConnectors}).");
        }

        private void LogReverseTrackEndpointAudit(Request request, Entity outerEdge, string reason)
        {
            string reverseSourceTrack = FormatEdgeTrackLaneEndpointAudit(
                request.PocketEdge,
                request.SplitNode,
                EndpointRole.SourceEndAtNode);
            string reverseTargetTrack = FormatEdgeTrackLaneEndpointAudit(
                outerEdge,
                request.SplitNode,
                EndpointRole.TargetStartAtNode);
            string splitTrackConnectors = FormatSplitNodeTrackConnectorAudit(
                request.SplitNode,
                outerEdge,
                request.PocketEdge);

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Reverse track endpoint audit splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reason} reverseSourceTrack=({reverseSourceTrack}) reverseTargetTrack=({reverseTargetTrack}) splitTrackConnectors=({splitTrackConnectors}).");
        }
    }
}
