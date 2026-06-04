using System;
using System.Collections.Generic;
using System.Linq;
using Game.Pathfind;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficSnapshotLaneMapper
    {
        public static TrafficEndpointSnapshot CreateMissingEndpointSnapshot()
        {
            return new TrafficEndpointSnapshot
            {
                HasEndpoint = false,
                Lateral = 0f,
                Order = -1
            };
        }

        public static TrafficEndpointSnapshot CaptureEndpointSnapshot(
            IReadOnlyList<LaneEndpoint> endpoints,
            int laneIndex)
        {
            int order = TrafficLaneEndpointHelpers.FindOrder(endpoints, laneIndex);
            if (order < 0)
            {
                return CreateMissingEndpointSnapshot();
            }

            return new TrafficEndpointSnapshot
            {
                HasEndpoint = true,
                Lateral = endpoints[order].Lateral,
                Order = order
            };
        }

        public static bool TryBuildTransitionMapping(
            int sourceLaneIndex,
            int targetLaneIndex,
            PathMethod method,
            bool isUnsafe,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            out TransitionConnectionSnapshotMapping mapping)
        {
            mapping = default;
            if (!TrafficLaneEndpointHelpers.TryFind(sourceLanes, sourceLaneIndex, out LaneEndpoint source) ||
                !TrafficLaneEndpointHelpers.TryFind(targetLanes, targetLaneIndex, out LaneEndpoint target))
            {
                return false;
            }

            mapping = new TransitionConnectionSnapshotMapping
            {
                SourceLaneIndex = sourceLaneIndex,
                TargetLaneIndex = targetLaneIndex,
                SourceLateral = source.Lateral,
                TargetLateral = target.Lateral,
                SourceLanePosition = source.LanePosition,
                TargetLanePosition = target.LanePosition,
                SourceCarriagewayAndGroup = source.CarriagewayAndGroup,
                TargetCarriagewayAndGroup = target.CarriagewayAndGroup,
                Method = method,
                IsUnsafe = isUnsafe
            };
            return true;
        }

        public static bool TryBuildLaneRemap(
            IReadOnlyList<TransitionConnectionSnapshotMapping> snapshotMappings,
            IReadOnlyList<LaneEndpoint> currentLanes,
            bool source,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder,
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
                    reason = $"no current lane for snapshotLane={snapshotLane.LaneIndex} rank={i} current={FormatLaneOrder(currentLanes, formatLaneOrder)}";
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

        public static bool TryResolveEndpoint(
            IReadOnlyList<LaneEndpoint> endpoints,
            int snapshotLaneIndex,
            bool hasSnapshotEndpoint,
            float snapshotLateral,
            int snapshotOrder,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder,
            out LaneEndpoint endpoint,
            out string detail)
        {
            endpoint = default;
            detail = string.Empty;

            if (TrafficLaneEndpointHelpers.TryFind(endpoints, snapshotLaneIndex, out endpoint))
            {
                detail = $"sameLaneIndex {snapshotLaneIndex}->{endpoint.LaneIndex}";
                return true;
            }

            if (hasSnapshotEndpoint &&
                snapshotOrder >= 0 &&
                endpoints != null &&
                snapshotOrder < endpoints.Count)
            {
                endpoint = endpoints[snapshotOrder];
                detail = $"rankFallback {snapshotLaneIndex}->{endpoint.LaneIndex} order={snapshotOrder}";
                return true;
            }

            if (hasSnapshotEndpoint &&
                endpoints != null)
            {
                float bestError = float.MaxValue;
                int bestIndex = -1;
                for (int i = 0; i < endpoints.Count; i++)
                {
                    float error = math.abs(endpoints[i].Lateral - snapshotLateral);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    endpoint = endpoints[bestIndex];
                    detail = $"lateralFallback {snapshotLaneIndex}->{endpoint.LaneIndex} snapshotLateral={snapshotLateral:0.##} currentLateral={endpoint.Lateral:0.##} error={bestError:0.##}";
                    return true;
                }
            }

            detail = $"laneMissing lane={snapshotLaneIndex} endpoints={FormatLaneOrder(endpoints, formatLaneOrder)}";
            return false;
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

        private static string FormatLaneOrder(
            IReadOnlyList<LaneEndpoint> endpoints,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder)
        {
            return formatLaneOrder != null ? formatLaneOrder(endpoints) : "<none>";
        }

        private struct SnapshotLaneOrder
        {
            public int LaneIndex;
            public float LateralSum;
            public int Count;
            public int FirstSnapshotOrder;

            public float AverageLateral => Count > 0 ? LateralSum / Count : 0f;
        }
    }
}
