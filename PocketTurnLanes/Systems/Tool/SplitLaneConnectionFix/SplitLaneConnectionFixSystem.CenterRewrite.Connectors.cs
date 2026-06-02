using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using NetCarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void CollectCenterConnectorLanes(
            Entity centerNode,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output,
            bool roadOnly)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                PathMethod pathMethods = subLane.m_PathMethods;
                if ((roadOnly ? (pathMethods & PathMethod.Road) == 0 : pathMethods == 0) ||
                    !TryGetConnectorLaneEdges(centerNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    (roadOnly && !EntityManager.HasComponent<NetCarLane>(laneEntity)) ||
                    sourceEdge == Entity.Null ||
                    targetEdge == Entity.Null ||
                    !IsEdgeConnectedToNode(sourceEdge, centerNode) ||
                    !IsEdgeConnectedToNode(targetEdge, centerNode))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }
    }
}
