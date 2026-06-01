using System.Collections.Generic;
using Colossal.Entities;
using Game.Pathfind;
using Unity.Entities;
using SubLane = Game.Net.SubLane;
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

            bool verifyTrack = request.FinalTrackTrafficWritten || !HasTrackPreservationMappings(request);
            bool trackForwardMatches = true;
            bool trackReverseMatches = true;
            string trackForwardDetail = "trackForward unifiedTrafficWriteNotVerified";
            string trackReverseDetail = "trackReverse unifiedTrafficWriteNotVerified";
            if (verifyTrack)
            {
                trackForwardMatches = VerifyTrackConnectorDirection(
                    request,
                    request.TrackForwardMappings,
                    request.OuterEdge,
                    request.PocketEdge,
                    "trackForward",
                    out trackForwardDetail);
                trackReverseMatches = VerifyTrackConnectorDirection(
                    request,
                    request.TrackReverseMappings,
                    request.PocketEdge,
                    request.OuterEdge,
                    "trackReverse",
                    out trackReverseDetail);
            }

            int staleUturnCount = CountStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, out string staleUturnSummary);
            bool matches = forwardMatches && reverseMatches && trackForwardMatches && trackReverseMatches && staleUturnCount == 0;
            if (!matches)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Connector verification mismatch splitNode={FormatEntity(request.SplitNode)} mode={request.Mode} verifyTrack={verifyTrack} finalTrackTrafficWritten={request.FinalTrackTrafficWritten} forward={forwardDetail} reverse={reverseDetail} trackForward={trackForwardDetail} trackReverse={trackReverseDetail} staleUturnCount={staleUturnCount} staleUturns={staleUturnSummary}.");
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

        private bool VerifyTrackConnectorDirection(
            Request request,
            LaneMapping[] mappings,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            out string detail)
        {
            if (mappings == null || mappings.Length == 0)
            {
                detail = $"{direction} source={FormatEntity(sourceEdge)} target={FormatEntity(targetEdge)} expected=<none>";
                return true;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            HashSet<ConnectionKey> actual = new HashSet<ConnectionKey>();
            if (EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                CollectTrackConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_TrackConnectorLanes);
                for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
                {
                    ConnectorLane connector = m_TrackConnectorLanes[i];
                    if ((connector.PathMethods & PathMethod.Track) == 0)
                    {
                        continue;
                    }

                    actual.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
                }
            }
            else
            {
                m_TrackConnectorLanes.Clear();
            }

            bool matches = expected.SetEquals(actual);
            detail = $"{direction} source={FormatEntity(sourceEdge)} target={FormatEntity(targetEdge)} expected={FormatConnectionSet(expected)} actual={FormatConnectionSet(actual)} connectors={m_TrackConnectorLanes.Count} expectedMappings={FormatMappings(mappings)}";
            return matches;
        }
    }
}
