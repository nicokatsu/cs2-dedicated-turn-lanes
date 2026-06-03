using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool.PrefabMatching;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private void VerifyAppliedReplacements()
        {
            m_VerifyAppliedReplacements = false;

            int verifiedCount = 0;
            int missingCount = 0;
            int replacedEntityCount = 0;

            for (int i = 0; i < m_AppliedReplacementCandidates.Count; i++)
            {
                ReplacementCandidate candidate = m_AppliedReplacementCandidates[i];
                if (IsReplacementTargetVisible(candidate, candidate.PocketEdge, out string visibleDetail))
                {
                    verifiedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement verified edge={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} lanes={candidate.OriginalForwardLanes}/{candidate.OriginalBackwardLanes}->{candidate.TargetForwardLanes}/{candidate.TargetBackwardLanes} detail={visibleDetail}.");
                    DeferSplitLaneConnectionFix(candidate, candidate.PocketEdge, "verified-pocket-edge");
                    continue;
                }

                if (TryFindReplacementResultEdge(candidate, out Entity resultEdge))
                {
                    replacedEntityCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement verified via replacement entity original={FormatEntity(candidate.PocketEdge)} result={FormatEntity(resultEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")}.");
                    DeferSplitLaneConnectionFix(candidate, resultEdge, "replacement-result-edge");
                    continue;
                }

                missingCount++;
                Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement not visible after apply original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} node={FormatEntity(candidate.Node)} splitNode={FormatEntity(candidate.SplitNode)}.");
            }

            Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement verification complete verified={verifiedCount}, replacedEntity={replacedEntityCount}, missing={missingCount}.");
            m_AppliedReplacementCandidates.Clear();
        }

        private void DeferSplitLaneConnectionFix(ReplacementCandidate candidate, Entity finalPocketEdge, string reason)
        {
            if (candidate.SplitNode == Entity.Null ||
                finalPocketEdge == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Deferred lane repair skipped: invalid splitNode={FormatEntity(candidate.SplitNode)} pocket={FormatEntity(finalPocketEdge)} original={FormatEntity(candidate.OriginalEdge)} reason={reason} mode={candidate.LaneRepairMode}.");
                return;
            }

            candidate.PocketEdge = finalPocketEdge;
            for (int i = 0; i < m_PendingLaneRepairCandidates.Count; i++)
            {
                ReplacementCandidate pending = m_PendingLaneRepairCandidates[i];
                if (pending.SplitNode == candidate.SplitNode &&
                    pending.PocketEdge == candidate.PocketEdge)
                {
                    m_PendingLaneRepairCandidates[i] = candidate;
                    Mod.LogDiagnostic($"[IntersectionTool] Updated deferred lane repair splitNode={FormatEntity(candidate.SplitNode)} pocket={FormatEntity(finalPocketEdge)} original={FormatEntity(candidate.OriginalEdge)} mode={candidate.LaneRepairMode} reason={reason} pending={m_PendingLaneRepairCandidates.Count}.");
                    return;
                }
            }

            m_PendingLaneRepairCandidates.Add(candidate);
            Mod.LogDiagnostic($"[IntersectionTool] Deferred lane repair until final apply phases complete splitNode={FormatEntity(candidate.SplitNode)} pocket={FormatEntity(finalPocketEdge)} original={FormatEntity(candidate.OriginalEdge)} node={FormatEntity(candidate.Node)} farNode={FormatEntity(candidate.FarNode)} mode={candidate.LaneRepairMode} reason={reason} pending={m_PendingLaneRepairCandidates.Count}.");
        }

        private void QueuePendingSplitLaneConnectionFixes(string reason)
        {
            int pendingCount = m_PendingLaneRepairCandidates.Count;
            if (pendingCount == 0)
            {
                return;
            }

            Mod.LogDiagnostic($"[IntersectionTool] Queueing deferred lane repairs count={pendingCount} reason={reason}.");
            for (int i = 0; i < m_PendingLaneRepairCandidates.Count; i++)
            {
                ReplacementCandidate candidate = m_PendingLaneRepairCandidates[i];
                QueueSplitLaneConnectionFix(candidate, candidate.PocketEdge);
            }

            m_PendingLaneRepairCandidates.Clear();
        }

        private void QueueSplitLaneConnectionFix(ReplacementCandidate candidate, Entity finalPocketEdge)
        {
            if (m_SplitLaneConnectionFixSystem == null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot queue split lane connection fix pocket={FormatEntity(finalPocketEdge)} splitNode={FormatEntity(candidate.SplitNode)}: fix system is not available.");
                return;
            }

            if (candidate.LaneRepairMode == SplitLaneConnectionRepairMode.BalancedOppositeTarget)
            {
                m_SplitLaneConnectionFixSystem.QueueBalancedOppositeTarget(
                    candidate.Node,
                    candidate.FarNode,
                    candidate.SplitNode,
                    candidate.OriginalEdge,
                    finalPocketEdge,
                    candidate.SourcePrefab,
                    candidate.TargetPrefab,
                    candidate.FarIntersectionSnapshot);
                return;
            }

            if (candidate.LaneRepairMode == SplitLaneConnectionRepairMode.ShortEdgeTransition)
            {
                m_SplitLaneConnectionFixSystem.QueueShortEdgeTransition(
                    candidate.Node,
                    candidate.SplitNode,
                    candidate.TransitionOuterEdge,
                    finalPocketEdge,
                    candidate.SourcePrefab,
                    candidate.TargetPrefab,
                    candidate.TransitionReverseSnapshot);
                return;
            }

            m_SplitLaneConnectionFixSystem.Queue(
                candidate.Node,
                candidate.SplitNode,
                candidate.OriginalEdge,
                finalPocketEdge,
                candidate.SourcePrefab,
                candidate.TargetPrefab);
        }

        private bool IsReplacementTargetVisible(
            ReplacementCandidate candidate,
            Entity edgeEntity,
            out string detail)
        {
            detail = "missing";
            if (edgeEntity == Entity.Null ||
                !EntityManager.Exists(edgeEntity) ||
                EntityManager.HasComponent<Deleted>(edgeEntity) ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef) ||
                prefabRef.m_Prefab != candidate.TargetPrefab)
            {
                return false;
            }

            if (candidate.TargetPrefab != candidate.SourcePrefab)
            {
                detail = "prefab-match";
                return true;
            }

            if (!TryGetRoadLaneProfile(edgeEntity, candidate.TargetPrefab, out RoadLaneProfile profile))
            {
                detail = "same-prefab profile=missing";
                return false;
            }

            RoadLaneCounts targetCounts = new RoadLaneCounts
            {
                Forward = candidate.TargetForwardLanes,
                Backward = candidate.TargetBackwardLanes
            };
            if (!RoadLaneCountMatcher.TryMatch(profile.RoadCounts, targetCounts, out bool invert))
            {
                detail = $"same-prefab profile={profile.Source} road={profile.RoadCounts} target={targetCounts} bus={profile.BusLaneLayout} tram={profile.TramTrackLayout}";
                return false;
            }

            detail = $"same-prefab profile={profile.Source} road={profile.RoadCounts} target={targetCounts} matchedOrientation={(invert ? "reversed" : "direct")} bus={profile.BusLaneLayout} tram={profile.TramTrackLayout}";
            return true;
        }

        private bool TryQueuePocketLaneReplacement(
            SplitCandidate splitCandidate,
            ref JobHandle result,
            out bool foundPocketEdge)
        {
            foundPocketEdge = false;

            if (splitCandidate.TargetPrefab == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot queue pocket lane replacement original={FormatEntity(splitCandidate.Edge)}: no target prefab was selected.");
                return false;
            }

            if (!TryFindPocketEdge(
                    splitCandidate,
                    out Entity pocketEdge,
                    out Entity splitNode,
                    out float splitNodeDistance,
                    out float lengthError,
                    true))
            {
                return false;
            }

            foundPocketEdge = true;

            if (EntityManager.TryGetComponent(pocketEdge, out PrefabRef pocketPrefabRef) &&
                pocketPrefabRef.m_Prefab == splitCandidate.TargetPrefab &&
                splitCandidate.TargetPrefab != splitCandidate.SourcePrefab)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement already present after split original={FormatEntity(splitCandidate.Edge)} pocket={FormatEntity(pocketEdge)} splitNode={FormatEntity(splitNode)} targetPrefab={GetPrefabNameFromPrefab(splitCandidate.TargetPrefab)} orientation={(splitCandidate.InvertTarget ? "reversed" : "direct")} splitNodeDistance={splitNodeDistance:0.##}m lengthError={lengthError:0.##}m.");
                ReplacementCandidate alreadyReplacedCandidate = new ReplacementCandidate
                {
                    Node = splitCandidate.Node,
                    FarNode = splitCandidate.FarNode,
                    SplitNode = splitNode,
                    OriginalEdge = splitCandidate.Edge,
                    PocketEdge = pocketEdge,
                    SourcePrefab = splitCandidate.SourcePrefab,
                    TargetPrefab = splitCandidate.TargetPrefab,
                    LaneRepairMode = splitCandidate.LaneRepairMode,
                    InvertTarget = splitCandidate.InvertTarget,
                    HasTargetUpgrade = splitCandidate.HasTargetUpgrade,
                    TargetUpgrade = splitCandidate.TargetUpgrade,
                    HitPosition = splitCandidate.HitPosition,
                    OriginalForwardLanes = splitCandidate.OriginalForwardLanes,
                    OriginalBackwardLanes = splitCandidate.OriginalBackwardLanes,
                    TargetForwardLanes = splitCandidate.TargetForwardLanes,
                    TargetBackwardLanes = splitCandidate.TargetBackwardLanes,
                    FarIntersectionSnapshot = splitCandidate.FarIntersectionSnapshot
                };
                DeferSplitLaneConnectionFix(alreadyReplacedCandidate, pocketEdge, "already-target-prefab");

                return false;
            }
            else if (splitCandidate.TargetPrefab == splitCandidate.SourcePrefab)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement uses the source prefab; queueing a replacement definition to refresh runtime composition original={FormatEntity(splitCandidate.Edge)} pocket={FormatEntity(pocketEdge)} splitNode={FormatEntity(splitNode)} prefab={GetPrefabNameFromPrefab(splitCandidate.TargetPrefab)} orientation={(splitCandidate.InvertTarget ? "reversed" : "direct")} lanes={splitCandidate.OriginalForwardLanes}/{splitCandidate.OriginalBackwardLanes}->{splitCandidate.TargetForwardLanes}/{splitCandidate.TargetBackwardLanes}.");
            }

            ReplacementCandidate replacementCandidate = new ReplacementCandidate
            {
                Node = splitCandidate.Node,
                FarNode = splitCandidate.FarNode,
                SplitNode = splitNode,
                OriginalEdge = splitCandidate.Edge,
                PocketEdge = pocketEdge,
                SourcePrefab = splitCandidate.SourcePrefab,
                TargetPrefab = splitCandidate.TargetPrefab,
                LaneRepairMode = splitCandidate.LaneRepairMode,
                InvertTarget = splitCandidate.InvertTarget,
                HasTargetUpgrade = splitCandidate.HasTargetUpgrade,
                TargetUpgrade = splitCandidate.TargetUpgrade,
                HitPosition = splitCandidate.HitPosition,
                OriginalForwardLanes = splitCandidate.OriginalForwardLanes,
                OriginalBackwardLanes = splitCandidate.OriginalBackwardLanes,
                TargetForwardLanes = splitCandidate.TargetForwardLanes,
                TargetBackwardLanes = splitCandidate.TargetBackwardLanes,
                FarIntersectionSnapshot = splitCandidate.FarIntersectionSnapshot
            };

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest request))
            {
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

            Mod.LogDiagnostic($"[IntersectionTool] Queued pocket lane replacement original={FormatEntity(splitCandidate.Edge)} pocket={FormatEntity(pocketEdge)} splitNode={FormatEntity(splitNode)} sourcePrefab={GetPrefabNameFromPrefab(splitCandidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(splitCandidate.TargetPrefab)} orientation={(splitCandidate.InvertTarget ? "reversed" : "direct")} targetUpgrade={(splitCandidate.HasTargetUpgrade ? splitCandidate.TargetUpgrade.m_Flags.ToString() : "none")} lanes={splitCandidate.OriginalForwardLanes}/{splitCandidate.OriginalBackwardLanes}->{splitCandidate.TargetForwardLanes}/{splitCandidate.TargetBackwardLanes} splitNodeDistance={splitNodeDistance:0.##}m lengthError={lengthError:0.##}m reusedOriginal={(pocketEdge == splitCandidate.Edge ? "yes" : "no")}.");
            return true;
        }

        private bool TryFindPocketEdge(
            SplitCandidate candidate,
            out Entity pocketEdge,
            out Entity splitNode,
            out float splitNodeDistance,
            out float lengthError,
            bool allowOriginalEdgeAsPocket = false)
        {
            pocketEdge = Entity.Null;
            splitNode = Entity.Null;
            splitNodeDistance = 0f;
            lengthError = 0f;

            if (!EntityManager.TryGetBuffer(candidate.Node, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find pocket edge for original={FormatEntity(candidate.Edge)}: node={FormatEntity(candidate.Node)} has no ConnectedEdge buffer.");
                return false;
            }

            float bestValidScore = float.MaxValue;
            float bestNodeDistance = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            Entity bestSplitNode = Entity.Null;
            float bestRejectedScore = float.MaxValue;
            float bestRejectedNodeDistance = float.MaxValue;
            float bestRejectedLengthError = float.MaxValue;
            Entity bestRejectedEdge = Entity.Null;
            int scannedCount = 0;
            int prefabMatchCount = 0;

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if ((edgeEntity == candidate.Edge && !allowOriginalEdgeAsPocket) ||
                    !IsRoadEdge(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                    !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                {
                    continue;
                }

                scannedCount++;
                if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                    prefabRef.m_Prefab != candidate.TargetPrefab)
                {
                    continue;
                }

                prefabMatchCount++;
                Entity otherNode = edge.m_Start == candidate.Node ? edge.m_End : edge.m_End == candidate.Node ? edge.m_Start : Entity.Null;
                if (otherNode == Entity.Null ||
                    !EntityManager.TryGetComponent(otherNode, out Node otherNodeData))
                {
                    continue;
                }

                float candidateNodeDistance = math.distance(otherNodeData.m_Position.xz, candidate.HitPosition.xz);
                float candidateLengthError = math.abs(curve.m_Length - candidate.SplitDistance);
                float score = candidateNodeDistance + candidateLengthError * 0.25f;
                if (candidateNodeDistance > SplitNodePositionTolerance ||
                    candidateLengthError > PocketEdgeLengthTolerance)
                {
                    if (score < bestRejectedScore)
                    {
                        bestRejectedScore = score;
                        bestRejectedNodeDistance = candidateNodeDistance;
                        bestRejectedLengthError = candidateLengthError;
                        bestRejectedEdge = edgeEntity;
                    }

                    continue;
                }

                if (score < bestValidScore)
                {
                    bestValidScore = score;
                    bestNodeDistance = candidateNodeDistance;
                    bestLengthError = candidateLengthError;
                    bestEdge = edgeEntity;
                    bestSplitNode = otherNode;
                }
            }

            if (bestEdge == Entity.Null ||
                bestNodeDistance > SplitNodePositionTolerance ||
                bestLengthError > PocketEdgeLengthTolerance)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find generated pocket edge original={FormatEntity(candidate.Edge)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} node={FormatEntity(candidate.Node)} expectedSplit=({candidate.HitPosition.x:0.##},{candidate.HitPosition.y:0.##},{candidate.HitPosition.z:0.##}) expectedDistance={candidate.SplitDistance:0.##}m scanned={scannedCount} sourceOrTargetPrefabMatches={prefabMatchCount} allowOriginal={allowOriginalEdgeAsPocket} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedNodeDistance={FormatMeters(bestRejectedNodeDistance)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}.");
                return false;
            }

            pocketEdge = bestEdge;
            splitNode = bestSplitNode;
            splitNodeDistance = bestNodeDistance;
            lengthError = bestLengthError;
            return true;
        }

        private bool TryFindReplacementResultEdge(ReplacementCandidate candidate, out Entity resultEdge)
        {
            resultEdge = Entity.Null;
            if (candidate.Node == Entity.Null ||
                candidate.SplitNode == Entity.Null ||
                !EntityManager.Exists(candidate.Node) ||
                !EntityManager.Exists(candidate.SplitNode) ||
                !EntityManager.TryGetBuffer(candidate.Node, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (edgeEntity == Entity.Null ||
                    !EntityManager.Exists(edgeEntity) ||
                    EntityManager.HasComponent<Deleted>(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                    !IsReplacementTargetVisible(candidate, edgeEntity, out _))
                {
                    continue;
                }

                if ((edge.m_Start == candidate.Node && edge.m_End == candidate.SplitNode) ||
                    (edge.m_Start == candidate.SplitNode && edge.m_End == candidate.Node))
                {
                    resultEdge = edgeEntity;
                    return true;
                }
            }

            return false;
        }
    }
}
