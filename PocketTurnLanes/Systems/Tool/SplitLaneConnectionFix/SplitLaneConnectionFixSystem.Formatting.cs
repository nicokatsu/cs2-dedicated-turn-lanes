using System.Collections.Generic;
using System.Linq;
using PocketTurnLanes.Tool;
using Unity.Entities;
using Unity.Mathematics;
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
            if (lanes == null || lanes.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", lanes.Select(lane => $"{lane.Endpoint}{lane.LaneIndex}|C{lane.OppositeLaneIndex}@{lane.Lateral:0.##}/{FormatEntity(lane.LaneEntity)} lanePos={FormatFloat3(lane.LanePosition)} cg={lane.CarriagewayAndGroup} methods=[{lane.PathMethods}] laneFlags=[{lane.LaneFlags}] carFlags=[{lane.CarFlags}] roadTypes=[{lane.RoadTypes}] trackTypes=[{lane.TrackTypes}] hasCarData={lane.HasCarLaneData} hasTrackData={lane.HasTrackLaneData} netTrack={lane.HasNetTrackLane}"));
        }

        private static string FormatFloat3(float3 value)
        {
            return DiagnosticFormat.Float3(value);
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
            int connectionCount = CountTrafficSnapshotConnections(snapshot.Entries);

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

        private void AddCenterApproachSkip(
            CenterRewritePlan plan,
            Entity sourceEdge,
            string reason,
            IReadOnlyList<ConnectorLane> approachConnectors,
            string sourceClass)
        {
            plan.ApproachesSkipped++;
            plan.Diagnostics.Add($"centerRewriteSkipped={reason} sourceEdge={FormatEntity(sourceEdge)} connectors={FormatConnectorLanes(approachConnectors)} sourceClass=({sourceClass})");
        }

        private void LogCenterRewritePlan(Request request, CenterRewritePlan plan)
        {
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Built center Traffic rewrite plan centerNode={FormatEntity(request.IntersectionNode)} splitNode={FormatEntity(request.SplitNode)} pocketEdge={FormatEntity(request.PocketEdge)} leftHandTraffic={plan.LeftHandTraffic} bigTurn={plan.BigTurn} smallTurn={plan.SmallTurn} trafficWriteOrder={GetTrafficWriteOrder(request.Mode)} scope=pocketApproachOnly connectorCount={plan.CenterConnectors} approachesScanned={plan.ApproachesScanned} offScopeApproaches={plan.OffScopeApproaches} approachesWithBigTurn={plan.BigTurnApproaches} approachesRewritten={plan.ApproachesRewritten} approachesSkipped={plan.ApproachesSkipped} sourcePlans={plan.BySource.Count} plannedConnections={plan.PlannedConnections} straightConnectionsSafe={plan.StraightConnectionsWrittenSafe} straightUnsafeCleared={plan.StraightUnsafeCleared} smallTurnClearedFromStraightLane={plan.SmallTurnConnectionsClearedFromStraightLane} roadBicycle={plan.BicycleConnectionsWrittenWithRoad} runtimePreserved={plan.PreservedRuntimeConnections} snapshotPreserved={plan.PreservedSnapshotConnections} preservedUturn={plan.PreservedUturnConnections} preservedNonRoad={plan.PreservedNonRoadConnections} preservedUnsafe={plan.PreservedUnsafeConnections} preservationSkipped={plan.PreservationSkipped} legacyOffScopeSourceKeys={FormatSourceLaneKeys(plan.LegacyOffScopeSourceKeys)} diagnostics={FormatStringList(plan.Diagnostics)}.");
        }

        private static string FormatCenterSourceMovementSummaries(
            Dictionary<int, CenterLaneMovementSummary> summaries)
        {
            if (summaries == null || summaries.Count == 0)
            {
                return "<none>";
            }

            List<CenterLaneMovementSummary> ordered = new List<CenterLaneMovementSummary>(summaries.Values);
            ordered.Sort((a, b) => a.SourceEndpoint.Lateral.CompareTo(b.SourceEndpoint.Lateral));
            List<string> values = new List<string>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                CenterLaneMovementSummary summary = ordered[i];
                values.Add($"{summary.SourceEndpoint.LaneIndex}@{summary.SourceEndpoint.Lateral:0.##}:small={summary.SmallTurn.Count},straight={summary.Straight.Count},big={summary.BigTurn.Count},uturn={summary.Uturn.Count},other={summary.Other.Count}");
            }

            return string.Join("|", values);
        }

        private static string FormatCenterTurnDiagnostics(
            IReadOnlyList<LaneEndpoint> targets,
            IReadOnlyList<int> leftCounts,
            IReadOnlyList<int> rightCounts,
            IReadOnlyList<int> straightCounts,
            IReadOnlyList<CenterTurnCandidate> candidates)
        {
            string targetSummary = string.Join("|", Enumerable.Range(0, targets.Count)
                .Select(i => $"target{targets[i].LaneIndex}/center{targets[i].OppositeLaneIndex}:L{leftCounts[i]} R{rightCounts[i]} S{straightCounts[i]}"));
            string candidateSummary = candidates == null || candidates.Count == 0
                ? "none"
                : string.Join(",", candidates.Select(candidate => $"{candidate.SourceLaneIndex}->{candidate.TargetLaneIndex}/{candidate.Turn}/{FormatEntity(candidate.TargetEdge)}/{candidate.Flags}/{FormatEntity(candidate.LaneEntity)}"));
            return $"targets=[{targetSummary}] connectors=[{candidateSummary}]";
        }
    }
}
