using System.Collections.Generic;
using Colossal.Entities;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void CollectLogicalUturnSuppressionSourcesForPlan(Request request, TrafficMappingPlan plan)
        {
            if (!EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return;
            }

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            plan.StaleUturnConnections = m_StaleConnectorLanes.Count;
            for (int i = 0; i < m_StaleConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_StaleConnectorLanes[i];
                plan.StaleUturnSourceKeys.Add(new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex));
            }

            if (plan.StaleUturnSourceKeys.Count == 0)
            {
                return;
            }

            CollectSplitNodeConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_ConnectorLanes);
            foreach (SourceLaneKey sourceKey in plan.StaleUturnSourceKeys)
            {
                if (CountRuntimeNonUturnConnectionsForSource(sourceKey, m_ConnectorLanes) > 0)
                {
                    plan.RuntimeNonUturnSourceKeys.Add(sourceKey);
                    continue;
                }

                if (TryFindMappingEndpoint(request, sourceKey.Edge, sourceKey.LaneIndex, source: true, out _))
                {
                    EnsureTrafficPlanSource(plan.BySource, sourceKey);
                }
            }
        }

        private static int CountRuntimeNonUturnConnectionsForSource(SourceLaneKey sourceKey, IReadOnlyList<ConnectorLane> connectors)
        {
            int count = 0;
            if (connectors == null)
            {
                return count;
            }

            for (int i = 0; i < connectors.Count; i++)
            {
                ConnectorLane connector = connectors[i];
                if (connector.SourceEdge == sourceKey.Edge &&
                    connector.SourceLaneIndex == sourceKey.LaneIndex &&
                    connector.TargetEdge != sourceKey.Edge)
                {
                    count++;
                }
            }

            return count;
        }

        private static void EnsureTrafficPlanSource(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            SourceLaneKey sourceKey)
        {
            if (!bySource.ContainsKey(sourceKey))
            {
                bySource.Add(sourceKey, new Dictionary<TargetLaneKey, LaneMapping>());
            }
        }
    }
}
