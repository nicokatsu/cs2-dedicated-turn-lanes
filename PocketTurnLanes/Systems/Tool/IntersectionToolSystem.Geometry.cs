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
            float targetPocketLength = PocketLaneLength)
        {
            request = default;
            splitPosition = 0f;
            splitDistance = 0f;
            intersectionDistance = 0f;
            pocketDistance = 0f;
            targetDistance = 0f;

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
            float maxDistanceFromNode = curve.m_Length * 0.5f;
            if (maxDistanceFromNode - intersectionDistance < MinimumPocketLaneLength)
            {
                Mod.log.Info($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: not enough room for an aligned pocket lane (length={curve.m_Length:0.##}m intersection={intersectionDistance:0.##}m).");
                return false;
            }

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

            if (!TryGetMergeableRoadNode(
                    nodeEntity,
                    edgeEntity,
                    edge,
                    prefabRef.m_Prefab,
                    out Entity removableNode,
                    out Entity continuationEdge,
                    out Edge continuationEdgeData,
                    out Curve continuationCurve,
                    out Entity farNode))
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
                    geometryData,
                    out float splitPosition,
                    out float splitDistance,
                    out float intersectionDistance,
                    out float pocketDistance,
                    out float targetDistance,
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
                InvertTarget = prefabMatch.Invert,
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

            Mod.log.Info($"[IntersectionTool] Road-node merge fallback accepted shortEdge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)} farNode={FormatEntity(farNode)} sourcePrefab={GetPrefabNameFromPrefab(prefabRef.m_Prefab)} shortLength={curve.m_Length:0.##}m continuationLength={continuationCurve.m_Length:0.##}m mergedLength={mergedLength:0.##}m split={splitPosition:0.###} splitDistance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m target={targetDistance:0.##}m. Safety: removable node has exactly two connected road edges and both prefabs match.");
            return true;
        }

        private bool TryGetMergeableRoadNode(
            Entity intersectionNode,
            Entity edgeEntity,
            Edge edge,
            Entity sourcePrefab,
            out Entity removableNode,
            out Entity continuationEdge,
            out Edge continuationEdgeData,
            out Curve continuationCurve,
            out Entity farNode)
        {
            removableNode = Entity.Null;
            continuationEdge = Entity.Null;
            continuationEdgeData = default;
            continuationCurve = default;
            farNode = Entity.Null;

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

            if (EntityManager.HasComponent<Owner>(edgeEntity) ||
                EntityManager.HasComponent<Owner>(continuationEdge))
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} continuation={FormatEntity(continuationEdge)}: owned sub-net edges are not merged automatically.");
                return false;
            }

            if (continuationPrefabRef.m_Prefab != sourcePrefab)
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(edgeEntity)} removableNode={FormatEntity(removableNode)} continuation={FormatEntity(continuationEdge)}: prefab mismatch source={GetPrefabNameFromPrefab(sourcePrefab)} continuation={GetPrefabNameFromPrefab(continuationPrefabRef.m_Prefab)}.");
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
            NetGeometryData geometryData,
            out float splitPosition,
            out float splitDistance,
            out float intersectionDistance,
            out float pocketDistance,
            out float targetDistance,
            out float3 hitPosition,
            float targetPocketLength = PocketLaneLength)
        {
            splitPosition = 0f;
            splitDistance = 0f;
            intersectionDistance = 0f;
            pocketDistance = 0f;
            targetDistance = 0f;
            hitPosition = default;

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
            float maxDistanceFromNode = mergedLength * 0.5f;
            if (maxDistanceFromNode - intersectionDistance < MinimumPocketLaneLength)
            {
                Mod.log.Info($"[IntersectionTool] Road-node merge fallback rejected edge={FormatEntity(sourceEdgeEntity)}: merged edge still has no room for an aligned pocket lane (mergedLength={mergedLength:0.##}m intersection={intersectionDistance:0.##}m).");
                return false;
            }

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

            float alignedPocketLength = math.ceil(targetPocketLength / SplitGridSize) * SplitGridSize;
            if (alignedPocketLength <= maxPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            alignedPocketLength = math.floor(maxPocketLength / SplitGridSize) * SplitGridSize;
            if (alignedPocketLength >= minimumPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            return maximumDistance;
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
