using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using NetCarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private struct CenterTurnEvidenceStats
        {
            public int SourceEntries;
            public int Connections;
            public int MappedSelectedTargets;
            public int ClassifiedConnections;
            public int SkippedConnections;
            public int SkippedSelfReferences;
        }

        private bool TryRefineExtraTargetFromCenterConnectors(
            Entity intersectionNode,
            Entity centerSourceEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string diagnostics)
        {
            extraTargetIndex = -1;
            turn = TurnDirection.Ambiguous;
            diagnostics = string.Empty;

            if (intersectionNode == Entity.Null ||
                centerSourceEdge == Entity.Null ||
                !EntityManager.Exists(intersectionNode) ||
                !EntityManager.TryGetBuffer(intersectionNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                diagnostics = $"centerRuntimeSelection=skipped reason=center-node-missing-sublanes intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)}";
                return false;
            }

            if (selectedTargets == null || selectedTargets.Count == 0)
            {
                diagnostics = "centerRuntimeSelection=skipped reason=no-selected-targets";
                return false;
            }

            int[] leftCounts = new int[selectedTargets.Count];
            int[] rightCounts = new int[selectedTargets.Count];
            int[] straightCounts = new int[selectedTargets.Count];
            m_CenterTurnCandidates.Clear();

            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, intersectionNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    sourceEdge != centerSourceEdge ||
                    targetEdge == centerSourceEdge)
                {
                    continue;
                }

                int sourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff;
                if (!TrafficCenterTurnTargetSelector.TryFindTargetByCenterLaneIndex(selectedTargets, sourceLaneIndex, out int targetListIndex))
                {
                    continue;
                }

                NetCarLane carLane = EntityManager.GetComponentData<NetCarLane>(laneEntity);
                TurnDirection connectorTurn = TrafficConnectorMovementClassifier.ClassifyCenterConnectorTurn(
                    EntityManager,
                    intersectionNode,
                    centerSourceEdge,
                    targetEdge,
                    carLane.m_Flags);
                TrafficCenterTurnTargetSelector.AddTurnCount(
                    connectorTurn,
                    targetListIndex,
                    leftCounts,
                    rightCounts,
                    straightCounts);

                m_CenterTurnCandidates.Add(new CenterTurnCandidate
                {
                    LaneEntity = laneEntity,
                    SourceLaneIndex = sourceLaneIndex,
                    TargetListIndex = targetListIndex,
                    TargetLaneIndex = selectedTargets[targetListIndex].LaneIndex,
                    TargetEdge = targetEdge,
                    Turn = connectorTurn,
                    Flags = carLane.m_Flags
                });
            }

            diagnostics = FormatCenterTurnDiagnostics(selectedTargets, leftCounts, rightCounts, straightCounts, m_CenterTurnCandidates);
            if (!TrafficCenterTurnTargetSelector.TrySelectExtraTarget(
                    selectedTargets,
                    leftCounts,
                    rightCounts,
                    straightCounts,
                    out extraTargetIndex,
                    out turn,
                    out string selectionDiagnostic))
            {
                diagnostics = $"centerRuntimeSelection=failed counts=({diagnostics}) {selectionDiagnostic}";
                return false;
            }

            diagnostics = $"centerRuntimeSelection=selected selectedExtra={selectedTargets[extraTargetIndex].LaneIndex}/{turn} counts=({diagnostics}) {selectionDiagnostic}";
            return true;
        }

        private bool TryRefineExtraTargetFromCenterTrafficOverride(
            TrafficApi trafficApi,
            Entity intersectionNode,
            Entity centerSourceEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string diagnostics)
        {
            extraTargetIndex = -1;
            turn = TurnDirection.Ambiguous;
            diagnostics = string.Empty;

            if (trafficApi == null ||
                intersectionNode == Entity.Null ||
                centerSourceEdge == Entity.Null ||
                !EntityManager.Exists(intersectionNode))
            {
                diagnostics = $"centerTrafficSelection=skipped reason=invalid-api-or-node intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)}";
                return false;
            }

            if (selectedTargets == null || selectedTargets.Count == 0)
            {
                diagnostics = "centerTrafficSelection=skipped reason=no-selected-targets";
                return false;
            }

            List<TrafficSourceSnapshot> snapshots = new List<TrafficSourceSnapshot>(8);
            if (!TryReadTrafficSourceSnapshots(
                    trafficApi,
                    intersectionNode,
                    source => source.SourceEdge == centerSourceEdge,
                    (source, generated) =>
                        generated.SourceEdge == centerSourceEdge &&
                        (generated.Method & PathMethod.Road) != 0,
                    snapshots,
                    out TrafficSnapshotReadStats readStats,
                    out string readDetail))
            {
                diagnostics = $"centerTrafficSelection=skipped reason={readDetail} intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} readStats=({TrafficSnapshotHelpers.FormatReadStats(readStats)})";
                return false;
            }

            if (snapshots.Count == 0)
            {
                diagnostics = $"centerTrafficSelection=skipped reason=no-matching-source intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} readStats=({TrafficSnapshotHelpers.FormatReadStats(readStats)})";
                return false;
            }

            int[] leftCounts = new int[selectedTargets.Count];
            int[] rightCounts = new int[selectedTargets.Count];
            int[] straightCounts = new int[selectedTargets.Count];
            CenterTurnEvidenceStats stats = default;
            List<string> evidenceSamples = new List<string>(8);
            List<string> skipSamples = new List<string>(8);

            for (int entryIndex = 0; entryIndex < snapshots.Count; entryIndex++)
            {
                TrafficSourceSnapshot source = snapshots[entryIndex];
                if (source.SourceEdge != centerSourceEdge)
                {
                    continue;
                }

                stats.SourceEntries++;
                TrafficGeneratedSnapshot[] connections = source.Connections;
                for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                {
                    TrafficGeneratedSnapshot connection = connections[connectionIndex];
                    stats.Connections++;

                    if (!TryValidateCenterTurnEvidenceSource(
                            centerSourceEdge,
                            connection.SourceEdge,
                            connection.SourceLaneIndex,
                            connection.TargetEdge,
                            connection.TargetLaneIndex,
                            connection.Method,
                            ref stats,
                            skipSamples,
                            "generatedSourceMismatch"))
                    {
                        continue;
                    }

                    int sourceLaneIndex = connection.SourceLaneIndex;
                    if (!TrafficCenterTurnTargetSelector.TryFindTargetByCenterLaneIndex(selectedTargets, sourceLaneIndex, out int targetListIndex) &&
                        !TrafficCenterTurnTargetSelector.TryFindTargetByCenterLaneIndex(selectedTargets, source.SourceLaneIndex, out targetListIndex))
                    {
                        stats.SkippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"sourceTargetMap source={FormatEntity(connection.SourceEdge)}:{sourceLaneIndex} sourceKey={source.SourceLaneIndex} selectedTargets={FormatLaneOrder(selectedTargets)}");
                        continue;
                    }

                    CollectCenterTurnEvidenceTarget(
                        intersectionNode,
                        centerSourceEdge,
                        selectedTargets,
                        targetListIndex,
                        sourceLaneIndex,
                        sourceLaneIndex,
                        connection.TargetEdge,
                        connection.TargetLaneIndex,
                        connection.SourceEdge,
                        connection.IsUnsafe,
                        leftCounts,
                        rightCounts,
                        straightCounts,
                        ref stats,
                        evidenceSamples,
                        skipSamples,
                        connection.Method);
                }
            }

            return TrySelectExtraTargetFromCenterTurnEvidence(
                "centerTrafficSelection",
                intersectionNode,
                centerSourceEdge,
                selectedTargets,
                leftCounts,
                rightCounts,
                straightCounts,
                stats,
                "generatedConnections",
                $"readStats=({TrafficSnapshotHelpers.FormatReadStats(readStats)}) readDetail=({readDetail})",
                evidenceSamples,
                skipSamples,
                out extraTargetIndex,
                out turn,
                out diagnostics);
        }

        private bool TryRefineExtraTargetFromCenterPlan(
            CenterPlan centerPlan,
            Entity intersectionNode,
            Entity centerSourceEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string diagnostics)
        {
            extraTargetIndex = -1;
            turn = TurnDirection.Ambiguous;
            diagnostics = string.Empty;

            if (centerPlan == null)
            {
                diagnostics = $"centerPlanSelection=skipped reason=no-center-plan intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)}";
                return false;
            }

            if (intersectionNode == Entity.Null ||
                centerSourceEdge == Entity.Null ||
                !EntityManager.Exists(intersectionNode))
            {
                diagnostics = $"centerPlanSelection=skipped reason=invalid-node intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)}";
                return false;
            }

            if (selectedTargets == null || selectedTargets.Count == 0)
            {
                diagnostics = "centerPlanSelection=skipped reason=no-selected-targets";
                return false;
            }

            if (centerPlan.BySource.Count == 0)
            {
                diagnostics = $"centerPlanSelection=skipped reason=empty-plan intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} planSources=0 planConnections=0 planDiagnostics={FormatStringList(centerPlan.Diagnostics)}";
                return false;
            }

            int[] leftCounts = new int[selectedTargets.Count];
            int[] rightCounts = new int[selectedTargets.Count];
            int[] straightCounts = new int[selectedTargets.Count];
            CenterTurnEvidenceStats stats = default;
            List<string> evidenceSamples = new List<string>(8);
            List<string> skipSamples = new List<string>(8);

            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> sourcePair in centerPlan.BySource)
            {
                SourceLaneKey sourceKey = sourcePair.Key;
                if (sourceKey.Edge != centerSourceEdge)
                {
                    continue;
                }

                stats.SourceEntries++;
                if (!TrafficCenterTurnTargetSelector.TryFindTargetByCenterLaneIndex(
                        selectedTargets,
                        sourceKey.LaneIndex,
                        out int targetListIndex))
                {
                    stats.SkippedConnections += sourcePair.Value.Count;
                    AddCenterTrafficSelectionSample(skipSamples, $"sourceTargetMap source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} selectedTargets={FormatLaneOrder(selectedTargets)}");
                    continue;
                }

                foreach (LaneMapping mapping in sourcePair.Value.Values)
                {
                    stats.Connections++;
                    if (!TryValidateCenterTurnEvidenceSource(
                            centerSourceEdge,
                            mapping.SourceEdge,
                            mapping.SourceLaneIndex,
                            mapping.TargetEdge,
                            mapping.TargetLaneIndex,
                            mapping.Method,
                            ref stats,
                            skipSamples,
                            "mappingSourceMismatch"))
                    {
                        continue;
                    }

                    CollectCenterTurnEvidenceTarget(
                        intersectionNode,
                        centerSourceEdge,
                        selectedTargets,
                        targetListIndex,
                        mapping.SourceLaneIndex,
                        sourceKey.LaneIndex,
                        mapping.TargetEdge,
                        mapping.TargetLaneIndex,
                        mapping.SourceEdge,
                        mapping.IsUnsafe,
                        leftCounts,
                        rightCounts,
                        straightCounts,
                        ref stats,
                        evidenceSamples,
                        skipSamples,
                        mapping.Method);
                }
            }

            if (stats.SourceEntries == 0)
            {
                diagnostics = $"centerPlanSelection=skipped reason=no-matching-source intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} planSources={centerPlan.BySource.Count} planConnections={TrafficCenterMappingBuilder.CountTrafficPlanConnections(centerPlan.BySource)} planDiagnostics={FormatStringList(centerPlan.Diagnostics)}";
                return false;
            }

            return TrySelectExtraTargetFromCenterTurnEvidence(
                "centerPlanSelection",
                intersectionNode,
                centerSourceEdge,
                selectedTargets,
                leftCounts,
                rightCounts,
                straightCounts,
                stats,
                "planConnections",
                $"planSources={centerPlan.BySource.Count}",
                evidenceSamples,
                skipSamples,
                out extraTargetIndex,
                out turn,
                out diagnostics,
                $"planDiagnostics={FormatStringList(centerPlan.Diagnostics)}");
        }

        private bool TryValidateCenterTurnEvidenceSource(
            Entity centerSourceEdge,
            Entity sourceEdge,
            int sourceLaneIndex,
            Entity targetEdge,
            int targetLaneIndex,
            PathMethod method,
            ref CenterTurnEvidenceStats stats,
            List<string> skipSamples,
            string sourceMismatchReason)
        {
            if (sourceEdge != centerSourceEdge)
            {
                stats.SkippedConnections++;
                AddCenterTrafficSelectionSample(skipSamples, $"{sourceMismatchReason} {FormatEntity(sourceEdge)}:{sourceLaneIndex}");
                return false;
            }

            if ((method & PathMethod.Road) == 0)
            {
                stats.SkippedConnections++;
                AddCenterTrafficSelectionSample(skipSamples, $"nonRoad {FormatEntity(sourceEdge)}:{sourceLaneIndex}->{FormatEntity(targetEdge)}:{targetLaneIndex} method={method}");
                return false;
            }

            return true;
        }

        private void CollectCenterTurnEvidenceTarget(
            Entity intersectionNode,
            Entity centerSourceEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            int targetListIndex,
            int sourceLaneIndex,
            int evidenceSourceLaneIndex,
            Entity targetEdge,
            int targetLaneIndex,
            Entity sourceEdge,
            bool isUnsafe,
            int[] leftCounts,
            int[] rightCounts,
            int[] straightCounts,
            ref CenterTurnEvidenceStats stats,
            List<string> evidenceSamples,
            List<string> skipSamples,
            PathMethod method)
        {
            if (targetEdge == centerSourceEdge ||
                targetEdge == sourceEdge)
            {
                stats.SkippedConnections++;
                stats.SkippedSelfReferences++;
                AddCenterTrafficSelectionSample(skipSamples, $"selfReference source={FormatEntity(sourceEdge)}:{sourceLaneIndex} target={FormatEntity(targetEdge)}:{targetLaneIndex}");
                return;
            }

            if (targetEdge == Entity.Null ||
                !EntityManager.Exists(targetEdge) ||
                EntityManager.HasComponent<Deleted>(targetEdge) ||
                !IsEdgeConnectedToNode(targetEdge, intersectionNode))
            {
                stats.SkippedConnections++;
                AddCenterTrafficSelectionSample(skipSamples, $"targetUnavailable source={FormatEntity(sourceEdge)}:{sourceLaneIndex} target={FormatEntity(targetEdge)}:{targetLaneIndex}");
                return;
            }

            stats.MappedSelectedTargets++;
            TurnDirection connectorTurn = TrafficConnectorMovementClassifier.ClassifyCenterConnectorTurn(
                EntityManager,
                intersectionNode,
                centerSourceEdge,
                targetEdge,
                default);
            TrafficCenterTurnTargetSelector.AddTurnCount(
                connectorTurn,
                targetListIndex,
                leftCounts,
                rightCounts,
                straightCounts);
            stats.ClassifiedConnections++;
            AddCenterTrafficSelectionSample(
                evidenceSamples,
                $"{evidenceSourceLaneIndex}->target{selectedTargets[targetListIndex].LaneIndex}/{connectorTurn}/{FormatEntity(targetEdge)}/{method}{(isUnsafe ? "!unsafe" : string.Empty)}");
        }

        private bool TrySelectExtraTargetFromCenterTurnEvidence(
            string selectionPrefix,
            Entity intersectionNode,
            Entity centerSourceEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            IReadOnlyList<int> leftCounts,
            IReadOnlyList<int> rightCounts,
            IReadOnlyList<int> straightCounts,
            CenterTurnEvidenceStats stats,
            string connectionCountField,
            string contextFields,
            List<string> evidenceSamples,
            List<string> skipSamples,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string diagnostics,
            string trailingFields = null)
        {
            string countDiagnostics = FormatCenterTurnDiagnostics(
                selectedTargets,
                leftCounts,
                rightCounts,
                straightCounts,
                null);
            string commonFields = $"intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} {contextFields} sourceEntries={stats.SourceEntries} {connectionCountField}={stats.Connections} mappedSelectedTargets={stats.MappedSelectedTargets} classifiedConnections={stats.ClassifiedConnections} skippedConnections={stats.SkippedConnections} skippedSelfReferences={stats.SkippedSelfReferences}";
            string sampleFields = $"evidenceSamples={FormatStringList(evidenceSamples)} skipSamples={FormatStringList(skipSamples)}{(string.IsNullOrEmpty(trailingFields) ? string.Empty : $" {trailingFields}")}";
            if (!TrafficCenterTurnTargetSelector.TrySelectExtraTarget(
                    selectedTargets,
                    leftCounts,
                    rightCounts,
                    straightCounts,
                    out extraTargetIndex,
                    out turn,
                    out string selectionDiagnostic))
            {
                diagnostics = $"{selectionPrefix}=failed {commonFields} counts=({countDiagnostics}) {selectionDiagnostic} {sampleFields}";
                return false;
            }

            diagnostics = $"{selectionPrefix}=selected {commonFields} selectedExtra={selectedTargets[extraTargetIndex].LaneIndex}/{turn} counts=({countDiagnostics}) {selectionDiagnostic} {sampleFields}";
            return true;
        }

        private static string CombineDiagnostics(params string[] diagnostics)
        {
            List<string> values = new List<string>(diagnostics.Length);
            for (int i = 0; i < diagnostics.Length; i++)
            {
                if (!string.IsNullOrEmpty(diagnostics[i]))
                {
                    values.Add(diagnostics[i]);
                }
            }

            return values.Count == 0 ? string.Empty : string.Join("; ", values);
        }

        private static void AddCenterTrafficSelectionSample(List<string> samples, string value)
        {
            if (samples.Count < 8)
            {
                samples.Add(value);
            }
        }
    }
}
