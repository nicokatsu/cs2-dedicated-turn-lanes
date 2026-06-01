using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game;
using Game.City;
using Game.Common;
using Unity.Entities;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem : GameSystemBase
    {
        private const int MaxLaneDataRetries = 12;
        private const int MaxVerificationRetries = 8;
        private const int MaxTrafficRuntimeWaitFrames = 120;
        private const int RequiredStableVerificationFrames = 3;
        private const string CleanupOnlyPersistentTrafficWriteDisabledReason = "disabledAfterTrafficUiCrashOnTramUpgrade";
        private static readonly bool s_EnableCleanupOnlyPersistentTrafficWrite = true;

        private readonly List<Request> m_Requests = new List<Request>();
        private readonly List<LaneEndpoint> m_SourceLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_TargetLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_ReverseSourceLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_ReverseTargetLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_TrackSourceLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_TrackTargetLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_TrackReverseSourceLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_TrackReverseTargetLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneMapping> m_Mappings = new List<LaneMapping>(12);
        private readonly List<LaneMapping> m_TrackMappings = new List<LaneMapping>(12);
        private readonly List<object> m_KeptTrafficConnections = new List<object>(16);
        private readonly List<ConnectorLane> m_ConnectorLanes = new List<ConnectorLane>(16);
        private readonly List<ConnectorLane> m_ExistingConnectorLanes = new List<ConnectorLane>(16);
        private readonly List<ConnectorLane> m_TrackConnectorLanes = new List<ConnectorLane>(16);
        private readonly List<ConnectorLane> m_StaleConnectorLanes = new List<ConnectorLane>(16);
        private readonly List<UturnCleanupSourcePlan> m_UturnCleanupSourcePlans = new List<UturnCleanupSourcePlan>(8);
        private readonly List<UturnCleanupConnectionPlan> m_UturnCleanupConnectionPlans = new List<UturnCleanupConnectionPlan>(16);
        private readonly List<int> m_RemoveSubLaneIndexes = new List<int>(16);
        private readonly List<CenterTurnCandidate> m_CenterTurnCandidates = new List<CenterTurnCandidate>(16);

        private CityConfigurationSystem m_CityConfigurationSystem;
        private TrafficApi m_TrafficApi;
        private bool m_TrafficUnavailableLogged;
        private int m_TrafficRuntimeWaitFrames;
        private EntityQuery m_LaneRefreshOwnerQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_LaneRefreshOwnerQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<SubLane>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() }
            });
            Mod.LogEssential("[SplitLaneConnectionFix] Created. Traffic lane connection writer runs only after final apply verification and before TrafficLaneSystem when ordered registration is available.");
        }

        protected override void OnUpdate()
        {
            if (m_Requests.Count == 0)
            {
                return;
            }

            if (!TryGetTrafficApi(out TrafficApi trafficApi, out string trafficError))
            {
                bool missingTrafficTypes = trafficError.StartsWith("missingTypes", StringComparison.Ordinal);
                if (missingTrafficTypes && !Mod.TrafficLaneConnectionFixEnabled)
                {
                    if (!m_TrafficUnavailableLogged)
                    {
                        Mod.UpdateTrafficRuntimeStatus(false, trafficError, m_TrafficRuntimeWaitFrames);
                        Mod.LogEssential($"[SplitLaneConnectionFix] Traffic runtime types are unavailable and repair systems were not enabled; queued split-node connection repairs will be skipped. Install/enable Traffic dependency 80095 to enable this repair path. trafficDetectedOnLoad={Mod.TrafficModDetected} trafficRepairEnabled={Mod.TrafficLaneConnectionFixEnabled} error={trafficError}");
                        m_TrafficUnavailableLogged = true;
                    }

                    m_Requests.Clear();
                    return;
                }

                m_TrafficRuntimeWaitFrames++;
                if (m_TrafficRuntimeWaitFrames > MaxTrafficRuntimeWaitFrames)
                {
                    Mod.UpdateTrafficRuntimeStatus(false, trafficError, m_TrafficRuntimeWaitFrames);
                    Mod.LogEssential($"[SplitLaneConnectionFix] Traffic runtime did not become ready after {m_TrafficRuntimeWaitFrames} frames; skipping queued split-node connection repairs. trafficDetectedOnLoad={Mod.TrafficModDetected} trafficRepairEnabled={Mod.TrafficLaneConnectionFixEnabled} error={trafficError}");
                    m_Requests.Clear();
                    m_TrafficRuntimeWaitFrames = 0;
                    return;
                }

                if (!m_TrafficUnavailableLogged || m_TrafficRuntimeWaitFrames % 30 == 0)
                {
                    Mod.UpdateTrafficRuntimeStatus(false, trafficError, m_TrafficRuntimeWaitFrames);
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Waiting for Traffic runtime before repairing split-node connections frameWait={m_TrafficRuntimeWaitFrames}/{MaxTrafficRuntimeWaitFrames} trafficDetectedOnLoad={Mod.TrafficModDetected} trafficRepairEnabled={Mod.TrafficLaneConnectionFixEnabled} missingTypes={missingTrafficTypes} error={trafficError}");
                    m_TrafficUnavailableLogged = true;
                }

                return;
            }

            m_TrafficRuntimeWaitFrames = 0;
            m_TrafficUnavailableLogged = false;
            EntityManager.CompleteAllTrackedJobs();

            for (int i = m_Requests.Count - 1; i >= 0; i--)
            {
                Request request = m_Requests[i];
                if (UnityEngine.Time.frameCount < request.QueuedFrame)
                {
                    m_Requests[i] = request;
                    continue;
                }

                try
                {
                    if (request.TrafficWritten)
                    {
                        m_Requests[i] = request;
                        continue;
                    }

                    if (!TryPrepareMappings(ref request))
                    {
                        request.LaneDataRetries++;
                        if (request.LaneDataRetries > MaxLaneDataRetries)
                        {
                            if (request.OuterEdge != Entity.Null)
                            {
                                EnsureTrackSnapshotCaptured(ref request, request.OuterEdge, "prepare-retry-exhausted");
                            }

                            if (!request.UturnCleanupPending)
                            {
                                QueueUturnCleanup(ref request, request.OuterEdge, "lane-data retries exhausted after prepare failure");
                            }

                            request.RemoveAfterUturnCleanup = true;
                            if (!request.ReverseTrackAuditLogged &&
                                request.OuterEdge != Entity.Null &&
                                request.UturnCleanupReason != null &&
                                request.UturnCleanupReason.Contains("reverse mapping failed"))
                            {
                                LogReverseTrackEndpointAudit(request, request.OuterEdge, request.UturnCleanupReason);
                                request.ReverseTrackAuditLogged = true;
                            }

                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Exhausted lane-data retries; waiting for post-lane U-turn cleanup before skipping request splitNode={FormatEntity(request.SplitNode)} intersection={FormatEntity(request.IntersectionNode)} original={FormatEntity(request.OriginalEdge)} outerEdge={FormatEntity(request.OuterEdge)} pocket={FormatEntity(request.PocketEdge)} sourcePrefab={FormatEntity(request.SourcePrefab)} targetPrefab={FormatEntity(request.TargetPrefab)} reason={request.UturnCleanupReason}.");
                            m_Requests[i] = request;
                            continue;
                        }

                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Lane data not ready splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)} retry={request.LaneDataRetries}/{MaxLaneDataRetries}.");
                        m_Requests[i] = request;
                        continue;
                    }

                    if (!WriteTrafficMappings(trafficApi, request))
                    {
                        if (request.OuterEdge != Entity.Null)
                        {
                            EnsureTrackSnapshotCaptured(ref request, request.OuterEdge, "road-only-write-failed");
                        }

                        QueueUturnCleanup(ref request, request.OuterEdge, "Traffic mapping write failed");
                        request.RemoveAfterUturnCleanup = true;
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic mapping write failed; queued post-lane U-turn cleanup before skipping request splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)}.");
                        m_Requests[i] = request;
                        continue;
                    }

                    request.TrafficWritten = true;
                    request.FinalTrackTrafficWritten = true;
                    request.TrafficWriteFrame = UnityEngine.Time.frameCount;
                    request.StableVerificationFrames = 0;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Wrote unified Traffic lane mapping; logical stages complete in one Traffic modification pass splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} forwardRoadState={request.ForwardRoadState} forwardSkipReason={request.ForwardRoadSkipReason} reverseRoadState={request.ReverseRoadState} reverseSkipReason={request.ReverseRoadSkipReason} mappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackSkippedReason={request.TrackSkippedReason} sourceOrder={FormatLaneOrder(request.SourceLanes)} targetOrder={FormatLaneOrder(request.TargetLanes)} reverseSourceOrder={FormatLaneOrder(request.ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(request.ReverseTargetLanes)} trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}) extraLane={request.ExtraTargetLaneIndex} turn={request.Turn} branchSource={request.BranchSourceLaneIndex} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()} leftHandTraffic={m_CityConfigurationSystem.leftHandTraffic}.");
                    m_Requests[i] = request;
                }
                catch (Exception ex)
                {
                    Mod.LogException(ex, $"[SplitLaneConnectionFix] Unhandled exception while repairing splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)}.");
                    m_Requests.RemoveAt(i);
                }
            }
        }

        internal void ProcessPostLaneGenerationCleanup()
        {
            if (m_Requests.Count == 0)
            {
                return;
            }

            EntityManager.CompleteAllTrackedJobs();

            int pendingTrafficWrite = 0;
            int processed = 0;
            for (int i = m_Requests.Count - 1; i >= 0; i--)
            {
                Request request = m_Requests[i];
                if (!request.TrafficWritten)
                {
                    if (request.UturnCleanupPending)
                    {
                        processed++;
                        try
                        {
                            int deletedUturn = DeleteStaleSplitNodeUturnConnectorLanes(request, request.OuterEdge, request.UturnCleanupReason);
                            request.UturnCleanupPending = false;
                            if (request.RemoveAfterUturnCleanup)
                            {
                                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Skipped lane connection repair after U-turn cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} deletedUturn={deletedUturn} reason={request.UturnCleanupReason} retries={request.LaneDataRetries}/{MaxLaneDataRetries}.");
                                m_Requests.RemoveAt(i);
                                continue;
                            }

                            m_Requests[i] = request;
                        }
                        catch (Exception ex)
                        {
                            Mod.LogException(ex, $"[SplitLaneConnectionFix] Unhandled exception during U-turn cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={request.UturnCleanupReason}.");
                            m_Requests.RemoveAt(i);
                        }

                        continue;
                    }

                    pendingTrafficWrite++;
                    continue;
                }

                if (UnityEngine.Time.frameCount <= request.TrafficWriteFrame)
                {
                    m_Requests[i] = request;
                    continue;
                }

                processed++;
                try
                {
                    if (VerifyConnectorLanes(request))
                    {
                        request.StableVerificationFrames++;
                        if (request.StableVerificationFrames >= RequiredStableVerificationFrames)
                        {
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Completed splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] leftHandTraffic={m_CityConfigurationSystem.leftHandTraffic} verificationAttempts={request.VerificationAttempts} stableFrames={request.StableVerificationFrames}/{RequiredStableVerificationFrames}.");
                            m_Requests.RemoveAt(i);
                            continue;
                        }

                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Post-lane connector verification stable splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] stableFrames={request.StableVerificationFrames}/{RequiredStableVerificationFrames}.");
                        m_Requests[i] = request;
                        continue;
                    }

                    request.StableVerificationFrames = 0;
                    if (!TryRebuildConnectorLanes(ref request, out DirectRebuildStats retryStats))
                    {
                        int deletedUturn = DeleteStaleSplitNodeUturnConnectorLanes(request, request.OuterEdge, $"direct rebuild failed: {retryStats.Reason}");
                        request.VerificationAttempts++;
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Post-lane direct connector rebuild retry failed; cleaned stale U-turns only splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] reason={retryStats.Reason} deletedUturn={deletedUturn} trackKept={retryStats.TrackKept} trackCloned={retryStats.TrackCloned} trackSkipped={retryStats.TrackSkipped} attempt={request.VerificationAttempts}/{MaxVerificationRetries} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
                        if (request.VerificationAttempts > MaxVerificationRetries)
                        {
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Verification exhausted splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}]; post-lane direct connector rebuild could not be re-applied.");
                            m_Requests.RemoveAt(i);
                            continue;
                        }

                        m_Requests[i] = request;
                        continue;
                    }

                    if (request.VerificationAttempts > MaxVerificationRetries)
                    {
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Verification exhausted splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}]; post-lane direct connector rebuild was applied but verification never stabilized.");
                        m_Requests.RemoveAt(i);
                        continue;
                    }

                    request.VerificationAttempts++;
                    request.TrafficWriteFrame = UnityEngine.Time.frameCount;
                    request.StableVerificationFrames = 0;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Post-lane direct connector rebuild pending verification splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] kept={retryStats.Kept} cloned={retryStats.Cloned} deleted={retryStats.Deleted} deletedUturn={retryStats.DeletedUturn} trackKept={retryStats.TrackKept} trackCloned={retryStats.TrackCloned} trackSkipped={retryStats.TrackSkipped} updated={retryStats.Updated} attempt={request.VerificationAttempts}/{MaxVerificationRetries} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
                    m_Requests[i] = request;
                }
                catch (Exception ex)
                {
                    Mod.LogException(ex, $"[SplitLaneConnectionFix] Unhandled exception during post-lane cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)}.");
                    m_Requests.RemoveAt(i);
                }
            }

            if (processed > 0 || pendingTrafficWrite > 0)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Post-lane cleanup pass frame={UnityEngine.Time.frameCount} processed={processed} pendingTrafficWrite={pendingTrafficWrite} remaining={m_Requests.Count} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
            }
        }

        public void Queue(
            Entity intersectionNode,
            Entity splitNode,
            Entity originalEdge,
            Entity pocketEdge,
            Entity sourcePrefab,
            Entity targetPrefab)
        {
            QueueInternal(
                intersectionNode,
                Entity.Null,
                splitNode,
                originalEdge,
                pocketEdge,
                sourcePrefab,
                targetPrefab,
                RepairMode.Standard);
        }

        public void QueueBalancedOppositeTarget(
            Entity intersectionNode,
            Entity farIntersectionNode,
            Entity splitNode,
            Entity originalEdge,
            Entity pocketEdge,
            Entity sourcePrefab,
            Entity targetPrefab,
            FarIntersectionTrafficSnapshot farIntersectionSnapshot)
        {
            QueueInternal(
                intersectionNode,
                farIntersectionNode,
                splitNode,
                originalEdge,
                pocketEdge,
                sourcePrefab,
                targetPrefab,
                RepairMode.BalancedOppositeTarget,
                farIntersectionSnapshot: farIntersectionSnapshot);
        }

        public void QueueShortEdgeTransition(
            Entity intersectionNode,
            Entity transitionNode,
            Entity continuationEdge,
            Entity shortEdge,
            Entity sourcePrefab,
            Entity targetPrefab,
            TransitionConnectionSnapshot reverseSnapshot)
        {
            QueueInternal(
                intersectionNode,
                Entity.Null,
                transitionNode,
                continuationEdge,
                shortEdge,
                sourcePrefab,
                targetPrefab,
                RepairMode.ShortEdgeTransition,
                continuationEdge,
                reverseSnapshot);
        }

        private void QueueInternal(
            Entity intersectionNode,
            Entity farIntersectionNode,
            Entity splitNode,
            Entity originalEdge,
            Entity pocketEdge,
            Entity sourcePrefab,
            Entity targetPrefab,
            RepairMode mode,
            Entity explicitOuterEdge = default,
            TransitionConnectionSnapshot reverseSnapshot = null,
            FarIntersectionTrafficSnapshot farIntersectionSnapshot = null)
        {
            if (splitNode == Entity.Null || pocketEdge == Entity.Null)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Queue skipped: invalid splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} mode={mode} farIntersection={FormatEntity(farIntersectionNode)}.");
                return;
            }

            for (int i = 0; i < m_Requests.Count; i++)
            {
                Request existing = m_Requests[i];
                if (existing.SplitNode == splitNode && existing.PocketEdge == pocketEdge)
                {
                    existing.IntersectionNode = intersectionNode;
                    existing.FarIntersectionNode = farIntersectionNode;
                    existing.OriginalEdge = originalEdge;
                    existing.SourcePrefab = sourcePrefab;
                    existing.TargetPrefab = targetPrefab;
                    existing.Mode = mode;
                    existing.OuterEdge = explicitOuterEdge;
                    existing.TransitionReverseSnapshot = reverseSnapshot;
                    existing.FarIntersectionSnapshot = farIntersectionSnapshot;
                    existing.QueuedFrame = UnityEngine.Time.frameCount;
                    existing.LaneDataRetries = 0;
                    existing.VerificationAttempts = 0;
                    existing.StableVerificationFrames = 0;
                    existing.TrafficWritten = false;
                    existing.FinalTrackTrafficWritten = false;
                    ResetRoadPreparation(ref existing);
                    ResetTrackSnapshot(ref existing);
                    m_Requests[i] = existing;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Updated queued request splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} explicitOuter={FormatEntity(explicitOuterEdge)} sourcePrefab={FormatEntity(sourcePrefab)} targetPrefab={FormatEntity(targetPrefab)} mode={mode} farIntersection={FormatEntity(farIntersectionNode)} reverseSnapshot={FormatSnapshot(reverseSnapshot)} farSnapshot={FormatFarSnapshot(farIntersectionSnapshot)} frame={UnityEngine.Time.frameCount}.");
                    return;
                }
            }

            m_Requests.Add(new Request
            {
                IntersectionNode = intersectionNode,
                FarIntersectionNode = farIntersectionNode,
                SplitNode = splitNode,
                OriginalEdge = originalEdge,
                PocketEdge = pocketEdge,
                SourcePrefab = sourcePrefab,
                TargetPrefab = targetPrefab,
                Mode = mode,
                OuterEdge = explicitOuterEdge,
                TransitionReverseSnapshot = reverseSnapshot,
                FarIntersectionSnapshot = farIntersectionSnapshot,
                ForwardRoadState = RoadDirectionState.Skipped,
                ReverseRoadState = RoadDirectionState.Skipped,
                QueuedFrame = UnityEngine.Time.frameCount
            });

            bool splitUpdated = MarkUpdatedIfExists(splitNode, out bool splitAlreadyUpdated);
            bool pocketUpdated = MarkUpdatedIfExists(pocketEdge, out bool pocketAlreadyUpdated);
            bool originalUpdated = MarkUpdatedIfExists(originalEdge, out bool originalAlreadyUpdated);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Queued split-node connection repair splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} explicitOuter={FormatEntity(explicitOuterEdge)} intersection={FormatEntity(intersectionNode)} farIntersection={FormatEntity(farIntersectionNode)} sourcePrefab={FormatEntity(sourcePrefab)} targetPrefab={FormatEntity(targetPrefab)} mode={mode} reverseSnapshot={FormatSnapshot(reverseSnapshot)} farSnapshot={FormatFarSnapshot(farIntersectionSnapshot)} frame={UnityEngine.Time.frameCount} preMarkedUpdated=split:{FormatUpdateMarker(splitUpdated, splitAlreadyUpdated)},pocket:{FormatUpdateMarker(pocketUpdated, pocketAlreadyUpdated)},original:{FormatUpdateMarker(originalUpdated, originalAlreadyUpdated)}. Repair waits for post-apply lane generation; preview is intentionally not modified.");
        }
    }
}
