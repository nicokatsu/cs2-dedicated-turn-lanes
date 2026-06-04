using System.Collections.Generic;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void AddRuntimeOuterPreservationMappingsToPlan(Request request, TrafficMappingPlan plan)
        {
            AddRuntimeOuterPreservationMappingsToPlan(request, plan, request.PreservationForwardMappings, "forward");
            AddRuntimeOuterPreservationMappingsToPlan(request, plan, request.PreservationReverseMappings, "reverse");
        }

        private void AddRuntimeOuterPreservationMappingsToPlan(
            Request request,
            TrafficMappingPlan plan,
            IReadOnlyList<LaneMapping> mappings,
            string direction)
        {
            if (mappings == null || mappings.Count == 0)
            {
                return;
            }

            int beforeRuntime = plan.PreservationRuntimeConnections;
            int beforeSkipped = plan.PreservationSkipped;
            HashSet<SourceLaneKey> runtimeSources = new HashSet<SourceLaneKey>();
            for (int i = 0; i < mappings.Count; i++)
            {
                LaneMapping mapping = mappings[i];
                bool sameEdgeUturn = mapping.SourceEdge == mapping.TargetEdge;

                if (!TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                    !TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                PathMethod method = TrafficPathMethods.RestrictPreservedTrafficPathMethodToEndpoints(
                    TrafficPathMethods.GetLayerPreservationPathMethod(mapping.Method, preserveUturn: sameEdgeUturn),
                    sourceEndpoint,
                    targetEndpoint);
                if (method == 0)
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                mapping.Method = method;
                mapping.IsPreservationOnly = true;
                mapping.HasPreservedPathMethods = true;
                TrafficMappingPlanMerge.AddOrMergeFinal(plan.BySource, mapping);
                SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                plan.PreservationSourceKeys.Add(sourceKey);
                plan.PreservationRuntimeConnections++;
                plan.PreservationOverlayRuntimeConnections++;
                runtimeSources.Add(sourceKey);
                TrafficMappingPlanPreservation.CountPreservedConnectionStats(
                    plan,
                    method,
                    mapping.IsUnsafe,
                    targetEndpoint);
            }

            int added = plan.PreservationRuntimeConnections - beforeRuntime;
            int skipped = plan.PreservationSkipped - beforeSkipped;
            if (direction == "forward")
            {
                plan.ForwardPreservationConnections += added;
            }
            else
            {
                plan.ReversePreservationConnections += added;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Added runtime outer preservation mappings to unified plan splitNode={FormatEntity(request.SplitNode)} direction={direction} runtimeSources={FormatSourceLaneKeys(runtimeSources)} runtimeConnections={added} skipped={skipped} mappings={FormatMappings(mappings)}.");
        }

        private void CopyTrafficSnapshotOuterPreservationOverlayToPlan(
            TrafficApi trafficApi,
            Request request,
            object modifiedBuffer,
            TrafficMappingPlan plan)
        {
            if (trafficApi == null ||
                modifiedBuffer == null ||
                plan.BySource.Count == 0)
            {
                return;
            }

            HashSet<SourceLaneKey> sourceKeys = new HashSet<SourceLaneKey>(plan.BySource.Keys);
            List<TrafficSourceSnapshot> sourceSnapshots = new List<TrafficSourceSnapshot>(sourceKeys.Count);
            TrafficSnapshotReadStats readStats = default;
            ReadTrafficSourceSnapshotsFromBuffer(
                trafficApi,
                modifiedBuffer,
                source => sourceKeys.Contains(new SourceLaneKey(source.SourceEdge, source.SourceLaneIndex)),
                null,
                sourceSnapshots,
                ref readStats);
            plan.PreservationSkipped += readStats.MissingGeneratedBuffers;

            HashSet<SourceLaneKey> overlaySources = new HashSet<SourceLaneKey>();
            int beforeSnapshot = plan.PreservationOverlaySnapshotConnections;
            int beforeSkipped = plan.PreservationSkipped;
            int sameEdgeUturnCandidates = 0;
            for (int i = 0; i < sourceSnapshots.Count; i++)
            {
                TrafficGeneratedSnapshot[] connections =
                    sourceSnapshots[i].Connections ?? System.Array.Empty<TrafficGeneratedSnapshot>();
                for (int generatedIndex = 0; generatedIndex < connections.Length; generatedIndex++)
                {
                    TrafficGeneratedSnapshot generated = connections[generatedIndex];
                    SourceLaneKey sourceKey = new SourceLaneKey(generated.SourceEdge, generated.SourceLaneIndex);
                    if (!sourceKeys.Contains(sourceKey))
                    {
                        continue;
                    }

                    if (!TryFindMappingEndpoint(request, generated.SourceEdge, generated.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                        !TryFindMappingEndpoint(request, generated.TargetEdge, generated.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                    {
                        plan.PreservationSkipped++;
                        continue;
                    }

                    bool sameEdgeUturn = generated.SourceEdge == generated.TargetEdge;
                    if (sameEdgeUturn)
                    {
                        sameEdgeUturnCandidates++;
                    }

                    PathMethod preservedMethod = sameEdgeUturn
                        ? TrafficPathMethods.SanitizePreservedTrafficPathMethod(generated.Method)
                        : plan.RoadRepairSourceKeys.Contains(sourceKey)
                        ? TrafficPathMethods.GetLayerPreservationPathMethod(generated.Method, preserveUturn: false)
                        : TrafficPathMethods.SanitizePreservedTrafficPathMethod(generated.Method);
                    PathMethod method = TrafficPathMethods.RestrictPreservedTrafficPathMethodToEndpoints(
                        preservedMethod,
                        sourceEndpoint,
                        targetEndpoint);
                    if (method == 0)
                    {
                        continue;
                    }

                    LaneMapping mapping = TrafficMappingPlanPreservation.CreatePreservationMapping(generated, method);
                    TrafficMappingPlanMerge.AddOrMergeFinal(plan.BySource, mapping);
                    plan.PreservationSourceKeys.Add(sourceKey);
                    plan.PreservationOverlaySnapshotConnections++;
                    overlaySources.Add(sourceKey);
                    TrafficMappingPlanPreservation.CountPreservedConnectionStats(
                        plan,
                        method,
                        mapping.IsUnsafe,
                        targetEndpoint);
                }
            }

            int snapshotConnections = plan.PreservationOverlaySnapshotConnections - beforeSnapshot;
            int skipped = plan.PreservationSkipped - beforeSkipped;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic snapshot outer preservation overlay scan complete splitNode={FormatEntity(request.SplitNode)} sourceKeys={FormatSourceLaneKeys(sourceKeys)} overlaySources={FormatSourceLaneKeys(overlaySources)} snapshotConnections={snapshotConnections} skipped={skipped} sameEdgeUturnCandidates={sameEdgeUturnCandidates} finalUturnPolicy=outerAuditSuppress readStats=({TrafficSnapshotHelpers.FormatReadStats(readStats)}).");
        }
    }
}
