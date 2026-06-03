using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficLaneEndpointDiagnostics
    {
        public static string FormatLaneOrder(IReadOnlyList<LaneEndpoint> lanes)
        {
            if (lanes == null || lanes.Count == 0)
            {
                return "<none>";
            }

            string[] values = new string[lanes.Count];
            for (int i = 0; i < lanes.Count; i++)
            {
                LaneEndpoint lane = lanes[i];
                values[i] = $"{lane.Endpoint}{lane.LaneIndex}|C{lane.OppositeLaneIndex}@{lane.Lateral:0.##}/{DiagnosticFormat.Entity(lane.LaneEntity)} lanePos={DiagnosticFormat.Float3(lane.LanePosition)} cg={lane.CarriagewayAndGroup} methods=[{lane.PathMethods}] laneFlags=[{lane.LaneFlags}] carFlags=[{lane.CarFlags}] roadTypes=[{lane.RoadTypes}] trackTypes=[{lane.TrackTypes}] hasCarData={lane.HasCarLaneData} hasTrackData={lane.HasTrackLaneData} netTrack={lane.HasNetTrackLane}";
            }

            return string.Join(",", values);
        }
    }
}
