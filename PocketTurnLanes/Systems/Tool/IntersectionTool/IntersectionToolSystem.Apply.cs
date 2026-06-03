using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Tool.PrefabMatching;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using static PocketTurnLanes.Tool.SplitGeometry.SplitGeometryMath;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private void SetVanillaMutationSystemsEnabled(bool enabled)
        {
            if (m_ValidationSystem != null)
            {
                m_ValidationSystem.Enabled = enabled;
            }

            if (m_NodeReductionSystem != null)
            {
                m_NodeReductionSystem.Enabled = enabled;
            }
        }

        private void EnsureVanillaMutationSystemsDisabled()
        {
            bool validationWasEnabled = m_ValidationSystem != null && m_ValidationSystem.Enabled;
            bool nodeReductionWasEnabled = m_NodeReductionSystem != null && m_NodeReductionSystem.Enabled;
            if (!validationWasEnabled && !nodeReductionWasEnabled)
            {
                return;
            }

            SetVanillaMutationSystemsEnabled(false);
            Mod.LogEssential($"[IntersectionTool] Vanilla mutation systems were enabled while the tool was active; disabled again to keep split/replacement previews isolated validationWasEnabled={validationWasEnabled} nodeReductionWasEnabled={nodeReductionWasEnabled}.");
        }

        private void CaptureAppliedCandidates()
        {
            m_AppliedCandidates.Clear();
            m_AppliedCandidates.AddRange(m_PreviewCandidates);

            m_VerifyAppliedSplits = m_AppliedCandidates.Count > 0;
        }

        private void CaptureAppliedReplacementCandidates()
        {
            m_AppliedReplacementCandidates.Clear();
            m_AppliedReplacementCandidates.AddRange(m_QueuedReplacementCandidates);
            m_QueuedReplacementCandidates.Clear();
            m_VerifyAppliedReplacements = m_AppliedReplacementCandidates.Count > 0;
        }

        private void CaptureAppliedNodeMergeCandidates()
        {
            m_AppliedNodeMergeCandidates.Clear();
            for (int i = 0; i < m_PreviewNodeMergeCandidates.Count; i++)
            {
                NodeMergeCandidate candidate = m_PreviewNodeMergeCandidates[i];
                if (candidate.Mode != NodeMergeMode.ShortEdgeReplacementOnly)
                {
                    m_AppliedNodeMergeCandidates.Add(candidate);
                }
            }

            m_VerifyAppliedNodeMerges = m_AppliedNodeMergeCandidates.Count > 0;
        }

        private bool TryQueueFailedSplitRetries(ref JobHandle result)
        {
            if (!m_VerifyAppliedSplits)
            {
                return false;
            }

            m_VerifyAppliedSplits = false;
            m_PreviewCandidates.Clear();
            m_QueuedReplacementCandidates.Clear();

            int succeededCount = 0;
            int retryCount = 0;
            int replacementCount = 0;
            int exhaustedCount = 0;
            Entity retryNode = Entity.Null;
            Entity lastRetryEdge = Entity.Null;

            for (int i = 0; i < m_AppliedCandidates.Count; i++)
            {
                SplitCandidate candidate = m_AppliedCandidates[i];
                bool replacementQueued = TryQueuePocketLaneReplacement(candidate, ref result, out bool foundPocketEdge);
                if (foundPocketEdge || !IsEdgeStillConnectedToNode(candidate.Node, candidate.Edge))
                {
                    succeededCount++;
                    if (replacementQueued)
                    {
                        replacementCount++;
                    }

                    continue;
                }

                if (candidate.LaneRepairMode == SplitLaneConnectionRepairMode.BalancedOppositeTarget)
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Balanced road-node merge split did not generate a pocket edge; no normal retry will be attempted because this branch must keep the two-margin half split original={FormatEntity(candidate.Edge)} node={FormatEntity(candidate.Node)} farNode={FormatEntity(candidate.FarNode)} splitDistance={candidate.SplitDistance:0.##}m targetPocket={candidate.TargetPocketLength:0.##}m.");
                    continue;
                }

                if (candidate.Attempt >= MaxSplitRetryAttempts)
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Split still failed edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} after {candidate.Attempt} retry attempt(s); leaving it unchanged.");
                    continue;
                }

                int nextAttempt = candidate.Attempt + 1;
                float retryPocketLength = candidate.TargetPocketLength + SplitRetryStep;
                string retryDetail = $"mode=fixed-step step={SplitRetryStep:0.##}m previousPocket={candidate.TargetPocketLength:0.##}m";

                if (!TryBuildSplitDefinitionRequest(
                        candidate.Node,
                        candidate.Edge,
                        out SplitDefinitionRequest request,
                        out float splitPosition,
                        out float splitDistance,
                        out float intersectionDistance,
                        out float pocketDistance,
                        out float targetDistance,
                        out float targetPocketLength,
                        retryPocketLength))
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Retry split cannot be prepared edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} {retryDetail} requestedPocket={targetPocketLength:0.##}m requestedBeforeCap={retryPocketLength:0.##}m.");
                    continue;
                }

                if (splitDistance < candidate.SplitDistance + MinimumRetryProgress)
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Retry split cannot move far enough edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} {retryDetail} previous={candidate.SplitDistance:0.##}m next={splitDistance:0.##}m.");
                    continue;
                }

                JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                {
                    Request = request,
                    ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                }.Schedule(result);

                m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);

                m_PreviewCandidates.Add(new SplitCandidate
                {
                    Node = candidate.Node,
                    FarNode = candidate.FarNode,
                    Edge = candidate.Edge,
                    SourcePrefab = candidate.SourcePrefab,
                    TargetPrefab = candidate.TargetPrefab,
                    LaneRepairMode = candidate.LaneRepairMode,
                    InvertTarget = candidate.InvertTarget,
                    HasTargetUpgrade = candidate.HasTargetUpgrade,
                    TargetUpgrade = candidate.TargetUpgrade,
                    CurvePosition = splitPosition,
                    HitPosition = request.HitPosition,
                    TargetDistance = targetDistance,
                    TargetPocketLength = targetPocketLength,
                    SplitDistance = splitDistance,
                    IntersectionDistance = intersectionDistance,
                    PocketDistance = pocketDistance,
                    OriginalForwardLanes = candidate.OriginalForwardLanes,
                    OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                    TargetForwardLanes = candidate.TargetForwardLanes,
                    TargetBackwardLanes = candidate.TargetBackwardLanes,
                    Attempt = nextAttempt,
                    FarIntersectionSnapshot = candidate.FarIntersectionSnapshot
                });

                retryCount++;
                retryNode = candidate.Node;
                lastRetryEdge = candidate.Edge;
                result = createDefinitionJobHandle;
                Mod.LogDiagnostic($"[IntersectionTool] Retrying failed split edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={nextAttempt} {retryDetail} requestedPocket={targetPocketLength:0.##}m requestedBeforeCap={retryPocketLength:0.##}m previousPocket={candidate.TargetPocketLength:0.##}m target={targetDistance:0.##}m split={splitPosition:0.###} distance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m.");
            }

            m_AppliedCandidates.Clear();
            if (retryCount == 0)
            {
                if (replacementCount > 0)
                {
                    m_PreviewIntersection = m_QueuedReplacementCandidates[0].Node;
                    m_PreviewEdge = Entity.Null;
                    m_PreviewEdgeCount = 0;
                    m_PreviewReady = true;
                    m_PreviewDirty = false;
                    m_ApplyPreviewNextFrame = false;
                    m_ApplyReplacementNextFrame = true;
                    m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                    Mod.LogDiagnostic($"[IntersectionTool] Split verification complete: succeeded={succeededCount}, retryQueued=0, exhausted={exhaustedCount}, replacementQueued={replacementCount}. Replacement definitions will be applied on the next tool frame.");
                    return true;
                }

                Mod.LogDiagnostic($"[IntersectionTool] Split verification complete: succeeded={succeededCount}, retryQueued=0, exhausted={exhaustedCount}, replacementQueued=0.");
                return false;
            }

            m_PreviewIntersection = retryNode;
            m_PreviewEdge = lastRetryEdge;
            m_PreviewEdgeCount = retryCount;
            m_PreviewReady = true;
            m_PreviewDirty = false;
            m_ApplyPreviewNextFrame = false;
            m_ApplyRetryNextFrame = true;
            m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
            Mod.LogDiagnostic($"[IntersectionTool] Split verification queued {retryCount} retry split definition(s); succeeded={succeededCount}, exhausted={exhaustedCount}, replacementQueued={replacementCount}. They will be applied on the next tool frame.");
            return true;
        }

        private bool TryQueueAppliedNodeMergeSplits(ref JobHandle result)
        {
            if (!m_VerifyAppliedNodeMerges)
            {
                return false;
            }

            m_VerifyAppliedNodeMerges = false;
            m_PreviewCandidates.Clear();
            m_PreviewNodeMergeCandidates.Clear();

            int verifiedMergeCount = 0;
            int queuedSplitCount = 0;
            int missingMergedEdgeCount = 0;
            int noRoomCount = 0;
            int deletedNodeCount = 0;
            Entity previewNode = Entity.Null;
            Entity lastQueuedEdge = Entity.Null;

            for (int i = 0; i < m_AppliedNodeMergeCandidates.Count; i++)
            {
                NodeMergeCandidate mergeCandidate = m_AppliedNodeMergeCandidates[i];
                if (TryMarkMergedRoadNodeDeleted(mergeCandidate, out int remainingConnectedEdges))
                {
                    deletedNodeCount++;
                }
                else if (remainingConnectedEdges > 0)
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Road-node merge postprocess kept node={FormatEntity(mergeCandidate.RemovableNode)} for now: connectedEdges={remainingConnectedEdges}.");
                }

                if (!TryFindAppliedMergedEdge(
                        mergeCandidate,
                        out Entity mergedEdge,
                        out float mergedLengthError,
                        out float mergedLength))
                {
                    missingMergedEdgeCount++;
                    continue;
                }

                verifiedMergeCount++;
                SplitDefinitionRequest request;
                float splitPosition;
                float splitDistance;
                float intersectionDistance;
                float pocketDistance;
                float targetDistance;
                float targetPocketLength;
                bool splitPrepared;
                if (mergeCandidate.Mode == NodeMergeMode.BalancedOppositeTarget)
                {
                    splitPrepared = TryBuildBalancedMergedSplitDefinitionRequest(
                        mergeCandidate,
                        mergedEdge,
                        mergedLength,
                        out request,
                        out splitPosition,
                        out splitDistance,
                        out intersectionDistance,
                        out pocketDistance,
                        out targetDistance,
                        out targetPocketLength);
                }
                else
                {
                    splitPrepared = TryBuildSplitDefinitionRequest(
                        mergeCandidate.Node,
                        mergedEdge,
                        out request,
                        out splitPosition,
                        out splitDistance,
                        out intersectionDistance,
                        out pocketDistance,
                        out targetDistance,
                        out targetPocketLength);
                }

                if (!splitPrepared)
                {
                    noRoomCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Road-node merge verified but split cannot be prepared mergedEdge={FormatEntity(mergedEdge)} shortEdge={FormatEntity(mergeCandidate.ShortEdge)} removableNode={FormatEntity(mergeCandidate.RemovableNode)} mode={mergeCandidate.Mode} mergedLength={mergedLength:0.##}m expectedLength={mergeCandidate.MergedLength:0.##}m lengthError={mergedLengthError:0.##}m.");
                    continue;
                }

                JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                {
                    Request = request,
                    ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                }.Schedule(result);

                m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
                result = createDefinitionJobHandle;

                m_PreviewCandidates.Add(new SplitCandidate
                {
                    Node = mergeCandidate.Node,
                    FarNode = mergeCandidate.FarNode,
                    Edge = mergedEdge,
                    SourcePrefab = mergeCandidate.MergeRequest.Prefab,
                    TargetPrefab = mergeCandidate.TargetPrefab,
                    LaneRepairMode = mergeCandidate.LaneRepairMode,
                    InvertTarget = mergeCandidate.PostMergeInvertTarget,
                    HasTargetUpgrade = mergeCandidate.HasTargetUpgrade,
                    TargetUpgrade = mergeCandidate.TargetUpgrade,
                    CurvePosition = splitPosition,
                    HitPosition = request.HitPosition,
                    TargetDistance = targetDistance,
                    TargetPocketLength = targetPocketLength,
                    SplitDistance = splitDistance,
                    IntersectionDistance = intersectionDistance,
                    PocketDistance = pocketDistance,
                    OriginalForwardLanes = mergeCandidate.OriginalForwardLanes,
                    OriginalBackwardLanes = mergeCandidate.OriginalBackwardLanes,
                    TargetForwardLanes = mergeCandidate.TargetForwardLanes,
                    TargetBackwardLanes = mergeCandidate.TargetBackwardLanes,
                    Attempt = 0,
                    FarIntersectionSnapshot = mergeCandidate.FarIntersectionSnapshot
                });

                queuedSplitCount++;
                previewNode = mergeCandidate.Node;
                lastQueuedEdge = mergedEdge;
                Mod.LogDiagnostic($"[IntersectionTool] Road-node merge verified; queued split on merged edge shortEdge={FormatEntity(mergeCandidate.ShortEdge)} continuation={FormatEntity(mergeCandidate.ContinuationEdge)} mergedEdge={FormatEntity(mergedEdge)} removableNode={FormatEntity(mergeCandidate.RemovableNode)} node={FormatEntity(mergeCandidate.Node)} farNode={FormatEntity(mergeCandidate.FarNode)} mode={mergeCandidate.Mode} mergePrefab={GetPrefabNameFromPrefab(mergeCandidate.MergeRequest.Prefab)} sourcePrefab={GetPrefabNameFromPrefab(mergeCandidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(mergeCandidate.TargetPrefab)} previewOrientation={(mergeCandidate.InvertTarget ? "reversed" : "direct")} postMergeOrientation={(mergeCandidate.PostMergeInvertTarget ? "reversed" : "direct")} laneRepair={mergeCandidate.LaneRepairMode} mergedLength={mergedLength:0.##}m expectedLength={mergeCandidate.MergedLength:0.##}m lengthError={mergedLengthError:0.##}m requestedPocket={targetPocketLength:0.##}m split={splitPosition:0.###} distance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m farIntersection={mergeCandidate.ExpectedFarIntersectionDistance:0.##}m usable={mergeCandidate.ExpectedUsableLength:0.##}m pocket={pocketDistance:0.##}m target={targetDistance:0.##}m.");
            }

            m_AppliedNodeMergeCandidates.Clear();
            if (queuedSplitCount == 0)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Road-node merge verification complete: verified={verifiedMergeCount}, queuedSplits=0, missingMergedEdge={missingMergedEdgeCount}, noRoom={noRoomCount}, deletedNodes={deletedNodeCount}.");
                return false;
            }

            m_PreviewIntersection = previewNode;
            m_PreviewEdge = lastQueuedEdge;
            m_PreviewEdgeCount = queuedSplitCount;
            m_PreviewReady = true;
            m_PreviewDirty = false;
            m_ApplyPreviewNextFrame = false;
            m_ApplyRetryNextFrame = true;
            m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
            Mod.LogDiagnostic($"[IntersectionTool] Road-node merge verification queued {queuedSplitCount} split definition(s); verified={verifiedMergeCount}, missingMergedEdge={missingMergedEdgeCount}, noRoom={noRoomCount}, deletedNodes={deletedNodeCount}. They will be applied on the next tool frame.");
            return true;
        }

        private bool HasBalancedRetrySplitCandidate()
        {
            for (int i = 0; i < m_PreviewCandidates.Count; i++)
            {
                if (m_PreviewCandidates[i].LaneRepairMode == SplitLaneConnectionRepairMode.BalancedOppositeTarget)
                {
                    return true;
                }
            }

            return false;
        }

        private bool AreBalancedRetrySplitNodesReady(out string detail)
        {
            int balancedCount = 0;
            int readyCount = 0;
            Entity missingEdge = Entity.Null;
            string lastDetail = "none";

            for (int i = 0; i < m_PreviewCandidates.Count; i++)
            {
                SplitCandidate candidate = m_PreviewCandidates[i];
                if (candidate.LaneRepairMode != SplitLaneConnectionRepairMode.BalancedOppositeTarget)
                {
                    continue;
                }

                balancedCount++;
                if (TryFindBalancedRetryPreviewSplitNode(candidate, out Entity splitNode, out string candidateDetail))
                {
                    readyCount++;
                    lastDetail = $"readySplitNode={FormatEntity(splitNode)} {candidateDetail}";
                    continue;
                }

                missingEdge = candidate.Edge;
                lastDetail = candidateDetail;
            }

            detail = $"balanced={balancedCount} ready={readyCount} missingEdge={FormatEntity(missingEdge)} last={lastDetail}";
            return balancedCount == readyCount;
        }

        private bool TryFindBalancedRetryPreviewSplitNode(SplitCandidate candidate, out Entity splitNode, out string detail)
        {
            if (TryFindPreviewSplitNode(candidate, out splitNode))
            {
                detail = $"directOriginalMatch edge={FormatEntity(candidate.Edge)} split={candidate.CurvePosition:0.###}.";
                return true;
            }

            int tempNodeCount = 0;
            int replaceNodeCount = 0;
            Entity bestNode = Entity.Null;
            Entity bestOriginal = Entity.Null;
            float bestCurvePosition = 0f;
            float bestDistance = float.MaxValue;

            using (NativeArray<Entity> entities = m_TempSplitNodeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempSplitNodeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    tempNodeCount++;
                    Temp temp = temps[i];
                    if ((temp.m_Flags & TempFlags.Replace) != TempFlags.Replace ||
                        (temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0)
                    {
                        continue;
                    }

                    replaceNodeCount++;
                    Entity entity = entities[i];
                    if (!EntityManager.TryGetComponent(entity, out Node node))
                    {
                        continue;
                    }

                    float distance = math.distance(node.m_Position, candidate.HitPosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestNode = entity;
                        bestOriginal = temp.m_Original;
                        bestCurvePosition = temp.m_CurvePosition;
                    }

                    bool originalMatchesCandidate = temp.m_Original == candidate.Edge;
                    bool originalIsMissing = temp.m_Original == Entity.Null;
                    bool splitPositionMatches = math.abs(temp.m_CurvePosition - candidate.CurvePosition) <= PreviewSplitNodeTolerance;
                    if (distance <= BalancedRetryPreviewSplitNodePositionTolerance &&
                        splitPositionMatches &&
                        (originalMatchesCandidate || originalIsMissing))
                    {
                        splitNode = entity;
                        detail = $"positionFallback edge={FormatEntity(candidate.Edge)} split={candidate.CurvePosition:0.###} tempOriginal={FormatEntity(temp.m_Original)} tempSplit={temp.m_CurvePosition:0.###} positionError={distance:0.###}m splitTolerance={PreviewSplitNodeTolerance:0.###} positionTolerance={BalancedRetryPreviewSplitNodePositionTolerance:0.##}m.";
                        return true;
                    }
                }
            }

            splitNode = Entity.Null;
            string bestDistanceText = bestNode == Entity.Null ? "n/a" : $"{bestDistance:0.###}m";
            detail = $"missing edge={FormatEntity(candidate.Edge)} split={candidate.CurvePosition:0.###} tempNodes={tempNodeCount} replaceNodes={replaceNodeCount} bestNode={FormatEntity(bestNode)} bestOriginal={FormatEntity(bestOriginal)} bestSplit={bestCurvePosition:0.###} bestPositionError={bestDistanceText} splitTolerance={PreviewSplitNodeTolerance:0.###} positionTolerance={BalancedRetryPreviewSplitNodePositionTolerance:0.##}m.";
            return false;
        }

        private bool TryBuildBalancedMergedSplitDefinitionRequest(
            NodeMergeCandidate candidate,
            Entity mergedEdge,
            float mergedLength,
            out SplitDefinitionRequest request,
            out float splitPosition,
            out float splitDistance,
            out float intersectionDistance,
            out float pocketDistance,
            out float targetDistance,
            out float targetPocketLength)
        {
            request = default;
            splitPosition = 0f;
            splitDistance = 0f;
            intersectionDistance = candidate.ExpectedIntersectionDistance;
            pocketDistance = 0f;
            targetDistance = 0f;
            targetPocketLength = candidate.ExpectedTargetPocketLength;

            if (!EntityManager.TryGetComponent(mergedEdge, out Edge edge) ||
                !EntityManager.TryGetComponent(mergedEdge, out Curve curve) ||
                !EntityManager.TryGetComponent(mergedEdge, out PrefabRef prefabRef) ||
                !EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometryData))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot prepare balanced road-node split mergedEdge={FormatEntity(mergedEdge)} shortEdge={FormatEntity(candidate.ShortEdge)}: missing Edge, Curve, PrefabRef, or NetGeometryData.");
                return false;
            }

            bool nodeIsStart = edge.m_Start == candidate.Node;
            bool nodeIsEnd = edge.m_End == candidate.Node;
            if (!nodeIsStart && !nodeIsEnd)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot prepare balanced road-node split mergedEdge={FormatEntity(mergedEdge)} shortEdge={FormatEntity(candidate.ShortEdge)}: current node {FormatEntity(candidate.Node)} is not an endpoint start={FormatEntity(edge.m_Start)} end={FormatEntity(edge.m_End)}.");
                return false;
            }

            float actualMergedLength = curve.m_Length > 0.01f
                ? curve.m_Length
                : mergedLength;
            GetMinMaxSplitPositions(
                actualMergedLength,
                geometryData.m_DefaultWidth,
                geometryData.m_EdgeLengthRange.min,
                out float minSplit,
                out float maxSplit);
            if (minSplit >= maxSplit)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot prepare balanced road-node split mergedEdge={FormatEntity(mergedEdge)} shortEdge={FormatEntity(candidate.ShortEdge)}: merged target edge is too short length={actualMergedLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###}.");
                return false;
            }

            float usableLength = actualMergedLength - candidate.ExpectedIntersectionDistance - candidate.ExpectedFarIntersectionDistance;
            float halfUsableLength = usableLength * 0.5f;
            if (!HasMinimumPocketLength(halfUsableLength))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot prepare balanced road-node split mergedEdge={FormatEntity(mergedEdge)} shortEdge={FormatEntity(candidate.ShortEdge)}: actual usable length is too small actualLength={actualMergedLength:0.##}m nearMargin={candidate.ExpectedIntersectionDistance:0.##}m farMargin={candidate.ExpectedFarIntersectionDistance:0.##}m usable={usableLength:0.##}m half={halfUsableLength:0.##}m minPocket={MinimumPocketLaneLength:0.##}m effectiveMinPocket={GetEffectiveMinimumPocketLength():0.##}m.");
                return false;
            }

            targetDistance = candidate.ExpectedIntersectionDistance + halfUsableLength;
            splitPosition = GetCurvePositionAtDistance(curve, nodeIsStart, targetDistance);
            if (splitPosition < minSplit || splitPosition > maxSplit)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot prepare balanced road-node split mergedEdge={FormatEntity(mergedEdge)} shortEdge={FormatEntity(candidate.ShortEdge)}: half split is outside safe split range split={splitPosition:0.###} minSplit={minSplit:0.###} maxSplit={maxSplit:0.###} targetDistance={targetDistance:0.##}m actualLength={actualMergedLength:0.##}m nearMargin={candidate.ExpectedIntersectionDistance:0.##}m farMargin={candidate.ExpectedFarIntersectionDistance:0.##}m usable={usableLength:0.##}m.");
                return false;
            }

            splitDistance = GetCurveDistanceFromNode(curve, nodeIsStart, splitPosition);
            pocketDistance = math.max(0f, splitDistance - candidate.ExpectedIntersectionDistance);
            float3 hitPosition = MathUtils.Position(curve.m_Bezier, splitPosition);
            int randomSeed = EntityManager.TryGetComponent(mergedEdge, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : mergedEdge.Index;

            request = new SplitDefinitionRequest
            {
                Edge = mergedEdge,
                Prefab = prefabRef.m_Prefab,
                HitPosition = hitPosition,
                CurvePosition = splitPosition,
                RandomSeed = randomSeed
            };

            Mod.LogDiagnostic($"[IntersectionTool] Prepared balanced road-node split mergedEdge={FormatEntity(mergedEdge)} shortEdge={FormatEntity(candidate.ShortEdge)} continuation={FormatEntity(candidate.ContinuationEdge)} node={FormatEntity(candidate.Node)} farNode={FormatEntity(candidate.FarNode)} targetPrefab={GetPrefabNameFromPrefab(prefabRef.m_Prefab)} split={splitPosition:0.###} splitDistance={splitDistance:0.##}m nearMargin={candidate.ExpectedIntersectionDistance:0.##}m farMargin={candidate.ExpectedFarIntersectionDistance:0.##}m usable={usableLength:0.##}m half={halfUsableLength:0.##}m requestedPocket={targetPocketLength:0.##}m minSplit={minSplit:0.###} maxSplit={maxSplit:0.###} laneRepair=balanced-opposite-target.");
            return true;
        }

        private bool TryMarkMergedRoadNodeDeleted(NodeMergeCandidate candidate, out int remainingConnectedEdges)
        {
            remainingConnectedEdges = -1;
            if (candidate.RemovableNode == Entity.Null ||
                !EntityManager.Exists(candidate.RemovableNode) ||
                EntityManager.HasComponent<Deleted>(candidate.RemovableNode))
            {
                return false;
            }

            if (!EntityManager.TryGetBuffer(candidate.RemovableNode, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Road-node merge postprocess cannot inspect removable node={FormatEntity(candidate.RemovableNode)}: missing ConnectedEdge buffer.");
                return false;
            }

            remainingConnectedEdges = connectedEdges.Length;
            if (connectedEdges.Length != 0)
            {
                return false;
            }

            EntityManager.AddComponent<Deleted>(candidate.RemovableNode);
            Mod.LogDiagnostic($"[IntersectionTool] Road-node merge postprocess marked removable node={FormatEntity(candidate.RemovableNode)} Deleted after both original edges detached.");
            return true;
        }

        private bool TryFindAppliedMergedEdge(
            NodeMergeCandidate candidate,
            out Entity mergedEdge,
            out float lengthError,
            out float mergedLength)
        {
            mergedEdge = Entity.Null;
            lengthError = 0f;
            mergedLength = 0f;

            if (!EntityManager.TryGetBuffer(candidate.Node, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find applied merged edge shortEdge={FormatEntity(candidate.ShortEdge)} node={FormatEntity(candidate.Node)}: node has no ConnectedEdge buffer.");
                return false;
            }

            float bestValidError = float.MaxValue;
            float bestRejectedError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            Entity bestRejectedEdge = Entity.Null;
            float bestLength = 0f;
            int scannedCount = 0;
            int endpointMatchCount = 0;
            int prefabMatchCount = 0;
            Entity expectedPrefab = candidate.MergeRequest.Prefab != Entity.Null
                ? candidate.MergeRequest.Prefab
                : candidate.SourcePrefab;

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (edgeEntity == Entity.Null ||
                    !EntityManager.Exists(edgeEntity) ||
                    EntityManager.HasComponent<Deleted>(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                    !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                {
                    continue;
                }

                scannedCount++;
                Entity otherNode = edge.m_Start == candidate.Node
                    ? edge.m_End
                    : edge.m_End == candidate.Node
                        ? edge.m_Start
                        : Entity.Null;
                if (otherNode != candidate.FarNode)
                {
                    continue;
                }

                endpointMatchCount++;
                if (prefabRef.m_Prefab != expectedPrefab)
                {
                    continue;
                }

                prefabMatchCount++;
                float candidateLength = curve.m_Length > 0.01f ? curve.m_Length : MathUtils.Length(curve.m_Bezier);
                float candidateLengthError = math.abs(candidateLength - candidate.MergedLength);
                if (candidateLengthError > MergedEdgeLengthTolerance)
                {
                    if (candidateLengthError < bestRejectedError)
                    {
                        bestRejectedError = candidateLengthError;
                        bestRejectedEdge = edgeEntity;
                    }

                    continue;
                }

                if (candidateLengthError < bestValidError)
                {
                    bestValidError = candidateLengthError;
                    bestLength = candidateLength;
                    bestEdge = edgeEntity;
                }
            }

            if (bestEdge == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find applied merged edge shortEdge={FormatEntity(candidate.ShortEdge)} removableNode={FormatEntity(candidate.RemovableNode)} continuation={FormatEntity(candidate.ContinuationEdge)} node={FormatEntity(candidate.Node)} farNode={FormatEntity(candidate.FarNode)} mode={candidate.Mode} expectedPrefab={GetPrefabNameFromPrefab(expectedPrefab)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} expectedLength={candidate.MergedLength:0.##}m scanned={scannedCount} endpointMatches={endpointMatchCount} prefabMatches={prefabMatchCount} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedLengthError={FormatMeters(bestRejectedError)}.");
                return false;
            }

            mergedEdge = bestEdge;
            lengthError = bestValidError;
            mergedLength = bestLength;
            return true;
        }

        private bool IsEdgeStillConnectedToNode(Entity nodeEntity, Entity edgeEntity)
        {
            if (nodeEntity == Entity.Null ||
                edgeEntity == Entity.Null ||
                !EntityManager.Exists(nodeEntity) ||
                !EntityManager.Exists(edgeEntity) ||
                !EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                if (connectedEdges[i].m_Edge == edgeEntity)
                {
                    return true;
                }
            }

            return false;
        }

    }
}
