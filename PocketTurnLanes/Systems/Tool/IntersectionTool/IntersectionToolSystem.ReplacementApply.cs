using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.PrefabMatching;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private bool VerifyAppliedReplacements()
        {
            m_VerifyAppliedReplacements = false;
            m_UtilityRetryReplacementCandidates.Clear();

            int verifiedCount = 0;
            int missingCount = 0;
            int replacedEntityCount = 0;
            int utilityFixQueuedCount = 0;
            int utilityFailedCount = 0;
            int utilityWaitingCount = 0;

            for (int i = 0; i < m_AppliedReplacementCandidates.Count; i++)
            {
                ReplacementCandidate candidate = m_AppliedReplacementCandidates[i];
                if (candidate.UtilityFixFrame >= 0 &&
                    UnityEngine.Time.frameCount <= candidate.UtilityFixFrame)
                {
                    utilityWaitingCount++;
                    m_UtilityRetryReplacementCandidates.Add(candidate);
                    Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement utility verification waiting one frame for post-replacement fix original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} attempts={candidate.UtilityFixAttempts}/{MaxReplacementUtilityFixAttempts} fixFrame={candidate.UtilityFixFrame} frame={UnityEngine.Time.frameCount} TrafficRepairDefer=utility-fix-pending.");
                    continue;
                }

                if (IsReplacementTargetVisible(candidate, candidate.PocketEdge, out string visibleDetail))
                {
                    if (!TryVerifyReplacementUtilityForTraffic(
                            ref candidate,
                            candidate.PocketEdge,
                            visibleDetail,
                            out string utilityDetail))
                    {
                        if (TryQueuePostReplacementUtilityFix(ref candidate, candidate.PocketEdge, utilityDetail))
                        {
                            utilityFixQueuedCount++;
                        }
                        else
                        {
                            utilityFailedCount++;
                        }

                        continue;
                    }

                    verifiedCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement verified edge={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} lanes={candidate.OriginalForwardLanes}/{candidate.OriginalBackwardLanes}->{candidate.TargetForwardLanes}/{candidate.TargetBackwardLanes} detail={visibleDetail} utility={utilityDetail}.");
                    DeferSplitLaneConnectionFix(candidate, candidate.PocketEdge, "verified-pocket-edge");
                    continue;
                }

                if (TryFindReplacementResultEdge(candidate, out Entity resultEdge))
                {
                    candidate.PocketEdge = resultEdge;
                    if (!TryVerifyReplacementUtilityForTraffic(
                            ref candidate,
                            resultEdge,
                            "replacement-result-edge",
                            out string utilityDetail))
                    {
                        if (TryQueuePostReplacementUtilityFix(ref candidate, resultEdge, utilityDetail))
                        {
                            utilityFixQueuedCount++;
                        }
                        else
                        {
                            utilityFailedCount++;
                        }

                        continue;
                    }

                    replacedEntityCount++;
                    Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement verified via replacement entity original={FormatEntity(candidate.OriginalEdge)} result={FormatEntity(resultEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} utility={utilityDetail}.");
                    DeferSplitLaneConnectionFix(candidate, resultEdge, "replacement-result-edge");
                    continue;
                }

                missingCount++;
                Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement not visible after apply original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} node={FormatEntity(candidate.Node)} splitNode={FormatEntity(candidate.SplitNode)}.");
            }

            m_AppliedReplacementCandidates.Clear();
            if (m_UtilityRetryReplacementCandidates.Count > 0)
            {
                m_AppliedReplacementCandidates.AddRange(m_UtilityRetryReplacementCandidates);
                m_VerifyAppliedReplacements = true;
            }

            Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement verification complete verified={verifiedCount}, replacedEntity={replacedEntityCount}, missing={missingCount}, utilityFixQueued={utilityFixQueuedCount}, utilityWaiting={utilityWaitingCount}, utilityFailed={utilityFailedCount}, retryPending={m_AppliedReplacementCandidates.Count}.");
            return m_VerifyAppliedReplacements;
        }

        private bool TryVerifyReplacementUtilityForTraffic(
            ref ReplacementCandidate candidate,
            Entity finalPocketEdge,
            string visibleDetail,
            out string detail)
        {
            ReplacementUtilityProfile sourceUtility = candidate.SourceUtilityProfile;
            ReplacementUtilityProfile targetUtility = candidate.TargetUtilityProfile;
            if (!sourceUtility.RequiresAny)
            {
                detail = $"status=not-required connectivityOk=True visualOk=True visualMismatch=False visible={visibleDetail} source={sourceUtility} target={targetUtility} targetUtilityFixFlags={candidate.TargetUtilityFixFlags} TrafficRepairProceed=utility-not-required";
                return true;
            }

            if (!m_ReplacementPrefabMatcher.TryGetRoadLaneProfile(finalPocketEdge, candidate.TargetPrefab, out RoadLaneProfile finalProfile))
            {
                detail = $"status=missing-final-profile connectivityOk=False visualOk=False visualMismatch=True visible={visibleDetail} edge={FormatEntity(finalPocketEdge)} source={sourceUtility} target={targetUtility} targetUtilityFixFlags={candidate.TargetUtilityFixFlags}";
                return false;
            }

            ReplacementUtilityProfile finalUtility = finalProfile.GetUtilityProfile();
            bool laneOk = true;
            string laneDetail = "not-required";
            if (sourceUtility.LaneLayout.HasAny)
            {
                laneOk = UtilityLaneLayoutsMatch(
                    targetUtility.LaneLayout,
                    finalUtility.LaneLayout,
                    out laneDetail);
            }

            bool typeOk = sourceUtility.UtilityTypes == Game.Net.UtilityTypes.None ||
                          (finalUtility.UtilityTypes & sourceUtility.UtilityTypes) == sourceUtility.UtilityTypes;
            bool electricityOk = !sourceUtility.ElectricityConnection || finalUtility.ElectricityConnection;
            bool waterOk = !sourceUtility.WaterPipeConnection || finalUtility.WaterPipeConnection;
            bool connectivityOk = typeOk && electricityOk && waterOk;
            bool visualOk = laneOk;
            bool visualMismatch = !visualOk;
            detail = $"status={(connectivityOk ? "ok" : "mismatch")} connectivityOk={connectivityOk} visualOk={visualOk} visualMismatch={visualMismatch} visible={visibleDetail} edge={FormatEntity(finalPocketEdge)} finalProfile={finalProfile.Source} source={sourceUtility} target={targetUtility} final={finalUtility} lane={laneDetail} typesOk={typeOk} electricityExpected={sourceUtility.ElectricityConnection} electricityActual={finalUtility.ElectricityConnection} waterExpected={sourceUtility.WaterPipeConnection} waterActual={finalUtility.WaterPipeConnection} targetUtilityFixFlags={candidate.TargetUtilityFixFlags} sourceLaneDetail={sourceUtility.LaneDetail} targetLaneDetail={targetUtility.LaneDetail} finalLaneDetail={finalUtility.LaneDetail} sourceElectricity={sourceUtility.ElectricityDetail} targetElectricity={targetUtility.ElectricityDetail} finalElectricity={finalUtility.ElectricityDetail} sourceWater={sourceUtility.WaterDetail} targetWater={targetUtility.WaterDetail} finalWater={finalUtility.WaterDetail} sourceComposition={sourceUtility.CompositionDetail} targetComposition={targetUtility.CompositionDetail} finalComposition={finalUtility.CompositionDetail}{(connectivityOk ? " TrafficRepairProceed=utility-connectivity-ok" : string.Empty)}";
            return connectivityOk;
        }

        private bool TryQueuePostReplacementUtilityFix(
            ref ReplacementCandidate candidate,
            Entity finalPocketEdge,
            string validationDetail)
        {
            if (finalPocketEdge == Entity.Null ||
                !EntityManager.Exists(finalPocketEdge) ||
                EntityManager.HasComponent<Deleted>(finalPocketEdge))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Post-replacement utility fix skipped: invalid final edge={FormatEntity(finalPocketEdge)} original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(candidate.PocketEdge)} validation=({validationDetail}) TrafficRepairDefer=utility-validation-invalid-edge.");
                return false;
            }

            if (candidate.UtilityFixAttempts >= MaxReplacementUtilityFixAttempts)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Post-replacement utility validation still failed after fix attempts original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(finalPocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} attempts={candidate.UtilityFixAttempts}/{MaxReplacementUtilityFixAttempts} validation=({validationDetail}) TrafficRepairDefer=utility-validation-failed-after-fix.");
                return false;
            }

            CompositionFlags requiredFlags = candidate.TargetUtilityFixFlags;
            if (candidate.HasTargetUpgrade)
            {
                requiredFlags |= candidate.TargetUpgrade.m_Flags;
            }

            if (requiredFlags == default(CompositionFlags))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Post-replacement utility fix cannot determine required upgraded flags original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(finalPocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} sourceUtility={candidate.SourceUtilityProfile} targetUtility={candidate.TargetUtilityProfile} validation=({validationDetail}) TrafficRepairDefer=utility-validation-no-required-flags.");
                return false;
            }

            bool hadUpgraded = EntityManager.TryGetComponent(finalPocketEdge, out Upgraded upgraded);
            CompositionFlags beforeFlags = hadUpgraded
                ? upgraded.m_Flags
                : default;
            upgraded.m_Flags = beforeFlags | requiredFlags;

            EntityCommandBuffer ecb = m_ToolOutputBarrier.CreateCommandBuffer();
            if (hadUpgraded)
            {
                ecb.SetComponent(finalPocketEdge, upgraded);
            }
            else
            {
                ecb.AddComponent(finalPocketEdge, upgraded);
            }

            if (!EntityManager.HasComponent<Updated>(finalPocketEdge))
            {
                ecb.AddComponent<Updated>(finalPocketEdge);
            }

            candidate.PocketEdge = finalPocketEdge;
            candidate.UtilityFixAttempts++;
            candidate.UtilityFixFrame = UnityEngine.Time.frameCount;
            m_UtilityRetryReplacementCandidates.Add(candidate);
            Mod.LogDiagnostic($"[IntersectionTool] Applied post-replacement utility fix original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(finalPocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} hadUpgraded={hadUpgraded} beforeFlags={beforeFlags} requiredFlags={requiredFlags} afterFlags={upgraded.m_Flags} attempts={candidate.UtilityFixAttempts}/{MaxReplacementUtilityFixAttempts} sourceUtility={candidate.SourceUtilityProfile} targetUtility={candidate.TargetUtilityProfile} validation=({validationDetail}) TrafficRepairDefer=utility-fix-applied.");
            return true;
        }

        private static bool UtilityLaneLayoutsMatch(
            DirectionalLaneOffsetProfile expected,
            DirectionalLaneOffsetProfile actual,
            out string detail)
        {
            if (!expected.HasAny || !actual.HasAny)
            {
                detail = $"expected={expected} actual={actual} hasExpected={expected.HasAny} hasActual={actual.HasAny}";
                return false;
            }

            bool countsMatch = expected.ForwardCount == actual.ForwardCount &&
                               expected.BackwardCount == actual.BackwardCount;
            float forwardDiff = GetAverageOffsetDiff(
                expected.ForwardCount,
                expected.ForwardOffsetSum,
                actual.ForwardCount,
                actual.ForwardOffsetSum);
            float backwardDiff = GetAverageOffsetDiff(
                expected.BackwardCount,
                expected.BackwardOffsetSum,
                actual.BackwardCount,
                actual.BackwardOffsetSum);
            bool offsetMatch = forwardDiff <= UtilityLaneOffsetTolerance &&
                               backwardDiff <= UtilityLaneOffsetTolerance;
            detail = $"expected={expected} actual={actual} countsMatch={countsMatch} forwardOffsetDiff={forwardDiff:0.###}m backwardOffsetDiff={backwardDiff:0.###}m tolerance={UtilityLaneOffsetTolerance:0.###}m";
            return countsMatch && offsetMatch;
        }

        private static float GetAverageOffsetDiff(
            int expectedCount,
            float expectedOffsetSum,
            int actualCount,
            float actualOffsetSum)
        {
            if (expectedCount == 0 && actualCount == 0)
            {
                return 0f;
            }

            if (expectedCount == 0 || actualCount == 0)
            {
                return float.MaxValue;
            }

            return math.abs(expectedOffsetSum / expectedCount - actualOffsetSum / actualCount);
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

            if (!m_ReplacementPrefabMatcher.TryGetRoadLaneProfile(edgeEntity, candidate.TargetPrefab, out RoadLaneProfile profile))
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
                ReplacementCandidate alreadyReplacedCandidate = CreateReplacementCandidate(splitCandidate, splitNode, pocketEdge);
                DeferSplitLaneConnectionFix(alreadyReplacedCandidate, pocketEdge, "already-target-prefab");

                return false;
            }
            else if (splitCandidate.TargetPrefab == splitCandidate.SourcePrefab)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Pocket lane replacement uses the source prefab; queueing a replacement definition to refresh runtime composition original={FormatEntity(splitCandidate.Edge)} pocket={FormatEntity(pocketEdge)} splitNode={FormatEntity(splitNode)} prefab={GetPrefabNameFromPrefab(splitCandidate.TargetPrefab)} orientation={(splitCandidate.InvertTarget ? "reversed" : "direct")} lanes={splitCandidate.OriginalForwardLanes}/{splitCandidate.OriginalBackwardLanes}->{splitCandidate.TargetForwardLanes}/{splitCandidate.TargetBackwardLanes}.");
            }

            ReplacementCandidate replacementCandidate = CreateReplacementCandidate(splitCandidate, splitNode, pocketEdge);

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest request))
            {
                return false;
            }

            JobHandle createDefinitionJobHandle = ScheduleReplacementDefinition(request, result);
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

            EdgeLookupSelection selection = EdgeLookupSelection.Create();
            EdgeLookupRejectedCandidate bestRejected = EdgeLookupRejectedCandidate.CreateScored();
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
                    bestRejected.RecordScore(edgeEntity, score, candidateNodeDistance, candidateLengthError);
                    continue;
                }

                selection.Record(
                    edgeEntity,
                    otherNode,
                    score,
                    candidateLengthError,
                    candidateNodeDistance,
                    0f);
            }

            if (selection.Edge == Entity.Null ||
                selection.NodeDistance > SplitNodePositionTolerance ||
                selection.LengthError > PocketEdgeLengthTolerance)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find generated pocket edge original={FormatEntity(candidate.Edge)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} node={FormatEntity(candidate.Node)} expectedSplit=({candidate.HitPosition.x:0.##},{candidate.HitPosition.y:0.##},{candidate.HitPosition.z:0.##}) expectedDistance={candidate.SplitDistance:0.##}m scanned={scannedCount} sourceOrTargetPrefabMatches={prefabMatchCount} allowOriginal={allowOriginalEdgeAsPocket} bestRejectedEdge={FormatEntity(bestRejected.Edge)} bestRejectedNodeDistance={FormatMeters(bestRejected.NodeDistance)} bestRejectedLengthError={FormatMeters(bestRejected.LengthError)}.");
                return false;
            }

            pocketEdge = selection.Edge;
            splitNode = selection.Node;
            splitNodeDistance = selection.NodeDistance;
            lengthError = selection.LengthError;
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
