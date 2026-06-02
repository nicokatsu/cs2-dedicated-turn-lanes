using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryWriteCenterRewriteMappings(
            TrafficApi trafficApi,
            Request request,
            CenterRewritePlan plan,
            out bool wrote)
        {
            wrote = false;
            if (plan == null ||
                plan.BySource.Count == 0 && plan.LegacyOffScopeSourceKeys.Count == 0)
            {
                return true;
            }

            object modifiedBuffer = plan.BySource.Count == 0 &&
                                    !trafficApi.HasModifiedLaneConnectionsBuffer(EntityManager, request.IntersectionNode)
                ? null
                : trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, request.IntersectionNode);
            if (modifiedBuffer == null && plan.BySource.Count == 0)
            {
                return true;
            }

            if (modifiedBuffer == null)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=modifiedBufferUnavailable.");
                return false;
            }

            CenterPreservationStats snapshotPreservation = CopyExistingCenterPreservedGeneratedConnections(
                trafficApi,
                plan,
                modifiedBuffer);
            if (snapshotPreservation.Connections > 0 || snapshotPreservation.Skipped > 0)
            {
                plan.PreservedSnapshotConnections += snapshotPreservation.Connections;
                plan.PreservedUturnConnections += snapshotPreservation.UturnConnections;
                plan.PreservedNonRoadConnections += snapshotPreservation.NonRoadConnections;
                plan.PreservedUnsafeConnections += snapshotPreservation.UnsafeConnections;
                plan.PreservationSkipped += snapshotPreservation.Skipped;
            }

            plan.AuditStats = AuditAndNormalizeTrafficMappingPlan(
                plan.BySource,
                CenterTrafficPlanAuditPolicy,
                null,
                null,
                null,
                null);
            plan.PlannedConnections = CountTrafficPlanConnections(plan.BySource);

            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> pair in plan.BySource)
            {
                if (!plan.SourceEndpoints.TryGetValue(pair.Key, out LaneEndpoint sourceEndpoint))
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=sourceEndpointMissing source={FormatEntity(pair.Key.Edge)}:{pair.Key.LaneIndex}.");
                    return false;
                }

                foreach (LaneMapping mapping in pair.Value.Values)
                {
                    TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
                    if (!plan.TargetEndpoints.TryGetValue(targetKey, out LaneEndpoint targetEndpoint))
                    {
                        if (!mapping.IsPreservationOnly || !mapping.HasTrafficMaps)
                        {
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=targetEndpointMissing mapping={FormatMapping(mapping)}.");
                            return false;
                        }

                        continue;
                    }

                    if (RestrictCenterTrafficPathMethodToEndpoints(mapping.Method, sourceEndpoint, targetEndpoint) == 0)
                    {
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] centerRewriteWriteFailed centerNode={FormatEntity(request.IntersectionNode)} reason=methodUnavailable mapping={FormatMapping(mapping)}.");
                        return false;
                    }
                }
            }

            List<object> kept = new List<object>(trafficApi.GetBufferLength(modifiedBuffer));
            int removedExisting = 0;
            int removedLegacyOffScope = 0;
            int originalLength = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < originalLength; i++)
            {
                object existing = trafficApi.GetBufferItem(modifiedBuffer, i);
                SourceLaneKey existingKey = new SourceLaneKey(
                    trafficApi.GetModifiedConnectionEdge(existing),
                    trafficApi.GetModifiedConnectionLaneIndex(existing));
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                bool rewriteSource = plan.BySource.ContainsKey(existingKey);
                bool legacyOffScopeSource = !rewriteSource &&
                                            plan.LegacyOffScopeSourceKeys.Contains(existingKey) &&
                                            LooksLikeLegacyCenterRewriteOverride(trafficApi, existing, existingKey);
                if (!rewriteSource && !legacyOffScopeSource)
                {
                    kept.Add(existing);
                    continue;
                }

                removedExisting++;
                if (legacyOffScopeSource)
                {
                    removedLegacyOffScope++;
                }

                if (modifiedEntity != Entity.Null && EntityManager.Exists(modifiedEntity))
                {
                    AddMarkerIfMissing<Deleted>(modifiedEntity);
                }
            }

            trafficApi.ClearBuffer(modifiedBuffer);
            for (int i = 0; i < kept.Count; i++)
            {
                trafficApi.AddBufferElement(modifiedBuffer, kept[i]);
            }

            int writtenSources = 0;
            int writtenConnections = 0;
            int writtenUnsafeConnections = 0;
            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> pair in plan.BySource)
            {
                LaneEndpoint sourceEndpoint = plan.SourceEndpoints[pair.Key];
                Entity modifiedConnectionEntity = CreateTrafficModifiedConnectionEntity(
                    trafficApi,
                    request.IntersectionNode,
                    out object generatedBuffer);

                foreach (LaneMapping mapping in pair.Value.Values)
                {
                    TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
                    bool hasTargetEndpoint = plan.TargetEndpoints.TryGetValue(targetKey, out LaneEndpoint targetEndpoint);
                    PathMethod method = hasTargetEndpoint
                        ? RestrictCenterTrafficPathMethodToEndpoints(
                            SanitizeCenterTrafficPathMethod(mapping.Method),
                            sourceEndpoint,
                            targetEndpoint)
                        : SanitizeCenterTrafficPathMethod(mapping.Method);
                    if (method == 0)
                    {
                        continue;
                    }

                    if (!mapping.HasTrafficMaps && !hasTargetEndpoint)
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
                            : new float3x2(sourceEndpoint.LanePosition, targetEndpoint.LanePosition),
                        mapping.HasTrafficMaps
                            ? mapping.TrafficCarriagewayAndGroupIndexMap
                            : new int4(sourceEndpoint.CarriagewayAndGroup, targetEndpoint.CarriagewayAndGroup),
                        method,
                        mapping.IsUnsafe));
                    writtenConnections++;
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

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, request.IntersectionNode);
            wrote = writtenSources > 0 || removedExisting > 0;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Center Traffic rewrite write counts centerNode={FormatEntity(request.IntersectionNode)} pocketEdge={FormatEntity(request.PocketEdge)} leftHandTraffic={plan.LeftHandTraffic} bigTurn={plan.BigTurn} smallTurn={plan.SmallTurn} trafficWriteOrder={GetTrafficWriteOrder(request.Mode)} removedExisting={removedExisting} removedLegacyOffScope={removedLegacyOffScope} preservedExisting={kept.Count} writtenSources={writtenSources} expectedSources={plan.BySource.Count} writtenConnections={writtenConnections} plannedConnections={plan.PlannedConnections} writtenUnsafeConnections={writtenUnsafeConnections} straightConnectionsSafe={plan.StraightConnectionsWrittenSafe} straightUnsafeCleared={plan.StraightUnsafeCleared} smallTurnClearedFromStraightLane={plan.SmallTurnConnectionsClearedFromStraightLane} roadBicycle={plan.BicycleConnectionsWrittenWithRoad} runtimePreserved={plan.PreservedRuntimeConnections} snapshotPreserved={plan.PreservedSnapshotConnections} preservedUturn={plan.PreservedUturnConnections} preservedNonRoad={plan.PreservedNonRoadConnections} preservedUnsafe={plan.PreservedUnsafeConnections} preservationSkipped={plan.PreservationSkipped} trafficPlanAudit=({FormatTrafficPlanAuditStats(plan.AuditStats)}) legacyOffScopeSourceKeys={FormatSourceLaneKeys(plan.LegacyOffScopeSourceKeys)} diagnostics={FormatStringList(plan.Diagnostics)}.");
            return writtenSources == plan.BySource.Count &&
                   writtenConnections == plan.PlannedConnections;
        }

        private bool LooksLikeLegacyCenterRewriteOverride(
            TrafficApi trafficApi,
            object modifiedConnection,
            SourceLaneKey sourceKey)
        {
            List<TrafficGeneratedSnapshot> generatedSnapshots = new List<TrafficGeneratedSnapshot>(4);
            if (!TryReadTrafficGeneratedSnapshots(
                    trafficApi,
                    trafficApi.GetModifiedConnectionEntity(modifiedConnection),
                    generatedSnapshots))
            {
                return false;
            }

            if (generatedSnapshots.Count <= 0 || generatedSnapshots.Count > 4)
            {
                return false;
            }

            for (int i = 0; i < generatedSnapshots.Count; i++)
            {
                TrafficGeneratedSnapshot generated = generatedSnapshots[i];
                PathMethod method = SanitizeCenterTrafficPathMethod(generated.Method);
                if (generated.SourceEdge != sourceKey.Edge ||
                    generated.SourceLaneIndex != sourceKey.LaneIndex ||
                    (method & ~PathMethod.Road) != 0 ||
                    generated.IsUnsafe)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
