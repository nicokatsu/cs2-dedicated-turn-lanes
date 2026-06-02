using System.Collections.Generic;
using Colossal.Entities;
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
        private int CollectConnectorLanes(Entity splitNode, Entity outerEdge, Entity pocketEdge, List<ConnectorLane> output)
        {
            output.Clear();
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return 0;
            }

            CollectConnectorLanes(splitNode, outerEdge, pocketEdge, subLanes, output);
            return output.Count;
        }

        private void CollectConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    !TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    sourceEdge != outerEdge ||
                    targetEdge != pocketEdge)
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private void CollectSplitPairPreservationConnectorLanes(
            Entity splitNode,
            Entity sourceEdge,
            Entity targetEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if (subLane.m_PathMethods == 0 ||
                    !TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity actualSourceEdge, out Entity actualTargetEdge) ||
                    actualSourceEdge != sourceEdge ||
                    (actualTargetEdge != targetEdge &&
                     actualTargetEdge != sourceEdge))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, actualSourceEdge, actualTargetEdge));
            }
        }

        private void CollectSplitNodeConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            bool restrictToSplitPair = outerEdge != Entity.Null;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if (!TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    (subLane.m_PathMethods == 0 && !IsTrackConnectorCandidate(laneEntity, subLane)) ||
                    (restrictToSplitPair &&
                     (sourceEdge != outerEdge && sourceEdge != pocketEdge ||
                      targetEdge != outerEdge && targetEdge != pocketEdge)))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private int CountStaleSplitNodeUturnConnectorLanes(Entity splitNode, Entity outerEdge, Entity pocketEdge, out string summary)
        {
            summary = string.Empty;
            m_StaleConnectorLanes.Clear();
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return 0;
            }

            CollectStaleSplitNodeUturnConnectorLanes(splitNode, outerEdge, pocketEdge, subLanes, m_StaleConnectorLanes);
            summary = FormatConnectorLanes(m_StaleConnectorLanes);
            return m_StaleConnectorLanes.Count;
        }

        private void CollectStaleSplitNodeUturnConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            bool restrictToSplitPair = outerEdge != Entity.Null;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if (!TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    (subLane.m_PathMethods == 0 && !IsTrackConnectorCandidate(laneEntity, subLane)) ||
                    !lane.m_StartNode.OwnerEquals(lane.m_EndNode) ||
                    sourceEdge != targetEdge ||
                    (restrictToSplitPair && sourceEdge != outerEdge && sourceEdge != pocketEdge))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }
    }
}
