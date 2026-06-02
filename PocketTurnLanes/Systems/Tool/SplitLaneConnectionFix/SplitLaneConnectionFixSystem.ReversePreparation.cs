using System;
using System.Collections.Generic;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryPrepareReverseMappings(
            ref Request request,
            Entity outerEdge,
            out string mappingSource,
            out string reason)
        {
            mappingSource = "none";
            reason = string.Empty;
            m_ReverseSourceLanes.Clear();
            m_ReverseTargetLanes.Clear();

            CollectEdgeCarLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_ReverseSourceLanes);
            CollectEdgeCarLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_ReverseTargetLanes);

            if (m_ReverseSourceLanes.Count == 0 && m_ReverseTargetLanes.Count == 0)
            {
                request.ReverseSourceLanes = Array.Empty<LaneEndpoint>();
                request.ReverseTargetLanes = Array.Empty<LaneEndpoint>();
                request.ReverseMappings = Array.Empty<LaneMapping>();
                mappingSource = "no-reverse-lanes";
                MarkReverseRoadSkipped(ref request, "roadMappingSkipped=noReverseLanes");
                return true;
            }

            if (m_ReverseSourceLanes.Count == 0 || m_ReverseTargetLanes.Count == 0)
            {
                reason = $"one-sided reverse lanes source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count}";
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                MarkReverseRoadSkipped(ref request, $"roadMappingSkipped=reverseOneSidedLaneData {reason}");
                mappingSource = "reverse-skipped-one-sided-lane-data";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} mode={request.Mode} reason={reason}; preserveExistingDirection=True forwardUnaffected=True.");
                return true;
            }

            if (request.Mode == RepairMode.Standard &&
                m_ReverseSourceLanes.Count != m_ReverseTargetLanes.Count)
            {
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                request.ReverseMappings = Array.Empty<LaneMapping>();
                reason = $"roadMappingSkipped=reverseLayoutMismatch source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count} expected={m_ReverseSourceLanes.Count}";
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "standard-reverse-skipped-layout-mismatch";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Standard reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N {reason} preserveExistingDirection=True forwardUnaffected=True outerPreservationSnapshotCaptured={request.OuterPreservationSnapshotCaptured} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
                return true;
            }

            float2 travelDirection = m_ReverseSourceLanes[0].TravelDirection;
            float2 right = new float2(travelDirection.y, -travelDirection.x);
            float2 sourceOrigin = TrafficLaneEndpointHelpers.GetAveragePosition(m_ReverseSourceLanes);
            TrafficLaneEndpointHelpers.AssignLaterals(m_ReverseSourceLanes, sourceOrigin, right);
            TrafficLaneEndpointHelpers.AssignLaterals(m_ReverseTargetLanes, sourceOrigin, right);

            if (request.Mode == RepairMode.BalancedOppositeTarget)
            {
                return TryPrepareBalancedReverseMappings(ref request, outerEdge, out mappingSource, out reason);
            }

            if (request.Mode == RepairMode.ShortEdgeTransition)
            {
                return TryPrepareShortEdgeReverseMappings(ref request, outerEdge, out mappingSource, out reason);
            }

            return TryPrepareStandardReverseMappings(ref request, outerEdge, out mappingSource, out reason);
        }

        private bool TryPrepareBalancedReverseMappings(
            ref Request request,
            Entity outerEdge,
            out string mappingSource,
            out string reason)
        {
            mappingSource = "none";
            reason = string.Empty;
            if (request.FarIntersectionNode == Entity.Null ||
                !EntityManager.Exists(request.FarIntersectionNode))
            {
                reason = $"roadMappingSkipped=balancedReverseMissingFarIntersection farIntersection={FormatEntity(request.FarIntersectionNode)}";
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "balanced-reverse-skipped-missing-far-intersection";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Balanced reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N+1 reason={reason}; preserveExistingDirection=True forwardUnaffected=True.");
                return true;
            }

            if (m_ReverseTargetLanes.Count < m_ReverseSourceLanes.Count + 1)
            {
                reason = $"roadMappingSkipped=balancedReverseLayoutMismatch source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count} expectedTargetAtLeast={m_ReverseSourceLanes.Count + 1}";
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "balanced-reverse-skipped-layout-mismatch";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Balanced reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N+1 reason={reason}; preserveExistingDirection=True forwardUnaffected=True reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
                return true;
            }

            if (!TrafficLaneTargetSelector.TrySelectPocketTargets(m_ReverseSourceLanes, m_ReverseTargetLanes, out List<LaneEndpoint> selectedReverseTargets, out int extraTargetListIndex, out float mappingScore))
            {
                reason = $"roadMappingSkipped=balancedReverseTargetSubsetSelectionFailed source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count}";
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "balanced-reverse-skipped-target-subset-selection";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Balanced reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N+1 reason={reason}; preserveExistingDirection=True forwardUnaffected=True reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
                return true;
            }

            TurnDirection turn = TrafficLaneTargetSelector.DetermineTurn(selectedReverseTargets, extraTargetListIndex);
            string centerTurnDiagnostic = "not-run";
            bool centerTurnEvidence = false;
            if (TryRefineExtraTargetFromCenterConnectors(
                    request.FarIntersectionNode,
                    outerEdge,
                    selectedReverseTargets,
                    out int centerExtraTargetListIndex,
                    out TurnDirection centerTurn,
                    out centerTurnDiagnostic))
            {
                centerTurnEvidence = true;
                if (centerExtraTargetListIndex != extraTargetListIndex || centerTurn != turn)
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far-center connector turn target overrides balanced reverse split lateral target splitNode={FormatEntity(request.SplitNode)} oldExtra={selectedReverseTargets[extraTargetListIndex].LaneIndex}/{turn} newExtra={selectedReverseTargets[centerExtraTargetListIndex].LaneIndex}/{centerTurn} diagnostics={centerTurnDiagnostic}.");
                }

                extraTargetListIndex = centerExtraTargetListIndex;
                turn = centerTurn;
            }

            if (turn == TurnDirection.Ambiguous)
            {
                reason = $"roadMappingSkipped=balancedReverseAmbiguousTurn extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}";
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "balanced-reverse-skipped-ambiguous-turn";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Balanced reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N+1 selectedTargets={FormatLaneOrder(selectedReverseTargets)} reason={reason}; preserveExistingDirection=True forwardUnaffected=True.");
                return true;
            }

            int branchSourceListIndex = turn == TurnDirection.Right ? m_ReverseSourceLanes.Count - 1 : 0;
            int branchSourceLaneIndex = m_ReverseSourceLanes[branchSourceListIndex].LaneIndex;
            int extraTargetLaneIndex = selectedReverseTargets[extraTargetListIndex].LaneIndex;

            CollectConnectorLanes(request.SplitNode, request.PocketEdge, outerEdge, m_ExistingConnectorLanes);
            if (m_ExistingConnectorLanes.Count == 0)
            {
                reason = $"roadMappingSkipped=balancedReverseWaitingForGeneratedConnectorTemplate source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count}";
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "balanced-reverse-skipped-waiting-for-template";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Balanced reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N+1 reason={reason}; preservationFallback=True forwardUnaffected=True.");
                return true;
            }

            if (!TryBuildDesiredMappings(
                    m_ReverseSourceLanes,
                    selectedReverseTargets,
                    extraTargetListIndex,
                    branchSourceLaneIndex,
                    m_ExistingConnectorLanes,
                    preferExistingConnectors: !centerTurnEvidence,
                    out LaneMapping[] balancedReverseMappings,
                    out string balancedMappingSource,
                    out string balancedMappingReason))
            {
                reason = $"roadMappingSkipped=balancedReverseDesiredMappingFailed detail=({balancedMappingReason}) reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} selectedTargets={FormatLaneOrder(selectedReverseTargets)} extraTarget={extraTargetLaneIndex} branchSource={branchSourceLaneIndex} existingReverse={FormatConnectorLanes(m_ExistingConnectorLanes)}";
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "balanced-reverse-skipped-desired-mapping-failed";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Balanced reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N+1 reason={reason}; preserveExistingDirection=True forwardUnaffected=True.");
                return true;
            }

            request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
            request.ReverseTargetLanes = selectedReverseTargets.ToArray();
            request.ReverseMappings = balancedReverseMappings;
            MarkReverseRoadPrepared(ref request);
            mappingSource = $"balanced-reverse-N->N+1-{balancedMappingSource}; score={mappingScore:0.###}; turn={turn}; branchSource={branchSourceLaneIndex}; extraTarget={extraTargetLaneIndex}; centerDiagnostics={centerTurnDiagnostic}";
            return true;
        }

        private bool TryPrepareShortEdgeReverseMappings(
            ref Request request,
            Entity outerEdge,
            out string mappingSource,
            out string reason)
        {
            mappingSource = "none";
            reason = string.Empty;
            if (!TryBuildSnapshotReverseMappings(
                    request.TransitionReverseSnapshot,
                    m_ReverseSourceLanes,
                    m_ReverseTargetLanes,
                    request.PocketEdge,
                    outerEdge,
                    out LaneMapping[] snapshotReverseMappings,
                    out mappingSource,
                    out string snapshotReason))
            {
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                request.ReverseMappings = Array.Empty<LaneMapping>();
                reason = $"roadMappingSkipped=shortEdgeTransitionSnapshotReverseFailed detail=({snapshotReason})";
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = $"short-edge-transition-reverse-skipped: {snapshotReason}";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Short-edge transition reverse restore skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} reason={snapshotReason} snapshot={FormatSnapshot(request.TransitionReverseSnapshot)} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)}; preserveExistingDirection=True forwardUnaffected=True.");
                return true;
            }

            request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
            request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
            request.ReverseMappings = snapshotReverseMappings;
            MarkReverseRoadPrepared(ref request);
            mappingSource = $"short-edge-transition-snapshot; {mappingSource}";
            return true;
        }

        private bool TryPrepareStandardReverseMappings(
            ref Request request,
            Entity outerEdge,
            out string mappingSource,
            out string reason)
        {
            mappingSource = "none";
            reason = string.Empty;
            CollectConnectorLanes(request.SplitNode, request.PocketEdge, outerEdge, m_ExistingConnectorLanes);
            if (!TryBuildStraightMappings(
                    m_ReverseSourceLanes,
                    m_ReverseTargetLanes,
                    m_ExistingConnectorLanes,
                    out LaneMapping[] reverseMappings,
                    out mappingSource,
                    out string buildReason))
            {
                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                request.ReverseMappings = Array.Empty<LaneMapping>();
                reason = $"roadMappingSkipped=reverseStraightMappingFailed detail=({buildReason}) reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)} existingReverse={FormatConnectorLanes(m_ExistingConnectorLanes)}";
                MarkReverseRoadSkipped(ref request, reason);
                mappingSource = "standard-reverse-skipped-straight-mapping-failed";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Standard reverse road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} rule=N->N preserveExistingDirection=True forwardUnaffected=True outerPreservationSnapshotCaptured={request.OuterPreservationSnapshotCaptured} reason={reason}.");
                return true;
            }

            request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
            request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
            request.ReverseMappings = reverseMappings;
            MarkReverseRoadPrepared(ref request);
            return true;
        }
    }
}
