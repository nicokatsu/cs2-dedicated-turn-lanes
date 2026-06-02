using System.Collections.Generic;
using PocketTurnLanes.Tool.Traffic;
using Unity.Mathematics;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
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

    }
}
