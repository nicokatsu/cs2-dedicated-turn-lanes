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
            foreach (SourceLaneKey sourceKey in TrafficUturnSuppressionPlanner.BuildStaleSourceKeys(m_StaleConnectorLanes))
            {
                plan.StaleUturnSourceKeys.Add(sourceKey);
            }

            if (plan.StaleUturnSourceKeys.Count == 0)
            {
                return;
            }

            CollectSplitNodeConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_ConnectorLanes);
            foreach (SourceLaneKey sourceKey in plan.StaleUturnSourceKeys)
            {
                if (TrafficUturnSuppressionPlanner.CountRuntimeNonUturnConnections(sourceKey, m_ConnectorLanes) > 0)
                {
                    plan.RuntimeNonUturnSourceKeys.Add(sourceKey);
                    continue;
                }

                if (TryFindMappingEndpoint(request, sourceKey.Edge, sourceKey.LaneIndex, source: true, out _))
                {
                    TrafficUturnSuppressionPlanner.EnsureTrafficPlanSource(plan.BySource, sourceKey);
                }
            }
        }
    }
}
