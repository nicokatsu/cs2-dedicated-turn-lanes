using System.Collections.Generic;
using System.Linq;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private static void AssignLaneLaterals(List<LaneEndpoint> lanes, float2 origin, float2 right)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                LaneEndpoint lane = lanes[i];
                lane.Lateral = math.dot(lane.Position.xz - origin, right);
                lanes[i] = lane;
            }

            lanes.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));
        }

        private static bool TrySelectLaneMapping(
            List<LaneEndpoint> sourceLanes,
            List<LaneEndpoint> targetLanes,
            out List<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out float bestScore)
        {
            selectedTargets = null;
            extraTargetIndex = -1;
            bestScore = float.MaxValue;

            int desiredTargetCount = sourceLanes.Count + 1;
            if (targetLanes.Count < desiredTargetCount)
            {
                return false;
            }

            int maxStart = targetLanes.Count - desiredTargetCount;
            for (int start = 0; start <= maxStart; start++)
            {
                List<LaneEndpoint> subset = targetLanes.GetRange(start, desiredTargetCount);
                for (int extraCandidate = 0; extraCandidate < 2; extraCandidate++)
                {
                    int extraIndex = extraCandidate == 0 ? 0 : subset.Count - 1;
                    float score = 0f;
                    int sourceIndex = 0;
                    for (int targetIndex = 0; targetIndex < subset.Count; targetIndex++)
                    {
                        if (targetIndex == extraIndex)
                        {
                            continue;
                        }

                        score += math.abs(sourceLanes[sourceIndex].Lateral - subset[targetIndex].Lateral);
                        sourceIndex++;
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        extraTargetIndex = extraIndex;
                        selectedTargets = subset;
                    }
                }
            }

            return selectedTargets != null && extraTargetIndex >= 0;
        }

        private bool TryRefineExtraTargetFromCenterConnectors(
            Entity intersectionNode,
            Entity centerSourceEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string diagnostics)
        {
            extraTargetIndex = -1;
            turn = TurnDirection.Ambiguous;
            diagnostics = string.Empty;

            if (intersectionNode == Entity.Null ||
                centerSourceEdge == Entity.Null ||
                !EntityManager.Exists(intersectionNode) ||
                !EntityManager.TryGetBuffer(intersectionNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                diagnostics = $"center-node-missing-sublanes intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)}";
                return false;
            }

            if (selectedTargets == null || selectedTargets.Count == 0)
            {
                diagnostics = "no-selected-targets";
                return false;
            }

            int[] leftCounts = new int[selectedTargets.Count];
            int[] rightCounts = new int[selectedTargets.Count];
            int[] straightCounts = new int[selectedTargets.Count];
            m_CenterTurnCandidates.Clear();

            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, intersectionNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    sourceEdge != centerSourceEdge ||
                    targetEdge == centerSourceEdge)
                {
                    continue;
                }

                int sourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff;
                if (!TryFindTargetByCenterLaneIndex(selectedTargets, sourceLaneIndex, out int targetListIndex))
                {
                    continue;
                }

                NetCarLane carLane = EntityManager.GetComponentData<NetCarLane>(laneEntity);
                TurnDirection connectorTurn = ClassifyCenterConnectorTurn(intersectionNode, centerSourceEdge, targetEdge, carLane.m_Flags);
                if (connectorTurn == TurnDirection.Left)
                {
                    leftCounts[targetListIndex]++;
                }
                else if (connectorTurn == TurnDirection.Right)
                {
                    rightCounts[targetListIndex]++;
                }
                else
                {
                    straightCounts[targetListIndex]++;
                }

                m_CenterTurnCandidates.Add(new CenterTurnCandidate
                {
                    LaneEntity = laneEntity,
                    SourceLaneIndex = sourceLaneIndex,
                    TargetListIndex = targetListIndex,
                    TargetLaneIndex = selectedTargets[targetListIndex].LaneIndex,
                    TargetEdge = targetEdge,
                    Turn = connectorTurn,
                    Flags = carLane.m_Flags
                });
            }

            diagnostics = FormatCenterTurnDiagnostics(selectedTargets, leftCounts, rightCounts, straightCounts, m_CenterTurnCandidates);
            int bestIndex = -1;
            int bestScore = int.MinValue;
            TurnDirection bestTurn = TurnDirection.Ambiguous;
            bool tied = false;

            for (int i = 0; i < selectedTargets.Count; i++)
            {
                bool edgeTarget = i == 0 || i == selectedTargets.Count - 1;
                if (!edgeTarget)
                {
                    continue;
                }

                int left = leftCounts[i];
                int right = rightCounts[i];
                if (left == right)
                {
                    continue;
                }

                TurnDirection candidateTurn = left > right ? TurnDirection.Left : TurnDirection.Right;
                int turnCount = math.max(left, right);
                int oppositeTurnCount = math.min(left, right);
                int score = turnCount * 16 - oppositeTurnCount * 8 - straightCounts[i] * 3;
                if (straightCounts[i] == 0)
                {
                    score += 1000;
                }

                if (score > bestScore)
                {
                    bestIndex = i;
                    bestScore = score;
                    bestTurn = candidateTurn;
                    tied = false;
                }
                else if (score == bestScore)
                {
                    tied = true;
                }
            }

            if (bestIndex < 0 || tied)
            {
                diagnostics = $"{diagnostics}; centerSelection={(bestIndex < 0 ? "none" : "tie")}";
                return false;
            }

            extraTargetIndex = bestIndex;
            turn = bestTurn;
            diagnostics = $"{diagnostics}; centerSelection=target{selectedTargets[bestIndex].LaneIndex}/{bestTurn}/score{bestScore}{(straightCounts[bestIndex] == 0 ? "/turnOnly" : string.Empty)}";
            return true;
        }

        private static bool TryFindTargetByCenterLaneIndex(IReadOnlyList<LaneEndpoint> targets, int centerLaneIndex, out int targetListIndex)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].OppositeLaneIndex == centerLaneIndex)
                {
                    targetListIndex = i;
                    return true;
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].LaneIndex == centerLaneIndex)
                {
                    targetListIndex = i;
                    return true;
                }
            }

            targetListIndex = -1;
            return false;
        }

        private TurnDirection ClassifyCenterConnectorTurn(Entity intersectionNode, Entity sourceEdge, Entity targetEdge, CarLaneFlags flags)
        {
            if ((flags & CarLaneFlags.TurnLeft) != 0)
            {
                return TurnDirection.Left;
            }

            if ((flags & CarLaneFlags.TurnRight) != 0)
            {
                return TurnDirection.Right;
            }

            if (!NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, sourceEdge, intersectionNode, out float2 sourceOutward) ||
                !NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, targetEdge, intersectionNode, out float2 targetOutward))
            {
                return TurnDirection.Ambiguous;
            }

            float2 incoming = -sourceOutward;
            float cross = NetTopologyHelpers.Cross(incoming, targetOutward);
            if (math.abs(cross) < 0.25f)
            {
                return TurnDirection.Ambiguous;
            }

            return cross > 0f ? TurnDirection.Left : TurnDirection.Right;
        }

        private bool TryBuildDesiredMappings(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            int extraTargetIndex,
            int branchSourceLaneIndex,
            IReadOnlyList<ConnectorLane> existingConnectors,
            bool preferExistingConnectors,
            out LaneMapping[] mappings,
            out string mappingSource,
            out string reason)
        {
            mappings = null;
            mappingSource = "none";
            reason = string.Empty;

            if (sourceLanes == null ||
                selectedTargets == null ||
                selectedTargets.Count != sourceLanes.Count + 1 ||
                extraTargetIndex < 0 ||
                extraTargetIndex >= selectedTargets.Count)
            {
                reason = $"invalid counts source={sourceLanes?.Count ?? 0} selected={selectedTargets?.Count ?? 0} extraIndex={extraTargetIndex}";
                return false;
            }

            int extraTargetLaneIndex = selectedTargets[extraTargetIndex].LaneIndex;
            List<LaneEndpoint> originalTargets = new List<LaneEndpoint>(sourceLanes.Count);
            for (int i = 0; i < selectedTargets.Count; i++)
            {
                if (i != extraTargetIndex)
                {
                    originalTargets.Add(selectedTargets[i]);
                }
            }

            int[] assignedTargets = new int[sourceLanes.Count];
            for (int i = 0; i < assignedTargets.Length; i++)
            {
                assignedTargets[i] = -1;
            }

            HashSet<int> usedTargets = new HashSet<int>();
            int existingAssignments = 0;
            if (!preferExistingConnectors)
            {
                for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
                {
                    assignedTargets[sourceIndex] = originalTargets[sourceIndex].LaneIndex;
                    usedTargets.Add(assignedTargets[sourceIndex]);
                }
            }
            else
            {
                for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
                {
                    LaneEndpoint source = sourceLanes[sourceIndex];
                    float bestScore = float.MaxValue;
                    int bestTarget = -1;

                    for (int connectorIndex = 0; connectorIndex < existingConnectors.Count; connectorIndex++)
                    {
                        ConnectorLane connector = existingConnectors[connectorIndex];
                        if (connector.SourceLaneIndex != source.LaneIndex ||
                            connector.TargetLaneIndex == extraTargetLaneIndex ||
                            usedTargets.Contains(connector.TargetLaneIndex) ||
                            !TryFindLaneEndpoint(originalTargets, connector.TargetLaneIndex, out LaneEndpoint target))
                        {
                            continue;
                        }

                        float score = math.abs(source.Lateral - target.Lateral);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestTarget = connector.TargetLaneIndex;
                        }
                    }

                    if (bestTarget >= 0)
                    {
                        assignedTargets[sourceIndex] = bestTarget;
                        usedTargets.Add(bestTarget);
                        existingAssignments++;
                    }
                }
            }

            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (assignedTargets[sourceIndex] >= 0)
                {
                    continue;
                }

                float bestFallbackScore = float.MaxValue;
                int bestFallbackTarget = -1;
                for (int targetIndex = 0; targetIndex < originalTargets.Count; targetIndex++)
                {
                    LaneEndpoint target = originalTargets[targetIndex];
                    if (usedTargets.Contains(target.LaneIndex))
                    {
                        continue;
                    }

                    float score = math.abs(sourceLanes[sourceIndex].Lateral - target.Lateral);
                    if (score < bestFallbackScore)
                    {
                        bestFallbackScore = score;
                        bestFallbackTarget = target.LaneIndex;
                    }
                }

                if (bestFallbackTarget < 0)
                {
                    reason = $"no remaining original target for source={sourceLanes[sourceIndex].LaneIndex} assigned={string.Join(",", assignedTargets)} originalTargets={FormatLaneOrder(originalTargets)}";
                    return false;
                }

                assignedTargets[sourceIndex] = bestFallbackTarget;
                usedTargets.Add(assignedTargets[sourceIndex]);
            }

            m_Mappings.Clear();
            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (!TryFindLaneEndpoint(selectedTargets, assignedTargets[sourceIndex], out LaneEndpoint target))
                {
                    reason = $"assigned target missing source={sourceLanes[sourceIndex].LaneIndex} target={assignedTargets[sourceIndex]}";
                    return false;
                }

                m_Mappings.Add(new LaneMapping
                {
                    SourceEdge = sourceLanes[sourceIndex].Edge,
                    TargetEdge = target.Edge,
                    SourceLaneIndex = sourceLanes[sourceIndex].LaneIndex,
                    TargetLaneIndex = assignedTargets[sourceIndex],
                    Method = TrafficPathMethods.GetMappingMethod(sourceLanes[sourceIndex], target),
                    IsBranch = false
                });
            }

            if (!TryFindLaneEndpoint(sourceLanes, branchSourceLaneIndex, out LaneEndpoint branchSource) ||
                !TryFindLaneEndpoint(selectedTargets, extraTargetLaneIndex, out LaneEndpoint branchTarget))
            {
                reason = $"branch endpoint missing source={branchSourceLaneIndex} target={extraTargetLaneIndex}";
                return false;
            }

            m_Mappings.Add(new LaneMapping
            {
                SourceEdge = branchSource.Edge,
                TargetEdge = branchTarget.Edge,
                SourceLaneIndex = branchSourceLaneIndex,
                TargetLaneIndex = extraTargetLaneIndex,
                Method = TrafficPathMethods.GetMappingMethod(branchSource, branchTarget),
                IsBranch = true
            });

            mappings = m_Mappings.ToArray();
            mappingSource = !preferExistingConnectors
                ? "center-turn-order"
                : existingAssignments == sourceLanes.Count
                ? "existing-connectors"
                : existingAssignments > 0
                    ? $"existing-connectors+fallback({existingAssignments}/{sourceLanes.Count})"
                    : "lateral-fallback";
            return true;
        }

        private bool TryBuildSnapshotReverseMappings(
            TransitionConnectionSnapshot snapshot,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            out LaneMapping[] mappings,
            out string mappingSource,
            out string reason)
        {
            mappings = null;
            mappingSource = "none";
            reason = string.Empty;

            if (snapshot == null || snapshot.Mappings == null || snapshot.Mappings.Length == 0)
            {
                reason = "snapshot empty";
                return false;
            }

            if (sourceLanes == null || targetLanes == null || sourceLanes.Count == 0 || targetLanes.Count == 0)
            {
                reason = $"missing reverse endpoints source={sourceLanes?.Count ?? 0} target={targetLanes?.Count ?? 0}";
                return false;
            }

            if (!TryBuildSnapshotLaneRemap(
                    snapshot.Mappings,
                    sourceLanes,
                    source: true,
                    out Dictionary<int, LaneEndpoint> sourceRemap,
                    out string sourceRemapDetail,
                    out string sourceRemapReason))
            {
                reason = $"source remap failed: {sourceRemapReason}";
                return false;
            }

            if (!TryBuildSnapshotLaneRemap(
                    snapshot.Mappings,
                    targetLanes,
                    source: false,
                    out Dictionary<int, LaneEndpoint> targetRemap,
                    out string targetRemapDetail,
                    out string targetRemapReason))
            {
                reason = $"target remap failed: {targetRemapReason}";
                return false;
            }

            m_Mappings.Clear();
            HashSet<ConnectionKey> used = new HashSet<ConnectionKey>();
            int skipped = 0;
            for (int i = 0; i < snapshot.Mappings.Length; i++)
            {
                TransitionConnectionSnapshotMapping snapshotMapping = snapshot.Mappings[i];
                if (!sourceRemap.TryGetValue(snapshotMapping.SourceLaneIndex, out LaneEndpoint source) ||
                    !targetRemap.TryGetValue(snapshotMapping.TargetLaneIndex, out LaneEndpoint target))
                {
                    skipped++;
                    continue;
                }

                ConnectionKey key = new ConnectionKey(source.LaneIndex, target.LaneIndex);
                if (used.Contains(key))
                {
                    skipped++;
                    continue;
                }

                used.Add(key);
                m_Mappings.Add(new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = source.LaneIndex,
                    TargetLaneIndex = target.LaneIndex,
                    Method = RemapSnapshotMethod(snapshotMapping.Method, source, target),
                    IsBranch = false
                });
            }

            if (m_Mappings.Count == 0)
            {
                reason = $"no snapshot mappings could be remapped snapshot={FormatSnapshot(snapshot)} skipped={skipped}";
                return false;
            }

            mappings = m_Mappings.ToArray();
            mappingSource = $"snapshot={snapshot.Source}; sourceRemap=({sourceRemapDetail}); targetRemap=({targetRemapDetail}); skipped={skipped}; original={snapshot.Mappings.Length}";
            reason = "ok";
            return true;
        }

        private static void NormalizeTransitionLaneLaterals(List<LaneEndpoint> sourceLanes, List<LaneEndpoint> targetLanes)
        {
            if (sourceLanes == null ||
                targetLanes == null ||
                sourceLanes.Count == 0 ||
                targetLanes.Count == 0)
            {
                return;
            }

            float2 travelDirection = sourceLanes[0].TravelDirection;
            if (math.lengthsq(travelDirection) <= 0.0001f)
            {
                return;
            }

            float2 right = new float2(travelDirection.y, -travelDirection.x);
            float2 sourceOrigin = GetAveragePosition(sourceLanes);
            AssignLaneLaterals(sourceLanes, sourceOrigin, right);
            AssignLaneLaterals(targetLanes, sourceOrigin, right);
        }

        private static bool TryBuildSnapshotLaneRemap(
            IReadOnlyList<TransitionConnectionSnapshotMapping> snapshotMappings,
            IReadOnlyList<LaneEndpoint> currentLanes,
            bool source,
            out Dictionary<int, LaneEndpoint> remap,
            out string detail,
            out string reason)
        {
            remap = null;
            detail = "none";
            reason = string.Empty;

            if (snapshotMappings == null || snapshotMappings.Count == 0)
            {
                reason = "snapshot empty";
                return false;
            }

            if (currentLanes == null || currentLanes.Count == 0)
            {
                reason = "current lanes empty";
                return false;
            }

            Dictionary<int, SnapshotLaneOrder> snapshotLanes = new Dictionary<int, SnapshotLaneOrder>();
            for (int i = 0; i < snapshotMappings.Count; i++)
            {
                TransitionConnectionSnapshotMapping mapping = snapshotMappings[i];
                int laneIndex = source ? mapping.SourceLaneIndex : mapping.TargetLaneIndex;
                float lateral = source ? mapping.SourceLateral : mapping.TargetLateral;
                if (snapshotLanes.TryGetValue(laneIndex, out SnapshotLaneOrder existing))
                {
                    existing.LateralSum += lateral;
                    existing.Count++;
                    snapshotLanes[laneIndex] = existing;
                }
                else
                {
                    snapshotLanes.Add(laneIndex, new SnapshotLaneOrder
                    {
                        LaneIndex = laneIndex,
                        LateralSum = lateral,
                        Count = 1,
                        FirstSnapshotOrder = i
                    });
                }
            }

            if (snapshotLanes.Count > currentLanes.Count)
            {
                reason = $"snapshot lanes exceed current lanes snapshot={snapshotLanes.Count} current={currentLanes.Count}";
                return false;
            }

            List<SnapshotLaneOrder> orderedSnapshot = snapshotLanes.Values.ToList();
            float minLateral = orderedSnapshot.Min(lane => lane.AverageLateral);
            float maxLateral = orderedSnapshot.Max(lane => lane.AverageLateral);
            bool useLateralOrder = maxLateral - minLateral > 0.75f;
            orderedSnapshot.Sort((a, b) =>
            {
                int compare = useLateralOrder
                    ? a.AverageLateral.CompareTo(b.AverageLateral)
                    : a.LaneIndex.CompareTo(b.LaneIndex);
                return compare != 0
                    ? compare
                    : a.FirstSnapshotOrder.CompareTo(b.FirstSnapshotOrder);
            });

            List<LaneEndpoint> orderedCurrent = currentLanes.ToList();
            orderedCurrent.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));

            remap = new Dictionary<int, LaneEndpoint>(orderedSnapshot.Count);
            HashSet<int> usedCurrentIndexes = new HashSet<int>();
            for (int i = 0; i < orderedSnapshot.Count; i++)
            {
                SnapshotLaneOrder snapshotLane = orderedSnapshot[i];
                if (!TrySelectCurrentLaneByRank(
                        orderedCurrent,
                        orderedSnapshot.Count,
                        i,
                        usedCurrentIndexes,
                        out LaneEndpoint currentLane))
                {
                    reason = $"no current lane for snapshotLane={snapshotLane.LaneIndex} rank={i} current={FormatLaneOrder(currentLanes)}";
                    remap = null;
                    return false;
                }

                remap.Add(snapshotLane.LaneIndex, currentLane);
                usedCurrentIndexes.Add(currentLane.LaneIndex);
            }

            List<string> remapDetails = new List<string>(orderedSnapshot.Count);
            for (int i = 0; i < orderedSnapshot.Count; i++)
            {
                SnapshotLaneOrder snapshotLane = orderedSnapshot[i];
                LaneEndpoint currentLane = remap[snapshotLane.LaneIndex];
                remapDetails.Add($"{snapshotLane.LaneIndex}->{currentLane.LaneIndex}@{snapshotLane.AverageLateral:0.##}/{currentLane.Lateral:0.##}");
            }

            detail = $"rank-{(useLateralOrder ? "lateral" : "index")}; " + string.Join(",", remapDetails);
            return true;
        }

        private static bool TrySelectCurrentLaneByRank(
            IReadOnlyList<LaneEndpoint> orderedCurrent,
            int snapshotLaneCount,
            int snapshotRank,
            HashSet<int> usedCurrentIndexes,
            out LaneEndpoint lane)
        {
            lane = default;
            if (orderedCurrent == null || orderedCurrent.Count == 0)
            {
                return false;
            }

            int preferredRank = snapshotLaneCount <= 1
                ? 0
                : (int)math.round(snapshotRank * (orderedCurrent.Count - 1f) / (snapshotLaneCount - 1f));
            preferredRank = math.clamp(preferredRank, 0, orderedCurrent.Count - 1);

            int bestRankDistance = int.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < orderedCurrent.Count; i++)
            {
                LaneEndpoint candidate = orderedCurrent[i];
                if (usedCurrentIndexes.Contains(candidate.LaneIndex))
                {
                    continue;
                }

                int rankDistance = math.abs(i - preferredRank);
                if (rankDistance < bestRankDistance)
                {
                    bestRankDistance = rankDistance;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            lane = orderedCurrent[bestIndex];
            return true;
        }

        private static PathMethod RemapSnapshotMethod(PathMethod snapshotMethod, LaneEndpoint source, LaneEndpoint target)
        {
            PathMethod method = snapshotMethod | PathMethod.Road;
            PathMethod compatible = TrafficPathMethods.GetMappingMethod(source, target);
            if ((compatible & PathMethod.Bicycle) != 0)
            {
                method |= PathMethod.Bicycle;
            }
            else
            {
                method &= ~PathMethod.Bicycle;
            }

            if ((method & PathMethod.Track) != 0 && (compatible & PathMethod.Track) == 0)
            {
                method &= ~PathMethod.Track;
            }

            return method;
        }

        private static bool TryBuildStraightMappings(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            IReadOnlyList<ConnectorLane> existingConnectors,
            out LaneMapping[] mappings,
            out string mappingSource,
            out string reason)
        {
            mappings = null;
            mappingSource = "none";
            reason = string.Empty;

            if (sourceLanes == null ||
                targetLanes == null ||
                sourceLanes.Count == 0 ||
                targetLanes.Count == 0 ||
                sourceLanes.Count != targetLanes.Count)
            {
                reason = $"reverse lane count mismatch source={sourceLanes?.Count ?? 0} target={targetLanes?.Count ?? 0}";
                return false;
            }

            int[] assignedTargets = new int[sourceLanes.Count];
            for (int i = 0; i < assignedTargets.Length; i++)
            {
                assignedTargets[i] = -1;
            }

            HashSet<int> usedTargets = new HashSet<int>();
            int existingAssignments = 0;
            if (existingConnectors != null)
            {
                for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
                {
                    LaneEndpoint source = sourceLanes[sourceIndex];
                    float bestScore = float.MaxValue;
                    int bestTarget = -1;

                    for (int connectorIndex = 0; connectorIndex < existingConnectors.Count; connectorIndex++)
                    {
                        ConnectorLane connector = existingConnectors[connectorIndex];
                        if (connector.SourceLaneIndex != source.LaneIndex ||
                            usedTargets.Contains(connector.TargetLaneIndex) ||
                            !TryFindLaneEndpoint(targetLanes, connector.TargetLaneIndex, out LaneEndpoint target))
                        {
                            continue;
                        }

                        float score = math.abs(source.Lateral - target.Lateral);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestTarget = target.LaneIndex;
                        }
                    }

                    if (bestTarget >= 0)
                    {
                        assignedTargets[sourceIndex] = bestTarget;
                        usedTargets.Add(bestTarget);
                        existingAssignments++;
                    }
                }
            }

            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (assignedTargets[sourceIndex] >= 0)
                {
                    continue;
                }

                float bestFallbackScore = float.MaxValue;
                int bestFallbackTarget = -1;
                for (int targetIndex = 0; targetIndex < targetLanes.Count; targetIndex++)
                {
                    LaneEndpoint target = targetLanes[targetIndex];
                    if (usedTargets.Contains(target.LaneIndex))
                    {
                        continue;
                    }

                    float score = math.abs(sourceLanes[sourceIndex].Lateral - target.Lateral);
                    if (score < bestFallbackScore)
                    {
                        bestFallbackScore = score;
                        bestFallbackTarget = target.LaneIndex;
                    }
                }

                if (bestFallbackTarget < 0)
                {
                    reason = $"no remaining reverse target for source={sourceLanes[sourceIndex].LaneIndex} assigned={string.Join(",", assignedTargets)} targetOrder={FormatLaneOrder(targetLanes)}";
                    return false;
                }

                assignedTargets[sourceIndex] = bestFallbackTarget;
                usedTargets.Add(assignedTargets[sourceIndex]);
            }

            LaneMapping[] result = new LaneMapping[sourceLanes.Count];
            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (!TryFindLaneEndpoint(targetLanes, assignedTargets[sourceIndex], out LaneEndpoint target))
                {
                    reason = $"assigned reverse target missing source={sourceLanes[sourceIndex].LaneIndex} target={assignedTargets[sourceIndex]}";
                    return false;
                }

                result[sourceIndex] = new LaneMapping
                {
                    SourceEdge = sourceLanes[sourceIndex].Edge,
                    TargetEdge = target.Edge,
                    SourceLaneIndex = sourceLanes[sourceIndex].LaneIndex,
                    TargetLaneIndex = target.LaneIndex,
                    Method = TrafficPathMethods.GetMappingMethod(sourceLanes[sourceIndex], target),
                    IsBranch = false
                };
            }

            mappings = result;
            mappingSource = existingAssignments == sourceLanes.Count
                ? "reverse-existing-connectors"
                : existingAssignments > 0
                    ? $"reverse-existing-connectors+fallback({existingAssignments}/{sourceLanes.Count})"
                    : "reverse-lateral-fallback";
            return true;
        }

        private bool TryAddCenterCandidateMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            CenterConnectorCandidate candidate,
            LaneEndpoint sourceEndpoint,
            LaneEndpoint targetEndpoint,
            bool preserveUnsafe,
            bool forceSafeStraight,
            out string reason)
        {
            reason = string.Empty;
            if (!candidate.HasTargetEndpoint)
            {
                reason = $"missing target endpoint target={FormatEntity(candidate.Connector.TargetEdge)}:{candidate.Connector.TargetLaneIndex}";
                return false;
            }

            PathMethod method = GetCenterRoadRewriteMethod(
                candidate.Connector.PathMethods,
                sourceEndpoint,
                targetEndpoint);
            if ((method & PathMethod.Road) == 0)
            {
                reason = $"road method unavailable source={FormatEntity(sourceEndpoint.Edge)}:{sourceEndpoint.LaneIndex} target={FormatEntity(targetEndpoint.Edge)}:{targetEndpoint.LaneIndex}";
                return false;
            }

            bool unsafeConnection = preserveUnsafe &&
                                    !forceSafeStraight &&
                                    (candidate.Connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0;
            LaneMapping mapping = new LaneMapping
            {
                SourceEdge = sourceEndpoint.Edge,
                TargetEdge = targetEndpoint.Edge,
                SourceLaneIndex = sourceEndpoint.LaneIndex,
                TargetLaneIndex = targetEndpoint.LaneIndex,
                TrafficLanePositionMap = new float3x2(sourceEndpoint.LanePosition, targetEndpoint.LanePosition),
                TrafficCarriagewayAndGroupIndexMap = new int4(sourceEndpoint.CarriagewayAndGroup, targetEndpoint.CarriagewayAndGroup),
                Method = method,
                TemplateEntity = candidate.Connector.Entity,
                TemplatePathMethods = candidate.Connector.PathMethods,
                IsUnsafe = unsafeConnection,
                HasTrafficMaps = true
            };
            AddOrMergeCenterTrafficMapping(bySource, mapping);
            sourceEndpoints[new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex)] = sourceEndpoint;
            targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = targetEndpoint;
            return true;
        }

        private bool TryAddCenterShiftedStraightMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            CenterConnectorCandidate straightCandidate,
            LaneEndpoint smallSourceEndpoint,
            LaneEndpoint shiftedTargetEndpoint,
            out string reason)
        {
            reason = string.Empty;
            PathMethod method = GetCenterRoadRewriteMethod(
                straightCandidate.Connector.PathMethods,
                smallSourceEndpoint,
                shiftedTargetEndpoint);
            if ((method & PathMethod.Road) == 0)
            {
                reason = $"road method unavailable source={FormatEntity(smallSourceEndpoint.Edge)}:{smallSourceEndpoint.LaneIndex} target={FormatEntity(shiftedTargetEndpoint.Edge)}:{shiftedTargetEndpoint.LaneIndex}";
                return false;
            }

            LaneMapping mapping = new LaneMapping
            {
                SourceEdge = smallSourceEndpoint.Edge,
                TargetEdge = shiftedTargetEndpoint.Edge,
                SourceLaneIndex = smallSourceEndpoint.LaneIndex,
                TargetLaneIndex = shiftedTargetEndpoint.LaneIndex,
                TrafficLanePositionMap = new float3x2(smallSourceEndpoint.LanePosition, shiftedTargetEndpoint.LanePosition),
                TrafficCarriagewayAndGroupIndexMap = new int4(smallSourceEndpoint.CarriagewayAndGroup, shiftedTargetEndpoint.CarriagewayAndGroup),
                Method = method,
                TemplateEntity = straightCandidate.Connector.Entity,
                TemplatePathMethods = straightCandidate.Connector.PathMethods,
                IsUnsafe = false,
                HasTrafficMaps = true
            };
            AddOrMergeCenterTrafficMapping(bySource, mapping);
            sourceEndpoints[new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex)] = smallSourceEndpoint;
            targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = shiftedTargetEndpoint;
            return true;
        }

        private bool TryResolveShiftedStraightTarget(
            Entity centerNode,
            LaneEndpoint smallSource,
            LaneEndpoint bigSource,
            CenterConnectorCandidate straight,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint shiftedTarget,
            out string detail)
        {
            shiftedTarget = default;
            detail = string.Empty;

            if (!straight.HasTargetEndpoint)
            {
                detail = "straight target endpoint missing";
                return false;
            }

            if (math.abs(smallSource.Lateral - bigSource.Lateral) <= 0.0001f)
            {
                detail = $"source lateral tie small={smallSource.Lateral:0.###} big={bigSource.Lateral:0.###}";
                return false;
            }

            if (!TryGetCenterTargetEndpoints(centerNode, straight.Connector.TargetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(straight.Connector.TargetEdge)}";
                return false;
            }

            int originalTargetOrder = -1;
            for (int i = 0; i < targetEndpoints.Count; i++)
            {
                if (targetEndpoints[i].LaneIndex == straight.Connector.TargetLaneIndex)
                {
                    originalTargetOrder = i;
                    break;
                }
            }

            if (originalTargetOrder < 0)
            {
                detail = $"original straight target missing lane={straight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            int shift = smallSource.Lateral > bigSource.Lateral ? -1 : 1;
            int shiftedOrder = originalTargetOrder + shift;
            if (shiftedOrder < 0 || shiftedOrder >= targetEndpoints.Count)
            {
                detail = $"adjacent target unavailable originalOrder={originalTargetOrder} shift={shift} targetCount={targetEndpoints.Count} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            LaneEndpoint originalTarget = targetEndpoints[originalTargetOrder];
            shiftedTarget = targetEndpoints[shiftedOrder];
            float targetDelta = shiftedTarget.Lateral - originalTarget.Lateral;
            if (math.abs(targetDelta) <= 0.0001f ||
                targetDelta > 0f != shift > 0)
            {
                detail = $"target lateral not ordered original={originalTarget.Lateral:0.###} shifted={shiftedTarget.Lateral:0.###} shift={shift}";
                return false;
            }

            detail = $"sourceLane {bigSource.LaneIndex}->{smallSource.LaneIndex} targetLane {originalTarget.LaneIndex}->{shiftedTarget.LaneIndex} targetEdge={FormatEntity(straight.Connector.TargetEdge)} order {originalTargetOrder}->{shiftedOrder} shift={shift}";
            return true;
        }

        private static bool TrySelectPocketExtraAndMiddleSmallStraightLane(
            IReadOnlyList<CenterLaneMovementSummary> smallStraight,
            int pocketExtraCenterLane,
            out CenterLaneMovementSummary pocketExtraLane,
            out CenterLaneMovementSummary middleLane,
            out string detail)
        {
            pocketExtraLane = null;
            middleLane = null;
            detail = string.Empty;
            if (smallStraight == null || smallStraight.Count != 2)
            {
                detail = $"smallStraightCount={smallStraight?.Count ?? 0}";
                return false;
            }

            for (int i = 0; i < smallStraight.Count; i++)
            {
                CenterLaneMovementSummary candidate = smallStraight[i];
                if (candidate.SourceEndpoint.LaneIndex == pocketExtraCenterLane)
                {
                    if (pocketExtraLane != null)
                    {
                        detail = $"duplicatePocketExtra lane={pocketExtraCenterLane}";
                        return false;
                    }

                    pocketExtraLane = candidate;
                }
                else
                {
                    middleLane = candidate;
                }
            }

            if (pocketExtraLane == null || middleLane == null)
            {
                detail = $"pocketExtraMissing expected={pocketExtraCenterLane}";
                return false;
            }

            detail = $"pocketExtra={pocketExtraLane.SourceEndpoint.LaneIndex} middle={middleLane.SourceEndpoint.LaneIndex}";
            return true;
        }

        private bool TryResolveCascadeStraightTargets(
            Entity centerNode,
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary middleLane,
            CenterLaneMovementSummary bigLane,
            CenterConnectorCandidate middleCurrentStraight,
            CenterConnectorCandidate bigCurrentStraight,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint smallLaneStraightTarget,
            out LaneEndpoint middleLaneStraightTarget,
            out string detail)
        {
            smallLaneStraightTarget = default;
            middleLaneStraightTarget = default;
            detail = string.Empty;

            if (!middleCurrentStraight.HasTargetEndpoint ||
                !bigCurrentStraight.HasTargetEndpoint)
            {
                detail = $"straight endpoint missing middle={middleCurrentStraight.HasTargetEndpoint} big={bigCurrentStraight.HasTargetEndpoint}";
                return false;
            }

            if (!TryValidateThreeLaneSourceCascade(
                    sourceEndpoints,
                    smallLane.SourceEndpoint,
                    middleLane.SourceEndpoint,
                    bigLane.SourceEndpoint,
                    out string sourceDetail))
            {
                detail = sourceDetail;
                return false;
            }

            if (middleCurrentStraight.Connector.TargetEdge != bigCurrentStraight.Connector.TargetEdge)
            {
                detail = $"straight target edge mismatch middle={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} big={FormatEntity(bigCurrentStraight.Connector.TargetEdge)} source=({sourceDetail})";
                return false;
            }

            if (!TryGetCenterTargetEndpoints(centerNode, bigCurrentStraight.Connector.TargetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(bigCurrentStraight.Connector.TargetEdge)}";
                return false;
            }

            int middleTargetOrder = FindLaneEndpointOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
            int bigTargetOrder = FindLaneEndpointOrder(targetEndpoints, bigCurrentStraight.Connector.TargetLaneIndex);
            if (middleTargetOrder < 0 || bigTargetOrder < 0)
            {
                detail = $"straight target order missing middleLane={middleCurrentStraight.Connector.TargetLaneIndex} bigLane={bigCurrentStraight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            int expectedTargetShift = smallLane.SourceEndpoint.Lateral > bigLane.SourceEndpoint.Lateral ? -1 : 1;
            if (middleTargetOrder != bigTargetOrder + expectedTargetShift)
            {
                detail = $"straight target not adjacent middleOrder={middleTargetOrder} bigOrder={bigTargetOrder} expectedShift={expectedTargetShift} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                return false;
            }

            smallLaneStraightTarget = middleCurrentStraight.TargetEndpoint;
            middleLaneStraightTarget = bigCurrentStraight.TargetEndpoint;
            detail = $"source=({sourceDetail}) straightCascade smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(bigCurrentStraight.Connector.TargetEdge)} targetOrders middle={middleTargetOrder} big={bigTargetOrder} expectedShift={expectedTargetShift}";
            return true;
        }

        private bool TryResolveSmallStraightConflictTargets(
            Entity centerNode,
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary middleLane,
            CenterLaneMovementSummary bigLane,
            CenterConnectorCandidate smallCurrentStraight,
            CenterConnectorCandidate middleCurrentStraight,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint smallLaneStraightTarget,
            out LaneEndpoint middleLaneStraightTarget,
            out string detail)
        {
            smallLaneStraightTarget = default;
            middleLaneStraightTarget = default;
            detail = string.Empty;

            if (!smallCurrentStraight.HasTargetEndpoint ||
                !middleCurrentStraight.HasTargetEndpoint)
            {
                detail = $"straight endpoint missing small={smallCurrentStraight.HasTargetEndpoint} middle={middleCurrentStraight.HasTargetEndpoint}";
                return false;
            }

            if (!TryValidateThreeLaneSourceCascade(
                    sourceEndpoints,
                    smallLane.SourceEndpoint,
                    middleLane.SourceEndpoint,
                    bigLane.SourceEndpoint,
                    out string sourceDetail))
            {
                detail = sourceDetail;
                return false;
            }

            if (smallCurrentStraight.Connector.TargetEdge != middleCurrentStraight.Connector.TargetEdge)
            {
                detail = $"straight target edge mismatch small={FormatEntity(smallCurrentStraight.Connector.TargetEdge)} middle={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} source=({sourceDetail})";
                return false;
            }

            if (!TryGetCenterTargetEndpoints(centerNode, middleCurrentStraight.Connector.TargetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)}";
                return false;
            }

            int smallTargetOrder = FindLaneEndpointOrder(targetEndpoints, smallCurrentStraight.Connector.TargetLaneIndex);
            int middleTargetOrder = FindLaneEndpointOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
            if (smallTargetOrder < 0 || middleTargetOrder < 0)
            {
                detail = $"straight target order missing smallLane={smallCurrentStraight.Connector.TargetLaneIndex} middleLane={middleCurrentStraight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            smallLaneStraightTarget = smallCurrentStraight.TargetEndpoint;
            int smallSideTargetShift = smallLane.SourceEndpoint.Lateral > bigLane.SourceEndpoint.Lateral ? -1 : 1;
            if (smallCurrentStraight.Connector.TargetLaneIndex == middleCurrentStraight.Connector.TargetLaneIndex)
            {
                int shiftedMiddleOrder = smallTargetOrder - smallSideTargetShift;
                if (shiftedMiddleOrder < 0 || shiftedMiddleOrder >= targetEndpoints.Count)
                {
                    detail = $"duplicate straight target cannot shift middle smallTargetOrder={smallTargetOrder} shift={-smallSideTargetShift} targetCount={targetEndpoints.Count} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                    return false;
                }

                middleLaneStraightTarget = targetEndpoints[shiftedMiddleOrder];
                detail = $"source=({sourceDetail}) duplicateStraightTargetShifted smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} targetOrders small={smallTargetOrder} middle={shiftedMiddleOrder} smallSideShift={smallSideTargetShift}";
                return true;
            }

            if (smallTargetOrder != middleTargetOrder + smallSideTargetShift)
            {
                detail = $"straight targets not in shifted order smallOrder={smallTargetOrder} middleOrder={middleTargetOrder} expectedSmallShift={smallSideTargetShift} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                return false;
            }

            middleLaneStraightTarget = middleCurrentStraight.TargetEndpoint;
            detail = $"source=({sourceDetail}) distinctStraightTargets smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} targetOrders small={smallTargetOrder} middle={middleTargetOrder} smallSideShift={smallSideTargetShift}";
            return true;
        }

        private static bool TryValidateThreeLaneSourceCascade(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            LaneEndpoint smallSource,
            LaneEndpoint middleSource,
            LaneEndpoint bigSource,
            out string detail)
        {
            detail = string.Empty;
            int smallOrder = FindLaneEndpointOrder(sourceEndpoints, smallSource.LaneIndex);
            int middleOrder = FindLaneEndpointOrder(sourceEndpoints, middleSource.LaneIndex);
            int bigOrder = FindLaneEndpointOrder(sourceEndpoints, bigSource.LaneIndex);
            if (smallOrder < 0 || middleOrder < 0 || bigOrder < 0)
            {
                detail = $"source order missing small={smallSource.LaneIndex}:{smallOrder} middle={middleSource.LaneIndex}:{middleOrder} big={bigSource.LaneIndex}:{bigOrder}";
                return false;
            }

            if (smallOrder == bigOrder)
            {
                detail = $"source order tie small={smallOrder} big={bigOrder}";
                return false;
            }

            int direction = bigOrder > smallOrder ? 1 : -1;
            if (middleOrder != smallOrder + direction ||
                bigOrder != middleOrder + direction)
            {
                detail = $"source lanes not adjacent smallOrder={smallOrder} middleOrder={middleOrder} bigOrder={bigOrder} direction={direction}";
                return false;
            }

            detail = $"small={smallSource.LaneIndex}@{smallOrder} middle={middleSource.LaneIndex}@{middleOrder} big={bigSource.LaneIndex}@{bigOrder} direction={direction}";
            return true;
        }

        private static PathMethod GetCenterRoadRewriteMethod(
            PathMethod templateMethod,
            LaneEndpoint source,
            LaneEndpoint target)
        {
            PathMethod method = TrafficPathMethods.RestrictTrafficPathMethodToEndpoints(
                PathMethod.Road,
                source,
                target);
            if ((method & PathMethod.Road) == 0)
            {
                return 0;
            }

            method |= templateMethod & PathMethod.Bicycle;
            return TrafficPathMethods.SanitizeCenterTrafficPathMethod(method);
        }

        private static int CountRoadBicycleMappings(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource)
        {
            int count = 0;
            foreach (Dictionary<TargetLaneKey, LaneMapping> byTarget in bySource.Values)
            {
                foreach (LaneMapping mapping in byTarget.Values)
                {
                    if ((mapping.Method & PathMethod.Road) != 0 &&
                        (mapping.Method & PathMethod.Bicycle) != 0)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static int CountTrafficPlanConnections(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource)
        {
            int count = 0;
            foreach (Dictionary<TargetLaneKey, LaneMapping> byTarget in bySource.Values)
            {
                count += byTarget.Count;
            }

            return count;
        }

        private static void AddOrMergeCenterTrafficMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping)
        {
            AddOrMergeTrafficMapping(bySource, mapping, TrafficPathMethodMergeMode.CenterRewrite);
        }

        private void MergeCenterApproachPlan(
            CenterRewritePlan plan,
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> approachBySource,
            Dictionary<SourceLaneKey, LaneEndpoint> approachSourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> approachTargetEndpoints)
        {
            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> sourcePair in approachBySource)
            {
                foreach (LaneMapping mapping in sourcePair.Value.Values)
                {
                    AddOrMergeCenterTrafficMapping(plan.BySource, mapping);
                }
            }

            foreach (KeyValuePair<SourceLaneKey, LaneEndpoint> pair in approachSourceEndpoints)
            {
                plan.SourceEndpoints[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<TargetLaneKey, LaneEndpoint> pair in approachTargetEndpoints)
            {
                plan.TargetEndpoints[pair.Key] = pair.Value;
            }
        }

        private static void AddOrMergeFinalTrafficMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping)
        {
            AddOrMergeTrafficMapping(bySource, mapping, TrafficPathMethodMergeMode.FinalRepair);
        }

        private static void AddOrMergeTrafficMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping,
            TrafficPathMethodMergeMode mode)
        {
            SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
            TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
            if (!bySource.TryGetValue(sourceKey, out Dictionary<TargetLaneKey, LaneMapping> byTarget))
            {
                byTarget = new Dictionary<TargetLaneKey, LaneMapping>();
                bySource.Add(sourceKey, byTarget);
            }

            PathMethod mergeMethod = TrafficPathMethods.SanitizeMappingMethod(
                mapping.Method,
                mode,
                mapping.HasPreservedPathMethods || mode == TrafficPathMethodMergeMode.CenterRewrite);
            if (mergeMethod == 0)
            {
                return;
            }

            if (byTarget.TryGetValue(targetKey, out LaneMapping existing))
            {
                bool preserveUnsafe = existing.IsPreservationOnly && mapping.IsPreservationOnly;
                bool hasPreservedPathMethods = existing.HasPreservedPathMethods || mapping.HasPreservedPathMethods;
                existing.Method = TrafficPathMethods.SanitizeMappingMethod(
                    existing.Method | mergeMethod,
                    mode,
                    hasPreservedPathMethods || mode == TrafficPathMethodMergeMode.CenterRewrite);
                existing.IsBranch |= mapping.IsBranch;
                existing.IsPreservationOnly &= mapping.IsPreservationOnly;
                existing.HasPreservedPathMethods = hasPreservedPathMethods;
                existing.IsUnsafe = mode == TrafficPathMethodMergeMode.CenterRewrite
                    ? existing.IsUnsafe || mapping.IsUnsafe
                    : preserveUnsafe && (existing.IsUnsafe || mapping.IsUnsafe);
                if (!existing.HasTrafficMaps && mapping.HasTrafficMaps)
                {
                    existing.TrafficLanePositionMap = mapping.TrafficLanePositionMap;
                    existing.TrafficCarriagewayAndGroupIndexMap = mapping.TrafficCarriagewayAndGroupIndexMap;
                    existing.HasTrafficMaps = true;
                }

                byTarget[targetKey] = existing;
                return;
            }

            mapping.Method = mergeMethod;
            byTarget.Add(targetKey, mapping);
        }

        private static TurnDirection DetermineTurn(List<LaneEndpoint> selectedTargets, int extraTargetIndex)
        {
            if (extraTargetIndex == 0)
            {
                return TurnDirection.Left;
            }

            if (extraTargetIndex == selectedTargets.Count - 1)
            {
                return TurnDirection.Right;
            }

            return TurnDirection.Ambiguous;
        }
    }
}
