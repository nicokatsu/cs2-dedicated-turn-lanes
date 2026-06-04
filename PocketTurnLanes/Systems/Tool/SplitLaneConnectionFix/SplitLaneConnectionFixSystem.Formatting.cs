using System.Collections.Generic;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }

        private static string FormatUpdateMarker(bool added, bool alreadyUpdated)
        {
            if (added)
            {
                return "added";
            }

            return alreadyUpdated ? "already" : "missing";
        }

        private static string FormatLaneOrder(IReadOnlyList<LaneEndpoint> lanes)
        {
            return TrafficLaneEndpointDiagnostics.FormatLaneOrder(lanes);
        }

        private static string FormatMappings(IReadOnlyList<LaneMapping> mappings)
        {
            return TrafficRepairDiagnosticFormat.FormatMappings(mappings);
        }

        private static string FormatSnapshot(TransitionConnectionSnapshot snapshot)
        {
            return TrafficRepairDiagnosticFormat.FormatSnapshot(snapshot);
        }

        private static string FormatFarSnapshot(FarIntersectionTrafficSnapshot snapshot)
        {
            return TrafficRepairDiagnosticFormat.FormatFarSnapshot(snapshot);
        }

        private static string FormatSnapshotMappings(IReadOnlyList<TransitionConnectionSnapshotMapping> mappings)
        {
            return TrafficRepairDiagnosticFormat.FormatSnapshotMappings(mappings);
        }

        private static string FormatMapping(LaneMapping mapping)
        {
            return TrafficRepairDiagnosticFormat.FormatMapping(mapping);
        }

        private static string FormatConnectorLanes(IReadOnlyList<ConnectorLane> connectors)
        {
            return TrafficRepairDiagnosticFormat.FormatConnectorLanes(connectors);
        }

        private static string FormatStringList(IReadOnlyList<string> values)
        {
            return TrafficRepairDiagnosticFormat.FormatStringList(values);
        }

        private static string FormatSourceLaneKeys(IEnumerable<SourceLaneKey> sourceLaneKeys)
        {
            return TrafficRepairDiagnosticFormat.FormatSourceLaneKeys(sourceLaneKeys);
        }

        private static string FormatConnectionSet(IEnumerable<ConnectionKey> set)
        {
            return TrafficRepairDiagnosticFormat.FormatConnectionSet(set);
        }
    }
}
