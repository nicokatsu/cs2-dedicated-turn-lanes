using System.Collections.Generic;
using System.Linq;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
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
