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
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] CaptureTrackSnapshot stage complete phase={phase} splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}) preservationForwardSource=({FormatLaneOrder(request.PreservationForwardSourceLanes)}) preservationForwardTarget=({FormatLaneOrder(request.PreservationForwardTargetLanes)}) preservationReverseSource=({FormatLaneOrder(request.PreservationReverseSourceLanes)}) preservationReverseTarget=({FormatLaneOrder(request.PreservationReverseTargetLanes)}) trackSkippedReason={request.TrackSkippedReason} preservationSkippedReason={request.PreservationSkippedReason}.");
        }

        private static void ResetTrackSnapshot(ref Request request)
        {
            request.TrackSnapshotCaptured = false;
            request.TrackForwardSourceLanes = null;
            request.TrackForwardTargetLanes = null;
            request.TrackReverseSourceLanes = null;
            request.TrackReverseTargetLanes = null;
            request.PreservationForwardSourceLanes = null;
            request.PreservationForwardTargetLanes = null;
            request.PreservationReverseSourceLanes = null;
            request.PreservationReverseTargetLanes = null;
            request.TrackForwardMappings = null;
            request.TrackReverseMappings = null;
            request.PreservationForwardMappings = null;
            request.PreservationReverseMappings = null;
            request.TrackSkippedReason = null;
            request.PreservationSkippedReason = null;
        }

        private void PrepareTrackPreservationMappings(ref Request request, Entity outerEdge)
        {
            m_TrackSourceLanes.Clear();
            m_TrackTargetLanes.Clear();
            m_TrackReverseSourceLanes.Clear();
            m_TrackReverseTargetLanes.Clear();
            m_PreservationSourceLanes.Clear();
            m_PreservationTargetLanes.Clear();
            m_PreservationReverseSourceLanes.Clear();
            m_PreservationReverseTargetLanes.Clear();

            CollectEdgeTrackLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_TrackSourceLanes);
            CollectEdgeTrackLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TrackTargetLanes);
            CollectEdgeTrackLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_TrackReverseSourceLanes);
            CollectEdgeTrackLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TrackReverseTargetLanes);
            CollectEdgePreservationLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_PreservationSourceLanes);
            CollectEdgePreservationLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_PreservationTargetLanes);
            CollectEdgePreservationLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_PreservationReverseSourceLanes);
            CollectEdgePreservationLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_PreservationReverseTargetLanes);

            List<string> skipped = new List<string>(8);
            List<string> preservationSkipped = new List<string>(8);
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
            PreservationMappingStats preservationForwardStats = BuildPreservationMappingsFromExistingConnectors(
                request.SplitNode,
                outerEdge,
                request.PocketEdge,
                m_PreservationSourceLanes,
                m_PreservationTargetLanes,
                "preservationForward",
                preservationSkipped,
                out LaneMapping[] preservationForwardMappings);
            PreservationMappingStats preservationReverseStats = BuildPreservationMappingsFromExistingConnectors(
                request.SplitNode,
                request.PocketEdge,
                outerEdge,
                m_PreservationReverseSourceLanes,
                m_PreservationReverseTargetLanes,
                "preservationReverse",
                preservationSkipped,
                out LaneMapping[] preservationReverseMappings);

            request.TrackForwardSourceLanes = m_TrackSourceLanes.ToArray();
            request.TrackForwardTargetLanes = m_TrackTargetLanes.ToArray();
            request.TrackReverseSourceLanes = m_TrackReverseSourceLanes.ToArray();
            request.TrackReverseTargetLanes = m_TrackReverseTargetLanes.ToArray();
            request.PreservationForwardSourceLanes = m_PreservationSourceLanes.ToArray();
            request.PreservationForwardTargetLanes = m_PreservationTargetLanes.ToArray();
            request.PreservationReverseSourceLanes = m_PreservationReverseSourceLanes.ToArray();
            request.PreservationReverseTargetLanes = m_PreservationReverseTargetLanes.ToArray();
            request.TrackForwardMappings = trackForwardMappings;
            request.TrackReverseMappings = trackReverseMappings;
            request.PreservationForwardMappings = preservationForwardMappings;
            request.PreservationReverseMappings = preservationReverseMappings;
            request.TrackSkippedReason = skipped.Count == 0 ? "none" : FormatStringList(skipped);
            request.PreservationSkippedReason = preservationSkipped.Count == 0 ? "none" : FormatStringList(preservationSkipped);

            LogSplitTrackEndpointAudit(request, outerEdge, forwardStats, reverseStats, preservationForwardStats, preservationReverseStats);
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
                    TemplatePathMethods = connector.PathMethods,
                    HasPreservedPathMethods = true
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

        private PreservationMappingStats BuildPreservationMappingsFromExistingConnectors(
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
                if (connector.SourceEdge == connector.TargetEdge)
                {
                    stats.UturnSuppressed++;
                    skipped.Add($"{direction}:preservationSkippedReason=uturnSuppressed source={connector.SourceLaneIndex} target={connector.TargetLaneIndex} methods=[{connector.PathMethods}] entity={FormatEntity(connector.Entity)}");
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceEndpoints, connector.SourceLaneIndex, out LaneEndpoint sourceEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:preservationSkippedReason=sourceEndpointMissing edge={FormatEntity(sourceEdge)} lane={connector.SourceLaneIndex} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                if (!TryFindLaneEndpoint(targetEndpoints, connector.TargetLaneIndex, out LaneEndpoint targetEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:preservationSkippedReason=targetEndpointMissing edge={FormatEntity(targetEdge)} lane={connector.TargetLaneIndex} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                PathMethod method = RestrictPreservedTrafficPathMethodToEndpoints(
                    GetLayerPreservationPathMethod(connector.PathMethods, preserveUturn: false),
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
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = connector.SourceLaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    Method = method,
                    IsBranch = false,
                    IsTrackPreservation = true,
                    IsUnsafe = unsafeConnection,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods,
                    HasPreservedPathMethods = true
                });
                stats.Mappings++;
                if ((method & ~PathMethod.Road) != 0)
                {
                    stats.NonRoadConnections++;
                }

                if (unsafeConnection)
                {
                    stats.UnsafeConnections++;
                }
            }

            mappings = m_PreservationMappings.ToArray();
            return stats;
        }

        private void LogSplitTrackEndpointAudit(
            Request request,
            Entity outerEdge,
            TrackMappingStats forwardStats,
            TrackMappingStats reverseStats,
            PreservationMappingStats preservationForwardStats,
            PreservationMappingStats preservationReverseStats)
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

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Split track endpoint audit splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackForwardSource=({trackForwardSource}) trackForwardTarget=({trackForwardTarget}) trackReverseSource=({trackReverseSource}) trackReverseTarget=({trackReverseTarget}) preservationForwardSource=({FormatLaneOrder(request.PreservationForwardSourceLanes)}) preservationForwardTarget=({FormatLaneOrder(request.PreservationForwardTargetLanes)}) preservationReverseSource=({FormatLaneOrder(request.PreservationReverseSourceLanes)}) preservationReverseTarget=({FormatLaneOrder(request.PreservationReverseTargetLanes)}) trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] trackSkippedReason={request.TrackSkippedReason} preservationSkippedReason={request.PreservationSkippedReason} trackForwardStats=connectors:{forwardStats.Connectors},mappings:{forwardStats.Mappings},endpointMisses:{forwardStats.EndpointMisses},skipped:{forwardStats.Skipped},trackOnlyTargets:{forwardStats.TrackOnlyTargets},sharedTrackConnections:{forwardStats.SharedTrackConnections} trackReverseStats=connectors:{reverseStats.Connectors},mappings:{reverseStats.Mappings},endpointMisses:{reverseStats.EndpointMisses},skipped:{reverseStats.Skipped},trackOnlyTargets:{reverseStats.TrackOnlyTargets},sharedTrackConnections:{reverseStats.SharedTrackConnections} preservationForwardStats=connectors:{preservationForwardStats.Connectors},mappings:{preservationForwardStats.Mappings},endpointMisses:{preservationForwardStats.EndpointMisses},skipped:{preservationForwardStats.Skipped},uturnSuppressed:{preservationForwardStats.UturnSuppressed},nonRoad:{preservationForwardStats.NonRoadConnections},unsafe:{preservationForwardStats.UnsafeConnections} preservationReverseStats=connectors:{preservationReverseStats.Connectors},mappings:{preservationReverseStats.Mappings},endpointMisses:{preservationReverseStats.EndpointMisses},skipped:{preservationReverseStats.Skipped},uturnSuppressed:{preservationReverseStats.UturnSuppressed},nonRoad:{preservationReverseStats.NonRoadConnections},unsafe:{preservationReverseStats.UnsafeConnections} splitTrackConnectors=({splitTrackConnectors}).");
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
