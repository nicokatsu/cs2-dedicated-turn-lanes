using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.PrefabMatching;
using PocketTurnLanes.Tool.SplitGeometry;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
using static PocketTurnLanes.Tool.SplitGeometry.SplitGeometryMath;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private bool TryBuildSplitDefinitionPlan(
            Entity nodeEntity,
            Entity edgeEntity,
            out SplitDefinitionPlan plan,
            float requestedTargetPocketLength = -1f)
        {
            plan = default;

            if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: missing Edge, Curve, or PrefabRef.");
                return false;
            }

            if (curve.m_Length <= 0.01f)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: curve length is {curve.m_Length:0.###}.");
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            if (!nodeIsStart && !nodeIsEnd)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: node {FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            if (!EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometryData))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: prefab {FormatEntity(prefabRef.m_Prefab)} has no NetGeometryData.");
                return false;
            }

            GetMinMaxSplitPositions(
                curve.m_Length,
                geometryData.m_DefaultWidth,
                geometryData.m_EdgeLengthRange.min,
                out float minSplit,
                out float maxSplit);

            if (minSplit >= maxSplit)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: too short to split safely (length={curve.m_Length:0.##}m).");
                return false;
            }

            plan.IntersectionDistance = GetIntersectionExitDistance(
                nodeEntity,
                edgeEntity,
                edge,
                curve,
                nodeIsStart);
            float adaptiveTargetPocketLength = GetAdaptiveTargetPocketLength(
                nodeEntity,
                edgeEntity,
                prefabRef.m_Prefab,
                nodeIsStart,
                geometryData,
                out string pocketWidthSource,
                out float pocketWidth,
                out float pocketEdgeGeometryWidth,
                out float pocketPrefabWidth,
                out string pocketLaneWidthDetail);
            plan.TargetPocketLength = ResolveTargetPocketLength(
                adaptiveTargetPocketLength,
                requestedTargetPocketLength,
                out float requestedTargetPocketLengthBeforeCap,
                out float maximumRequestedPocketLength,
                out bool retryOverride);
            if (!TryCalculateSafeSplitTargetPlan(
                    curve.m_Bezier,
                    nodeIsStart,
                    minSplit,
                    maxSplit,
                    plan.IntersectionDistance,
                    plan.TargetPocketLength,
                    out SafeSplitTargetPlan targetPlan))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: not enough room for a pocket lane (length={curve.m_Length:0.##}m intersection={plan.IntersectionDistance:0.##}m maxDistanceFromNode={targetPlan.MaxDistanceFromNode:0.##}m availablePocket={targetPlan.MaxDistanceFromNode - plan.IntersectionDistance:0.###}m minPocket={MinimumPocketLaneLength:0.##}m effectiveMinPocket={GetEffectiveMinimumPocketLength():0.###}m tolerance={MinimumPocketLaneLengthTolerance:0.###}m requestedPocket={plan.TargetPocketLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###}).");
                return false;
            }

            Mod.LogDiagnostic($"[IntersectionTool] Split target pocket length node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabNameFromPrefab(prefabRef.m_Prefab)} widthSource={pocketWidthSource} width={FormatMeters(pocketWidth)} edgeGeometryWidth={FormatMeters(pocketEdgeGeometryWidth)} prefabWidth={FormatMeters(pocketPrefabWidth)} laneWidthDetail={pocketLaneWidthDetail} adaptivePocket={adaptiveTargetPocketLength:0.##}m requestedPocket={plan.TargetPocketLength:0.##}m requestedBeforeCap={requestedTargetPocketLengthBeforeCap:0.##}m minPocket={MinimumWidthBasedPocketLaneLength:0.##}m maxPocket={maximumRequestedPocketLength:0.##}m retryOverride={retryOverride} maxDistanceFromNode={targetPlan.MaxDistanceFromNode:0.##}m intersection={plan.IntersectionDistance:0.##}m.");

            plan.TargetDistance = targetPlan.TargetDistance;
            plan.CurvePosition = targetPlan.CurvePosition;
            plan.SplitDistance = targetPlan.SplitDistance;
            plan.PocketDistance = targetPlan.PocketDistance;
            plan.Request = new SplitDefinitionRequest
            {
                Edge = edgeEntity,
                Prefab = prefabRef.m_Prefab,
                HitPosition = targetPlan.HitPosition,
                CurvePosition = plan.CurvePosition,
                RandomSeed = GetDefinitionRandomSeed(edgeEntity)
            };

            return true;
        }

        private bool TryBuildNodeMergeCandidate(
            Entity nodeEntity,
            Entity edgeEntity,
            ReplacementPrefabMatch prefabMatch,
            out NodeMergeCandidate candidate)
        {
            candidate = default;

            if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef) ||
                !EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometryData))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot prepare road-node merge fallback edge={FormatEntity(edgeEntity)}: missing Edge, Curve, PrefabRef, or prefab NetGeometryData.");
                return false;
            }

            bool nodeIsStartOnShortEdge = edge.m_Start == nodeEntity;
            bool nodeIsEndOnShortEdge = edge.m_End == nodeEntity;
            if (!nodeIsStartOnShortEdge && !nodeIsEndOnShortEdge)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot prepare road-node merge fallback edge={FormatEntity(edgeEntity)}: node {FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            if (!TryGetShortEdgeFallbackContext(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    out ShortEdgeFallbackContext fallbackContext))
            {
                return false;
            }

            if (fallbackContext.ContinuationPrefab == prefabRef.m_Prefab)
            {
                return TryBuildSourcePrefabContinuationNodeMergeCandidate(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    curve,
                    prefabRef.m_Prefab,
                    geometryData,
                    prefabMatch,
                    fallbackContext,
                    out candidate);
            }

            if (TryBuildBalancedOppositeTargetNodeMergeCandidate(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    curve,
                    prefabRef.m_Prefab,
                    geometryData,
                    prefabMatch,
                    fallbackContext,
                    out candidate))
            {
                return true;
            }

            return TryBuildShortEdgeReplacementOnlyCandidate(
                nodeEntity,
                edgeEntity,
                edge,
                curve,
                prefabRef.m_Prefab,
                prefabMatch,
                fallbackContext,
                out candidate);
        }

        private bool TryBuildSourcePrefabContinuationNodeMergeCandidate(
            Entity nodeEntity,
            Entity edgeEntity,
            Edge edge,
            Curve curve,
            Entity sourcePrefab,
            NetGeometryData geometryData,
            ReplacementPrefabMatch prefabMatch,
            ShortEdgeFallbackContext fallbackContext,
            out NodeMergeCandidate candidate)
        {
            candidate = default;

            Entity removableNode = fallbackContext.RemovableNode;
            Entity continuationEdge = fallbackContext.ContinuationEdge;
            Edge continuationEdgeData = fallbackContext.ContinuationEdgeData;
            Curve continuationCurve = fallbackContext.ContinuationCurve;
            Entity farNode = fallbackContext.FarNode;

            Entity neighbor1 = edge.m_Start == removableNode ? edge.m_End : edge.m_Start;
            Entity neighbor2 = continuationEdgeData.m_Start == removableNode ? continuationEdgeData.m_End : continuationEdgeData.m_Start;
            bool isForward = edge.m_End == removableNode;
            Bezier4x3 mergedBezier = ComputeMergedBezier(removableNode, edge, curve, continuationEdgeData, continuationCurve);
            Entity startNode = isForward ? neighbor1 : neighbor2;
            Entity endNode = isForward ? neighbor2 : neighbor1;
            if (!isForward)
            {
                mergedBezier = MathUtils.Invert(mergedBezier);
            }

            if (startNode != nodeEntity && endNode != nodeEntity)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)}: merged edge would not connect hovered node {FormatEntity(nodeEntity)} start={FormatEntity(startNode)} end={FormatEntity(endNode)}.");
                return false;
            }

            float mergedLength = MathUtils.Length(mergedBezier);
            if (!TryCalculateMergedSplitGeometry(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    curve,
                    startNode == nodeEntity,
                    mergedBezier,
                    mergedLength,
                    sourcePrefab,
                    geometryData,
                    out float splitPosition,
                    out float splitDistance,
                    out float intersectionDistance,
                    out float pocketDistance,
                    out float targetDistance,
                    out float targetPocketLength,
                    out float3 hitPosition))
            {
                return false;
            }

            bool hasUpgraded = EntityManager.TryGetComponent(edgeEntity, out Upgraded upgraded);

            candidate = new NodeMergeCandidate
            {
                Node = nodeEntity,
                ShortEdge = edgeEntity,
                RemovableNode = removableNode,
                ContinuationEdge = continuationEdge,
                FarNode = farNode,
                SourcePrefab = sourcePrefab,
                TargetPrefab = prefabMatch.Prefab,
                LaneRepairMode = SplitLaneConnectionRepairMode.Standard,
                InvertTarget = prefabMatch.Invert,
                PostMergeInvertTarget = prefabMatch.Invert,
                HasTargetUpgrade = prefabMatch.HasTargetUpgrade,
                TargetUpgrade = prefabMatch.TargetUpgrade,
                ShortEdgeLength = curve.m_Length,
                ContinuationEdgeLength = continuationCurve.m_Length,
                MergedLength = mergedLength,
                ExpectedSplitPosition = splitPosition,
                ExpectedSplitDistance = splitDistance,
                ExpectedIntersectionDistance = intersectionDistance,
                ExpectedPocketDistance = pocketDistance,
                ExpectedTargetDistance = targetDistance,
                ExpectedTargetPocketLength = targetPocketLength,
                ExpectedHitPosition = hitPosition,
                OriginalForwardLanes = prefabMatch.OriginalCounts.Forward,
                OriginalBackwardLanes = prefabMatch.OriginalCounts.Backward,
                TargetForwardLanes = prefabMatch.TargetCounts.Forward,
                TargetBackwardLanes = prefabMatch.TargetCounts.Backward,
                MergeRequest = new NodeMergeDefinitionRequest
                {
                    Prefab = sourcePrefab,
                    RemovedNode = removableNode,
                    StartNode = startNode,
                    EndNode = endNode,
                    MergedCurve = mergedBezier,
                    MergedLength = mergedLength,
                    RandomSeed = GetDefinitionRandomSeed(edgeEntity),
                    HasUpgraded = hasUpgraded,
                    Upgraded = upgraded,
                    FirstDeletion = new EdgeDeletionDefinitionRequest
                    {
                        Edge = edgeEntity,
                        StartNode = edge.m_Start,
                        EndNode = edge.m_End,
                        Curve = curve.m_Bezier,
                        Length = curve.m_Length > 0.01f ? curve.m_Length : MathUtils.Length(curve.m_Bezier)
                    },
                    SecondDeletion = new EdgeDeletionDefinitionRequest
                    {
                        Edge = continuationEdge,
                        StartNode = continuationEdgeData.m_Start,
                        EndNode = continuationEdgeData.m_End,
                        Curve = continuationCurve.m_Bezier,
                        Length = continuationCurve.m_Length > 0.01f ? continuationCurve.m_Length : MathUtils.Length(continuationCurve.m_Bezier)
                    }
                }
            };

            Mod.LogDiagnostic($"[IntersectionTool] Road-node merge fallback accepted shortEdge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)} farNode={FormatEntity(farNode)} sourcePrefab={GetPrefabNameFromPrefab(sourcePrefab)} shortLength={curve.m_Length:0.##}m continuationLength={continuationCurve.m_Length:0.##}m mergedLength={mergedLength:0.##}m split={splitPosition:0.###} splitDistance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m requestedPocket={targetPocketLength:0.##}m target={targetDistance:0.##}m. Safety: removable node has exactly two connected road edges and both prefabs match.");
            return true;
        }

        private bool TryBuildBalancedOppositeTargetNodeMergeCandidate(
            Entity nodeEntity,
            Entity edgeEntity,
            Edge edge,
            Curve curve,
            Entity sourcePrefab,
            NetGeometryData sourceGeometryData,
            ReplacementPrefabMatch prefabMatch,
            ShortEdgeFallbackContext fallbackContext,
            out NodeMergeCandidate candidate)
        {
            candidate = default;

            if (prefabMatch.Prefab == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)}: replacement target prefab is missing.");
                return false;
            }

            Entity removableNode = fallbackContext.RemovableNode;
            Entity continuationEdge = fallbackContext.ContinuationEdge;
            Edge continuationEdgeData = fallbackContext.ContinuationEdgeData;
            Curve continuationCurve = fallbackContext.ContinuationCurve;
            Entity farNode = fallbackContext.FarNode;
            Entity continuationPrefab = fallbackContext.ContinuationPrefab;

            if (continuationPrefab != prefabMatch.Prefab)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)}: continuation prefab does not match target prefab continuationPrefab={GetPrefabNameFromPrefab(continuationPrefab)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)} connectedEdges={fallbackContext.ConnectedEdgeCount}.");
                return false;
            }

            if (!IsValidIntersection(farNode))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} farNode={FormatEntity(farNode)}: far node is not a valid intersection for opposite-side lane repair.");
                return false;
            }

            bool nodeIsStartOnShortEdge = edge.m_Start == nodeEntity;
            bool farNodeIsStartOnContinuation = continuationEdgeData.m_Start == farNode;
            if (!TryContinuationMatchesOppositeTargetLayout(
                    continuationEdge,
                    prefabMatch,
                    nodeIsStartOnShortEdge,
                    farNodeIsStartOnContinuation,
                    out RoadLaneCounts continuationRoadCounts,
                    out string continuationLayoutDetail))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: continuation is not the reverse target layout. {continuationLayoutDetail}");
                return false;
            }

            if (!EntityManager.TryGetComponent(prefabMatch.Prefab, out NetGeometryData targetGeometryData))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: target prefab has no NetGeometryData.");
                return false;
            }

            float nearIntersectionDistance = GetIntersectionExitDistance(
                nodeEntity,
                edgeEntity,
                edge,
                curve,
                nodeIsStartOnShortEdge);

            float farIntersectionDistance = GetIntersectionExitDistance(
                farNode,
                continuationEdge,
                continuationEdgeData,
                continuationCurve,
                farNodeIsStartOnContinuation);

            float targetPocketLength = GetAdaptiveTargetPocketLength(
                nodeEntity,
                edgeEntity,
                sourcePrefab,
                nodeIsStartOnShortEdge,
                sourceGeometryData,
                out string pocketWidthSource,
                out float pocketWidth,
                out float pocketEdgeGeometryWidth,
                out float pocketPrefabWidth,
                out string pocketLaneWidthDetail);

            Bezier4x3 currentToFarBezier = ComputeMergedBezier(
                removableNode,
                edge,
                curve,
                continuationEdgeData,
                continuationCurve);
            bool continuationStartsAtRemovedNode = continuationEdgeData.m_Start == removableNode;
            Bezier4x3 mergedBezier = continuationStartsAtRemovedNode
                ? currentToFarBezier
                : MathUtils.Invert(currentToFarBezier);
            Entity startNode = continuationStartsAtRemovedNode ? nodeEntity : farNode;
            Entity endNode = continuationStartsAtRemovedNode ? farNode : nodeEntity;
            bool nodeIsStartOnMergedEdge = startNode == nodeEntity;
            bool postMergeInvertTarget = prefabMatch.Invert ^ (nodeIsStartOnShortEdge != nodeIsStartOnMergedEdge);
            float mergedLength = MathUtils.Length(mergedBezier);
            if (mergedLength <= 0.01f)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)}: merged curve length is {mergedLength:0.###}m.");
                return false;
            }

            float usableLength = mergedLength - nearIntersectionDistance - farIntersectionDistance;
            float reservedLength = targetPocketLength * 2f;
            if (usableLength >= reservedLength)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge skipped shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: merged edge has enough room for two reserved pockets, so no half split is needed mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m reservedTwice={reservedLength:0.##}m requestedPocket={targetPocketLength:0.##}m.");
                return false;
            }

            float halfUsableLength = usableLength * 0.5f;
            if (!HasMinimumPocketLength(halfUsableLength))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: usable length is too small after both intersection margins mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m half={halfUsableLength:0.##}m minPocket={MinimumPocketLaneLength:0.##}m effectiveMinPocket={GetEffectiveMinimumPocketLength():0.##}m requestedPocket={targetPocketLength:0.##}m.");
                return false;
            }

            GetMinMaxSplitPositions(
                mergedLength,
                targetGeometryData.m_DefaultWidth,
                targetGeometryData.m_EdgeLengthRange.min,
                out float minSplit,
                out float maxSplit);
            if (minSplit >= maxSplit)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: merged target edge is too short to split safely length={mergedLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###}.");
                return false;
            }

            float splitDistance = nearIntersectionDistance + halfUsableLength;
            float splitPosition = GetCurvePositionAtDistance(
                mergedBezier,
                nodeIsStartOnMergedEdge,
                splitDistance);
            if (splitPosition < minSplit || splitPosition > maxSplit)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: half split is outside safe split range split={splitPosition:0.###} minSplit={minSplit:0.###} maxSplit={maxSplit:0.###} splitDistance={splitDistance:0.##}m mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m.");
                return false;
            }

            splitDistance = GetCurveDistanceFromNode(mergedBezier, nodeIsStartOnMergedEdge, splitPosition);
            float pocketDistance = math.max(0f, splitDistance - nearIntersectionDistance);
            float3 hitPosition = MathUtils.Position(mergedBezier, splitPosition);
            bool hasMergedUpgraded = EntityManager.TryGetComponent(continuationEdge, out Upgraded mergedUpgraded);
            FarIntersectionTrafficSnapshot farSnapshot = m_SplitLaneConnectionFixSystem != null
                ? m_SplitLaneConnectionFixSystem.CaptureFarIntersectionTrafficSnapshot(farNode, continuationEdge)
                : null;

            candidate = new NodeMergeCandidate
            {
                Mode = NodeMergeMode.BalancedOppositeTarget,
                Node = nodeEntity,
                ShortEdge = edgeEntity,
                RemovableNode = removableNode,
                ContinuationEdge = continuationEdge,
                FarNode = farNode,
                SourcePrefab = sourcePrefab,
                TargetPrefab = prefabMatch.Prefab,
                LaneRepairMode = SplitLaneConnectionRepairMode.BalancedOppositeTarget,
                InvertTarget = prefabMatch.Invert,
                PostMergeInvertTarget = postMergeInvertTarget,
                HasTargetUpgrade = prefabMatch.HasTargetUpgrade,
                TargetUpgrade = prefabMatch.TargetUpgrade,
                ShortEdgeLength = curve.m_Length > 0.01f ? curve.m_Length : MathUtils.Length(curve.m_Bezier),
                ContinuationEdgeLength = continuationCurve.m_Length > 0.01f ? continuationCurve.m_Length : MathUtils.Length(continuationCurve.m_Bezier),
                MergedLength = mergedLength,
                ExpectedSplitPosition = splitPosition,
                ExpectedSplitDistance = splitDistance,
                ExpectedIntersectionDistance = nearIntersectionDistance,
                ExpectedFarIntersectionDistance = farIntersectionDistance,
                ExpectedUsableLength = usableLength,
                ExpectedPocketDistance = pocketDistance,
                ExpectedTargetDistance = splitDistance,
                ExpectedTargetPocketLength = targetPocketLength,
                ExpectedHitPosition = hitPosition,
                OriginalForwardLanes = continuationRoadCounts.Forward,
                OriginalBackwardLanes = continuationRoadCounts.Backward,
                TargetForwardLanes = prefabMatch.TargetCounts.Forward,
                TargetBackwardLanes = prefabMatch.TargetCounts.Backward,
                FarIntersectionSnapshot = farSnapshot,
                MergeRequest = new NodeMergeDefinitionRequest
                {
                    Prefab = prefabMatch.Prefab,
                    RemovedNode = removableNode,
                    StartNode = startNode,
                    EndNode = endNode,
                    MergedCurve = mergedBezier,
                    MergedLength = mergedLength,
                    RandomSeed = GetDefinitionRandomSeed(continuationEdge),
                    HasUpgraded = hasMergedUpgraded,
                    Upgraded = mergedUpgraded,
                    FirstDeletion = new EdgeDeletionDefinitionRequest
                    {
                        Edge = edgeEntity,
                        StartNode = edge.m_Start,
                        EndNode = edge.m_End,
                        Curve = curve.m_Bezier,
                        Length = curve.m_Length > 0.01f ? curve.m_Length : MathUtils.Length(curve.m_Bezier)
                    },
                    SecondDeletion = new EdgeDeletionDefinitionRequest
                    {
                        Edge = continuationEdge,
                        StartNode = continuationEdgeData.m_Start,
                        EndNode = continuationEdgeData.m_End,
                        Curve = continuationCurve.m_Bezier,
                        Length = continuationCurve.m_Length > 0.01f ? continuationCurve.m_Length : MathUtils.Length(continuationCurve.m_Bezier)
                    }
                }
            };

            string farSnapshotDetail = farSnapshot != null ? farSnapshot.Detail : "unavailable";
            Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge fallback accepted shortEdge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)} farNode={FormatEntity(farNode)} sourcePrefab={GetPrefabNameFromPrefab(sourcePrefab)} continuationPrefab={GetPrefabNameFromPrefab(continuationPrefab)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)} mergeDirection={(continuationStartsAtRemovedNode ? "current-to-far" : "far-to-current")} previewShortEdgeOrientation={(prefabMatch.Invert ? "reversed" : "direct")} postMergeCurrentOrientation={(postMergeInvertTarget ? "reversed" : "direct")} currentNodeSideOnShort={(nodeIsStartOnShortEdge ? "start" : "end")} currentNodeSideOnMerged={(nodeIsStartOnMergedEdge ? "start" : "end")} continuationLayout={continuationLayoutDetail} shortLength={candidate.ShortEdgeLength:0.##}m continuationLength={candidate.ContinuationEdgeLength:0.##}m mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m reservedTwice={reservedLength:0.##}m half={halfUsableLength:0.##}m split={splitPosition:0.###} splitDistance={splitDistance:0.##}m requestedPocket={targetPocketLength:0.##}m widthSource={pocketWidthSource} width={FormatMeters(pocketWidth)} edgeGeometryWidth={FormatMeters(pocketEdgeGeometryWidth)} prefabWidth={FormatMeters(pocketPrefabWidth)} laneWidthDetail={pocketLaneWidthDetail} mergeTargetUpgrade={(hasMergedUpgraded ? mergedUpgraded.m_Flags.ToString() : "none")} currentTargetUpgrade={(prefabMatch.HasTargetUpgrade ? prefabMatch.TargetUpgrade.m_Flags.ToString() : "none")} farSnapshot=({farSnapshotDetail}) laneRepair=balanced-opposite-target.");
            return true;
        }

        private bool TryBuildShortEdgeReplacementOnlyCandidate(
            Entity nodeEntity,
            Entity edgeEntity,
            Edge edge,
            Curve curve,
            Entity sourcePrefab,
            ReplacementPrefabMatch prefabMatch,
            ShortEdgeFallbackContext fallbackContext,
            out NodeMergeCandidate candidate)
        {
            candidate = default;

            if (prefabMatch.Prefab == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge direct replacement rejected shortEdge={FormatEntity(edgeEntity)}: replacement target prefab is missing.");
                return false;
            }

            if (fallbackContext.ContinuationPrefab == sourcePrefab)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge direct replacement rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(fallbackContext.ContinuationEdge)}: continuation prefab still matches source prefab={GetPrefabNameFromPrefab(sourcePrefab)}, so source-prefab merge should own this case.");
                return false;
            }

            float shortLength = curve.m_Length > 0.01f
                ? curve.m_Length
                : MathUtils.Length(curve.m_Bezier);
            float continuationLength = fallbackContext.ContinuationCurve.m_Length > 0.01f
                ? fallbackContext.ContinuationCurve.m_Length
                : MathUtils.Length(fallbackContext.ContinuationCurve.m_Bezier);
            float3 hitPosition = MathUtils.Position(curve.m_Bezier, 0.5f);
            TransitionConnectionSnapshot reverseSnapshot = m_SplitLaneConnectionFixSystem != null
                ? m_SplitLaneConnectionFixSystem.CaptureTransitionReverseConnections(
                    fallbackContext.RemovableNode,
                    edgeEntity,
                    fallbackContext.ContinuationEdge)
                : null;

            candidate = new NodeMergeCandidate
            {
                Mode = NodeMergeMode.ShortEdgeReplacementOnly,
                Node = nodeEntity,
                ShortEdge = edgeEntity,
                RemovableNode = fallbackContext.RemovableNode,
                ContinuationEdge = fallbackContext.ContinuationEdge,
                FarNode = fallbackContext.FarNode,
                SourcePrefab = sourcePrefab,
                TargetPrefab = prefabMatch.Prefab,
                LaneRepairMode = SplitLaneConnectionRepairMode.ShortEdgeTransition,
                InvertTarget = prefabMatch.Invert,
                PostMergeInvertTarget = prefabMatch.Invert,
                HasTargetUpgrade = prefabMatch.HasTargetUpgrade,
                TargetUpgrade = prefabMatch.TargetUpgrade,
                ShortEdgeLength = shortLength,
                ContinuationEdgeLength = continuationLength,
                MergedLength = shortLength,
                ExpectedSplitPosition = 0f,
                ExpectedSplitDistance = shortLength,
                ExpectedIntersectionDistance = 0f,
                ExpectedFarIntersectionDistance = 0f,
                ExpectedUsableLength = shortLength,
                ExpectedPocketDistance = shortLength,
                ExpectedTargetDistance = shortLength,
                ExpectedTargetPocketLength = shortLength,
                ExpectedHitPosition = hitPosition,
                OriginalForwardLanes = prefabMatch.OriginalCounts.Forward,
                OriginalBackwardLanes = prefabMatch.OriginalCounts.Backward,
                TargetForwardLanes = prefabMatch.TargetCounts.Forward,
                TargetBackwardLanes = prefabMatch.TargetCounts.Backward,
                TransitionReverseSnapshot = reverseSnapshot
            };

            string snapshotDetail = reverseSnapshot != null
                ? reverseSnapshot.Detail
                : "snapshot=unavailable fixSystem=missing";
            Mod.LogDiagnostic($"[IntersectionTool] Short-edge direct replacement fallback accepted shortEdge={FormatEntity(edgeEntity)} transitionNode={FormatEntity(fallbackContext.RemovableNode)} continuation={FormatEntity(fallbackContext.ContinuationEdge)} farNode={FormatEntity(fallbackContext.FarNode)} sourcePrefab={GetPrefabNameFromPrefab(sourcePrefab)} continuationPrefab={GetPrefabNameFromPrefab(fallbackContext.ContinuationPrefab)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)} orientation={(prefabMatch.Invert ? "reversed" : "direct")} shortLength={shortLength:0.##}m continuationLength={continuationLength:0.##}m connectedEdges={fallbackContext.ConnectedEdgeCount} noMergeDefinitions=true noSplitDefinitions=true laneRepair=short-edge-transition {snapshotDetail}.");
            return true;
        }

        private bool TryContinuationMatchesOppositeTargetLayout(
            Entity continuationEdge,
            ReplacementPrefabMatch prefabMatch,
            bool currentNodeIsStartOnShortEdge,
            bool farNodeIsStartOnContinuation,
            out RoadLaneCounts continuationRoadCounts,
            out string detail)
        {
            continuationRoadCounts = default;
            if (!m_ReplacementPrefabMatcher.TryGetRoadLaneProfile(continuationEdge, prefabMatch.Prefab, out RoadLaneProfile continuationProfile))
            {
                detail = "continuationProfile=missing";
                return false;
            }

            return ContinuationTargetLaneProfileMatcher.TryMatchContinuationToReplacementTarget(
                prefabMatch,
                continuationProfile,
                currentNodeIsStartOnShortEdge,
                farNodeIsStartOnContinuation,
                out continuationRoadCounts,
                out detail);
        }

        private bool TryGetShortEdgeFallbackContext(
            Entity intersectionNode,
            Entity edgeEntity,
            Edge edge,
            out ShortEdgeFallbackContext context)
        {
            context = default;

            Entity removableNode = edge.m_Start == intersectionNode
                ? edge.m_End
                : edge.m_End == intersectionNode
                    ? edge.m_Start
                    : Entity.Null;
            if (removableNode == Entity.Null ||
                !EntityManager.Exists(removableNode) ||
                !EntityManager.HasComponent<Node>(removableNode) ||
                EntityManager.HasComponent<Roundabout>(removableNode))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge fallback rejected edge={FormatEntity(edgeEntity)}: far endpoint {FormatEntity(removableNode)} is not a removable/transition road node.");
                return false;
            }

            if (!EntityManager.TryGetBuffer(removableNode, true, out DynamicBuffer<ConnectedEdge> removableConnectedEdges))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge fallback rejected edge={FormatEntity(edgeEntity)} transitionNode={FormatEntity(removableNode)}: missing ConnectedEdge buffer.");
                return false;
            }

            if (removableConnectedEdges.Length != 2)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge fallback rejected edge={FormatEntity(edgeEntity)} transitionNode={FormatEntity(removableNode)}: connectedEdges={removableConnectedEdges.Length}; all short-edge fallback modes require exactly two connected edges, so road intersections are skipped.");
                return false;
            }

            Entity continuationEdge = Entity.Null;
            for (int i = 0; i < removableConnectedEdges.Length; i++)
            {
                Entity candidateEdge = removableConnectedEdges[i].m_Edge;
                if (candidateEdge != edgeEntity)
                {
                    continuationEdge = candidateEdge;
                    break;
                }
            }

            if (continuationEdge == Entity.Null ||
                !IsRoadEdge(continuationEdge) ||
                !EntityManager.TryGetComponent(continuationEdge, out Edge continuationEdgeData) ||
                !EntityManager.TryGetComponent(continuationEdge, out Curve continuationCurve) ||
                !EntityManager.TryGetComponent(continuationEdge, out PrefabRef continuationPrefabRef))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge fallback rejected edge={FormatEntity(edgeEntity)} transitionNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)}: continuation is not a complete road edge.");
                return false;
            }

            if (EntityManager.HasComponent<Owner>(edgeEntity) ||
                EntityManager.HasComponent<Owner>(continuationEdge))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge fallback rejected edge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)}: owned sub-net edges are not modified automatically.");
                return false;
            }

            Entity farNode = continuationEdgeData.m_Start == removableNode
                ? continuationEdgeData.m_End
                : continuationEdgeData.m_End == removableNode
                    ? continuationEdgeData.m_Start
                    : Entity.Null;
            if (farNode == Entity.Null ||
                farNode == intersectionNode ||
                !EntityManager.Exists(farNode) ||
                !EntityManager.HasComponent<Node>(farNode))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge fallback rejected edge={FormatEntity(edgeEntity)} transitionNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)}: invalid far node {FormatEntity(farNode)}.");
                return false;
            }

            context = new ShortEdgeFallbackContext
            {
                RemovableNode = removableNode,
                ContinuationEdge = continuationEdge,
                ContinuationEdgeData = continuationEdgeData,
                ContinuationCurve = continuationCurve,
                FarNode = farNode,
                ContinuationPrefab = continuationPrefabRef.m_Prefab,
                ConnectedEdgeCount = removableConnectedEdges.Length
            };
            return true;
        }

        private bool TryCalculateMergedSplitGeometry(
            Entity nodeEntity,
            Entity sourceEdgeEntity,
            Edge sourceEdge,
            Curve sourceCurve,
            bool nodeIsStartOnMergedEdge,
            Bezier4x3 mergedBezier,
            float mergedLength,
            Entity sourcePrefab,
            NetGeometryData geometryData,
            out float splitPosition,
            out float splitDistance,
            out float intersectionDistance,
            out float pocketDistance,
            out float targetDistance,
            out float targetPocketLength,
            out float3 hitPosition,
            float requestedTargetPocketLength = -1f)
        {
            splitPosition = 0f;
            splitDistance = 0f;
            intersectionDistance = 0f;
            pocketDistance = 0f;
            targetDistance = 0f;
            hitPosition = default;
            targetPocketLength = 0f;

            if (mergedLength <= 0.01f)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(sourceEdgeEntity)}: merged curve length is {mergedLength:0.###}m.");
                return false;
            }

            GetMinMaxSplitPositions(
                mergedLength,
                geometryData.m_DefaultWidth,
                geometryData.m_EdgeLengthRange.min,
                out float minSplit,
                out float maxSplit);

            if (minSplit >= maxSplit)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(sourceEdgeEntity)}: merged edge is still too short to split safely (length={mergedLength:0.##}m).");
                return false;
            }

            bool nodeIsStartOnSourceEdge = sourceEdge.m_Start == nodeEntity;
            intersectionDistance = GetIntersectionExitDistance(
                nodeEntity,
                sourceEdgeEntity,
                sourceEdge,
                sourceCurve,
                nodeIsStartOnSourceEdge);
            float adaptiveTargetPocketLength = GetAdaptiveTargetPocketLength(
                nodeEntity,
                sourceEdgeEntity,
                sourcePrefab,
                nodeIsStartOnSourceEdge,
                geometryData,
                out string pocketWidthSource,
                out float pocketWidth,
                out float pocketEdgeGeometryWidth,
                out float pocketPrefabWidth,
                out string pocketLaneWidthDetail);
            targetPocketLength = ResolveTargetPocketLength(
                adaptiveTargetPocketLength,
                requestedTargetPocketLength,
                out float requestedTargetPocketLengthBeforeCap,
                out float maximumRequestedPocketLength,
                out bool retryOverride);
            if (!TryCalculateSafeSplitTargetPlan(
                    mergedBezier,
                    nodeIsStartOnMergedEdge,
                    minSplit,
                    maxSplit,
                    intersectionDistance,
                    targetPocketLength,
                    out SafeSplitTargetPlan targetPlan))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(sourceEdgeEntity)}: merged edge still has no room for a pocket lane (mergedLength={mergedLength:0.##}m intersection={intersectionDistance:0.##}m maxDistanceFromNode={targetPlan.MaxDistanceFromNode:0.##}m availablePocket={targetPlan.MaxDistanceFromNode - intersectionDistance:0.###}m minPocket={MinimumPocketLaneLength:0.##}m effectiveMinPocket={GetEffectiveMinimumPocketLength():0.###}m tolerance={MinimumPocketLaneLengthTolerance:0.###}m requestedPocket={targetPocketLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###}).");
                return false;
            }

            Mod.LogDiagnostic($"[IntersectionTool] Road-node merge split target pocket length node={FormatEntity(nodeEntity)} sourceEdge={FormatEntity(sourceEdgeEntity)} prefab={GetPrefabName(sourceEdgeEntity)} widthSource={pocketWidthSource} width={FormatMeters(pocketWidth)} edgeGeometryWidth={FormatMeters(pocketEdgeGeometryWidth)} prefabWidth={FormatMeters(pocketPrefabWidth)} laneWidthDetail={pocketLaneWidthDetail} adaptivePocket={adaptiveTargetPocketLength:0.##}m requestedPocket={targetPocketLength:0.##}m requestedBeforeCap={requestedTargetPocketLengthBeforeCap:0.##}m minPocket={MinimumWidthBasedPocketLaneLength:0.##}m maxPocket={maximumRequestedPocketLength:0.##}m retryOverride={retryOverride} maxDistanceFromNode={targetPlan.MaxDistanceFromNode:0.##}m intersection={intersectionDistance:0.##}m.");

            targetDistance = targetPlan.TargetDistance;
            splitPosition = targetPlan.CurvePosition;
            splitDistance = targetPlan.SplitDistance;
            pocketDistance = targetPlan.PocketDistance;
            hitPosition = targetPlan.HitPosition;
            return true;
        }

        private float GetIntersectionExitDistance(
            Entity nodeEntity,
            Entity edgeEntity,
            Edge edge,
            Curve curve,
            bool nodeIsStart)
        {
            float fallbackDistance = IntersectionExitBuffer;
            if (!NetTopologyHelpers.TryGetOutwardDirection(edge, curve, nodeEntity, nodeIsStart, out float2 currentDirection))
            {
                return fallbackDistance;
            }

            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return fallbackDistance;
            }

            float result = fallbackDistance;
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity otherEdgeEntity = connectedEdges[i].m_Edge;
                if (otherEdgeEntity == edgeEntity ||
                    !IsRoadEdge(otherEdgeEntity) ||
                    !EntityManager.TryGetComponent(otherEdgeEntity, out Edge otherEdge) ||
                    !EntityManager.TryGetComponent(otherEdgeEntity, out Curve otherCurve))
                {
                    continue;
                }

                bool otherNodeIsStart = otherEdge.m_Start == nodeEntity;
                bool otherNodeIsEnd = otherEdge.m_End == nodeEntity;
                if ((!otherNodeIsStart && !otherNodeIsEnd) ||
                    !NetTopologyHelpers.TryGetOutwardDirection(otherEdge, otherCurve, nodeEntity, otherNodeIsStart, out float2 otherDirection))
                {
                    continue;
                }

                float sinAngle = math.abs(NetTopologyHelpers.Cross(currentDirection, otherDirection));
                if (sinAngle < MinimumIntersectionSin ||
                    !TryGetEdgeHalfWidthAtNode(
                        otherEdgeEntity,
                        nodeEntity,
                        otherNodeIsStart,
                        out float otherHalfWidth,
                        out string widthSource,
                        out float edgeGeometryWidth,
                        out float prefabWidth))
                {
                    continue;
                }

                float candidate = otherHalfWidth / sinAngle + IntersectionExitBuffer;
                result = math.max(result, math.min(candidate, MaxIntersectionExitDistance));
                Mod.LogDiagnostic($"[IntersectionTool] Exit width compare node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} crossEdge={FormatEntity(otherEdgeEntity)} crossPrefab={GetPrefabName(otherEdgeEntity)} source={widthSource} edgeGeometryWidth={FormatMeters(edgeGeometryWidth)} prefabWidth={FormatMeters(prefabWidth)} sin={sinAngle:0.###} candidate={candidate:0.##}m result={result:0.##}m.");
            }

            Mod.LogDiagnostic($"[IntersectionTool] Exit distance result node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} distance={result:0.##}m.");
            return result;
        }

        private bool TryGetEdgeHalfWidthAtNode(
            Entity edgeEntity,
            Entity nodeEntity,
            bool nodeIsStart,
            out float halfWidth,
            out string widthSource,
            out float edgeGeometryWidth,
            out float prefabWidth)
        {
            edgeGeometryWidth = 0f;
            prefabWidth = 0f;

            if (EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometryData) &&
                geometryData.m_DefaultWidth > 0f)
            {
                prefabWidth = geometryData.m_DefaultWidth;
            }

            if (EntityManager.TryGetComponent(edgeEntity, out EdgeGeometry edgeGeometry) &&
                EntityManager.TryGetComponent(nodeEntity, out Node node) &&
                TryGetSegmentWidthAtNode(node.m_Position, nodeIsStart ? edgeGeometry.m_Start : edgeGeometry.m_End, out edgeGeometryWidth))
            {
                halfWidth = edgeGeometryWidth * 0.5f;
                widthSource = "EdgeGeometry";
                return true;
            }

            if (prefabWidth > 0f)
            {
                halfWidth = prefabWidth * 0.5f;
                widthSource = "PrefabFallback";
                return true;
            }

            halfWidth = 0f;
            widthSource = "Missing";
            return false;
        }

        private float GetAdaptiveTargetPocketLength(
            Entity nodeEntity,
            Entity edgeEntity,
            Entity sourcePrefab,
            bool nodeIsStart,
            NetGeometryData geometryData,
            out string widthSource,
            out float roadWidth,
            out float edgeGeometryWidth,
            out float prefabWidth,
            out string laneWidthDetail)
        {
            bool hasMeasuredWidth = TryGetEdgeHalfWidthAtNode(
                edgeEntity,
                nodeEntity,
                nodeIsStart,
                out float halfWidth,
                out string measuredWidthSource,
                out edgeGeometryWidth,
                out prefabWidth);

            if (prefabWidth <= 0f && geometryData.m_DefaultWidth > 0f)
            {
                prefabWidth = geometryData.m_DefaultWidth;
            }

            if (m_ReplacementPrefabMatcher.TryGetRoadLaneProfile(edgeEntity, sourcePrefab, out RoadLaneProfile profile) &&
                profile.DrivableLaneEnvelopeWidth > 0f)
            {
                roadWidth = profile.DrivableLaneEnvelopeWidth;
                widthSource = $"DrivableLaneEnvelope:{profile.Source}";
                laneWidthDetail = $"lanes={profile.DrivableLaneEnvelopeCount} min={profile.DrivableLaneEnvelopeMin:0.##}m max={profile.DrivableLaneEnvelopeMax:0.##}m envelope={profile.DrivableLaneEnvelopeWidth:0.##}m buffer={DrivableLaneEnvelopeBuffer:0.##}m detail={profile.DrivableLaneEnvelopeDetail}";
                return AlignLengthUpToPocketLengthGrid(math.clamp(
                    roadWidth + DrivableLaneEnvelopeBuffer,
                    MinimumWidthBasedPocketLaneLength,
                    MaximumWidthBasedPocketLaneLength));
            }

            laneWidthDetail = profile.Source == null
                ? "profile=not-attempted"
                : $"profile={profile.Source} drivableEnvelope=missing";

            if (prefabWidth > 0f)
            {
                roadWidth = prefabWidth;
                widthSource = edgeGeometryWidth > 0f ? "PrefabWidthWithEdgeGeometry" : "PrefabWidth";
            }
            else if (hasMeasuredWidth)
            {
                roadWidth = halfWidth * 2f;
                widthSource = measuredWidthSource;
            }
            else
            {
                roadWidth = 0f;
                widthSource = "MissingWidthFallback";
            }

            if (roadWidth <= 0f)
            {
                return FallbackPocketLaneLength;
            }

            float clampedWidth = math.clamp(
                roadWidth,
                MinimumWidthBasedPocketLaneLength,
                MaximumWidthBasedPocketLaneLength);
            return AlignLengthUpToPocketLengthGrid(clampedWidth);
        }

        private static string FormatMeters(float value)
        {
            return DiagnosticFormat.MetersOrMissing(value);
        }
    }
}
