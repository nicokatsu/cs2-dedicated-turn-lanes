using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class IntersectionToolSystem
    {
        private bool TryBuildSplitDefinitionRequest(
            Entity nodeEntity,
            Entity edgeEntity,
            out SplitDefinitionRequest request,
            out float splitPosition,
            out float splitDistance,
            out float intersectionDistance,
            out float pocketDistance,
            out float targetDistance,
            out float targetPocketLength,
            float requestedTargetPocketLength = -1f)
        {
            request = default;
            splitPosition = 0f;
            splitDistance = 0f;
            intersectionDistance = 0f;
            pocketDistance = 0f;
            targetDistance = 0f;
            targetPocketLength = 0f;

            if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
            {
                Mod.log.Info($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: missing Edge, Curve, or PrefabRef.");
                return false;
            }

            if (curve.m_Length <= 0.01f)
            {
                Mod.log.Info($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: curve length is {curve.m_Length:0.###}.");
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            if (!nodeIsStart && !nodeIsEnd)
            {
                Mod.log.Info($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: node {FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            if (!EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometryData))
            {
                Mod.log.Info($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: prefab {FormatEntity(prefabRef.m_Prefab)} has no NetGeometryData.");
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
                Mod.log.Info($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: too short to split safely (length={curve.m_Length:0.##}m).");
                return false;
            }

            intersectionDistance = GetIntersectionExitDistance(
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
            float requestedTargetPocketLengthBeforeCap = requestedTargetPocketLength > 0f
                ? requestedTargetPocketLength
                : adaptiveTargetPocketLength;
            float maximumRequestedPocketLength = requestedTargetPocketLength > 0f
                ? MaximumRetryPocketLaneLength
                : MaximumWidthBasedPocketLaneLength;
            targetPocketLength = requestedTargetPocketLength > 0f
                ? math.clamp(requestedTargetPocketLength, MinimumPocketLaneLength, maximumRequestedPocketLength)
                : adaptiveTargetPocketLength;
            float maxDistanceFromNode = GetMaximumSplitDistanceFromNode(curve, nodeIsStart, minSplit, maxSplit);
            if (!HasMinimumPocketLength(maxDistanceFromNode - intersectionDistance))
            {
                Mod.log.Info($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: not enough room for a pocket lane (length={curve.m_Length:0.##}m intersection={intersectionDistance:0.##}m maxDistanceFromNode={maxDistanceFromNode:0.##}m availablePocket={maxDistanceFromNode - intersectionDistance:0.###}m minPocket={MinimumPocketLaneLength:0.##}m effectiveMinPocket={GetEffectiveMinimumPocketLength():0.###}m tolerance={MinimumPocketLaneLengthTolerance:0.###}m requestedPocket={targetPocketLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###}).");
                return false;
            }

            Mod.log.Info($"[IntersectionTool] Split target pocket length node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabNameFromPrefab(prefabRef.m_Prefab)} widthSource={pocketWidthSource} width={FormatMeters(pocketWidth)} edgeGeometryWidth={FormatMeters(pocketEdgeGeometryWidth)} prefabWidth={FormatMeters(pocketPrefabWidth)} laneWidthDetail={pocketLaneWidthDetail} adaptivePocket={adaptiveTargetPocketLength:0.##}m requestedPocket={targetPocketLength:0.##}m requestedBeforeCap={requestedTargetPocketLengthBeforeCap:0.##}m minPocket={MinimumWidthBasedPocketLaneLength:0.##}m maxPocket={maximumRequestedPocketLength:0.##}m retryOverride={(requestedTargetPocketLength > 0f)} maxDistanceFromNode={maxDistanceFromNode:0.##}m intersection={intersectionDistance:0.##}m.");

            float desiredDistance = GetGridAlignedSplitDistance(
                intersectionDistance,
                targetPocketLength,
                MinimumPocketLaneLength,
                maxDistanceFromNode);
            targetDistance = desiredDistance;
            float desiredPosition = GetCurvePositionAtDistance(curve, nodeIsStart, desiredDistance);
            splitPosition = math.clamp(desiredPosition, minSplit, maxSplit);
            splitDistance = GetCurveDistanceFromNode(curve, nodeIsStart, splitPosition);
            pocketDistance = math.max(0f, splitDistance - intersectionDistance);

            float3 hitPosition = MathUtils.Position(curve.m_Bezier, splitPosition);
            int randomSeed = EntityManager.TryGetComponent(edgeEntity, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : edgeEntity.Index;

            request = new SplitDefinitionRequest
            {
                Edge = edgeEntity,
                Prefab = prefabRef.m_Prefab,
                HitPosition = hitPosition,
                CurvePosition = splitPosition,
                RandomSeed = randomSeed
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
                Mod.log.Info($"[IntersectionTool] Cannot prepare road-node merge fallback edge={FormatEntity(edgeEntity)}: missing Edge, Curve, PrefabRef, or prefab NetGeometryData.");
                return false;
            }

            bool nodeIsStartOnShortEdge = edge.m_Start == nodeEntity;
            bool nodeIsEndOnShortEdge = edge.m_End == nodeEntity;
            if (!nodeIsStartOnShortEdge && !nodeIsEndOnShortEdge)
            {
                Mod.log.Info($"[IntersectionTool] Cannot prepare road-node merge fallback edge={FormatEntity(edgeEntity)}: node {FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            if (TryBuildBalancedOppositeTargetNodeMergeCandidate(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    curve,
                    prefabRef.m_Prefab,
                    geometryData,
                    prefabMatch,
                    out candidate))
            {
                return true;
            }

            if (!TryGetMergeableRoadNode(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    prefabRef.m_Prefab,
                    out Entity removableNode,
                    out Entity continuationEdge,
                    out Edge continuationEdgeData,
                    out Curve continuationCurve,
                    out Entity farNode,
                    out _))
            {
                return false;
            }

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
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)}: merged edge would not connect hovered node {FormatEntity(nodeEntity)} start={FormatEntity(startNode)} end={FormatEntity(endNode)}.");
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
                    prefabRef.m_Prefab,
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

            int randomSeed = EntityManager.TryGetComponent(edgeEntity, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : edgeEntity.Index;
            bool hasUpgraded = EntityManager.TryGetComponent(edgeEntity, out Upgraded upgraded);

            candidate = new NodeMergeCandidate
            {
                Node = nodeEntity,
                ShortEdge = edgeEntity,
                RemovableNode = removableNode,
                ContinuationEdge = continuationEdge,
                FarNode = farNode,
                SourcePrefab = prefabRef.m_Prefab,
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
                    Prefab = prefabRef.m_Prefab,
                    RemovedNode = removableNode,
                    StartNode = startNode,
                    EndNode = endNode,
                    MergedCurve = mergedBezier,
                    MergedLength = mergedLength,
                    RandomSeed = randomSeed,
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

            Mod.log.Info($"[IntersectionTool] Road-node merge fallback accepted shortEdge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)} farNode={FormatEntity(farNode)} sourcePrefab={GetPrefabNameFromPrefab(prefabRef.m_Prefab)} shortLength={curve.m_Length:0.##}m continuationLength={continuationCurve.m_Length:0.##}m mergedLength={mergedLength:0.##}m split={splitPosition:0.###} splitDistance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m requestedPocket={targetPocketLength:0.##}m target={targetDistance:0.##}m. Safety: removable node has exactly two connected road edges and both prefabs match.");
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
            out NodeMergeCandidate candidate)
        {
            candidate = default;

            if (prefabMatch.Prefab == Entity.Null)
            {
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)}: replacement target prefab is missing.");
                return false;
            }

            if (!TryGetMergeableRoadNode(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    prefabMatch.Prefab,
                    out Entity removableNode,
                    out Entity continuationEdge,
                    out Edge continuationEdgeData,
                    out Curve continuationCurve,
                    out Entity farNode,
                    out Entity continuationPrefab))
            {
                return false;
            }

            if (!IsValidIntersection(farNode))
            {
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} farNode={FormatEntity(farNode)}: far node is not a valid intersection for opposite-side lane repair.");
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
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: continuation is not the reverse target layout. {continuationLayoutDetail}");
                return false;
            }

            if (!EntityManager.TryGetComponent(prefabMatch.Prefab, out NetGeometryData targetGeometryData))
            {
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: target prefab has no NetGeometryData.");
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
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)}: merged curve length is {mergedLength:0.###}m.");
                return false;
            }

            float usableLength = mergedLength - nearIntersectionDistance - farIntersectionDistance;
            float reservedLength = targetPocketLength * 2f;
            if (usableLength >= reservedLength)
            {
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge skipped shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: merged edge has enough room for two reserved pockets, so no half split is needed mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m reservedTwice={reservedLength:0.##}m requestedPocket={targetPocketLength:0.##}m.");
                return false;
            }

            float halfUsableLength = usableLength * 0.5f;
            if (!HasMinimumPocketLength(halfUsableLength))
            {
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: usable length is too small after both intersection margins mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m half={halfUsableLength:0.##}m minPocket={MinimumPocketLaneLength:0.##}m effectiveMinPocket={GetEffectiveMinimumPocketLength():0.##}m requestedPocket={targetPocketLength:0.##}m.");
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
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: merged target edge is too short to split safely length={mergedLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###}.");
                return false;
            }

            float splitDistance = nearIntersectionDistance + halfUsableLength;
            float splitPosition = GetCurvePositionAtDistance(
                mergedBezier,
                nodeIsStartOnMergedEdge,
                splitDistance);
            if (splitPosition < minSplit || splitPosition > maxSplit)
            {
                Mod.log.Info($"[IntersectionTool] Balanced road-node merge rejected shortEdge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)}: half split is outside safe split range split={splitPosition:0.###} minSplit={minSplit:0.###} maxSplit={maxSplit:0.###} splitDistance={splitDistance:0.##}m mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m.");
                return false;
            }

            splitDistance = GetCurveDistanceFromNode(mergedBezier, nodeIsStartOnMergedEdge, splitPosition);
            float pocketDistance = math.max(0f, splitDistance - nearIntersectionDistance);
            float3 hitPosition = MathUtils.Position(mergedBezier, splitPosition);
            int randomSeed = EntityManager.TryGetComponent(continuationEdge, out PseudoRandomSeed continuationSeed)
                ? continuationSeed.m_Seed
                : continuationEdge.Index;
            bool hasMergedUpgraded = EntityManager.TryGetComponent(continuationEdge, out Upgraded mergedUpgraded);

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
                MergeRequest = new NodeMergeDefinitionRequest
                {
                    Prefab = prefabMatch.Prefab,
                    RemovedNode = removableNode,
                    StartNode = startNode,
                    EndNode = endNode,
                    MergedCurve = mergedBezier,
                    MergedLength = mergedLength,
                    RandomSeed = randomSeed,
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

            Mod.log.Info($"[IntersectionTool] Balanced road-node merge fallback accepted shortEdge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)} farNode={FormatEntity(farNode)} sourcePrefab={GetPrefabNameFromPrefab(sourcePrefab)} continuationPrefab={GetPrefabNameFromPrefab(continuationPrefab)} targetPrefab={GetPrefabNameFromPrefab(prefabMatch.Prefab)} mergeDirection={(continuationStartsAtRemovedNode ? "current-to-far" : "far-to-current")} previewShortEdgeOrientation={(prefabMatch.Invert ? "reversed" : "direct")} postMergeCurrentOrientation={(postMergeInvertTarget ? "reversed" : "direct")} currentNodeSideOnShort={(nodeIsStartOnShortEdge ? "start" : "end")} currentNodeSideOnMerged={(nodeIsStartOnMergedEdge ? "start" : "end")} continuationLayout={continuationLayoutDetail} shortLength={candidate.ShortEdgeLength:0.##}m continuationLength={candidate.ContinuationEdgeLength:0.##}m mergedLength={mergedLength:0.##}m nearMargin={nearIntersectionDistance:0.##}m farMargin={farIntersectionDistance:0.##}m usable={usableLength:0.##}m reservedTwice={reservedLength:0.##}m half={halfUsableLength:0.##}m split={splitPosition:0.###} splitDistance={splitDistance:0.##}m requestedPocket={targetPocketLength:0.##}m widthSource={pocketWidthSource} width={FormatMeters(pocketWidth)} edgeGeometryWidth={FormatMeters(pocketEdgeGeometryWidth)} prefabWidth={FormatMeters(pocketPrefabWidth)} laneWidthDetail={pocketLaneWidthDetail} mergeTargetUpgrade={(hasMergedUpgraded ? mergedUpgraded.m_Flags.ToString() : "none")} currentTargetUpgrade={(prefabMatch.HasTargetUpgrade ? prefabMatch.TargetUpgrade.m_Flags.ToString() : "none")} laneRepair=balanced-opposite-target.");
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
            if (!TryGetRoadLaneProfile(continuationEdge, prefabMatch.Prefab, out RoadLaneProfile continuationProfile))
            {
                detail = "continuationProfile=missing";
                return false;
            }

            continuationRoadCounts = continuationProfile.RoadCounts;
            if (!CountsMatchAtApproachNodes(
                    prefabMatch.TargetCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.RoadCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} road={FormatApproachCounts(continuationProfile.RoadCounts, farNodeIsStartOnContinuation)} expectedTarget={FormatApproachCounts(prefabMatch.TargetCounts, currentNodeIsStartOnShortEdge)} rawTargetRoad={prefabMatch.TargetCounts}";
                return false;
            }

            if (!CountsMatchAtApproachNodes(
                    prefabMatch.TargetTramTrackCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.TramTrackCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} tramTracks={FormatApproachCounts(continuationProfile.TramTrackCounts, farNodeIsStartOnContinuation)} expectedTargetTramTracks={FormatApproachCounts(prefabMatch.TargetTramTrackCounts, currentNodeIsStartOnShortEdge)} rawTargetTramTracks={prefabMatch.TargetTramTrackCounts}";
                return false;
            }

            if (!CountsMatchAtApproachNodes(
                    prefabMatch.TargetIndependentTramCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.IndependentTramCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} independentTram={FormatApproachCounts(continuationProfile.IndependentTramCounts, farNodeIsStartOnContinuation)} expectedTargetIndependentTram={FormatApproachCounts(prefabMatch.TargetIndependentTramCounts, currentNodeIsStartOnShortEdge)} rawTargetIndependentTram={prefabMatch.TargetIndependentTramCounts}";
                return false;
            }

            if (!CountsMatchAtApproachNodes(
                    prefabMatch.TargetPublicTransportTramCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.PublicTransportTramCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} publicTransportTram={FormatApproachCounts(continuationProfile.PublicTransportTramCounts, farNodeIsStartOnContinuation)} expectedTargetPublicTransportTram={FormatApproachCounts(prefabMatch.TargetPublicTransportTramCounts, currentNodeIsStartOnShortEdge)} rawTargetPublicTransportTram={prefabMatch.TargetPublicTransportTramCounts}";
                return false;
            }

            if (!LayoutCountsMatchAtApproachNodes(
                    prefabMatch.TargetBusLaneOffsetProfile,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.BusLaneLayout,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} busLayout={FormatApproachLayout(continuationProfile.BusLaneLayout, farNodeIsStartOnContinuation)} expectedTargetBus={FormatApproachLayout(prefabMatch.TargetBusLaneOffsetProfile, currentNodeIsStartOnShortEdge)} rawTargetBus={prefabMatch.TargetBusLaneLayout}";
                return false;
            }

            if (!LayoutCountsMatchAtApproachNodes(
                    prefabMatch.TargetTramTrackOffsetProfile,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.TramTrackLayout,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} tramLayout={FormatApproachLayout(continuationProfile.TramTrackLayout, farNodeIsStartOnContinuation)} expectedTargetTramLayout={FormatApproachLayout(prefabMatch.TargetTramTrackOffsetProfile, currentNodeIsStartOnShortEdge)} rawTargetTramLayout={prefabMatch.TargetTramTrackLayout}";
                return false;
            }

            detail = $"continuationProfile={continuationProfile.Source} road={FormatApproachCounts(continuationProfile.RoadCounts, farNodeIsStartOnContinuation)} expectedTarget={FormatApproachCounts(prefabMatch.TargetCounts, currentNodeIsStartOnShortEdge)} bus={FormatApproachLayout(continuationProfile.BusLaneLayout, farNodeIsStartOnContinuation)} tram={FormatApproachLayout(continuationProfile.TramTrackLayout, farNodeIsStartOnContinuation)}";
            return true;
        }

        private static bool CountsMatchAtApproachNodes(
            RoadLaneCounts targetCounts,
            bool targetNodeIsStart,
            RoadLaneCounts continuationCounts,
            bool continuationNodeIsStart)
        {
            return GetIncomingCount(targetCounts, targetNodeIsStart) == GetIncomingCount(continuationCounts, continuationNodeIsStart) &&
                   GetOutgoingCount(targetCounts, targetNodeIsStart) == GetOutgoingCount(continuationCounts, continuationNodeIsStart);
        }

        private static int GetIncomingCount(RoadLaneCounts counts, bool nodeIsStart)
        {
            return nodeIsStart ? counts.Backward : counts.Forward;
        }

        private static int GetOutgoingCount(RoadLaneCounts counts, bool nodeIsStart)
        {
            return nodeIsStart ? counts.Forward : counts.Backward;
        }

        private static string FormatApproachCounts(RoadLaneCounts counts, bool nodeIsStart)
        {
            return $"raw={counts} nodeSide={(nodeIsStart ? "start" : "end")} incoming={GetIncomingCount(counts, nodeIsStart)} outgoing={GetOutgoingCount(counts, nodeIsStart)}";
        }

        private static bool LayoutCountsMatchAtApproachNodes(
            DirectionalLaneOffsetProfile targetLayout,
            bool targetNodeIsStart,
            DirectionalLaneOffsetProfile continuationLayout,
            bool continuationNodeIsStart)
        {
            return GetIncomingLayoutCount(targetLayout, targetNodeIsStart) == GetIncomingLayoutCount(continuationLayout, continuationNodeIsStart) &&
                   GetOutgoingLayoutCount(targetLayout, targetNodeIsStart) == GetOutgoingLayoutCount(continuationLayout, continuationNodeIsStart);
        }

        private static int GetIncomingLayoutCount(DirectionalLaneOffsetProfile layout, bool nodeIsStart)
        {
            return nodeIsStart ? layout.BackwardCount : layout.ForwardCount;
        }

        private static int GetOutgoingLayoutCount(DirectionalLaneOffsetProfile layout, bool nodeIsStart)
        {
            return nodeIsStart ? layout.ForwardCount : layout.BackwardCount;
        }

        private static string FormatApproachLayout(DirectionalLaneOffsetProfile layout, bool nodeIsStart)
        {
            return $"raw={layout} nodeSide={(nodeIsStart ? "start" : "end")} incoming={GetIncomingLayoutCount(layout, nodeIsStart)} outgoing={GetOutgoingLayoutCount(layout, nodeIsStart)}";
        }

        private bool TryGetMergeableRoadNode(
            Entity intersectionNode,
            Entity edgeEntity,
            Edge edge,
            Entity expectedContinuationPrefab,
            out Entity removableNode,
            out Entity continuationEdge,
            out Edge continuationEdgeData,
            out Curve continuationCurve,
            out Entity farNode,
            out Entity continuationPrefab)
        {
            removableNode = Entity.Null;
            continuationEdge = Entity.Null;
            continuationEdgeData = default;
            continuationCurve = default;
            farNode = Entity.Null;
            continuationPrefab = Entity.Null;

            removableNode = edge.m_Start == intersectionNode
                ? edge.m_End
                : edge.m_End == intersectionNode
                    ? edge.m_Start
                    : Entity.Null;
            if (removableNode == Entity.Null ||
                !EntityManager.Exists(removableNode) ||
                !EntityManager.HasComponent<Node>(removableNode) ||
                EntityManager.HasComponent<Roundabout>(removableNode))
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)}: far endpoint {FormatEntity(removableNode)} is not a removable road node.");
                return false;
            }

            if (!EntityManager.TryGetBuffer(removableNode, true, out DynamicBuffer<ConnectedEdge> removableConnectedEdges))
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)}: missing ConnectedEdge buffer.");
                return false;
            }

            if (removableConnectedEdges.Length != 2)
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)}: connectedEdges={removableConnectedEdges.Length}; Network Tools-style road-node removal only allows exactly two connected edges, so intersections are protected.");
                return false;
            }

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
                    !EntityManager.TryGetComponent(continuationEdge, out continuationEdgeData) ||
                    !EntityManager.TryGetComponent(continuationEdge, out continuationCurve) ||
                    !EntityManager.TryGetComponent(continuationEdge, out PrefabRef continuationPrefabRef))
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)}: continuation is not a complete road edge.");
                return false;
            }
            continuationPrefab = continuationPrefabRef.m_Prefab;

            if (EntityManager.HasComponent<Owner>(edgeEntity) ||
                EntityManager.HasComponent<Owner>(continuationEdge))
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)}: owned sub-net edges are not merged automatically.");
                return false;
            }

            if (continuationPrefabRef.m_Prefab != expectedContinuationPrefab)
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)}: prefab mismatch expected={GetPrefabNameFromPrefab(expectedContinuationPrefab)} continuation={GetPrefabNameFromPrefab(continuationPrefabRef.m_Prefab)}.");
                return false;
            }

            farNode = continuationEdgeData.m_Start == removableNode
                ? continuationEdgeData.m_End
                : continuationEdgeData.m_End == removableNode
                    ? continuationEdgeData.m_Start
                    : Entity.Null;
            if (farNode == Entity.Null ||
                farNode == intersectionNode ||
                !EntityManager.Exists(farNode) ||
                !EntityManager.HasComponent<Node>(farNode))
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)}: invalid far node {FormatEntity(farNode)}.");
                return false;
            }

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
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(sourceEdgeEntity)}: merged curve length is {mergedLength:0.###}m.");
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
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(sourceEdgeEntity)}: merged edge is still too short to split safely (length={mergedLength:0.##}m).");
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
            float requestedTargetPocketLengthBeforeCap = requestedTargetPocketLength > 0f
                ? requestedTargetPocketLength
                : adaptiveTargetPocketLength;
            float maximumRequestedPocketLength = requestedTargetPocketLength > 0f
                ? MaximumRetryPocketLaneLength
                : MaximumWidthBasedPocketLaneLength;
            targetPocketLength = requestedTargetPocketLength > 0f
                ? math.clamp(requestedTargetPocketLength, MinimumPocketLaneLength, maximumRequestedPocketLength)
                : adaptiveTargetPocketLength;
            float maxDistanceFromNode = GetMaximumSplitDistanceFromNode(
                mergedBezier,
                nodeIsStartOnMergedEdge,
                minSplit,
                maxSplit);
            if (!HasMinimumPocketLength(maxDistanceFromNode - intersectionDistance))
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(sourceEdgeEntity)}: merged edge still has no room for a pocket lane (mergedLength={mergedLength:0.##}m intersection={intersectionDistance:0.##}m maxDistanceFromNode={maxDistanceFromNode:0.##}m availablePocket={maxDistanceFromNode - intersectionDistance:0.###}m minPocket={MinimumPocketLaneLength:0.##}m effectiveMinPocket={GetEffectiveMinimumPocketLength():0.###}m tolerance={MinimumPocketLaneLengthTolerance:0.###}m requestedPocket={targetPocketLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###}).");
                return false;
            }

            Mod.log.Info($"[IntersectionTool] Road-node merge split target pocket length node={FormatEntity(nodeEntity)} sourceEdge={FormatEntity(sourceEdgeEntity)} prefab={GetPrefabName(sourceEdgeEntity)} widthSource={pocketWidthSource} width={FormatMeters(pocketWidth)} edgeGeometryWidth={FormatMeters(pocketEdgeGeometryWidth)} prefabWidth={FormatMeters(pocketPrefabWidth)} laneWidthDetail={pocketLaneWidthDetail} adaptivePocket={adaptiveTargetPocketLength:0.##}m requestedPocket={targetPocketLength:0.##}m requestedBeforeCap={requestedTargetPocketLengthBeforeCap:0.##}m minPocket={MinimumWidthBasedPocketLaneLength:0.##}m maxPocket={maximumRequestedPocketLength:0.##}m retryOverride={(requestedTargetPocketLength > 0f)} maxDistanceFromNode={maxDistanceFromNode:0.##}m intersection={intersectionDistance:0.##}m.");

            float desiredDistance = GetGridAlignedSplitDistance(
                intersectionDistance,
                targetPocketLength,
                MinimumPocketLaneLength,
                maxDistanceFromNode);
            targetDistance = desiredDistance;
            float desiredPosition = GetCurvePositionAtDistance(mergedBezier, nodeIsStartOnMergedEdge, desiredDistance);
            splitPosition = math.clamp(desiredPosition, minSplit, maxSplit);
            splitDistance = GetCurveDistanceFromNode(mergedBezier, nodeIsStartOnMergedEdge, splitPosition);
            pocketDistance = math.max(0f, splitDistance - intersectionDistance);
            hitPosition = MathUtils.Position(mergedBezier, splitPosition);
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
                Mod.log.Info($"[IntersectionTool] Exit width compare node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} crossEdge={FormatEntity(otherEdgeEntity)} crossPrefab={GetPrefabName(otherEdgeEntity)} source={widthSource} edgeGeometryWidth={FormatMeters(edgeGeometryWidth)} prefabWidth={FormatMeters(prefabWidth)} sin={sinAngle:0.###} candidate={candidate:0.##}m result={result:0.##}m.");
            }

            Mod.log.Info($"[IntersectionTool] Exit distance result node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} distance={result:0.##}m.");
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

            if (TryGetRoadLaneProfile(edgeEntity, sourcePrefab, out RoadLaneProfile profile) &&
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
            return value > 0f ? $"{value:0.##}m" : "<missing>";
        }

        private static bool TryGetSegmentWidthAtNode(float3 nodePosition, Segment segment, out float width)
        {
            float2 node = nodePosition.xz;
            float2 centerA = ((segment.m_Left.a + segment.m_Right.a) * 0.5f).xz;
            float2 centerD = ((segment.m_Left.d + segment.m_Right.d) * 0.5f).xz;

            bool useA = math.distancesq(centerA, node) <= math.distancesq(centerD, node);
            float2 left = useA ? segment.m_Left.a.xz : segment.m_Left.d.xz;
            float2 right = useA ? segment.m_Right.a.xz : segment.m_Right.d.xz;
            width = math.distance(left, right);
            return width > 0.01f && !float.IsNaN(width) && !float.IsInfinity(width);
        }

        private static float GetGridAlignedSplitDistance(
            float intersectionDistance,
            float targetPocketLength,
            float minimumPocketLength,
            float maximumDistance)
        {
            if (maximumDistance <= 0f)
            {
                return 0f;
            }

            float maxPocketLength = maximumDistance - intersectionDistance;
            if (maxPocketLength <= 0f)
            {
                return maximumDistance;
            }

            float alignedPocketLength = AlignLengthUpToSplitGrid(targetPocketLength);
            if (alignedPocketLength <= maxPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            alignedPocketLength = AlignLengthDownToSplitGrid(maxPocketLength);
            if (alignedPocketLength >= minimumPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            return maximumDistance;
        }

        private static float AlignLengthUpToSplitGrid(float length)
        {
            if (length <= 0f)
            {
                return 0f;
            }

            return math.ceil(math.max(0f, length - SplitGridAlignmentTolerance) / SplitGridSize) * SplitGridSize;
        }

        private static float AlignLengthUpToPocketLengthGrid(float length)
        {
            if (length <= 0f)
            {
                return 0f;
            }

            return math.ceil(math.max(0f, length - SplitGridAlignmentTolerance) / PocketLengthGridSize) * PocketLengthGridSize;
        }

        private static float AlignLengthDownToSplitGrid(float length)
        {
            if (length <= 0f)
            {
                return 0f;
            }

            return math.floor((length + SplitGridAlignmentTolerance) / SplitGridSize) * SplitGridSize;
        }

        private static bool HasMinimumPocketLength(float pocketLength)
        {
            return pocketLength + SplitLengthBuffer + MinimumPocketLaneLengthTolerance >= MinimumPocketLaneLength;
        }

        private static float GetEffectiveMinimumPocketLength()
        {
            return math.max(0f, MinimumPocketLaneLength - SplitLengthBuffer - MinimumPocketLaneLengthTolerance);
        }

        private static float GetCurvePositionAtDistance(Curve curve, bool fromStart, float distance)
        {
            return GetCurvePositionAtDistance(curve.m_Bezier, fromStart, distance);
        }

        private static float GetCurvePositionAtDistance(Bezier4x3 bezier, bool fromStart, float distance)
        {
            if (distance <= 0f)
            {
                return fromStart ? 0f : 1f;
            }

            Bezier4x3 orientedBezier = fromStart ? bezier : MathUtils.Invert(bezier);
            Bounds1 range = new Bounds1(0f, 1f);
            float remainingDistance = distance;
            float position = MathUtils.ClampLength(orientedBezier, ref range, ref remainingDistance) ? range.max : 1f;
            return fromStart ? position : 1f - position;
        }

        private static float GetCurveDistanceFromNode(Curve curve, bool fromStart, float position)
        {
            return GetCurveDistanceFromNode(curve.m_Bezier, fromStart, position);
        }

        private static float GetCurveDistanceFromNode(Bezier4x3 bezier, bool fromStart, float position)
        {
            position = math.saturate(position);
            Bounds1 range = fromStart ? new Bounds1(0f, position) : new Bounds1(position, 1f);
            return MathUtils.Length(bezier, range);
        }

        private static float GetMaximumSplitDistanceFromNode(
            Curve curve,
            bool fromStart,
            float minSplit,
            float maxSplit)
        {
            return GetMaximumSplitDistanceFromNode(curve.m_Bezier, fromStart, minSplit, maxSplit);
        }

        private static float GetMaximumSplitDistanceFromNode(
            Bezier4x3 bezier,
            bool fromStart,
            float minSplit,
            float maxSplit)
        {
            float furthestSafePosition = fromStart ? maxSplit : minSplit;
            return GetCurveDistanceFromNode(bezier, fromStart, furthestSafePosition);
        }

        private static Bezier4x3 ComputeMergedBezier(Entity nodeEntity, Edge edge1, Curve curve1, Edge edge2, Curve curve2)
        {
            Bezier4x3 bezier1 = edge1.m_Start == nodeEntity ? MathUtils.Invert(curve1.m_Bezier) : curve1.m_Bezier;
            Bezier4x3 bezier2 = edge2.m_End == nodeEntity ? MathUtils.Invert(curve2.m_Bezier) : curve2.m_Bezier;

            float3 startTangent = bezier1.b - bezier1.a;
            float3 endTangent = bezier2.c - bezier2.d;
            if (math.lengthsq(startTangent) <= 0.0001f)
            {
                startTangent = bezier1.d - bezier1.a;
            }

            if (math.lengthsq(endTangent) <= 0.0001f)
            {
                endTangent = bezier2.a - bezier2.d;
            }

            startTangent = math.normalizesafe(startTangent, new float3(0f, 0f, 1f));
            endTangent = math.normalizesafe(endTangent, new float3(0f, 0f, -1f));

            float lengthA = math.distance(bezier1.a, bezier1.b);
            float lengthB = math.distance(bezier2.c, bezier2.d);
            float totalDistance = math.distance(bezier1.a, bezier2.d);
            float q1Length = lengthA + totalDistance * 0.1f;
            float q2Length = lengthB + totalDistance * 0.1f;

            float3 q0 = bezier1.a;
            float3 q3 = bezier2.d;
            return new Bezier4x3(
                q0,
                q0 + startTangent * q1Length,
                q3 + endTangent * q2Length,
                q3);
        }

        private static float GetMinimumSplitDistance(float edgeLength, float roadWidth, float minEdgeLengthRange)
        {
            if (edgeLength <= 0f)
            {
                return 0.5f;
            }

            float halfWidth = roadWidth * 0.5f;
            float minEdgeLength = math.max(halfWidth, minEdgeLengthRange) + SplitLengthBuffer;
            return math.saturate(minEdgeLength / edgeLength);
        }

        private static void GetMinMaxSplitPositions(
            float edgeLength,
            float roadWidth,
            float minEdgeLengthRange,
            out float minSplit,
            out float maxSplit)
        {
            if (edgeLength <= 0f)
            {
                minSplit = 0.5f;
                maxSplit = 0.5f;
                return;
            }

            float baseMinimum = GetMinimumSplitDistance(edgeLength, roadWidth, minEdgeLengthRange);
            minSplit = baseMinimum;
            maxSplit = 1f - baseMinimum;

            if (minSplit >= maxSplit)
            {
                minSplit = 0.5f;
                maxSplit = 0.5f;
            }
        }
    }
}
