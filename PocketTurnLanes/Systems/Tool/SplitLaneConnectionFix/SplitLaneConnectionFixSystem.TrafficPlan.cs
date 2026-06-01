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
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic mapping write order starting trafficWriteOrder=centerFirstOuterSecond centerNode={FormatEntity(request.IntersectionNode)} splitNode={FormatEntity(request.SplitNode)} pocketEdge={FormatEntity(request.PocketEdge)} outerEdge={FormatEntity(request.OuterEdge)} leftHandTraffic={m_CityConfigurationSystem.leftHandTraffic}.");
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
                mapping.IsTrackPreservation = false;
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

                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Unified Traffic mapping plan has no writable sources splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} centerRewriteWritten={centerRewriteWritten} centerRewriteWriteSucceeded={centerRewriteWriteSucceeded} centerRewriteSources={centerPlan.BySource.Count} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} roadMappings={FormatMappings(validRoadMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}].");
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
                            plan.BySource,
                            ref plan.TrackSkipped,
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

                Entity modifiedConnectionEntity = EntityManager.CreateEntity();
                trafficApi.AddDataOwner(EntityManager, modifiedConnectionEntity, request.SplitNode);
                trafficApi.AddFakePrefabRef(EntityManager, modifiedConnectionEntity);
                object generatedBuffer = trafficApi.AddGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity);

                foreach (LaneMapping mapping in pair.Value.Values)
                {
                    if (!TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                    {
                        continue;
                    }

                    PathMethod method = RestrictTrafficPathMethodToEndpoints(
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
                    if (!mapping.IsTrackPreservation)
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
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Unified Traffic mapping write counts splitNode={FormatEntity(request.SplitNode)} trafficWriteOrder=centerFirstOuterSecond centerRewriteWritten={centerRewriteWritten} centerRewriteWriteSucceeded={centerRewriteWriteSucceeded} centerRewriteSources={centerPlan.BySource.Count} centerRewriteConnections={centerPlan.PlannedConnections} centerStraightUnsafeCleared={centerPlan.StraightUnsafeCleared} centerSmallTurnClearedFromStraightLane={centerPlan.SmallTurnConnectionsClearedFromStraightLane} centerRoadBicycle={centerPlan.BicycleConnectionsWrittenWithRoad} centerRuntimePreserved={centerPlan.PreservedRuntimeConnections} centerSnapshotPreserved={centerPlan.PreservedSnapshotConnections} centerPreservedUturn={centerPlan.PreservedUturnConnections} centerPreservedNonRoad={centerPlan.PreservedNonRoadConnections} centerPreservedUnsafe={centerPlan.PreservedUnsafeConnections} centerPreservationSkipped={centerPlan.PreservationSkipped} removedExisting={removedExisting} preservedExisting={m_KeptTrafficConnections.Count} preservedExistingForOverlay={preservedExistingForOverlay} preservedUnsafeForOverlay={preservedUnsafeForOverlay} writtenSources={writtenSources} writtenConnections={writtenConnections} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} roadRepairConnections={plan.RoadRepairConnections} writtenRoadRepairConnections={writtenRoadRepairConnections} preservationTrafficSnapshotConnections={plan.PreservationTrafficSnapshotConnections} preservationRuntimeConnections={plan.PreservationRuntimeConnections} preservationSkipped={plan.PreservationSkipped} forwardPreservationConnections={plan.ForwardPreservationConnections} reversePreservationConnections={plan.ReversePreservationConnections} trackConnectionsPlanned={plan.TrackConnections} writtenTrackConnections={writtenTrackConnections} trackOnlyTargets={plan.TrackOnlyTargets} sharedTrackConnections={plan.SharedTrackConnections} trackSkipped={plan.TrackSkipped} staleUturnConnections={plan.StaleUturnConnections} staleUturnSources={plan.StaleUturnSourceKeys.Count} uturnSourcesCoveredByPlan={plan.UturnSourcesCoveredByPlan} uturnSourcesCoveredByEmptyOverride={plan.UturnSourcesCoveredByEmptyOverride} uturnSourcesLeftForDirectCleanup={plan.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={plan.RuntimeNonUturnSuppressionSkipped} writtenUnsafeConnections={writtenUnsafeConnections} finalTrackInUnifiedWrite=True forwardMappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] mergedMappings={FormatMappings(mergedMappings)} trackSkippedReason={request.TrackSkippedReason}.");
            return writtenSources > 0;
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

            AddLogicalUturnSuppressionToPlan(request, plan);

            List<LaneMapping> trackMappings = GetTrackFixMappings(request);
            for (int i = 0; i < trackMappings.Count; i++)
            {
                LaneMapping mapping = trackMappings[i];
                if (!TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                    !TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                {
                    plan.TrackSkipped++;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Unified track mapping stage skipped splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} trackSkippedReason=unifiedTrackEndpointMissing.");
                    continue;
                }

                PathMethod method = RestrictTrafficPathMethodToEndpoints(
                    mapping.Method,
                    sourceEndpoint,
                    targetEndpoint);
                if ((method & PathMethod.Track) == 0)
                {
                    plan.TrackSkipped++;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Unified track mapping stage skipped splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} trackSkippedReason=unifiedTrackMethodMissing restricted=[{method}].");
                    continue;
                }

                mapping.Method = method;
                mapping.IsTrackPreservation = true;
                AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                plan.TrackConnections++;
                if (IsTrackOnlyEndpoint(targetEndpoint))
                {
                    plan.TrackOnlyTargets++;
                }

                if ((method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                {
                    plan.SharedTrackConnections++;
                }
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Built unified logical Traffic mapping plan splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} stageOrder=trackSnapshot,roadRepairOrPreservation,uturnDelete,trackRestore sourcePlans={plan.BySource.Count} roadSources={plan.RoadRepairSourceKeys.Count} roadConnections={plan.RoadRepairConnections} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} preservationTrafficSnapshotConnections={plan.PreservationTrafficSnapshotConnections} preservationRuntimeConnections={plan.PreservationRuntimeConnections} preservationSkipped={plan.PreservationSkipped} forwardPreservationConnections={plan.ForwardPreservationConnections} reversePreservationConnections={plan.ReversePreservationConnections} trackConnections={plan.TrackConnections} trackSkipped={plan.TrackSkipped} staleUturnConnections={plan.StaleUturnConnections} staleUturnSources={plan.StaleUturnSourceKeys.Count} uturnCovered={plan.UturnSourcesCoveredByPlan} uturnEmptyOverrides={plan.UturnSourcesCoveredByEmptyOverride} uturnDirectCleanupFallback={plan.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={plan.RuntimeNonUturnSuppressionSkipped}.");
            return plan;
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

                PathMethod method = RestrictTrafficPathMethodToEndpoints(
                    SanitizeTrafficPathMethod(connector.PathMethods),
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
                    IsTrackPreservation = true,
                    IsUnsafe = (connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods
                };
                AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                plan.PreservationSourceKeys.Add(sourceKey);
                plan.PreservationRuntimeConnections++;
                runtimeSources.Add(sourceKey);
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

            int length = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < length; i++)
            {
                object modified = trafficApi.GetBufferItem(modifiedBuffer, i);
                SourceLaneKey modifiedKey = new SourceLaneKey(
                    trafficApi.GetModifiedConnectionEdge(modified),
                    trafficApi.GetModifiedConnectionLaneIndex(modified));
                if (!sourceKeys.Contains(modifiedKey) ||
                    plan.RoadRepairSourceKeys.Contains(modifiedKey))
                {
                    continue;
                }

                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(modified);
                if (modifiedEntity == Entity.Null ||
                    !EntityManager.Exists(modifiedEntity) ||
                    !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
                {
                    plan.PreservationSkipped++;
                    continue;
                }

                object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
                int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
                for (int generatedIndex = 0; generatedIndex < generatedLength; generatedIndex++)
                {
                    object generated = trafficApi.GetBufferItem(generatedBuffer, generatedIndex);
                    Entity generatedSourceEdge = trafficApi.GetGeneratedConnectionSource(generated);
                    Entity generatedTargetEdge = trafficApi.GetGeneratedConnectionTarget(generated);
                    int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
                    int sourceLaneIndex = laneIndexMap.x & 0xff;
                    int targetLaneIndex = laneIndexMap.y & 0xff;
                    SourceLaneKey sourceKey = new SourceLaneKey(generatedSourceEdge, sourceLaneIndex);
                    if (generatedSourceEdge != sourceEdge ||
                        generatedTargetEdge != targetEdge ||
                        generatedTargetEdge == generatedSourceEdge ||
                        !sourceKeys.Contains(sourceKey))
                    {
                        continue;
                    }

                    if (!TryFindMappingEndpoint(request, generatedSourceEdge, sourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                        !TryFindMappingEndpoint(request, generatedTargetEdge, targetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                    {
                        plan.PreservationSkipped++;
                        continue;
                    }

                    PathMethod method = RestrictTrafficPathMethodToEndpoints(
                        SanitizeTrafficPathMethod(trafficApi.GetGeneratedConnectionMethod(generated)),
                        sourceEndpoint,
                        targetEndpoint);
                    if (method == 0)
                    {
                        plan.PreservationSkipped++;
                        continue;
                    }

                    LaneMapping mapping = new LaneMapping
                    {
                        SourceEdge = generatedSourceEdge,
                        TargetEdge = generatedTargetEdge,
                        SourceLaneIndex = sourceLaneIndex,
                        TargetLaneIndex = targetLaneIndex,
                        TrafficLanePositionMap = trafficApi.GetGeneratedConnectionLanePositionMap(generated),
                        TrafficCarriagewayAndGroupIndexMap = trafficApi.GetGeneratedConnectionCarriagewayAndGroupIndexMap(generated),
                        Method = method,
                        IsBranch = false,
                        IsTrackPreservation = true,
                        IsUnsafe = trafficApi.GetGeneratedConnectionUnsafe(generated),
                        HasTrafficMaps = true
                    };
                    AddOrMergeFinalTrafficMapping(plan.BySource, mapping);
                    plan.PreservationSourceKeys.Add(sourceKey);
                    plan.PreservationTrafficSnapshotConnections++;
                    trafficSnapshotSources.Add(sourceKey);
                }
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic snapshot preservation scan complete splitNode={FormatEntity(request.SplitNode)} direction={direction} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} sourceKeys={FormatSourceLaneKeys(sourceKeys)} trafficSnapshotSources={FormatSourceLaneKeys(trafficSnapshotSources)}.");
        }

        private void AddLogicalUturnSuppressionToPlan(Request request, TrafficMappingPlan plan)
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
                if (plan.BySource.ContainsKey(sourceKey))
                {
                    plan.UturnSourcesCoveredByPlan++;
                    continue;
                }

                if (CountRuntimeNonUturnConnectionsForSource(sourceKey, m_ConnectorLanes) > 0)
                {
                    plan.RuntimeNonUturnSuppressionSkipped++;
                    plan.UturnSourcesLeftForDirectCleanup++;
                    continue;
                }

                if (!TryFindMappingEndpoint(request, sourceKey.Edge, sourceKey.LaneIndex, source: true, out _))
                {
                    plan.UturnSourcesLeftForDirectCleanup++;
                    continue;
                }

                EnsureTrafficPlanSource(plan.BySource, sourceKey);
                plan.UturnSourcesCoveredByPlan++;
                plan.UturnSourcesCoveredByEmptyOverride++;
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
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            ref int skipped,
            ref int unsafePreserved)
        {
            if (modifiedEntity == Entity.Null ||
                !EntityManager.Exists(modifiedEntity) ||
                !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
            {
                return 0;
            }

            int copied = 0;
            object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
            int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
            for (int i = 0; i < generatedLength; i++)
            {
                object generated = trafficApi.GetBufferItem(generatedBuffer, i);
                Entity sourceEdge = trafficApi.GetGeneratedConnectionSource(generated);
                Entity targetEdge = trafficApi.GetGeneratedConnectionTarget(generated);
                int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
                if (targetEdge == sourceEdge)
                {
                    skipped++;
                    continue;
                }

                LaneMapping mapping = new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = laneIndexMap.x & 0xff,
                    TargetLaneIndex = laneIndexMap.y & 0xff,
                    TrafficLanePositionMap = trafficApi.GetGeneratedConnectionLanePositionMap(generated),
                    TrafficCarriagewayAndGroupIndexMap = trafficApi.GetGeneratedConnectionCarriagewayAndGroupIndexMap(generated),
                    Method = SanitizeTrafficPathMethod(trafficApi.GetGeneratedConnectionMethod(generated)),
                    IsBranch = false,
                    IsTrackPreservation = true,
                    IsUnsafe = trafficApi.GetGeneratedConnectionUnsafe(generated),
                    HasTrafficMaps = true
                };

                SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                if (!bySource.ContainsKey(sourceKey))
                {
                    continue;
                }

                if (!TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                    !TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                {
                    skipped++;
                    continue;
                }

                mapping.Method = RestrictTrafficPathMethodToEndpoints(
                    mapping.Method,
                    sourceEndpoint,
                    targetEndpoint);
                if (mapping.Method == 0)
                {
                    skipped++;
                    continue;
                }

                AddOrMergeFinalTrafficMapping(bySource, mapping);
                copied++;
                if (mapping.IsUnsafe)
                {
                    unsafePreserved++;
                }
            }

            return copied;
        }

        private static void AddOrMergeFinalTrafficMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping)
        {
            SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
            TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
            if (!bySource.TryGetValue(sourceKey, out Dictionary<TargetLaneKey, LaneMapping> byTarget))
            {
                byTarget = new Dictionary<TargetLaneKey, LaneMapping>();
                bySource.Add(sourceKey, byTarget);
            }

            if (byTarget.TryGetValue(targetKey, out LaneMapping existing))
            {
                bool preserveUnsafe = existing.IsTrackPreservation && mapping.IsTrackPreservation;
                existing.Method = SanitizeTrafficPathMethod(existing.Method | mapping.Method);
                existing.IsBranch |= mapping.IsBranch;
                existing.IsTrackPreservation &= mapping.IsTrackPreservation;
                existing.IsUnsafe = preserveUnsafe && (existing.IsUnsafe || mapping.IsUnsafe);
                if (!existing.HasTrafficMaps && mapping.HasTrafficMaps)
                {
                    existing.TrafficLanePositionMap = mapping.TrafficLanePositionMap;
                    existing.TrafficCarriagewayAndGroupIndexMap = mapping.TrafficCarriagewayAndGroupIndexMap;
                    existing.HasTrafficMaps = true;
                }

                byTarget[targetKey] = existing;
                return;
            }

            mapping.Method = SanitizeTrafficPathMethod(mapping.Method);
            byTarget.Add(targetKey, mapping);
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

        private static bool HasTrackPreservationMappings(Request request)
        {
            return (request.TrackForwardMappings != null && request.TrackForwardMappings.Length > 0) ||
                   (request.TrackReverseMappings != null && request.TrackReverseMappings.Length > 0);
        }

        private static List<LaneMapping> GetTrackFixMappings(Request request)
        {
            List<LaneMapping> mappings = new List<LaneMapping>(
                (request.TrackForwardMappings?.Length ?? 0) +
                (request.TrackReverseMappings?.Length ?? 0));
            AddTrackFixMappings(request.TrackForwardMappings, mappings);
            AddTrackFixMappings(request.TrackReverseMappings, mappings);
            return mappings;
        }

        private static void AddTrackFixMappings(LaneMapping[] mappings, List<LaneMapping> output)
        {
            if (mappings == null)
            {
                return;
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                mapping.Method = GetTrackFixMethod(mapping.Method);
                mapping.IsTrackPreservation = true;
                if (mapping.Method != 0)
                {
                    output.Add(mapping);
                }
            }
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
                mapping.IsTrackPreservation = false;
                mapping.IsUnsafe = false;
                output.Add(mapping);
            }
        }

        private static PathMethod GetRoadFixMethod(PathMethod method)
        {
            return PathMethod.Road | (method & PathMethod.Bicycle);
        }

        private static PathMethod GetTrackFixMethod(PathMethod method)
        {
            method = SanitizeTrafficPathMethod(method);
            return (method & PathMethod.Track) != 0 ? method : 0;
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
