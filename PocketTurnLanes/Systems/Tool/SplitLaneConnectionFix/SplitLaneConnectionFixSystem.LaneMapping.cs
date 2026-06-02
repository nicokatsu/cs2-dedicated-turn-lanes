using System.Collections.Generic;
using System.Linq;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
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
                            !TrafficLaneEndpointHelpers.TryFind(originalTargets, connector.TargetLaneIndex, out LaneEndpoint target))
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
                if (!TrafficLaneEndpointHelpers.TryFind(selectedTargets, assignedTargets[sourceIndex], out LaneEndpoint target))
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

            if (!TrafficLaneEndpointHelpers.TryFind(sourceLanes, branchSourceLaneIndex, out LaneEndpoint branchSource) ||
                !TrafficLaneEndpointHelpers.TryFind(selectedTargets, extraTargetLaneIndex, out LaneEndpoint branchTarget))
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
            float2 sourceOrigin = TrafficLaneEndpointHelpers.GetAveragePosition(sourceLanes);
            TrafficLaneEndpointHelpers.AssignLaterals(sourceLanes, sourceOrigin, right);
            TrafficLaneEndpointHelpers.AssignLaterals(targetLanes, sourceOrigin, right);
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
                            !TrafficLaneEndpointHelpers.TryFind(targetLanes, connector.TargetLaneIndex, out LaneEndpoint target))
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
                if (!TrafficLaneEndpointHelpers.TryFind(targetLanes, assignedTargets[sourceIndex], out LaneEndpoint target))
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

    }
}
