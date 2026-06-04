using System.Collections.Generic;
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
            bool tolerateRuntimeStaleUturn =
                request.TrafficWritten &&
                !s_EnableRuntimeStaleUturnDirectDeletion &&
                staleUturnCount > 0;
            bool matches = forwardMatches && reverseMatches && (staleUturnCount == 0 || tolerateRuntimeStaleUturn);
            if (!matches)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Connector verification mismatch splitNode={FormatEntity(request.SplitNode)} mode={request.Mode} forward={forwardDetail} reverse={reverseDetail} staleUturnCount={staleUturnCount} staleUturns={staleUturnSummary}.");
            }
            else if (tolerateRuntimeStaleUturn)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Connector verification road-stable with runtime stale U-turns left in place splitNode={FormatEntity(request.SplitNode)} mode={request.Mode} forward={forwardDetail} reverse={reverseDetail} staleUturnCount={staleUturnCount} staleUturns={staleUturnSummary} directDeletion={s_EnableRuntimeStaleUturnDirectDeletion} reason={RuntimeStaleUturnDirectDeletionDisabledReason}.");
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
            HashSet<ConnectionKey> expected = TrafficConnectionKeySets.FromMappings(mappings);

            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, m_ExistingConnectorLanes);
            HashSet<ConnectionKey> actual = TrafficConnectionKeySets.FromConnectors(m_ExistingConnectorLanes);

            bool matches = expected.SetEquals(actual);
            detail = $"{direction} source={FormatEntity(sourceEdge)} target={FormatEntity(targetEdge)} expected={FormatConnectionSet(expected)} actual={FormatConnectionSet(actual)} connectors={m_ExistingConnectorLanes.Count}";
            return matches;
        }
    }
}
