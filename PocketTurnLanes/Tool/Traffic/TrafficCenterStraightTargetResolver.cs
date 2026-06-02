using System.Collections.Generic;
using PocketTurnLanes.Tool;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficCenterStraightTargetResolver
    {
        public delegate bool TryGetTargetEndpoints(Entity targetEdge, out IReadOnlyList<LaneEndpoint> targetEndpoints);

        public static bool TryResolveShiftedStraightTarget(
            LaneEndpoint smallSource,
            LaneEndpoint bigSource,
            CenterConnectorCandidate straight,
            TryGetTargetEndpoints tryGetTargetEndpoints,
            out LaneEndpoint shiftedTarget,
            out string detail)
        {
            shiftedTarget = default;
            detail = string.Empty;

            if (!straight.HasTargetEndpoint)
            {
                detail = "straight target endpoint missing";
                return false;
            }

            if (math.abs(smallSource.Lateral - bigSource.Lateral) <= 0.0001f)
            {
                detail = $"source lateral tie small={smallSource.Lateral:0.###} big={bigSource.Lateral:0.###}";
                return false;
            }

            if (!tryGetTargetEndpoints(straight.Connector.TargetEdge, out IReadOnlyList<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(straight.Connector.TargetEdge)}";
                return false;
            }

            int originalTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, straight.Connector.TargetLaneIndex);
            if (originalTargetOrder < 0)
            {
                detail = $"original straight target missing lane={straight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            int shift = smallSource.Lateral > bigSource.Lateral ? -1 : 1;
            int shiftedOrder = originalTargetOrder + shift;
            if (shiftedOrder < 0 || shiftedOrder >= targetEndpoints.Count)
            {
                detail = $"adjacent target unavailable originalOrder={originalTargetOrder} shift={shift} targetCount={targetEndpoints.Count} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            LaneEndpoint originalTarget = targetEndpoints[originalTargetOrder];
            shiftedTarget = targetEndpoints[shiftedOrder];
            float targetDelta = shiftedTarget.Lateral - originalTarget.Lateral;
            if (math.abs(targetDelta) <= 0.0001f ||
                targetDelta > 0f != shift > 0)
            {
                detail = $"target lateral not ordered original={originalTarget.Lateral:0.###} shifted={shiftedTarget.Lateral:0.###} shift={shift}";
                return false;
            }

            detail = $"sourceLane {bigSource.LaneIndex}->{smallSource.LaneIndex} targetLane {originalTarget.LaneIndex}->{shiftedTarget.LaneIndex} targetEdge={FormatEntity(straight.Connector.TargetEdge)} order {originalTargetOrder}->{shiftedOrder} shift={shift}";
            return true;
        }

        public static bool TrySelectPocketExtraAndMiddleSmallStraightLane(
            IReadOnlyList<CenterLaneMovementSummary> smallStraight,
            int pocketExtraCenterLane,
            out CenterLaneMovementSummary pocketExtraLane,
            out CenterLaneMovementSummary middleLane,
            out string detail)
        {
            pocketExtraLane = null;
            middleLane = null;
            detail = string.Empty;
            if (smallStraight == null || smallStraight.Count != 2)
            {
                detail = $"smallStraightCount={smallStraight?.Count ?? 0}";
                return false;
            }

            for (int i = 0; i < smallStraight.Count; i++)
            {
                CenterLaneMovementSummary candidate = smallStraight[i];
                if (candidate.SourceEndpoint.LaneIndex == pocketExtraCenterLane)
                {
                    if (pocketExtraLane != null)
                    {
                        detail = $"duplicatePocketExtra lane={pocketExtraCenterLane}";
                        return false;
                    }

                    pocketExtraLane = candidate;
                }
                else
                {
                    middleLane = candidate;
                }
            }

            if (pocketExtraLane == null || middleLane == null)
            {
                detail = $"pocketExtraMissing expected={pocketExtraCenterLane}";
                return false;
            }

            detail = $"pocketExtra={pocketExtraLane.SourceEndpoint.LaneIndex} middle={middleLane.SourceEndpoint.LaneIndex}";
            return true;
        }

        public static bool TryResolveCascadeStraightTargets(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary middleLane,
            CenterLaneMovementSummary bigLane,
            CenterConnectorCandidate middleCurrentStraight,
            CenterConnectorCandidate bigCurrentStraight,
            TryGetTargetEndpoints tryGetTargetEndpoints,
            out LaneEndpoint smallLaneStraightTarget,
            out LaneEndpoint middleLaneStraightTarget,
            out string detail)
        {
            smallLaneStraightTarget = default;
            middleLaneStraightTarget = default;
            detail = string.Empty;

            if (!middleCurrentStraight.HasTargetEndpoint ||
                !bigCurrentStraight.HasTargetEndpoint)
            {
                detail = $"straight endpoint missing middle={middleCurrentStraight.HasTargetEndpoint} big={bigCurrentStraight.HasTargetEndpoint}";
                return false;
            }

            if (!TryValidateThreeLaneSourceCascade(
                    sourceEndpoints,
                    smallLane.SourceEndpoint,
                    middleLane.SourceEndpoint,
                    bigLane.SourceEndpoint,
                    out string sourceDetail))
            {
                detail = sourceDetail;
                return false;
            }

            if (middleCurrentStraight.Connector.TargetEdge != bigCurrentStraight.Connector.TargetEdge)
            {
                detail = $"straight target edge mismatch middle={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} big={FormatEntity(bigCurrentStraight.Connector.TargetEdge)} source=({sourceDetail})";
                return false;
            }

            if (!tryGetTargetEndpoints(bigCurrentStraight.Connector.TargetEdge, out IReadOnlyList<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(bigCurrentStraight.Connector.TargetEdge)}";
                return false;
            }

            int middleTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
            int bigTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, bigCurrentStraight.Connector.TargetLaneIndex);
            if (middleTargetOrder < 0 || bigTargetOrder < 0)
            {
                detail = $"straight target order missing middleLane={middleCurrentStraight.Connector.TargetLaneIndex} bigLane={bigCurrentStraight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            int expectedTargetShift = smallLane.SourceEndpoint.Lateral > bigLane.SourceEndpoint.Lateral ? -1 : 1;
            if (middleTargetOrder != bigTargetOrder + expectedTargetShift)
            {
                detail = $"straight target not adjacent middleOrder={middleTargetOrder} bigOrder={bigTargetOrder} expectedShift={expectedTargetShift} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                return false;
            }

            smallLaneStraightTarget = middleCurrentStraight.TargetEndpoint;
            middleLaneStraightTarget = bigCurrentStraight.TargetEndpoint;
            detail = $"source=({sourceDetail}) straightCascade smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(bigCurrentStraight.Connector.TargetEdge)} targetOrders middle={middleTargetOrder} big={bigTargetOrder} expectedShift={expectedTargetShift}";
            return true;
        }

        public static bool TryResolveSmallStraightConflictTargets(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary middleLane,
            CenterLaneMovementSummary bigLane,
            CenterConnectorCandidate smallCurrentStraight,
            CenterConnectorCandidate middleCurrentStraight,
            TryGetTargetEndpoints tryGetTargetEndpoints,
            out LaneEndpoint smallLaneStraightTarget,
            out LaneEndpoint middleLaneStraightTarget,
            out string detail)
        {
            smallLaneStraightTarget = default;
            middleLaneStraightTarget = default;
            detail = string.Empty;

            if (!smallCurrentStraight.HasTargetEndpoint ||
                !middleCurrentStraight.HasTargetEndpoint)
            {
                detail = $"straight endpoint missing small={smallCurrentStraight.HasTargetEndpoint} middle={middleCurrentStraight.HasTargetEndpoint}";
                return false;
            }

            if (!TryValidateThreeLaneSourceCascade(
                    sourceEndpoints,
                    smallLane.SourceEndpoint,
                    middleLane.SourceEndpoint,
                    bigLane.SourceEndpoint,
                    out string sourceDetail))
            {
                detail = sourceDetail;
                return false;
            }

            if (smallCurrentStraight.Connector.TargetEdge != middleCurrentStraight.Connector.TargetEdge)
            {
                detail = $"straight target edge mismatch small={FormatEntity(smallCurrentStraight.Connector.TargetEdge)} middle={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} source=({sourceDetail})";
                return false;
            }

            if (!tryGetTargetEndpoints(middleCurrentStraight.Connector.TargetEdge, out IReadOnlyList<LaneEndpoint> targetEndpoints))
            {
                detail = $"target endpoint list missing edge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)}";
                return false;
            }

            int smallTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, smallCurrentStraight.Connector.TargetLaneIndex);
            int middleTargetOrder = TrafficLaneEndpointHelpers.FindOrder(targetEndpoints, middleCurrentStraight.Connector.TargetLaneIndex);
            if (smallTargetOrder < 0 || middleTargetOrder < 0)
            {
                detail = $"straight target order missing smallLane={smallCurrentStraight.Connector.TargetLaneIndex} middleLane={middleCurrentStraight.Connector.TargetLaneIndex} targets={FormatLaneOrder(targetEndpoints)}";
                return false;
            }

            smallLaneStraightTarget = smallCurrentStraight.TargetEndpoint;
            int smallSideTargetShift = smallLane.SourceEndpoint.Lateral > bigLane.SourceEndpoint.Lateral ? -1 : 1;
            if (smallCurrentStraight.Connector.TargetLaneIndex == middleCurrentStraight.Connector.TargetLaneIndex)
            {
                int shiftedMiddleOrder = smallTargetOrder - smallSideTargetShift;
                if (shiftedMiddleOrder < 0 || shiftedMiddleOrder >= targetEndpoints.Count)
                {
                    detail = $"duplicate straight target cannot shift middle smallTargetOrder={smallTargetOrder} shift={-smallSideTargetShift} targetCount={targetEndpoints.Count} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                    return false;
                }

                middleLaneStraightTarget = targetEndpoints[shiftedMiddleOrder];
                detail = $"source=({sourceDetail}) duplicateStraightTargetShifted smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} targetOrders small={smallTargetOrder} middle={shiftedMiddleOrder} smallSideShift={smallSideTargetShift}";
                return true;
            }

            if (smallTargetOrder != middleTargetOrder + smallSideTargetShift)
            {
                detail = $"straight targets not in shifted order smallOrder={smallTargetOrder} middleOrder={middleTargetOrder} expectedSmallShift={smallSideTargetShift} targets={FormatLaneOrder(targetEndpoints)} source=({sourceDetail})";
                return false;
            }

            middleLaneStraightTarget = middleCurrentStraight.TargetEndpoint;
            detail = $"source=({sourceDetail}) distinctStraightTargets smallLane {smallLane.SourceEndpoint.LaneIndex}->{smallLaneStraightTarget.LaneIndex} middleLane {middleLane.SourceEndpoint.LaneIndex}->{middleLaneStraightTarget.LaneIndex} targetEdge={FormatEntity(middleCurrentStraight.Connector.TargetEdge)} targetOrders small={smallTargetOrder} middle={middleTargetOrder} smallSideShift={smallSideTargetShift}";
            return true;
        }

        private static bool TryValidateThreeLaneSourceCascade(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            LaneEndpoint smallSource,
            LaneEndpoint middleSource,
            LaneEndpoint bigSource,
            out string detail)
        {
            detail = string.Empty;
            int smallOrder = TrafficLaneEndpointHelpers.FindOrder(sourceEndpoints, smallSource.LaneIndex);
            int middleOrder = TrafficLaneEndpointHelpers.FindOrder(sourceEndpoints, middleSource.LaneIndex);
            int bigOrder = TrafficLaneEndpointHelpers.FindOrder(sourceEndpoints, bigSource.LaneIndex);
            if (smallOrder < 0 || middleOrder < 0 || bigOrder < 0)
            {
                detail = $"source order missing small={smallSource.LaneIndex}:{smallOrder} middle={middleSource.LaneIndex}:{middleOrder} big={bigSource.LaneIndex}:{bigOrder}";
                return false;
            }

            if (smallOrder == bigOrder)
            {
                detail = $"source order tie small={smallOrder} big={bigOrder}";
                return false;
            }

            int direction = bigOrder > smallOrder ? 1 : -1;
            if (middleOrder != smallOrder + direction ||
                bigOrder != middleOrder + direction)
            {
                detail = $"source lanes not adjacent smallOrder={smallOrder} middleOrder={middleOrder} bigOrder={bigOrder} direction={direction}";
                return false;
            }

            detail = $"small={smallSource.LaneIndex}@{smallOrder} middle={middleSource.LaneIndex}@{middleOrder} big={bigSource.LaneIndex}@{bigOrder} direction={direction}";
            return true;
        }

        private static string FormatLaneOrder(IReadOnlyList<LaneEndpoint> lanes)
        {
            if (lanes == null || lanes.Count == 0)
            {
                return "<none>";
            }

            List<string> values = new List<string>(lanes.Count);
            for (int i = 0; i < lanes.Count; i++)
            {
                LaneEndpoint lane = lanes[i];
                values.Add($"{lane.Endpoint}{lane.LaneIndex}|C{lane.OppositeLaneIndex}@{lane.Lateral:0.##}/{FormatEntity(lane.LaneEntity)} lanePos={DiagnosticFormat.Float3(lane.LanePosition)} cg={lane.CarriagewayAndGroup} methods=[{lane.PathMethods}] laneFlags=[{lane.LaneFlags}] carFlags=[{lane.CarFlags}] roadTypes=[{lane.RoadTypes}] trackTypes=[{lane.TrackTypes}] hasCarData={lane.HasCarLaneData} hasTrackData={lane.HasTrackLaneData} netTrack={lane.HasNetTrackLane}");
            }

            return string.Join(",", values);
        }

        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }
    }
}
