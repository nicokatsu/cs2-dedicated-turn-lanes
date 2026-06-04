using System.Collections.Generic;
using System.Linq;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficRepairDiagnosticFormat
    {
        public static string FormatMappings(IReadOnlyList<LaneMapping> mappings)
        {
            if (mappings == null || mappings.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", mappings.Select(FormatMapping));
        }

        public static string FormatSnapshot(TransitionConnectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "<none>";
            }

            int count = snapshot.Mappings?.Length ?? 0;
            return $"{snapshot.Source}/{count} node={DiagnosticFormat.Entity(snapshot.Node)} source={DiagnosticFormat.Entity(snapshot.SourceEdge)} target={DiagnosticFormat.Entity(snapshot.TargetEdge)} detail=({snapshot.Detail})";
        }

        public static string FormatFarSnapshot(FarIntersectionTrafficSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "<none>";
            }

            int sourceCount = snapshot.Entries?.Length ?? 0;
            int connectionCount = TrafficSnapshotHelpers.CountConnections(snapshot.Entries);

            return $"{snapshot.Source}/{sourceCount} sources/{connectionCount} connections node={DiagnosticFormat.Entity(snapshot.Node)} continuation={DiagnosticFormat.Entity(snapshot.ContinuationEdge)} detail=({snapshot.Detail})";
        }

        public static string FormatSnapshotMappings(IReadOnlyList<TransitionConnectionSnapshotMapping> mappings)
        {
            if (mappings == null || mappings.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", mappings.Select(mapping => $"{mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}[{mapping.Method}] unsafe={mapping.IsUnsafe} srcLat={mapping.SourceLateral:0.##} tgtLat={mapping.TargetLateral:0.##}"));
        }

        public static string FormatMapping(LaneMapping mapping)
        {
            return $"{DiagnosticFormat.Entity(mapping.SourceEdge)}:{mapping.SourceLaneIndex}->{DiagnosticFormat.Entity(mapping.TargetEdge)}:{mapping.TargetLaneIndex}[{mapping.Method}]{(mapping.IsBranch ? "*" : string.Empty)}{(mapping.HasPreservedPathMethods ? "#preserve" : string.Empty)}{(mapping.IsUnsafe ? "!unsafe" : string.Empty)}";
        }

        public static string FormatConnectorLanes(IReadOnlyList<ConnectorLane> connectors)
        {
            if (connectors == null || connectors.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", connectors.Select(connector => $"{DiagnosticFormat.Entity(connector.SourceEdge)}:{connector.SourceLaneIndex}->{DiagnosticFormat.Entity(connector.TargetEdge)}:{connector.TargetLaneIndex}[{connector.PathMethods}] flags=[{connector.LaneFlags}] trackTypes=[{connector.TrackTypes}] trackData={connector.HasTrackLaneData} netTrack={connector.HasNetTrackLane}/{DiagnosticFormat.Entity(connector.Entity)}"));
        }

        public static string FormatStringList(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "<none>";
            }

            return string.Join(" || ", values);
        }

        public static string FormatSourceLaneKeys(IEnumerable<SourceLaneKey> sourceLaneKeys)
        {
            if (sourceLaneKeys == null)
            {
                return "<none>";
            }

            string[] formatted = sourceLaneKeys
                .OrderBy(key => key.Edge.Index)
                .ThenBy(key => key.LaneIndex)
                .Select(key => $"{DiagnosticFormat.Entity(key.Edge)}:{key.LaneIndex}")
                .ToArray();
            return formatted.Length == 0 ? "<none>" : string.Join(",", formatted);
        }

        public static string FormatConnectionSet(IEnumerable<ConnectionKey> set)
        {
            return string.Join(",", set.OrderBy(item => item.SourceLaneIndex).ThenBy(item => item.TargetLaneIndex).Select(item => $"{item.SourceLaneIndex}->{item.TargetLaneIndex}"));
        }
    }
}
