using System.Collections.Generic;
using System.Linq;
using Colossal.Entities;
using Game.Common;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void QueueUturnCleanup(ref Request request, Entity outerEdge, string reason)
        {
            if (outerEdge != Entity.Null)
            {
                request.OuterEdge = outerEdge;
            }

            request.UturnCleanupPending = true;
            request.UturnCleanupReason = reason;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Queued post-lane U-turn cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} preserveExistingTraffic=True roadTrafficWrite={request.TrafficWritten} outerPreservationSnapshotCaptured={request.PreservationSnapshotCapturedForOuter} unsafePreservedMode=unchanged reason={reason}.");
        }

        private int DeleteStaleSplitNodeUturnConnectorLanes(Request request, Entity outerEdge, string reason)
        {
            if (!EntityManager.TryGetBuffer(request.SplitNode, false, out DynamicBuffer<SubLane> subLanes))
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot delete stale split-node U-turn connectors after skip: split node has no SubLane buffer splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} staleUturnCount=unknown preserveExistingTraffic=True roadTrafficWrite={request.TrafficWritten} outerPreservationSnapshotCaptured={request.PreservationSnapshotCapturedForOuter} unsafePreservedMode=unchanged persistentTrafficWrite=False persistentReason=noSubLaneBuffer reason={reason}.");
                return 0;
            }

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, outerEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            bool mandatoryRewriteAfterRoadSkip = false;
            if (m_StaleConnectorLanes.Count == 0)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] No stale split-node U-turn connectors found after skip splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} staleUturnCount=0 preserveExistingTraffic=True roadTrafficWrite={request.TrafficWritten} outerPreservationSnapshotCaptured={request.PreservationSnapshotCapturedForOuter} unsafePreservedMode=unchanged persistentTrafficWrite=False persistentReason=noStaleUturnConnectors reason={reason}.");
                return 0;
            }

            string staleSummary = FormatConnectorLanes(m_StaleConnectorLanes);
            bool persistentTrafficWritten = false;
            UturnCleanupWriteStats persistentStats = default;
            PopulateStaleSourceStats(m_StaleConnectorLanes, ref persistentStats);
            if (request.TrafficWritten)
            {
                persistentStats.Reason = "trafficMappingAlreadyWritten";
            }
            else if (!s_EnableCleanupOnlyPersistentTrafficWrite)
            {
                persistentStats.Reason = CleanupOnlyPersistentTrafficWriteDisabledReason;
            }
            else if (TryGetTrafficApi(out TrafficApi trafficApi, out string trafficError))
            {
                persistentTrafficWritten = TryWritePersistentUturnCleanupTrafficMappings(
                    trafficApi,
                    request,
                    outerEdge,
                    subLanes,
                    m_StaleConnectorLanes,
                    mandatoryRewriteAfterRoadSkip,
                    reason,
                    out persistentStats);
            }
            else
            {
                persistentStats.Reason = $"trafficApiUnavailable:{trafficError}";
            }

            if (!s_EnableRuntimeStaleUturnDirectDeletion)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Skipped direct runtime stale split-node U-turn connector deletion after skip splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} staleUturnCount={m_StaleConnectorLanes.Count} preserveExistingTraffic=True roadTrafficWrite={request.TrafficWritten} outerPreservationSnapshotCaptured={request.PreservationSnapshotCapturedForOuter} unsafePreservedMode=unchanged persistentTrafficWrite={persistentTrafficWritten} persistentSources={persistentStats.WrittenSources} persistentStaleSources={persistentStats.StaleSourceLanes} persistentKept={persistentStats.PreservedConnections} persistentTrafficSnapshotKept={persistentStats.PreservedTrafficSnapshotConnections} persistentTrafficSnapshotSources={persistentStats.TrafficSnapshotSourceLanes} persistentMissingTrafficSnapshotSources={persistentStats.MissingTrafficSnapshotSources} persistentMissingGeneratedBufferSources={persistentStats.MissingGeneratedBufferSources} persistentRuntimeFallbackKept=0 persistentRuntimeFallbackSuppressed={persistentStats.RuntimeFallbackSuppressedConnections} persistentUnsafeKept={persistentStats.UnsafePreservedConnections} suppressedTrafficUturn={persistentStats.SuppressedTrafficUturnConnections} persistentPreservedTrackConnections={persistentStats.PreservedTrackConnections} persistentTrackWrittenConnections={persistentStats.TrackWrittenConnections} persistentTrackOnlyTargets={persistentStats.TrackOnlyTargetConnections} persistentSharedTrackConnections={persistentStats.SharedTrackConnections} persistentEmptySources={persistentStats.EmptySources} persistentNormalizedMethods={persistentStats.NormalizedMethods} persistentInvalidLoadValidation={persistentStats.InvalidLoadValidationConnections} persistentSanitizedLoadValidation={persistentStats.SanitizedLoadValidationConnections} persistentRemovedExisting={persistentStats.RemovedExisting} persistentTrafficLoadValidation=({persistentStats.LoadValidationDetail}) persistentReason={persistentStats.Reason} staleSourceLanes={persistentStats.SourceLanes} rewriteSourceLanes={persistentStats.RewriteSourceLanes} reason={reason} directDeletionReason={RuntimeStaleUturnDirectDeletionDisabledReason} connectors={staleSummary} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
                return 0;
            }

            m_RemoveSubLaneIndexes.Clear();
            for (int i = 0; i < m_StaleConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_StaleConnectorLanes[i];
                QueueDeleteConnector(connector.Entity);
                m_RemoveSubLaneIndexes.Add(connector.SubLaneIndex);
            }

            int removedSubLanes = 0;
            int lastRemovedIndex = -1;
            m_RemoveSubLaneIndexes.Sort();
            for (int i = m_RemoveSubLaneIndexes.Count - 1; i >= 0; i--)
            {
                int index = m_RemoveSubLaneIndexes[i];
                if (index == lastRemovedIndex)
                {
                    continue;
                }

                if (index >= 0 && index < subLanes.Length)
                {
                    subLanes.RemoveAt(index);
                    removedSubLanes++;
                    lastRemovedIndex = index;
                }
            }

            MarkUpdatedIfExists(request.SplitNode);
            MarkUpdatedIfExists(outerEdge);
            MarkUpdatedIfExists(request.PocketEdge);

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Deleted stale split-node U-turn connectors after skip splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} staleUturnCount={m_StaleConnectorLanes.Count} deletedUturn={m_StaleConnectorLanes.Count} removedSubLanes={removedSubLanes} preserveExistingTraffic=True roadTrafficWrite={request.TrafficWritten} outerPreservationSnapshotCaptured={request.PreservationSnapshotCapturedForOuter} unsafePreservedMode=unchanged mandatoryRewriteAfterRoadSkip={mandatoryRewriteAfterRoadSkip} persistentTrafficWrite={persistentTrafficWritten} persistentSources={persistentStats.WrittenSources} persistentStaleSources={persistentStats.StaleSourceLanes} persistentKept={persistentStats.PreservedConnections} persistentTrafficSnapshotKept={persistentStats.PreservedTrafficSnapshotConnections} persistentTrafficSnapshotSources={persistentStats.TrafficSnapshotSourceLanes} persistentMissingTrafficSnapshotSources={persistentStats.MissingTrafficSnapshotSources} persistentMissingGeneratedBufferSources={persistentStats.MissingGeneratedBufferSources} persistentRuntimeFallbackKept=0 persistentRuntimeFallbackSuppressed={persistentStats.RuntimeFallbackSuppressedConnections} persistentUnsafeKept={persistentStats.UnsafePreservedConnections} suppressedTrafficUturn={persistentStats.SuppressedTrafficUturnConnections} persistentPreservedTrackConnections={persistentStats.PreservedTrackConnections} persistentTrackWrittenConnections={persistentStats.TrackWrittenConnections} persistentTrackOnlyTargets={persistentStats.TrackOnlyTargetConnections} persistentSharedTrackConnections={persistentStats.SharedTrackConnections} persistentEmptySources={persistentStats.EmptySources} persistentNormalizedMethods={persistentStats.NormalizedMethods} persistentInvalidLoadValidation={persistentStats.InvalidLoadValidationConnections} persistentSanitizedLoadValidation={persistentStats.SanitizedLoadValidationConnections} persistentRemovedExisting={persistentStats.RemovedExisting} persistentTrafficLoadValidation=({persistentStats.LoadValidationDetail}) persistentReason={persistentStats.Reason} staleSourceLanes={persistentStats.SourceLanes} rewriteSourceLanes={persistentStats.RewriteSourceLanes} reason={reason} connectors={staleSummary} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
            return m_StaleConnectorLanes.Count;
        }

        private static void PopulateStaleSourceStats(IReadOnlyList<ConnectorLane> staleUturns, ref UturnCleanupWriteStats stats)
        {
            HashSet<SourceLaneKey> staleSourceKeys = TrafficUturnSuppressionPlanner.BuildStaleSourceKeys(staleUturns);
            stats.StaleSourceLanes = staleSourceKeys.Count;
            stats.SourceLanes = FormatSourceLaneKeys(staleSourceKeys);
        }

        private bool TryWritePersistentUturnCleanupTrafficMappings(
            TrafficApi trafficApi,
            Request request,
            Entity outerEdge,
            DynamicBuffer<SubLane> subLanes,
            IReadOnlyList<ConnectorLane> staleUturns,
            bool allowRewriteWithoutStaleUturns,
            string reason,
            out UturnCleanupWriteStats stats)
        {
            stats = default;
            if ((staleUturns == null || staleUturns.Count == 0) &&
                !allowRewriteWithoutStaleUturns)
            {
                stats.Reason = "noStaleUturnConnectors";
                return false;
            }

            HashSet<SourceLaneKey> staleSourceKeys = TrafficUturnSuppressionPlanner.BuildStaleSourceKeys(staleUturns);
            stats.StaleSourceLanes = staleSourceKeys.Count;
            stats.SourceLanes = FormatSourceLaneKeys(staleSourceKeys);

            CollectSplitNodeConnectorLanes(request.SplitNode, outerEdge, request.PocketEdge, subLanes, m_ConnectorLanes);
            stats.RuntimeFallbackSuppressedConnections = TrafficUturnSuppressionPlanner.CountRuntimeNonUturnConnections(
                staleSourceKeys,
                m_ConnectorLanes);

            if (!trafficApi.HasModifiedLaneConnectionsBuffer(EntityManager, request.SplitNode))
            {
                stats.MissingTrafficSnapshotSources = staleSourceKeys.Count;
                stats.Reason = "noTrafficSnapshotRuntimeFallbackDisabled";
                return false;
            }

            if (!TryCreateTrafficLoadValidationContext(
                    request.SplitNode,
                    "cleanup-only",
                    out TrafficLoadValidationContext loadValidationContext,
                    out string loadValidationContextReason))
            {
                stats.InvalidLoadValidationConnections = staleSourceKeys.Count;
                stats.LoadValidationDetail = loadValidationContextReason;
                stats.Reason = "trafficLoadValidationContextUnavailable";
                Mod.LogEssential($"[SplitLaneConnectionFix] Cleanup-only Traffic U-turn suppression skipped before mutation because split node cannot pass Traffic load validation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} staleSourceLanes={stats.SourceLanes} reason={loadValidationContextReason}.");
                return false;
            }

            m_UturnCleanupSourcePlans.Clear();
            m_UturnCleanupConnectionPlans.Clear();
            HashSet<SourceLaneKey> rewriteSourceKeys = new HashSet<SourceLaneKey>();
            TrafficLoadValidationStats loadValidationStats = TrafficLoadValidationStats.Create();
            foreach (SourceLaneKey sourceKey in staleSourceKeys.OrderBy(key => key.Edge.Index).ThenBy(key => key.LaneIndex))
            {
                bool copiedTrafficSnapshot = TryAppendExistingTrafficCleanupMappings(
                    trafficApi,
                    request,
                    sourceKey,
                    loadValidationContext,
                    out UturnCleanupSourcePlan sourcePlan,
                    ref stats,
                    ref loadValidationStats);
                if (!copiedTrafficSnapshot)
                {
                    continue;
                }

                if (sourcePlan.ConnectionCount == 0)
                {
                    stats.EmptySources++;
                }

                rewriteSourceKeys.Add(sourceKey);
                m_UturnCleanupSourcePlans.Add(sourcePlan);
            }

            stats.RewriteSourceLanes = FormatSourceLaneKeys(rewriteSourceKeys);
            stats.InvalidLoadValidationConnections = loadValidationStats.InvalidConnections + loadValidationStats.InvalidSources;
            stats.SanitizedLoadValidationConnections = loadValidationStats.SanitizedConnections;
            stats.LoadValidationDetail = loadValidationStats.Format(rewriteSourceKeys);
            if (rewriteSourceKeys.Count == 0)
            {
                stats.Reason = stats.MissingTrafficSnapshotSources > 0
                    ? "noTrafficSnapshotRuntimeFallbackDisabled"
                    : stats.InvalidLoadValidationConnections > 0
                    ? "noLoadValidTrafficSnapshotConnections"
                    : "noReadableTrafficSnapshotRuntimeFallbackDisabled";
                return false;
            }

            if (m_UturnCleanupSourcePlans.Count == 0)
            {
                stats.Reason = "noWritableSourceEndpoints";
                return false;
            }

            object modifiedBuffer = trafficApi.GetModifiedLaneConnectionsBuffer(EntityManager, request.SplitNode, false);
            if (modifiedBuffer == null)
            {
                stats.Reason = "modifiedLaneConnectionsBufferUnavailable";
                return false;
            }

            m_KeptTrafficConnections.Clear();
            int originalLength = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < originalLength; i++)
            {
                object existing = trafficApi.GetBufferItem(modifiedBuffer, i);
                SourceLaneKey existingKey = new SourceLaneKey(
                    trafficApi.GetModifiedConnectionEdge(existing),
                    trafficApi.GetModifiedConnectionLaneIndex(existing));
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                if (rewriteSourceKeys.Contains(existingKey))
                {
                    stats.RemovedExisting++;
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

            for (int i = 0; i < m_UturnCleanupSourcePlans.Count; i++)
            {
                UturnCleanupSourcePlan sourcePlan = m_UturnCleanupSourcePlans[i];
                Entity modifiedConnectionEntity = CreateTrafficModifiedConnectionEntity(
                    trafficApi,
                    request.SplitNode,
                    out object generatedBuffer);

                for (int j = 0; j < sourcePlan.ConnectionCount; j++)
                {
                    UturnCleanupConnectionPlan connectionPlan = m_UturnCleanupConnectionPlans[sourcePlan.FirstConnection + j];
                    trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                        sourcePlan.Key.Edge,
                        connectionPlan.TargetEdge,
                        sourcePlan.Key.LaneIndex,
                        connectionPlan.TargetLaneIndex,
                        connectionPlan.LanePositionMap,
                        connectionPlan.CarriagewayAndGroupIndexMap,
                        connectionPlan.Method,
                        connectionPlan.IsUnsafe));
                    stats.PreservedConnections++;
                    if ((connectionPlan.Method & PathMethod.Track) != 0)
                    {
                        stats.TrackWrittenConnections++;
                        if ((connectionPlan.Method & PathMethod.Road) == 0)
                        {
                            stats.TrackOnlyTargetConnections++;
                        }

                        if ((connectionPlan.Method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                        {
                            stats.SharedTrackConnections++;
                        }
                    }
                }

                trafficApi.AddBufferElement(modifiedBuffer, trafficApi.CreateModifiedLaneConnection(
                    sourcePlan.Key.LaneIndex,
                    sourcePlan.SourceCarriagewayAndGroup,
                    sourcePlan.SourceLanePosition,
                    sourcePlan.Key.Edge,
                    modifiedConnectionEntity));
                stats.WrittenSources++;
            }

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, request.SplitNode);
            MarkForLaneRebuild(request);
            stats.Reason = allowRewriteWithoutStaleUturns ? "okSnapshotOnlyMandatoryRewriteAfterRoadSkip" : "okSnapshotOnly";
            if (stats.MissingTrafficSnapshotSources > 0 || stats.MissingGeneratedBufferSources > 0)
            {
                stats.Reason += "Partial";
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Wrote cleanup-only Traffic U-turn suppression splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} allowRewriteWithoutStaleUturns={allowRewriteWithoutStaleUturns} staleSourceLanes={stats.SourceLanes} rewriteSourceLanes={stats.RewriteSourceLanes} writtenSources={stats.WrittenSources} preservedNonUturn={stats.PreservedConnections} preservedTrafficSnapshot={stats.PreservedTrafficSnapshotConnections} trafficSnapshotSources={stats.TrafficSnapshotSourceLanes} missingTrafficSnapshotSources={stats.MissingTrafficSnapshotSources} missingGeneratedBufferSources={stats.MissingGeneratedBufferSources} runtimeFallback=0 runtimeFallbackSuppressed={stats.RuntimeFallbackSuppressedConnections} unsafePreserved={stats.UnsafePreservedConnections} suppressedTrafficUturn={stats.SuppressedTrafficUturnConnections} preservedTrackConnections={stats.PreservedTrackConnections} trackWrittenConnections={stats.TrackWrittenConnections} trackOnlyTargets={stats.TrackOnlyTargetConnections} sharedTrackConnections={stats.SharedTrackConnections} emptySources={stats.EmptySources} normalizedMethods={stats.NormalizedMethods} invalidLoadValidation={stats.InvalidLoadValidationConnections} sanitizedLoadValidation={stats.SanitizedLoadValidationConnections} removedExisting={stats.RemovedExisting} trafficLoadValidation=({stats.LoadValidationDetail}) reason={reason} staleConnectors={FormatConnectorLanes(staleUturns)} runtimeConnectors={FormatConnectorLanes(m_ConnectorLanes)}.");
            return true;
        }

        private bool TryAppendExistingTrafficCleanupMappings(
            TrafficApi trafficApi,
            Request request,
            SourceLaneKey sourceKey,
            TrafficLoadValidationContext loadValidationContext,
            out UturnCleanupSourcePlan sourcePlan,
            ref UturnCleanupWriteStats stats,
            ref TrafficLoadValidationStats loadValidationStats)
        {
            sourcePlan = default;
            List<TrafficSourceSnapshot> sourceSnapshots = new List<TrafficSourceSnapshot>(2);
            if (!TryReadTrafficSourceSnapshots(
                    trafficApi,
                    request.SplitNode,
                    source => source.SourceEdge == sourceKey.Edge && source.SourceLaneIndex == sourceKey.LaneIndex,
                    null,
                    sourceSnapshots,
                    out TrafficSnapshotReadStats readStats,
                    out _))
            {
                stats.MissingTrafficSnapshotSources++;
                return false;
            }

            if (readStats.AcceptedSources == 0)
            {
                stats.MissingTrafficSnapshotSources++;
                return false;
            }

            stats.MissingGeneratedBufferSources += readStats.MissingGeneratedBuffers;
            bool copiedReadableSnapshot = false;
            int firstConnection = m_UturnCleanupConnectionPlans.Count;
            if (!TrySanitizeTrafficSourceForLoad(
                    loadValidationContext,
                    sourceKey,
                    out int2 sourceCarriagewayAndGroup,
                    out float3 sourceLanePosition,
                    out string sourceValidationReason))
            {
                loadValidationStats.InvalidSources++;
                loadValidationStats.AddSample($"cleanup-only:source {sourceValidationReason}");
                return false;
            }

            loadValidationStats.ValidSources++;
            for (int i = 0; i < sourceSnapshots.Count; i++)
            {
                TrafficSourceSnapshot sourceSnapshot = sourceSnapshots[i];
                if (!sourceSnapshot.HasGeneratedBuffer)
                {
                    continue;
                }

                if (!copiedReadableSnapshot)
                {
                    sourcePlan = new UturnCleanupSourcePlan
                    {
                        Key = sourceKey,
                        SourceCarriagewayAndGroup = sourceCarriagewayAndGroup,
                        SourceLanePosition = sourceLanePosition,
                        FirstConnection = firstConnection
                    };
                    copiedReadableSnapshot = true;
                }

                TrafficGeneratedSnapshot[] connections =
                    sourceSnapshot.Connections ?? System.Array.Empty<TrafficGeneratedSnapshot>();
                for (int generatedIndex = 0; generatedIndex < connections.Length; generatedIndex++)
                {
                    TrafficGeneratedSnapshot generated = connections[generatedIndex];
                    if (generated.SourceEdge != sourceKey.Edge || generated.SourceLaneIndex != sourceKey.LaneIndex)
                    {
                        continue;
                    }

                    if (generated.TargetEdge == sourceKey.Edge)
                    {
                        stats.SuppressedTrafficUturnConnections++;
                        continue;
                    }

                    PathMethod originalMethod = generated.Method;
                    PathMethod method = TrafficPathMethods.SanitizePreservedTrafficPathMethod(originalMethod);
                    if (method != originalMethod)
                    {
                        stats.NormalizedMethods++;
                    }

                    if (method == 0)
                    {
                        continue;
                    }

                    generated.Method = method;
                    if (!TrySanitizeTrafficGeneratedSnapshotForLoad(
                            loadValidationContext,
                            sourceKey,
                            generated,
                            out TrafficGeneratedSnapshot sanitized,
                            out bool methodChanged,
                            out string connectionValidationReason))
                    {
                        loadValidationStats.InvalidConnections++;
                        loadValidationStats.InvalidPreservationConnections++;
                        loadValidationStats.AddSample($"cleanup-only:connection {connectionValidationReason}");
                        continue;
                    }

                    loadValidationStats.ValidConnections++;
                    if (methodChanged ||
                        !generated.LanePositionMap.Equals(sanitized.LanePositionMap) ||
                        !generated.CarriagewayAndGroupIndexMap.Equals(sanitized.CarriagewayAndGroupIndexMap))
                    {
                        loadValidationStats.SanitizedConnections++;
                    }

                    m_UturnCleanupConnectionPlans.Add(new UturnCleanupConnectionPlan
                    {
                        TargetEdge = sanitized.TargetEdge,
                        TargetLaneIndex = sanitized.TargetLaneIndex,
                        LanePositionMap = sanitized.LanePositionMap,
                        CarriagewayAndGroupIndexMap = sanitized.CarriagewayAndGroupIndexMap,
                        Method = sanitized.Method,
                        IsUnsafe = sanitized.IsUnsafe
                    });
                    stats.PreservedTrafficSnapshotConnections++;
                    if ((sanitized.Method & PathMethod.Track) != 0)
                    {
                        stats.PreservedTrackConnections++;
                    }

                    if (generated.IsUnsafe)
                    {
                        stats.UnsafePreservedConnections++;
                    }
                }
            }

            if (!copiedReadableSnapshot)
            {
                return false;
            }

            sourcePlan.ConnectionCount = m_UturnCleanupConnectionPlans.Count - firstConnection;
            stats.TrafficSnapshotSourceLanes++;
            return true;
        }
    }
}
