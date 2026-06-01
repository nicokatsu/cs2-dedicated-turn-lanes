using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using PocketTurnLanes.Tool;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetEdge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private enum CenterRewriteMovement
        {
            Ambiguous,
            Straight,
            SmallTurn,
            BigTurn,
            Uturn
        }

        private sealed class CenterRewritePlan
        {
            public readonly Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> BySource = new Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>>();
            public readonly Dictionary<SourceLaneKey, LaneEndpoint> SourceEndpoints = new Dictionary<SourceLaneKey, LaneEndpoint>();
            public readonly Dictionary<TargetLaneKey, LaneEndpoint> TargetEndpoints = new Dictionary<TargetLaneKey, LaneEndpoint>();
            public readonly HashSet<SourceLaneKey> LegacyOffScopeSourceKeys = new HashSet<SourceLaneKey>();
            public readonly List<string> Diagnostics = new List<string>(16);
            public int ApproachesScanned;
            public int ApproachesRewritten;
            public int ApproachesSkipped;
            public int OffScopeApproaches;
            public int CenterConnectors;
            public int BigTurnApproaches;
            public int PlannedConnections;
            public int StraightConnectionsWrittenSafe;
            public int StraightUnsafeCleared;
            public int SmallTurnConnectionsClearedFromStraightLane;
            public int PreservedRuntimeConnections;
            public int PreservedSnapshotConnections;
            public int PreservedUturnConnections;
            public int PreservedNonRoadConnections;
            public int PreservedUnsafeConnections;
            public int PreservationSkipped;
            public int BicycleConnectionsWrittenWithRoad;
            public bool LeftHandTraffic;
            public TurnDirection BigTurn;
            public TurnDirection SmallTurn;
        }

        private struct CenterPreservationStats
        {
            public int Connections;
            public int UturnConnections;
            public int NonRoadConnections;
            public int UnsafeConnections;
            public int Skipped;
        }

        private readonly struct CenterConnectorCandidate
        {
            public readonly ConnectorLane Connector;
            public readonly CenterRewriteMovement Movement;
            public readonly LaneEndpoint SourceEndpoint;
            public readonly LaneEndpoint TargetEndpoint;
            public readonly bool HasTargetEndpoint;

            public CenterConnectorCandidate(
                ConnectorLane connector,
                CenterRewriteMovement movement,
                LaneEndpoint sourceEndpoint,
                LaneEndpoint targetEndpoint,
                bool hasTargetEndpoint)
            {
                Connector = connector;
                Movement = movement;
                SourceEndpoint = sourceEndpoint;
                TargetEndpoint = targetEndpoint;
                HasTargetEndpoint = hasTargetEndpoint;
            }
        }

        private sealed class CenterLaneMovementSummary
        {
            public readonly LaneEndpoint SourceEndpoint;
            public readonly List<CenterConnectorCandidate> Straight = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> SmallTurn = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> BigTurn = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> Uturn = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> Other = new List<CenterConnectorCandidate>(2);

            public CenterLaneMovementSummary(LaneEndpoint sourceEndpoint)
            {
                SourceEndpoint = sourceEndpoint;
            }

            public bool IsSmallTurnExclusive =>
                SmallTurn.Count > 0 &&
                Straight.Count == 0 &&
                BigTurn.Count == 0 &&
                Other.Count == 0;

            public bool IsBigTurnAndStraight =>
                BigTurn.Count > 0 &&
                Straight.Count > 0 &&
                SmallTurn.Count == 0 &&
                Other.Count == 0;

            public bool IsBigTurnExclusive =>
                BigTurn.Count > 0 &&
                Straight.Count == 0 &&
                SmallTurn.Count == 0 &&
                Other.Count == 0;

            public bool IsSmallTurnAndStraight =>
                SmallTurn.Count > 0 &&
                Straight.Count > 0 &&
                BigTurn.Count == 0 &&
                Other.Count == 0;

            public void Add(CenterConnectorCandidate candidate)
            {
                switch (candidate.Movement)
                {
                    case CenterRewriteMovement.Straight:
                        Straight.Add(candidate);
                        break;
                    case CenterRewriteMovement.SmallTurn:
                        SmallTurn.Add(candidate);
                        break;
                    case CenterRewriteMovement.BigTurn:
                        BigTurn.Add(candidate);
                        break;
                    case CenterRewriteMovement.Uturn:
                        Uturn.Add(candidate);
                        break;
                    default:
                        Other.Add(candidate);
                        break;
                }
            }
        }

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

        private void CollectCenterConnectorLanes(
            Entity centerNode,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output,
            bool roadOnly)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                PathMethod pathMethods = subLane.m_PathMethods;
                if ((roadOnly ? (pathMethods & PathMethod.Road) == 0 : pathMethods == 0) ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    (roadOnly && !EntityManager.HasComponent<NetCarLane>(laneEntity)) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, centerNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    sourceEdge == Entity.Null ||
                    targetEdge == Entity.Null ||
                    !IsEdgeConnectedToNode(sourceEdge, centerNode) ||
                    !IsEdgeConnectedToNode(targetEdge, centerNode))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
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
            AddOrMergeCenterTrafficMapping(bySource, mapping);
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
            AddOrMergeCenterTrafficMapping(bySource, mapping);
            sourceEndpoints[new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex)] = smallSourceEndpoint;
            targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = shiftedTargetEndpoint;
            return true;
        }

        private CenterPreservationStats AddCenterRuntimePreservationMappings(
            Entity centerNode,
            CenterRewritePlan plan,
            IReadOnlyList<ConnectorLane> allApproachConnectors,
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            Dictionary<Entity, List<LaneEndpoint>> roadTargetEndpointCache)
        {
            CenterPreservationStats stats = default;
            if (allApproachConnectors == null || allApproachConnectors.Count == 0)
            {
                return stats;
            }

            Dictionary<Entity, List<LaneEndpoint>> preservationTargetEndpointCache = new Dictionary<Entity, List<LaneEndpoint>>();
            for (int i = 0; i < allApproachConnectors.Count; i++)
            {
                ConnectorLane connector = allApproachConnectors[i];
                SourceLaneKey sourceKey = new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex);
                if (!bySource.ContainsKey(sourceKey))
                {
                    continue;
                }

                CenterRewriteMovement movement = ClassifyCenterRewriteMovement(
                    centerNode,
                    connector.SourceEdge,
                    connector.TargetEdge,
                    connector.CarFlags,
                    plan.BigTurn,
                    plan.SmallTurn);
                PathMethod preservedMethod = GetCenterPreservedConnectorMethod(connector.PathMethods, movement);
                if (preservedMethod == 0)
                {
                    continue;
                }

                if (!sourceEndpoints.TryGetValue(sourceKey, out LaneEndpoint sourceEndpoint))
                {
                    stats.Skipped++;
                    continue;
                }

                LaneEndpoint targetEndpoint;
                if (!TryFindCenterPreservationTargetEndpoint(
                        centerNode,
                        connector.TargetEdge,
                        connector.TargetLaneIndex,
                        roadTargetEndpointCache,
                        preservationTargetEndpointCache,
                        out targetEndpoint))
                {
                    stats.Skipped++;
                    continue;
                }

                bool unsafeConnection = (connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0;
                LaneMapping mapping = new LaneMapping
                {
                    SourceEdge = sourceEndpoint.Edge,
                    TargetEdge = connector.TargetEdge,
                    SourceLaneIndex = sourceEndpoint.LaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    TrafficLanePositionMap = new float3x2(sourceEndpoint.LanePosition, targetEndpoint.LanePosition),
                    TrafficCarriagewayAndGroupIndexMap = new int4(sourceEndpoint.CarriagewayAndGroup, targetEndpoint.CarriagewayAndGroup),
                    Method = preservedMethod,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods,
                    IsTrackPreservation = true,
                    IsUnsafe = unsafeConnection,
                    HasTrafficMaps = true
                };
                AddOrMergeCenterTrafficMapping(bySource, mapping);
                targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = targetEndpoint;
                stats.Connections++;

                if (movement == CenterRewriteMovement.Uturn || connector.SourceEdge == connector.TargetEdge)
                {
                    stats.UturnConnections++;
                }

                if ((preservedMethod & ~PathMethod.Road) != 0)
                {
                    stats.NonRoadConnections++;
                }

                if (unsafeConnection)
                {
                    stats.UnsafeConnections++;
                }
            }

            return stats;
        }

        private static PathMethod GetCenterRoadRewriteMethod(
            PathMethod templateMethod,
            LaneEndpoint source,
            LaneEndpoint target)
        {
            PathMethod method = RestrictTrafficPathMethodToEndpoints(
                PathMethod.Road,
                source,
                target);
            if ((method & PathMethod.Road) == 0)
            {
                return 0;
            }

            method |= templateMethod & PathMethod.Bicycle;
            return SanitizeCenterTrafficPathMethod(method);
        }

        private static PathMethod GetCenterPreservedConnectorMethod(PathMethod method, CenterRewriteMovement movement)
        {
            if (method == 0)
            {
                return 0;
            }

            if (movement == CenterRewriteMovement.Uturn)
            {
                return SanitizeCenterTrafficPathMethod(method);
            }

            PathMethod preserved = method & PathMethod.Track;
            if ((method & PathMethod.Road) == 0)
            {
                preserved |= method & ~PathMethod.Road;
            }

            return SanitizeCenterTrafficPathMethod(preserved);
        }

        private static PathMethod SanitizeCenterTrafficPathMethod(PathMethod method)
        {
            return method;
        }

        private static PathMethod RestrictCenterTrafficPathMethodToEndpoints(
            PathMethod method,
            LaneEndpoint source,
            LaneEndpoint target)
        {
            PathMethod roadAndTrack = RestrictTrafficPathMethodToEndpoints(
                method & (PathMethod.Road | PathMethod.Track),
                source,
                target);
            PathMethod otherMethods = method & ~(PathMethod.Road | PathMethod.Track);
            return SanitizeCenterTrafficPathMethod(roadAndTrack | otherMethods);
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

        private static void AddOrMergeCenterTrafficMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping)
        {
            SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
            TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
            if (!bySource.TryGetValue(sourceKey, out Dictionary<TargetLaneKey, LaneMapping> byTarget))
            {
                byTarget = new Dictionary<TargetLaneKey, LaneMapping>();
                bySource.Add(sourceKey, byTarget);
            }

            mapping.Method = SanitizeCenterTrafficPathMethod(mapping.Method);
            if (mapping.Method == 0)
            {
                return;
            }

            if (byTarget.TryGetValue(targetKey, out LaneMapping existing))
            {
                existing.Method = SanitizeCenterTrafficPathMethod(existing.Method | mapping.Method);
                existing.IsBranch |= mapping.IsBranch;
                existing.IsTrackPreservation &= mapping.IsTrackPreservation;
                existing.IsUnsafe |= mapping.IsUnsafe;
                if (!existing.HasTrafficMaps && mapping.HasTrafficMaps)
                {
                    existing.TrafficLanePositionMap = mapping.TrafficLanePositionMap;
                    existing.TrafficCarriagewayAndGroupIndexMap = mapping.TrafficCarriagewayAndGroupIndexMap;
                    existing.HasTrafficMaps = true;
                }

                byTarget[targetKey] = existing;
                return;
            }

            byTarget.Add(targetKey, mapping);
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

            int middleTargetOrder = FindLaneEndpointOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
            int bigTargetOrder = FindLaneEndpointOrder(targetEndpoints, bigCurrentStraight.Connector.TargetLaneIndex);
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

            int smallTargetOrder = FindLaneEndpointOrder(targetEndpoints, smallCurrentStraight.Connector.TargetLaneIndex);
            int middleTargetOrder = FindLaneEndpointOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
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
            int smallOrder = FindLaneEndpointOrder(sourceEndpoints, smallSource.LaneIndex);
            int middleOrder = FindLaneEndpointOrder(sourceEndpoints, middleSource.LaneIndex);
            int bigOrder = FindLaneEndpointOrder(sourceEndpoints, bigSource.LaneIndex);
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

        private static int FindLaneEndpointOrder(IReadOnlyList<LaneEndpoint> lanes, int laneIndex)
        {
            if (lanes == null)
            {
                return -1;
            }

            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i].LaneIndex == laneIndex)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool TryFindCenterTargetEndpoint(
            Entity centerNode,
            Entity targetEdge,
            int laneIndex,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint targetEndpoint)
        {
            targetEndpoint = default;
            if (!TryGetCenterTargetEndpoints(centerNode, targetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                return false;
            }

            return TryFindLaneEndpoint(targetEndpoints, laneIndex, out targetEndpoint);
        }

        private bool TryFindCenterPreservationTargetEndpoint(
            Entity centerNode,
            Entity targetEdge,
            int laneIndex,
            Dictionary<Entity, List<LaneEndpoint>> roadTargetEndpointCache,
            Dictionary<Entity, List<LaneEndpoint>> preservationTargetEndpointCache,
            out LaneEndpoint targetEndpoint)
        {
            if (TryFindCenterTargetEndpoint(
                    centerNode,
                    targetEdge,
                    laneIndex,
                    roadTargetEndpointCache,
                    out targetEndpoint))
            {
                return true;
            }

            if (!preservationTargetEndpointCache.TryGetValue(targetEdge, out List<LaneEndpoint> targetEndpoints))
            {
                targetEndpoints = new List<LaneEndpoint>(8);
                CollectEdgeCenterPreservationLaneEndpoints(
                    targetEdge,
                    centerNode,
                    EndpointRole.TargetStartAtNode,
                    targetEndpoints);
                SortLaneEndpointsByLateral(targetEndpoints);
                preservationTargetEndpointCache.Add(targetEdge, targetEndpoints);
            }

            return TryFindLaneEndpoint(targetEndpoints, laneIndex, out targetEndpoint);
        }

        private bool TryGetCenterTargetEndpoints(
            Entity centerNode,
            Entity targetEdge,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out List<LaneEndpoint> targetEndpoints)
        {
            if (!targetEndpointCache.TryGetValue(targetEdge, out targetEndpoints))
            {
                targetEndpoints = new List<LaneEndpoint>(8);
                CollectEdgeCarLaneEndpoints(targetEdge, centerNode, EndpointRole.TargetStartAtNode, targetEndpoints);
                SortLaneEndpointsByLateral(targetEndpoints);
                targetEndpointCache.Add(targetEdge, targetEndpoints);
            }

            return targetEndpoints.Count > 0;
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

        private bool TryWriteCenterRewriteMappings(
            TrafficApi trafficApi,
            Request request,
            CenterRewritePlan plan,
            out bool wrote)
        {
            wrote = false;
            if (plan == null ||
                plan.BySource.Count == 0 && plan.LegacyOffScopeSourceKeys.Count == 0)
            {
                return true;
            }

            object modifiedBuffer = plan.BySource.Count == 0 &&
                                    !trafficApi.HasModifiedLaneConnectionsBuffer(EntityManager, request.IntersectionNode)
                ? null
                : trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, request.IntersectionNode);
            if (modifiedBuffer == null && plan.BySource.Count == 0)
            {
                return true;
            }

            if (modifiedBuffer == null)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=modifiedBufferUnavailable.");
                return false;
            }

            CenterPreservationStats snapshotPreservation = CopyExistingCenterPreservedGeneratedConnections(
                trafficApi,
                plan,
                modifiedBuffer);
            if (snapshotPreservation.Connections > 0 || snapshotPreservation.Skipped > 0)
            {
                plan.PreservedSnapshotConnections += snapshotPreservation.Connections;
                plan.PreservedUturnConnections += snapshotPreservation.UturnConnections;
                plan.PreservedNonRoadConnections += snapshotPreservation.NonRoadConnections;
                plan.PreservedUnsafeConnections += snapshotPreservation.UnsafeConnections;
                plan.PreservationSkipped += snapshotPreservation.Skipped;
                plan.PlannedConnections = CountTrafficPlanConnections(plan.BySource);
            }

            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> pair in plan.BySource)
            {
                if (!plan.SourceEndpoints.TryGetValue(pair.Key, out LaneEndpoint sourceEndpoint))
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=sourceEndpointMissing source={FormatEntity(pair.Key.Edge)}:{pair.Key.LaneIndex}.");
                    return false;
                }

                foreach (LaneMapping mapping in pair.Value.Values)
                {
                    TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
                    if (!plan.TargetEndpoints.TryGetValue(targetKey, out LaneEndpoint targetEndpoint))
                    {
                        if (!mapping.IsTrackPreservation || !mapping.HasTrafficMaps)
                        {
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=targetEndpointMissing mapping={FormatMapping(mapping)}.");
                            return false;
                        }

                        continue;
                    }

                    if (RestrictCenterTrafficPathMethodToEndpoints(mapping.Method, sourceEndpoint, targetEndpoint) == 0)
                    {
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=methodUnavailable mapping={FormatMapping(mapping)}.");
                        return false;
                    }
                }
            }

            List<object> kept = new List<object>(trafficApi.GetBufferLength(modifiedBuffer));
            int removedExisting = 0;
            int removedLegacyOffScope = 0;
            int originalLength = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < originalLength; i++)
            {
                object existing = trafficApi.GetBufferItem(modifiedBuffer, i);
                SourceLaneKey existingKey = new SourceLaneKey(
                    trafficApi.GetModifiedConnectionEdge(existing),
                    trafficApi.GetModifiedConnectionLaneIndex(existing));
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                bool rewriteSource = plan.BySource.ContainsKey(existingKey);
                bool legacyOffScopeSource = !rewriteSource &&
                                            plan.LegacyOffScopeSourceKeys.Contains(existingKey) &&
                                            LooksLikeLegacyCenterRewriteOverride(trafficApi, existing, existingKey);
                if (!rewriteSource && !legacyOffScopeSource)
                {
                    kept.Add(existing);
                    continue;
                }

                removedExisting++;
                if (legacyOffScopeSource)
                {
                    removedLegacyOffScope++;
                }

                if (modifiedEntity != Entity.Null && EntityManager.Exists(modifiedEntity))
                {
                    AddMarkerIfMissing<Deleted>(modifiedEntity);
                }
            }

            trafficApi.ClearBuffer(modifiedBuffer);
            for (int i = 0; i < kept.Count; i++)
            {
                trafficApi.AddBufferElement(modifiedBuffer, kept[i]);
            }

            int writtenSources = 0;
            int writtenConnections = 0;
            int writtenUnsafeConnections = 0;
            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> pair in plan.BySource)
            {
                LaneEndpoint sourceEndpoint = plan.SourceEndpoints[pair.Key];
                Entity modifiedConnectionEntity = EntityManager.CreateEntity();
                trafficApi.AddDataOwner(EntityManager, modifiedConnectionEntity, request.IntersectionNode);
                trafficApi.AddFakePrefabRef(EntityManager, modifiedConnectionEntity);
                object generatedBuffer = trafficApi.AddGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity);

                foreach (LaneMapping mapping in pair.Value.Values)
                {
                    TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
                    bool hasTargetEndpoint = plan.TargetEndpoints.TryGetValue(targetKey, out LaneEndpoint targetEndpoint);
                    PathMethod method = hasTargetEndpoint
                        ? RestrictCenterTrafficPathMethodToEndpoints(
                            SanitizeCenterTrafficPathMethod(mapping.Method),
                            sourceEndpoint,
                            targetEndpoint)
                        : SanitizeCenterTrafficPathMethod(mapping.Method);
                    if (method == 0)
                    {
                        continue;
                    }

                    if (!mapping.HasTrafficMaps && !hasTargetEndpoint)
                    {
                        continue;
                    }

                    trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                        mapping.SourceEdge,
                        mapping.TargetEdge,
                        mapping.SourceLaneIndex,
                        mapping.TargetLaneIndex,
                        mapping.HasTrafficMaps
                            ? mapping.TrafficLanePositionMap
                            : new float3x2(sourceEndpoint.LanePosition, targetEndpoint.LanePosition),
                        mapping.HasTrafficMaps
                            ? mapping.TrafficCarriagewayAndGroupIndexMap
                            : new int4(sourceEndpoint.CarriagewayAndGroup, targetEndpoint.CarriagewayAndGroup),
                        method,
                        mapping.IsUnsafe));
                    writtenConnections++;
                    if (mapping.IsUnsafe)
                    {
                        writtenUnsafeConnections++;
                    }
                }

                trafficApi.AddBufferElement(modifiedBuffer, trafficApi.CreateModifiedLaneConnection(
                    pair.Key.LaneIndex,
                    sourceEndpoint.CarriagewayAndGroup,
                    sourceEndpoint.LanePosition,
                    pair.Key.Edge,
                    modifiedConnectionEntity));
                writtenSources++;
            }

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, request.IntersectionNode);
            wrote = writtenSources > 0 || removedExisting > 0;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Center Traffic rewrite write counts centerNode={FormatEntity(request.IntersectionNode)} pocketEdge={FormatEntity(request.PocketEdge)} leftHandTraffic={plan.LeftHandTraffic} bigTurn={plan.BigTurn} smallTurn={plan.SmallTurn} trafficWriteOrder=centerFirstOuterSecond removedExisting={removedExisting} removedLegacyOffScope={removedLegacyOffScope} preservedExisting={kept.Count} writtenSources={writtenSources} expectedSources={plan.BySource.Count} writtenConnections={writtenConnections} plannedConnections={plan.PlannedConnections} writtenUnsafeConnections={writtenUnsafeConnections} straightConnectionsSafe={plan.StraightConnectionsWrittenSafe} straightUnsafeCleared={plan.StraightUnsafeCleared} smallTurnClearedFromStraightLane={plan.SmallTurnConnectionsClearedFromStraightLane} roadBicycle={plan.BicycleConnectionsWrittenWithRoad} runtimePreserved={plan.PreservedRuntimeConnections} snapshotPreserved={plan.PreservedSnapshotConnections} preservedUturn={plan.PreservedUturnConnections} preservedNonRoad={plan.PreservedNonRoadConnections} preservedUnsafe={plan.PreservedUnsafeConnections} preservationSkipped={plan.PreservationSkipped} legacyOffScopeSourceKeys={FormatSourceLaneKeys(plan.LegacyOffScopeSourceKeys)} diagnostics={FormatStringList(plan.Diagnostics)}.");
            return writtenSources == plan.BySource.Count &&
                   writtenConnections == plan.PlannedConnections;
        }

        private CenterPreservationStats CopyExistingCenterPreservedGeneratedConnections(
            TrafficApi trafficApi,
            CenterRewritePlan plan,
            object modifiedBuffer)
        {
            CenterPreservationStats stats = default;
            if (trafficApi == null ||
                plan == null ||
                modifiedBuffer == null ||
                plan.BySource.Count == 0)
            {
                return stats;
            }

            int length = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < length; i++)
            {
                object modified = trafficApi.GetBufferItem(modifiedBuffer, i);
                SourceLaneKey modifiedKey = new SourceLaneKey(
                    trafficApi.GetModifiedConnectionEdge(modified),
                    trafficApi.GetModifiedConnectionLaneIndex(modified));
                if (!plan.BySource.ContainsKey(modifiedKey))
                {
                    continue;
                }

                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(modified);
                if (modifiedEntity == Entity.Null ||
                    !EntityManager.Exists(modifiedEntity) ||
                    !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
                {
                    stats.Skipped++;
                    continue;
                }

                object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
                int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
                for (int generatedIndex = 0; generatedIndex < generatedLength; generatedIndex++)
                {
                    object generated = trafficApi.GetBufferItem(generatedBuffer, generatedIndex);
                    Entity sourceEdge = trafficApi.GetGeneratedConnectionSource(generated);
                    Entity targetEdge = trafficApi.GetGeneratedConnectionTarget(generated);
                    int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
                    SourceLaneKey sourceKey = new SourceLaneKey(sourceEdge, laneIndexMap.x & 0xff);
                    if (!plan.BySource.ContainsKey(sourceKey))
                    {
                        continue;
                    }

                    PathMethod originalMethod = trafficApi.GetGeneratedConnectionMethod(generated);
                    bool isUturn = sourceEdge == targetEdge;
                    PathMethod preservedMethod = isUturn
                        ? SanitizeCenterTrafficPathMethod(originalMethod)
                        : GetCenterPreservedGeneratedMethod(originalMethod);
                    if (preservedMethod == 0)
                    {
                        continue;
                    }

                    LaneMapping mapping = new LaneMapping
                    {
                        SourceEdge = sourceEdge,
                        TargetEdge = targetEdge,
                        SourceLaneIndex = laneIndexMap.x & 0xff,
                        TargetLaneIndex = laneIndexMap.y & 0xff,
                        TrafficLanePositionMap = trafficApi.GetGeneratedConnectionLanePositionMap(generated),
                        TrafficCarriagewayAndGroupIndexMap = trafficApi.GetGeneratedConnectionCarriagewayAndGroupIndexMap(generated),
                        Method = preservedMethod,
                        IsBranch = false,
                        IsTrackPreservation = true,
                        IsUnsafe = trafficApi.GetGeneratedConnectionUnsafe(generated),
                        HasTrafficMaps = true
                    };
                    AddOrMergeCenterTrafficMapping(plan.BySource, mapping);
                    stats.Connections++;
                    if (isUturn)
                    {
                        stats.UturnConnections++;
                    }

                    if ((preservedMethod & ~PathMethod.Road) != 0)
                    {
                        stats.NonRoadConnections++;
                    }

                    if (mapping.IsUnsafe)
                    {
                        stats.UnsafeConnections++;
                    }
                }
            }

            return stats;
        }

        private static PathMethod GetCenterPreservedGeneratedMethod(PathMethod method)
        {
            PathMethod preserved = method & PathMethod.Track;
            if ((method & PathMethod.Road) == 0)
            {
                preserved |= method & ~PathMethod.Road;
            }

            return SanitizeCenterTrafficPathMethod(preserved);
        }

        private bool LooksLikeLegacyCenterRewriteOverride(
            TrafficApi trafficApi,
            object modifiedConnection,
            SourceLaneKey sourceKey)
        {
            Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(modifiedConnection);
            if (modifiedEntity == Entity.Null ||
                !EntityManager.Exists(modifiedEntity) ||
                !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
            {
                return false;
            }

            object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
            int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
            if (generatedLength <= 0 || generatedLength > 4)
            {
                return false;
            }

            for (int i = 0; i < generatedLength; i++)
            {
                object generated = trafficApi.GetBufferItem(generatedBuffer, i);
                Entity generatedSource = trafficApi.GetGeneratedConnectionSource(generated);
                int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
                PathMethod method = SanitizeCenterTrafficPathMethod(trafficApi.GetGeneratedConnectionMethod(generated));
                if (generatedSource != sourceKey.Edge ||
                    (laneIndexMap.x & 0xff) != sourceKey.LaneIndex ||
                    (method & ~PathMethod.Road) != 0 ||
                    trafficApi.GetGeneratedConnectionUnsafe(generated))
                {
                    return false;
                }
            }

            return true;
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
                    AddOrMergeCenterTrafficMapping(plan.BySource, mapping);
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

        private void AddCenterApproachSkip(
            CenterRewritePlan plan,
            Entity sourceEdge,
            string reason,
            IReadOnlyList<ConnectorLane> approachConnectors,
            string sourceClass)
        {
            plan.ApproachesSkipped++;
            plan.Diagnostics.Add($"centerRewriteSkipped={reason} sourceEdge={FormatEntity(sourceEdge)} connectors={FormatConnectorLanes(approachConnectors)} sourceClass=({sourceClass})");
        }

        private void LogCenterRewritePlan(Request request, CenterRewritePlan plan)
        {
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Built center Traffic rewrite plan centerNode={FormatEntity(request.IntersectionNode)} splitNode={FormatEntity(request.SplitNode)} pocketEdge={FormatEntity(request.PocketEdge)} leftHandTraffic={plan.LeftHandTraffic} bigTurn={plan.BigTurn} smallTurn={plan.SmallTurn} trafficWriteOrder=centerFirstOuterSecond scope=pocketApproachOnly connectorCount={plan.CenterConnectors} approachesScanned={plan.ApproachesScanned} offScopeApproaches={plan.OffScopeApproaches} approachesWithBigTurn={plan.BigTurnApproaches} approachesRewritten={plan.ApproachesRewritten} approachesSkipped={plan.ApproachesSkipped} sourcePlans={plan.BySource.Count} plannedConnections={plan.PlannedConnections} straightConnectionsSafe={plan.StraightConnectionsWrittenSafe} straightUnsafeCleared={plan.StraightUnsafeCleared} smallTurnClearedFromStraightLane={plan.SmallTurnConnectionsClearedFromStraightLane} roadBicycle={plan.BicycleConnectionsWrittenWithRoad} runtimePreserved={plan.PreservedRuntimeConnections} snapshotPreserved={plan.PreservedSnapshotConnections} preservedUturn={plan.PreservedUturnConnections} preservedNonRoad={plan.PreservedNonRoadConnections} preservedUnsafe={plan.PreservedUnsafeConnections} preservationSkipped={plan.PreservationSkipped} legacyOffScopeSourceKeys={FormatSourceLaneKeys(plan.LegacyOffScopeSourceKeys)} diagnostics={FormatStringList(plan.Diagnostics)}.");
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

        private static void SortLaneEndpointsByLateral(List<LaneEndpoint> lanes)
        {
            lanes.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));
        }

        private bool IsEdgeConnectedToNode(Entity edgeEntity, Entity node)
        {
            return edgeEntity != Entity.Null &&
                   node != Entity.Null &&
                   EntityManager.Exists(edgeEntity) &&
                   !EntityManager.HasComponent<Deleted>(edgeEntity) &&
                   EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) &&
                   (edge.m_Start == node || edge.m_End == node);
        }

        private static string FormatCenterSourceMovementSummaries(
            Dictionary<int, CenterLaneMovementSummary> summaries)
        {
            if (summaries == null || summaries.Count == 0)
            {
                return "<none>";
            }

            List<CenterLaneMovementSummary> ordered = new List<CenterLaneMovementSummary>(summaries.Values);
            ordered.Sort((a, b) => a.SourceEndpoint.Lateral.CompareTo(b.SourceEndpoint.Lateral));
            List<string> values = new List<string>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                CenterLaneMovementSummary summary = ordered[i];
                values.Add($"{summary.SourceEndpoint.LaneIndex}@{summary.SourceEndpoint.Lateral:0.##}:small={summary.SmallTurn.Count},straight={summary.Straight.Count},big={summary.BigTurn.Count},uturn={summary.Uturn.Count},other={summary.Other.Count}");
            }

            return string.Join("|", values);
        }
    }
}
