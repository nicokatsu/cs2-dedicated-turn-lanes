using System.Collections.Generic;
using System.Linq;
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
            if (mappings == null || mappings.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", mappings.Select(FormatMapping));
        }

        private static string FormatSnapshot(TransitionConnectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "<none>";
            }

            int count = snapshot.Mappings?.Length ?? 0;
            return $"{snapshot.Source}/{count} node={FormatEntity(snapshot.Node)} source={FormatEntity(snapshot.SourceEdge)} target={FormatEntity(snapshot.TargetEdge)} detail=({snapshot.Detail})";
        }

        private static string FormatFarSnapshot(FarIntersectionTrafficSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "<none>";
            }

            int sourceCount = snapshot.Entries?.Length ?? 0;
            int connectionCount = TrafficSnapshotHelpers.CountConnections(snapshot.Entries);

            return $"{snapshot.Source}/{sourceCount} sources/{connectionCount} connections node={FormatEntity(snapshot.Node)} continuation={FormatEntity(snapshot.ContinuationEdge)} detail=({snapshot.Detail})";
        }

        private static string FormatSnapshotMappings(IReadOnlyList<TransitionConnectionSnapshotMapping> mappings)
        {
            if (mappings == null || mappings.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", mappings.Select(mapping => $"{mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}[{mapping.Method}] unsafe={mapping.IsUnsafe} srcLat={mapping.SourceLateral:0.##} tgtLat={mapping.TargetLateral:0.##}"));
        }

        private static string FormatMapping(LaneMapping mapping)
        {
            return $"{FormatEntity(mapping.SourceEdge)}:{mapping.SourceLaneIndex}->{FormatEntity(mapping.TargetEdge)}:{mapping.TargetLaneIndex}[{mapping.Method}]{(mapping.IsBranch ? "*" : string.Empty)}{(mapping.HasPreservedPathMethods ? "#preserve" : string.Empty)}{(mapping.IsUnsafe ? "!unsafe" : string.Empty)}";
        }

        private static string FormatConnectorLanes(IReadOnlyList<ConnectorLane> connectors)
        {
            if (connectors == null || connectors.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", connectors.Select(connector => $"{FormatEntity(connector.SourceEdge)}:{connector.SourceLaneIndex}->{FormatEntity(connector.TargetEdge)}:{connector.TargetLaneIndex}[{connector.PathMethods}] flags=[{connector.LaneFlags}] trackTypes=[{connector.TrackTypes}] trackData={connector.HasTrackLaneData} netTrack={connector.HasNetTrackLane}/{FormatEntity(connector.Entity)}"));
        }

        private static string FormatStringList(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "<none>";
            }

            return string.Join(" || ", values);
        }

        private static string FormatSourceLaneKeys(IEnumerable<SourceLaneKey> sourceLaneKeys)
        {
            if (sourceLaneKeys == null)
            {
                return "<none>";
            }

            string[] formatted = sourceLaneKeys
                .OrderBy(key => key.Edge.Index)
                .ThenBy(key => key.LaneIndex)
                .Select(key => $"{FormatEntity(key.Edge)}:{key.LaneIndex}")
                .ToArray();
            return formatted.Length == 0 ? "<none>" : string.Join(",", formatted);
        }

        private static string FormatConnectionSet(IEnumerable<ConnectionKey> set)
        {
            return string.Join(",", set.OrderBy(item => item.SourceLaneIndex).ThenBy(item => item.TargetLaneIndex).Select(item => $"{item.SourceLaneIndex}->{item.TargetLaneIndex}"));
        }
    }
}
