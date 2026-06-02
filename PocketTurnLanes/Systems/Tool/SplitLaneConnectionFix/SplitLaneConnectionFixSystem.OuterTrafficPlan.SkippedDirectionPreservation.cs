using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void AddSkippedRoadDirectionPreservationToPlan(
            TrafficApi trafficApi,
            Request request,
            object modifiedBuffer,
            TrafficMappingPlan plan,
            RoadDirectionPlan direction)
        {
            AddSkippedRoadDirectionPreservationToPlan(
                trafficApi,
                request,
                modifiedBuffer,
                plan,
                direction.SourceEdge,
                direction.TargetEdge,
                direction.SourceLanes,
                direction.Name,
                direction.SkipReason);
        }

        private void AddSkippedRoadDirectionPreservationToPlan(
            TrafficApi trafficApi,
            Request request,
            object modifiedBuffer,
            TrafficMappingPlan plan,
            Entity sourceEdge,
            Entity targetEdge,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            string direction,
            string skipReason)
        {
            HashSet<SourceLaneKey> sourceKeys = new HashSet<SourceLaneKey>();
            if (sourceLanes != null)
            {
                for (int i = 0; i < sourceLanes.Count; i++)
                {
                    sourceKeys.Add(new SourceLaneKey(sourceEdge, sourceLanes[i].LaneIndex));
                }
            }

            if (sourceKeys.Count == 0)
            {
                plan.PreservationSkipped++;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Skipped road preservation has no source endpoints splitNode={FormatEntity(request.SplitNode)} direction={direction} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} reason={skipReason}.");
                return;
            }

            HashSet<SourceLaneKey> trafficSnapshotSources = new HashSet<SourceLaneKey>();
            int beforeTraffic = plan.PreservationTrafficSnapshotConnections;
            int beforeRuntime = plan.PreservationRuntimeConnections;
            int beforeSkipped = plan.PreservationSkipped;

            CopyTrafficSnapshotRoadPreservationToPlan(
                trafficApi,
                request,
                modifiedBuffer,
                plan,
                sourceEdge,
                targetEdge,
                sourceKeys,
                trafficSnapshotSources,
                direction);

            HashSet<SourceLaneKey> runtimeSources = new HashSet<SourceLaneKey>();
            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, m_ExistingConnectorLanes);
            for (int i = 0; i < m_ExistingConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ExistingConnectorLanes[i];
                SourceLaneKey sourceKey = new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex);
                if (!sourceKeys.Contains(sourceKey) ||
                    trafficSnapshotSources.Contains(sourceKey) ||
                    plan.RoadRepairSourceKeys.Contains(sourceKey) ||
                    connector.TargetEdge == connector.SourceEdge)
                {
                    continue;
                }

                if (!TryFindMappingEndpoint(request, connector.SourceEdge, connector.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                    !TryFindMappingEndpoint(request, connector.TargetEdge, connector.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                PathMethod method = TrafficPathMethods.RestrictPreservedTrafficPathMethodToEndpoints(
                    TrafficPathMethods.SanitizePreservedTrafficPathMethod(connector.PathMethods),
                    sourceEndpoint,
                    targetEndpoint);
                if (method == 0)
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                LaneMapping mapping = new LaneMapping
                {
                    SourceEdge = connector.SourceEdge,
                    TargetEdge = connector.TargetEdge,
                    SourceLaneIndex = connector.SourceLaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    Method = method,
                    IsBranch = false,
                    IsPreservationOnly = true,
                    IsUnsafe = (connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods,
                    HasPreservedPathMethods = true
                };
                AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                plan.PreservationSourceKeys.Add(sourceKey);
                plan.PreservationRuntimeConnections++;
                runtimeSources.Add(sourceKey);
                if ((method & ~PathMethod.Road) != 0)
                {
                    plan.PreservationNonRoadConnections++;
                }

                if (mapping.IsUnsafe)
                {
                    plan.PreservationUnsafeConnections++;
                }

                CountPreservationTrackStats(plan, method, targetEndpoint);
            }

            int missingSources = 0;
            foreach (SourceLaneKey sourceKey in sourceKeys)
            {
                if (!trafficSnapshotSources.Contains(sourceKey) &&
                    !runtimeSources.Contains(sourceKey) &&
                    !plan.RoadRepairSourceKeys.Contains(sourceKey))
                {
                    missingSources++;
                }
            }

            if (missingSources > 0)
            {
                plan.PreservationSkipped += missingSources;
            }

            int trafficConnections = plan.PreservationTrafficSnapshotConnections - beforeTraffic;
            int runtimeConnections = plan.PreservationRuntimeConnections - beforeRuntime;
            int skipped = plan.PreservationSkipped - beforeSkipped;
            if (direction == "forward")
            {
                plan.ForwardPreservationConnections += trafficConnections + runtimeConnections;
            }
            else
            {
                plan.ReversePreservationConnections += trafficConnections + runtimeConnections;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Added skipped road preservation to unified plan splitNode={FormatEntity(request.SplitNode)} direction={direction} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} skipReason={skipReason} sourceKeys={FormatSourceLaneKeys(sourceKeys)} trafficSnapshotSources={FormatSourceLaneKeys(trafficSnapshotSources)} runtimeFallbackSources={FormatSourceLaneKeys(runtimeSources)} trafficSnapshotConnections={trafficConnections} runtimeConnections={runtimeConnections} skipped={skipped} runtimeConnectors={FormatConnectorLanes(m_ExistingConnectorLanes)}.");
        }

        private void CopyTrafficSnapshotRoadPreservationToPlan(
            TrafficApi trafficApi,
            Request request,
            object modifiedBuffer,
            TrafficMappingPlan plan,
            Entity sourceEdge,
            Entity targetEdge,
            HashSet<SourceLaneKey> sourceKeys,
            HashSet<SourceLaneKey> trafficSnapshotSources,
            string direction)
        {
            if (modifiedBuffer == null)
            {
                plan.PreservationSkipped += sourceKeys.Count;
                return;
            }

            List<TrafficSourceSnapshot> sourceSnapshots = new List<TrafficSourceSnapshot>(sourceKeys.Count);
            TrafficSnapshotReadStats readStats = default;
            ReadTrafficSourceSnapshotsFromBuffer(
                trafficApi,
                modifiedBuffer,
                source =>
                {
                    SourceLaneKey modifiedKey = new SourceLaneKey(source.SourceEdge, source.SourceLaneIndex);
                    return sourceKeys.Contains(modifiedKey) && !plan.RoadRepairSourceKeys.Contains(modifiedKey);
                },
                null,
                sourceSnapshots,
                ref readStats);
            plan.PreservationSkipped += readStats.MissingGeneratedBuffers;

            for (int i = 0; i < sourceSnapshots.Count; i++)
            {
                TrafficGeneratedSnapshot[] connections =
                    sourceSnapshots[i].Connections ?? System.Array.Empty<TrafficGeneratedSnapshot>();
                for (int generatedIndex = 0; generatedIndex < connections.Length; generatedIndex++)
                {
                    TrafficGeneratedSnapshot generated = connections[generatedIndex];
                    SourceLaneKey sourceKey = new SourceLaneKey(generated.SourceEdge, generated.SourceLaneIndex);
                    if (generated.SourceEdge != sourceEdge ||
                        (generated.TargetEdge != targetEdge &&
                         generated.TargetEdge != sourceEdge) ||
                        !sourceKeys.Contains(sourceKey))
                    {
                        continue;
                    }

                    if (!TryFindMappingEndpoint(request, generated.SourceEdge, generated.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                        !TryFindMappingEndpoint(request, generated.TargetEdge, generated.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                    {
                        plan.PreservationSkipped++;
                        continue;
                    }

                    PathMethod method = TrafficPathMethods.RestrictPreservedTrafficPathMethodToEndpoints(
                        TrafficPathMethods.SanitizePreservedTrafficPathMethod(generated.Method),
                        sourceEndpoint,
                        targetEndpoint);
                    if (method == 0)
                    {
                        plan.PreservationSkipped++;
                        continue;
                    }

                    LaneMapping mapping = CreateLaneMappingFromTrafficSnapshot(generated, method);
                    AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                    plan.PreservationSourceKeys.Add(sourceKey);
                    plan.PreservationTrafficSnapshotConnections++;
                    trafficSnapshotSources.Add(sourceKey);
                    if ((method & ~PathMethod.Road) != 0)
                    {
                        plan.PreservationNonRoadConnections++;
                    }

                    if (mapping.IsUnsafe)
                    {
                        plan.PreservationUnsafeConnections++;
                    }

                    CountPreservationTrackStats(plan, method, targetEndpoint);
                }
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic snapshot preservation scan complete splitNode={FormatEntity(request.SplitNode)} direction={direction} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} sourceKeys={FormatSourceLaneKeys(sourceKeys)} trafficSnapshotSources={FormatSourceLaneKeys(trafficSnapshotSources)}.");
        }
    }
}
