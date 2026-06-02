using System.Collections.Generic;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryFindCenterTargetEndpoint(
            Entity centerNode,
            Entity targetEdge,
            int laneIndex,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint targetEndpoint)
        {
            targetEndpoint = default;
            if (!TryGetCenterTargetEndpoints(centerNode, targetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                return false;
            }

            return TryFindLaneEndpoint(targetEndpoints, laneIndex, out targetEndpoint);
        }

        private bool TryFindCenterPreservationTargetEndpoint(
            Entity centerNode,
            Entity targetEdge,
            int laneIndex,
            Dictionary<Entity, List<LaneEndpoint>> roadTargetEndpointCache,
            Dictionary<Entity, List<LaneEndpoint>> preservationTargetEndpointCache,
            out LaneEndpoint targetEndpoint)
        {
            if (TryFindCenterTargetEndpoint(
                    centerNode,
                    targetEdge,
                    laneIndex,
                    roadTargetEndpointCache,
                    out targetEndpoint))
            {
                return true;
            }

            if (!preservationTargetEndpointCache.TryGetValue(targetEdge, out List<LaneEndpoint> targetEndpoints))
            {
                targetEndpoints = new List<LaneEndpoint>(8);
                CollectEdgePreservationLaneEndpoints(
                    targetEdge,
                    centerNode,
                    EndpointRole.TargetStartAtNode,
                    targetEndpoints);
                SortLaneEndpointsByLateral(targetEndpoints);
                preservationTargetEndpointCache.Add(targetEdge, targetEndpoints);
            }

            return TryFindLaneEndpoint(targetEndpoints, laneIndex, out targetEndpoint);
        }

        private bool TryGetCenterTargetEndpoints(
            Entity centerNode,
            Entity targetEdge,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out List<LaneEndpoint> targetEndpoints)
        {
            if (!targetEndpointCache.TryGetValue(targetEdge, out targetEndpoints))
            {
                targetEndpoints = new List<LaneEndpoint>(8);
                CollectEdgeCarLaneEndpoints(targetEdge, centerNode, EndpointRole.TargetStartAtNode, targetEndpoints);
                SortLaneEndpointsByLateral(targetEndpoints);
                targetEndpointCache.Add(targetEdge, targetEndpoints);
            }

            return targetEndpoints.Count > 0;
        }
    }
}
