using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Tool;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
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
                TargetBackwardLanes = candidate.TargetBackwardLanes,
                FarIntersectionSnapshot = candidate.FarIntersectionSnapshot
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
                        (TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_Start, shortEdge.m_Start) &&
                         TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_End, shortEdge.m_End)) ||
                        (TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_Start, shortEdge.m_End) &&
                         TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_End, shortEdge.m_Start));
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

            bool startMatches = TempEntityHelpers.IsSameOrTempOriginal(EntityManager, previewEdge.m_Start, candidate.RemovableNode);
            bool endMatches = TempEntityHelpers.IsSameOrTempOriginal(EntityManager, previewEdge.m_End, candidate.RemovableNode);
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
                        (TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_Start, sourceEdgeData.m_Start) &&
                         TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_End, sourceEdgeData.m_End)) ||
                        (TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_Start, sourceEdgeData.m_End) &&
                         TempEntityHelpers.IsSameOrTempOriginal(EntityManager, edge.m_End, sourceEdgeData.m_Start));
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

    }
}
