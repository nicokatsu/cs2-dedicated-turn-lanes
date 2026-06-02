using System.Collections.Generic;
using System.Linq;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool WriteTrafficMappings(TrafficApi trafficApi, Request request)
        {
            string trafficWriteOrder = GetTrafficWriteOrder(request.Mode);
            bool farRestoreSucceeded = true;
            string farRestoreDetail = FormatFarSnapshot(request.FarIntersectionSnapshot);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic mapping write order starting trafficWriteOrder={trafficWriteOrder} centerNode={FormatEntity(request.IntersectionNode)} farNode={FormatEntity(request.FarIntersectionNode)} splitNode={FormatEntity(request.SplitNode)} pocketEdge={FormatEntity(request.PocketEdge)} outerEdge={FormatEntity(request.OuterEdge)} leftHandTraffic={m_CityConfigurationSystem.leftHandTraffic} farSnapshot={farRestoreDetail}.");
            if (request.Mode == RepairMode.BalancedOppositeTarget)
            {
                try
                {
                    farRestoreSucceeded = TryRestoreFarIntersectionTrafficSnapshot(
                        trafficApi,
                        request,
                        out farRestoreDetail);
                    if (!farRestoreSucceeded)
                    {
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic restore failed or partially skipped; continuing center and outer repair splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(request.FarIntersectionNode)} outerEdge={FormatEntity(request.OuterEdge)} detail={farRestoreDetail}.");
                    }
                }
                catch (System.Exception ex)
                {
                    farRestoreSucceeded = false;
                    farRestoreDetail = ex.ToString();
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic restore threw; continuing center and outer repair splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(request.FarIntersectionNode)} outerEdge={FormatEntity(request.OuterEdge)} exception={ex}.");
                }
            }

            CenterRewritePlan centerPlan = BuildCenterRewritePlan(request);
            bool centerRewriteWritten = false;
            bool centerRewriteWriteSucceeded = true;
            if (centerPlan.BySource.Count > 0 || centerPlan.LegacyOffScopeSourceKeys.Count > 0)
            {
                try
                {
                    centerRewriteWriteSucceeded = TryWriteCenterRewriteMappings(
                        trafficApi,
                        request,
                        centerPlan,
                        out centerRewriteWritten);
                    if (!centerRewriteWriteSucceeded)
                    {
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} splitNode={FormatEntity(request.SplitNode)} pocketEdge={FormatEntity(request.PocketEdge)} plannedSources={centerPlan.BySource.Count} plannedConnections={centerPlan.PlannedConnections}; continuing outer split-node Traffic repair.");
                    }
                }
                catch (System.Exception ex)
                {
                    centerRewriteWriteSucceeded = false;
                    centerRewriteWritten = false;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} splitNode={FormatEntity(request.SplitNode)} pocketEdge={FormatEntity(request.PocketEdge)} exception={ex}; continuing outer split-node Traffic repair.");
                }
            }
            else
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Center Traffic rewrite skipped without writable sources centerNode={FormatEntity(request.IntersectionNode)} splitNode={FormatEntity(request.SplitNode)} pocketEdge={FormatEntity(request.PocketEdge)} diagnostics={FormatStringList(centerPlan.Diagnostics)}; continuing outer split-node Traffic repair.");
            }

            List<LaneMapping> roadMappings = GetRoadFixMappings(request);
            List<LaneMapping> validRoadMappings = new List<LaneMapping>(roadMappings.Count);
            for (int i = 0; i < roadMappings.Count; i++)
            {
                LaneMapping mapping = roadMappings[i];
                bool sourceFound = TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out _);
                bool targetFound = TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out _);
                if (!sourceFound || !targetFound)
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic mapping preflight failed splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} sourceFound={sourceFound} targetFound={targetFound}.");
                    if (centerRewriteWritten)
                    {
                        MarkCenterForLaneRebuild(request.IntersectionNode);
                    }

                    return false;
                }

                mapping.Method = GetRoadFixMethod(mapping.Method);
                mapping.IsPreservationOnly = false;
                mapping.IsUnsafe = false;
                validRoadMappings.Add(mapping);
            }

            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, request.SplitNode);
            if (modifiedBuffer == null)
            {
                if (centerRewriteWritten)
                {
                    MarkCenterForLaneRebuild(request.IntersectionNode);
                }

                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Outer Traffic mapping write failed before plan splitNode={FormatEntity(request.SplitNode)} centerRewriteWritten={centerRewriteWritten} centerRewriteWriteSucceeded={centerRewriteWriteSucceeded} reason=modifiedBufferUnavailable.");
                return false;
            }

            TrafficMappingPlan plan = BuildUnifiedTrafficMappingPlan(trafficApi, request, validRoadMappings, modifiedBuffer);
            if (plan.BySource.Count == 0)
            {
                if (centerRewriteWritten)
                {
                    MarkCenterForLaneRebuild(request.IntersectionNode);
                }

                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Unified Traffic mapping plan has no writable sources splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trafficWriteOrder={trafficWriteOrder} farRestoreSucceeded={farRestoreSucceeded} farRestoreDetail=({farRestoreDetail}) centerRewriteWritten={centerRewriteWritten} centerRewriteWriteSucceeded={centerRewriteWriteSucceeded} centerRewriteSources={centerPlan.BySource.Count} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} roadMappings={FormatMappings(validRoadMappings)} preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] preservationSkippedReason={request.PreservationSkippedReason}.");
                return false;
            }

            m_KeptTrafficConnections.Clear();

            int removedExisting = 0;
            int preservedExistingForOverlay = 0;
            int preservedUnsafeForOverlay = 0;
            int originalLength = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < originalLength; i++)
            {
                object existing = trafficApi.GetBufferItem(modifiedBuffer, i);
                Entity edge = trafficApi.GetModifiedConnectionEdge(existing);
                SourceLaneKey existingKey = new SourceLaneKey(
                    edge,
                    trafficApi.GetModifiedConnectionLaneIndex(existing));
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                bool sourceWillBeRewritten = plan.BySource.ContainsKey(existingKey);
                if (sourceWillBeRewritten)
                {
                    if (!plan.RoadRepairSourceKeys.Contains(existingKey) &&
                        !plan.PreservationSourceKeys.Contains(existingKey))
                    {
                        preservedExistingForOverlay += CopyExistingGeneratedConnectionsForTrafficPreservation(
                            trafficApi,
                            request,
                            modifiedEntity,
                            plan,
                            ref preservedUnsafeForOverlay);
                    }

                    removedExisting++;
                    if (modifiedEntity != Entity.Null && EntityManager.Exists(modifiedEntity))
                    {
                        AddMarkerIfMissing<Deleted>(modifiedEntity);
                    }

                    continue;
                }

                m_KeptTrafficConnections.Add(existing);
            }

            trafficApi.ClearBuffer(modifiedBuffer);
            for (int i = 0; i < m_KeptTrafficConnections.Count; i++)
            {
                trafficApi.AddBufferElement(modifiedBuffer, m_KeptTrafficConnections[i]);
            }

            List<LaneMapping> mergedMappings = plan.BySource.Values.SelectMany(byTarget => byTarget.Values).ToList();
            int writtenSources = 0;
            int writtenConnections = 0;
            int writtenRoadRepairConnections = 0;
            int writtenTrackConnections = 0;
            int writtenUnsafeConnections = 0;
            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> pair in plan.BySource)
            {
                if (!TryFindMappingEndpoint(request, pair.Key.Edge, pair.Key.LaneIndex, source: true, out LaneEndpoint sourceEndpoint))
                {
                    continue;
                }

                Entity modifiedConnectionEntity = CreateTrafficModifiedConnectionEntity(
                    trafficApi,
                    request.SplitNode,
                    out object generatedBuffer);

                foreach (LaneMapping mapping in pair.Value.Values)
                {
                    if (!TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                    {
                        continue;
                    }

                    PathMethod method = mapping.HasPreservedPathMethods
                        ? RestrictPreservedTrafficPathMethodToEndpoints(mapping.Method, sourceEndpoint, targetEndpoint)
                        : RestrictTrafficPathMethodToEndpoints(
                            SanitizeTrafficPathMethod(mapping.Method),
                            sourceEndpoint,
                            targetEndpoint);

                    if (method == 0)
                    {
                        continue;
                    }

                    trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                        mapping.SourceEdge,
                        mapping.TargetEdge,
                        mapping.SourceLaneIndex,
                        mapping.TargetLaneIndex,
                        mapping.HasTrafficMaps
                            ? mapping.TrafficLanePositionMap
                            : new float3x2(
                                sourceEndpoint.LanePosition,
                                targetEndpoint.LanePosition),
                        mapping.HasTrafficMaps
                            ? mapping.TrafficCarriagewayAndGroupIndexMap
                            : new int4(
                                sourceEndpoint.CarriagewayAndGroup,
                                targetEndpoint.CarriagewayAndGroup),
                        method,
                        mapping.IsUnsafe));
                    writtenConnections++;
                    if (!mapping.IsPreservationOnly)
                    {
                        writtenRoadRepairConnections++;
                    }

                    if ((method & PathMethod.Track) != 0)
                    {
                        writtenTrackConnections++;
                    }

                    if (mapping.IsUnsafe)
                    {
                        writtenUnsafeConnections++;
                    }
                }

                trafficApi.AddBufferElement(modifiedBuffer, trafficApi.CreateModifiedLaneConnection(
                    pair.Key.LaneIndex,
                    sourceEndpoint.CarriagewayAndGroup,
                    sourceEndpoint.LanePosition,
                    pair.Key.Edge,
                    modifiedConnectionEntity));
                writtenSources++;
            }

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, request.SplitNode);
            if (centerRewriteWritten)
            {
                MarkCenterForLaneRebuild(request.IntersectionNode);
            }

            MarkForLaneRebuild(request);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Unified Traffic mapping write counts splitNode={FormatEntity(request.SplitNode)} trafficWriteOrder={trafficWriteOrder} farRestoreSucceeded={farRestoreSucceeded} farRestoreDetail=({farRestoreDetail}) centerRewriteWritten={centerRewriteWritten} centerRewriteWriteSucceeded={centerRewriteWriteSucceeded} centerRewriteSources={centerPlan.BySource.Count} centerRewriteConnections={centerPlan.PlannedConnections} centerStraightUnsafeCleared={centerPlan.StraightUnsafeCleared} centerSmallTurnClearedFromStraightLane={centerPlan.SmallTurnConnectionsClearedFromStraightLane} centerRoadBicycle={centerPlan.BicycleConnectionsWrittenWithRoad} centerRuntimePreserved={centerPlan.PreservedRuntimeConnections} centerSnapshotPreserved={centerPlan.PreservedSnapshotConnections} centerPreservedUturn={centerPlan.PreservedUturnConnections} centerPreservedNonRoad={centerPlan.PreservedNonRoadConnections} centerPreservedUnsafe={centerPlan.PreservedUnsafeConnections} centerPreservationSkipped={centerPlan.PreservationSkipped} centerTrafficPlanAudit=({FormatTrafficPlanAuditStats(centerPlan.AuditStats)}) removedExisting={removedExisting} preservedExisting={m_KeptTrafficConnections.Count} preservedExistingForOverlay={preservedExistingForOverlay} preservedUnsafeForOverlay={preservedUnsafeForOverlay} writtenSources={writtenSources} writtenConnections={writtenConnections} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} roadRepairConnections={plan.RoadRepairConnections} writtenRoadRepairConnections={writtenRoadRepairConnections} preservationTrafficSnapshotConnections={plan.PreservationTrafficSnapshotConnections} preservationRuntimeConnections={plan.PreservationRuntimeConnections} preservationOverlaySnapshotConnections={plan.PreservationOverlaySnapshotConnections} preservationOverlayRuntimeConnections={plan.PreservationOverlayRuntimeConnections} preservationNonRoadConnections={plan.PreservationNonRoadConnections} preservationUnsafeConnections={plan.PreservationUnsafeConnections} preservationSuppressedUturnConnections={plan.AuditStats.SuppressedUturnConnections} preservationTrackConnections={plan.PreservationTrackConnections} preservationTrackOnlyTargets={plan.PreservationTrackOnlyTargets} preservationSharedTrackConnections={plan.PreservationSharedTrackConnections} preservationSkipped={plan.PreservationSkipped} forwardPreservationConnections={plan.ForwardPreservationConnections} reversePreservationConnections={plan.ReversePreservationConnections} writtenTrackConnections={writtenTrackConnections} staleUturnConnections={plan.StaleUturnConnections} staleUturnSources={plan.StaleUturnSourceKeys.Count} uturnSourcesCoveredByPlan={plan.AuditStats.UturnSourcesCoveredByPlan} uturnSourcesCoveredByEmptyOverride={plan.AuditStats.UturnSourcesCoveredByEmptyOverride} uturnSourcesLeftForDirectCleanup={plan.AuditStats.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={plan.AuditStats.RuntimeNonUturnSuppressionSkipped} writtenUnsafeConnections={writtenUnsafeConnections} trafficPlanAudit=({FormatTrafficPlanAuditStats(plan.AuditStats)}) forwardMappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] mergedMappings={FormatMappings(mergedMappings)} preservationSkippedReason={request.PreservationSkippedReason}.");
            return writtenSources > 0;
        }

        private Entity CreateTrafficModifiedConnectionEntity(
            TrafficApi trafficApi,
            Entity ownerNode,
            out object generatedBuffer)
        {
            Entity modifiedConnectionEntity = EntityManager.CreateEntity();
            trafficApi.AddDataOwner(EntityManager, modifiedConnectionEntity, ownerNode);
            trafficApi.AddFakePrefabRef(EntityManager, modifiedConnectionEntity);
            generatedBuffer = trafficApi.AddGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity);
            return modifiedConnectionEntity;
        }

        private TrafficMappingPlan BuildUnifiedTrafficMappingPlan(
            TrafficApi trafficApi,
            Request request,
            IReadOnlyList<LaneMapping> validRoadMappings,
            object modifiedBuffer)
        {
            TrafficMappingPlan plan = new TrafficMappingPlan();
            for (int i = 0; i < validRoadMappings.Count; i++)
            {
                LaneMapping mapping = validRoadMappings[i];
                SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                plan.RoadRepairSourceKeys.Add(sourceKey);
                AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                plan.RoadRepairConnections++;
            }

            RoadDirectionPlan forwardDirection = GetRoadDirectionPlan(request, RoadDirection.Forward);
            if (forwardDirection.State == RoadDirectionState.Skipped)
            {
                AddSkippedRoadDirectionPreservationToPlan(
                    trafficApi,
                    request,
                    modifiedBuffer,
                    plan,
                    forwardDirection);
            }

            RoadDirectionPlan reverseDirection = GetRoadDirectionPlan(request, RoadDirection.Reverse);
            if (reverseDirection.State == RoadDirectionState.Skipped)
            {
                AddSkippedRoadDirectionPreservationToPlan(
                    trafficApi,
                    request,
                    modifiedBuffer,
                    plan,
                    reverseDirection);
            }

            AddRuntimeOuterPreservationMappingsToPlan(request, plan);
            CollectLogicalUturnSuppressionSourcesForPlan(request, plan);
            CopyTrafficSnapshotOuterPreservationOverlayToPlan(trafficApi, request, modifiedBuffer, plan);
            plan.AuditStats = AuditAndNormalizeTrafficMappingPlan(
                plan.BySource,
                OuterTrafficPlanAuditPolicy,
                plan.RoadRepairSourceKeys,
                plan.PreservationSourceKeys,
                plan.StaleUturnSourceKeys,
                plan.RuntimeNonUturnSourceKeys);

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Built unified logical Traffic mapping plan splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} stageOrder=outerPreservationSnapshot,roadRepairOrPreservation,runtimeOuterPreservation,collectUturnSources,snapshotOuterPreservation,finalAudit sourcePlans={plan.BySource.Count} roadSources={plan.RoadRepairSourceKeys.Count} roadConnections={plan.RoadRepairConnections} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} preservationTrafficSnapshotConnections={plan.PreservationTrafficSnapshotConnections} preservationRuntimeConnections={plan.PreservationRuntimeConnections} preservationOverlaySnapshotConnections={plan.PreservationOverlaySnapshotConnections} preservationOverlayRuntimeConnections={plan.PreservationOverlayRuntimeConnections} preservationNonRoadConnections={plan.PreservationNonRoadConnections} preservationUnsafeConnections={plan.PreservationUnsafeConnections} preservationSuppressedUturnConnections={plan.AuditStats.SuppressedUturnConnections} preservationTrackConnections={plan.PreservationTrackConnections} preservationTrackOnlyTargets={plan.PreservationTrackOnlyTargets} preservationSharedTrackConnections={plan.PreservationSharedTrackConnections} preservationSkipped={plan.PreservationSkipped} forwardPreservationConnections={plan.ForwardPreservationConnections} reversePreservationConnections={plan.ReversePreservationConnections} staleUturnConnections={plan.StaleUturnConnections} staleUturnSources={plan.StaleUturnSourceKeys.Count} runtimeNonUturnSources={plan.RuntimeNonUturnSourceKeys.Count} uturnCovered={plan.AuditStats.UturnSourcesCoveredByPlan} uturnEmptyOverrides={plan.AuditStats.UturnSourcesCoveredByEmptyOverride} uturnDirectCleanupFallback={plan.AuditStats.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={plan.AuditStats.RuntimeNonUturnSuppressionSkipped} trafficPlanAudit=({FormatTrafficPlanAuditStats(plan.AuditStats)}) preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] preservationSkippedReason={request.PreservationSkippedReason}.");
            return plan;
        }

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

                PathMethod method = RestrictPreservedTrafficPathMethodToEndpoints(
                    GetLayerPreservationPathMethod(mapping.Method, preserveUturn: sameEdgeUturn),
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
                AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                plan.PreservationSourceKeys.Add(sourceKey);
                plan.PreservationRuntimeConnections++;
                plan.PreservationOverlayRuntimeConnections++;
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
                        ? SanitizePreservedTrafficPathMethod(generated.Method)
                        : plan.RoadRepairSourceKeys.Contains(sourceKey)
                        ? GetLayerPreservationPathMethod(generated.Method, preserveUturn: false)
                        : SanitizePreservedTrafficPathMethod(generated.Method);
                    PathMethod method = RestrictPreservedTrafficPathMethodToEndpoints(
                        preservedMethod,
                        sourceEndpoint,
                        targetEndpoint);
                    if (method == 0)
                    {
                        continue;
                    }

                    LaneMapping mapping = CreateLaneMappingFromTrafficSnapshot(generated, method);
                    AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                    plan.PreservationSourceKeys.Add(sourceKey);
                    plan.PreservationOverlaySnapshotConnections++;
                    overlaySources.Add(sourceKey);
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

            int snapshotConnections = plan.PreservationOverlaySnapshotConnections - beforeSnapshot;
            int skipped = plan.PreservationSkipped - beforeSkipped;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic snapshot outer preservation overlay scan complete splitNode={FormatEntity(request.SplitNode)} sourceKeys={FormatSourceLaneKeys(sourceKeys)} overlaySources={FormatSourceLaneKeys(overlaySources)} snapshotConnections={snapshotConnections} skipped={skipped} sameEdgeUturnCandidates={sameEdgeUturnCandidates} finalUturnPolicy=outerAuditSuppress readStats=({FormatTrafficSnapshotReadStats(readStats)}).");
        }

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

                PathMethod method = RestrictPreservedTrafficPathMethodToEndpoints(
                    SanitizePreservedTrafficPathMethod(connector.PathMethods),
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

                    PathMethod method = RestrictPreservedTrafficPathMethodToEndpoints(
                        SanitizePreservedTrafficPathMethod(generated.Method),
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

        private void CollectLogicalUturnSuppressionSourcesForPlan(Request request, TrafficMappingPlan plan)
        {
            if (!EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return;
            }

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            plan.StaleUturnConnections = m_StaleConnectorLanes.Count;
            for (int i = 0; i < m_StaleConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_StaleConnectorLanes[i];
                plan.StaleUturnSourceKeys.Add(new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex));
            }

            if (plan.StaleUturnSourceKeys.Count == 0)
            {
                return;
            }

            CollectSplitNodeConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_ConnectorLanes);
            foreach (SourceLaneKey sourceKey in plan.StaleUturnSourceKeys)
            {
                if (CountRuntimeNonUturnConnectionsForSource(sourceKey, m_ConnectorLanes) > 0)
                {
                    plan.RuntimeNonUturnSourceKeys.Add(sourceKey);
                    continue;
                }

                if (TryFindMappingEndpoint(request, sourceKey.Edge, sourceKey.LaneIndex, source: true, out _))
                {
                    EnsureTrafficPlanSource(plan.BySource, sourceKey);
                }
            }
        }

        private static int CountRuntimeNonUturnConnectionsForSource(SourceLaneKey sourceKey, IReadOnlyList<ConnectorLane> connectors)
        {
            int count = 0;
            if (connectors == null)
            {
                return count;
            }

            for (int i = 0; i < connectors.Count; i++)
            {
                ConnectorLane connector = connectors[i];
                if (connector.SourceEdge == sourceKey.Edge &&
                    connector.SourceLaneIndex == sourceKey.LaneIndex &&
                    connector.TargetEdge != sourceKey.Edge)
                {
                    count++;
                }
            }

            return count;
        }

        private static void EnsureTrafficPlanSource(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            SourceLaneKey sourceKey)
        {
            if (!bySource.ContainsKey(sourceKey))
            {
                bySource.Add(sourceKey, new Dictionary<TargetLaneKey, LaneMapping>());
            }
        }

        private int CopyExistingGeneratedConnectionsForTrafficPreservation(
            TrafficApi trafficApi,
            Request request,
            Entity modifiedEntity,
            TrafficMappingPlan plan,
            ref int unsafePreserved)
        {
            List<TrafficGeneratedSnapshot> generatedSnapshots = new List<TrafficGeneratedSnapshot>(4);
            if (!TryReadTrafficGeneratedSnapshots(trafficApi, modifiedEntity, generatedSnapshots))
            {
                return 0;
            }

            int copied = 0;
            for (int i = 0; i < generatedSnapshots.Count; i++)
            {
                TrafficGeneratedSnapshot generated = generatedSnapshots[i];
                if (generated.TargetEdge == generated.SourceEdge)
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                PathMethod preservedMethod = SanitizePreservedTrafficPathMethod(generated.Method);
                if (preservedMethod == 0)
                {
                    continue;
                }

                LaneMapping mapping = CreateLaneMappingFromTrafficSnapshot(
                    generated,
                    preservedMethod);

                SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                if (!plan.BySource.ContainsKey(sourceKey))
                {
                    continue;
                }

                if (!TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                    !TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                mapping.Method = RestrictPreservedTrafficPathMethodToEndpoints(
                    mapping.Method,
                    sourceEndpoint,
                    targetEndpoint);
                if (mapping.Method == 0)
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                CountPreservationTrackStats(plan, mapping.Method, targetEndpoint);
                copied++;
                if (mapping.IsUnsafe)
                {
                    unsafePreserved++;
                }
            }

            return copied;
        }

        private static void CountPreservationTrackStats(
            TrafficMappingPlan plan,
            PathMethod method,
            LaneEndpoint targetEndpoint)
        {
            if ((method & PathMethod.Track) == 0)
            {
                return;
            }

            plan.PreservationTrackConnections++;
            if (IsTrackOnlyEndpoint(targetEndpoint))
            {
                plan.PreservationTrackOnlyTargets++;
            }

            if ((method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
            {
                plan.PreservationSharedTrackConnections++;
            }
        }

        private static List<LaneMapping> GetRoadFixMappings(Request request)
        {
            List<LaneMapping> allMappings = new List<LaneMapping>(
                (request.Mappings?.Length ?? 0) +
                (request.ReverseMappings?.Length ?? 0));
            if (request.Mappings != null)
            {
                AddRoadFixMappings(request.Mappings, allMappings);
            }

            if (request.ReverseMappings != null)
            {
                AddRoadFixMappings(request.ReverseMappings, allMappings);
            }

            return allMappings;
        }

        private static void AddRoadFixMappings(LaneMapping[] mappings, List<LaneMapping> output)
        {
            if (mappings == null)
            {
                return;
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                mapping.Method = GetRoadFixMethod(mapping.Method);
                mapping.IsPreservationOnly = false;
                mapping.IsUnsafe = false;
                output.Add(mapping);
            }
        }

        private static PathMethod GetRoadFixMethod(PathMethod method)
        {
            return PathMethod.Road | (method & PathMethod.Bicycle);
        }

        private static string GetReverseRoadDirectionLabel(RepairMode mode)
        {
            if (mode == RepairMode.BalancedOppositeTarget)
            {
                return "balanced-reverse";
            }

            if (mode == RepairMode.ShortEdgeTransition)
            {
                return "short-edge-transition-reverse";
            }

            return "standard-reverse";
        }
    }
}
