using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficLaneEndpointHelpers
    {
        public static int FindOrder(IReadOnlyList<LaneEndpoint> lanes, int laneIndex)
        {
            if (lanes == null)
            {
                return -1;
            }

            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i].LaneIndex == laneIndex)
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool TryFind(IReadOnlyList<LaneEndpoint> lanes, int laneIndex, out LaneEndpoint lane)
        {
            if (lanes != null)
            {
                for (int i = 0; i < lanes.Count; i++)
                {
                    if (lanes[i].LaneIndex == laneIndex)
                    {
                        lane = lanes[i];
                        return true;
                    }
                }
            }

            lane = default;
            return false;
        }

        public static void SortByLateral(List<LaneEndpoint> lanes)
        {
            lanes.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));
        }

        public static float2 GetAveragePosition(IReadOnlyList<LaneEndpoint> lanes)
        {
            float2 origin = default;
            for (int i = 0; i < lanes.Count; i++)
            {
                origin += lanes[i].Position.xz;
            }

            return origin / math.max(1, lanes.Count);
        }

        public static void AssignLaterals(List<LaneEndpoint> lanes, float2 origin, float2 right)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                LaneEndpoint lane = lanes[i];
                lane.Lateral = math.dot(lane.Position.xz - origin, right);
                lanes[i] = lane;
            }

            SortByLateral(lanes);
        }

        public static void NormalizeTransitionLaneLaterals(List<LaneEndpoint> sourceLanes, List<LaneEndpoint> targetLanes)
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
            AssignLaterals(sourceLanes, sourceOrigin, right);
            AssignLaterals(targetLanes, sourceOrigin, right);
        }

        public static Bezier4x3 BuildConnectorCurve(LaneEndpoint source, LaneEndpoint target)
        {
            float distance = math.max(1f, math.distance(source.Position.xz, target.Position.xz));
            float tangentLength = math.min(12f, distance * 0.5f);
            float3 sourceTangent = new float3(source.TravelDirection.x, 0f, source.TravelDirection.y) * tangentLength;
            float3 targetTangent = new float3(target.TravelDirection.x, 0f, target.TravelDirection.y) * tangentLength;
            return NetUtils.FitCurve(source.Position, sourceTangent, -targetTangent, target.Position);
        }
    }
}
