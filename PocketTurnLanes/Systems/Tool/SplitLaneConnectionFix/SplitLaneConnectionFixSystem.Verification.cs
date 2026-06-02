using System.Collections.Generic;
using Colossal.Entities;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool VerifyConnectorLanes(Request request)
        {
            RoadDirectionPlan forwardDirection = GetRoadDirectionPlan(request, RoadDirection.Forward);
            bool forwardMatches = VerifyRoadDirection(request, forwardDirection, out string forwardDetail);

            RoadDirectionPlan reverseDirection = GetRoadDirectionPlan(request, RoadDirection.Reverse);
            bool reverseMatches = VerifyRoadDirection(request, reverseDirection, out string reverseDetail);

            int staleUturnCount = CountStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, out string staleUturnSummary);
            bool matches = forwardMatches && reverseMatches && staleUturnCount == 0;
            if (!matches)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Connector verification mismatch splitNode={FormatEntity(request.SplitNode)} mode={request.Mode} forward={forwardDetail} reverse={reverseDetail} staleUturnCount={staleUturnCount} staleUturns={staleUturnSummary}.");
            }

            return matches;
        }

        private bool VerifyRoadDirection(Request request, RoadDirectionPlan direction, out string detail)
        {
            detail = GetRoadDirectionInitialReason(direction);
            if (direction.State != RoadDirectionState.Prepared)
            {
                return true;
            }

            return direction.Mappings != null &&
                   direction.Mappings.Length > 0 &&
                   VerifyConnectorDirection(
                       request,
                       direction.Mappings,
                       direction.SourceEdge,
                       direction.TargetEdge,
                       direction.Label,
                       out detail);
        }

        private bool VerifyConnectorDirection(
            Request request,
            LaneMapping[] mappings,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            out string detail)
        {
            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            HashSet<ConnectionKey> actual = new HashSet<ConnectionKey>();
            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, m_ExistingConnectorLanes);
            for (int i = 0; i < m_ExistingConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ExistingConnectorLanes[i];
                actual.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
            }

            bool matches = expected.SetEquals(actual);
            detail = $"{direction} source={FormatEntity(sourceEdge)} target={FormatEntity(targetEdge)} expected={FormatConnectionSet(expected)} actual={FormatConnectionSet(actual)} connectors={m_ExistingConnectorLanes.Count}";
            return matches;
        }
    }
}
