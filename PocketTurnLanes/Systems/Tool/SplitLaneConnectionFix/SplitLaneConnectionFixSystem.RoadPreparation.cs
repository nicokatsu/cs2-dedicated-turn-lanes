using System.Collections.Generic;
using Colossal.Entities;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryPrepareMappings(ref Request request)
        {
            ResetRoadPreparation(ref request);

            if (!EntityManager.Exists(request.SplitNode) ||
                !EntityManager.Exists(request.PocketEdge) ||
                !EntityManager.TryGetComponent(request.PocketEdge, out NetEdge _))
            {
                string reason = "missing split node or pocket edge";
                QueueUturnCleanup(ref request, Entity.Null, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Missing split node or pocket edge splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)}.");
                return false;
            }

            if (!TryFindOuterEdge(request, out Entity outerEdge))
            {
                string reason = "outer edge not found";
                QueueUturnCleanup(ref request, Entity.Null, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot identify outer edge splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)} original={FormatEntity(request.OriginalEdge)} sourcePrefab={FormatEntity(request.SourcePrefab)}.");
                return false;
            }

            request.OuterEdge = outerEdge;
            m_SourceLanes.Clear();
            m_TargetLanes.Clear();
            CollectEdgeCarLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_SourceLanes);
            CollectEdgeCarLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TargetLanes);

            if (m_SourceLanes.Count == 0 || m_TargetLanes.Count == 0)
            {
                string reason = $"missing lane data source={m_SourceLanes.Count} target={m_TargetLanes.Count}";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Missing lane data splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count}.");
                return false;
            }

            request.SourceLanes = m_SourceLanes.ToArray();
            request.TargetLanes = m_TargetLanes.ToArray();
            EnsureOuterPreservationSnapshotCaptured(ref request, outerEdge, "capture-before-directional-road-mapping");

            PrepareForwardRoadMappings(ref request, outerEdge, out ForwardRoadPreparationResult forwardResult);

            if (!TryPrepareReverseMappings(ref request, outerEdge, out string reverseMappingSource, out string reverseMappingReason))
            {
                QueueUturnCleanup(ref request, outerEdge, $"reverse mapping failed: {reverseMappingReason}");
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot prepare reverse split-node mapping splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reverseMappingReason}; leaving connectors unchanged to avoid partial Traffic data.");
                return false;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Prepared directional Traffic mapping plan splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} farIntersection={FormatEntity(request.FarIntersectionNode)} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} forwardRule=N->N+1 sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count} selectedTargetCount={request.TargetLanes?.Length ?? 0} mappingScore={forwardResult.MappingScore:0.###} mappingSource={forwardResult.MappingSource} mappingReason={forwardResult.MappingReason} turn={forwardResult.Turn} branchSource={forwardResult.BranchSourceLaneIndex} extraTarget={forwardResult.ExtraTargetLaneIndex} centerDiagnostics={forwardResult.CenterTurnDiagnostic} existingConnectors={m_ExistingConnectorLanes.Count} existing={FormatConnectorLanes(m_ExistingConnectorLanes)} mappings={FormatMappings(request.Mappings)} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} reverseSourceCount={request.ReverseSourceLanes?.Length ?? 0} reverseTargetCount={request.ReverseTargetLanes?.Length ?? 0} reverseMappingSource={reverseMappingSource} reverseMappingReason={reverseMappingReason} reverseMappings={FormatMappings(request.ReverseMappings)} preservationForwardSource=({FormatLaneOrder(request.PreservationForwardSourceLanes)}) preservationForwardTarget=({FormatLaneOrder(request.PreservationForwardTargetLanes)}) preservationReverseSource=({FormatLaneOrder(request.PreservationReverseSourceLanes)}) preservationReverseTarget=({FormatLaneOrder(request.PreservationReverseTargetLanes)}) preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] preservationSkippedReason={request.PreservationSkippedReason}.");
            return true;
        }

        private void PrepareForwardRoadMappings(ref Request request, Entity outerEdge, out ForwardRoadPreparationResult result)
        {
            string forwardMappingSource = "skipped";
            string forwardMappingReason = string.Empty;
            float mappingScore = 0f;
            string centerTurnDiagnostic = "not-run";
            TurnDirection turn = TurnDirection.Ambiguous;
            int branchSourceLaneIndex = -1;
            int extraTargetLaneIndex = -1;
            int expectedForwardTargets = m_SourceLanes.Count + 1;
            if (m_TargetLanes.Count != expectedForwardTargets)
            {
                string reason = $"roadMappingSkipped=forwardLayoutMismatch source={m_SourceLanes.Count} target={m_TargetLanes.Count} expected={expectedForwardTargets}";
                MarkForwardRoadSkipped(ref request, reason);
                forwardMappingReason = reason;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Forward road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} rule=N->N+1 sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count} expectedTarget={expectedForwardTargets}; preserveExistingDirection=True continueReverse=True.");
            }
            else
            {
                float2 travelDirection = m_SourceLanes[0].TravelDirection;
                float2 right = new float2(travelDirection.y, -travelDirection.x);
                float2 sourceOrigin = GetAveragePosition(m_SourceLanes);
                AssignLaneLaterals(m_SourceLanes, sourceOrigin, right);
                AssignLaneLaterals(m_TargetLanes, sourceOrigin, right);
                request.SourceLanes = m_SourceLanes.ToArray();
                request.TargetLanes = m_TargetLanes.ToArray();

                if (!TrySelectLaneMapping(m_SourceLanes, m_TargetLanes, out List<LaneEndpoint> selectedTargets, out int extraTargetListIndex, out mappingScore))
                {
                    string reason = $"roadMappingSkipped=forwardTargetSubsetSelectionFailed source={m_SourceLanes.Count} target={m_TargetLanes.Count}";
                    MarkForwardRoadSkipped(ref request, reason);
                    forwardMappingReason = reason;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Forward road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} rule=N->N+1 sourceOrder={FormatLaneOrder(m_SourceLanes)} targetOrder={FormatLaneOrder(m_TargetLanes)} reason={reason}; preserveExistingDirection=True continueReverse=True.");
                }
                else
                {
                    turn = DetermineTurn(selectedTargets, extraTargetListIndex);
                    bool centerTurnEvidence = false;
                    if (TryRefineExtraTargetFromCenterConnectors(
                            request.IntersectionNode,
                            request.PocketEdge,
                            selectedTargets,
                            out int centerExtraTargetListIndex,
                            out TurnDirection centerTurn,
                            out centerTurnDiagnostic))
                    {
                        centerTurnEvidence = true;
                        if (centerExtraTargetListIndex != extraTargetListIndex || centerTurn != turn)
                        {
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Center connector turn target overrides split lateral target splitNode={FormatEntity(request.SplitNode)} oldExtra={selectedTargets[extraTargetListIndex].LaneIndex}/{turn} newExtra={selectedTargets[centerExtraTargetListIndex].LaneIndex}/{centerTurn} diagnostics={centerTurnDiagnostic}.");
                        }

                        extraTargetListIndex = centerExtraTargetListIndex;
                        turn = centerTurn;
                    }

                    if (turn == TurnDirection.Ambiguous)
                    {
                        string reason = $"roadMappingSkipped=forwardAmbiguousTurn extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}";
                        MarkForwardRoadSkipped(ref request, reason);
                        forwardMappingReason = reason;
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Forward road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} selectedTargets={FormatLaneOrder(selectedTargets)} extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}; preserveExistingDirection=True continueReverse=True.");
                    }
                    else
                    {
                        int branchSourceListIndex = turn == TurnDirection.Right ? m_SourceLanes.Count - 1 : 0;
                        branchSourceLaneIndex = m_SourceLanes[branchSourceListIndex].LaneIndex;
                        extraTargetLaneIndex = selectedTargets[extraTargetListIndex].LaneIndex;

                        CollectConnectorLanes(request.SplitNode, outerEdge, request.PocketEdge, m_ExistingConnectorLanes);
                        if (m_ExistingConnectorLanes.Count == 0)
                        {
                            string reason = "roadMappingSkipped=forwardWaitingForGeneratedConnectorTemplate";
                            MarkForwardRoadSkipped(ref request, reason);
                            forwardMappingReason = reason;
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Forward road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count} reason={reason}; preservationFallback=True continueReverse=True.");
                        }
                        else if (!TryBuildDesiredMappings(
                                     m_SourceLanes,
                                     selectedTargets,
                                     extraTargetListIndex,
                                     branchSourceLaneIndex,
                                     m_ExistingConnectorLanes,
                                     preferExistingConnectors: !centerTurnEvidence,
                                     out LaneMapping[] mappings,
                                     out forwardMappingSource,
                                     out string mappingReason))
                        {
                            string reason = $"roadMappingSkipped=forwardDesiredMappingFailed detail=({mappingReason})";
                            MarkForwardRoadSkipped(ref request, reason);
                            forwardMappingReason = reason;
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Forward road mapping skipped independently splitNode={FormatEntity(request.SplitNode)} sourceOrder={FormatLaneOrder(m_SourceLanes)} selectedTargets={FormatLaneOrder(selectedTargets)} extraTarget={extraTargetLaneIndex} branchSource={branchSourceLaneIndex} existing={FormatConnectorLanes(m_ExistingConnectorLanes)} reason={reason}; preserveExistingDirection=True continueReverse=True.");
                        }
                        else
                        {
                            request.Mappings = mappings;
                            request.TargetLanes = selectedTargets.ToArray();
                            request.BranchSourceLaneIndex = branchSourceLaneIndex;
                            request.ExtraTargetLaneIndex = extraTargetLaneIndex;
                            request.Turn = turn;
                            MarkForwardRoadPrepared(ref request);
                            forwardMappingReason = mappingReason;
                        }
                    }
                }
            }

            result = new ForwardRoadPreparationResult
            {
                MappingSource = forwardMappingSource,
                MappingReason = forwardMappingReason,
                MappingScore = mappingScore,
                CenterTurnDiagnostic = centerTurnDiagnostic,
                Turn = turn,
                BranchSourceLaneIndex = branchSourceLaneIndex,
                ExtraTargetLaneIndex = extraTargetLaneIndex
            };
        }
    }
}
