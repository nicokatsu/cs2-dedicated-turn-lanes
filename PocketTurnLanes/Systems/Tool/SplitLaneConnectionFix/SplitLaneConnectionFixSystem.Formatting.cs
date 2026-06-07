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

        private static string FormatUnifiedTrafficWriteCounts(
            Request request,
            string trafficWriteOrder,
            bool farRestoreSucceeded,
            bool centerRewriteWritten,
            bool centerRewriteWriteSucceeded,
            CenterPlan centerPlan,
            TrafficMappingPlan plan,
            TrafficLoadValidationStats loadValidationStats,
            int removedExisting,
            int preservedExisting,
            int preservedExistingForOverlay,
            int preservedUnsafeForOverlay,
            int writtenSources,
            int writtenConnections,
            int writtenRoadRepairConnections,
            int writtenTrackConnections,
            int writtenUnsafeConnections,
            int mergedMappingCount)
        {
            return $"[SplitLaneConnectionFix] Unified Traffic mapping write counts splitNode={FormatEntity(request.SplitNode)} " +
                   $"trafficWriteOrder={trafficWriteOrder} farRestoreSucceeded={farRestoreSucceeded} " +
                   $"centerRewrite=written:{centerRewriteWritten},ok:{centerRewriteWriteSucceeded},sources:{centerPlan.BySource.Count},connections:{centerPlan.PlannedConnections} " +
                   $"existing=removed:{removedExisting},preserved:{preservedExisting},overlay:{preservedExistingForOverlay},unsafeOverlay:{preservedUnsafeForOverlay} " +
                   $"written=sources:{writtenSources},connections:{writtenConnections},road:{writtenRoadRepairConnections}/{plan.RoadRepairConnections},track:{writtenTrackConnections},unsafe:{writtenUnsafeConnections} " +
                   $"preservation=trafficSnapshot:{plan.PreservationTrafficSnapshotConnections},runtime:{plan.PreservationRuntimeConnections},overlaySnapshot:{plan.PreservationOverlaySnapshotConnections},overlayRuntime:{plan.PreservationOverlayRuntimeConnections},nonRoad:{plan.PreservationNonRoadConnections},unsafe:{plan.PreservationUnsafeConnections},track:{plan.PreservationTrackConnections},skipped:{plan.PreservationSkipped},forward:{plan.ForwardPreservationConnections},reverse:{plan.ReversePreservationConnections} " +
                   $"uturn=suppressed:{plan.AuditStats.SuppressedUturnConnections},stale:{plan.StaleUturnConnections},staleSources:{plan.StaleUturnSourceKeys.Count},covered:{plan.AuditStats.UturnSourcesCoveredByPlan},emptyOverride:{plan.AuditStats.UturnSourcesCoveredByEmptyOverride},directCleanup:{plan.AuditStats.UturnSourcesLeftForDirectCleanup} " +
                   $"load={loadValidationStats.State},invalidSources:{loadValidationStats.InvalidSources},invalidConnections:{loadValidationStats.InvalidConnections},sanitized:{loadValidationStats.SanitizedConnections} " +
                   $"mergedMappingCount={mergedMappingCount} preservationSkippedReasonPresent={!string.IsNullOrEmpty(request.PreservationSkippedReason)}.";
        }

        private static string FormatBuiltUnifiedTrafficMappingPlan(Request request, TrafficMappingPlan plan)
        {
            return $"[SplitLaneConnectionFix] Built unified logical Traffic mapping plan splitNode={FormatEntity(request.SplitNode)} " +
                   $"outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} " +
                   $"sourcePlans={plan.BySource.Count} roadSources={plan.RoadRepairSourceKeys.Count} roadConnections={plan.RoadRepairConnections} " +
                   $"roadState=forward:{request.ForwardRoadState},reverse:{request.ReverseRoadState} " +
                   $"preservation=trafficSnapshot:{plan.PreservationTrafficSnapshotConnections},runtime:{plan.PreservationRuntimeConnections},overlaySnapshot:{plan.PreservationOverlaySnapshotConnections},overlayRuntime:{plan.PreservationOverlayRuntimeConnections},nonRoad:{plan.PreservationNonRoadConnections},unsafe:{plan.PreservationUnsafeConnections},track:{plan.PreservationTrackConnections},skipped:{plan.PreservationSkipped},forward:{plan.ForwardPreservationConnections},reverse:{plan.ReversePreservationConnections} " +
                   $"uturn=suppressed:{plan.AuditStats.SuppressedUturnConnections},stale:{plan.StaleUturnConnections},staleSources:{plan.StaleUturnSourceKeys.Count},covered:{plan.AuditStats.UturnSourcesCoveredByPlan},emptyOverride:{plan.AuditStats.UturnSourcesCoveredByEmptyOverride},directCleanup:{plan.AuditStats.UturnSourcesLeftForDirectCleanup} " +
                   $"runtimeNonUturnSources={plan.RuntimeNonUturnSourceKeys.Count} runtimeNonUturnSuppressionSkipped={plan.AuditStats.RuntimeNonUturnSuppressionSkipped} " +
                   $"preservationForwardMappings={request.PreservationForwardMappings?.Length ?? 0} preservationReverseMappings={request.PreservationReverseMappings?.Length ?? 0} preservationSkippedReasonPresent={!string.IsNullOrEmpty(request.PreservationSkippedReason)}.";
        }

        private static string FormatUnifiedTrafficLaneMappingWritten(
            Request request,
            int laneRefreshOwners,
            bool leftHandTraffic)
        {
            return $"[SplitLaneConnectionFix] Wrote unified Traffic lane mapping splitNode={FormatEntity(request.SplitNode)} " +
                   $"outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} " +
                   $"forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} " +
                   $"reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} " +
                   $"forwardMappings={request.Mappings?.Length ?? 0} reverseMappings={request.ReverseMappings?.Length ?? 0} " +
                   $"preservationForwardMappings={request.PreservationForwardMappings?.Length ?? 0} preservationReverseMappings={request.PreservationReverseMappings?.Length ?? 0} " +
                   $"preservationSkipped={!string.IsNullOrEmpty(request.PreservationSkippedReason)} extraLane={request.ExtraTargetLaneIndex} " +
                   $"turn={request.Turn} branchSource={request.BranchSourceLaneIndex} laneRefreshOwners={laneRefreshOwners} leftHandTraffic={leftHandTraffic}.";
        }

        private static string FormatCenterTrafficRewriteWriteCounts(
            Request request,
            CenterPlan plan,
            int removedExisting,
            int removedLegacyOffScope,
            int preservedExisting,
            int writtenSources,
            int writtenConnections,
            int writtenUnsafeConnections)
        {
            return $"[SplitLaneConnectionFix] Center Traffic rewrite write counts centerNode={FormatEntity(request.IntersectionNode)} " +
                   $"pocketEdge={FormatEntity(request.PocketEdge)} leftHandTraffic={plan.LeftHandTraffic} bigTurn={plan.BigTurn} smallTurn={plan.SmallTurn} " +
                   $"trafficWriteOrder={GetTrafficWriteOrder(request.Mode)} removedExisting={removedExisting} removedLegacyOffScope={removedLegacyOffScope} " +
                   $"preservedExisting={preservedExisting} writtenSources={writtenSources} expectedSources={plan.BySource.Count} " +
                   $"writtenConnections={writtenConnections} plannedConnections={plan.PlannedConnections} writtenUnsafeConnections={writtenUnsafeConnections} " +
                   $"straightConnectionsSafe={plan.StraightConnectionsWrittenSafe} straightUnsafeCleared={plan.StraightUnsafeCleared} " +
                   $"smallTurnClearedFromStraightLane={plan.SmallTurnConnectionsClearedFromStraightLane} roadBicycle={plan.BicycleConnectionsWrittenWithRoad} " +
                   $"runtimePreserved={plan.PreservedRuntimeConnections} snapshotPreserved={plan.PreservedSnapshotConnections} " +
                   $"preservedUturn={plan.PreservedUturnConnections} preservedNonRoad={plan.PreservedNonRoadConnections} preservedUnsafe={plan.PreservedUnsafeConnections} " +
                   $"preservationSkipped={plan.PreservationSkipped} diagnosticsCount={plan.Diagnostics.Count} legacyOffScopeSources={plan.LegacyOffScopeSourceKeys.Count}.";
        }
    }
}
