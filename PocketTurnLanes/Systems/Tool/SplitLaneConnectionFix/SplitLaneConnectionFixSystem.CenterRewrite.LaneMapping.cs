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
                diagnostics = $"center-node-missing-sublanes intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)}";
                return false;
            }

            if (selectedTargets == null || selectedTargets.Count == 0)
            {
                diagnostics = "no-selected-targets";
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
                if (connectorTurn == TurnDirection.Left)
                {
                    leftCounts[targetListIndex]++;
                }
                else if (connectorTurn == TurnDirection.Right)
                {
                    rightCounts[targetListIndex]++;
                }
                else
                {
                    straightCounts[targetListIndex]++;
                }

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
                diagnostics = $"{diagnostics}; {selectionDiagnostic}";
                return false;
            }

            diagnostics = $"{diagnostics}; {selectionDiagnostic}";
            return true;
        }

        private bool TryResolveShiftedStraightTarget(
            Entity centerNode,
            LaneEndpoint smallSource,
            LaneEndpoint bigSource,
            CenterConnectorCandidate straight,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint shiftedTarget,
            out string detail)
        {
            bool TryGetTargets(Entity targetEdge, out IReadOnlyList<LaneEndpoint> targetEndpoints)
            {
                if (TryGetCenterTargetEndpoints(centerNode, targetEdge, targetEndpointCache, out List<LaneEndpoint> endpoints))
                {
                    targetEndpoints = endpoints;
                    return true;
                }

                targetEndpoints = null;
                return false;
            }

            return TrafficCenterStraightTargetResolver.TryResolveShiftedStraightTarget(
                smallSource,
                bigSource,
                straight,
                TryGetTargets,
                out shiftedTarget,
                out detail);
        }

        private bool TryResolveCascadeStraightTargets(
            Entity centerNode,
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary middleLane,
            CenterLaneMovementSummary bigLane,
            CenterConnectorCandidate middleCurrentStraight,
            CenterConnectorCandidate bigCurrentStraight,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint smallLaneStraightTarget,
            out LaneEndpoint middleLaneStraightTarget,
            out string detail)
        {
            bool TryGetTargets(Entity targetEdge, out IReadOnlyList<LaneEndpoint> targetEndpoints)
            {
                if (TryGetCenterTargetEndpoints(centerNode, targetEdge, targetEndpointCache, out List<LaneEndpoint> endpoints))
                {
                    targetEndpoints = endpoints;
                    return true;
                }

                targetEndpoints = null;
                return false;
            }

            return TrafficCenterStraightTargetResolver.TryResolveCascadeStraightTargets(
                sourceEndpoints,
                smallLane,
                middleLane,
                bigLane,
                middleCurrentStraight,
                bigCurrentStraight,
                TryGetTargets,
                out smallLaneStraightTarget,
                out middleLaneStraightTarget,
                out detail);
        }

        private bool TryResolveSmallStraightConflictTargets(
            Entity centerNode,
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary middleLane,
            CenterLaneMovementSummary bigLane,
            CenterConnectorCandidate smallCurrentStraight,
            CenterConnectorCandidate middleCurrentStraight,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint smallLaneStraightTarget,
            out LaneEndpoint middleLaneStraightTarget,
            out string detail)
        {
            bool TryGetTargets(Entity targetEdge, out IReadOnlyList<LaneEndpoint> targetEndpoints)
            {
                if (TryGetCenterTargetEndpoints(centerNode, targetEdge, targetEndpointCache, out List<LaneEndpoint> endpoints))
                {
                    targetEndpoints = endpoints;
                    return true;
                }

                targetEndpoints = null;
                return false;
            }

            return TrafficCenterStraightTargetResolver.TryResolveSmallStraightConflictTargets(
                sourceEndpoints,
                smallLane,
                middleLane,
                bigLane,
                smallCurrentStraight,
                middleCurrentStraight,
                TryGetTargets,
                out smallLaneStraightTarget,
                out middleLaneStraightTarget,
                out detail);
        }

    }
}
