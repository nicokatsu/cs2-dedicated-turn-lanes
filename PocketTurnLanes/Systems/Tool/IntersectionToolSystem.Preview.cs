using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class IntersectionToolSystem
    {
        private bool SetToolEnabled(bool enabled)
        {
            if (IsToolEnabled == enabled)
            {
                return false;
            }

            IsToolEnabled = enabled;
            ToolEnabledChanged?.Invoke(enabled);
            return true;
        }

        private bool HasPreviewState()
        {
            return m_PreviewDirty ||
                   m_PreviewReady ||
                   m_PreviewValidationPending ||
                   m_ApplyPreviewNextFrame ||
                   m_RebuildSplitPreviewForApply ||
                   m_HasReplacementPreviewDefinitions ||
                   m_PreviewNodeMergeCandidates.Count > 0 ||
                   m_AppliedNodeMergeCandidates.Count > 0 ||
                   m_VerifyAppliedNodeMerges ||
                   m_PreviewIntersection != Entity.Null ||
                   m_PreviewEdge != Entity.Null ||
                   m_PreviewEdgeCount > 0;
        }

        private void ResetPreviewState()
        {
            m_PreviewIntersection = Entity.Null;
            m_PreviewEdge = Entity.Null;
            m_PreviewDirty = false;
            m_PreviewReady = false;
            m_ApplyPreviewNextFrame = false;
            m_ApplyRetryNextFrame = false;
            m_ApplyReplacementNextFrame = false;
            m_RebuildSplitPreviewForApply = false;
            m_PreviewValidationPending = false;
            m_PreviewCreatedFrame = -1;
            m_PreviewEdgeCount = 0;
            m_HasReplacementPreviewDefinitions = false;
            m_HasShortEdgeReplacementPreviewDefinitions = false;
            m_NormalReplacementPreviewDefinitionsQueued = false;
            m_ShortEdgeReplacementPreviewAttempted = false;
            m_NodeMergeDefinitionsReadyForApply = false;
            m_ShortEdgeReplacementPreviewQueuedCount = 0;
            m_PreviewCandidates.Clear();
            m_NextPreviewCandidates.Clear();
            m_AppliedCandidates.Clear();
            m_PreviewNodeMergeCandidates.Clear();
            m_AppliedNodeMergeCandidates.Clear();
            m_VerifyAppliedSplits = false;
            m_VerifyAppliedNodeMerges = false;
            m_QueuedReplacementCandidates.Clear();
            m_AppliedReplacementCandidates.Clear();
            m_PendingLaneRepairCandidates.Clear();
            m_VerifyAppliedReplacements = false;
        }

        private JobHandle ClearPreviewDefinitions(JobHandle inputDeps, string reason)
        {
            applyMode = ApplyMode.Clear;
            JobHandle result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
            ResetPreviewState();
            Mod.LogDiagnostic($"[IntersectionTool] Cleared split preview definitions ({reason}).");
            return result;
        }

        private JobHandle ValidateSplitPreview(JobHandle inputDeps)
        {
            if (UnityEngine.Time.frameCount <= m_PreviewCreatedFrame)
            {
                return inputDeps;
            }

            if (m_PreviewCandidates.Count == 0)
            {
                if (m_PreviewNodeMergeCandidates.Count > 0)
                {
                    if (m_HasShortEdgeReplacementPreviewDefinitions)
                    {
                        Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview definitions are queued without temp-edge validation node={FormatEntity(m_PreviewIntersection)} definitionsQueued={m_ShortEdgeReplacementPreviewQueuedCount}; keeping them until hover changes or click apply clears preview definitions.");
                    }

                    m_PreviewValidationPending = false;
                    m_PreviewReady = true;
                    m_PreviewDirty = false;
                    m_PreviewEdgeCount = m_PreviewNodeMergeCandidates.Count;
                    m_OverlaySystem.Clear();
                    Mod.LogDiagnostic($"[IntersectionTool] Preview validation complete node={FormatEntity(m_PreviewIntersection)} visibleSplits=0 shortEdgeReplacementPreviewDefinitions={m_ShortEdgeReplacementPreviewQueuedCount} roadNodeMergeApplyCandidates={m_PreviewNodeMergeCandidates.Count}; ready for click apply with road-node merge candidate(s).");
                    return inputDeps;
                }

                m_PreviewValidationPending = false;
                m_PreviewReady = false;
                m_PreviewDirty = false;
                Mod.LogDiagnostic($"[IntersectionTool] Preview validation complete node={FormatEntity(m_PreviewIntersection)} visibleSplits=0; ready=false.");
                return inputDeps;
            }

            m_NextPreviewCandidates.Clear();
            int visibleCount = 0;
            int retryCount = 0;
            int exhaustedCount = 0;
            int exhaustedFallbackCount = 0;
            bool needsRetry = false;

            for (int i = 0; i < m_PreviewCandidates.Count; i++)
            {
                SplitCandidate candidate = m_PreviewCandidates[i];
                if (TryFindPreviewSplitNode(candidate, out _))
                {
                    visibleCount++;
                    m_NextPreviewCandidates.Add(candidate);
                    continue;
                }

                if (candidate.Attempt >= MaxSplitRetryAttempts)
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Preview split still has no generated node edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={candidate.Attempt} distance={candidate.SplitDistance:0.##}m; no more retry room.");
                    if (TryPromoteExhaustedSplitToNodeMergeFallback(candidate, "max retry attempts reached without a generated temp split node"))
                    {
                        exhaustedFallbackCount++;
                    }

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
                    Mod.LogDiagnostic($"[IntersectionTool] Preview retry cannot be prepared edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} {retryDetail} requestedPocket={targetPocketLength:0.##}m requestedBeforeCap={retryPocketLength:0.##}m.");
                    if (TryPromoteExhaustedSplitToNodeMergeFallback(candidate, $"retry split could not be prepared; {retryDetail}"))
                    {
                        exhaustedFallbackCount++;
                    }

                    continue;
                }

                if (splitDistance < candidate.SplitDistance + MinimumRetryProgress)
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Preview retry cannot move far enough edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} {retryDetail} previous={candidate.SplitDistance:0.##}m next={splitDistance:0.##}m.");
                    if (TryPromoteExhaustedSplitToNodeMergeFallback(candidate, $"retry split could not move far enough; {retryDetail} previous={candidate.SplitDistance:0.##}m next={splitDistance:0.##}m"))
                    {
                        exhaustedFallbackCount++;
                    }

                    continue;
                }

                retryCount++;
                needsRetry = true;
                m_NextPreviewCandidates.Add(new SplitCandidate
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
                    Attempt = nextAttempt
                });
                Mod.LogDiagnostic($"[IntersectionTool] Preview split missing edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)}; retry attempt={nextAttempt} {retryDetail} requestedPocket={targetPocketLength:0.##}m requestedBeforeCap={retryPocketLength:0.##}m previousPocket={candidate.TargetPocketLength:0.##}m split={splitPosition:0.###} target={targetDistance:0.##}m distance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m.");
            }

            if (needsRetry)
            {
                return RequeueSplitPreview(m_NextPreviewCandidates, inputDeps, visibleCount, retryCount, exhaustedCount);
            }

            if (m_NextPreviewCandidates.Count == 0)
            {
                int replacementPreviewDefinitionCount = m_ReplacementPreviewDefinitionQuery.CalculateEntityCount();
                applyMode = ApplyMode.Clear;
                JobHandle clearResult = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
                m_HasReplacementPreviewDefinitions = false;
                m_NormalReplacementPreviewDefinitionsQueued = false;
                if (m_PreviewNodeMergeCandidates.Count > 0)
                {
                    if (m_HasShortEdgeReplacementPreviewDefinitions)
                    {
                        m_HasShortEdgeReplacementPreviewDefinitions = false;
                        m_ShortEdgeReplacementPreviewAttempted = true;
                        m_ShortEdgeReplacementPreviewQueuedCount = 0;
                        Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview downgraded because normal split preview validation exhausted; full preview definition clear removed replacementPreviewDefinitionEntities={replacementPreviewDefinitionCount}. Road-node merge apply candidate(s) remain queued.");
                    }

                    m_PreviewCandidates.Clear();
                    int queuedShortEdgePreviewCount = 0;
                    int degradedShortEdgePreviewCount = 0;
                    if (!m_ShortEdgeReplacementPreviewAttempted)
                    {
                        QueueShortEdgeReplacementPreviews(
                            ref clearResult,
                            "normal split preview validation exhausted; promoting exhausted split(s) to short-edge fallback preview",
                            out queuedShortEdgePreviewCount,
                            out degradedShortEdgePreviewCount);
                    }

                    bool hasPendingShortEdgeReplacementPreview = queuedShortEdgePreviewCount > 0;
                    m_PreviewEdge = m_PreviewNodeMergeCandidates[m_PreviewNodeMergeCandidates.Count - 1].ShortEdge;
                    m_PreviewEdgeCount = m_PreviewNodeMergeCandidates.Count;
                    m_PreviewValidationPending = hasPendingShortEdgeReplacementPreview;
                    m_PreviewReady = !hasPendingShortEdgeReplacementPreview;
                    m_PreviewDirty = false;
                    m_NodeMergeDefinitionsReadyForApply = false;
                    m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                    m_OverlaySystem.Clear();
                    Mod.LogDiagnostic($"[IntersectionTool] Split preview validation exhausted normal split definitions node={FormatEntity(m_PreviewIntersection)} visible=0, retried=0, exhausted={exhaustedCount}, exhaustedFallbacks={exhaustedFallbackCount}; keeping {m_PreviewNodeMergeCandidates.Count} road-node merge apply candidate(s) shortEdgeReplacementPreviewQueued={queuedShortEdgePreviewCount} shortEdgeReplacementPreviewDegraded={degradedShortEdgePreviewCount} pendingShortEdgePreview={hasPendingShortEdgeReplacementPreview}.");
                    return clearResult;
                }

                MarkNoSplitPreviewReady(m_PreviewIntersection);
                Mod.LogDiagnostic($"[IntersectionTool] Split preview validation complete node={FormatEntity(m_PreviewIntersection)} visible=0, retried=0, exhausted={exhaustedCount}, exhaustedFallbacks={exhaustedFallbackCount}; no visible split definitions remain.");
                return clearResult;
            }

            if (m_NextPreviewCandidates.Count != m_PreviewCandidates.Count)
            {
                return RequeueSplitPreview(m_NextPreviewCandidates, inputDeps, visibleCount, 0, exhaustedCount);
            }

            JobHandle result = inputDeps;
            int shortEdgeReplacementPreviewDefinitionCount = 0;
            bool queuedShortEdgeReplacementPreviewThisFrame = false;

            int replacementPreviewCount = m_NormalReplacementPreviewDefinitionsQueued
                ? m_PreviewCandidates.Count
                : 0;
            if (!m_NormalReplacementPreviewDefinitionsQueued)
            {
                for (int i = 0; i < m_PreviewCandidates.Count; i++)
                {
                    if (TryApplyReplacementPreview(m_PreviewCandidates[i], ref result))
                    {
                        replacementPreviewCount++;
                    }
                }

                if (replacementPreviewCount == m_PreviewCandidates.Count)
                {
                    m_NormalReplacementPreviewDefinitionsQueued = true;
                }
            }

            if (!m_NormalReplacementPreviewDefinitionsQueued &&
                replacementPreviewCount < m_PreviewCandidates.Count &&
                UnityEngine.Time.frameCount - m_PreviewCreatedFrame < MaxReplacementPreviewWaitFrames)
            {
                m_PreviewValidationPending = true;
                m_PreviewReady = false;
                Mod.LogDiagnostic($"[IntersectionTool] Waiting for replacement preview edges node={FormatEntity(m_PreviewIntersection)} previewed={replacementPreviewCount}/{m_PreviewCandidates.Count} frameDelta={UnityEngine.Time.frameCount - m_PreviewCreatedFrame}.");
                return result;
            }

            if (m_PreviewNodeMergeCandidates.Count > 0 &&
                !m_ShortEdgeReplacementPreviewAttempted)
            {
                QueueShortEdgeReplacementPreviews(
                    ref result,
                    "normal replacement preview definitions were queued; queueing short-edge preview in the same tool frame",
                    out int queuedShortEdgePreviewCount,
                    out _);
                queuedShortEdgeReplacementPreviewThisFrame = queuedShortEdgePreviewCount > 0;
            }

            if (queuedShortEdgeReplacementPreviewThisFrame)
            {
                m_PreviewValidationPending = true;
                m_PreviewReady = false;
                m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                Mod.LogDiagnostic($"[IntersectionTool] Queued synchronized replacement hover preview definitions node={FormatEntity(m_PreviewIntersection)} normalReplacementPreviewed={replacementPreviewCount}/{m_PreviewCandidates.Count} shortEdgeReplacementPreviewDefinitions={m_ShortEdgeReplacementPreviewQueuedCount}; waiting one frame to validate short-edge temp preview.");
                return result;
            }

            m_NextPreviewCandidates.Clear();
            m_PreviewValidationPending = false;
            m_PreviewReady = true;
            m_PreviewDirty = false;
            m_PreviewEdgeCount = m_PreviewCandidates.Count + m_PreviewNodeMergeCandidates.Count;
            if (m_HasShortEdgeReplacementPreviewDefinitions)
            {
                shortEdgeReplacementPreviewDefinitionCount = m_ShortEdgeReplacementPreviewQueuedCount;
            }

            Mod.LogDiagnostic($"[IntersectionTool] Split preview validation complete node={FormatEntity(m_PreviewIntersection)} visible={visibleCount}, retried=0, exhausted={exhaustedCount}, exhaustedFallbacks={exhaustedFallbackCount}, replacementPreviewed={replacementPreviewCount}, shortEdgeReplacementPreviewDefinitions={shortEdgeReplacementPreviewDefinitionCount}, roadNodeMergeApplyCandidates={m_PreviewNodeMergeCandidates.Count}. Ready for click apply.");
            return result;
        }

        private bool TryPromoteExhaustedSplitToNodeMergeFallback(SplitCandidate candidate, string reason)
        {
            if (candidate.Edge == Entity.Null ||
                candidate.Node == Entity.Null ||
                !EntityManager.Exists(candidate.Edge))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot promote exhausted split to road-node merge fallback: missing candidate edge or node edge={FormatEntity(candidate.Edge)} node={FormatEntity(candidate.Node)} reason={reason}.");
                return false;
            }

            for (int i = 0; i < m_PreviewNodeMergeCandidates.Count; i++)
            {
                NodeMergeCandidate existing = m_PreviewNodeMergeCandidates[i];
                if (existing.Node == candidate.Node &&
                    existing.ShortEdge == candidate.Edge)
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Exhausted split already has a road-node merge fallback candidate edge={FormatEntity(candidate.Edge)} node={FormatEntity(candidate.Node)} reason={reason}.");
                    return false;
                }
            }

            if (!TryFindPocketLaneReplacementPrefab(
                    candidate.Node,
                    candidate.Edge,
                    out ReplacementPrefabMatch prefabMatch))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot promote exhausted split to road-node merge fallback edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} node={FormatEntity(candidate.Node)}: replacement prefab no longer matches. reason={reason}");
                return false;
            }

            if (!TryBuildNodeMergeCandidate(
                    candidate.Node,
                    candidate.Edge,
                    prefabMatch,
                    out NodeMergeCandidate mergeCandidate))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Exhausted split is not eligible for road-node merge fallback edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} node={FormatEntity(candidate.Node)} reason={reason}.");
                return false;
            }

            m_PreviewNodeMergeCandidates.Add(mergeCandidate);
            Mod.LogDiagnostic($"[IntersectionTool] Promoted exhausted split preview to road-node merge fallback edge={FormatEntity(candidate.Edge)} node={FormatEntity(candidate.Node)} removableNode={FormatEntity(mergeCandidate.RemovableNode)} continuation={FormatEntity(mergeCandidate.ContinuationEdge)} farNode={FormatEntity(mergeCandidate.FarNode)} mode={mergeCandidate.Mode} laneRepair={mergeCandidate.LaneRepairMode} reason={reason} splitDistance={candidate.SplitDistance:0.##}m targetPocket={candidate.TargetPocketLength:0.##}m expectedMergedSplit={mergeCandidate.ExpectedSplitDistance:0.##}m expectedPocket={mergeCandidate.ExpectedPocketDistance:0.##}m.");
            return true;
        }

        private JobHandle RequeueSplitPreview(List<SplitCandidate> candidates, JobHandle inputDeps, int visibleCount, int retryCount, int exhaustedCount)
        {
            int replacementPreviewDefinitionCount = m_ReplacementPreviewDefinitionQuery.CalculateEntityCount();
            applyMode = ApplyMode.Clear;
            JobHandle result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
            m_HasReplacementPreviewDefinitions = false;
            m_NormalReplacementPreviewDefinitionsQueued = false;
            if (m_HasShortEdgeReplacementPreviewDefinitions)
            {
                m_HasShortEdgeReplacementPreviewDefinitions = false;
                m_ShortEdgeReplacementPreviewAttempted = true;
                m_ShortEdgeReplacementPreviewQueuedCount = 0;
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview downgraded during normal split preview retry/requeue; full preview definition clear removed replacementPreviewDefinitionEntities={replacementPreviewDefinitionCount}. Road-node merge apply candidate(s) remain queued.");
            }

            m_PreviewCandidates.Clear();
            int queuedCount = 0;
            Entity previewNode = Entity.Null;
            Entity lastQueuedEdge = Entity.Null;

            for (int i = 0; i < candidates.Count; i++)
            {
                SplitCandidate candidate = candidates[i];
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
                        candidate.TargetPocketLength))
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Cannot rebuild preview split edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={candidate.Attempt}.");
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
                    Attempt = candidate.Attempt
                });

                queuedCount++;
                previewNode = candidate.Node;
                lastQueuedEdge = candidate.Edge;
                result = createDefinitionJobHandle;
            }

            if (queuedCount == 0)
            {
                if (m_PreviewNodeMergeCandidates.Count > 0)
                {
                    NodeMergeCandidate lastMergeCandidate = m_PreviewNodeMergeCandidates[m_PreviewNodeMergeCandidates.Count - 1];
                    m_PreviewIntersection = lastMergeCandidate.Node;
                    m_PreviewEdge = lastMergeCandidate.ShortEdge;
                    m_PreviewEdgeCount = m_PreviewNodeMergeCandidates.Count;
                    m_PreviewReady = true;
                    m_PreviewValidationPending = false;
                    m_PreviewDirty = false;
                    m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                    m_NodeMergeDefinitionsReadyForApply = false;
                    m_OverlaySystem.Clear();
                    Mod.LogDiagnostic($"[IntersectionTool] Preview retry had no normal split definitions left; keeping {m_PreviewNodeMergeCandidates.Count} road-node merge apply candidate(s) with short-edge hover preview disabled/degraded.");
                    return result;
                }

                applyMode = ApplyMode.None;
                m_OverlaySystem.Clear();
                ResetPreviewState();
                Mod.LogDiagnostic("[IntersectionTool] Preview retry had no split definitions left to queue.");
                return result;
            }

            m_PreviewIntersection = previewNode;
            m_PreviewEdge = lastQueuedEdge;
            m_PreviewEdgeCount = queuedCount + m_PreviewNodeMergeCandidates.Count;
            m_PreviewReady = false;
            m_PreviewValidationPending = true;
            m_PreviewDirty = false;
            m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
            ShowPreviewOverlay(previewNode);
            m_NodeMergeDefinitionsReadyForApply = false;
            Mod.LogDiagnostic($"[IntersectionTool] Rebuilt preview definitions for retry pass node={FormatEntity(previewNode)} splitDefinitions={queuedCount}, shortEdgePreviewDisabledOrDegraded={m_PreviewNodeMergeCandidates.Count}, visible={visibleCount}, retrying={retryCount}, exhausted={exhaustedCount}.");
            return result;
        }

        private JobHandle RebuildSplitDefinitionsForApply(JobHandle inputDeps)
        {
            JobHandle result = inputDeps;
            int remainingReplacementPreviewDefinitions = m_ReplacementPreviewDefinitionQuery.CalculateEntityCount();
            if (remainingReplacementPreviewDefinitions > 0)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Clean apply rebuild found replacement preview definitions still present after the clear frame count={remainingReplacementPreviewDefinitions}; continuing with clean split/merge definitions so click apply can proceed.");
            }

            int queuedCount = 0;
            int mergeQueuedCount = 0;
            int directReplacementQueuedCount = 0;
            Entity previewNode = Entity.Null;
            Entity lastQueuedEdge = Entity.Null;
            m_NextPreviewCandidates.Clear();

            for (int i = 0; i < m_PreviewCandidates.Count; i++)
            {
                SplitCandidate candidate = m_PreviewCandidates[i];
                if (candidate.Edge == Entity.Null ||
                    candidate.SourcePrefab == Entity.Null ||
                    !EntityManager.Exists(candidate.Edge))
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Cannot rebuild clean apply split definition edge={FormatEntity(candidate.Edge)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)}: edge or prefab is missing.");
                    continue;
                }

                int randomSeed = EntityManager.TryGetComponent(candidate.Edge, out PseudoRandomSeed seed)
                    ? seed.m_Seed
                    : candidate.Edge.Index;

                SplitDefinitionRequest request = new SplitDefinitionRequest
                {
                    Edge = candidate.Edge,
                    Prefab = candidate.SourcePrefab,
                    HitPosition = candidate.HitPosition,
                    CurvePosition = candidate.CurvePosition,
                    RandomSeed = randomSeed
                };

                JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                {
                    Request = request,
                    ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                }.Schedule(result);

                m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
                result = createDefinitionJobHandle;
                queuedCount++;
                m_NextPreviewCandidates.Add(candidate);
                previewNode = candidate.Node;
                lastQueuedEdge = candidate.Edge;
            }

            for (int i = 0; i < m_PreviewNodeMergeCandidates.Count; i++)
            {
                NodeMergeCandidate mergeCandidate = m_PreviewNodeMergeCandidates[i];
                if (mergeCandidate.Mode == NodeMergeMode.ShortEdgeReplacementOnly)
                {
                    if (!TryQueueShortEdgeReplacementApplyDefinition(mergeCandidate, ref result))
                    {
                        continue;
                    }

                    directReplacementQueuedCount++;
                    previewNode = mergeCandidate.Node;
                    lastQueuedEdge = mergeCandidate.ShortEdge;
                    continue;
                }

                if (!QueueNodeMergeDefinition(mergeCandidate, ref result))
                {
                    continue;
                }

                mergeQueuedCount++;
                previewNode = mergeCandidate.Node;
                lastQueuedEdge = mergeCandidate.ShortEdge;
            }

            m_PreviewCandidates.Clear();
            m_PreviewCandidates.AddRange(m_NextPreviewCandidates);
            m_NextPreviewCandidates.Clear();
            m_PreviewIntersection = previewNode;
            m_PreviewEdge = lastQueuedEdge;
            m_PreviewEdgeCount = queuedCount + mergeQueuedCount + directReplacementQueuedCount;
            m_NodeMergeDefinitionsReadyForApply = m_PreviewNodeMergeCandidates.Count == 0 || (mergeQueuedCount + directReplacementQueuedCount) > 0;
            Mod.LogDiagnostic($"[IntersectionTool] Rebuilt clean definitions for apply node={FormatEntity(previewNode)} splitDefinitions={queuedCount} roadNodeMergeDefinitions={mergeQueuedCount} directShortEdgeReplacementDefinitions={directReplacementQueuedCount}; hover preview definitions were discarded before apply remainingReplacementPreviewDefinitions={remainingReplacementPreviewDefinitions}.");
            return result;
        }

        private bool TryQueueShortEdgeReplacementApplyDefinition(NodeMergeCandidate candidate, ref JobHandle result)
        {
            if (candidate.ShortEdge == Entity.Null ||
                candidate.TargetPrefab == Entity.Null ||
                !EntityManager.Exists(candidate.ShortEdge))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot queue short-edge direct replacement apply definition shortEdge={FormatEntity(candidate.ShortEdge)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)}: missing edge or target prefab.");
                return false;
            }

            ReplacementCandidate replacementCandidate = new ReplacementCandidate
            {
                Node = candidate.Node,
                FarNode = candidate.FarNode,
                SplitNode = candidate.RemovableNode,
                OriginalEdge = candidate.ShortEdge,
                PocketEdge = candidate.ShortEdge,
                SourcePrefab = candidate.SourcePrefab,
                TargetPrefab = candidate.TargetPrefab,
                LaneRepairMode = SplitLaneConnectionRepairMode.ShortEdgeTransition,
                InvertTarget = candidate.InvertTarget,
                HasTargetUpgrade = candidate.HasTargetUpgrade,
                TargetUpgrade = candidate.TargetUpgrade,
                HitPosition = candidate.ExpectedHitPosition,
                OriginalForwardLanes = candidate.OriginalForwardLanes,
                OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                TargetForwardLanes = candidate.TargetForwardLanes,
                TargetBackwardLanes = candidate.TargetBackwardLanes,
                TransitionOuterEdge = candidate.ContinuationEdge,
                TransitionReverseSnapshot = candidate.TransitionReverseSnapshot
            };

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest request))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot queue short-edge direct replacement apply definition shortEdge={FormatEntity(candidate.ShortEdge)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} transitionNode={FormatEntity(candidate.RemovableNode)} continuation={FormatEntity(candidate.ContinuationEdge)}: replacement request could not be built.");
                return false;
            }

            JobHandle createDefinitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = request,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(result);

            m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
            result = createDefinitionJobHandle;
            m_QueuedReplacementCandidates.Add(replacementCandidate);

            string snapshotDetail = candidate.TransitionReverseSnapshot != null
                ? candidate.TransitionReverseSnapshot.Detail
                : "snapshot=unavailable";
            Mod.LogDiagnostic($"[IntersectionTool] Queued short-edge direct replacement apply definition shortEdge={FormatEntity(candidate.ShortEdge)} transitionNode={FormatEntity(candidate.RemovableNode)} continuation={FormatEntity(candidate.ContinuationEdge)} farNode={FormatEntity(candidate.FarNode)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} flags={request.Flags} fixedIndex={request.FixedIndex} randomSeed={request.RandomSeed} laneRepair=short-edge-transition {snapshotDetail}.");
            return true;
        }

        private bool QueueNodeMergeDefinition(NodeMergeCandidate candidate, ref JobHandle result)
        {
            if (candidate.MergeRequest.Prefab == Entity.Null ||
                candidate.MergeRequest.FirstDeletion.Edge == Entity.Null ||
                candidate.MergeRequest.SecondDeletion.Edge == Entity.Null ||
                candidate.MergeRequest.StartNode == Entity.Null ||
                candidate.MergeRequest.EndNode == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot queue road-node merge definition shortEdge={FormatEntity(candidate.ShortEdge)} removableNode={FormatEntity(candidate.RemovableNode)} continuation={FormatEntity(candidate.ContinuationEdge)}: incomplete merge request.");
                return false;
            }

            JobHandle createDefinitionJobHandle = new CreateNodeMergeDefinitionJob
            {
                Request = candidate.MergeRequest,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(result);

            m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
            result = createDefinitionJobHandle;
            return true;
        }

        private void QueueShortEdgeReplacementPreviews(
            ref JobHandle result,
            string reason,
            out int queuedCount,
            out int degradedCount)
        {
            queuedCount = 0;
            degradedCount = 0;

            if (m_ShortEdgeReplacementPreviewAttempted)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview batch was already attempted; skipping duplicate queue reason={reason} queuedDefinitions={m_ShortEdgeReplacementPreviewQueuedCount} activePreview={m_HasShortEdgeReplacementPreviewDefinitions}.");
                return;
            }

            m_ShortEdgeReplacementPreviewAttempted = true;
            int initialDefinitionCount = m_ShortEdgeReplacementPreviewQueuedCount;
            for (int i = 0; i < m_PreviewNodeMergeCandidates.Count; i++)
            {
                NodeMergeCandidate mergeCandidate = m_PreviewNodeMergeCandidates[i];
                if (TryQueueShortEdgeReplacementPreview(mergeCandidate, ref result))
                {
                    queuedCount++;
                }
                else
                {
                    degradedCount++;
                }
            }

            int createdDefinitionCount = m_ShortEdgeReplacementPreviewQueuedCount - initialDefinitionCount;
            Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview batch complete reason={reason} queuedCandidates={queuedCount} queuedDefinitions={createdDefinitionCount} totalQueuedDefinitions={m_ShortEdgeReplacementPreviewQueuedCount} degraded={degradedCount} roadNodeMergeApplyCandidates={m_PreviewNodeMergeCandidates.Count}.");
        }

        private bool TryQueueShortEdgeReplacementPreview(NodeMergeCandidate candidate, ref JobHandle result)
        {
            if (candidate.ShortEdge == Entity.Null ||
                candidate.TargetPrefab == Entity.Null ||
                !EntityManager.Exists(candidate.ShortEdge))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview downgraded: missing short edge or target prefab shortEdge={FormatEntity(candidate.ShortEdge)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)}; road-node merge apply candidate remains queued.");
                return false;
            }

            Entity previewOriginalEdge = candidate.ShortEdge;
            string previewSource = "LiveShortEdge";
            string previewSourceDetail = "no temp short-edge source was requested";
            Entity continuationPreviewEdge = Entity.Null;
            string continuationPreviewDetail = "no continuation preview source was requested";
            bool requiresContinuationPreview = m_PreviewCandidates.Count > 0 &&
                                               candidate.Mode == NodeMergeMode.SourcePrefabContinuation;
            if (m_PreviewCandidates.Count > 0)
            {
                if (TryFindPreviewShortEdgeSource(candidate, out Entity tempShortEdge, out previewSourceDetail))
                {
                    previewOriginalEdge = tempShortEdge;
                    previewSource = "TempShortEdge";
                }
                else
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview downgraded: normal split preview is active but no temp short-edge source could be found shortEdge={FormatEntity(candidate.ShortEdge)} removableNode={FormatEntity(candidate.RemovableNode)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} detail={previewSourceDetail}; road-node merge apply candidate remains queued.");
                    return false;
                }

                if (requiresContinuationPreview &&
                    !TryFindPreviewContinuationSource(candidate, out continuationPreviewEdge, out continuationPreviewDetail))
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview downgraded: normal split preview is active but no temp continuation source could be found shortEdge={FormatEntity(candidate.ShortEdge)} continuation={FormatEntity(candidate.ContinuationEdge)} removableNode={FormatEntity(candidate.RemovableNode)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} detail={continuationPreviewDetail}; road-node merge apply candidate remains queued.");
                    return false;
                }

                if (!requiresContinuationPreview)
                {
                    continuationPreviewDetail = $"{candidate.Mode} skips companion source preview; preview intentionally remains the short replacement segment only";
                }
            }

            ReplacementCandidate replacementCandidate = new ReplacementCandidate
            {
                Node = candidate.Node,
                SplitNode = Entity.Null,
                OriginalEdge = candidate.ShortEdge,
                PocketEdge = previewOriginalEdge,
                SourcePrefab = candidate.SourcePrefab,
                TargetPrefab = candidate.TargetPrefab,
                LaneRepairMode = candidate.LaneRepairMode,
                InvertTarget = candidate.InvertTarget,
                HasTargetUpgrade = candidate.HasTargetUpgrade,
                TargetUpgrade = candidate.TargetUpgrade,
                HitPosition = candidate.ExpectedHitPosition,
                OriginalForwardLanes = candidate.OriginalForwardLanes,
                OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                TargetForwardLanes = candidate.TargetForwardLanes,
                TargetBackwardLanes = candidate.TargetBackwardLanes
            };

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest request))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview downgraded: could not build replacement definition shortEdge={FormatEntity(candidate.ShortEdge)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)}; road-node merge apply candidate remains queued.");
                return false;
            }

            bool queueContinuationPreview = continuationPreviewEdge != Entity.Null;
            ReplacementDefinitionRequest continuationRequest = default;
            string continuationBuildDetail = "not queued";
            if (queueContinuationPreview &&
                !TryBuildShortEdgeContinuationSourceDefinitionRequest(
                    candidate,
                    previewOriginalEdge,
                    continuationPreviewEdge,
                    candidate.SourcePrefab,
                    out continuationRequest,
                    out continuationBuildDetail))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Short-edge replacement hover preview downgraded: could not build bridged continuation source preview definition before queueing short-edge replacement shortEdgePreview={FormatEntity(previewOriginalEdge)} continuation={FormatEntity(candidate.ContinuationEdge)} continuationPreview={FormatEntity(continuationPreviewEdge)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} detail={continuationPreviewDetail} buildDetail={continuationBuildDetail}; road-node merge apply candidate remains queued.");
                return false;
            }

            request.PreviewOnly = true;
            if (queueContinuationPreview)
            {
                continuationRequest.PreviewOnly = true;
            }

            JobHandle createDefinitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = request,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(result);

            m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
            result = createDefinitionJobHandle;

            int createdDefinitions = 1;
            if (queueContinuationPreview)
            {
                JobHandle continuationDefinitionJobHandle = new CreateReplacementDefinitionJob
                {
                    Request = continuationRequest,
                    ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                }.Schedule(result);
                m_ToolOutputBarrier.AddJobHandleForProducer(continuationDefinitionJobHandle);
                result = continuationDefinitionJobHandle;
                createdDefinitions++;
                Mod.LogDiagnostic($"[IntersectionTool] Queued continuation source hover preview definition for short-edge fallback continuation={FormatEntity(candidate.ContinuationEdge)} continuationPreview={FormatEntity(continuationPreviewEdge)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} flags={continuationRequest.Flags} continuationComposition=source-definition detail={continuationPreviewDetail} buildDetail={continuationBuildDetail}.");
            }

            m_HasReplacementPreviewDefinitions = true;
            m_HasShortEdgeReplacementPreviewDefinitions = true;
            m_ShortEdgeReplacementPreviewQueuedCount += createdDefinitions;

            Mod.LogDiagnostic($"[IntersectionTool] Queued short-edge fallback hover preview definitions shortEdge={FormatEntity(candidate.ShortEdge)} previewOriginal={FormatEntity(previewOriginalEdge)} previewSource={previewSource} removableNode={FormatEntity(candidate.RemovableNode)} continuation={FormatEntity(candidate.ContinuationEdge)} continuationPreview={FormatEntity(continuationPreviewEdge)} farNode={FormatEntity(candidate.FarNode)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} shortLength={candidate.ShortEdgeLength:0.##}m continuationLength={candidate.ContinuationEdgeLength:0.##}m mergedLength={candidate.MergedLength:0.##}m expectedSplit={candidate.ExpectedSplitDistance:0.##}m expectedPocket={candidate.ExpectedPocketDistance:0.##}m shortFlags={request.Flags} continuationFlags={(queueContinuationPreview ? continuationRequest.Flags.ToString() : "none")} fixedIndex={request.FixedIndex} randomSeed={request.RandomSeed} createdDefinitions={createdDefinitions} continuationComposition={(queueContinuationPreview ? "source-definition" : "none")} noMergeDefinitions=true noDeleteDefinitions=true noSplitNodePreview=true sourceDetail={previewSourceDetail} continuationDetail={continuationPreviewDetail} continuationBuildDetail={continuationBuildDetail}.");
            return true;
        }

        private bool TryFindPreviewShortEdgeSource(
            NodeMergeCandidate candidate,
            out Entity previewEdge,
            out string detail)
        {
            previewEdge = Entity.Null;
            detail = string.Empty;

            if (!EntityManager.TryGetComponent(candidate.ShortEdge, out Edge shortEdge) ||
                !EntityManager.TryGetComponent(candidate.ShortEdge, out Curve shortCurve))
            {
                detail = "missing live shortEdge Edge/Curve";
                return false;
            }

            float expectedLength = shortCurve.m_Length > 0.01f
                ? shortCurve.m_Length
                : candidate.ShortEdgeLength;
            float bestScore = float.MaxValue;
            float bestRejectedLengthError = float.MaxValue;
            Entity bestRejectedEdge = Entity.Null;
            int tempEdgeCount = 0;
            int identityMatches = 0;
            int sourcePrefabMatches = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    Temp temp = temps[i];
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0 ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    bool originalMatch = temp.m_Original == candidate.ShortEdge;
                    bool endpointMatch =
                        (IsSameOrTempOriginalNode(edge.m_Start, shortEdge.m_Start) && IsSameOrTempOriginalNode(edge.m_End, shortEdge.m_End)) ||
                        (IsSameOrTempOriginalNode(edge.m_Start, shortEdge.m_End) && IsSameOrTempOriginalNode(edge.m_End, shortEdge.m_Start));
                    if (!originalMatch && !endpointMatch)
                    {
                        continue;
                    }

                    identityMatches++;
                    if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                        prefabRef.m_Prefab != candidate.TargetPrefab)
                    {
                        continue;
                    }

                    sourcePrefabMatches++;
                    float lengthError = math.abs(curve.m_Length - expectedLength);
                    float score = lengthError + (prefabRef.m_Prefab == candidate.SourcePrefab ? 0f : 0.5f) + (originalMatch ? 0f : 0.25f);
                    if (lengthError > PocketEdgeLengthTolerance)
                    {
                        if (lengthError < bestRejectedLengthError)
                        {
                            bestRejectedLengthError = lengthError;
                            bestRejectedEdge = edgeEntity;
                        }

                        continue;
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        previewEdge = edgeEntity;
                    }
                }
            }

            if (previewEdge != Entity.Null)
            {
                detail = $"tempPreviewEdge={FormatEntity(previewEdge)} expectedLength={expectedLength:0.##}m tempEdges={tempEdgeCount} identityMatches={identityMatches} sourceOrTargetPrefabMatches={sourcePrefabMatches}";
                return true;
            }

            detail = $"expectedLength={expectedLength:0.##}m tempEdges={tempEdgeCount} identityMatches={identityMatches} sourceOrTargetPrefabMatches={sourcePrefabMatches} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}";
            return false;
        }

        private bool TryFindPreviewContinuationSource(
            NodeMergeCandidate candidate,
            out Entity previewEdge,
            out string detail)
        {
            if (TryFindPreviewSourceEdge(
                candidate.ContinuationEdge,
                candidate.SourcePrefab,
                "continuation",
                out previewEdge,
                out detail))
            {
                return true;
            }

            string tempDetail = detail;
            previewEdge = Entity.Null;
            if (!EntityManager.Exists(candidate.ContinuationEdge) ||
                !EntityManager.TryGetComponent(candidate.ContinuationEdge, out Curve continuationCurve) ||
                !EntityManager.TryGetComponent(candidate.ContinuationEdge, out PrefabRef continuationPrefab) ||
                continuationPrefab.m_Prefab != candidate.SourcePrefab)
            {
                detail = $"{tempDetail}; liveContinuationSourceFallback=unavailable liveExists={EntityManager.Exists(candidate.ContinuationEdge)} livePrefab={GetPrefabName(candidate.ContinuationEdge)} expectedSourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)}";
                return false;
            }

            previewEdge = candidate.ContinuationEdge;
            float continuationLength = continuationCurve.m_Length > 0.01f
                ? continuationCurve.m_Length
                : MathUtils.Length(continuationCurve.m_Bezier);
            detail = $"{tempDetail}; liveContinuationSourceFallback=using-live-continuation-source-recreate liveContinuation={FormatEntity(candidate.ContinuationEdge)} liveLength={continuationLength:0.##}m livePrefab={GetPrefabNameFromPrefab(continuationPrefab.m_Prefab)}";
            return true;
        }

        private bool TryBuildShortEdgeContinuationSourceDefinitionRequest(
            NodeMergeCandidate candidate,
            Entity previewShortEdge,
            Entity continuationPreviewEdge,
            Entity sourcePrefab,
            out ReplacementDefinitionRequest request,
            out string detail)
        {
            request = default;
            detail = string.Empty;

            if (continuationPreviewEdge != candidate.ContinuationEdge)
            {
                bool built = TryBuildPreviewSourceDefinitionRequest(continuationPreviewEdge, sourcePrefab, out request);
                detail = built
                    ? $"mode=temp-continuation-source previewShortEdge={FormatEntity(previewShortEdge)} continuationPreview={FormatEntity(continuationPreviewEdge)}"
                    : $"mode=temp-continuation-source failed previewShortEdge={FormatEntity(previewShortEdge)} continuationPreview={FormatEntity(continuationPreviewEdge)}";
                return built;
            }

            if (!TryGetPreviewShortEdgeRemovableNode(candidate, previewShortEdge, out Entity previewRemovableNode, out string removableDetail))
            {
                detail = $"mode=live-continuation-bridged failedToFindPreviewRemovableNode previewShortEdge={FormatEntity(previewShortEdge)} {removableDetail}";
                return false;
            }

            if (!EntityManager.TryGetComponent(candidate.ContinuationEdge, out Edge continuationEdge) ||
                !EntityManager.TryGetComponent(candidate.ContinuationEdge, out Curve continuationCurve))
            {
                detail = $"mode=live-continuation-bridged missing continuation Edge/Curve continuation={FormatEntity(candidate.ContinuationEdge)} {removableDetail}";
                return false;
            }

            Entity startNode = continuationEdge.m_Start;
            Entity endNode = continuationEdge.m_End;
            bool replacedStart = continuationEdge.m_Start == candidate.RemovableNode;
            bool replacedEnd = continuationEdge.m_End == candidate.RemovableNode;
            if (replacedStart)
            {
                startNode = previewRemovableNode;
            }
            else if (replacedEnd)
            {
                endNode = previewRemovableNode;
            }
            else
            {
                detail = $"mode=live-continuation-bridged continuation does not touch removable node continuation={FormatEntity(candidate.ContinuationEdge)} start={FormatEntity(continuationEdge.m_Start)} end={FormatEntity(continuationEdge.m_End)} removableNode={FormatEntity(candidate.RemovableNode)} {removableDetail}";
                return false;
            }

            if (!TryBuildPreviewSourceDefinitionRequest(
                    candidate.ContinuationEdge,
                    sourcePrefab,
                    startNode,
                    endNode,
                    continuationCurve.m_Bezier,
                    continuationCurve.m_Length,
                    out request))
            {
                detail = $"mode=live-continuation-bridged failedToBuildSourceDefinition continuation={FormatEntity(candidate.ContinuationEdge)} previewRemovableNode={FormatEntity(previewRemovableNode)} replacedSide={(replacedStart ? "start" : "end")} {removableDetail}";
                return false;
            }

            detail = $"mode=live-continuation-bridged continuation={FormatEntity(candidate.ContinuationEdge)} previewShortEdge={FormatEntity(previewShortEdge)} previewRemovableNode={FormatEntity(previewRemovableNode)} replacedSide={(replacedStart ? "start" : "end")} startNode={FormatEntity(startNode)} endNode={FormatEntity(endNode)} {removableDetail}";
            return true;
        }

        private bool TryGetPreviewShortEdgeRemovableNode(
            NodeMergeCandidate candidate,
            Entity previewShortEdge,
            out Entity previewRemovableNode,
            out string detail)
        {
            previewRemovableNode = Entity.Null;
            detail = string.Empty;

            if (!EntityManager.TryGetComponent(previewShortEdge, out Edge previewEdge))
            {
                detail = $"missing preview short-edge Edge previewShortEdge={FormatEntity(previewShortEdge)}";
                return false;
            }

            bool startMatches = IsSameOrTempOriginalNode(previewEdge.m_Start, candidate.RemovableNode);
            bool endMatches = IsSameOrTempOriginalNode(previewEdge.m_End, candidate.RemovableNode);
            if (startMatches == endMatches)
            {
                detail = $"ambiguous removable endpoint previewShortEdge={FormatEntity(previewShortEdge)} start={FormatEntity(previewEdge.m_Start)} end={FormatEntity(previewEdge.m_End)} removableNode={FormatEntity(candidate.RemovableNode)} startMatches={startMatches} endMatches={endMatches}";
                return false;
            }

            previewRemovableNode = startMatches ? previewEdge.m_Start : previewEdge.m_End;
            detail = $"previewRemovableNode={FormatEntity(previewRemovableNode)} previewShortStart={FormatEntity(previewEdge.m_Start)} previewShortEnd={FormatEntity(previewEdge.m_End)} removableSide={(startMatches ? "start" : "end")}";
            return true;
        }

        private bool TryFindPreviewSourceEdge(
            Entity sourceEdge,
            Entity sourcePrefab,
            string label,
            out Entity previewEdge,
            out string detail)
        {
            previewEdge = Entity.Null;
            detail = string.Empty;

            if (!EntityManager.TryGetComponent(sourceEdge, out Edge sourceEdgeData) ||
                !EntityManager.TryGetComponent(sourceEdge, out Curve sourceCurve))
            {
                detail = $"missing live {label} Edge/Curve";
                return false;
            }

            float expectedLength = sourceCurve.m_Length > 0.01f
                ? sourceCurve.m_Length
                : MathUtils.Length(sourceCurve.m_Bezier);
            float bestScore = float.MaxValue;
            float bestRejectedLengthError = float.MaxValue;
            Entity bestRejectedEdge = Entity.Null;
            int tempEdgeCount = 0;
            int identityMatches = 0;
            int sourcePrefabMatches = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    Temp temp = temps[i];
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0 ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    bool originalMatch = temp.m_Original == sourceEdge;
                    bool endpointMatch =
                        (IsSameOrTempOriginalNode(edge.m_Start, sourceEdgeData.m_Start) && IsSameOrTempOriginalNode(edge.m_End, sourceEdgeData.m_End)) ||
                        (IsSameOrTempOriginalNode(edge.m_Start, sourceEdgeData.m_End) && IsSameOrTempOriginalNode(edge.m_End, sourceEdgeData.m_Start));
                    if (!originalMatch && !endpointMatch)
                    {
                        continue;
                    }

                    identityMatches++;
                    if (prefabRef.m_Prefab != sourcePrefab)
                    {
                        continue;
                    }

                    sourcePrefabMatches++;
                    float lengthError = math.abs(curve.m_Length - expectedLength);
                    float score = lengthError + (originalMatch ? 0f : 0.25f);
                    if (lengthError > PocketEdgeLengthTolerance)
                    {
                        if (lengthError < bestRejectedLengthError)
                        {
                            bestRejectedLengthError = lengthError;
                            bestRejectedEdge = edgeEntity;
                        }

                        continue;
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        previewEdge = edgeEntity;
                    }
                }
            }

            if (previewEdge != Entity.Null)
            {
                detail = $"tempPreviewEdge={FormatEntity(previewEdge)} expectedLength={expectedLength:0.##}m lengthTolerance={PocketEdgeLengthTolerance:0.##}m tempEdges={tempEdgeCount} identityMatches={identityMatches} sourcePrefabMatches={sourcePrefabMatches}";
                return true;
            }

            detail = $"expectedLength={expectedLength:0.##}m lengthTolerance={PocketEdgeLengthTolerance:0.##}m tempEdges={tempEdgeCount} identityMatches={identityMatches} sourcePrefabMatches={sourcePrefabMatches} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}";
            return false;
        }

        private void MarkNoSplitPreviewReady(Entity nodeEntity)
        {
            m_OverlaySystem.Clear();
            m_PreviewIntersection = nodeEntity;
            m_PreviewEdge = Entity.Null;
            m_PreviewEdgeCount = 0;
            m_PreviewReady = true;
            m_PreviewValidationPending = false;
            m_PreviewDirty = false;
            m_ApplyPreviewNextFrame = false;
            m_NodeMergeDefinitionsReadyForApply = false;
            m_HasReplacementPreviewDefinitions = false;
            m_HasShortEdgeReplacementPreviewDefinitions = false;
            m_NormalReplacementPreviewDefinitionsQueued = false;
            m_ShortEdgeReplacementPreviewAttempted = false;
            m_ShortEdgeReplacementPreviewQueuedCount = 0;
            m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
            m_PreviewCandidates.Clear();
            m_NextPreviewCandidates.Clear();
            m_PreviewNodeMergeCandidates.Clear();
            m_QueuedReplacementCandidates.Clear();
        }

        private bool TryFindPreviewSplitNode(SplitCandidate candidate, out Entity splitNode)
        {
            splitNode = Entity.Null;

            using (NativeArray<Entity> entities = m_TempSplitNodeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempSplitNodeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    Temp temp = temps[i];
                    if ((temp.m_Flags & TempFlags.Replace) != TempFlags.Replace ||
                        (temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0)
                    {
                        continue;
                    }

                    if (temp.m_Original == candidate.Edge &&
                        math.abs(temp.m_CurvePosition - candidate.CurvePosition) <= PreviewSplitNodeTolerance)
                    {
                        splitNode = entities[i];
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryApplyReplacementPreview(SplitCandidate candidate, ref JobHandle result)
        {
            if (candidate.TargetPrefab == Entity.Null)
            {
                return false;
            }

            if (!TryFindPreviewSplitNode(candidate, out Entity splitNode) ||
                !TryFindPreviewPocketEdge(candidate, splitNode, out Entity pocketEdge, out float lengthError))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot preview pocket lane replacement original={FormatEntity(candidate.Edge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)}: preview pocket edge was not found.");
                return false;
            }

            if (!TryFindPreviewOuterEdge(
                candidate,
                splitNode,
                pocketEdge,
                out Entity outerEdge,
                out float outerLengthError))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot preview pocket lane replacement original={FormatEntity(candidate.Edge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)}: preview outer edge was not found.");
                return false;
            }

            ReplacementCandidate replacementCandidate = new ReplacementCandidate
            {
                Node = candidate.Node,
                FarNode = candidate.FarNode,
                SplitNode = splitNode,
                OriginalEdge = candidate.Edge,
                PocketEdge = pocketEdge,
                SourcePrefab = candidate.SourcePrefab,
                TargetPrefab = candidate.TargetPrefab,
                LaneRepairMode = candidate.LaneRepairMode,
                InvertTarget = candidate.InvertTarget,
                HasTargetUpgrade = candidate.HasTargetUpgrade,
                TargetUpgrade = candidate.TargetUpgrade,
                HitPosition = candidate.HitPosition,
                OriginalForwardLanes = candidate.OriginalForwardLanes,
                OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                TargetForwardLanes = candidate.TargetForwardLanes,
                TargetBackwardLanes = candidate.TargetBackwardLanes
            };

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest definitionRequest))
            {
                return false;
            }

            if (!TryBuildPreviewSourceDefinitionRequest(outerEdge, candidate.SourcePrefab, out ReplacementDefinitionRequest outerDefinitionRequest))
            {
                return false;
            }

            definitionRequest.PreviewOnly = true;
            outerDefinitionRequest.PreviewOnly = true;

            JobHandle definitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = definitionRequest,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(result);
            m_ToolOutputBarrier.AddJobHandleForProducer(definitionJobHandle);
            result = definitionJobHandle;

            JobHandle outerDefinitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = outerDefinitionRequest,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(definitionJobHandle);

            m_ToolOutputBarrier.AddJobHandleForProducer(outerDefinitionJobHandle);
            result = outerDefinitionJobHandle;

            m_HasReplacementPreviewDefinitions = true;

            Mod.LogDiagnostic($"[IntersectionTool] Created pocket lane replacement definition preview original={FormatEntity(candidate.Edge)} pocket={FormatEntity(pocketEdge)} outer={FormatEntity(outerEdge)} splitNode={FormatEntity(splitNode)} splitNodePrefab=definition-driven sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} targetUpgrade={(candidate.HasTargetUpgrade ? candidate.TargetUpgrade.m_Flags.ToString() : "none")} pocketFlags={definitionRequest.Flags} outerFlags={outerDefinitionRequest.Flags} collisionValidation=vanilla-disabled pocketComposition=definition-driven outerComposition=source-definition lanes={candidate.OriginalForwardLanes}/{candidate.OriginalBackwardLanes}->{candidate.TargetForwardLanes}/{candidate.TargetBackwardLanes} pocketLengthError={lengthError:0.##}m outerLengthError={outerLengthError:0.##}m.");
            return true;
        }

        private bool TryFindPreviewPocketEdge(
            SplitCandidate candidate,
            Entity splitNode,
            out Entity pocketEdge,
            out float lengthError)
        {
            pocketEdge = Entity.Null;
            lengthError = 0f;

            float bestScore = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            Entity bestRejectedEdge = Entity.Null;
            float bestRejectedLengthError = float.MaxValue;
            int tempEdgeCount = 0;
            int connectedMatchCount = 0;
            int prefabMatchCount = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    Temp temp = temps[i];
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0 ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    bool connectsSplitNode = edge.m_Start == splitNode || edge.m_End == splitNode;
                    Entity otherNode = edge.m_Start == splitNode ? edge.m_End : edge.m_Start;
                    bool connectsCandidateNode = connectsSplitNode && IsSameOrTempOriginalNode(otherNode, candidate.Node);
                    if (!connectsCandidateNode)
                    {
                        continue;
                    }

                    connectedMatchCount++;
                    if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                        prefabRef.m_Prefab != candidate.TargetPrefab)
                    {
                        continue;
                    }

                    prefabMatchCount++;
                    float candidateLengthError = math.abs(curve.m_Length - candidate.SplitDistance);
                    if (candidateLengthError > PocketEdgeLengthTolerance)
                    {
                        if (candidateLengthError < bestRejectedLengthError)
                        {
                            bestRejectedLengthError = candidateLengthError;
                            bestRejectedEdge = edgeEntity;
                        }

                        continue;
                    }

                    float score = candidateLengthError;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestLengthError = candidateLengthError;
                        bestEdge = edgeEntity;
                    }
                }
            }

            if (bestEdge == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find preview pocket edge original={FormatEntity(candidate.Edge)} splitNode={FormatEntity(splitNode)} expectedDistance={candidate.SplitDistance:0.##}m tempEdges={tempEdgeCount} connectedMatches={connectedMatchCount} prefabMatches={prefabMatchCount} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}.");
                return false;
            }

            pocketEdge = bestEdge;
            lengthError = bestLengthError;
            return true;
        }

        private bool TryFindPreviewOuterEdge(
            SplitCandidate candidate,
            Entity splitNode,
            Entity pocketEdge,
            out Entity outerEdge,
            out float lengthError)
        {
            outerEdge = Entity.Null;
            lengthError = 0f;

            float expectedLength = -1f;
            if (EntityManager.TryGetComponent(candidate.Edge, out Curve originalCurve))
            {
                expectedLength = math.max(0f, originalCurve.m_Length - candidate.SplitDistance);
            }

            float bestScore = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            Entity bestRejectedEdge = Entity.Null;
            float bestRejectedLengthError = float.MaxValue;
            int tempEdgeCount = 0;
            int connectedMatchCount = 0;
            int prefabMatchCount = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    if (edgeEntity == pocketEdge)
                    {
                        continue;
                    }

                    Temp temp = temps[i];
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0 ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    if (edge.m_Start != splitNode && edge.m_End != splitNode)
                    {
                        continue;
                    }

                    Entity otherNode = edge.m_Start == splitNode ? edge.m_End : edge.m_Start;
                    if (IsSameOrTempOriginalNode(otherNode, candidate.Node))
                    {
                        continue;
                    }

                    connectedMatchCount++;
                    if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                        prefabRef.m_Prefab != candidate.TargetPrefab)
                    {
                        continue;
                    }

                    prefabMatchCount++;
                    float candidateLengthError = expectedLength >= 0f
                        ? math.abs(curve.m_Length - expectedLength)
                        : 0f;
                    if (expectedLength >= 0f && candidateLengthError > PocketEdgeLengthTolerance)
                    {
                        if (candidateLengthError < bestRejectedLengthError)
                        {
                            bestRejectedLengthError = candidateLengthError;
                            bestRejectedEdge = edgeEntity;
                        }

                        continue;
                    }

                    float sourcePrefabPenalty = prefabRef.m_Prefab == candidate.SourcePrefab ? 0f : PocketEdgeLengthTolerance;
                    float score = candidateLengthError + sourcePrefabPenalty;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestLengthError = candidateLengthError;
                        bestEdge = edgeEntity;
                    }
                }
            }

            if (bestEdge == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find preview outer edge original={FormatEntity(candidate.Edge)} splitNode={FormatEntity(splitNode)} expectedLength={FormatMeters(expectedLength)} tempEdges={tempEdgeCount} connectedMatches={connectedMatchCount} prefabMatches={prefabMatchCount} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}.");
                return false;
            }

            outerEdge = bestEdge;
            lengthError = bestLengthError;
            return true;
        }

        private bool IsSameOrTempOriginalNode(Entity node, Entity originalNode)
        {
            if (node == originalNode)
            {
                return true;
            }

            return node != Entity.Null &&
                   EntityManager.TryGetComponent(node, out Temp temp) &&
                   temp.m_Original == originalNode &&
                   (temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) == (TempFlags)0;
        }
    }
}
