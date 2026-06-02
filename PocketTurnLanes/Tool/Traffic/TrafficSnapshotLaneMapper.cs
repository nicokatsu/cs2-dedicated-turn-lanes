using System;
using System.Collections.Generic;
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

        private static string FormatLaneOrder(
            IReadOnlyList<LaneEndpoint> endpoints,
            Func<IReadOnlyList<LaneEndpoint>, string> formatLaneOrder)
        {
            return formatLaneOrder != null ? formatLaneOrder(endpoints) : "<none>";
        }
    }
}
