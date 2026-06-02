using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private CenterRewritePlan BuildCenterRewritePlan(Request request)
        {
            CenterRewritePlan plan = new CenterRewritePlan
            {
                LeftHandTraffic = m_CityConfigurationSystem.leftHandTraffic,
                BigTurn = m_CityConfigurationSystem.leftHandTraffic ? TurnDirection.Right : TurnDirection.Left,
                SmallTurn = m_CityConfigurationSystem.leftHandTraffic ? TurnDirection.Left : TurnDirection.Right
            };

            if (request.IntersectionNode == Entity.Null ||
                !EntityManager.Exists(request.IntersectionNode) ||
                EntityManager.HasComponent<Deleted>(request.IntersectionNode))
            {
                plan.Diagnostics.Add($"centerRewriteSkipped=missingCenter centerNode={FormatEntity(request.IntersectionNode)}");
                LogCenterRewritePlan(request, plan);
                return plan;
            }

            if (!EntityManager.TryGetBuffer(request.IntersectionNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                plan.Diagnostics.Add($"centerRewriteSkipped=noSubLaneBuffer centerNode={FormatEntity(request.IntersectionNode)}");
                LogCenterRewritePlan(request, plan);
                return plan;
            }

            List<ConnectorLane> centerRoadConnectors = new List<ConnectorLane>(subLanes.Length);
            List<ConnectorLane> centerAllConnectors = new List<ConnectorLane>(subLanes.Length);
            CollectCenterConnectorLanes(request.IntersectionNode, subLanes, centerRoadConnectors, roadOnly: true);
            CollectCenterConnectorLanes(request.IntersectionNode, subLanes, centerAllConnectors, roadOnly: false);
            plan.CenterConnectors = centerRoadConnectors.Count;
            if (centerRoadConnectors.Count == 0)
            {
                plan.Diagnostics.Add($"centerRewriteSkipped=noRoadConnectors centerNode={FormatEntity(request.IntersectionNode)}");
                LogCenterRewritePlan(request, plan);
                return plan;
            }

            Dictionary<Entity, List<ConnectorLane>> bySourceEdge = new Dictionary<Entity, List<ConnectorLane>>();
            Dictionary<Entity, List<ConnectorLane>> allBySourceEdge = new Dictionary<Entity, List<ConnectorLane>>();
            List<Entity> sourceEdges = new List<Entity>(8);
            for (int i = 0; i < centerRoadConnectors.Count; i++)
            {
                ConnectorLane connector = centerRoadConnectors[i];
                if (!bySourceEdge.TryGetValue(connector.SourceEdge, out List<ConnectorLane> approach))
                {
                    approach = new List<ConnectorLane>(8);
                    bySourceEdge.Add(connector.SourceEdge, approach);
                    sourceEdges.Add(connector.SourceEdge);
                }

                approach.Add(connector);
            }

            for (int i = 0; i < centerAllConnectors.Count; i++)
            {
                ConnectorLane connector = centerAllConnectors[i];
                if (!allBySourceEdge.TryGetValue(connector.SourceEdge, out List<ConnectorLane> approach))
                {
                    approach = new List<ConnectorLane>(8);
                    allBySourceEdge.Add(connector.SourceEdge, approach);
                }

                approach.Add(connector);
            }

            sourceEdges.Sort((a, b) => a.Index.CompareTo(b.Index));
            for (int i = 0; i < sourceEdges.Count; i++)
            {
                Entity sourceEdge = sourceEdges[i];
                allBySourceEdge.TryGetValue(sourceEdge, out List<ConnectorLane> allApproachConnectors);
                if (sourceEdge == request.PocketEdge)
                {
                    BuildCenterApproachRewritePlan(
                        request,
                        plan,
                        request.IntersectionNode,
                        sourceEdge,
                        bySourceEdge[sourceEdge],
                        allApproachConnectors ?? bySourceEdge[sourceEdge]);
                    continue;
                }

                plan.OffScopeApproaches++;
                CenterRewritePlan legacyPlan = CreateCenterRewriteSubPlan(plan);
                BuildCenterApproachRewritePlan(
                    request,
                    legacyPlan,
                    request.IntersectionNode,
                    sourceEdge,
                    bySourceEdge[sourceEdge],
                    allApproachConnectors ?? bySourceEdge[sourceEdge],
                    requirePocketExtraLane: false);
                if (legacyPlan.BySource.Count > 0)
                {
                    foreach (SourceLaneKey sourceKey in legacyPlan.BySource.Keys)
                    {
                        plan.LegacyOffScopeSourceKeys.Add(sourceKey);
                    }

                    plan.Diagnostics.Add($"centerRewriteSkipped=offPocketPotentialLegacyCleanup sourceEdge={FormatEntity(sourceEdge)} sourceKeys={FormatSourceLaneKeys(legacyPlan.BySource.Keys)}");
                }
            }

            LogCenterRewritePlan(request, plan);
            return plan;
        }

        private static CenterRewritePlan CreateCenterRewriteSubPlan(CenterRewritePlan parent)
        {
            return new CenterRewritePlan
            {
                LeftHandTraffic = parent.LeftHandTraffic,
                BigTurn = parent.BigTurn,
                SmallTurn = parent.SmallTurn
            };
        }

        private void BuildCenterApproachRewritePlan(
            Request request,
            CenterRewritePlan plan,
            Entity centerNode,
            Entity sourceEdge,
            IReadOnlyList<ConnectorLane> approachConnectors,
            IReadOnlyList<ConnectorLane> allApproachConnectors,
            bool requirePocketExtraLane = true)
        {
            plan.ApproachesScanned++;

            List<LaneEndpoint> sourceEndpoints = new List<LaneEndpoint>(8);
            CollectEdgeCarLaneEndpoints(sourceEdge, centerNode, EndpointRole.SourceEndAtNode, sourceEndpoints);
            SortLaneEndpointsByLateral(sourceEndpoints);
            if (sourceEndpoints.Count == 0)
            {
                AddCenterApproachSkip(plan, sourceEdge, "noSourceEndpoints", approachConnectors, "<none>");
                return;
            }

            Dictionary<int, CenterLaneMovementSummary> summaries = new Dictionary<int, CenterLaneMovementSummary>();
            for (int i = 0; i < sourceEndpoints.Count; i++)
            {
                summaries[sourceEndpoints[i].LaneIndex] = new CenterLaneMovementSummary(sourceEndpoints[i]);
            }

            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache = new Dictionary<Entity, List<LaneEndpoint>>();
            int relevantEndpointMisses = 0;
            int bigTurnConnections = 0;
            for (int i = 0; i < approachConnectors.Count; i++)
            {
                ConnectorLane connector = approachConnectors[i];
                CenterRewriteMovement movement = ClassifyCenterRewriteMovement(
                    centerNode,
                    connector.SourceEdge,
                    connector.TargetEdge,
                    connector.CarFlags,
                    plan.BigTurn,
                    plan.SmallTurn);

                if (!summaries.TryGetValue(connector.SourceLaneIndex, out CenterLaneMovementSummary summary))
                {
                    if (movement == CenterRewriteMovement.Straight ||
                        movement == CenterRewriteMovement.SmallTurn ||
                        movement == CenterRewriteMovement.BigTurn)
                    {
                        relevantEndpointMisses++;
                    }

                    continue;
                }

                LaneEndpoint targetEndpoint = default;
                bool hasTargetEndpoint = false;
                if (movement == CenterRewriteMovement.Straight ||
                    movement == CenterRewriteMovement.SmallTurn ||
                    movement == CenterRewriteMovement.BigTurn)
                {
                    hasTargetEndpoint = TryFindCenterTargetEndpoint(
                        centerNode,
                        connector.TargetEdge,
                        connector.TargetLaneIndex,
                        targetEndpointCache,
                        out targetEndpoint);
                    if (!hasTargetEndpoint)
                    {
                        relevantEndpointMisses++;
                    }
                }

                if (movement == CenterRewriteMovement.BigTurn)
                {
                    bigTurnConnections++;
                }

                summary.Add(new CenterConnectorCandidate(
                    connector,
                    movement,
                    summary.SourceEndpoint,
                    targetEndpoint,
                    hasTargetEndpoint));
            }

            string sourceClass = FormatCenterSourceMovementSummaries(summaries);
            if (relevantEndpointMisses > 0)
            {
                AddCenterApproachSkip(plan, sourceEdge, $"endpointMissing count={relevantEndpointMisses}", approachConnectors, sourceClass);
                return;
            }

            if (bigTurnConnections == 0)
            {
                AddCenterApproachSkip(plan, sourceEdge, "noBigTurn", approachConnectors, sourceClass);
                return;
            }

            plan.BigTurnApproaches++;

            List<CenterLaneMovementSummary> smallExclusive = new List<CenterLaneMovementSummary>(2);
            List<CenterLaneMovementSummary> bigStraight = new List<CenterLaneMovementSummary>(2);
            List<CenterLaneMovementSummary> bigExclusive = new List<CenterLaneMovementSummary>(2);
            List<CenterLaneMovementSummary> smallStraight = new List<CenterLaneMovementSummary>(2);
            foreach (CenterLaneMovementSummary summary in summaries.Values)
            {
                if (summary.IsSmallTurnExclusive)
                {
                    smallExclusive.Add(summary);
                }

                if (summary.IsBigTurnAndStraight)
                {
                    bigStraight.Add(summary);
                }

                if (summary.IsBigTurnExclusive)
                {
                    bigExclusive.Add(summary);
                }

                if (summary.IsSmallTurnAndStraight)
                {
                    smallStraight.Add(summary);
                }
            }

            int pocketExtraCenterLane = -1;
            bool activePocketScope = requirePocketExtraLane && sourceEdge == request.PocketEdge;
            if (activePocketScope &&
                !TryGetPocketExtraCenterLaneIndex(request, out pocketExtraCenterLane))
            {
                AddCenterApproachSkip(plan, sourceEdge, "pocketExtraCenterLaneUnknown", approachConnectors, sourceClass);
                return;
            }

            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> approachBySource = new Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>>();
            Dictionary<SourceLaneKey, LaneEndpoint> approachSourceEndpoints = new Dictionary<SourceLaneKey, LaneEndpoint>();
            Dictionary<TargetLaneKey, LaneEndpoint> approachTargetEndpoints = new Dictionary<TargetLaneKey, LaneEndpoint>();

            CenterLaneMovementSummary smallLane = null;
            CenterLaneMovementSummary middleLane = null;
            CenterLaneMovementSummary bigLane = null;
            CenterConnectorCandidate smallLaneStraightTemplate = default;
            CenterConnectorCandidate middleLaneStraightTemplate = default;
            LaneEndpoint smallLaneStraightTarget = default;
            LaneEndpoint middleLaneStraightTarget = default;
            string rewriteMode = "twoLaneShift";
            string shiftDetail = string.Empty;
            int straightMappingsWritten = 0;
            int straightUnsafeCleared = 0;
            int smallTurnsClearedFromStraightLane = 0;

            if (smallExclusive.Count == 1 && bigStraight.Count == 1)
            {
                smallLane = smallExclusive[0];
                bigLane = bigStraight[0];
                if (activePocketScope && smallLane.SourceEndpoint.LaneIndex != pocketExtraCenterLane)
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"smallTurnNotPocketExtra expectedCenterLane={pocketExtraCenterLane} actual={smallLane.SourceEndpoint.LaneIndex}", approachConnectors, sourceClass);
                    return;
                }

                if (bigLane.Straight.Count != 1)
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"ambiguousBigStraightConnections count={bigLane.Straight.Count}", approachConnectors, sourceClass);
                    return;
                }

                if (smallStraight.Count > 1)
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"ambiguousSmallTurnStraightLane count={smallStraight.Count}", approachConnectors, sourceClass);
                    return;
                }

                if (smallStraight.Count == 1)
                {
                    middleLane = smallStraight[0];
                    if (middleLane.Straight.Count != 1)
                    {
                        AddCenterApproachSkip(plan, sourceEdge, $"ambiguousMiddleStraightConnections count={middleLane.Straight.Count}", approachConnectors, sourceClass);
                        return;
                    }

                    CenterConnectorCandidate middleCurrentStraight = middleLane.Straight[0];
                    CenterConnectorCandidate bigCurrentStraight = bigLane.Straight[0];
                    if (!TryResolveCascadeStraightTargets(
                            centerNode,
                            sourceEndpoints,
                            smallLane,
                            middleLane,
                            bigLane,
                            middleCurrentStraight,
                            bigCurrentStraight,
                            targetEndpointCache,
                            out smallLaneStraightTarget,
                            out middleLaneStraightTarget,
                            out shiftDetail))
                    {
                        AddCenterApproachSkip(plan, sourceEdge, $"straightTargetCascadeFailed detail=({shiftDetail})", approachConnectors, sourceClass);
                        return;
                    }

                    smallLaneStraightTemplate = middleCurrentStraight;
                    middleLaneStraightTemplate = bigCurrentStraight;
                    rewriteMode = "cascadeThreeLane";
                    straightMappingsWritten = 2;
                    smallTurnsClearedFromStraightLane = middleLane.SmallTurn.Count;
                }
                else
                {
                    CenterConnectorCandidate bigCurrentStraight = bigLane.Straight[0];
                    if (!TryResolveShiftedStraightTarget(
                            centerNode,
                            smallLane.SourceEndpoint,
                            bigLane.SourceEndpoint,
                            bigCurrentStraight,
                            targetEndpointCache,
                            out smallLaneStraightTarget,
                            out shiftDetail))
                    {
                        AddCenterApproachSkip(plan, sourceEdge, $"straightTargetShiftFailed detail=({shiftDetail})", approachConnectors, sourceClass);
                        return;
                    }

                    smallLaneStraightTemplate = bigCurrentStraight;
                    straightMappingsWritten = 1;
                }
            }
            else if (activePocketScope &&
                     smallExclusive.Count == 0 &&
                     bigStraight.Count == 0 &&
                     bigExclusive.Count == 1 &&
                     smallStraight.Count == 2)
            {
                if (!TrySelectPocketExtraAndMiddleSmallStraightLane(
                        smallStraight,
                        pocketExtraCenterLane,
                        out smallLane,
                        out middleLane,
                        out string selectDetail))
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"smallStraightConflictSelectFailed detail=({selectDetail})", approachConnectors, sourceClass);
                    return;
                }

                bigLane = bigExclusive[0];
                if (smallLane.Straight.Count != 1 ||
                    middleLane.Straight.Count != 1)
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"ambiguousSmallStraightConflictStraightCounts small={smallLane.Straight.Count} middle={middleLane.Straight.Count}", approachConnectors, sourceClass);
                    return;
                }

                CenterConnectorCandidate smallCurrentStraight = smallLane.Straight[0];
                CenterConnectorCandidate middleCurrentStraight = middleLane.Straight[0];
                if (!TryResolveSmallStraightConflictTargets(
                        centerNode,
                        sourceEndpoints,
                        smallLane,
                        middleLane,
                        bigLane,
                        smallCurrentStraight,
                        middleCurrentStraight,
                        targetEndpointCache,
                        out smallLaneStraightTarget,
                        out middleLaneStraightTarget,
                        out shiftDetail))
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"smallStraightConflictTargetFailed detail=({shiftDetail})", approachConnectors, sourceClass);
                    return;
                }

                smallLaneStraightTemplate = smallCurrentStraight;
                middleLaneStraightTemplate = middleCurrentStraight;
                rewriteMode = "repairSmallStraightConflict";
                straightMappingsWritten = 2;
                smallTurnsClearedFromStraightLane = middleLane.SmallTurn.Count;
            }
            else
            {
                string reason;
                if (smallExclusive.Count != 1)
                {
                    reason = smallExclusive.Count == 0
                        ? (bigExclusive.Count > 0 && smallStraight.Count > 0 ? "alreadyBigTurnExclusiveOrPartialRewrite" : "noSmallTurnExclusive")
                        : $"ambiguousSmallTurnExclusive count={smallExclusive.Count}";
                }
                else if (bigStraight.Count != 1)
                {
                    reason = bigStraight.Count == 0
                        ? (bigExclusive.Count > 0 ? "alreadyBigTurnExclusive" : "noBigTurnStraightLane")
                        : $"ambiguousBigTurnStraightLane count={bigStraight.Count}";
                }
                else
                {
                    reason = "unsupportedCenterRewritePattern";
                }

                AddCenterApproachSkip(plan, sourceEdge, reason, approachConnectors, sourceClass);
                return;
            }

            for (int i = 0; i < smallLane.SmallTurn.Count; i++)
            {
                if (!TryAddCenterCandidateMapping(
                        approachBySource,
                        approachSourceEndpoints,
                        approachTargetEndpoints,
                        smallLane.SmallTurn[i],
                        smallLane.SourceEndpoint,
                        smallLane.SmallTurn[i].TargetEndpoint,
                        preserveUnsafe: true,
                        forceSafeStraight: false,
                        out string reason))
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"smallTurnMappingFailed detail=({reason})", approachConnectors, sourceClass);
                    return;
                }
            }

            if (!TryAddCenterShiftedStraightMapping(
                    approachBySource,
                    approachSourceEndpoints,
                    approachTargetEndpoints,
                    smallLaneStraightTemplate,
                    smallLane.SourceEndpoint,
                    smallLaneStraightTarget,
                    out string straightReason))
            {
                AddCenterApproachSkip(plan, sourceEdge, $"straightMappingFailed detail=({straightReason})", approachConnectors, sourceClass);
                return;
            }

            if (middleLane != null)
            {
                if (!TryAddCenterShiftedStraightMapping(
                        approachBySource,
                        approachSourceEndpoints,
                        approachTargetEndpoints,
                        middleLaneStraightTemplate,
                        middleLane.SourceEndpoint,
                        middleLaneStraightTarget,
                        out string middleStraightReason))
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"middleStraightMappingFailed detail=({middleStraightReason})", approachConnectors, sourceClass);
                    return;
                }
            }

            for (int i = 0; i < bigLane.BigTurn.Count; i++)
            {
                if (!TryAddCenterCandidateMapping(
                        approachBySource,
                        approachSourceEndpoints,
                        approachTargetEndpoints,
                        bigLane.BigTurn[i],
                        bigLane.SourceEndpoint,
                        bigLane.BigTurn[i].TargetEndpoint,
                        preserveUnsafe: true,
                        forceSafeStraight: false,
                        out string reason))
                {
                    AddCenterApproachSkip(plan, sourceEdge, $"bigTurnMappingFailed detail=({reason})", approachConnectors, sourceClass);
                    return;
                }
            }

            int roadBicycleConnections = CountRoadBicycleMappings(approachBySource);
            CenterPreservationStats preservationStats = AddCenterRuntimePreservationMappings(
                centerNode,
                plan,
                allApproachConnectors,
                approachBySource,
                approachSourceEndpoints,
                approachTargetEndpoints,
                targetEndpointCache);

            int connectionCount = CountTrafficPlanConnections(approachBySource);
            if (connectionCount == 0)
            {
                AddCenterApproachSkip(plan, sourceEdge, "noWritableConnections", approachConnectors, sourceClass);
                return;
            }

            MergeCenterApproachPlan(plan, approachBySource, approachSourceEndpoints, approachTargetEndpoints);
            plan.ApproachesRewritten++;
            plan.PlannedConnections += connectionCount;
            plan.StraightConnectionsWrittenSafe += straightMappingsWritten;
            if ((smallLaneStraightTemplate.Connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0)
            {
                straightUnsafeCleared++;
            }

            if (middleLane != null &&
                (middleLaneStraightTemplate.Connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0)
            {
                straightUnsafeCleared++;
            }

            plan.StraightUnsafeCleared += straightUnsafeCleared;
            plan.SmallTurnConnectionsClearedFromStraightLane += smallTurnsClearedFromStraightLane;
            plan.PreservedRuntimeConnections += preservationStats.Connections;
            plan.PreservedUturnConnections += preservationStats.UturnConnections;
            plan.PreservedNonRoadConnections += preservationStats.NonRoadConnections;
            plan.PreservedUnsafeConnections += preservationStats.UnsafeConnections;
            plan.PreservationSkipped += preservationStats.Skipped;
            plan.BicycleConnectionsWrittenWithRoad += roadBicycleConnections;
            string extraEvidence = sourceEdge == request.PocketEdge
                ? $"pocketExtraCenterLane={pocketExtraCenterLane}"
                : "extraEvidence=legacyOffScopeRuntimePatternOnly";
            string middleEvidence = middleLane != null
                ? $" middleLane={middleLane.SourceEndpoint.LaneIndex} clearedMiddleSmallTurn={smallTurnsClearedFromStraightLane}"
                : string.Empty;
            plan.Diagnostics.Add($"centerRewritePlanned sourceEdge={FormatEntity(sourceEdge)} mode={rewriteMode} smallLane={smallLane.SourceEndpoint.LaneIndex}{middleEvidence} bigLane={bigLane.SourceEndpoint.LaneIndex} {extraEvidence} bigTurn={plan.BigTurn} smallTurn={plan.SmallTurn} shift=({shiftDetail}) sourceClass=({sourceClass}) connections={connectionCount} straightSafe={straightMappingsWritten} straightUnsafeCleared={straightUnsafeCleared} roadBicycle={roadBicycleConnections} runtimePreserved={preservationStats.Connections} preservedUturn={preservationStats.UturnConnections} preservedNonRoad={preservationStats.NonRoadConnections} preservedUnsafe={preservationStats.UnsafeConnections} preservationSkipped={preservationStats.Skipped}");
        }

        private CenterRewriteMovement ClassifyCenterRewriteMovement(
            Entity centerNode,
            Entity sourceEdge,
            Entity targetEdge,
            CarLaneFlags flags,
            TurnDirection bigTurn,
            TurnDirection smallTurn)
        {
            if (sourceEdge == targetEdge ||
                (flags & (CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight)) != 0)
            {
                return CenterRewriteMovement.Uturn;
            }

            bool left = (flags & (CarLaneFlags.TurnLeft | CarLaneFlags.GentleTurnLeft)) != 0;
            bool right = (flags & (CarLaneFlags.TurnRight | CarLaneFlags.GentleTurnRight)) != 0;
            if (left != right)
            {
                return TurnToCenterMovement(left ? TurnDirection.Left : TurnDirection.Right, bigTurn, smallTurn);
            }

            if ((flags & CarLaneFlags.Forward) != 0)
            {
                return CenterRewriteMovement.Straight;
            }

            if (!TryClassifyCenterMovementByGeometry(centerNode, sourceEdge, targetEdge, bigTurn, smallTurn, out CenterRewriteMovement movement))
            {
                return CenterRewriteMovement.Ambiguous;
            }

            return movement;
        }

        private bool TryClassifyCenterMovementByGeometry(
            Entity centerNode,
            Entity sourceEdge,
            Entity targetEdge,
            TurnDirection bigTurn,
            TurnDirection smallTurn,
            out CenterRewriteMovement movement)
        {
            movement = CenterRewriteMovement.Ambiguous;
            if (!NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, sourceEdge, centerNode, out float2 sourceOutward) ||
                !NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, targetEdge, centerNode, out float2 targetOutward))
            {
                return false;
            }

            float2 incoming = -sourceOutward;
            float cross = NetTopologyHelpers.Cross(incoming, targetOutward);
            float dot = math.dot(incoming, targetOutward);
            if (math.abs(cross) < 0.25f)
            {
                if (dot > 0.25f)
                {
                    movement = CenterRewriteMovement.Straight;
                    return true;
                }

                movement = CenterRewriteMovement.Uturn;
                return true;
            }

            movement = TurnToCenterMovement(cross > 0f ? TurnDirection.Left : TurnDirection.Right, bigTurn, smallTurn);
            return movement != CenterRewriteMovement.Ambiguous;
        }

        private static CenterRewriteMovement TurnToCenterMovement(
            TurnDirection turn,
            TurnDirection bigTurn,
            TurnDirection smallTurn)
        {
            if (turn == bigTurn)
            {
                return CenterRewriteMovement.BigTurn;
            }

            if (turn == smallTurn)
            {
                return CenterRewriteMovement.SmallTurn;
            }

            return CenterRewriteMovement.Ambiguous;
        }

        private bool TryGetPocketExtraCenterLaneIndex(Request request, out int centerLaneIndex)
        {
            centerLaneIndex = -1;
            if (request.ExtraTargetLaneIndex < 0 ||
                request.TargetLanes == null)
            {
                return false;
            }

            for (int i = 0; i < request.TargetLanes.Length; i++)
            {
                LaneEndpoint target = request.TargetLanes[i];
                if (target.LaneIndex == request.ExtraTargetLaneIndex)
                {
                    centerLaneIndex = target.OppositeLaneIndex;
                    return centerLaneIndex >= 0;
                }
            }

            return false;
        }
    }
}
