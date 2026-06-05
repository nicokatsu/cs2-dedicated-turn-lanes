using System.Collections.Generic;
using System.Linq;
using Game.Common;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
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

            CenterPlan centerPlan = request.CenterPlan ?? BuildCenterPlan(request);
            bool centerRewriteWritten = false;
            bool centerRewriteWriteSucceeded = true;
            if (centerPlan.BySource.Count > 0 || centerPlan.LegacyOffScopeSourceKeys.Count > 0)
            {
                try
                {
                    centerRewriteWriteSucceeded = TryWriteCenterMappings(
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

            List<LaneMapping> roadMappings = TrafficMappingPlanMerge.CreateRoadRepairMappings(
                request.Mappings,
                request.ReverseMappings);
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

            if (!TrySanitizeTrafficMappingDictionaryForLoad(
                    request.SplitNode,
                    plan.BySource,
                    plan.RoadRepairSourceKeys,
                    plan.StaleUturnSourceKeys,
                    "outer-unified",
                    failOnRoadRepairInvalid: true,
                    out TrafficLoadValidationStats outerLoadValidationStats,
                    out string outerLoadValidationDetail))
            {
                if (centerRewriteWritten)
                {
                    MarkCenterForLaneRebuild(request.IntersectionNode);
                }

                Mod.LogEssential($"[SplitLaneConnectionFix] Unified Traffic mapping write blocked before mutation because core road repair would not survive Traffic load validation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trafficWriteOrder={trafficWriteOrder} {outerLoadValidationDetail} forwardMappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}].");
                return false;
            }

            if (outerLoadValidationStats.InvalidConnections > 0 ||
                outerLoadValidationStats.InvalidSources > 0 ||
                outerLoadValidationStats.SanitizedConnections > 0)
            {
                Mod.LogEssential($"[SplitLaneConnectionFix] Unified Traffic mapping load validation adjusted write data before mutation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trafficWriteOrder={trafficWriteOrder} {outerLoadValidationDetail}.");
            }

            if (plan.BySource.Count == 0)
            {
                if (centerRewriteWritten)
                {
                    MarkCenterForLaneRebuild(request.IntersectionNode);
                }

                Mod.LogEssential($"[SplitLaneConnectionFix] Unified Traffic mapping write skipped after load validation removed all sources splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trafficWriteOrder={trafficWriteOrder} {outerLoadValidationDetail}.");
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
                        ? TrafficPathMethods.RestrictPreservedTrafficPathMethodToEndpoints(mapping.Method, sourceEndpoint, targetEndpoint)
                        : TrafficPathMethods.RestrictTrafficPathMethodToEndpoints(
                            TrafficPathMethods.SanitizeTrafficPathMethod(mapping.Method),
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
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Unified Traffic mapping write counts splitNode={FormatEntity(request.SplitNode)} trafficWriteOrder={trafficWriteOrder} farRestoreSucceeded={farRestoreSucceeded} farRestoreDetail=({farRestoreDetail}) centerRewriteWritten={centerRewriteWritten} centerRewriteWriteSucceeded={centerRewriteWriteSucceeded} centerRewriteSources={centerPlan.BySource.Count} centerRewriteConnections={centerPlan.PlannedConnections} centerStraightUnsafeCleared={centerPlan.StraightUnsafeCleared} centerSmallTurnClearedFromStraightLane={centerPlan.SmallTurnConnectionsClearedFromStraightLane} centerRoadBicycle={centerPlan.BicycleConnectionsWrittenWithRoad} centerRuntimePreserved={centerPlan.PreservedRuntimeConnections} centerSnapshotPreserved={centerPlan.PreservedSnapshotConnections} centerPreservedUturn={centerPlan.PreservedUturnConnections} centerPreservedNonRoad={centerPlan.PreservedNonRoadConnections} centerPreservedUnsafe={centerPlan.PreservedUnsafeConnections} centerPreservationSkipped={centerPlan.PreservationSkipped} centerTrafficPlanAudit=({TrafficMappingPlanAudit.FormatStats(centerPlan.AuditStats)}) removedExisting={removedExisting} preservedExisting={m_KeptTrafficConnections.Count} preservedExistingForOverlay={preservedExistingForOverlay} preservedUnsafeForOverlay={preservedUnsafeForOverlay} writtenSources={writtenSources} writtenConnections={writtenConnections} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} roadRepairConnections={plan.RoadRepairConnections} writtenRoadRepairConnections={writtenRoadRepairConnections} preservationTrafficSnapshotConnections={plan.PreservationTrafficSnapshotConnections} preservationRuntimeConnections={plan.PreservationRuntimeConnections} preservationOverlaySnapshotConnections={plan.PreservationOverlaySnapshotConnections} preservationOverlayRuntimeConnections={plan.PreservationOverlayRuntimeConnections} preservationNonRoadConnections={plan.PreservationNonRoadConnections} preservationUnsafeConnections={plan.PreservationUnsafeConnections} preservationSuppressedUturnConnections={plan.AuditStats.SuppressedUturnConnections} preservationTrackConnections={plan.PreservationTrackConnections} preservationTrackOnlyTargets={plan.PreservationTrackOnlyTargets} preservationSharedTrackConnections={plan.PreservationSharedTrackConnections} preservationSkipped={plan.PreservationSkipped} forwardPreservationConnections={plan.ForwardPreservationConnections} reversePreservationConnections={plan.ReversePreservationConnections} writtenTrackConnections={writtenTrackConnections} staleUturnConnections={plan.StaleUturnConnections} staleUturnSources={plan.StaleUturnSourceKeys.Count} uturnSourcesCoveredByPlan={plan.AuditStats.UturnSourcesCoveredByPlan} uturnSourcesCoveredByEmptyOverride={plan.AuditStats.UturnSourcesCoveredByEmptyOverride} uturnSourcesLeftForDirectCleanup={plan.AuditStats.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={plan.AuditStats.RuntimeNonUturnSuppressionSkipped} writtenUnsafeConnections={writtenUnsafeConnections} trafficPlanAudit=({TrafficMappingPlanAudit.FormatStats(plan.AuditStats)}) trafficLoadValidation=({outerLoadValidationDetail}) forwardMappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] mergedMappings={FormatMappings(mergedMappings)} preservationSkippedReason={request.PreservationSkippedReason}.");
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
                TrafficMappingPlanMerge.AddOrMergeFinal(plan.BySource, mapping);
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
            plan.AuditStats = TrafficMappingPlanAudit.AuditAndNormalize(
                plan.BySource,
                TrafficMappingPlanAudit.OuterSuppressSplitPairUturns,
                plan.RoadRepairSourceKeys,
                plan.PreservationSourceKeys,
                plan.StaleUturnSourceKeys,
                plan.RuntimeNonUturnSourceKeys);

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Built unified logical Traffic mapping plan splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} stageOrder=outerPreservationSnapshot,roadRepairOrPreservation,runtimeOuterPreservation,collectUturnSources,snapshotOuterPreservation,finalAudit sourcePlans={plan.BySource.Count} roadSources={plan.RoadRepairSourceKeys.Count} roadConnections={plan.RoadRepairConnections} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} preservationTrafficSnapshotConnections={plan.PreservationTrafficSnapshotConnections} preservationRuntimeConnections={plan.PreservationRuntimeConnections} preservationOverlaySnapshotConnections={plan.PreservationOverlaySnapshotConnections} preservationOverlayRuntimeConnections={plan.PreservationOverlayRuntimeConnections} preservationNonRoadConnections={plan.PreservationNonRoadConnections} preservationUnsafeConnections={plan.PreservationUnsafeConnections} preservationSuppressedUturnConnections={plan.AuditStats.SuppressedUturnConnections} preservationTrackConnections={plan.PreservationTrackConnections} preservationTrackOnlyTargets={plan.PreservationTrackOnlyTargets} preservationSharedTrackConnections={plan.PreservationSharedTrackConnections} preservationSkipped={plan.PreservationSkipped} forwardPreservationConnections={plan.ForwardPreservationConnections} reversePreservationConnections={plan.ReversePreservationConnections} staleUturnConnections={plan.StaleUturnConnections} staleUturnSources={plan.StaleUturnSourceKeys.Count} runtimeNonUturnSources={plan.RuntimeNonUturnSourceKeys.Count} uturnCovered={plan.AuditStats.UturnSourcesCoveredByPlan} uturnEmptyOverrides={plan.AuditStats.UturnSourcesCoveredByEmptyOverride} uturnDirectCleanupFallback={plan.AuditStats.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={plan.AuditStats.RuntimeNonUturnSuppressionSkipped} trafficPlanAudit=({TrafficMappingPlanAudit.FormatStats(plan.AuditStats)}) preservationMappings=forward[{FormatMappings(request.PreservationForwardMappings)}] reverse[{FormatMappings(request.PreservationReverseMappings)}] preservationSkippedReason={request.PreservationSkippedReason}.");
            return plan;
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

                PathMethod preservedMethod = TrafficPathMethods.SanitizePreservedTrafficPathMethod(generated.Method);
                if (preservedMethod == 0)
                {
                    continue;
                }

                LaneMapping mapping = TrafficMappingPlanPreservation.CreatePreservationMapping(
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

                mapping.Method = TrafficPathMethods.RestrictPreservedTrafficPathMethodToEndpoints(
                    mapping.Method,
                    sourceEndpoint,
                    targetEndpoint);
                if (mapping.Method == 0)
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                TrafficMappingPlanMerge.AddOrMergeFinal(plan.BySource, mapping);
                TrafficMappingPlanPreservation.CountTrackStats(plan, mapping.Method, targetEndpoint);
                copied++;
                if (mapping.IsUnsafe)
                {
                    unsafePreserved++;
                }
            }

            return copied;
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
