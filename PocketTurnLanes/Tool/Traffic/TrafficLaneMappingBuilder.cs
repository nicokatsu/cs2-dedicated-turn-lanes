using System;
using System.Collections.Generic;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficLaneMappingBuilder
    {
        private enum TargetAssignmentMode
        {
            DirectOrder,
            ExistingThenLateralFallback
        }

        public static bool TryBuildDesiredMappings(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            int extraTargetIndex,
            int branchSourceLaneIndex,
            IReadOnlyList<ConnectorLane> existingConnectors,
            bool preferExistingConnectors,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder,
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

            if (!TryAssignTargetsByExistingOrLateral(
                    sourceLanes,
                    originalTargets,
                    existingConnectors,
                    preferExistingConnectors ? TargetAssignmentMode.ExistingThenLateralFallback : TargetAssignmentMode.DirectOrder,
                    extraTargetLaneIndex,
                    formatLaneOrder,
                    "no remaining original target",
                    "originalTargets",
                    out int[] assignedTargets,
                    out int existingAssignments,
                    out reason))
            {
                return false;
            }

            List<LaneMapping> result = new List<LaneMapping>(sourceLanes.Count + 1);
            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (!TrafficLaneEndpointHelpers.TryFind(selectedTargets, assignedTargets[sourceIndex], out LaneEndpoint target))
                {
                    reason = $"assigned target missing source={sourceLanes[sourceIndex].LaneIndex} target={assignedTargets[sourceIndex]}";
                    return false;
                }

                PathMethod method = TrafficPathMethods.GetMappingMethod(sourceLanes[sourceIndex], target);
                result.Add(new LaneMapping
                {
                    SourceEdge = sourceLanes[sourceIndex].Edge,
                    TargetEdge = target.Edge,
                    SourceLaneIndex = sourceLanes[sourceIndex].LaneIndex,
                    TargetLaneIndex = assignedTargets[sourceIndex],
                    Method = method,
                    IsBranch = false,
                    PreserveSharedTrack = TrafficPathMethods.HasSharedTrackPath(method)
                });
            }

            if (!TrafficLaneEndpointHelpers.TryFind(sourceLanes, branchSourceLaneIndex, out LaneEndpoint branchSource) ||
                !TrafficLaneEndpointHelpers.TryFind(selectedTargets, extraTargetLaneIndex, out LaneEndpoint branchTarget))
            {
                reason = $"branch endpoint missing source={branchSourceLaneIndex} target={extraTargetLaneIndex}";
                return false;
            }

            PathMethod branchMethod = TrafficPathMethods.GetMappingMethod(branchSource, branchTarget);
            result.Add(new LaneMapping
            {
                SourceEdge = branchSource.Edge,
                TargetEdge = branchTarget.Edge,
                SourceLaneIndex = branchSourceLaneIndex,
                TargetLaneIndex = extraTargetLaneIndex,
                Method = branchMethod,
                IsBranch = true
            });

            mappings = result.ToArray();
            mappingSource = !preferExistingConnectors
                ? "center-turn-order"
                : existingAssignments == sourceLanes.Count
                    ? "existing-connectors"
                    : existingAssignments > 0
                        ? $"existing-connectors+fallback({existingAssignments}/{sourceLanes.Count})"
                        : "lateral-fallback";
            return true;
        }

        public static bool TryBuildSnapshotReverseMappings(
            TransitionConnectionSnapshot snapshot,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder,
            Func<TransitionConnectionSnapshot, string> formatSnapshot,
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

            if (!TrafficSnapshotLaneMapper.TryBuildLaneRemap(
                    snapshot.Mappings,
                    sourceLanes,
                    source: true,
                    formatLaneOrder,
                    out Dictionary<int, LaneEndpoint> sourceRemap,
                    out string sourceRemapDetail,
                    out string sourceRemapReason))
            {
                reason = $"source remap failed: {sourceRemapReason}";
                return false;
            }

            if (!TrafficSnapshotLaneMapper.TryBuildLaneRemap(
                    snapshot.Mappings,
                    targetLanes,
                    source: false,
                    formatLaneOrder,
                    out Dictionary<int, LaneEndpoint> targetRemap,
                    out string targetRemapDetail,
                    out string targetRemapReason))
            {
                reason = $"target remap failed: {targetRemapReason}";
                return false;
            }

            List<LaneMapping> result = new List<LaneMapping>(snapshot.Mappings.Length);
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
                PathMethod method = RemapSnapshotMethod(snapshotMapping.Method, source, target);
                result.Add(new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = source.LaneIndex,
                    TargetLaneIndex = target.LaneIndex,
                    Method = method,
                    IsBranch = false,
                    PreserveSharedTrack = TrafficPathMethods.HasSharedTrackPath(method)
                });
            }

            if (result.Count == 0)
            {
                reason = $"no snapshot mappings could be remapped snapshot={FormatSnapshot(snapshot, formatSnapshot)} skipped={skipped}";
                return false;
            }

            mappings = result.ToArray();
            mappingSource = $"snapshot={snapshot.Source}; sourceRemap=({sourceRemapDetail}); targetRemap=({targetRemapDetail}); skipped={skipped}; original={snapshot.Mappings.Length}";
            reason = "ok";
            return true;
        }

        public static bool TryBuildStraightMappings(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            IReadOnlyList<ConnectorLane> existingConnectors,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder,
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

            if (!TryAssignTargetsByExistingOrLateral(
                    sourceLanes,
                    targetLanes,
                    existingConnectors,
                    TargetAssignmentMode.ExistingThenLateralFallback,
                    excludedTargetLaneIndex: -1,
                    formatLaneOrder,
                    "no remaining reverse target",
                    "targetOrder",
                    out int[] assignedTargets,
                    out int existingAssignments,
                    out reason))
            {
                return false;
            }

            LaneMapping[] result = new LaneMapping[sourceLanes.Count];
            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (!TrafficLaneEndpointHelpers.TryFind(targetLanes, assignedTargets[sourceIndex], out LaneEndpoint target))
                {
                    reason = $"assigned reverse target missing source={sourceLanes[sourceIndex].LaneIndex} target={assignedTargets[sourceIndex]}";
                    return false;
                }

                PathMethod method = TrafficPathMethods.GetMappingMethod(sourceLanes[sourceIndex], target);
                result[sourceIndex] = new LaneMapping
                {
                    SourceEdge = sourceLanes[sourceIndex].Edge,
                    TargetEdge = target.Edge,
                    SourceLaneIndex = sourceLanes[sourceIndex].LaneIndex,
                    TargetLaneIndex = target.LaneIndex,
                    Method = method,
                    IsBranch = false,
                    PreserveSharedTrack = TrafficPathMethods.HasSharedTrackPath(method)
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

        private static bool TryAssignTargetsByExistingOrLateral(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            IReadOnlyList<ConnectorLane> existingConnectors,
            TargetAssignmentMode assignmentMode,
            int excludedTargetLaneIndex,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder,
            string noRemainingTargetReason,
            string targetOrderFieldName,
            out int[] assignedTargets,
            out int existingAssignments,
            out string reason)
        {
            assignedTargets = new int[sourceLanes.Count];
            for (int i = 0; i < assignedTargets.Length; i++)
            {
                assignedTargets[i] = -1;
            }

            reason = string.Empty;
            existingAssignments = 0;
            HashSet<int> usedTargets = new HashSet<int>();

            if (assignmentMode == TargetAssignmentMode.DirectOrder)
            {
                for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
                {
                    assignedTargets[sourceIndex] = targetLanes[sourceIndex].LaneIndex;
                    usedTargets.Add(assignedTargets[sourceIndex]);
                }
            }
            else if (existingConnectors != null)
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
                            connector.TargetLaneIndex == excludedTargetLaneIndex ||
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
                    reason = $"{noRemainingTargetReason} for source={sourceLanes[sourceIndex].LaneIndex} assigned={string.Join(",", assignedTargets)} {targetOrderFieldName}={FormatLaneOrder(targetLanes, formatLaneOrder)}";
                    return false;
                }

                assignedTargets[sourceIndex] = bestFallbackTarget;
                usedTargets.Add(assignedTargets[sourceIndex]);
            }

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

        private static string FormatLaneOrder(
            IReadOnlyList<LaneEndpoint> lanes,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder)
        {
            return formatLaneOrder != null ? formatLaneOrder(lanes) : "<none>";
        }

        private static string FormatSnapshot(
            TransitionConnectionSnapshot snapshot,
            Func<TransitionConnectionSnapshot, string> formatSnapshot)
        {
            return formatSnapshot != null ? formatSnapshot(snapshot) : "<none>";
        }
    }
}
