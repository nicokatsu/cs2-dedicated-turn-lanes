using System.Collections.Generic;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        public TransitionConnectionSnapshot CaptureTransitionReverseConnections(
            Entity transitionNode,
            Entity sourceEdge,
            Entity targetEdge)
        {
            m_ReverseSourceLanes.Clear();
            m_ReverseTargetLanes.Clear();
            CollectEdgeCarLaneEndpoints(sourceEdge, transitionNode, EndpointRole.SourceEndAtNode, m_ReverseSourceLanes);
            CollectEdgeCarLaneEndpoints(targetEdge, transitionNode, EndpointRole.TargetStartAtNode, m_ReverseTargetLanes);
            NormalizeTransitionLaneLaterals(m_ReverseSourceLanes, m_ReverseTargetLanes);

            List<TransitionConnectionSnapshotMapping> mappings = new List<TransitionConnectionSnapshotMapping>(8);
            string source = "none";
            string trafficDetail = "not-run";
            if (TryGetTrafficApi(out TrafficApi trafficApi, out string trafficError))
            {
                if (TryCaptureTrafficReverseMappings(
                        trafficApi,
                        transitionNode,
                        sourceEdge,
                        targetEdge,
                        m_ReverseSourceLanes,
                        m_ReverseTargetLanes,
                        mappings,
                        out trafficDetail))
                {
                    source = "traffic";
                }
            }
            else
            {
                trafficDetail = trafficError;
            }

            string liveDetail = "not-run";
            if (mappings.Count == 0)
            {
                CollectConnectorLanes(transitionNode, sourceEdge, targetEdge, m_ExistingConnectorLanes);
                for (int i = 0; i < m_ExistingConnectorLanes.Count; i++)
                {
                    ConnectorLane connector = m_ExistingConnectorLanes[i];
                    if (!TryBuildSnapshotMapping(
                            connector.SourceLaneIndex,
                            connector.TargetLaneIndex,
                            connector.PathMethods,
                            false,
                            m_ReverseSourceLanes,
                            m_ReverseTargetLanes,
                            out TransitionConnectionSnapshotMapping mapping))
                    {
                        continue;
                    }

                    mappings.Add(mapping);
                }

                source = mappings.Count > 0 ? "live-connectors" : "empty";
                liveDetail = $"connectors={m_ExistingConnectorLanes.Count}";
            }

            TransitionConnectionSnapshot snapshot = new TransitionConnectionSnapshot
            {
                Node = transitionNode,
                SourceEdge = sourceEdge,
                TargetEdge = targetEdge,
                Source = source,
                Mappings = mappings.ToArray()
            };
            snapshot.Detail = $"snapshotSource={source} mappings={snapshot.Mappings.Length} sourceLanes={m_ReverseSourceLanes.Count} targetLanes={m_ReverseTargetLanes.Count} trafficDetail={trafficDetail} liveDetail={liveDetail}";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Captured transition reverse connection snapshot node={FormatEntity(transitionNode)} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} {snapshot.Detail} mappings={FormatSnapshotMappings(snapshot.Mappings)} sourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} targetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
            return snapshot;
        }

        private bool TryCaptureTrafficReverseMappings(
            TrafficApi trafficApi,
            Entity transitionNode,
            Entity sourceEdge,
            Entity targetEdge,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            List<TransitionConnectionSnapshotMapping> mappings,
            out string detail)
        {
            detail = "none";
            List<TrafficSourceSnapshot> sourceSnapshots = new List<TrafficSourceSnapshot>(4);
            if (!TryReadTrafficSourceSnapshots(
                    trafficApi,
                    transitionNode,
                    source => source.SourceEdge == sourceEdge,
                    (source, generated) => generated.SourceEdge == sourceEdge && generated.TargetEdge == targetEdge,
                    sourceSnapshots,
                    out TrafficSnapshotReadStats readStats,
                    out string readDetail))
            {
                detail = readDetail;
                return false;
            }

            int accepted = 0;
            int generatedEntries = 0;
            for (int i = 0; i < sourceSnapshots.Count; i++)
            {
                TrafficGeneratedSnapshot[] connections =
                    sourceSnapshots[i].Connections ?? System.Array.Empty<TrafficGeneratedSnapshot>();
                generatedEntries += connections.Length;
                for (int generatedIndex = 0; generatedIndex < connections.Length; generatedIndex++)
                {
                    TrafficGeneratedSnapshot generated = connections[generatedIndex];
                    if (!TryBuildSnapshotMapping(
                            generated.SourceLaneIndex,
                            generated.TargetLaneIndex,
                            generated.Method,
                            generated.IsUnsafe,
                            sourceLanes,
                            targetLanes,
                            out TransitionConnectionSnapshotMapping mapping))
                    {
                        continue;
                    }

                    mappings.Add(mapping);
                    accepted++;
                }
            }

            detail = $"trafficSources={readStats.AcceptedSources} generatedMatches={generatedEntries} accepted={accepted}";
            return accepted > 0;
        }

        private static bool TryBuildSnapshotMapping(
            int sourceLaneIndex,
            int targetLaneIndex,
            PathMethod method,
            bool isUnsafe,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            out TransitionConnectionSnapshotMapping mapping)
        {
            mapping = default;
            if (!TryFindLaneEndpoint(sourceLanes, sourceLaneIndex, out LaneEndpoint source) ||
                !TryFindLaneEndpoint(targetLanes, targetLaneIndex, out LaneEndpoint target))
            {
                return false;
            }

            mapping = new TransitionConnectionSnapshotMapping
            {
                SourceLaneIndex = sourceLaneIndex,
                TargetLaneIndex = targetLaneIndex,
                SourceLateral = source.Lateral,
                TargetLateral = target.Lateral,
                SourceLanePosition = source.LanePosition,
                TargetLanePosition = target.LanePosition,
                SourceCarriagewayAndGroup = source.CarriagewayAndGroup,
                TargetCarriagewayAndGroup = target.CarriagewayAndGroup,
                Method = method,
                IsUnsafe = isUnsafe
            };
            return true;
        }
    }
}
