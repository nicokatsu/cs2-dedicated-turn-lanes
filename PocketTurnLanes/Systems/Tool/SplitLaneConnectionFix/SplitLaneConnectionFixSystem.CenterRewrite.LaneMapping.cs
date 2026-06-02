using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
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

        private bool TryAddCenterCandidateMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            CenterConnectorCandidate candidate,
            LaneEndpoint sourceEndpoint,
            LaneEndpoint targetEndpoint,
            bool preserveUnsafe,
            bool forceSafeStraight,
            out string reason)
        {
            reason = string.Empty;
            if (!candidate.HasTargetEndpoint)
            {
                reason = $"missing target endpoint target={FormatEntity(candidate.Connector.TargetEdge)}:{candidate.Connector.TargetLaneIndex}";
                return false;
            }

            PathMethod method = GetCenterRoadRewriteMethod(
                candidate.Connector.PathMethods,
                sourceEndpoint,
                targetEndpoint);
            if ((method & PathMethod.Road) == 0)
            {
                reason = $"road method unavailable source={FormatEntity(sourceEndpoint.Edge)}:{sourceEndpoint.LaneIndex} target={FormatEntity(targetEndpoint.Edge)}:{targetEndpoint.LaneIndex}";
                return false;
            }

            bool unsafeConnection = preserveUnsafe &&
                                    !forceSafeStraight &&
                                    (candidate.Connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0;
            LaneMapping mapping = new LaneMapping
            {
                SourceEdge = sourceEndpoint.Edge,
                TargetEdge = targetEndpoint.Edge,
                SourceLaneIndex = sourceEndpoint.LaneIndex,
                TargetLaneIndex = targetEndpoint.LaneIndex,
                TrafficLanePositionMap = new float3x2(sourceEndpoint.LanePosition, targetEndpoint.LanePosition),
                TrafficCarriagewayAndGroupIndexMap = new int4(sourceEndpoint.CarriagewayAndGroup, targetEndpoint.CarriagewayAndGroup),
                Method = method,
                TemplateEntity = candidate.Connector.Entity,
                TemplatePathMethods = candidate.Connector.PathMethods,
                IsUnsafe = unsafeConnection,
                HasTrafficMaps = true
            };
            TrafficMappingPlanMerge.AddOrMergeCenterRewrite(bySource, mapping);
            sourceEndpoints[new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex)] = sourceEndpoint;
            targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = targetEndpoint;
            return true;
        }

        private bool TryAddCenterShiftedStraightMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            CenterConnectorCandidate straightCandidate,
            LaneEndpoint smallSourceEndpoint,
            LaneEndpoint shiftedTargetEndpoint,
            out string reason)
        {
            reason = string.Empty;
            PathMethod method = GetCenterRoadRewriteMethod(
                straightCandidate.Connector.PathMethods,
                smallSourceEndpoint,
                shiftedTargetEndpoint);
            if ((method & PathMethod.Road) == 0)
            {
                reason = $"road method unavailable source={FormatEntity(smallSourceEndpoint.Edge)}:{smallSourceEndpoint.LaneIndex} target={FormatEntity(shiftedTargetEndpoint.Edge)}:{shiftedTargetEndpoint.LaneIndex}";
                return false;
            }

            LaneMapping mapping = new LaneMapping
            {
                SourceEdge = smallSourceEndpoint.Edge,
                TargetEdge = shiftedTargetEndpoint.Edge,
                SourceLaneIndex = smallSourceEndpoint.LaneIndex,
                TargetLaneIndex = shiftedTargetEndpoint.LaneIndex,
                TrafficLanePositionMap = new float3x2(smallSourceEndpoint.LanePosition, shiftedTargetEndpoint.LanePosition),
                TrafficCarriagewayAndGroupIndexMap = new int4(smallSourceEndpoint.CarriagewayAndGroup, shiftedTargetEndpoint.CarriagewayAndGroup),
                Method = method,
                TemplateEntity = straightCandidate.Connector.Entity,
                TemplatePathMethods = straightCandidate.Connector.PathMethods,
                IsUnsafe = false,
                HasTrafficMaps = true
            };
            TrafficMappingPlanMerge.AddOrMergeCenterRewrite(bySource, mapping);
            sourceEndpoints[new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex)] = smallSourceEndpoint;
            targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = shiftedTargetEndpoint;
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
            shiftedTarget = default;
            detail = string.Empty;

            if (!straight.HasTargetEndpoint)
            {
                detail = "straight target endpoint missing";
                return false;
            }

            if (math.abs(smallSource.Lateral - bigSource.Lateral) <= 0.0001f)
            {
                detail = $"source lateral tie small={smallSource.Lateral:0.###} big={bigSource.Lateral:0.###}";
                return false;
            }

            if (!TryGetCenterTargetEndpoints(centerNode, straight.Connector.TargetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(straight.Connector.TargetEdge)}";
                return false;
            }

            int originalTargetOrder = -1;
            for (int i = 0; i < targetEndpoints.Count; i++)
            {
                if (targetEndpoints[i].LaneIndex == straight.Connector.TargetLaneIndex)
                {
                    originalTargetOrder = i;
                    break;
                }
            }

            if (originalTargetOrder < 0)
            {
                detail = $"original straight target missing lane={straight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            int shift = smallSource.Lateral > bigSource.Lateral ? -1 : 1;
            int shiftedOrder = originalTargetOrder + shift;
            if (shiftedOrder < 0 || shiftedOrder >= targetEndpoints.Count)
            {
                detail = $"adjacent target unavailable originalOrder={originalTargetOrder} shift={shift} targetCount={targetEndpoints.Count} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            LaneEndpoint originalTarget = targetEndpoints[originalTargetOrder];
            shiftedTarget = targetEndpoints[shiftedOrder];
            float targetDelta = shiftedTarget.Lateral - originalTarget.Lateral;
            if (math.abs(targetDelta) <= 0.0001f ||
                targetDelta > 0f != shift > 0)
            {
                detail = $"target lateral not ordered original={originalTarget.Lateral:0.###} shifted={shiftedTarget.Lateral:0.###} shift={shift}";
                return false;
            }

            detail = $"sourceLane {bigSource.LaneIndex}->{smallSource.LaneIndex} targetLane {originalTarget.LaneIndex}->{shiftedTarget.LaneIndex} targetEdge={FormatEntity(straight.Connector.TargetEdge)} order {originalTargetOrder}->{shiftedOrder} shift={shift}";
            return true;
        }

        private static bool TrySelectPocketExtraAndMiddleSmallStraightLane(
            IReadOnlyList<CenterLaneMovementSummary> smallStraight,
            int pocketExtraCenterLane,
            out CenterLaneMovementSummary pocketExtraLane,
            out CenterLaneMovementSummary middleLane,
            out string detail)
        {
            pocketExtraLane = null;
            middleLane = null;
            detail = string.Empty;
            if (smallStraight == null || smallStraight.Count != 2)
            {
                detail = $"smallStraightCount={smallStraight?.Count ?? 0}";
                return false;
            }

            for (int i = 0; i < smallStraight.Count; i++)
            {
                CenterLaneMovementSummary candidate = smallStraight[i];
                if (candidate.SourceEndpoint.LaneIndex == pocketExtraCenterLane)
                {
                    if (pocketExtraLane != null)
                    {
                        detail = $"duplicatePocketExtra lane={pocketExtraCenterLane}";
                        return false;
                    }

                    pocketExtraLane = candidate;
                }
                else
                {
                    middleLane = candidate;
                }
            }

            if (pocketExtraLane == null || middleLane == null)
            {
                detail = $"pocketExtraMissing expected={pocketExtraCenterLane}";
                return false;
            }

            detail = $"pocketExtra={pocketExtraLane.SourceEndpoint.LaneIndex} middle={middleLane.SourceEndpoint.LaneIndex}";
            return true;
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
            smallLaneStraightTarget = default;
            middleLaneStraightTarget = default;
            detail = string.Empty;

            if (!middleCurrentStraight.HasTargetEndpoint ||
                !bigCurrentStraight.HasTargetEndpoint)
            {
                detail = $"straight endpoint missing middle={middleCurrentStraight.HasTargetEndpoint} big={bigCurrentStraight.HasTargetEndpoint}";
                return false;
            }

            if (!TryValidateThreeLaneSourceCascade(
                    sourceEndpoints,
                    smallLane.SourceEndpoint,
                    middleLane.SourceEndpoint,
                    bigLane.SourceEndpoint,
                    out string sourceDetail))
            {
                detail = sourceDetail;
                return false;
            }

            if (middleCurrentStraight.Connector.TargetEdge != bigCurrentStraight.Connector.TargetEdge)
            {
                detail = $"straight target edge mismatch middle={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} big={FormatEntity(bigCurrentStraight.Connector.TargetEdge)} source=({sourceDetail})";
                return false;
            }

            if (!TryGetCenterTargetEndpoints(centerNode, bigCurrentStraight.Connector.TargetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(bigCurrentStraight.Connector.TargetEdge)}";
                return false;
            }

            int middleTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
            int bigTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, bigCurrentStraight.Connector.TargetLaneIndex);
            if (middleTargetOrder < 0 || bigTargetOrder < 0)
            {
                detail = $"straight target order missing middleLane={middleCurrentStraight.Connector.TargetLaneIndex} bigLane={bigCurrentStraight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            int expectedTargetShift = smallLane.SourceEndpoint.Lateral > bigLane.SourceEndpoint.Lateral ? -1 : 1;
            if (middleTargetOrder != bigTargetOrder + expectedTargetShift)
            {
                detail = $"straight target not adjacent middleOrder={middleTargetOrder} bigOrder={bigTargetOrder} expectedShift={expectedTargetShift} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                return false;
            }

            smallLaneStraightTarget = middleCurrentStraight.TargetEndpoint;
            middleLaneStraightTarget = bigCurrentStraight.TargetEndpoint;
            detail = $"source=({sourceDetail}) straightCascade smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(bigCurrentStraight.Connector.TargetEdge)} targetOrders middle={middleTargetOrder} big={bigTargetOrder} expectedShift={expectedTargetShift}";
            return true;
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
            smallLaneStraightTarget = default;
            middleLaneStraightTarget = default;
            detail = string.Empty;

            if (!smallCurrentStraight.HasTargetEndpoint ||
                !middleCurrentStraight.HasTargetEndpoint)
            {
                detail = $"straight endpoint missing small={smallCurrentStraight.HasTargetEndpoint} middle={middleCurrentStraight.HasTargetEndpoint}";
                return false;
            }

            if (!TryValidateThreeLaneSourceCascade(
                    sourceEndpoints,
                    smallLane.SourceEndpoint,
                    middleLane.SourceEndpoint,
                    bigLane.SourceEndpoint,
                    out string sourceDetail))
            {
                detail = sourceDetail;
                return false;
            }

            if (smallCurrentStraight.Connector.TargetEdge != middleCurrentStraight.Connector.TargetEdge)
            {
                detail = $"straight target edge mismatch small={FormatEntity(smallCurrentStraight.Connector.TargetEdge)} middle={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} source=({sourceDetail})";
                return false;
            }

            if (!TryGetCenterTargetEndpoints(centerNode, middleCurrentStraight.Connector.TargetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)}";
                return false;
            }

            int smallTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, smallCurrentStraight.Connector.TargetLaneIndex);
            int middleTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
            if (smallTargetOrder < 0 || middleTargetOrder < 0)
            {
                detail = $"straight target order missing smallLane={smallCurrentStraight.Connector.TargetLaneIndex} middleLane={middleCurrentStraight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            smallLaneStraightTarget = smallCurrentStraight.TargetEndpoint;
            int smallSideTargetShift = smallLane.SourceEndpoint.Lateral > bigLane.SourceEndpoint.Lateral ? -1 : 1;
            if (smallCurrentStraight.Connector.TargetLaneIndex == middleCurrentStraight.Connector.TargetLaneIndex)
            {
                int shiftedMiddleOrder = smallTargetOrder - smallSideTargetShift;
                if (shiftedMiddleOrder < 0 || shiftedMiddleOrder >= targetEndpoints.Count)
                {
                    detail = $"duplicate straight target cannot shift middle smallTargetOrder={smallTargetOrder} shift={-smallSideTargetShift} targetCount={targetEndpoints.Count} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                    return false;
                }

                middleLaneStraightTarget = targetEndpoints[shiftedMiddleOrder];
                detail = $"source=({sourceDetail}) duplicateStraightTargetShifted smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} targetOrders small={smallTargetOrder} middle={shiftedMiddleOrder} smallSideShift={smallSideTargetShift}";
                return true;
            }

            if (smallTargetOrder != middleTargetOrder + smallSideTargetShift)
            {
                detail = $"straight targets not in shifted order smallOrder={smallTargetOrder} middleOrder={middleTargetOrder} expectedSmallShift={smallSideTargetShift} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                return false;
            }

            middleLaneStraightTarget = middleCurrentStraight.TargetEndpoint;
            detail = $"source=({sourceDetail}) distinctStraightTargets smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} targetOrders small={smallTargetOrder} middle={middleTargetOrder} smallSideShift={smallSideTargetShift}";
            return true;
        }

        private static bool TryValidateThreeLaneSourceCascade(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            LaneEndpoint smallSource,
            LaneEndpoint middleSource,
            LaneEndpoint bigSource,
            out string detail)
        {
            detail = string.Empty;
            int smallOrder = TrafficLaneEndpointHelpers.FindOrder(sourceEndpoints, smallSource.LaneIndex);
            int middleOrder = TrafficLaneEndpointHelpers.FindOrder(sourceEndpoints, middleSource.LaneIndex);
            int bigOrder = TrafficLaneEndpointHelpers.FindOrder(sourceEndpoints, bigSource.LaneIndex);
            if (smallOrder < 0 || middleOrder < 0 || bigOrder < 0)
            {
                detail = $"source order missing small={smallSource.LaneIndex}:{smallOrder} middle={middleSource.LaneIndex}:{middleOrder} big={bigSource.LaneIndex}:{bigOrder}";
                return false;
            }

            if (smallOrder == bigOrder)
            {
                detail = $"source order tie small={smallOrder} big={bigOrder}";
                return false;
            }

            int direction = bigOrder > smallOrder ? 1 : -1;
            if (middleOrder != smallOrder + direction ||
                bigOrder != middleOrder + direction)
            {
                detail = $"source lanes not adjacent smallOrder={smallOrder} middleOrder={middleOrder} bigOrder={bigOrder} direction={direction}";
                return false;
            }

            detail = $"small={smallSource.LaneIndex}@{smallOrder} middle={middleSource.LaneIndex}@{middleOrder} big={bigSource.LaneIndex}@{bigOrder} direction={direction}";
            return true;
        }

        private static PathMethod GetCenterRoadRewriteMethod(
            PathMethod templateMethod,
            LaneEndpoint source,
            LaneEndpoint target)
        {
            PathMethod method = TrafficPathMethods.RestrictTrafficPathMethodToEndpoints(
                PathMethod.Road,
                source,
                target);
            if ((method & PathMethod.Road) == 0)
            {
                return 0;
            }

            method |= templateMethod & PathMethod.Bicycle;
            return TrafficPathMethods.SanitizeCenterTrafficPathMethod(method);
        }

        private static int CountRoadBicycleMappings(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource)
        {
            int count = 0;
            foreach (Dictionary<TargetLaneKey, LaneMapping> byTarget in bySource.Values)
            {
                foreach (LaneMapping mapping in byTarget.Values)
                {
                    if ((mapping.Method & PathMethod.Road) != 0 &&
                        (mapping.Method & PathMethod.Bicycle) != 0)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static int CountTrafficPlanConnections(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource)
        {
            int count = 0;
            foreach (Dictionary<TargetLaneKey, LaneMapping> byTarget in bySource.Values)
            {
                count += byTarget.Count;
            }

            return count;
        }

        private void MergeCenterApproachPlan(
            CenterRewritePlan plan,
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> approachBySource,
            Dictionary<SourceLaneKey, LaneEndpoint> approachSourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> approachTargetEndpoints)
        {
            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> sourcePair in approachBySource)
            {
                foreach (LaneMapping mapping in sourcePair.Value.Values)
                {
                    TrafficMappingPlanMerge.AddOrMergeCenterRewrite(plan.BySource, mapping);
                }
            }

            foreach (KeyValuePair<SourceLaneKey, LaneEndpoint> pair in approachSourceEndpoints)
            {
                plan.SourceEndpoints[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<TargetLaneKey, LaneEndpoint> pair in approachTargetEndpoints)
            {
                plan.TargetEndpoints[pair.Key] = pair.Value;
            }
        }
    }
}
