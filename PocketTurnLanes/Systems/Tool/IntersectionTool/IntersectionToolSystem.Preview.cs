using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.PrefabMatching;
using PocketTurnLanes.Tool.SplitGeometry;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using static PocketTurnLanes.Tool.SplitGeometry.SplitGeometryMath;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
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
            m_NodeMergeVerificationStartedFrame = -1;
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
            JobHandle result = DestroyToolDefinitions(inputDeps);
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

                if (!TryCreateSplitRetryRequest(candidate.Attempt, candidate.TargetPocketLength, out SplitRetryRequest retryRequest))
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Preview split still has no generated node edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={candidate.Attempt} distance={candidate.SplitDistance:0.##}m; no more retry room.");
                    if (TryPromoteExhaustedSplitToShortEdgeFallback(candidate, "max retry attempts reached without a generated temp split node", out bool queuedSplitFallback))
                    {
                        exhaustedFallbackCount++;
                        needsRetry |= queuedSplitFallback;
                    }

                    continue;
                }

                if (!TryBuildSplitDefinitionPlan(
                        candidate.Node,
                        candidate.Edge,
                        out SplitDefinitionPlan splitPlan,
                        retryRequest.RequestedPocketLength))
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Preview retry cannot be prepared edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} {retryRequest.Detail} requestedPocket={splitPlan.TargetPocketLength:0.##}m requestedBeforeCap={retryRequest.RequestedPocketLength:0.##}m.");
                    if (TryPromoteExhaustedSplitToShortEdgeFallback(candidate, $"retry split could not be prepared; {retryRequest.Detail}", out bool queuedSplitFallback))
                    {
                        exhaustedFallbackCount++;
                        needsRetry |= queuedSplitFallback;
                    }

                    continue;
                }

                if (retryRequest.RequiresMinimumProgress &&
                    !HasMinimumRetryProgress(candidate.SplitDistance, splitPlan.SplitDistance))
                {
                    exhaustedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Preview retry cannot move far enough edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} {retryRequest.Detail} previous={candidate.SplitDistance:0.##}m next={splitPlan.SplitDistance:0.##}m.");
                    if (TryPromoteExhaustedSplitToShortEdgeFallback(candidate, $"retry split could not move far enough; {retryRequest.Detail} previous={candidate.SplitDistance:0.##}m next={splitPlan.SplitDistance:0.##}m", out bool queuedSplitFallback))
                    {
                        exhaustedFallbackCount++;
                        needsRetry |= queuedSplitFallback;
                    }

                    continue;
                }

                retryCount++;
                needsRetry = true;
                m_NextPreviewCandidates.Add(UpdateSplitCandidate(candidate, splitPlan, retryRequest.NextAttempt));
                Mod.LogDiagnostic($"[IntersectionTool] Preview split missing edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)}; retry attempt={retryRequest.NextAttempt} {retryRequest.Detail} requestedPocket={splitPlan.TargetPocketLength:0.##}m requestedBeforeCap={retryRequest.RequestedPocketLength:0.##}m previousPocket={candidate.TargetPocketLength:0.##}m split={splitPlan.CurvePosition:0.###} target={splitPlan.TargetDistance:0.##}m distance={splitPlan.SplitDistance:0.##}m intersection={splitPlan.IntersectionDistance:0.##}m pocket={splitPlan.PocketDistance:0.##}m.");
            }

            if (needsRetry)
            {
                return RequeueSplitPreview(m_NextPreviewCandidates, inputDeps, visibleCount, retryCount, exhaustedCount);
            }

            if (m_NextPreviewCandidates.Count == 0)
            {
                int replacementPreviewDefinitionCount = m_ReplacementPreviewDefinitionQuery.CalculateEntityCount();
                applyMode = ApplyMode.Clear;
                JobHandle clearResult = DestroyToolDefinitions(inputDeps);
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

        private bool TryPromoteExhaustedSplitToShortEdgeFallback(
            SplitCandidate candidate,
            string reason,
            out bool queuedSplitFallback)
        {
            queuedSplitFallback = false;

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

            if (!m_ReplacementPrefabMatcher.TryFindPocketLaneReplacementPrefab(
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
                if (!candidate.LateHalfFallbackAttempted &&
                    TryBuildLateHalfPocketSplitDefinitionPlan(
                        candidate.Node,
                        candidate.Edge,
                        out SplitDefinitionPlan lateHalfSplitPlan))
                {
                    SplitCandidate lateHalfCandidate = CreateSplitCandidate(
                        candidate.Node,
                        candidate.Edge,
                        lateHalfSplitPlan,
                        prefabMatch);
                    m_NextPreviewCandidates.Add(lateHalfCandidate);
                    queuedSplitFallback = true;
                    Mod.LogDiagnostic($"[IntersectionTool] Promoted exhausted split preview to late half-pocket fallback edge={FormatEntity(candidate.Edge)} node={FormatEntity(candidate.Node)} farNode={FormatEntity(lateHalfSplitPlan.FarNode)} reason={reason} splitDistance={candidate.SplitDistance:0.##}m targetPocket={candidate.TargetPocketLength:0.##}m lateHalfSplit={lateHalfSplitPlan.CurvePosition:0.###} lateHalfDistance={lateHalfSplitPlan.SplitDistance:0.##}m lateHalfPocket={lateHalfSplitPlan.TargetPocketLength:0.##}m.");
                    return true;
                }

                Mod.LogDiagnostic($"[IntersectionTool] Exhausted split is not eligible for short-edge fallback edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} node={FormatEntity(candidate.Node)} reason={reason} lateHalfAlreadyAttempted={candidate.LateHalfFallbackAttempted}.");
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
            JobHandle result = DestroyToolDefinitions(inputDeps);
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
                if (!TryRebuildSplitDefinitionPlan(candidate, out SplitDefinitionPlan splitPlan))
                {
                    Mod.LogDiagnostic($"[IntersectionTool] Cannot rebuild preview split edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={candidate.Attempt}.");
                    continue;
                }

                JobHandle createDefinitionJobHandle = ScheduleSplitDefinition(splitPlan.Request, result);

                m_PreviewCandidates.Add(UpdateSplitCandidate(candidate, splitPlan, candidate.Attempt));

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

        private bool TryRebuildSplitDefinitionPlan(SplitCandidate candidate, out SplitDefinitionPlan splitPlan)
        {
            if (candidate.GeometryMode == SplitGeometryMode.LateHalfPocket &&
                candidate.Attempt == 0)
            {
                return TryBuildLateHalfPocketSplitDefinitionPlan(
                    candidate.Node,
                    candidate.Edge,
                    out splitPlan);
            }

            return TryBuildSplitDefinitionPlan(
                candidate.Node,
                candidate.Edge,
                out splitPlan,
                candidate.TargetPocketLength);
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

                SplitDefinitionRequest request = new SplitDefinitionRequest
                {
                    Edge = candidate.Edge,
                    Prefab = candidate.SourcePrefab,
                    HitPosition = candidate.HitPosition,
                    CurvePosition = candidate.CurvePosition,
                    RandomSeed = GetDefinitionRandomSeed(candidate.Edge)
                };

                JobHandle createDefinitionJobHandle = ScheduleSplitDefinition(request, result);
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

            ReplacementCandidate replacementCandidate = CreateShortEdgeReplacementCandidate(
                candidate,
                candidate.RemovableNode,
                candidate.ShortEdge,
                true);

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest request))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot queue short-edge direct replacement apply definition shortEdge={FormatEntity(candidate.ShortEdge)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} transitionNode={FormatEntity(candidate.RemovableNode)} continuation={FormatEntity(candidate.ContinuationEdge)}: replacement request could not be built.");
                return false;
            }

            JobHandle createDefinitionJobHandle = ScheduleReplacementDefinition(request, result);
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
            result = TrackToolUpdateJobHandle(createDefinitionJobHandle);
            return true;
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

            ReplacementCandidate replacementCandidate = CreateReplacementCandidate(candidate, splitNode, pocketEdge);

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

            JobHandle definitionJobHandle = ScheduleReplacementDefinition(definitionRequest, result);
            result = definitionJobHandle;

            JobHandle outerDefinitionJobHandle = ScheduleReplacementDefinition(outerDefinitionRequest, definitionJobHandle);
            result = outerDefinitionJobHandle;

            m_HasReplacementPreviewDefinitions = true;

            Mod.LogDiagnostic($"[IntersectionTool] Created pocket lane replacement definition preview original={FormatEntity(candidate.Edge)} pocket={FormatEntity(pocketEdge)} outer={FormatEntity(outerEdge)} splitNode={FormatEntity(splitNode)} splitNodePrefab=definition-driven sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} targetUpgrade={(candidate.HasTargetUpgrade ? candidate.TargetUpgrade.m_Flags.ToString() : "none")} pocketFlags={definitionRequest.Flags} outerFlags={outerDefinitionRequest.Flags} collisionValidation=vanilla-disabled pocketComposition=definition-driven outerComposition=source-definition lanes={candidate.OriginalForwardLanes}/{candidate.OriginalBackwardLanes}->{candidate.TargetForwardLanes}/{candidate.TargetBackwardLanes} pocketLengthError={lengthError:0.##}m outerLengthError={outerLengthError:0.##}m.");
            return true;
        }

    }
}
