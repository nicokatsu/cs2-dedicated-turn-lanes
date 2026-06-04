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
            int sourceEntries = 0;
            int generatedConnections = 0;
            int mappedSelectedTargets = 0;
            int classifiedConnections = 0;
            int skippedConnections = 0;
            int skippedSelfReferences = 0;
            List<string> evidenceSamples = new List<string>(8);
            List<string> skipSamples = new List<string>(8);

            for (int entryIndex = 0; entryIndex < snapshots.Count; entryIndex++)
            {
                TrafficSourceSnapshot source = snapshots[entryIndex];
                if (source.SourceEdge != centerSourceEdge)
                {
                    continue;
                }

                sourceEntries++;
                TrafficGeneratedSnapshot[] connections = source.Connections;
                for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                {
                    TrafficGeneratedSnapshot connection = connections[connectionIndex];
                    generatedConnections++;
                    if (connection.SourceEdge != centerSourceEdge)
                    {
                        skippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"generatedSourceMismatch {FormatEntity(connection.SourceEdge)}:{connection.SourceLaneIndex}");
                        continue;
                    }

                    if ((connection.Method & PathMethod.Road) == 0)
                    {
                        skippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"nonRoad {FormatEntity(connection.SourceEdge)}:{connection.SourceLaneIndex}->{FormatEntity(connection.TargetEdge)}:{connection.TargetLaneIndex} method={connection.Method}");
                        continue;
                    }

                    int sourceLaneIndex = connection.SourceLaneIndex;
                    if (!TrafficCenterTurnTargetSelector.TryFindTargetByCenterLaneIndex(selectedTargets, sourceLaneIndex, out int targetListIndex) &&
                        !TrafficCenterTurnTargetSelector.TryFindTargetByCenterLaneIndex(selectedTargets, source.SourceLaneIndex, out targetListIndex))
                    {
                        skippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"sourceTargetMap source={FormatEntity(connection.SourceEdge)}:{sourceLaneIndex} sourceKey={source.SourceLaneIndex} selectedTargets={FormatLaneOrder(selectedTargets)}");
                        continue;
                    }

                    if (connection.TargetEdge == centerSourceEdge ||
                        connection.TargetEdge == connection.SourceEdge)
                    {
                        skippedConnections++;
                        skippedSelfReferences++;
                        AddCenterTrafficSelectionSample(skipSamples, $"selfReference source={FormatEntity(connection.SourceEdge)}:{sourceLaneIndex} target={FormatEntity(connection.TargetEdge)}:{connection.TargetLaneIndex}");
                        continue;
                    }

                    if (connection.TargetEdge == Entity.Null ||
                        !EntityManager.Exists(connection.TargetEdge) ||
                        EntityManager.HasComponent<Deleted>(connection.TargetEdge) ||
                        !IsEdgeConnectedToNode(connection.TargetEdge, intersectionNode))
                    {
                        skippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"targetUnavailable source={FormatEntity(connection.SourceEdge)}:{sourceLaneIndex} target={FormatEntity(connection.TargetEdge)}:{connection.TargetLaneIndex}");
                        continue;
                    }

                    mappedSelectedTargets++;
                    TurnDirection connectorTurn = TrafficConnectorMovementClassifier.ClassifyCenterConnectorTurn(
                        EntityManager,
                        intersectionNode,
                        centerSourceEdge,
                        connection.TargetEdge,
                        default);
                    TrafficCenterTurnTargetSelector.AddTurnCount(
                        connectorTurn,
                        targetListIndex,
                        leftCounts,
                        rightCounts,
                        straightCounts);
                    classifiedConnections++;
                    AddCenterTrafficSelectionSample(
                        evidenceSamples,
                        $"{sourceLaneIndex}->target{selectedTargets[targetListIndex].LaneIndex}/{connectorTurn}/{FormatEntity(connection.TargetEdge)}/{connection.Method}{(connection.IsUnsafe ? "!unsafe" : string.Empty)}");
                }
            }

            string countDiagnostics = FormatCenterTurnDiagnostics(
                selectedTargets,
                leftCounts,
                rightCounts,
                straightCounts,
                null);
            if (!TrafficCenterTurnTargetSelector.TrySelectExtraTarget(
                    selectedTargets,
                    leftCounts,
                    rightCounts,
                    straightCounts,
                    out extraTargetIndex,
                    out turn,
                    out string selectionDiagnostic))
            {
                diagnostics = $"centerTrafficSelection=failed intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} readStats=({TrafficSnapshotHelpers.FormatReadStats(readStats)}) readDetail=({readDetail}) sourceEntries={sourceEntries} generatedConnections={generatedConnections} mappedSelectedTargets={mappedSelectedTargets} classifiedConnections={classifiedConnections} skippedConnections={skippedConnections} skippedSelfReferences={skippedSelfReferences} counts=({countDiagnostics}) {selectionDiagnostic} evidenceSamples={FormatStringList(evidenceSamples)} skipSamples={FormatStringList(skipSamples)}";
                return false;
            }

            diagnostics = $"centerTrafficSelection=selected intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} readStats=({TrafficSnapshotHelpers.FormatReadStats(readStats)}) readDetail=({readDetail}) sourceEntries={sourceEntries} generatedConnections={generatedConnections} mappedSelectedTargets={mappedSelectedTargets} classifiedConnections={classifiedConnections} skippedConnections={skippedConnections} skippedSelfReferences={skippedSelfReferences} selectedExtra={selectedTargets[extraTargetIndex].LaneIndex}/{turn} counts=({countDiagnostics}) {selectionDiagnostic} evidenceSamples={FormatStringList(evidenceSamples)} skipSamples={FormatStringList(skipSamples)}";
            return true;
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
            int sourceEntries = 0;
            int planConnections = 0;
            int mappedSelectedTargets = 0;
            int classifiedConnections = 0;
            int skippedConnections = 0;
            int skippedSelfReferences = 0;
            List<string> evidenceSamples = new List<string>(8);
            List<string> skipSamples = new List<string>(8);

            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> sourcePair in centerPlan.BySource)
            {
                SourceLaneKey sourceKey = sourcePair.Key;
                if (sourceKey.Edge != centerSourceEdge)
                {
                    continue;
                }

                sourceEntries++;
                if (!TrafficCenterTurnTargetSelector.TryFindTargetByCenterLaneIndex(
                        selectedTargets,
                        sourceKey.LaneIndex,
                        out int targetListIndex))
                {
                    skippedConnections += sourcePair.Value.Count;
                    AddCenterTrafficSelectionSample(skipSamples, $"sourceTargetMap source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} selectedTargets={FormatLaneOrder(selectedTargets)}");
                    continue;
                }

                foreach (LaneMapping mapping in sourcePair.Value.Values)
                {
                    planConnections++;
                    if (mapping.SourceEdge != centerSourceEdge)
                    {
                        skippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"mappingSourceMismatch {FormatEntity(mapping.SourceEdge)}:{mapping.SourceLaneIndex}");
                        continue;
                    }

                    if ((mapping.Method & PathMethod.Road) == 0)
                    {
                        skippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"nonRoad {FormatEntity(mapping.SourceEdge)}:{mapping.SourceLaneIndex}->{FormatEntity(mapping.TargetEdge)}:{mapping.TargetLaneIndex} method={mapping.Method}");
                        continue;
                    }

                    if (mapping.TargetEdge == centerSourceEdge ||
                        mapping.TargetEdge == mapping.SourceEdge)
                    {
                        skippedConnections++;
                        skippedSelfReferences++;
                        AddCenterTrafficSelectionSample(skipSamples, $"selfReference source={FormatEntity(mapping.SourceEdge)}:{mapping.SourceLaneIndex} target={FormatEntity(mapping.TargetEdge)}:{mapping.TargetLaneIndex}");
                        continue;
                    }

                    if (mapping.TargetEdge == Entity.Null ||
                        !EntityManager.Exists(mapping.TargetEdge) ||
                        EntityManager.HasComponent<Deleted>(mapping.TargetEdge) ||
                        !IsEdgeConnectedToNode(mapping.TargetEdge, intersectionNode))
                    {
                        skippedConnections++;
                        AddCenterTrafficSelectionSample(skipSamples, $"targetUnavailable source={FormatEntity(mapping.SourceEdge)}:{mapping.SourceLaneIndex} target={FormatEntity(mapping.TargetEdge)}:{mapping.TargetLaneIndex}");
                        continue;
                    }

                    mappedSelectedTargets++;
                    TurnDirection connectorTurn = TrafficConnectorMovementClassifier.ClassifyCenterConnectorTurn(
                        EntityManager,
                        intersectionNode,
                        centerSourceEdge,
                        mapping.TargetEdge,
                        default);
                    TrafficCenterTurnTargetSelector.AddTurnCount(
                        connectorTurn,
                        targetListIndex,
                        leftCounts,
                        rightCounts,
                        straightCounts);
                    classifiedConnections++;
                    AddCenterTrafficSelectionSample(
                        evidenceSamples,
                        $"{sourceKey.LaneIndex}->target{selectedTargets[targetListIndex].LaneIndex}/{connectorTurn}/{FormatEntity(mapping.TargetEdge)}/{mapping.Method}{(mapping.IsUnsafe ? "!unsafe" : string.Empty)}");
                }
            }

            if (sourceEntries == 0)
            {
                diagnostics = $"centerPlanSelection=skipped reason=no-matching-source intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} planSources={centerPlan.BySource.Count} planConnections={TrafficCenterMappingBuilder.CountTrafficPlanConnections(centerPlan.BySource)} planDiagnostics={FormatStringList(centerPlan.Diagnostics)}";
                return false;
            }

            string countDiagnostics = FormatCenterTurnDiagnostics(
                selectedTargets,
                leftCounts,
                rightCounts,
                straightCounts,
                null);
            if (!TrafficCenterTurnTargetSelector.TrySelectExtraTarget(
                    selectedTargets,
                    leftCounts,
                    rightCounts,
                    straightCounts,
                    out extraTargetIndex,
                    out turn,
                    out string selectionDiagnostic))
            {
                diagnostics = $"centerPlanSelection=failed intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} planSources={centerPlan.BySource.Count} sourceEntries={sourceEntries} planConnections={planConnections} mappedSelectedTargets={mappedSelectedTargets} classifiedConnections={classifiedConnections} skippedConnections={skippedConnections} skippedSelfReferences={skippedSelfReferences} counts=({countDiagnostics}) {selectionDiagnostic} evidenceSamples={FormatStringList(evidenceSamples)} skipSamples={FormatStringList(skipSamples)} planDiagnostics={FormatStringList(centerPlan.Diagnostics)}";
                return false;
            }

            diagnostics = $"centerPlanSelection=selected intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)} planSources={centerPlan.BySource.Count} sourceEntries={sourceEntries} planConnections={planConnections} mappedSelectedTargets={mappedSelectedTargets} classifiedConnections={classifiedConnections} skippedConnections={skippedConnections} skippedSelfReferences={skippedSelfReferences} selectedExtra={selectedTargets[extraTargetIndex].LaneIndex}/{turn} counts=({countDiagnostics}) {selectionDiagnostic} evidenceSamples={FormatStringList(evidenceSamples)} skipSamples={FormatStringList(skipSamples)} planDiagnostics={FormatStringList(centerPlan.Diagnostics)}";
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
