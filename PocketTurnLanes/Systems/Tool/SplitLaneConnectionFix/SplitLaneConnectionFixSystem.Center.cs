using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private CenterPlan BuildCenterPlan(Request request)
        {
            CenterPlan plan = new CenterPlan
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
                LogCenterPlan(request, plan);
                return plan;
            }

            if (!EntityManager.TryGetBuffer(request.IntersectionNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                plan.Diagnostics.Add($"centerRewriteSkipped=noSubLaneBuffer centerNode={FormatEntity(request.IntersectionNode)}");
                LogCenterPlan(request, plan);
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
                LogCenterPlan(request, plan);
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
                CenterPlan legacyPlan = CreateCenterSubPlan(plan);
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

            LogCenterPlan(request, plan);
            return plan;
        }

        private static CenterPlan CreateCenterSubPlan(CenterPlan parent)
        {
            return new CenterPlan
            {
                LeftHandTraffic = parent.LeftHandTraffic,
                BigTurn = parent.BigTurn,
                SmallTurn = parent.SmallTurn
            };
        }

        private void BuildCenterApproachRewritePlan(
            Request request,
            CenterPlan plan,
            Entity centerNode,
            Entity sourceEdge,
            IReadOnlyList<ConnectorLane> approachConnectors,
            IReadOnlyList<ConnectorLane> allApproachConnectors,
            bool requirePocketExtraLane = true)
        {
            plan.ApproachesScanned++;

            List<LaneEndpoint> sourceEndpoints = new List<LaneEndpoint>(8);
            CollectEdgeCarLaneEndpoints(sourceEdge, centerNode, EndpointRole.SourceEndAtNode, sourceEndpoints);
            TrafficLaneEndpointHelpers.SortByLateral(sourceEndpoints);
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
                CenterMovement movement = ClassifyCenterMovement(
                    centerNode,
                    connector.SourceEdge,
                    connector.TargetEdge,
                    connector.CarFlags,
                    plan.BigTurn,
                    plan.SmallTurn);

                if (!summaries.TryGetValue(connector.SourceLaneIndex, out CenterLaneMovementSummary summary))
                {
                    if (movement == CenterMovement.Straight ||
                        movement == CenterMovement.SmallTurn ||
                        movement == CenterMovement.BigTurn)
                    {
                        relevantEndpointMisses++;
                    }

                    continue;
                }

                LaneEndpoint targetEndpoint = default;
                bool hasTargetEndpoint = false;
                if (movement == CenterMovement.Straight ||
                    movement == CenterMovement.SmallTurn ||
                    movement == CenterMovement.BigTurn)
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

                if (movement == CenterMovement.BigTurn)
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

            if (!TrafficCenterPatternSelector.TrySelect(
                    sourceEndpoints,
                    smallExclusive,
                    bigStraight,
                    bigExclusive,
                    smallStraight,
                    activePocketScope,
                    pocketExtraCenterLane,
                    TryGetTargets,
                    out CenterPatternSelection pattern,
                    out string patternSkipReason))
            {
                AddCenterApproachSkip(plan, sourceEdge, patternSkipReason, approachConnectors, sourceClass);
                return;
            }

            CenterLaneMovementSummary smallLane = pattern.SmallLane;
            CenterLaneMovementSummary middleLane = pattern.MiddleLane;
            CenterLaneMovementSummary bigLane = pattern.BigLane;
            CenterConnectorCandidate smallLaneStraightTemplate = pattern.SmallLaneStraightTemplate;
            CenterConnectorCandidate middleLaneStraightTemplate = pattern.MiddleLaneStraightTemplate;
            LaneEndpoint smallLaneStraightTarget = pattern.SmallLaneStraightTarget;
            LaneEndpoint middleLaneStraightTarget = pattern.MiddleLaneStraightTarget;
            string rewriteMode = pattern.RewriteMode;
            string shiftDetail = pattern.ShiftDetail;
            int straightMappingsWritten = pattern.StraightMappingsWritten;
            int straightUnsafeCleared = 0;
            int smallTurnsClearedFromStraightLane = pattern.SmallTurnsClearedFromStraightLane;

            for (int i = 0; i < smallLane.SmallTurn.Count; i++)
            {
                if (!TrafficCenterMappingBuilder.TryAddCandidateMapping(
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

            if (!TrafficCenterMappingBuilder.TryAddShiftedStraightMapping(
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
                if (!TrafficCenterMappingBuilder.TryAddShiftedStraightMapping(
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
                if (!TrafficCenterMappingBuilder.TryAddCandidateMapping(
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

            int roadBicycleConnections = TrafficCenterMappingBuilder.CountRoadBicycleMappings(approachBySource);
            CenterPreservationStats preservationStats = AddCenterRuntimePreservationMappings(
                centerNode,
                plan,
                allApproachConnectors,
                approachBySource,
                approachSourceEndpoints,
                approachTargetEndpoints,
                targetEndpointCache);

            int connectionCount = TrafficCenterMappingBuilder.CountTrafficPlanConnections(approachBySource);
            if (connectionCount == 0)
            {
                AddCenterApproachSkip(plan, sourceEdge, "noWritableConnections", approachConnectors, sourceClass);
                return;
            }

            TrafficCenterMappingBuilder.MergeApproachPlan(plan, approachBySource, approachSourceEndpoints, approachTargetEndpoints);
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

        private CenterMovement ClassifyCenterMovement(
            Entity centerNode,
            Entity sourceEdge,
            Entity targetEdge,
            CarLaneFlags flags,
            TurnDirection bigTurn,
            TurnDirection smallTurn)
        {
            TrafficConnectorMovement movement = TrafficConnectorMovementClassifier.ClassifyCenter(
                EntityManager,
                centerNode,
                sourceEdge,
                targetEdge,
                flags);
            return ToCenterMovement(movement, bigTurn, smallTurn);
        }

        private static CenterMovement ToCenterMovement(
            TrafficConnectorMovement movement,
            TurnDirection bigTurn,
            TurnDirection smallTurn)
        {
            switch (movement)
            {
                case TrafficConnectorMovement.Straight:
                    return CenterMovement.Straight;
                case TrafficConnectorMovement.Uturn:
                    return CenterMovement.Uturn;
                case TrafficConnectorMovement.Left:
                    return TurnToCenterMovement(TurnDirection.Left, bigTurn, smallTurn);
                case TrafficConnectorMovement.Right:
                    return TurnToCenterMovement(TurnDirection.Right, bigTurn, smallTurn);
                default:
                    return CenterMovement.Ambiguous;
            }
        }

        private static CenterMovement TurnToCenterMovement(
            TurnDirection turn,
            TurnDirection bigTurn,
            TurnDirection smallTurn)
        {
            if (turn == bigTurn)
            {
                return CenterMovement.BigTurn;
            }

            if (turn == smallTurn)
            {
                return CenterMovement.SmallTurn;
            }

            return CenterMovement.Ambiguous;
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
