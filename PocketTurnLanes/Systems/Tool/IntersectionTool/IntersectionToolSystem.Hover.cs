using System.Collections.Generic;
using System.Text;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Tool;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using PathMethod = Game.Pathfind.PathMethod;
using NetCarLane = Game.Net.CarLane;
using NetSubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private Entity GetCurrentRaycastNode()
        {
            if (!GetRaycastResult(out Entity entity, out RaycastHit hit))
            {
                return Entity.Null;
            }

            if (IsValidIntersection(entity))
            {
                return entity;
            }

            if (!EntityManager.TryGetComponent(entity, out Edge edge))
            {
                return Entity.Null;
            }

            if (ShouldIgnorePreviewNodeMergeEdgeHit(entity, hit))
            {
                return Entity.Null;
            }

            Entity closestNode = Entity.Null;
            float closestDistance = MaxNodePickDistance;
            if (EntityManager.TryGetComponent(edge.m_Start, out Node startNode))
            {
                float distance = math.distance(startNode.m_Position.xz, hit.m_Position.xz);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestNode = edge.m_Start;
                }
            }

            if (EntityManager.TryGetComponent(edge.m_End, out Node endNode))
            {
                float distance = math.distance(endNode.m_Position.xz, hit.m_Position.xz);
                if (distance < closestDistance)
                {
                    closestNode = edge.m_End;
                }
            }

            return IsValidIntersection(closestNode) ? closestNode : Entity.Null;
        }

        private bool ShouldIgnorePreviewNodeMergeEdgeHit(Entity edgeEntity, RaycastHit hit)
        {
            if (m_PreviewNodeMergeCandidates.Count == 0)
            {
                return false;
            }

            Entity originalEdge = ResolveTempOriginalEdge(edgeEntity);

            for (int i = 0; i < m_PreviewNodeMergeCandidates.Count; i++)
            {
                NodeMergeCandidate candidate = m_PreviewNodeMergeCandidates[i];
                if (originalEdge != candidate.ShortEdge &&
                    originalEdge != candidate.ContinuationEdge)
                {
                    continue;
                }

                if (!EntityManager.TryGetComponent(candidate.Node, out Node node))
                {
                    continue;
                }

                if (IsRaycastInsideNodeMergePreviewIntersection(candidate.Node, hit, out string boundsDetail))
                {
                    return false;
                }

                Mod.LogDiagnostic($"[IntersectionTool] Raycast moved onto road-node merge fallback preview edge outside the intersection; clearing hover preview edge={FormatEntity(edgeEntity)} originalEdge={FormatEntity(originalEdge)} node={FormatEntity(candidate.Node)} shortEdge={FormatEntity(candidate.ShortEdge)} continuation={FormatEntity(candidate.ContinuationEdge)} {boundsDetail}.");
                return true;
            }

            return false;
        }

        private bool IsRaycastInsideNodeMergePreviewIntersection(
            Entity nodeEntity,
            RaycastHit hit,
            out string detail)
        {
            if (EntityManager.TryGetComponent(nodeEntity, out NodeGeometry geometry))
            {
                float3 min = geometry.m_Bounds.min;
                float3 max = geometry.m_Bounds.max;
                bool inside =
                    hit.m_Position.x >= min.x - NodeMergePreviewIntersectionBoundsMargin &&
                    hit.m_Position.x <= max.x + NodeMergePreviewIntersectionBoundsMargin &&
                    hit.m_Position.z >= min.z - NodeMergePreviewIntersectionBoundsMargin &&
                    hit.m_Position.z <= max.z + NodeMergePreviewIntersectionBoundsMargin;
                detail = $"hit=({hit.m_Position.x:0.##},{hit.m_Position.z:0.##}) boundsMin=({min.x:0.##},{min.z:0.##}) boundsMax=({max.x:0.##},{max.z:0.##}) margin={NodeMergePreviewIntersectionBoundsMargin:0.##}m insideBounds={inside}";
                return inside;
            }

            if (EntityManager.TryGetComponent(nodeEntity, out Node node))
            {
                float distance = math.distance(node.m_Position.xz, hit.m_Position.xz);
                bool inside = distance <= NodeMergePreviewEdgeExitDistance;
                detail = $"hitDistance={distance:0.##}m fallbackThreshold={NodeMergePreviewEdgeExitDistance:0.##}m insideFallback={inside}";
                return inside;
            }

            detail = "missing NodeGeometry and Node";
            return false;
        }

        private Entity ResolveTempOriginalEdge(Entity edgeEntity)
        {
            Entity originalEdge = edgeEntity;
            for (int i = 0; i < 4; i++)
            {
                if (!EntityManager.TryGetComponent(originalEdge, out Temp temp) ||
                    temp.m_Original == Entity.Null ||
                    (temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0)
                {
                    break;
                }

                originalEdge = temp.m_Original;
            }

            return originalEdge;
        }

        private bool IsValidIntersection(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
            {
                return false;
            }

            if (!EntityManager.HasComponent<Node>(entity) || EntityManager.HasComponent<Roundabout>(entity))
            {
                return false;
            }

            return EntityManager.TryGetBuffer(entity, true, out DynamicBuffer<ConnectedEdge> connectedEdges) && connectedEdges.Length >= 2;
        }

        private void UpdateHoveredIntersection(Entity entity)
        {
            if (entity == m_HoveredIntersection)
            {
                return;
            }

            m_HoveredIntersection = entity;
            m_PreviewDirty = entity != Entity.Null;
            m_PreviewReady = false;
            m_ApplyPreviewNextFrame = false;
            m_PreviewValidationPending = false;
            m_PreviewIntersection = Entity.Null;
            m_PreviewEdge = Entity.Null;

            if (entity == Entity.Null)
            {
                m_OverlaySystem.Clear();
                return;
            }

            m_OverlaySystem.Clear();

            LogIntersection(entity, "hover");
        }

        private void LogIntersection(Entity nodeEntity, string reason)
        {
            if (!EntityManager.Exists(nodeEntity))
            {
                return;
            }

            StringBuilder builder = new StringBuilder();
            builder.Append($"[IntersectionTool] {reason}: node={FormatEntity(nodeEntity)}");

            if (EntityManager.TryGetComponent(nodeEntity, out Node node))
            {
                builder.Append($" position=({node.m_Position.x:0.##}, {node.m_Position.y:0.##}, {node.m_Position.z:0.##})");
            }

            if (EntityManager.TryGetComponent(nodeEntity, out TrafficLights trafficLights))
            {
                builder.Append($" trafficLights={trafficLights.m_Flags}");
            }

            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                builder.Append(" connectedEdges=0");
                Mod.LogDiagnostic(builder.ToString());
                return;
            }

            builder.Append($" connectedEdges={connectedEdges.Length}");

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                builder.AppendLine();
                builder.Append("  ");
                builder.Append(i);
                builder.Append(": edge=");
                builder.Append(FormatEntity(edgeEntity));

                if (EntityManager.TryGetComponent(edgeEntity, out Edge edge))
                {
                    string nodeSide = edge.m_Start == nodeEntity ? "start" : edge.m_End == nodeEntity ? "end" : "unknown";
                    builder.Append($" nodeSide={nodeSide}");
                }

                builder.Append(" prefab=");
                builder.Append(GetPrefabName(edgeEntity));
            }

            Mod.LogDiagnostic(builder.ToString());
        }

        private JobHandle QueueSplitPreview(Entity nodeEntity, JobHandle inputDeps)
        {
            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                ResetPreviewState();
                Mod.LogDiagnostic($"[IntersectionTool] Cannot preview split {FormatEntity(nodeEntity)}: no ConnectedEdge buffer.");
                return inputDeps;
            }

            applyMode = ApplyMode.Clear;
            inputDeps = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
            m_PreviewCandidates.Clear();
            m_NextPreviewCandidates.Clear();
            m_PreviewNodeMergeCandidates.Clear();
            m_QueuedReplacementCandidates.Clear();
            m_HasReplacementPreviewDefinitions = false;
            m_HasShortEdgeReplacementPreviewDefinitions = false;
            m_NormalReplacementPreviewDefinitionsQueued = false;
            m_ShortEdgeReplacementPreviewAttempted = false;
            m_NodeMergeDefinitionsReadyForApply = false;
            m_ShortEdgeReplacementPreviewQueuedCount = 0;

            int splitQueuedCount = 0;
            int shortEdgeReplacementPreviewQueuedCount = 0;
            int shortEdgePreviewDegradedCount = 0;
            int nodeMergeApplyCandidateCount = 0;
            int skippedCount = 0;
            JobHandle result = inputDeps;
            Entity lastQueuedEdge = Entity.Null;

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (!IsRoadEdge(edgeEntity))
                {
                    skippedCount++;
                    continue;
                }

                if (IsHighwayRoadEdge(edgeEntity, out string highwayDetail))
                {
                    skippedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: highway roads are excluded from selection and replacement matching. {highwayDetail}");
                    continue;
                }

                if (!HasAnyMotorRoadLane(edgeEntity, out string motorLaneDetail))
                {
                    skippedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: road has no automotive road lanes and is excluded from selection. {motorLaneDetail}");
                    continue;
                }

                if (IsBridgeRoadEdge(edgeEntity, out string bridgeDetail))
                {
                    skippedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: bridge road prefabs are excluded from selection and replacement matching. {bridgeDetail}");
                    continue;
                }

                if (!HasIncomingCarLane(nodeEntity, edgeEntity, connectedEdges))
                {
                    skippedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: no automotive road lane entering node {FormatEntity(nodeEntity)}.");
                    continue;
                }

                if (!ApproachNeedsPocketLane(nodeEntity, edgeEntity, connectedEdges, out string demandReason))
                {
                    skippedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: no dedicated turn lane demand at node {FormatEntity(nodeEntity)}. {demandReason}");
                    continue;
                }

                if (TryBuildSplitDefinitionRequest(
                        nodeEntity,
                        edgeEntity,
                        out SplitDefinitionRequest request,
                        out float splitPosition,
                        out float splitDistance,
                        out float intersectionDistance,
                        out float pocketDistance,
                        out float targetDistance,
                        out float targetPocketLength))
                {
                    if (!TryFindPocketLaneReplacementPrefab(
                            nodeEntity,
                            edgeEntity,
                            out ReplacementPrefabMatch prefabMatch))
                    {
                        skippedCount++;
                        continue;
                    }

                    JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                    {
                        Request = request,
                        ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                    }.Schedule(result);

                    m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);

                    splitQueuedCount++;
                    m_PreviewCandidates.Add(new SplitCandidate
                    {
                        Node = nodeEntity,
                        Edge = edgeEntity,
                        SourcePrefab = request.Prefab,
                        TargetPrefab = prefabMatch.Prefab,
                        InvertTarget = prefabMatch.Invert,
                        HasTargetUpgrade = prefabMatch.HasTargetUpgrade,
                        TargetUpgrade = prefabMatch.TargetUpgrade,
                        CurvePosition = splitPosition,
                        HitPosition = request.HitPosition,
                        TargetDistance = targetDistance,
                        TargetPocketLength = targetPocketLength,
                        SplitDistance = splitDistance,
                        IntersectionDistance = intersectionDistance,
                        PocketDistance = pocketDistance,
                        OriginalForwardLanes = prefabMatch.OriginalCounts.Forward,
                        OriginalBackwardLanes = prefabMatch.OriginalCounts.Backward,
                        TargetForwardLanes = prefabMatch.TargetCounts.Forward,
                        TargetBackwardLanes = prefabMatch.TargetCounts.Backward,
                        Attempt = 0
                    });
                    Mod.LogDiagnostic($"[IntersectionTool] Prepared split preview edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} split={splitPosition:0.###} requestedPocket={targetPocketLength:0.##}m target={targetDistance:0.##}m distance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m replacement={GetPrefabNameFromPrefab(prefabMatch.Prefab)} orientation={(prefabMatch.Invert ? "reversed" : "direct")} lanes={prefabMatch.OriginalCounts}->{prefabMatch.TargetCounts} frame={UnityEngine.Time.frameCount}.");
                    result = createDefinitionJobHandle;
                    lastQueuedEdge = edgeEntity;
                }
                else
                {
                    if (!TryFindPocketLaneReplacementPrefab(
                            nodeEntity,
                            edgeEntity,
                            out ReplacementPrefabMatch prefabMatch))
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!TryBuildNodeMergeCandidate(
                            nodeEntity,
                            edgeEntity,
                            prefabMatch,
                            out NodeMergeCandidate mergeCandidate))
                    {
                        skippedCount++;
                        continue;
                    }

                    m_PreviewNodeMergeCandidates.Add(mergeCandidate);
                    nodeMergeApplyCandidateCount++;
                    lastQueuedEdge = edgeEntity;
                    Mod.LogDiagnostic($"[IntersectionTool] Road-node merge fallback candidate found; apply candidate is queued shortEdge={FormatEntity(edgeEntity)} removableNode={FormatEntity(mergeCandidate.RemovableNode)} continuation={FormatEntity(mergeCandidate.ContinuationEdge)} farNode={FormatEntity(mergeCandidate.FarNode)} mode={mergeCandidate.Mode} laneRepair={mergeCandidate.LaneRepairMode} sourcePrefab={GetPrefabNameFromPrefab(mergeCandidate.SourcePrefab)} mergePrefab={GetPrefabNameFromPrefab(mergeCandidate.MergeRequest.Prefab)} targetPrefab={GetPrefabNameFromPrefab(mergeCandidate.TargetPrefab)} previewOrientation={(mergeCandidate.InvertTarget ? "reversed" : "direct")} postMergeOrientation={(mergeCandidate.PostMergeInvertTarget ? "reversed" : "direct")} shortLength={mergeCandidate.ShortEdgeLength:0.##}m continuationLength={mergeCandidate.ContinuationEdgeLength:0.##}m mergedLength={mergeCandidate.MergedLength:0.##}m nearMargin={mergeCandidate.ExpectedIntersectionDistance:0.##}m farMargin={mergeCandidate.ExpectedFarIntersectionDistance:0.##}m usable={mergeCandidate.ExpectedUsableLength:0.##}m expectedSplit={mergeCandidate.ExpectedSplitDistance:0.##}m expectedPocket={mergeCandidate.ExpectedPocketDistance:0.##}m lanes={mergeCandidate.OriginalForwardLanes}/{mergeCandidate.OriginalBackwardLanes}->{mergeCandidate.TargetForwardLanes}/{mergeCandidate.TargetBackwardLanes} frame={UnityEngine.Time.frameCount} singleShortEdgeReplacementPreview=pending-sync.");
                }
            }

            if (nodeMergeApplyCandidateCount > 0)
            {
                if (splitQueuedCount == 0)
                {
                    QueueShortEdgeReplacementPreviews(
                        ref result,
                        "no normal split preview definitions were queued, so short-edge fallback preview can start immediately",
                        out shortEdgeReplacementPreviewQueuedCount,
                        out shortEdgePreviewDegradedCount);
                }
                else
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Deferring {nodeMergeApplyCandidateCount} short-edge replacement hover preview definition(s) until normal split preview replacement definitions can be queued in the same tool frame splitDefinitions={splitQueuedCount}.");
                }
            }

            int queuedCount = splitQueuedCount + nodeMergeApplyCandidateCount;
            if (queuedCount > 0)
            {
                bool hasPendingShortEdgeReplacementPreview = shortEdgeReplacementPreviewQueuedCount > 0;
                m_PreviewIntersection = nodeEntity;
                m_PreviewEdge = lastQueuedEdge;
                m_PreviewEdgeCount = queuedCount;
                m_PreviewReady = nodeMergeApplyCandidateCount > 0 && splitQueuedCount == 0 && !hasPendingShortEdgeReplacementPreview;
                m_PreviewValidationPending = splitQueuedCount > 0 || hasPendingShortEdgeReplacementPreview;
                m_PreviewDirty = false;
                m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                if (splitQueuedCount > 0)
                {
                    ShowPreviewOverlay(nodeEntity);
                }
                else
                {
                    m_OverlaySystem.Clear();
                }

                Mod.LogDiagnostic($"[IntersectionTool] Created preview/apply state around node {FormatEntity(nodeEntity)} splitDefinitions={splitQueuedCount} shortEdgeReplacementPreviewCandidates={shortEdgeReplacementPreviewQueuedCount} shortEdgeReplacementPreviewDefinitions={m_ShortEdgeReplacementPreviewQueuedCount} shortEdgePreviewDegraded={shortEdgePreviewDegradedCount} roadNodeMergeApplyCandidates={nodeMergeApplyCandidateCount}; skipped {skippedCount} edge(s).");
            }
            else
            {
                applyMode = ApplyMode.None;
                MarkNoSplitPreviewReady(nodeEntity);
                Mod.LogDiagnostic($"[IntersectionTool] No entering road edges were eligible to preview around node {FormatEntity(nodeEntity)}.");
            }

            return result;
        }

        private void ShowPreviewOverlay(Entity nodeEntity)
        {
            if (EntityManager.TryGetComponent(nodeEntity, out NodeGeometry geometry))
            {
                m_OverlaySystem.ShowBounds(geometry.m_Bounds);
                return;
            }

            m_OverlaySystem.Clear();
        }

        private bool IsRoadEdge(Entity edgeEntity)
        {
            return edgeEntity != Entity.Null &&
                   EntityManager.Exists(edgeEntity) &&
                   EntityManager.HasComponent<Edge>(edgeEntity) &&
                   EntityManager.HasComponent<Road>(edgeEntity);
        }

        private bool HasAnyMotorRoadLane(Entity edgeEntity, out string detail)
        {
            if (!EntityManager.TryGetBuffer(edgeEntity, true, out DynamicBuffer<NetSubLane> edgeSubLanes))
            {
                detail = "edgeSubLanes=missing";
                return false;
            }

            int subLaneCount = edgeSubLanes.Length;
            int carLaneComponentCount = 0;
            int motorRoadLaneCount = 0;
            int bicycleOnlyCount = 0;
            int nonRoadMethodCount = 0;
            int missingPrefabRefCount = 0;
            int missingCarLaneDataCount = 0;
            for (int i = 0; i < edgeSubLanes.Length; i++)
            {
                Entity laneEntity = edgeSubLanes[i].m_SubLane;
                if (laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity))
                {
                    continue;
                }

                carLaneComponentCount++;
                PathMethod pathMethods = edgeSubLanes[i].m_PathMethods;
                if ((pathMethods & PathMethod.Road) == 0)
                {
                    nonRoadMethodCount++;
                    continue;
                }

                if (!EntityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef))
                {
                    missingPrefabRefCount++;
                    continue;
                }

                if (!EntityManager.TryGetComponent(prefabRef.m_Prefab, out CarLaneData carLaneData))
                {
                    missingCarLaneDataCount++;
                    continue;
                }

                if ((carLaneData.m_RoadTypes & RoadTypes.Car) != 0)
                {
                    motorRoadLaneCount++;
                }
                else if ((carLaneData.m_RoadTypes & RoadTypes.Bicycle) != 0)
                {
                    bicycleOnlyCount++;
                }
            }

            detail = $"edgeSubLanes={subLaneCount} carLaneComponents={carLaneComponentCount} motorRoadLanes={motorRoadLaneCount} bicycleOnlyCarLanes={bicycleOnlyCount} nonRoadMethod={nonRoadMethodCount} missingPrefabRef={missingPrefabRefCount} missingCarLaneData={missingCarLaneDataCount}";
            return motorRoadLaneCount > 0;
        }

        private bool HasIncomingCarLane(Entity nodeEntity, Entity edgeEntity, DynamicBuffer<ConnectedEdge> connectedEdges)
        {
            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<NetSubLane> nodeSubLanes))
            {
                return false;
            }

            for (int i = 0; i < nodeSubLanes.Length; i++)
            {
                Entity nodeLaneEntity = nodeSubLanes[i].m_SubLane;
                if (!IsMotorRoadLane(nodeLaneEntity, nodeSubLanes[i].m_PathMethods, out _) ||
                    !EntityManager.TryGetComponent(nodeLaneEntity, out Lane nodeLane))
                {
                    continue;
                }

                if (TryGetSourceEdgeFromNodeLane(nodeLane, connectedEdges, out Entity sourceEdge) && sourceEdge == edgeEntity)
                {
                    return true;
                }
            }

            return false;
        }

        private bool ApproachNeedsPocketLane(
            Entity nodeEntity,
            Entity edgeEntity,
            DynamicBuffer<ConnectedEdge> connectedEdges,
            out string reason)
        {
            int roadEdgeCount = CountRoadConnectedEdges(connectedEdges);
            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<NetSubLane> nodeSubLanes))
            {
                reason = $"roadEdges={roadEdgeCount}; missing node SubLane buffer, skipping because demand cannot be diagnosed from connector allocation.";
                return false;
            }

            Dictionary<int, ApproachLaneUsage> laneUsages = new Dictionary<int, ApproachLaneUsage>();
            Dictionary<int, HashSet<ApproachMovementKey>> laneMovementKeys = new Dictionary<int, HashSet<ApproachMovementKey>>();
            StringBuilder connectorSummary = new StringBuilder();
            int connectorCount = 0;
            int ignoredConnectorCount = 0;

            for (int i = 0; i < nodeSubLanes.Length; i++)
            {
                NetSubLane subLane = nodeSubLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if (laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !IsMotorRoadLane(laneEntity, subLane.m_PathMethods, out _) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, nodeEntity, lane, out Entity sourceEdge, out Entity targetEdge))
                {
                    continue;
                }

                if (sourceEdge != edgeEntity)
                {
                    continue;
                }

                if (targetEdge == edgeEntity || !IsRoadEdge(targetEdge))
                {
                    ignoredConnectorCount++;
                    continue;
                }

                connectorCount++;
                int sourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff;
                int targetLaneIndex = lane.m_EndNode.GetLaneIndex() & 0xff;
                NetCarLane carLane = EntityManager.GetComponentData<NetCarLane>(laneEntity);
                ApproachMovement movement = ClassifyCenterMovement(nodeEntity, edgeEntity, targetEdge, carLane.m_Flags);
                ApproachMovementKey movementKey = new ApproachMovementKey(movement, targetEdge);

                if (!laneUsages.TryGetValue(sourceLaneIndex, out ApproachLaneUsage usage))
                {
                    usage = new ApproachLaneUsage
                    {
                        LaneIndex = sourceLaneIndex
                    };
                }

                usage.Add(movement);
                laneUsages[sourceLaneIndex] = usage;

                if (!laneMovementKeys.TryGetValue(sourceLaneIndex, out HashSet<ApproachMovementKey> movementKeys))
                {
                    movementKeys = new HashSet<ApproachMovementKey>();
                    laneMovementKeys[sourceLaneIndex] = movementKeys;
                }

                movementKeys.Add(movementKey);

                if (connectorSummary.Length > 0)
                {
                    connectorSummary.Append(",");
                }

                connectorSummary.Append(sourceLaneIndex);
                connectorSummary.Append("->");
                connectorSummary.Append(targetLaneIndex);
                connectorSummary.Append("/");
                connectorSummary.Append(movement);
                connectorSummary.Append("/");
                connectorSummary.Append(FormatEntity(targetEdge));
                connectorSummary.Append("/");
                connectorSummary.Append(FormatEntity(laneEntity));
            }

            if (connectorCount == 0)
            {
                reason = $"roadEdges={roadEdgeCount}; no center connector evidence for source edge, skipping instead of guessing from road or lane counts.";
                return false;
            }

            HashSet<ApproachMovementKey> dedicatedTurnKeys = new HashSet<ApproachMovementKey>();
            HashSet<ApproachMovementKey> mixedTurnKeys = new HashSet<ApproachMovementKey>();
            HashSet<ApproachMovementKey> allKeys = new HashSet<ApproachMovementKey>();
            int mixedLaneCount = 0;
            int ambiguousLaneCount = 0;

            foreach (KeyValuePair<int, HashSet<ApproachMovementKey>> pair in laneMovementKeys)
            {
                bool laneMixed = pair.Value.Count > 1;
                if (laneMixed)
                {
                    mixedLaneCount++;
                }

                foreach (ApproachMovementKey key in pair.Value)
                {
                    allKeys.Add(key);
                    if (key.Movement == ApproachMovement.Ambiguous)
                    {
                        ambiguousLaneCount++;
                    }

                    if (!key.IsTurn)
                    {
                        continue;
                    }

                    if (laneMixed)
                    {
                        mixedTurnKeys.Add(key);
                    }
                    else
                    {
                        dedicatedTurnKeys.Add(key);
                    }
                }
            }

            bool needsLeft = false;
            bool needsRight = false;
            List<ApproachMovementKey> unmetTurnKeys = new List<ApproachMovementKey>();
            foreach (ApproachMovementKey key in mixedTurnKeys)
            {
                if (dedicatedTurnKeys.Contains(key))
                {
                    continue;
                }

                unmetTurnKeys.Add(key);
                if (key.Movement == ApproachMovement.Left)
                {
                    needsLeft = true;
                }
                else if (key.Movement == ApproachMovement.Right)
                {
                    needsRight = true;
                }
            }

            bool needsPocket = needsLeft || needsRight;
            string usageSummary = FormatApproachLaneUsages(laneUsages);
            string keySummary = FormatApproachMovementKeys(laneMovementKeys);
            string dedicatedSummary = FormatApproachMovementKeySet(dedicatedTurnKeys);
            string mixedSummary = FormatApproachMovementKeySet(mixedTurnKeys);
            string unmetSummary = FormatApproachMovementKeySet(unmetTurnKeys);
            string diagnostics = $"roadEdges={roadEdgeCount} connectors={connectorCount} ignored={ignoredConnectorCount} lanes={laneUsages.Count} distinctTargets={allKeys.Count} mixedLanes={mixedLaneCount} ambiguousKeys={ambiguousLaneCount} dedicatedTurnKeys=[{dedicatedSummary}] mixedTurnKeys=[{mixedSummary}] unmetTurnKeys=[{unmetSummary}] needsLeft={needsLeft} needsRight={needsRight} usage=[{usageSummary}] targetUsage=[{keySummary}] connectors=[{connectorSummary}]";

            if (!needsPocket)
            {
                reason = $"{diagnostics}; existing connector allocation is already turn-dedicated or has no split turn demand.";
                return false;
            }

            reason = diagnostics;
            Mod.LogDiagnostic($"[IntersectionTool] Edge {FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} has dedicated turn lane demand at node {FormatEntity(nodeEntity)}. {reason}");
            return true;
        }

        private int CountRoadConnectedEdges(DynamicBuffer<ConnectedEdge> connectedEdges)
        {
            int count = 0;
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                if (IsRoadEdge(connectedEdges[i].m_Edge))
                {
                    count++;
                }
            }

            return count;
        }

        private ApproachMovement ClassifyCenterMovement(Entity intersectionNode, Entity sourceEdge, Entity targetEdge, CarLaneFlags flags)
        {
            bool leftFlag = (flags & CarLaneFlags.TurnLeft) != 0;
            bool rightFlag = (flags & CarLaneFlags.TurnRight) != 0;
            if (leftFlag && !rightFlag)
            {
                return ApproachMovement.Left;
            }

            if (rightFlag && !leftFlag)
            {
                return ApproachMovement.Right;
            }

            if (!NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, sourceEdge, intersectionNode, out float2 sourceOutward) ||
                !NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, targetEdge, intersectionNode, out float2 targetOutward))
            {
                return ApproachMovement.Ambiguous;
            }

            float2 incoming = -sourceOutward;
            float cross = NetTopologyHelpers.Cross(incoming, targetOutward);
            float dot = math.dot(incoming, targetOutward);
            if (math.abs(cross) < 0.25f)
            {
                return dot > 0f ? ApproachMovement.Straight : ApproachMovement.Ambiguous;
            }

            return cross > 0f ? ApproachMovement.Left : ApproachMovement.Right;
        }

        private static string FormatApproachLaneUsages(Dictionary<int, ApproachLaneUsage> laneUsages)
        {
            if (laneUsages.Count == 0)
            {
                return "<none>";
            }

            List<ApproachLaneUsage> usages = new List<ApproachLaneUsage>(laneUsages.Values);
            usages.Sort((a, b) => a.LaneIndex.CompareTo(b.LaneIndex));

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < usages.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                ApproachLaneUsage usage = usages[i];
                builder.Append("lane");
                builder.Append(usage.LaneIndex);
                builder.Append(":S");
                builder.Append(usage.Straight);
                builder.Append("/L");
                builder.Append(usage.Left);
                builder.Append("/R");
                builder.Append(usage.Right);
                builder.Append("/A");
                builder.Append(usage.Ambiguous);
            }

            return builder.ToString();
        }

        private static string FormatApproachMovementKeys(Dictionary<int, HashSet<ApproachMovementKey>> laneMovementKeys)
        {
            if (laneMovementKeys.Count == 0)
            {
                return "<none>";
            }

            List<int> laneIndexes = new List<int>(laneMovementKeys.Keys);
            laneIndexes.Sort();

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < laneIndexes.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                int laneIndex = laneIndexes[i];
                builder.Append("lane");
                builder.Append(laneIndex);
                builder.Append(":");
                builder.Append(FormatApproachMovementKeySet(laneMovementKeys[laneIndex]));
            }

            return builder.ToString();
        }

        private static string FormatApproachMovementKeySet(IEnumerable<ApproachMovementKey> keys)
        {
            if (keys == null)
            {
                return "<none>";
            }

            List<ApproachMovementKey> sortedKeys = new List<ApproachMovementKey>(keys);
            if (sortedKeys.Count == 0)
            {
                return "<none>";
            }

            sortedKeys.Sort((a, b) =>
            {
                int movementCompare = a.Movement.CompareTo(b.Movement);
                if (movementCompare != 0)
                {
                    return movementCompare;
                }

                int indexCompare = a.TargetEdge.Index.CompareTo(b.TargetEdge.Index);
                return indexCompare != 0 ? indexCompare : a.TargetEdge.Version.CompareTo(b.TargetEdge.Version);
            });

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < sortedKeys.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("|");
                }

                ApproachMovementKey key = sortedKeys[i];
                builder.Append(key.Movement);
                builder.Append("->");
                builder.Append(FormatEntity(key.TargetEdge));
            }

            return builder.ToString();
        }

        private bool TryGetSourceEdgeFromNodeLane(Lane nodeLane, DynamicBuffer<ConnectedEdge> connectedEdges, out Entity sourceEdge)
        {
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity connectedEdge = connectedEdges[i].m_Edge;
                if (!EntityManager.TryGetBuffer(connectedEdge, true, out DynamicBuffer<NetSubLane> edgeSubLanes))
                {
                    continue;
                }

                for (int j = 0; j < edgeSubLanes.Length; j++)
                {
                    Entity edgeLaneEntity = edgeSubLanes[j].m_SubLane;
                    if (!IsMotorRoadLane(edgeLaneEntity, edgeSubLanes[j].m_PathMethods, out _) ||
                        !EntityManager.TryGetComponent(edgeLaneEntity, out Lane edgeLane))
                    {
                        continue;
                    }

                    if (nodeLane.m_StartNode.Equals(edgeLane.m_EndNode) || nodeLane.m_StartNode.Equals(edgeLane.m_StartNode))
                    {
                        sourceEdge = connectedEdge;
                        return true;
                    }
                }
            }

            sourceEdge = Entity.Null;
            return false;
        }

        private bool IsMotorRoadLane(Entity laneEntity, PathMethod pathMethods, out string detail)
        {
            if (laneEntity == Entity.Null ||
                !EntityManager.Exists(laneEntity) ||
                EntityManager.HasComponent<Deleted>(laneEntity))
            {
                detail = "lane=missing-or-deleted";
                return false;
            }

            if ((pathMethods & PathMethod.Road) == 0)
            {
                detail = $"pathMethods={pathMethods} missingRoadMethod";
                return false;
            }

            if (!EntityManager.HasComponent<NetCarLane>(laneEntity))
            {
                detail = "netCarLane=missing";
                return false;
            }

            if (!EntityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef))
            {
                detail = "prefabRef=missing";
                return false;
            }

            if (!EntityManager.TryGetComponent(prefabRef.m_Prefab, out CarLaneData carLaneData))
            {
                detail = $"lanePrefab={FormatEntity(prefabRef.m_Prefab)} carLaneData=missing";
                return false;
            }

            bool isMotorRoadLane = (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
            detail = $"lanePrefab={FormatEntity(prefabRef.m_Prefab)} roadTypes={carLaneData.m_RoadTypes} pathMethods={pathMethods} isMotorRoadLane={isMotorRoadLane}";
            return isMotorRoadLane;
        }
    }
}
