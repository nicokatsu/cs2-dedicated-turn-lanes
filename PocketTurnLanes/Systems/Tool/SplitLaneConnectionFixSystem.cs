using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetEdge = Game.Net.Edge;
using NetTrackLane = Game.Net.TrackLane;
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool
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

        private enum RepairMode
        {
            Standard,
            BalancedOppositeTarget,
            ShortEdgeTransition
        }

        public sealed class TransitionConnectionSnapshot
        {
            public Entity Node;
            public Entity SourceEdge;
            public Entity TargetEdge;
            public string Source;
            public string Detail;
            public TransitionConnectionSnapshotMapping[] Mappings;
        }

        public struct TransitionConnectionSnapshotMapping
        {
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public float SourceLateral;
            public float TargetLateral;
            public float3 SourceLanePosition;
            public float3 TargetLanePosition;
            public int2 SourceCarriagewayAndGroup;
            public int2 TargetCarriagewayAndGroup;
            public PathMethod Method;
            public bool IsUnsafe;
        }

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
                        if (request.FinalTrackTrafficPending &&
                            !request.FinalTrackTrafficWritten)
                        {
                            if (TryWriteFinalTrackTrafficMappings(trafficApi, request, out FinalTrackWriteStats finalTrackStats))
                            {
                                request.FinalTrackTrafficPending = false;
                                request.FinalTrackTrafficWritten = true;
                                request.TrafficWriteFrame = UnityEngine.Time.frameCount;
                                request.StableVerificationFrames = 0;
                                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Wrote final track Traffic mappings in pre-lane phase splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackSources={finalTrackStats.WrittenSources} trackConnections={finalTrackStats.TrackConnections} roadConnectionsPreserved={finalTrackStats.RoadConnectionsPreserved} skipped={finalTrackStats.Skipped} removedExisting={finalTrackStats.RemovedExisting} reason={finalTrackStats.Reason}.");
                            }
                            else
                            {
                                request.FinalTrackTrafficPending = false;
                                request.FinalTrackTrafficWritten = true;
                                request.TrafficWriteFrame = UnityEngine.Time.frameCount;
                                request.StableVerificationFrames = 0;
                                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Final track Traffic write failed in pre-lane phase; falling back to direct/runtime track restore verification splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} skipped={finalTrackStats.Skipped} reason={finalTrackStats.Reason} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}].");
                            }
                        }

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
                    request.TrafficWriteFrame = UnityEngine.Time.frameCount;
                    request.StableVerificationFrames = 0;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Wrote road-only Traffic lane mapping; U-turn cleanup and final track snapshot restore deferred until post-lane phase splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackSkippedReason={request.TrackSkippedReason} sourceOrder={FormatLaneOrder(request.SourceLanes)} targetOrder={FormatLaneOrder(request.TargetLanes)} reverseSourceOrder={FormatLaneOrder(request.ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(request.ReverseTargetLanes)} trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}) extraLane={request.ExtraTargetLaneIndex} turn={request.Turn} branchSource={request.BranchSourceLaneIndex} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()} leftHandTraffic={m_CityConfigurationSystem.leftHandTraffic}.");
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
                        if (!request.FinalTrackTrafficWritten &&
                            HasTrackPreservationMappings(request))
                        {
                            request.FinalTrackTrafficPending = true;
                            request.StableVerificationFrames = 0;
                            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Road/U-turn verification passed; queued final track Traffic write for next pre-lane phase splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}].");
                            m_Requests[i] = request;
                            continue;
                        }

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
            Entity targetPrefab)
        {
            QueueInternal(
                intersectionNode,
                farIntersectionNode,
                splitNode,
                originalEdge,
                pocketEdge,
                sourcePrefab,
                targetPrefab,
                RepairMode.BalancedOppositeTarget);
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
            TransitionConnectionSnapshot reverseSnapshot = null)
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
                    existing.QueuedFrame = UnityEngine.Time.frameCount;
                    existing.LaneDataRetries = 0;
                    existing.VerificationAttempts = 0;
                    existing.StableVerificationFrames = 0;
                    existing.TrafficWritten = false;
                    existing.FinalTrackTrafficWritten = false;
                    existing.FinalTrackTrafficPending = false;
                    ResetTrackSnapshot(ref existing);
                    m_Requests[i] = existing;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Updated queued request splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} explicitOuter={FormatEntity(explicitOuterEdge)} sourcePrefab={FormatEntity(sourcePrefab)} targetPrefab={FormatEntity(targetPrefab)} mode={mode} farIntersection={FormatEntity(farIntersectionNode)} reverseSnapshot={FormatSnapshot(reverseSnapshot)} frame={UnityEngine.Time.frameCount}.");
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
                QueuedFrame = UnityEngine.Time.frameCount
            });

            bool splitUpdated = MarkUpdatedIfExists(splitNode, out bool splitAlreadyUpdated);
            bool pocketUpdated = MarkUpdatedIfExists(pocketEdge, out bool pocketAlreadyUpdated);
            bool originalUpdated = MarkUpdatedIfExists(originalEdge, out bool originalAlreadyUpdated);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Queued split-node connection repair splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} explicitOuter={FormatEntity(explicitOuterEdge)} intersection={FormatEntity(intersectionNode)} farIntersection={FormatEntity(farIntersectionNode)} sourcePrefab={FormatEntity(sourcePrefab)} targetPrefab={FormatEntity(targetPrefab)} mode={mode} reverseSnapshot={FormatSnapshot(reverseSnapshot)} frame={UnityEngine.Time.frameCount} preMarkedUpdated=split:{FormatUpdateMarker(splitUpdated, splitAlreadyUpdated)},pocket:{FormatUpdateMarker(pocketUpdated, pocketAlreadyUpdated)},original:{FormatUpdateMarker(originalUpdated, originalAlreadyUpdated)}. Repair waits for post-apply lane generation; preview is intentionally not modified.");
        }

        public TransitionConnectionSnapshot CaptureTransitionReverseConnections(
            Entity transitionNode,
            Entity sourceEdge,
            Entity targetEdge)
        {
            m_ReverseSourceLanes.Clear();
            m_ReverseTargetLanes.Clear();
            CollectEdgeCarLaneEndpoints(sourceEdge, transitionNode, EndpointRole.SourceEndAtNode, m_ReverseSourceLanes);
            CollectEdgeCarLaneEndpoints(targetEdge, transitionNode, EndpointRole.TargetStartAtNode, m_ReverseTargetLanes);
            NormalizeTransitionLaneLaterals(m_ReverseSourceLanes, m_ReverseTargetLanes);

            List<TransitionConnectionSnapshotMapping> mappings = new List<TransitionConnectionSnapshotMapping>(8);
            string source = "none";
            string trafficDetail = "not-run";
            if (TryGetTrafficApi(out TrafficApi trafficApi, out string trafficError))
            {
                if (TryCaptureTrafficReverseMappings(
                        trafficApi,
                        transitionNode,
                        sourceEdge,
                        targetEdge,
                        m_ReverseSourceLanes,
                        m_ReverseTargetLanes,
                        mappings,
                        out trafficDetail))
                {
                    source = "traffic";
                }
            }
            else
            {
                trafficDetail = trafficError;
            }

            string liveDetail = "not-run";
            if (mappings.Count == 0)
            {
                CollectConnectorLanes(transitionNode, sourceEdge, targetEdge, m_ExistingConnectorLanes);
                for (int i = 0; i < m_ExistingConnectorLanes.Count; i++)
                {
                    ConnectorLane connector = m_ExistingConnectorLanes[i];
                    if (!TryBuildSnapshotMapping(
                            connector.SourceLaneIndex,
                            connector.TargetLaneIndex,
                            connector.PathMethods,
                            false,
                            m_ReverseSourceLanes,
                            m_ReverseTargetLanes,
                            out TransitionConnectionSnapshotMapping mapping))
                    {
                        continue;
                    }

                    mappings.Add(mapping);
                }

                source = mappings.Count > 0 ? "live-connectors" : "empty";
                liveDetail = $"connectors={m_ExistingConnectorLanes.Count}";
            }

            TransitionConnectionSnapshot snapshot = new TransitionConnectionSnapshot
            {
                Node = transitionNode,
                SourceEdge = sourceEdge,
                TargetEdge = targetEdge,
                Source = source,
                Mappings = mappings.ToArray()
            };
            snapshot.Detail = $"snapshotSource={source} mappings={snapshot.Mappings.Length} sourceLanes={m_ReverseSourceLanes.Count} targetLanes={m_ReverseTargetLanes.Count} trafficDetail={trafficDetail} liveDetail={liveDetail}";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Captured transition reverse connection snapshot node={FormatEntity(transitionNode)} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} {snapshot.Detail} mappings={FormatSnapshotMappings(snapshot.Mappings)} sourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} targetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
            return snapshot;
        }

        private bool TryCaptureTrafficReverseMappings(
            TrafficApi trafficApi,
            Entity transitionNode,
            Entity sourceEdge,
            Entity targetEdge,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            List<TransitionConnectionSnapshotMapping> mappings,
            out string detail)
        {
            detail = "none";
            if (!trafficApi.HasModifiedLaneConnectionsBuffer(EntityManager, transitionNode))
            {
                detail = "trafficBuffer=missing";
                return false;
            }

            object modifiedBuffer = trafficApi.GetModifiedLaneConnectionsBuffer(EntityManager, transitionNode, true);
            int sourceEntries = 0;
            int generatedEntries = 0;
            int accepted = 0;
            int length = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < length; i++)
            {
                object modified = trafficApi.GetBufferItem(modifiedBuffer, i);
                Entity edge = trafficApi.GetModifiedConnectionEdge(modified);
                if (edge != sourceEdge)
                {
                    continue;
                }

                sourceEntries++;
                Entity modifiedConnectionEntity = trafficApi.GetModifiedConnectionEntity(modified);
                if (modifiedConnectionEntity == Entity.Null ||
                    !EntityManager.Exists(modifiedConnectionEntity) ||
                    !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity))
                {
                    continue;
                }

                object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity, true);
                int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
                for (int generatedIndex = 0; generatedIndex < generatedLength; generatedIndex++)
                {
                    object generated = trafficApi.GetBufferItem(generatedBuffer, generatedIndex);
                    if (trafficApi.GetGeneratedConnectionSource(generated) != sourceEdge ||
                        trafficApi.GetGeneratedConnectionTarget(generated) != targetEdge)
                    {
                        continue;
                    }

                    generatedEntries++;
                    int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
                    if (!TryBuildSnapshotMapping(
                            laneIndexMap.x & 0xff,
                            laneIndexMap.y & 0xff,
                            trafficApi.GetGeneratedConnectionMethod(generated),
                            trafficApi.GetGeneratedConnectionUnsafe(generated),
                            sourceLanes,
                            targetLanes,
                            out TransitionConnectionSnapshotMapping mapping))
                    {
                        continue;
                    }

                    mappings.Add(mapping);
                    accepted++;
                }
            }

            detail = $"trafficSources={sourceEntries} generatedMatches={generatedEntries} accepted={accepted}";
            return accepted > 0;
        }

        private static bool TryBuildSnapshotMapping(
            int sourceLaneIndex,
            int targetLaneIndex,
            PathMethod method,
            bool isUnsafe,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            out TransitionConnectionSnapshotMapping mapping)
        {
            mapping = default;
            if (!TryFindLaneEndpoint(sourceLanes, sourceLaneIndex, out LaneEndpoint source) ||
                !TryFindLaneEndpoint(targetLanes, targetLaneIndex, out LaneEndpoint target))
            {
                return false;
            }

            mapping = new TransitionConnectionSnapshotMapping
            {
                SourceLaneIndex = sourceLaneIndex,
                TargetLaneIndex = targetLaneIndex,
                SourceLateral = source.Lateral,
                TargetLateral = target.Lateral,
                SourceLanePosition = source.LanePosition,
                TargetLanePosition = target.LanePosition,
                SourceCarriagewayAndGroup = source.CarriagewayAndGroup,
                TargetCarriagewayAndGroup = target.CarriagewayAndGroup,
                Method = method,
                IsUnsafe = isUnsafe
            };
            return true;
        }

        private bool TryPrepareMappings(ref Request request)
        {
            if (!EntityManager.Exists(request.SplitNode) ||
                !EntityManager.Exists(request.PocketEdge) ||
                !EntityManager.TryGetComponent(request.PocketEdge, out NetEdge _))
            {
                string reason = "missing split node or pocket edge";
                QueueUturnCleanup(ref request, Entity.Null, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Missing split node or pocket edge splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)}.");
                return false;
            }

            if (!TryFindOuterEdge(request, out Entity outerEdge))
            {
                string reason = "outer edge not found";
                QueueUturnCleanup(ref request, Entity.Null, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot identify outer edge splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)} original={FormatEntity(request.OriginalEdge)} sourcePrefab={FormatEntity(request.SourcePrefab)}.");
                return false;
            }

            request.OuterEdge = outerEdge;
            m_SourceLanes.Clear();
            m_TargetLanes.Clear();
            CollectEdgeCarLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_SourceLanes);
            CollectEdgeCarLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TargetLanes);

            if (m_SourceLanes.Count == 0 || m_TargetLanes.Count == 0)
            {
                string reason = $"missing lane data source={m_SourceLanes.Count} target={m_TargetLanes.Count}";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Missing lane data splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count}.");
                return false;
            }

            if (m_TargetLanes.Count < m_SourceLanes.Count + 1)
            {
                string reason = $"forward lane count mismatch source={m_SourceLanes.Count} target={m_TargetLanes.Count} expectedTargetAtLeast={m_SourceLanes.Count + 1}";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot apply N->N+1 rule splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count}; expected at least one extra target lane.");
                return false;
            }

            float2 travelDirection = m_SourceLanes[0].TravelDirection;
            float2 right = new float2(travelDirection.y, -travelDirection.x);
            float2 sourceOrigin = GetAveragePosition(m_SourceLanes);
            AssignLaneLaterals(m_SourceLanes, sourceOrigin, right);
            AssignLaneLaterals(m_TargetLanes, sourceOrigin, right);

            if (!TrySelectLaneMapping(m_SourceLanes, m_TargetLanes, out List<LaneEndpoint> selectedTargets, out int extraTargetListIndex, out float mappingScore))
            {
                string reason = $"target subset selection failed source={m_SourceLanes.Count} target={m_TargetLanes.Count}";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot select an N->N+1 target subset splitNode={FormatEntity(request.SplitNode)} sourceOrder={FormatLaneOrder(m_SourceLanes)} targetOrder={FormatLaneOrder(m_TargetLanes)}.");
                return false;
            }

            TurnDirection turn = DetermineTurn(selectedTargets, extraTargetListIndex);
            string centerTurnDiagnostic = "not-run";
            bool centerTurnEvidence = false;
            if (TryRefineExtraTargetFromCenterConnectors(
                    request.IntersectionNode,
                    request.PocketEdge,
                    selectedTargets,
                    out int centerExtraTargetListIndex,
                    out TurnDirection centerTurn,
                    out centerTurnDiagnostic))
            {
                centerTurnEvidence = true;
                if (centerExtraTargetListIndex != extraTargetListIndex || centerTurn != turn)
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Center connector turn target overrides split lateral target splitNode={FormatEntity(request.SplitNode)} oldExtra={selectedTargets[extraTargetListIndex].LaneIndex}/{turn} newExtra={selectedTargets[centerExtraTargetListIndex].LaneIndex}/{centerTurn} diagnostics={centerTurnDiagnostic}.");
                }

                extraTargetListIndex = centerExtraTargetListIndex;
                turn = centerTurn;
            }

            if (turn == TurnDirection.Ambiguous)
            {
                string reason = $"ambiguous turn extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot determine turn side splitNode={FormatEntity(request.SplitNode)} selectedTargets={FormatLaneOrder(selectedTargets)} extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}; leaving connectors unchanged.");
                return false;
            }

            int branchSourceListIndex = turn == TurnDirection.Right ? m_SourceLanes.Count - 1 : 0;
            int branchSourceLaneIndex = m_SourceLanes[branchSourceListIndex].LaneIndex;
            int extraTargetLaneIndex = selectedTargets[extraTargetListIndex].LaneIndex;

            CollectConnectorLanes(request.SplitNode, outerEdge, request.PocketEdge, m_ExistingConnectorLanes);
            if (m_ExistingConnectorLanes.Count == 0)
            {
                string reason = "waiting for generated connector template";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Waiting for generated split-node connectors splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count}; direct rebuild needs an existing connector template.");
                return false;
            }

            EnsureTrackSnapshotCaptured(ref request, outerEdge, "capture-before-road-only-mapping");

            if (!TryBuildDesiredMappings(
                    m_SourceLanes,
                    selectedTargets,
                    extraTargetListIndex,
                    branchSourceLaneIndex,
                    m_ExistingConnectorLanes,
                    preferExistingConnectors: !centerTurnEvidence,
                    out LaneMapping[] mappings,
                    out string mappingSource,
                    out string mappingReason))
            {
                QueueUturnCleanup(ref request, outerEdge, $"desired mapping failed: {mappingReason}");
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot build desired lane mapping splitNode={FormatEntity(request.SplitNode)} sourceOrder={FormatLaneOrder(m_SourceLanes)} selectedTargets={FormatLaneOrder(selectedTargets)} extraTarget={extraTargetLaneIndex} branchSource={branchSourceLaneIndex} existing={FormatConnectorLanes(m_ExistingConnectorLanes)} reason={mappingReason}.");
                return false;
            }

            request.Mappings = mappings;
            request.SourceLanes = m_SourceLanes.ToArray();
            request.TargetLanes = selectedTargets.ToArray();
            request.BranchSourceLaneIndex = branchSourceLaneIndex;
            request.ExtraTargetLaneIndex = extraTargetLaneIndex;
            request.Turn = turn;

            if (!TryPrepareReverseMappings(ref request, outerEdge, out string reverseMappingSource, out string reverseMappingReason))
            {
                if (!request.ReverseTrackAuditLogged && request.LaneDataRetries >= MaxLaneDataRetries - 1)
                {
                    LogReverseTrackEndpointAudit(request, outerEdge, reverseMappingReason);
                    request.ReverseTrackAuditLogged = true;
                }

                QueueUturnCleanup(ref request, outerEdge, $"reverse mapping failed: {reverseMappingReason}");
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot prepare reverse split-node mapping splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reverseMappingReason}; leaving connectors unchanged to avoid partial Traffic data.");
                return false;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Prepared Traffic mapping splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} farIntersection={FormatEntity(request.FarIntersectionNode)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count} selectedTargetCount={selectedTargets.Count} mappingScore={mappingScore:0.###} mappingSource={mappingSource} turn={turn} branchSource={branchSourceLaneIndex} extraTarget={extraTargetLaneIndex} centerDiagnostics={centerTurnDiagnostic} existingConnectors={m_ExistingConnectorLanes.Count} existing={FormatConnectorLanes(m_ExistingConnectorLanes)} mappings={FormatMappings(request.Mappings)} reverseSourceCount={request.ReverseSourceLanes?.Length ?? 0} reverseTargetCount={request.ReverseTargetLanes?.Length ?? 0} reverseMappingSource={reverseMappingSource} reverseMappings={FormatMappings(request.ReverseMappings)} trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}) trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackSkippedReason={request.TrackSkippedReason}.");
            return true;
        }

        private bool TryPrepareReverseMappings(
            ref Request request,
            Entity outerEdge,
            out string mappingSource,
            out string reason)
        {
            mappingSource = "none";
            reason = string.Empty;
            m_ReverseSourceLanes.Clear();
            m_ReverseTargetLanes.Clear();

            CollectEdgeCarLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_ReverseSourceLanes);
            CollectEdgeCarLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_ReverseTargetLanes);

            if (m_ReverseSourceLanes.Count == 0 && m_ReverseTargetLanes.Count == 0)
            {
                request.ReverseSourceLanes = Array.Empty<LaneEndpoint>();
                request.ReverseTargetLanes = Array.Empty<LaneEndpoint>();
                request.ReverseMappings = Array.Empty<LaneMapping>();
                mappingSource = "no-reverse-lanes";
                return true;
            }

            if (m_ReverseSourceLanes.Count == 0 || m_ReverseTargetLanes.Count == 0)
            {
                reason = $"one-sided reverse lanes source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count}";
                return false;
            }

            float2 travelDirection = m_ReverseSourceLanes[0].TravelDirection;
            float2 right = new float2(travelDirection.y, -travelDirection.x);
            float2 sourceOrigin = GetAveragePosition(m_ReverseSourceLanes);
            AssignLaneLaterals(m_ReverseSourceLanes, sourceOrigin, right);
            AssignLaneLaterals(m_ReverseTargetLanes, sourceOrigin, right);

            if (request.Mode == RepairMode.BalancedOppositeTarget)
            {
                if (request.FarIntersectionNode == Entity.Null ||
                    !EntityManager.Exists(request.FarIntersectionNode))
                {
                    reason = $"balanced reverse mapping missing far intersection farIntersection={FormatEntity(request.FarIntersectionNode)}";
                    return false;
                }

                if (m_ReverseTargetLanes.Count < m_ReverseSourceLanes.Count + 1)
                {
                    reason = $"balanced reverse lane count mismatch source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count} expectedTargetAtLeast={m_ReverseSourceLanes.Count + 1}";
                    return false;
                }

                if (!TrySelectLaneMapping(m_ReverseSourceLanes, m_ReverseTargetLanes, out List<LaneEndpoint> selectedReverseTargets, out int extraTargetListIndex, out float mappingScore))
                {
                    reason = $"balanced reverse target subset selection failed source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count}";
                    return false;
                }

                TurnDirection turn = DetermineTurn(selectedReverseTargets, extraTargetListIndex);
                string centerTurnDiagnostic = "not-run";
                bool centerTurnEvidence = false;
                if (TryRefineExtraTargetFromCenterConnectors(
                        request.FarIntersectionNode,
                        outerEdge,
                        selectedReverseTargets,
                        out int centerExtraTargetListIndex,
                        out TurnDirection centerTurn,
                        out centerTurnDiagnostic))
                {
                    centerTurnEvidence = true;
                    if (centerExtraTargetListIndex != extraTargetListIndex || centerTurn != turn)
                    {
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far-center connector turn target overrides balanced reverse split lateral target splitNode={FormatEntity(request.SplitNode)} oldExtra={selectedReverseTargets[extraTargetListIndex].LaneIndex}/{turn} newExtra={selectedReverseTargets[centerExtraTargetListIndex].LaneIndex}/{centerTurn} diagnostics={centerTurnDiagnostic}.");
                    }

                    extraTargetListIndex = centerExtraTargetListIndex;
                    turn = centerTurn;
                }

                if (turn == TurnDirection.Ambiguous)
                {
                    reason = $"balanced reverse ambiguous turn extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}";
                    return false;
                }

                int branchSourceListIndex = turn == TurnDirection.Right ? m_ReverseSourceLanes.Count - 1 : 0;
                int branchSourceLaneIndex = m_ReverseSourceLanes[branchSourceListIndex].LaneIndex;
                int extraTargetLaneIndex = selectedReverseTargets[extraTargetListIndex].LaneIndex;

                CollectConnectorLanes(request.SplitNode, request.PocketEdge, outerEdge, m_ExistingConnectorLanes);
                if (m_ExistingConnectorLanes.Count == 0)
                {
                    reason = $"balanced reverse waiting for generated connector template source={m_ReverseSourceLanes.Count} target={m_ReverseTargetLanes.Count}";
                    return false;
                }

                if (!TryBuildDesiredMappings(
                        m_ReverseSourceLanes,
                        selectedReverseTargets,
                        extraTargetListIndex,
                        branchSourceLaneIndex,
                        m_ExistingConnectorLanes,
                        preferExistingConnectors: !centerTurnEvidence,
                        out LaneMapping[] balancedReverseMappings,
                        out string balancedMappingSource,
                        out string balancedMappingReason))
                {
                    reason = $"balanced reverse desired mapping failed: {balancedMappingReason} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} selectedTargets={FormatLaneOrder(selectedReverseTargets)} extraTarget={extraTargetLaneIndex} branchSource={branchSourceLaneIndex} existingReverse={FormatConnectorLanes(m_ExistingConnectorLanes)}";
                    return false;
                }

                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = selectedReverseTargets.ToArray();
                request.ReverseMappings = balancedReverseMappings;
                mappingSource = $"balanced-reverse-{balancedMappingSource}; score={mappingScore:0.###}; turn={turn}; branchSource={branchSourceLaneIndex}; extraTarget={extraTargetLaneIndex}; centerDiagnostics={centerTurnDiagnostic}";
                return true;
            }

            if (request.Mode == RepairMode.ShortEdgeTransition)
            {
                if (!TryBuildSnapshotReverseMappings(
                        request.TransitionReverseSnapshot,
                        m_ReverseSourceLanes,
                        m_ReverseTargetLanes,
                        request.PocketEdge,
                        outerEdge,
                        out LaneMapping[] snapshotReverseMappings,
                        out mappingSource,
                        out string snapshotReason))
                {
                    request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                    request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                    request.ReverseMappings = Array.Empty<LaneMapping>();
                    mappingSource = $"short-edge-transition-reverse-skipped: {snapshotReason}";
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Short-edge transition reverse restore skipped splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} reason={snapshotReason} snapshot={FormatSnapshot(request.TransitionReverseSnapshot)} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
                    return true;
                }

                request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                request.ReverseMappings = snapshotReverseMappings;
                mappingSource = $"short-edge-transition-{mappingSource}";
                return true;
            }

            CollectConnectorLanes(request.SplitNode, request.PocketEdge, outerEdge, m_ExistingConnectorLanes);
            if (!TryBuildStraightMappings(
                    m_ReverseSourceLanes,
                    m_ReverseTargetLanes,
                    m_ExistingConnectorLanes,
                    out LaneMapping[] reverseMappings,
                    out mappingSource,
                    out string buildReason))
            {
                if (TryBuildExistingConnectorSnapshotMappings(
                        m_ReverseSourceLanes,
                        m_ReverseTargetLanes,
                        m_ExistingConnectorLanes,
                        request.PocketEdge,
                        outerEdge,
                        out LaneMapping[] existingSnapshotMappings,
                        out string existingSnapshotSource,
                        out string existingSnapshotReason))
                {
                    request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
                    request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
                    request.ReverseMappings = existingSnapshotMappings;
                    mappingSource = $"reverse-existing-connector-snapshot; straightReason=({buildReason}); {existingSnapshotSource}";
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Standard reverse lane count/rank mapping failed; preserving existing reverse split connectors instead splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} straightReason={buildReason} snapshotReason={existingSnapshotReason} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)} existingReverse={FormatConnectorLanes(m_ExistingConnectorLanes)} reverseMappings={FormatMappings(existingSnapshotMappings)}.");
                    return true;
                }

                reason = $"{buildReason} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)} existingReverse={FormatConnectorLanes(m_ExistingConnectorLanes)}";
                return false;
            }

            request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
            request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
            request.ReverseMappings = reverseMappings;
            return true;
        }

        private void EnsureTrackSnapshotCaptured(ref Request request, Entity outerEdge, string phase)
        {
            if (request.TrackSnapshotCaptured)
            {
                return;
            }

            PrepareTrackPreservationMappings(ref request, outerEdge);
            request.TrackSnapshotCaptured = true;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] CaptureTrackSnapshot stage complete phase={phase} splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}) trackSkippedReason={request.TrackSkippedReason}.");
        }

        private static void ResetTrackSnapshot(ref Request request)
        {
            request.TrackSnapshotCaptured = false;
            request.TrackForwardSourceLanes = null;
            request.TrackForwardTargetLanes = null;
            request.TrackReverseSourceLanes = null;
            request.TrackReverseTargetLanes = null;
            request.TrackForwardMappings = null;
            request.TrackReverseMappings = null;
            request.TrackSkippedReason = null;
        }

        private void PrepareTrackPreservationMappings(ref Request request, Entity outerEdge)
        {
            m_TrackSourceLanes.Clear();
            m_TrackTargetLanes.Clear();
            m_TrackReverseSourceLanes.Clear();
            m_TrackReverseTargetLanes.Clear();

            CollectEdgeTrackLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_TrackSourceLanes);
            CollectEdgeTrackLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TrackTargetLanes);
            CollectEdgeTrackLaneEndpoints(request.PocketEdge, request.SplitNode, EndpointRole.SourceEndAtNode, m_TrackReverseSourceLanes);
            CollectEdgeTrackLaneEndpoints(outerEdge, request.SplitNode, EndpointRole.TargetStartAtNode, m_TrackReverseTargetLanes);

            List<string> skipped = new List<string>(8);
            TrackMappingStats forwardStats = BuildTrackMappingsFromExistingConnectors(
                request.SplitNode,
                outerEdge,
                request.PocketEdge,
                m_TrackSourceLanes,
                m_TrackTargetLanes,
                "trackForward",
                skipped,
                out LaneMapping[] trackForwardMappings);
            TrackMappingStats reverseStats = BuildTrackMappingsFromExistingConnectors(
                request.SplitNode,
                request.PocketEdge,
                outerEdge,
                m_TrackReverseSourceLanes,
                m_TrackReverseTargetLanes,
                "trackReverse",
                skipped,
                out LaneMapping[] trackReverseMappings);

            request.TrackForwardSourceLanes = m_TrackSourceLanes.ToArray();
            request.TrackForwardTargetLanes = m_TrackTargetLanes.ToArray();
            request.TrackReverseSourceLanes = m_TrackReverseSourceLanes.ToArray();
            request.TrackReverseTargetLanes = m_TrackReverseTargetLanes.ToArray();
            request.TrackForwardMappings = trackForwardMappings;
            request.TrackReverseMappings = trackReverseMappings;
            request.TrackSkippedReason = skipped.Count == 0 ? "none" : FormatStringList(skipped);

            LogSplitTrackEndpointAudit(request, outerEdge, forwardStats, reverseStats);
        }

        private TrackMappingStats BuildTrackMappingsFromExistingConnectors(
            Entity splitNode,
            Entity sourceEdge,
            Entity targetEdge,
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            IReadOnlyList<LaneEndpoint> targetEndpoints,
            string direction,
            List<string> skipped,
            out LaneMapping[] mappings)
        {
            mappings = Array.Empty<LaneMapping>();
            TrackMappingStats stats = default;
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                skipped.Add($"{direction}:trackSkippedReason=noSubLaneBuffer");
                return stats;
            }

            CollectTrackConnectorLanes(splitNode, sourceEdge, targetEdge, subLanes, m_TrackConnectorLanes);
            stats.Connectors = m_TrackConnectorLanes.Count;
            if (m_TrackConnectorLanes.Count == 0)
            {
                return stats;
            }

            m_TrackMappings.Clear();
            HashSet<ConnectionKey> used = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                ConnectionKey key = new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex);
                if (used.Contains(key))
                {
                    stats.Skipped++;
                    skipped.Add($"{direction}:trackSkippedReason=duplicateConnector source={connector.SourceLaneIndex} target={connector.TargetLaneIndex} entity={FormatEntity(connector.Entity)}");
                    continue;
                }

                if ((connector.PathMethods & PathMethod.Track) == 0)
                {
                    stats.Skipped++;
                    skipped.Add($"{direction}:trackSkippedReason=connectorMissingTrackMethod source={connector.SourceLaneIndex} target={connector.TargetLaneIndex} methods=[{connector.PathMethods}] entity={FormatEntity(connector.Entity)}");
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceEndpoints, connector.SourceLaneIndex, out LaneEndpoint sourceEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:trackSkippedReason=sourceEndpointMissing edge={FormatEntity(sourceEdge)} lane={connector.SourceLaneIndex} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                if (!TryFindLaneEndpoint(targetEndpoints, connector.TargetLaneIndex, out LaneEndpoint targetEndpoint))
                {
                    stats.EndpointMisses++;
                    skipped.Add($"{direction}:trackSkippedReason=targetEndpointMissing edge={FormatEntity(targetEdge)} lane={connector.TargetLaneIndex} connector={FormatEntity(connector.Entity)}");
                    continue;
                }

                PathMethod method = GetTrackPreservationMethod(connector, sourceEndpoint, targetEndpoint);
                if ((method & PathMethod.Track) == 0)
                {
                    stats.Skipped++;
                    skipped.Add($"{direction}:trackSkippedReason=methodWithoutTrack source={connector.SourceLaneIndex} target={connector.TargetLaneIndex} computed=[{method}] connectorMethods=[{connector.PathMethods}]");
                    continue;
                }

                used.Add(key);
                if (IsTrackOnlyEndpoint(targetEndpoint))
                {
                    stats.TrackOnlyTargets++;
                }

                if ((method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                {
                    stats.SharedTrackConnections++;
                }

                m_TrackMappings.Add(new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = connector.SourceLaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    Method = method,
                    IsBranch = false,
                    IsTrackPreservation = true,
                    TemplateEntity = connector.Entity,
                    TemplatePathMethods = connector.PathMethods
                });
            }

            stats.Mappings = m_TrackMappings.Count;
            mappings = m_TrackMappings.ToArray();
            return stats;
        }

        private static PathMethod GetTrackPreservationMethod(ConnectorLane connector, LaneEndpoint sourceEndpoint, LaneEndpoint targetEndpoint)
        {
            PathMethod method = connector.PathMethods & (PathMethod.Road | PathMethod.Track);
            if (!SupportsRoadPath(sourceEndpoint) || !SupportsRoadPath(targetEndpoint))
            {
                method &= ~PathMethod.Road;
            }

            return (method & PathMethod.Track) != 0 ? method : 0;
        }

        private void LogSplitTrackEndpointAudit(Request request, Entity outerEdge, TrackMappingStats forwardStats, TrackMappingStats reverseStats)
        {
            string trackForwardSource = FormatEdgeTrackLaneEndpointAudit(
                outerEdge,
                request.SplitNode,
                EndpointRole.SourceEndAtNode);
            string trackForwardTarget = FormatEdgeTrackLaneEndpointAudit(
                request.PocketEdge,
                request.SplitNode,
                EndpointRole.TargetStartAtNode);
            string trackReverseSource = FormatEdgeTrackLaneEndpointAudit(
                request.PocketEdge,
                request.SplitNode,
                EndpointRole.SourceEndAtNode);
            string trackReverseTarget = FormatEdgeTrackLaneEndpointAudit(
                outerEdge,
                request.SplitNode,
                EndpointRole.TargetStartAtNode);
            string splitTrackConnectors = FormatSplitNodeTrackConnectorAudit(
                request.SplitNode,
                outerEdge,
                request.PocketEdge);

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Split track endpoint audit splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} trackForwardSource=({trackForwardSource}) trackForwardTarget=({trackForwardTarget}) trackReverseSource=({trackReverseSource}) trackReverseTarget=({trackReverseTarget}) trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackSkippedReason={request.TrackSkippedReason} trackForwardStats=connectors:{forwardStats.Connectors},mappings:{forwardStats.Mappings},endpointMisses:{forwardStats.EndpointMisses},skipped:{forwardStats.Skipped},trackOnlyTargets:{forwardStats.TrackOnlyTargets},sharedTrackConnections:{forwardStats.SharedTrackConnections} trackReverseStats=connectors:{reverseStats.Connectors},mappings:{reverseStats.Mappings},endpointMisses:{reverseStats.EndpointMisses},skipped:{reverseStats.Skipped},trackOnlyTargets:{reverseStats.TrackOnlyTargets},sharedTrackConnections:{reverseStats.SharedTrackConnections} splitTrackConnectors=({splitTrackConnectors}).");
        }

        private void LogReverseTrackEndpointAudit(Request request, Entity outerEdge, string reason)
        {
            string reverseSourceTrack = FormatEdgeTrackLaneEndpointAudit(
                request.PocketEdge,
                request.SplitNode,
                EndpointRole.SourceEndAtNode);
            string reverseTargetTrack = FormatEdgeTrackLaneEndpointAudit(
                outerEdge,
                request.SplitNode,
                EndpointRole.TargetStartAtNode);
            string splitTrackConnectors = FormatSplitNodeTrackConnectorAudit(
                request.SplitNode,
                outerEdge,
                request.PocketEdge);

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Reverse track endpoint audit splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reason} reverseSourceTrack=({reverseSourceTrack}) reverseTargetTrack=({reverseTargetTrack}) splitTrackConnectors=({splitTrackConnectors}).");
        }

        private void QueueUturnCleanup(ref Request request, Entity outerEdge, string reason)
        {
            if (outerEdge != Entity.Null)
            {
                request.OuterEdge = outerEdge;
            }

            request.UturnCleanupPending = true;
            request.UturnCleanupReason = reason;
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Queued post-lane U-turn cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reason}.");
        }

        private int DeleteStaleSplitNodeUturnConnectorLanes(Request request, Entity outerEdge, string reason)
        {
            if (!EntityManager.TryGetBuffer(request.SplitNode, false, out DynamicBuffer<SubLane> subLanes))
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cannot delete stale split-node U-turn connectors after skip: split node has no SubLane buffer splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} persistentTrafficWrite=False persistentReason=noSubLaneBuffer reason={reason}.");
                return 0;
            }

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, outerEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            bool mandatoryRewriteAfterRoadSkip = request.RemoveAfterUturnCleanup &&
                                                 !request.TrafficWritten &&
                                                 outerEdge != Entity.Null;
            if (m_StaleConnectorLanes.Count == 0 && !mandatoryRewriteAfterRoadSkip)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] No stale split-node U-turn connectors found after skip splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} persistentTrafficWrite=False persistentReason=noStaleUturnConnectors reason={reason}.");
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

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Deleted stale split-node U-turn connectors after skip splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} deletedUturn={m_StaleConnectorLanes.Count} removedSubLanes={removedSubLanes} mandatoryRewriteAfterRoadSkip={mandatoryRewriteAfterRoadSkip} persistentTrafficWrite={persistentTrafficWritten} persistentSources={persistentStats.WrittenSources} persistentStaleSources={persistentStats.StaleSourceLanes} persistentKept={persistentStats.PreservedConnections} persistentRuntimeTrackKept={persistentStats.PreservedTrackConnections} persistentTrackWrittenConnections={persistentStats.TrackWrittenConnections} persistentTrackSnapshotConnections={persistentStats.TrackSnapshotConnections} persistentTrackSnapshotSkipped={persistentStats.TrackSnapshotSkipped} persistentTrackOnlyTargets={persistentStats.TrackOnlyTargetConnections} persistentSharedTrackConnections={persistentStats.SharedTrackConnections} persistentEmptySources={persistentStats.EmptySources} persistentNormalizedMethods={persistentStats.NormalizedMethods} persistentRemovedExisting={persistentStats.RemovedExisting} persistentReason={persistentStats.Reason} staleSourceLanes={persistentStats.SourceLanes} rewriteSourceLanes={persistentStats.RewriteSourceLanes} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackSkippedReason={request.TrackSkippedReason} reason={reason} connectors={staleSummary} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
            return m_StaleConnectorLanes.Count;
        }

        private static void PopulateStaleSourceStats(IReadOnlyList<ConnectorLane> staleUturns, ref UturnCleanupWriteStats stats)
        {
            HashSet<SourceLaneKey> staleSourceKeys = new HashSet<SourceLaneKey>();
            if (staleUturns != null)
            {
                for (int i = 0; i < staleUturns.Count; i++)
                {
                    ConnectorLane connector = staleUturns[i];
                    staleSourceKeys.Add(new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex));
                }
            }

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

            HashSet<SourceLaneKey> staleSourceKeys = new HashSet<SourceLaneKey>();
            if (staleUturns != null)
            {
                for (int i = 0; i < staleUturns.Count; i++)
                {
                    ConnectorLane connector = staleUturns[i];
                    staleSourceKeys.Add(new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex));
                }
            }

            stats.StaleSourceLanes = staleSourceKeys.Count;
            stats.SourceLanes = FormatSourceLaneKeys(staleSourceKeys);

            CollectSplitNodeConnectorLanes(request.SplitNode, outerEdge, request.PocketEdge, subLanes, m_ConnectorLanes);
            HashSet<SourceLaneKey> rewriteSourceKeys = new HashSet<SourceLaneKey>(staleSourceKeys);
            HashSet<SourceLaneKey> currentConnectorSourceKeys = new HashSet<SourceLaneKey>();
            HashSet<SourceLaneKey> trackSnapshotSourceKeys = new HashSet<SourceLaneKey>();
            Dictionary<SourceLaneKey, List<ConnectorLane>> nonUturnBySource = new Dictionary<SourceLaneKey, List<ConnectorLane>>();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                SourceLaneKey sourceKey = new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex);
                rewriteSourceKeys.Add(sourceKey);
                currentConnectorSourceKeys.Add(sourceKey);

                if (connector.SourceEdge == connector.TargetEdge)
                {
                    continue;
                }

                if (!nonUturnBySource.TryGetValue(sourceKey, out List<ConnectorLane> connectors))
                {
                    connectors = new List<ConnectorLane>(2);
                    nonUturnBySource.Add(sourceKey, connectors);
                }

                connectors.Add(connector);
            }

            AddTrackSnapshotSourceKeys(request.TrackForwardMappings, trackSnapshotSourceKeys);
            AddTrackSnapshotSourceKeys(request.TrackReverseMappings, trackSnapshotSourceKeys);
            foreach (SourceLaneKey sourceKey in trackSnapshotSourceKeys)
            {
                rewriteSourceKeys.Add(sourceKey);
            }

            m_UturnCleanupSourcePlans.Clear();
            m_UturnCleanupConnectionPlans.Clear();
            stats.RewriteSourceLanes = FormatSourceLaneKeys(rewriteSourceKeys);
            foreach (SourceLaneKey sourceKey in rewriteSourceKeys.OrderBy(key => key.Edge.Index).ThenBy(key => key.LaneIndex))
            {
                if (!TryFindCleanupLaneEndpoint(
                        request.SplitNode,
                        sourceKey.Edge,
                        sourceKey.LaneIndex,
                        EndpointRole.SourceEndAtNode,
                        out LaneEndpoint sourceEndpoint))
                {
                    stats.EndpointMisses++;
                    if (trackSnapshotSourceKeys.Contains(sourceKey) &&
                        !staleSourceKeys.Contains(sourceKey) &&
                        !currentConnectorSourceKeys.Contains(sourceKey))
                    {
                        stats.TrackSnapshotSkipped++;
                        Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cleanup-only track snapshot skipped splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex} trackSkippedReason=cleanupTrackSourceEndpointMissing.");
                        continue;
                    }

                    stats.Reason = $"sourceEndpointMissing edge={FormatEntity(sourceKey.Edge)} lane={sourceKey.LaneIndex}";
                    return false;
                }

                int firstConnection = m_UturnCleanupConnectionPlans.Count;
                Dictionary<TargetLaneKey, int> plannedByTarget = new Dictionary<TargetLaneKey, int>();
                if (nonUturnBySource.TryGetValue(sourceKey, out List<ConnectorLane> keepConnectors))
                {
                    for (int i = 0; i < keepConnectors.Count; i++)
                    {
                        ConnectorLane connector = keepConnectors[i];
                        if (!TryFindCleanupLaneEndpoint(
                                request.SplitNode,
                                connector.TargetEdge,
                                connector.TargetLaneIndex,
                                EndpointRole.TargetStartAtNode,
                                out LaneEndpoint targetEndpoint))
                        {
                            stats.EndpointMisses++;
                            stats.Reason = $"targetEndpointMissing edge={FormatEntity(connector.TargetEdge)} lane={connector.TargetLaneIndex} source={FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex}";
                            return false;
                        }

                        PathMethod method = SanitizeTrafficPathMethod(connector.PathMethods);
                        if (method != connector.PathMethods)
                        {
                            stats.NormalizedMethods++;
                        }

                        if ((method & PathMethod.Track) != 0)
                        {
                            stats.PreservedTrackConnections++;
                        }

                        AddOrMergeUturnCleanupConnectionPlan(
                            sourceKey,
                            connector.TargetEdge,
                            connector.TargetLaneIndex,
                            targetEndpoint,
                            method,
                            (connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0,
                            fromTrackSnapshot: false,
                            plannedByTarget,
                            ref stats);
                    }
                }

                AppendTrackSnapshotCleanupMappings(
                    request,
                    sourceKey,
                    request.TrackForwardMappings,
                    plannedByTarget,
                    ref stats);
                AppendTrackSnapshotCleanupMappings(
                    request,
                    sourceKey,
                    request.TrackReverseMappings,
                    plannedByTarget,
                    ref stats);

                int connectionCount = m_UturnCleanupConnectionPlans.Count - firstConnection;
                if (connectionCount == 0)
                {
                    stats.EmptySources++;
                }

                bool sourceRequiredForSuppression = staleSourceKeys.Contains(sourceKey) ||
                                                    currentConnectorSourceKeys.Contains(sourceKey);
                if (connectionCount == 0 &&
                    trackSnapshotSourceKeys.Contains(sourceKey) &&
                    !sourceRequiredForSuppression)
                {
                    continue;
                }

                m_UturnCleanupSourcePlans.Add(new UturnCleanupSourcePlan
                {
                    Key = sourceKey,
                    Source = sourceEndpoint,
                    FirstConnection = firstConnection,
                    ConnectionCount = connectionCount
                });
            }

            if (m_UturnCleanupSourcePlans.Count == 0)
            {
                stats.Reason = "noWritableSourceEndpoints";
                return false;
            }

            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, request.SplitNode);
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
                Entity modifiedConnectionEntity = EntityManager.CreateEntity();
                trafficApi.AddDataOwner(EntityManager, modifiedConnectionEntity, request.SplitNode);
                trafficApi.AddFakePrefabRef(EntityManager, modifiedConnectionEntity);
                object generatedBuffer = trafficApi.AddGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity);

                for (int j = 0; j < sourcePlan.ConnectionCount; j++)
                {
                    UturnCleanupConnectionPlan connectionPlan = m_UturnCleanupConnectionPlans[sourcePlan.FirstConnection + j];
                    LaneEndpoint targetEndpoint = connectionPlan.Target;
                    trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                        sourcePlan.Key.Edge,
                        connectionPlan.TargetEdge,
                        sourcePlan.Key.LaneIndex,
                        connectionPlan.TargetLaneIndex,
                        new float3x2(
                            sourcePlan.Source.LanePosition,
                            targetEndpoint.LanePosition),
                        new int4(
                            sourcePlan.Source.CarriagewayAndGroup,
                            targetEndpoint.CarriagewayAndGroup),
                        connectionPlan.Method,
                        connectionPlan.IsUnsafe));
                    stats.PreservedConnections++;
                    if ((connectionPlan.Method & PathMethod.Track) != 0)
                    {
                        stats.TrackWrittenConnections++;
                        if (IsTrackOnlyEndpoint(targetEndpoint))
                        {
                            stats.TrackOnlyTargetConnections++;
                        }

                        if ((connectionPlan.Method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
                        {
                            stats.SharedTrackConnections++;
                        }
                    }

                    if (connectionPlan.FromTrackSnapshot)
                    {
                        stats.TrackSnapshotConnections++;
                    }
                }

                trafficApi.AddBufferElement(modifiedBuffer, trafficApi.CreateModifiedLaneConnection(
                    sourcePlan.Key.LaneIndex,
                    sourcePlan.Source.CarriagewayAndGroup,
                    sourcePlan.Source.LanePosition,
                    sourcePlan.Key.Edge,
                    modifiedConnectionEntity));
                stats.WrittenSources++;
            }

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, request.SplitNode);
            MarkForLaneRebuild(request);
            stats.Reason = allowRewriteWithoutStaleUturns ? "okMandatoryRewriteAfterRoadSkip" : "ok";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Wrote cleanup-only Traffic U-turn suppression splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} allowRewriteWithoutStaleUturns={allowRewriteWithoutStaleUturns} staleSourceLanes={stats.SourceLanes} rewriteSourceLanes={stats.RewriteSourceLanes} writtenSources={stats.WrittenSources} preservedNonUturn={stats.PreservedConnections} preservedRuntimeTrack={stats.PreservedTrackConnections} trackWrittenConnections={stats.TrackWrittenConnections} trackSnapshotConnections={stats.TrackSnapshotConnections} trackSnapshotSkipped={stats.TrackSnapshotSkipped} trackOnlyTargets={stats.TrackOnlyTargetConnections} sharedTrackConnections={stats.SharedTrackConnections} emptySources={stats.EmptySources} normalizedMethods={stats.NormalizedMethods} removedExisting={stats.RemovedExisting} reason={reason} staleConnectors={FormatConnectorLanes(staleUturns)} preservedConnectors={FormatConnectorLanes(m_ConnectorLanes)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] trackSkippedReason={request.TrackSkippedReason}.");
            return true;
        }

        private static void AddTrackSnapshotSourceKeys(LaneMapping[] mappings, HashSet<SourceLaneKey> output)
        {
            if (mappings == null)
            {
                return;
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                if ((mapping.Method & PathMethod.Track) == 0)
                {
                    continue;
                }

                output.Add(new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex));
            }
        }

        private void AppendTrackSnapshotCleanupMappings(
            Request request,
            SourceLaneKey sourceKey,
            LaneMapping[] mappings,
            Dictionary<TargetLaneKey, int> plannedByTarget,
            ref UturnCleanupWriteStats stats)
        {
            if (mappings == null || mappings.Length == 0)
            {
                return;
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                if (mapping.SourceEdge != sourceKey.Edge ||
                    mapping.SourceLaneIndex != sourceKey.LaneIndex)
                {
                    continue;
                }

                PathMethod method = SanitizeTrafficPathMethod(mapping.Method);
                if ((method & PathMethod.Track) == 0)
                {
                    stats.TrackSnapshotSkipped++;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cleanup-only track snapshot skipped splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} trackSkippedReason=cleanupTrackMethodMissing.");
                    continue;
                }

                if (!TryFindMappingEndpoint(
                        request,
                        mapping.TargetEdge,
                        mapping.TargetLaneIndex,
                        source: false,
                        out LaneEndpoint targetEndpoint))
                {
                    stats.EndpointMisses++;
                    stats.TrackSnapshotSkipped++;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cleanup-only track snapshot skipped splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} trackSkippedReason=cleanupTrackTargetEndpointMissing.");
                    continue;
                }

                AddOrMergeUturnCleanupConnectionPlan(
                    sourceKey,
                    mapping.TargetEdge,
                    mapping.TargetLaneIndex,
                    targetEndpoint,
                    method,
                    isUnsafe: false,
                    fromTrackSnapshot: true,
                    plannedByTarget,
                    ref stats);
            }
        }

        private void AddOrMergeUturnCleanupConnectionPlan(
            SourceLaneKey sourceKey,
            Entity targetEdge,
            int targetLaneIndex,
            LaneEndpoint targetEndpoint,
            PathMethod method,
            bool isUnsafe,
            bool fromTrackSnapshot,
            Dictionary<TargetLaneKey, int> plannedByTarget,
            ref UturnCleanupWriteStats stats)
        {
            method = SanitizeTrafficPathMethod(method);
            TargetLaneKey targetKey = new TargetLaneKey(targetEdge, targetLaneIndex);
            if (plannedByTarget.TryGetValue(targetKey, out int existingIndex))
            {
                UturnCleanupConnectionPlan existing = m_UturnCleanupConnectionPlans[existingIndex];
                PathMethod mergedMethod = SanitizeTrafficPathMethod(existing.Method | method);
                if (mergedMethod != existing.Method)
                {
                    stats.NormalizedMethods++;
                }

                existing.Method = mergedMethod;
                existing.IsUnsafe |= isUnsafe;
                existing.FromTrackSnapshot |= fromTrackSnapshot;
                m_UturnCleanupConnectionPlans[existingIndex] = existing;
                return;
            }

            plannedByTarget.Add(targetKey, m_UturnCleanupConnectionPlans.Count);
            m_UturnCleanupConnectionPlans.Add(new UturnCleanupConnectionPlan
            {
                TargetEdge = targetEdge,
                TargetLaneIndex = targetLaneIndex,
                Target = targetEndpoint,
                Method = method,
                IsUnsafe = isUnsafe,
                FromTrackSnapshot = fromTrackSnapshot
            });
        }

        private bool TryFindOuterEdge(Request request, out Entity outerEdge)
        {
            outerEdge = Entity.Null;
            if (request.OuterEdge != Entity.Null &&
                EntityManager.Exists(request.OuterEdge) &&
                !EntityManager.HasComponent<Deleted>(request.OuterEdge) &&
                EntityManager.TryGetComponent(request.OuterEdge, out NetEdge explicitEdge) &&
                (explicitEdge.m_Start == request.SplitNode || explicitEdge.m_End == request.SplitNode))
            {
                outerEdge = request.OuterEdge;
                return true;
            }

            if (!EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            float bestScore = float.MinValue;
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (edgeEntity == request.PocketEdge ||
                    edgeEntity == Entity.Null ||
                    !EntityManager.Exists(edgeEntity) ||
                    EntityManager.HasComponent<Deleted>(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) ||
                    !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool connectsSplit = edge.m_Start == request.SplitNode || edge.m_End == request.SplitNode;
                if (!connectsSplit)
                {
                    continue;
                }

                float score = 0f;
                if (edgeEntity == request.OriginalEdge)
                {
                    score += 1000f;
                }

                if (prefabRef.m_Prefab == request.SourcePrefab)
                {
                    score += 100f;
                }

                Entity otherNode = edge.m_Start == request.SplitNode ? edge.m_End : edge.m_Start;
                if (otherNode != request.IntersectionNode)
                {
                    score += 10f;
                }

                if (EntityManager.TryGetComponent(edgeEntity, out Curve curve))
                {
                    score += math.min(curve.m_Length, 100f) * 0.01f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    outerEdge = edgeEntity;
                }
            }

            return outerEdge != Entity.Null;
        }

        private void CollectEdgeCarLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output)
        {
            CollectEdgeLaneEndpoints(edgeEntity, splitNode, role, output, includeTrackOnly: false);
        }

        private void CollectEdgeTrafficLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output)
        {
            CollectEdgeLaneEndpoints(edgeEntity, splitNode, role, output, includeTrackOnly: true);
        }

        private void CollectEdgeTrackLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output)
        {
            CollectEdgeLaneEndpoints(edgeEntity, splitNode, role, output, includeTrackOnly: true, trackCandidateMode: true);
        }

        private void CollectEdgeLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output,
            bool includeTrackOnly,
            bool trackCandidateMode = false)
        {
            output.Clear();

            if (!EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Composition composition) ||
                !EntityManager.TryGetComponent(edgeEntity, out EdgeGeometry edgeGeometry) ||
                !EntityManager.TryGetComponent(composition.m_Edge, out NetCompositionData compositionData) ||
                !EntityManager.TryGetBuffer(composition.m_Edge, true, out DynamicBuffer<NetCompositionLane> compositionLanes) ||
                !EntityManager.TryGetBuffer(edgeEntity, true, out DynamicBuffer<SubLane> subLanes))
            {
                return;
            }

            bool splitIsStart = edge.m_Start == splitNode;
            bool splitIsEnd = edge.m_End == splitNode;
            if (!splitIsStart && !splitIsEnd)
            {
                return;
            }

            bool isEnd = splitIsEnd;
            float endpointDelta = isEnd ? 1f : 0f;
            if (isEnd)
            {
                edgeGeometry.m_Start.m_Left = MathUtils.Invert(edgeGeometry.m_End.m_Right);
                edgeGeometry.m_Start.m_Right = MathUtils.Invert(edgeGeometry.m_End.m_Left);
            }

            bool[] visitedCompositionLanes = new bool[compositionLanes.Length];
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if (laneEntity == Entity.Null ||
                    (subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !EntityManager.TryGetComponent(laneEntity, out EdgeLane edgeLane) ||
                    !EntityManager.TryGetComponent(laneEntity, out Curve curve) ||
                    !EntityManager.TryGetComponent(laneEntity, out PrefabRef lanePrefab) ||
                    !EntityManager.TryGetComponent(lanePrefab.m_Prefab, out NetLaneData laneData))
                {
                    continue;
                }

                bool hasCarLaneData = EntityManager.TryGetComponent(lanePrefab.m_Prefab, out CarLaneData carLaneData);
                bool hasTrackLaneData = EntityManager.TryGetComponent(lanePrefab.m_Prefab, out TrackLaneData trackLaneData);
                bool hasNetTrackLane = EntityManager.HasComponent<NetTrackLane>(laneEntity);
                bool hasSecondaryLane = EntityManager.HasComponent<Game.Net.SecondaryLane>(laneEntity);
                bool laneFlagsTrack = (laneData.m_Flags & LaneFlags.Track) != 0;
                bool hasTrackPathMethod = (subLane.m_PathMethods & PathMethod.Track) != 0;
                bool hasTrackEvidence = hasTrackPathMethod || hasTrackLaneData || hasNetTrackLane;
                bool isCarRoadLane = (subLane.m_PathMethods & PathMethod.Road) != 0 &&
                                     (laneData.m_Flags & LaneFlags.Road) != 0 &&
                                     hasCarLaneData &&
                                     (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
                bool isTrackLane = includeTrackOnly &&
                                   laneFlagsTrack &&
                                   hasTrackEvidence;
                bool includeLane = trackCandidateMode ? isTrackLane : isCarRoadLane || isTrackLane;
                if (!includeLane ||
                    (hasSecondaryLane && !isTrackLane) ||
                    (laneData.m_Flags & (LaneFlags.Utility | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.ParkingLeft | LaneFlags.ParkingRight)) != 0)
                {
                    continue;
                }

                bool startAtSplit = math.abs(edgeLane.m_EdgeDelta.x - endpointDelta) <= 0.001f;
                bool endAtSplit = math.abs(edgeLane.m_EdgeDelta.y - endpointDelta) <= 0.001f;
                if (!startAtSplit && !endAtSplit)
                {
                    continue;
                }

                bool isSourceEndpoint = endAtSplit;
                if ((role == EndpointRole.SourceEndAtNode && !isSourceEndpoint) ||
                    (role == EndpointRole.TargetStartAtNode && isSourceEndpoint))
                {
                    continue;
                }

                if (isSourceEndpoint)
                {
                    curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                }

                if (!TryFindTrafficCompositionLane(
                        laneEntity,
                        curve,
                        laneData,
                        compositionData,
                        compositionLanes,
                        edgeGeometry,
                        isEnd,
                        isSourceEndpoint,
                        visitedCompositionLanes,
                        out NetCompositionLane compositionLane,
                        out float order))
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Skipped lane endpoint without Traffic composition match edge={FormatEntity(edgeEntity)} splitNode={FormatEntity(splitNode)} lane={FormatEntity(laneEntity)} role={role} edgeDelta={edgeLane.m_EdgeDelta} laneFlags={laneData.m_Flags} methods={subLane.m_PathMethods} includeTrackOnly={includeTrackOnly} trackCandidateMode={trackCandidateMode} hasCarData={hasCarLaneData} hasTrackData={hasTrackLaneData} netTrack={hasNetTrackLane} secondary={hasSecondaryLane} trackTypes={trackLaneData.m_TrackTypes}.");
                    continue;
                }

                float3 tangent = MathUtils.StartTangent(curve.m_Bezier);
                float2 travelDirection = -tangent.xz;
                if (math.lengthsq(travelDirection) <= 0.0001f)
                {
                    continue;
                }

                travelDirection = math.normalize(travelDirection);
                PathNode pathNode = isSourceEndpoint ? lane.m_EndNode : lane.m_StartNode;
                PathNode oppositePathNode = isSourceEndpoint ? lane.m_StartNode : lane.m_EndNode;
                NetCarLane carLane = EntityManager.TryGetComponent(laneEntity, out NetCarLane laneComponent)
                    ? laneComponent
                    : default;
                output.Add(new LaneEndpoint
                {
                    LaneEntity = laneEntity,
                    Edge = edgeEntity,
                    LaneIndex = pathNode.GetLaneIndex() & 0xff,
                    OppositeLaneIndex = oppositePathNode.GetLaneIndex() & 0xff,
                    PathNode = pathNode,
                    OppositePathNode = oppositePathNode,
                    Position = curve.m_Bezier.a,
                    LanePosition = compositionLane.m_Position,
                    TravelDirection = travelDirection,
                    CarriagewayAndGroup = new int2(compositionLane.m_Carriageway, compositionLane.m_Group),
                    Lateral = order,
                    Endpoint = isSourceEndpoint ? "E" : "S",
                    PathMethods = subLane.m_PathMethods,
                    LaneFlags = compositionLane.m_Flags,
                    CarFlags = carLane.m_Flags,
                    RoadTypes = hasCarLaneData ? carLaneData.m_RoadTypes : default,
                    TrackTypes = hasTrackLaneData ? trackLaneData.m_TrackTypes : default,
                    HasCarLaneData = hasCarLaneData,
                    HasTrackLaneData = hasTrackLaneData,
                    HasNetTrackLane = hasNetTrackLane
                });
            }
        }

        private string FormatEdgeTrackLaneEndpointAudit(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role)
        {
            if (!EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Composition composition) ||
                !EntityManager.TryGetComponent(edgeEntity, out EdgeGeometry edgeGeometry) ||
                !EntityManager.TryGetComponent(composition.m_Edge, out NetCompositionData compositionData) ||
                !EntityManager.TryGetBuffer(composition.m_Edge, true, out DynamicBuffer<NetCompositionLane> compositionLanes) ||
                !EntityManager.TryGetBuffer(edgeEntity, true, out DynamicBuffer<SubLane> subLanes))
            {
                return $"edge={FormatEntity(edgeEntity)} role={role} unavailable";
            }

            bool splitIsStart = edge.m_Start == splitNode;
            bool splitIsEnd = edge.m_End == splitNode;
            if (!splitIsStart && !splitIsEnd)
            {
                return $"edge={FormatEntity(edgeEntity)} role={role} notConnectedToSplit";
            }

            bool isEnd = splitIsEnd;
            float endpointDelta = isEnd ? 1f : 0f;
            if (isEnd)
            {
                edgeGeometry.m_Start.m_Left = MathUtils.Invert(edgeGeometry.m_End.m_Right);
                edgeGeometry.m_Start.m_Right = MathUtils.Invert(edgeGeometry.m_End.m_Left);
            }

            bool[] visitedCompositionLanes = new bool[compositionLanes.Length];
            List<string> samples = new List<string>(12);
            int candidates = 0;
            int roleMatches = 0;
            int compositionMatches = 0;
            int trackOnly = 0;
            int noCarData = 0;
            int netTrack = 0;
            int netCar = 0;
            int secondary = 0;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                bool hasLane = EntityManager.TryGetComponent(laneEntity, out Lane lane);
                bool hasEdgeLane = EntityManager.TryGetComponent(laneEntity, out EdgeLane edgeLane);
                bool hasCurve = EntityManager.TryGetComponent(laneEntity, out Curve curve);
                PrefabRef lanePrefab = default;
                NetLaneData laneData = default;
                CarLaneData carLaneData = default;
                TrackLaneData trackLaneData = default;
                bool hasPrefab = EntityManager.TryGetComponent(laneEntity, out lanePrefab);
                bool hasLaneData = hasPrefab && EntityManager.TryGetComponent(lanePrefab.m_Prefab, out laneData);
                bool hasCarLaneData = hasPrefab && EntityManager.TryGetComponent(lanePrefab.m_Prefab, out carLaneData);
                bool hasTrackLaneData = hasPrefab && EntityManager.TryGetComponent(lanePrefab.m_Prefab, out trackLaneData);
                bool hasNetTrackLane = EntityManager.HasComponent<NetTrackLane>(laneEntity);
                bool hasNetCarLane = EntityManager.HasComponent<NetCarLane>(laneEntity);
                bool hasSecondaryLane = EntityManager.HasComponent<Game.Net.SecondaryLane>(laneEntity);
                bool hasTrackMethod = (subLane.m_PathMethods & PathMethod.Track) != 0;
                bool hasRoadMethod = (subLane.m_PathMethods & PathMethod.Road) != 0;
                bool laneFlagsTrack = hasLaneData && (laneData.m_Flags & LaneFlags.Track) != 0;

                if (!hasTrackMethod && !laneFlagsTrack && !hasTrackLaneData && !hasNetTrackLane)
                {
                    continue;
                }

                candidates++;
                if (hasNetTrackLane)
                {
                    netTrack++;
                }

                if (hasNetCarLane)
                {
                    netCar++;
                }

                if (hasSecondaryLane)
                {
                    secondary++;
                }

                if (!hasCarLaneData)
                {
                    noCarData++;
                }

                if ((hasTrackMethod && !hasRoadMethod) || (hasTrackLaneData && !hasCarLaneData))
                {
                    trackOnly++;
                }

                bool roleMatch = false;
                bool isSourceEndpoint = false;
                if (hasEdgeLane)
                {
                    bool startAtSplit = math.abs(edgeLane.m_EdgeDelta.x - endpointDelta) <= 0.001f;
                    bool endAtSplit = math.abs(edgeLane.m_EdgeDelta.y - endpointDelta) <= 0.001f;
                    isSourceEndpoint = endAtSplit;
                    roleMatch = (role == EndpointRole.SourceEndAtNode && isSourceEndpoint) ||
                                (role == EndpointRole.TargetStartAtNode && !isSourceEndpoint && startAtSplit);
                }

                if (roleMatch)
                {
                    roleMatches++;
                }

                string compositionMatch = "notTried";
                if (roleMatch && hasCurve && hasLaneData)
                {
                    Curve matchCurve = curve;
                    if (isSourceEndpoint)
                    {
                        matchCurve.m_Bezier = MathUtils.Invert(matchCurve.m_Bezier);
                    }

                    if (TryFindTrafficCompositionLane(
                            laneEntity,
                            matchCurve,
                            laneData,
                            compositionData,
                            compositionLanes,
                            edgeGeometry,
                            isEnd,
                            isSourceEndpoint,
                            visitedCompositionLanes,
                            out NetCompositionLane compositionLane,
                            out float order))
                    {
                        compositionMatches++;
                        compositionMatch = $"ok order={order:0.###} lanePos={FormatFloat3(compositionLane.m_Position)} cg={new int2(compositionLane.m_Carriageway, compositionLane.m_Group)} flags=[{compositionLane.m_Flags}]";
                    }
                    else
                    {
                        compositionMatch = "miss";
                    }
                }

                if (samples.Count < 12)
                {
                    int startLane = hasLane ? lane.m_StartNode.GetLaneIndex() & 0xff : -1;
                    int endLane = hasLane ? lane.m_EndNode.GetLaneIndex() & 0xff : -1;
                    LaneFlags laneFlags = hasLaneData ? laneData.m_Flags : default;
                    TrackTypes trackTypes = hasTrackLaneData ? trackLaneData.m_TrackTypes : default;
                    samples.Add($"{FormatEntity(laneEntity)} roleMatch={roleMatch} endpoint={(hasEdgeLane ? (isSourceEndpoint ? "source" : "target") : "unknown")} startLane={startLane} endLane={endLane} edgeDelta={(hasEdgeLane ? edgeLane.m_EdgeDelta.ToString() : "<missing>")} methods=[{subLane.m_PathMethods}] laneFlags=[{laneFlags}] hasCarData={hasCarLaneData} hasTrackData={hasTrackLaneData} trackTypes=[{trackTypes}] netCar={hasNetCarLane} netTrack={hasNetTrackLane} secondary={hasSecondaryLane} comp={compositionMatch}");
                }
            }

            return $"edge={FormatEntity(edgeEntity)} role={role} candidates={candidates} roleMatches={roleMatches} compositionMatches={compositionMatches} trackOnly={trackOnly} noCarData={noCarData} netTrack={netTrack} netCar={netCar} secondary={secondary} samples={FormatStringList(samples)}";
        }

        private string FormatSplitNodeTrackConnectorAudit(Entity splitNode, Entity outerEdge, Entity pocketEdge)
        {
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return $"splitNode={FormatEntity(splitNode)} noSubLaneBuffer";
            }

            List<string> samples = new List<string>(12);
            int candidates = 0;
            int splitPair = 0;
            int trackOnly = 0;
            int netTrack = 0;
            int netCar = 0;
            int noCarData = 0;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                PrefabRef lanePrefab = default;
                NetLaneData laneData = default;
                CarLaneData carLaneData = default;
                TrackLaneData trackLaneData = default;
                bool hasPrefab = EntityManager.TryGetComponent(laneEntity, out lanePrefab);
                bool hasLaneData = hasPrefab && EntityManager.TryGetComponent(lanePrefab.m_Prefab, out laneData);
                bool hasCarLaneData = hasPrefab && EntityManager.TryGetComponent(lanePrefab.m_Prefab, out carLaneData);
                bool hasTrackLaneData = hasPrefab && EntityManager.TryGetComponent(lanePrefab.m_Prefab, out trackLaneData);
                bool hasNetTrackLane = EntityManager.HasComponent<NetTrackLane>(laneEntity);
                bool hasNetCarLane = EntityManager.HasComponent<NetCarLane>(laneEntity);
                bool hasTrackMethod = (subLane.m_PathMethods & PathMethod.Track) != 0;
                bool hasRoadMethod = (subLane.m_PathMethods & PathMethod.Road) != 0;
                bool laneFlagsTrack = hasLaneData && (laneData.m_Flags & LaneFlags.Track) != 0;

                if (!hasTrackMethod && !laneFlagsTrack && !hasTrackLaneData && !hasNetTrackLane)
                {
                    continue;
                }

                if (!EntityManager.TryGetComponent(laneEntity, out Lane lane))
                {
                    continue;
                }

                Entity sourceEdge = Entity.Null;
                Entity targetEdge = Entity.Null;
                bool hasEdges = NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, splitNode, lane, out sourceEdge, out targetEdge);
                bool isSplitPair = hasEdges &&
                                   (sourceEdge == outerEdge || sourceEdge == pocketEdge) &&
                                   (targetEdge == outerEdge || targetEdge == pocketEdge);
                candidates++;
                if (isSplitPair)
                {
                    splitPair++;
                }

                if ((hasTrackMethod && !hasRoadMethod) || (hasTrackLaneData && !hasCarLaneData))
                {
                    trackOnly++;
                }

                if (hasNetTrackLane)
                {
                    netTrack++;
                }

                if (hasNetCarLane)
                {
                    netCar++;
                }

                if (!hasCarLaneData)
                {
                    noCarData++;
                }

                if (samples.Count < 12)
                {
                    TrackTypes trackTypes = hasTrackLaneData ? trackLaneData.m_TrackTypes : default;
                    LaneFlags laneFlags = hasLaneData ? laneData.m_Flags : default;
                    bool hasLaneConnection = EntityManager.TryGetComponent(laneEntity, out LaneConnection laneConnection);
                    string connection = hasLaneConnection
                        ? $"{FormatEntity(laneConnection.m_StartLane)}->{FormatEntity(laneConnection.m_EndLane)} pos={laneConnection.m_StartPosition:0.###}->{laneConnection.m_EndPosition:0.###}"
                        : "<none>";
                    samples.Add($"{FormatEntity(laneEntity)} pair={isSplitPair} edges={(hasEdges ? $"{FormatEntity(sourceEdge)}->{FormatEntity(targetEdge)}" : "<unknown>")} lanes={(lane.m_StartNode.GetLaneIndex() & 0xff)}->{(lane.m_EndNode.GetLaneIndex() & 0xff)} methods=[{subLane.m_PathMethods}] laneFlags=[{laneFlags}] hasCarData={hasCarLaneData} hasTrackData={hasTrackLaneData} trackTypes=[{trackTypes}] netCar={hasNetCarLane} netTrack={hasNetTrackLane} laneConnection={connection}");
                }
            }

            return $"splitNode={FormatEntity(splitNode)} candidates={candidates} splitPair={splitPair} trackOnly={trackOnly} noCarData={noCarData} netTrack={netTrack} netCar={netCar} samples={FormatStringList(samples)}";
        }

        private bool TryFindTrafficCompositionLane(
            Entity laneEntity,
            Curve laneCurve,
            NetLaneData laneData,
            NetCompositionData compositionData,
            DynamicBuffer<NetCompositionLane> compositionLanes,
            EdgeGeometry edgeGeometry,
            bool isEnd,
            bool isSourceEndpoint,
            bool[] visitedCompositionLanes,
            out NetCompositionLane result,
            out float order)
        {
            result = default;
            order = 0f;

            LaneFlags disconnectedFlag = isSourceEndpoint ? LaneFlags.DisconnectedEnd : LaneFlags.DisconnectedStart;
            if (EntityManager.HasComponent<MasterLane>(laneEntity))
            {
                return false;
            }

            LaneFlags expectedFlags = laneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.Underground);
            LaneFlags mask = LaneFlags.Invert | LaneFlags.Slave | LaneFlags.Road | LaneFlags.Track | LaneFlags.Underground | disconnectedFlag;

            if (isSourceEndpoint != isEnd)
            {
                expectedFlags |= LaneFlags.Invert;
            }

            if (EntityManager.HasComponent<SlaveLane>(laneEntity))
            {
                expectedFlags |= LaneFlags.Slave;
            }

            if ((laneData.m_Flags & disconnectedFlag) != 0)
            {
                return false;
            }

            int bestIndex = -1;
            float bestError = float.MaxValue;
            Line2 edgeLine = new Line2(edgeGeometry.m_Start.m_Right.a.xz, edgeGeometry.m_Start.m_Left.a.xz);
            Line2 laneLine = new Line2(laneCurve.m_Bezier.a.xz, laneCurve.m_Bezier.b.xz);

            for (int i = 0; i < compositionLanes.Length; i++)
            {
                if (visitedCompositionLanes[i])
                {
                    continue;
                }

                NetCompositionLane compositionLane = compositionLanes[i];
                if ((compositionLane.m_Flags & mask) != expectedFlags)
                {
                    continue;
                }

                compositionLane.m_Position.x = math.select(-compositionLane.m_Position.x, compositionLane.m_Position.x, isEnd);
                float candidateOrder = compositionLane.m_Position.x / math.max(1f, compositionData.m_Width) + 0.5f;
                if (!MathUtils.Intersect(edgeLine, laneLine, out float2 t))
                {
                    continue;
                }

                float error = math.abs(candidateOrder - t.x);
                if (error < bestError)
                {
                    bestIndex = i;
                    bestError = error;
                    order = candidateOrder;
                    result = compositionLane;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            visitedCompositionLanes[bestIndex] = true;
            return true;
        }

        private static float2 GetAveragePosition(List<LaneEndpoint> lanes)
        {
            float2 origin = default;
            for (int i = 0; i < lanes.Count; i++)
            {
                origin += lanes[i].Position.xz;
            }

            return origin / math.max(1, lanes.Count);
        }

        private static void AssignLaneLaterals(List<LaneEndpoint> lanes, float2 origin, float2 right)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                LaneEndpoint lane = lanes[i];
                lane.Lateral = math.dot(lane.Position.xz - origin, right);
                lanes[i] = lane;
            }

            lanes.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));
        }

        private static bool TrySelectLaneMapping(
            List<LaneEndpoint> sourceLanes,
            List<LaneEndpoint> targetLanes,
            out List<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out float bestScore)
        {
            selectedTargets = null;
            extraTargetIndex = -1;
            bestScore = float.MaxValue;

            int desiredTargetCount = sourceLanes.Count + 1;
            if (targetLanes.Count < desiredTargetCount)
            {
                return false;
            }

            int maxStart = targetLanes.Count - desiredTargetCount;
            for (int start = 0; start <= maxStart; start++)
            {
                List<LaneEndpoint> subset = targetLanes.GetRange(start, desiredTargetCount);
                for (int extraCandidate = 0; extraCandidate < 2; extraCandidate++)
                {
                    int extraIndex = extraCandidate == 0 ? 0 : subset.Count - 1;
                    float score = 0f;
                    int sourceIndex = 0;
                    for (int targetIndex = 0; targetIndex < subset.Count; targetIndex++)
                    {
                        if (targetIndex == extraIndex)
                        {
                            continue;
                        }

                        score += math.abs(sourceLanes[sourceIndex].Lateral - subset[targetIndex].Lateral);
                        sourceIndex++;
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        extraTargetIndex = extraIndex;
                        selectedTargets = subset;
                    }
                }
            }

            return selectedTargets != null && extraTargetIndex >= 0;
        }

        private bool TryRefineExtraTargetFromCenterConnectors(
            Entity intersectionNode,
            Entity centerSourceEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string diagnostics)
        {
            extraTargetIndex = -1;
            turn = TurnDirection.Ambiguous;
            diagnostics = string.Empty;

            if (intersectionNode == Entity.Null ||
                centerSourceEdge == Entity.Null ||
                !EntityManager.Exists(intersectionNode) ||
                !EntityManager.TryGetBuffer(intersectionNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                diagnostics = $"center-node-missing-sublanes intersection={FormatEntity(intersectionNode)} sourceEdge={FormatEntity(centerSourceEdge)}";
                return false;
            }

            if (selectedTargets == null || selectedTargets.Count == 0)
            {
                diagnostics = "no-selected-targets";
                return false;
            }

            int[] leftCounts = new int[selectedTargets.Count];
            int[] rightCounts = new int[selectedTargets.Count];
            int[] straightCounts = new int[selectedTargets.Count];
            m_CenterTurnCandidates.Clear();

            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, intersectionNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    sourceEdge != centerSourceEdge ||
                    targetEdge == centerSourceEdge)
                {
                    continue;
                }

                int sourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff;
                if (!TryFindTargetByCenterLaneIndex(selectedTargets, sourceLaneIndex, out int targetListIndex))
                {
                    continue;
                }

                NetCarLane carLane = EntityManager.GetComponentData<NetCarLane>(laneEntity);
                TurnDirection connectorTurn = ClassifyCenterConnectorTurn(intersectionNode, centerSourceEdge, targetEdge, carLane.m_Flags);
                if (connectorTurn == TurnDirection.Left)
                {
                    leftCounts[targetListIndex]++;
                }
                else if (connectorTurn == TurnDirection.Right)
                {
                    rightCounts[targetListIndex]++;
                }
                else
                {
                    straightCounts[targetListIndex]++;
                }

                m_CenterTurnCandidates.Add(new CenterTurnCandidate
                {
                    LaneEntity = laneEntity,
                    SourceLaneIndex = sourceLaneIndex,
                    TargetListIndex = targetListIndex,
                    TargetLaneIndex = selectedTargets[targetListIndex].LaneIndex,
                    TargetEdge = targetEdge,
                    Turn = connectorTurn,
                    Flags = carLane.m_Flags
                });
            }

            diagnostics = FormatCenterTurnDiagnostics(selectedTargets, leftCounts, rightCounts, straightCounts, m_CenterTurnCandidates);
            int bestIndex = -1;
            int bestScore = int.MinValue;
            TurnDirection bestTurn = TurnDirection.Ambiguous;
            bool tied = false;

            for (int i = 0; i < selectedTargets.Count; i++)
            {
                bool edgeTarget = i == 0 || i == selectedTargets.Count - 1;
                if (!edgeTarget)
                {
                    continue;
                }

                int left = leftCounts[i];
                int right = rightCounts[i];
                if (left == right)
                {
                    continue;
                }

                TurnDirection candidateTurn = left > right ? TurnDirection.Left : TurnDirection.Right;
                int turnCount = math.max(left, right);
                int oppositeTurnCount = math.min(left, right);
                int score = turnCount * 16 - oppositeTurnCount * 8 - straightCounts[i] * 3;
                if (straightCounts[i] == 0)
                {
                    score += 1000;
                }

                if (score > bestScore)
                {
                    bestIndex = i;
                    bestScore = score;
                    bestTurn = candidateTurn;
                    tied = false;
                }
                else if (score == bestScore)
                {
                    tied = true;
                }
            }

            if (bestIndex < 0 || tied)
            {
                diagnostics = $"{diagnostics}; centerSelection={(bestIndex < 0 ? "none" : "tie")}";
                return false;
            }

            extraTargetIndex = bestIndex;
            turn = bestTurn;
            diagnostics = $"{diagnostics}; centerSelection=target{selectedTargets[bestIndex].LaneIndex}/{bestTurn}/score{bestScore}{(straightCounts[bestIndex] == 0 ? "/turnOnly" : string.Empty)}";
            return true;
        }

        private static bool TryFindTargetByCenterLaneIndex(IReadOnlyList<LaneEndpoint> targets, int centerLaneIndex, out int targetListIndex)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].OppositeLaneIndex == centerLaneIndex)
                {
                    targetListIndex = i;
                    return true;
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].LaneIndex == centerLaneIndex)
                {
                    targetListIndex = i;
                    return true;
                }
            }

            targetListIndex = -1;
            return false;
        }

        private TurnDirection ClassifyCenterConnectorTurn(Entity intersectionNode, Entity sourceEdge, Entity targetEdge, CarLaneFlags flags)
        {
            if ((flags & CarLaneFlags.TurnLeft) != 0)
            {
                return TurnDirection.Left;
            }

            if ((flags & CarLaneFlags.TurnRight) != 0)
            {
                return TurnDirection.Right;
            }

            if (!NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, sourceEdge, intersectionNode, out float2 sourceOutward) ||
                !NetTopologyHelpers.TryGetEdgeDirectionFromNode(EntityManager, targetEdge, intersectionNode, out float2 targetOutward))
            {
                return TurnDirection.Ambiguous;
            }

            float2 incoming = -sourceOutward;
            float cross = NetTopologyHelpers.Cross(incoming, targetOutward);
            if (math.abs(cross) < 0.25f)
            {
                return TurnDirection.Ambiguous;
            }

            return cross > 0f ? TurnDirection.Left : TurnDirection.Right;
        }

        private bool TryBuildDesiredMappings(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            int extraTargetIndex,
            int branchSourceLaneIndex,
            IReadOnlyList<ConnectorLane> existingConnectors,
            bool preferExistingConnectors,
            out LaneMapping[] mappings,
            out string mappingSource,
            out string reason)
        {
            mappings = null;
            mappingSource = "none";
            reason = string.Empty;

            if (sourceLanes == null ||
                selectedTargets == null ||
                selectedTargets.Count != sourceLanes.Count + 1 ||
                extraTargetIndex < 0 ||
                extraTargetIndex >= selectedTargets.Count)
            {
                reason = $"invalid counts source={sourceLanes?.Count ?? 0} selected={selectedTargets?.Count ?? 0} extraIndex={extraTargetIndex}";
                return false;
            }

            int extraTargetLaneIndex = selectedTargets[extraTargetIndex].LaneIndex;
            List<LaneEndpoint> originalTargets = new List<LaneEndpoint>(sourceLanes.Count);
            for (int i = 0; i < selectedTargets.Count; i++)
            {
                if (i != extraTargetIndex)
                {
                    originalTargets.Add(selectedTargets[i]);
                }
            }

            int[] assignedTargets = new int[sourceLanes.Count];
            for (int i = 0; i < assignedTargets.Length; i++)
            {
                assignedTargets[i] = -1;
            }

            HashSet<int> usedTargets = new HashSet<int>();
            int existingAssignments = 0;
            if (!preferExistingConnectors)
            {
                for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
                {
                    assignedTargets[sourceIndex] = originalTargets[sourceIndex].LaneIndex;
                    usedTargets.Add(assignedTargets[sourceIndex]);
                }
            }
            else
            {
                for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
                {
                    LaneEndpoint source = sourceLanes[sourceIndex];
                    float bestScore = float.MaxValue;
                    int bestTarget = -1;

                    for (int connectorIndex = 0; connectorIndex < existingConnectors.Count; connectorIndex++)
                    {
                        ConnectorLane connector = existingConnectors[connectorIndex];
                        if (connector.SourceLaneIndex != source.LaneIndex ||
                            connector.TargetLaneIndex == extraTargetLaneIndex ||
                            usedTargets.Contains(connector.TargetLaneIndex) ||
                            !TryFindLaneEndpoint(originalTargets, connector.TargetLaneIndex, out LaneEndpoint target))
                        {
                            continue;
                        }

                        float score = math.abs(source.Lateral - target.Lateral);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestTarget = connector.TargetLaneIndex;
                        }
                    }

                    if (bestTarget >= 0)
                    {
                        assignedTargets[sourceIndex] = bestTarget;
                        usedTargets.Add(bestTarget);
                        existingAssignments++;
                    }
                }
            }

            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (assignedTargets[sourceIndex] >= 0)
                {
                    continue;
                }

                float bestFallbackScore = float.MaxValue;
                int bestFallbackTarget = -1;
                for (int targetIndex = 0; targetIndex < originalTargets.Count; targetIndex++)
                {
                    LaneEndpoint target = originalTargets[targetIndex];
                    if (usedTargets.Contains(target.LaneIndex))
                    {
                        continue;
                    }

                    float score = math.abs(sourceLanes[sourceIndex].Lateral - target.Lateral);
                    if (score < bestFallbackScore)
                    {
                        bestFallbackScore = score;
                        bestFallbackTarget = target.LaneIndex;
                    }
                }

                if (bestFallbackTarget < 0)
                {
                    reason = $"no remaining original target for source={sourceLanes[sourceIndex].LaneIndex} assigned={string.Join(",", assignedTargets)} originalTargets={FormatLaneOrder(originalTargets)}";
                    return false;
                }

                assignedTargets[sourceIndex] = bestFallbackTarget;
                usedTargets.Add(assignedTargets[sourceIndex]);
            }

            m_Mappings.Clear();
            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (!TryFindLaneEndpoint(selectedTargets, assignedTargets[sourceIndex], out LaneEndpoint target))
                {
                    reason = $"assigned target missing source={sourceLanes[sourceIndex].LaneIndex} target={assignedTargets[sourceIndex]}";
                    return false;
                }

                m_Mappings.Add(new LaneMapping
                {
                    SourceEdge = sourceLanes[sourceIndex].Edge,
                    TargetEdge = target.Edge,
                    SourceLaneIndex = sourceLanes[sourceIndex].LaneIndex,
                    TargetLaneIndex = assignedTargets[sourceIndex],
                    Method = GetMappingMethod(sourceLanes[sourceIndex], target),
                    IsBranch = false
                });
            }

            if (!TryFindLaneEndpoint(sourceLanes, branchSourceLaneIndex, out LaneEndpoint branchSource) ||
                !TryFindLaneEndpoint(selectedTargets, extraTargetLaneIndex, out LaneEndpoint branchTarget))
            {
                reason = $"branch endpoint missing source={branchSourceLaneIndex} target={extraTargetLaneIndex}";
                return false;
            }

            m_Mappings.Add(new LaneMapping
            {
                SourceEdge = branchSource.Edge,
                TargetEdge = branchTarget.Edge,
                SourceLaneIndex = branchSourceLaneIndex,
                TargetLaneIndex = extraTargetLaneIndex,
                Method = GetMappingMethod(branchSource, branchTarget),
                IsBranch = true
            });

            mappings = m_Mappings.ToArray();
            mappingSource = !preferExistingConnectors
                ? "center-turn-order"
                : existingAssignments == sourceLanes.Count
                ? "existing-connectors"
                : existingAssignments > 0
                    ? $"existing-connectors+fallback({existingAssignments}/{sourceLanes.Count})"
                    : "lateral-fallback";
            return true;
        }

        private bool TryBuildSnapshotReverseMappings(
            TransitionConnectionSnapshot snapshot,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            out LaneMapping[] mappings,
            out string mappingSource,
            out string reason)
        {
            mappings = null;
            mappingSource = "none";
            reason = string.Empty;

            if (snapshot == null || snapshot.Mappings == null || snapshot.Mappings.Length == 0)
            {
                reason = "snapshot empty";
                return false;
            }

            if (sourceLanes == null || targetLanes == null || sourceLanes.Count == 0 || targetLanes.Count == 0)
            {
                reason = $"missing reverse endpoints source={sourceLanes?.Count ?? 0} target={targetLanes?.Count ?? 0}";
                return false;
            }

            if (!TryBuildSnapshotLaneRemap(
                    snapshot.Mappings,
                    sourceLanes,
                    source: true,
                    out Dictionary<int, LaneEndpoint> sourceRemap,
                    out string sourceRemapDetail,
                    out string sourceRemapReason))
            {
                reason = $"source remap failed: {sourceRemapReason}";
                return false;
            }

            if (!TryBuildSnapshotLaneRemap(
                    snapshot.Mappings,
                    targetLanes,
                    source: false,
                    out Dictionary<int, LaneEndpoint> targetRemap,
                    out string targetRemapDetail,
                    out string targetRemapReason))
            {
                reason = $"target remap failed: {targetRemapReason}";
                return false;
            }

            m_Mappings.Clear();
            HashSet<ConnectionKey> used = new HashSet<ConnectionKey>();
            int skipped = 0;
            for (int i = 0; i < snapshot.Mappings.Length; i++)
            {
                TransitionConnectionSnapshotMapping snapshotMapping = snapshot.Mappings[i];
                if (!sourceRemap.TryGetValue(snapshotMapping.SourceLaneIndex, out LaneEndpoint source) ||
                    !targetRemap.TryGetValue(snapshotMapping.TargetLaneIndex, out LaneEndpoint target))
                {
                    skipped++;
                    continue;
                }

                ConnectionKey key = new ConnectionKey(source.LaneIndex, target.LaneIndex);
                if (used.Contains(key))
                {
                    skipped++;
                    continue;
                }

                used.Add(key);
                m_Mappings.Add(new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = source.LaneIndex,
                    TargetLaneIndex = target.LaneIndex,
                    Method = RemapSnapshotMethod(snapshotMapping.Method, source, target),
                    IsBranch = false
                });
            }

            if (m_Mappings.Count == 0)
            {
                reason = $"no snapshot mappings could be remapped snapshot={FormatSnapshot(snapshot)} skipped={skipped}";
                return false;
            }

            mappings = m_Mappings.ToArray();
            mappingSource = $"snapshot={snapshot.Source}; sourceRemap=({sourceRemapDetail}); targetRemap=({targetRemapDetail}); skipped={skipped}; original={snapshot.Mappings.Length}";
            reason = "ok";
            return true;
        }

        private static void NormalizeTransitionLaneLaterals(List<LaneEndpoint> sourceLanes, List<LaneEndpoint> targetLanes)
        {
            if (sourceLanes == null ||
                targetLanes == null ||
                sourceLanes.Count == 0 ||
                targetLanes.Count == 0)
            {
                return;
            }

            float2 travelDirection = sourceLanes[0].TravelDirection;
            if (math.lengthsq(travelDirection) <= 0.0001f)
            {
                return;
            }

            float2 right = new float2(travelDirection.y, -travelDirection.x);
            float2 sourceOrigin = GetAveragePosition(sourceLanes);
            AssignLaneLaterals(sourceLanes, sourceOrigin, right);
            AssignLaneLaterals(targetLanes, sourceOrigin, right);
        }

        private static bool TryBuildSnapshotLaneRemap(
            IReadOnlyList<TransitionConnectionSnapshotMapping> snapshotMappings,
            IReadOnlyList<LaneEndpoint> currentLanes,
            bool source,
            out Dictionary<int, LaneEndpoint> remap,
            out string detail,
            out string reason)
        {
            remap = null;
            detail = "none";
            reason = string.Empty;

            if (snapshotMappings == null || snapshotMappings.Count == 0)
            {
                reason = "snapshot empty";
                return false;
            }

            if (currentLanes == null || currentLanes.Count == 0)
            {
                reason = "current lanes empty";
                return false;
            }

            Dictionary<int, SnapshotLaneOrder> snapshotLanes = new Dictionary<int, SnapshotLaneOrder>();
            for (int i = 0; i < snapshotMappings.Count; i++)
            {
                TransitionConnectionSnapshotMapping mapping = snapshotMappings[i];
                int laneIndex = source ? mapping.SourceLaneIndex : mapping.TargetLaneIndex;
                float lateral = source ? mapping.SourceLateral : mapping.TargetLateral;
                if (snapshotLanes.TryGetValue(laneIndex, out SnapshotLaneOrder existing))
                {
                    existing.LateralSum += lateral;
                    existing.Count++;
                    snapshotLanes[laneIndex] = existing;
                }
                else
                {
                    snapshotLanes.Add(laneIndex, new SnapshotLaneOrder
                    {
                        LaneIndex = laneIndex,
                        LateralSum = lateral,
                        Count = 1,
                        FirstSnapshotOrder = i
                    });
                }
            }

            if (snapshotLanes.Count > currentLanes.Count)
            {
                reason = $"snapshot lanes exceed current lanes snapshot={snapshotLanes.Count} current={currentLanes.Count}";
                return false;
            }

            List<SnapshotLaneOrder> orderedSnapshot = snapshotLanes.Values.ToList();
            float minLateral = orderedSnapshot.Min(lane => lane.AverageLateral);
            float maxLateral = orderedSnapshot.Max(lane => lane.AverageLateral);
            bool useLateralOrder = maxLateral - minLateral > 0.75f;
            orderedSnapshot.Sort((a, b) =>
            {
                int compare = useLateralOrder
                    ? a.AverageLateral.CompareTo(b.AverageLateral)
                    : a.LaneIndex.CompareTo(b.LaneIndex);
                return compare != 0
                    ? compare
                    : a.FirstSnapshotOrder.CompareTo(b.FirstSnapshotOrder);
            });

            List<LaneEndpoint> orderedCurrent = currentLanes.ToList();
            orderedCurrent.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));

            remap = new Dictionary<int, LaneEndpoint>(orderedSnapshot.Count);
            HashSet<int> usedCurrentIndexes = new HashSet<int>();
            for (int i = 0; i < orderedSnapshot.Count; i++)
            {
                SnapshotLaneOrder snapshotLane = orderedSnapshot[i];
                if (!TrySelectCurrentLaneByRank(
                        orderedCurrent,
                        orderedSnapshot.Count,
                        i,
                        usedCurrentIndexes,
                        out LaneEndpoint currentLane))
                {
                    reason = $"no current lane for snapshotLane={snapshotLane.LaneIndex} rank={i} current={FormatLaneOrder(currentLanes)}";
                    remap = null;
                    return false;
                }

                remap.Add(snapshotLane.LaneIndex, currentLane);
                usedCurrentIndexes.Add(currentLane.LaneIndex);
            }

            List<string> remapDetails = new List<string>(orderedSnapshot.Count);
            for (int i = 0; i < orderedSnapshot.Count; i++)
            {
                SnapshotLaneOrder snapshotLane = orderedSnapshot[i];
                LaneEndpoint currentLane = remap[snapshotLane.LaneIndex];
                remapDetails.Add($"{snapshotLane.LaneIndex}->{currentLane.LaneIndex}@{snapshotLane.AverageLateral:0.##}/{currentLane.Lateral:0.##}");
            }

            detail = $"rank-{(useLateralOrder ? "lateral" : "index")}; " + string.Join(",", remapDetails);
            return true;
        }

        private static bool TrySelectCurrentLaneByRank(
            IReadOnlyList<LaneEndpoint> orderedCurrent,
            int snapshotLaneCount,
            int snapshotRank,
            HashSet<int> usedCurrentIndexes,
            out LaneEndpoint lane)
        {
            lane = default;
            if (orderedCurrent == null || orderedCurrent.Count == 0)
            {
                return false;
            }

            int preferredRank = snapshotLaneCount <= 1
                ? 0
                : (int)math.round(snapshotRank * (orderedCurrent.Count - 1f) / (snapshotLaneCount - 1f));
            preferredRank = math.clamp(preferredRank, 0, orderedCurrent.Count - 1);

            int bestRankDistance = int.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < orderedCurrent.Count; i++)
            {
                LaneEndpoint candidate = orderedCurrent[i];
                if (usedCurrentIndexes.Contains(candidate.LaneIndex))
                {
                    continue;
                }

                int rankDistance = math.abs(i - preferredRank);
                if (rankDistance < bestRankDistance)
                {
                    bestRankDistance = rankDistance;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            lane = orderedCurrent[bestIndex];
            return true;
        }

        private static PathMethod RemapSnapshotMethod(PathMethod snapshotMethod, LaneEndpoint source, LaneEndpoint target)
        {
            PathMethod method = snapshotMethod | PathMethod.Road;
            PathMethod compatible = GetMappingMethod(source, target);
            if ((method & PathMethod.Track) != 0 && (compatible & PathMethod.Track) == 0)
            {
                method &= ~PathMethod.Track;
            }

            return method;
        }

        private static bool TryBuildStraightMappings(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            IReadOnlyList<ConnectorLane> existingConnectors,
            out LaneMapping[] mappings,
            out string mappingSource,
            out string reason)
        {
            mappings = null;
            mappingSource = "none";
            reason = string.Empty;

            if (sourceLanes == null ||
                targetLanes == null ||
                sourceLanes.Count == 0 ||
                targetLanes.Count == 0 ||
                sourceLanes.Count != targetLanes.Count)
            {
                reason = $"reverse lane count mismatch source={sourceLanes?.Count ?? 0} target={targetLanes?.Count ?? 0}";
                return false;
            }

            int[] assignedTargets = new int[sourceLanes.Count];
            for (int i = 0; i < assignedTargets.Length; i++)
            {
                assignedTargets[i] = -1;
            }

            HashSet<int> usedTargets = new HashSet<int>();
            int existingAssignments = 0;
            if (existingConnectors != null)
            {
                for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
                {
                    LaneEndpoint source = sourceLanes[sourceIndex];
                    float bestScore = float.MaxValue;
                    int bestTarget = -1;

                    for (int connectorIndex = 0; connectorIndex < existingConnectors.Count; connectorIndex++)
                    {
                        ConnectorLane connector = existingConnectors[connectorIndex];
                        if (connector.SourceLaneIndex != source.LaneIndex ||
                            usedTargets.Contains(connector.TargetLaneIndex) ||
                            !TryFindLaneEndpoint(targetLanes, connector.TargetLaneIndex, out LaneEndpoint target))
                        {
                            continue;
                        }

                        float score = math.abs(source.Lateral - target.Lateral);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestTarget = target.LaneIndex;
                        }
                    }

                    if (bestTarget >= 0)
                    {
                        assignedTargets[sourceIndex] = bestTarget;
                        usedTargets.Add(bestTarget);
                        existingAssignments++;
                    }
                }
            }

            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (assignedTargets[sourceIndex] >= 0)
                {
                    continue;
                }

                float bestFallbackScore = float.MaxValue;
                int bestFallbackTarget = -1;
                for (int targetIndex = 0; targetIndex < targetLanes.Count; targetIndex++)
                {
                    LaneEndpoint target = targetLanes[targetIndex];
                    if (usedTargets.Contains(target.LaneIndex))
                    {
                        continue;
                    }

                    float score = math.abs(sourceLanes[sourceIndex].Lateral - target.Lateral);
                    if (score < bestFallbackScore)
                    {
                        bestFallbackScore = score;
                        bestFallbackTarget = target.LaneIndex;
                    }
                }

                if (bestFallbackTarget < 0)
                {
                    reason = $"no remaining reverse target for source={sourceLanes[sourceIndex].LaneIndex} assigned={string.Join(",", assignedTargets)} targetOrder={FormatLaneOrder(targetLanes)}";
                    return false;
                }

                assignedTargets[sourceIndex] = bestFallbackTarget;
                usedTargets.Add(assignedTargets[sourceIndex]);
            }

            LaneMapping[] result = new LaneMapping[sourceLanes.Count];
            for (int sourceIndex = 0; sourceIndex < sourceLanes.Count; sourceIndex++)
            {
                if (!TryFindLaneEndpoint(targetLanes, assignedTargets[sourceIndex], out LaneEndpoint target))
                {
                    reason = $"assigned reverse target missing source={sourceLanes[sourceIndex].LaneIndex} target={assignedTargets[sourceIndex]}";
                    return false;
                }

                result[sourceIndex] = new LaneMapping
                {
                    SourceEdge = sourceLanes[sourceIndex].Edge,
                    TargetEdge = target.Edge,
                    SourceLaneIndex = sourceLanes[sourceIndex].LaneIndex,
                    TargetLaneIndex = target.LaneIndex,
                    Method = GetMappingMethod(sourceLanes[sourceIndex], target),
                    IsBranch = false
                };
            }

            mappings = result;
            mappingSource = existingAssignments == sourceLanes.Count
                ? "reverse-existing-connectors"
                : existingAssignments > 0
                    ? $"reverse-existing-connectors+fallback({existingAssignments}/{sourceLanes.Count})"
                    : "reverse-lateral-fallback";
            return true;
        }

        private bool TryBuildExistingConnectorSnapshotMappings(
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            IReadOnlyList<ConnectorLane> existingConnectors,
            Entity sourceEdge,
            Entity targetEdge,
            out LaneMapping[] mappings,
            out string mappingSource,
            out string reason)
        {
            mappings = null;
            mappingSource = "none";
            reason = string.Empty;

            if (sourceLanes == null ||
                targetLanes == null ||
                existingConnectors == null ||
                existingConnectors.Count == 0)
            {
                reason = $"missing inputs source={sourceLanes?.Count ?? 0} target={targetLanes?.Count ?? 0} existing={existingConnectors?.Count ?? 0}";
                return false;
            }

            m_Mappings.Clear();
            HashSet<ConnectionKey> used = new HashSet<ConnectionKey>();
            int endpointMisses = 0;
            int duplicates = 0;
            int methodFallbacks = 0;
            for (int i = 0; i < existingConnectors.Count; i++)
            {
                ConnectorLane connector = existingConnectors[i];
                if (!TryFindLaneEndpoint(sourceLanes, connector.SourceLaneIndex, out LaneEndpoint source) ||
                    !TryFindLaneEndpoint(targetLanes, connector.TargetLaneIndex, out LaneEndpoint target))
                {
                    endpointMisses++;
                    continue;
                }

                ConnectionKey key = new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex);
                if (used.Contains(key))
                {
                    duplicates++;
                    continue;
                }

                PathMethod method = SanitizeTrafficPathMethod(connector.PathMethods);
                if ((method & PathMethod.Road) == 0)
                {
                    method = GetMappingMethod(source, target);
                    methodFallbacks++;
                }

                used.Add(key);
                m_Mappings.Add(new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = connector.SourceLaneIndex,
                    TargetLaneIndex = connector.TargetLaneIndex,
                    Method = method,
                    IsBranch = false
                });
            }

            if (m_Mappings.Count == 0)
            {
                reason = $"no existing connector mappings accepted existing={existingConnectors.Count} endpointMisses={endpointMisses} duplicates={duplicates} sourceOrder={FormatLaneOrder(sourceLanes)} targetOrder={FormatLaneOrder(targetLanes)}";
                return false;
            }

            mappings = m_Mappings.ToArray();
            mappingSource = $"existingConnectorSnapshot accepted={mappings.Length}/{existingConnectors.Count} endpointMisses={endpointMisses} duplicates={duplicates} methodFallbacks={methodFallbacks}";
            reason = "ok";
            return true;
        }

        private static PathMethod GetMappingMethod(LaneEndpoint source, LaneEndpoint target)
        {
            PathMethod method = 0;
            if (SupportsRoadPath(source) && SupportsRoadPath(target))
            {
                method |= PathMethod.Road;
            }

            if (SupportsTrackPath(source) &&
                SupportsTrackPath(target) &&
                TrackTypesCompatible(source.TrackTypes, target.TrackTypes))
            {
                method |= PathMethod.Track;
            }

            if (method == 0)
            {
                method = SupportsTrackPath(source) && SupportsTrackPath(target)
                    ? PathMethod.Track
                    : PathMethod.Road;
            }

            return SanitizeTrafficPathMethod(method);
        }

        private static PathMethod SanitizeTrafficPathMethod(PathMethod method)
        {
            method &= PathMethod.Road | PathMethod.Track;
            return method == 0 ? PathMethod.Road : method;
        }

        private static PathMethod RestrictTrafficPathMethodToEndpoints(PathMethod method, LaneEndpoint source, LaneEndpoint target)
        {
            method &= PathMethod.Road | PathMethod.Track;
            if (!SupportsRoadPath(source) || !SupportsRoadPath(target))
            {
                method &= ~PathMethod.Road;
            }

            if (!SupportsTrackPath(source) ||
                !SupportsTrackPath(target) ||
                !TrackTypesCompatible(source.TrackTypes, target.TrackTypes))
            {
                method &= ~PathMethod.Track;
            }

            return method;
        }

        private static bool SupportsRoadPath(LaneEndpoint endpoint)
        {
            return (endpoint.PathMethods & PathMethod.Road) != 0 &&
                   (endpoint.LaneFlags & LaneFlags.Road) != 0 &&
                   (endpoint.RoadTypes & RoadTypes.Car) != 0;
        }

        private static bool SupportsTrackPath(LaneEndpoint endpoint)
        {
            return (endpoint.LaneFlags & LaneFlags.Track) != 0 &&
                   ((endpoint.PathMethods & PathMethod.Track) != 0 ||
                    endpoint.HasTrackLaneData ||
                    endpoint.HasNetTrackLane);
        }

        private static bool IsTrackOnlyEndpoint(LaneEndpoint endpoint)
        {
            return SupportsTrackPath(endpoint) && !SupportsRoadPath(endpoint);
        }

        private static bool TrackTypesCompatible(TrackTypes source, TrackTypes target)
        {
            return EqualityComparer<TrackTypes>.Default.Equals(source, default) ||
                   EqualityComparer<TrackTypes>.Default.Equals(target, default) ||
                   !EqualityComparer<TrackTypes>.Default.Equals(source & target, default);
        }

        private static TurnDirection DetermineTurn(List<LaneEndpoint> selectedTargets, int extraTargetIndex)
        {
            if (extraTargetIndex == 0)
            {
                return TurnDirection.Left;
            }

            if (extraTargetIndex == selectedTargets.Count - 1)
            {
                return TurnDirection.Right;
            }

            return TurnDirection.Ambiguous;
        }

        private bool WriteTrafficMappings(TrafficApi trafficApi, Request request)
        {
            List<LaneMapping> allMappings = GetRoadFixMappings(request);
            if (allMappings.Count == 0)
            {
                return false;
            }

            List<LaneMapping> validMappings = new List<LaneMapping>(allMappings.Count);
            for (int i = 0; i < allMappings.Count; i++)
            {
                LaneMapping mapping = allMappings[i];
                bool sourceFound = TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out _);
                bool targetFound = TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out _);
                if (!sourceFound || !targetFound)
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic mapping preflight failed splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} sourceFound={sourceFound} targetFound={targetFound}.");
                    return false;
                }

                mapping.Method = GetRoadFixMethod(mapping.Method);
                validMappings.Add(mapping);
            }

            if (validMappings.Count == 0)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic mapping preflight found no writable road mappings splitNode={FormatEntity(request.SplitNode)} allMappings={FormatMappings(allMappings)}.");
                return false;
            }

            bool hasRoadMappings = validMappings.Any(mapping => !mapping.IsTrackPreservation);
            if (!hasRoadMappings)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic mapping preflight found no road repair mappings splitNode={FormatEntity(request.SplitNode)} mappings={FormatMappings(validMappings)}.");
                return false;
            }

            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, request.SplitNode);
            if (modifiedBuffer == null)
            {
                return false;
            }

            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource = new Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>>();
            for (int i = 0; i < validMappings.Count; i++)
            {
                LaneMapping mapping = validMappings[i];
                SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
                if (!bySource.TryGetValue(sourceKey, out Dictionary<TargetLaneKey, LaneMapping> byTarget))
                {
                    byTarget = new Dictionary<TargetLaneKey, LaneMapping>();
                    bySource.Add(sourceKey, byTarget);
                }

                if (byTarget.TryGetValue(targetKey, out LaneMapping existing))
                {
                    existing.Method = GetRoadFixMethod(existing.Method | mapping.Method);
                    existing.IsBranch |= mapping.IsBranch;
                    existing.IsTrackPreservation |= mapping.IsTrackPreservation;
                    byTarget[targetKey] = existing;
                }
                else
                {
                    byTarget.Add(targetKey, mapping);
                }
            }

            m_KeptTrafficConnections.Clear();

            int removedExisting = 0;
            int originalLength = trafficApi.GetBufferLength(modifiedBuffer);
            bool removePocketExisting = request.Mode != RepairMode.ShortEdgeTransition ||
                                        (request.ReverseMappings != null && request.ReverseMappings.Length > 0);
            for (int i = 0; i < originalLength; i++)
            {
                object existing = trafficApi.GetBufferItem(modifiedBuffer, i);
                Entity edge = trafficApi.GetModifiedConnectionEdge(existing);
                SourceLaneKey existingKey = new SourceLaneKey(
                    edge,
                    trafficApi.GetModifiedConnectionLaneIndex(existing));
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                if (edge == request.OuterEdge ||
                    (removePocketExisting && edge == request.PocketEdge) ||
                    bySource.ContainsKey(existingKey))
                {
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

            List<LaneMapping> mergedMappings = bySource.Values.SelectMany(byTarget => byTarget.Values).ToList();
            int writtenSources = 0;
            int writtenConnections = 0;
            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> pair in bySource)
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

                    PathMethod method = GetRoadFixMethod(mapping.Method);
                    trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                        mapping.SourceEdge,
                        mapping.TargetEdge,
                        mapping.SourceLaneIndex,
                        mapping.TargetLaneIndex,
                        new float3x2(
                            sourceEndpoint.LanePosition,
                            targetEndpoint.LanePosition),
                        new int4(
                            sourceEndpoint.CarriagewayAndGroup,
                            targetEndpoint.CarriagewayAndGroup),
                        method,
                        false));
                    writtenConnections++;
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
            MarkForLaneRebuild(request);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic road write counts splitNode={FormatEntity(request.SplitNode)} removedExisting={removedExisting} preservedExisting={m_KeptTrafficConnections.Count} writtenSources={writtenSources} writtenConnections={writtenConnections} trackWrittenConnections=0 trackDeferred=True forwardMappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} deferredTrackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] mergedRoadMappings={FormatMappings(mergedMappings)} trackSkippedReason={request.TrackSkippedReason}.");
            return writtenSources > 0 && writtenConnections > 0;
        }

        private bool TryWriteFinalTrackTrafficMappings(
            TrafficApi trafficApi,
            Request request,
            out FinalTrackWriteStats stats)
        {
            stats = default;
            List<LaneMapping> trackMappings = GetTrackFixMappings(request);
            if (trackMappings.Count == 0)
            {
                stats.Reason = "noTrackMappings";
                return false;
            }

            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource = new Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>>();
            HashSet<SourceLaneKey> affectedSources = new HashSet<SourceLaneKey>();
            for (int i = 0; i < trackMappings.Count; i++)
            {
                LaneMapping mapping = trackMappings[i];
                if (!TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out _) ||
                    !TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out _))
                {
                    stats.Skipped++;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Final track mapping skipped splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} trackSkippedReason=finalTrackEndpointMissing.");
                    continue;
                }

                SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                affectedSources.Add(sourceKey);
                AddOrMergeFinalTrafficMapping(bySource, mapping);
            }

            if (affectedSources.Count == 0)
            {
                stats.Reason = "noWritableTrackMappings";
                return false;
            }

            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, request.SplitNode);
            if (modifiedBuffer == null)
            {
                stats.Reason = "modifiedLaneConnectionsBufferUnavailable";
                return false;
            }

            int originalLength = trafficApi.GetBufferLength(modifiedBuffer);
            m_KeptTrafficConnections.Clear();
            for (int i = 0; i < originalLength; i++)
            {
                object existing = trafficApi.GetBufferItem(modifiedBuffer, i);
                SourceLaneKey existingKey = new SourceLaneKey(
                    trafficApi.GetModifiedConnectionEdge(existing),
                    trafficApi.GetModifiedConnectionLaneIndex(existing));
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                if (!affectedSources.Contains(existingKey))
                {
                    m_KeptTrafficConnections.Add(existing);
                    continue;
                }

                stats.RemovedExisting++;
                CopyExistingGeneratedConnectionsForFinalTrack(
                    trafficApi,
                    request,
                    modifiedEntity,
                    bySource,
                    ref stats);
                if (modifiedEntity != Entity.Null && EntityManager.Exists(modifiedEntity))
                {
                    AddMarkerIfMissing<Deleted>(modifiedEntity);
                }
            }

            trafficApi.ClearBuffer(modifiedBuffer);
            for (int i = 0; i < m_KeptTrafficConnections.Count; i++)
            {
                trafficApi.AddBufferElement(modifiedBuffer, m_KeptTrafficConnections[i]);
            }

            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> pair in bySource)
            {
                if (!TryFindMappingEndpoint(request, pair.Key.Edge, pair.Key.LaneIndex, source: true, out LaneEndpoint sourceEndpoint))
                {
                    stats.Skipped++;
                    continue;
                }

                Entity modifiedConnectionEntity = EntityManager.CreateEntity();
                trafficApi.AddDataOwner(EntityManager, modifiedConnectionEntity, request.SplitNode);
                trafficApi.AddFakePrefabRef(EntityManager, modifiedConnectionEntity);
                object generatedBuffer = trafficApi.AddGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity);
                int sourceConnectionCount = 0;
                foreach (LaneMapping mapping in pair.Value.Values)
                {
                    if (!TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                    {
                        stats.Skipped++;
                        continue;
                    }

                    PathMethod method = RestrictTrafficPathMethodToEndpoints(
                        mapping.Method,
                        sourceEndpoint,
                        targetEndpoint);
                    if (method == 0)
                    {
                        stats.Skipped++;
                        continue;
                    }

                    trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                        mapping.SourceEdge,
                        mapping.TargetEdge,
                        mapping.SourceLaneIndex,
                        mapping.TargetLaneIndex,
                        new float3x2(
                            sourceEndpoint.LanePosition,
                            targetEndpoint.LanePosition),
                        new int4(
                            sourceEndpoint.CarriagewayAndGroup,
                            targetEndpoint.CarriagewayAndGroup),
                        method,
                        false));
                    sourceConnectionCount++;
                    if ((method & PathMethod.Road) != 0)
                    {
                        stats.RoadConnectionsPreserved++;
                    }

                    if ((method & PathMethod.Track) != 0)
                    {
                        stats.TrackConnections++;
                    }
                }

                if (sourceConnectionCount == 0)
                {
                    stats.Skipped++;
                    if (modifiedConnectionEntity != Entity.Null && EntityManager.Exists(modifiedConnectionEntity))
                    {
                        AddMarkerIfMissing<Deleted>(modifiedConnectionEntity);
                    }

                    continue;
                }

                trafficApi.AddBufferElement(modifiedBuffer, trafficApi.CreateModifiedLaneConnection(
                    pair.Key.LaneIndex,
                    sourceEndpoint.CarriagewayAndGroup,
                    sourceEndpoint.LanePosition,
                    pair.Key.Edge,
                    modifiedConnectionEntity));
                stats.WrittenSources++;
            }

            if (stats.WrittenSources == 0 || stats.TrackConnections == 0)
            {
                stats.Reason = $"noFinalTrackWritten sources={stats.WrittenSources} trackConnections={stats.TrackConnections}";
                return false;
            }

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, request.SplitNode);
            MarkForLaneRebuild(request);
            stats.Reason = "ok";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Final track Traffic write counts splitNode={FormatEntity(request.SplitNode)} removedExisting={stats.RemovedExisting} preservedExisting={m_KeptTrafficConnections.Count} writtenSources={stats.WrittenSources} roadConnectionsPreserved={stats.RoadConnectionsPreserved} trackConnections={stats.TrackConnections} skipped={stats.Skipped} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}].");
            return true;
        }

        private void CopyExistingGeneratedConnectionsForFinalTrack(
            TrafficApi trafficApi,
            Request request,
            Entity modifiedEntity,
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            ref FinalTrackWriteStats stats)
        {
            if (modifiedEntity == Entity.Null ||
                !EntityManager.Exists(modifiedEntity) ||
                !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
            {
                return;
            }

            object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
            int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
            for (int i = 0; i < generatedLength; i++)
            {
                object generated = trafficApi.GetBufferItem(generatedBuffer, i);
                Entity sourceEdge = trafficApi.GetGeneratedConnectionSource(generated);
                Entity targetEdge = trafficApi.GetGeneratedConnectionTarget(generated);
                int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
                LaneMapping mapping = new LaneMapping
                {
                    SourceEdge = sourceEdge,
                    TargetEdge = targetEdge,
                    SourceLaneIndex = laneIndexMap.x & 0xff,
                    TargetLaneIndex = laneIndexMap.y & 0xff,
                    Method = SanitizeTrafficPathMethod(trafficApi.GetGeneratedConnectionMethod(generated)),
                    IsBranch = false,
                    IsTrackPreservation = false
                };

                if (!TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out LaneEndpoint sourceEndpoint) ||
                    !TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                {
                    stats.Skipped++;
                    continue;
                }

                mapping.Method = RestrictTrafficPathMethodToEndpoints(
                    mapping.Method,
                    sourceEndpoint,
                    targetEndpoint);
                if (mapping.Method == 0)
                {
                    stats.Skipped++;
                    continue;
                }

                AddOrMergeFinalTrafficMapping(bySource, mapping);
            }
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
                existing.Method = SanitizeTrafficPathMethod(existing.Method | mapping.Method);
                existing.IsBranch |= mapping.IsBranch;
                existing.IsTrackPreservation |= mapping.IsTrackPreservation;
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
                output.Add(mapping);
            }
        }

        private static PathMethod GetRoadFixMethod(PathMethod method)
        {
            return PathMethod.Road;
        }

        private static PathMethod GetTrackFixMethod(PathMethod method)
        {
            method = SanitizeTrafficPathMethod(method);
            return (method & PathMethod.Track) != 0 ? method : 0;
        }

        private bool TryRebuildConnectorLanes(ref Request request, out DirectRebuildStats stats)
        {
            stats = default;
            if (request.Mappings == null || request.Mappings.Length == 0)
            {
                stats.Reason = "missing expected mappings";
                return false;
            }

            if (!EntityManager.TryGetBuffer(request.SplitNode, false, out DynamicBuffer<SubLane> subLanes))
            {
                stats.Reason = "split node has no SubLane buffer";
                return false;
            }

            int nextNodeLaneIndex = GetNextNodeLaneIndex(request.SplitNode, subLanes);
            m_RemoveSubLaneIndexes.Clear();

            int preflightNextNodeLaneIndex = nextNodeLaneIndex;
            if (!TryPreflightRebuildConnectorDirection(
                    request,
                    subLanes,
                    request.Mappings,
                    request.SourceLanes,
                    request.TargetLanes,
                    request.OuterEdge,
                    request.PocketEdge,
                    "forward",
                    preflightNextNodeLaneIndex,
                    out int forwardMissingClones,
                    out string forwardReason))
            {
                stats.Reason = forwardReason;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight skipped before mutation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason}.");
                return false;
            }

            preflightNextNodeLaneIndex += forwardMissingClones;
            string reverseReason = request.Mode == RepairMode.ShortEdgeTransition
                ? "short-edge-transition-reverse-not-restored"
                : "standard-reverse-not-rebuilt";
            if (request.Mode == RepairMode.BalancedOppositeTarget)
            {
                if (request.ReverseMappings == null || request.ReverseMappings.Length == 0)
                {
                    stats.Reason = "balanced reverse mappings missing";
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight skipped before mutation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason} reverse={stats.Reason}.");
                    return false;
                }

                if (!TryPreflightRebuildConnectorDirection(
                        request,
                        subLanes,
                        request.ReverseMappings,
                        request.ReverseSourceLanes,
                        request.ReverseTargetLanes,
                        request.PocketEdge,
                        request.OuterEdge,
                        "balanced-reverse",
                        preflightNextNodeLaneIndex,
                        out int reverseMissingClones,
                        out reverseReason))
                {
                    stats.Reason = reverseReason;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight skipped before mutation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason} reverse={reverseReason}.");
                    return false;
                }

                preflightNextNodeLaneIndex += reverseMissingClones;
            }
            else if (request.Mode == RepairMode.ShortEdgeTransition &&
                     request.ReverseMappings != null &&
                     request.ReverseMappings.Length > 0)
            {
                if (!TryPreflightRebuildConnectorDirection(
                        request,
                        subLanes,
                        request.ReverseMappings,
                        request.ReverseSourceLanes,
                        request.ReverseTargetLanes,
                        request.PocketEdge,
                        request.OuterEdge,
                        "short-edge-transition-reverse",
                        preflightNextNodeLaneIndex,
                        out int reverseMissingClones,
                        out reverseReason))
                {
                    stats.Reason = reverseReason;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight skipped before mutation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason} reverse={reverseReason}.");
                    return false;
                }

                preflightNextNodeLaneIndex += reverseMissingClones;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight ok splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason} reverse={reverseReason} startNodeLaneIndex={nextNodeLaneIndex} preflightNextNodeLaneIndex={preflightNextNodeLaneIndex}.");

            if (!TryRebuildConnectorDirection(
                    request,
                    subLanes,
                    request.Mappings,
                    request.SourceLanes,
                    request.TargetLanes,
                    request.OuterEdge,
                    request.PocketEdge,
                    "forward",
                    ref nextNodeLaneIndex,
                    ref stats,
                    out forwardReason))
            {
                stats.Reason = forwardReason;
                return false;
            }

            if (request.Mode == RepairMode.BalancedOppositeTarget)
            {
                if (!TryRebuildConnectorDirection(
                        request,
                        subLanes,
                        request.ReverseMappings,
                        request.ReverseSourceLanes,
                        request.ReverseTargetLanes,
                        request.PocketEdge,
                        request.OuterEdge,
                        "balanced-reverse",
                        ref nextNodeLaneIndex,
                        ref stats,
                        out reverseReason))
                {
                    stats.Reason = reverseReason;
                    return false;
                }
            }
            else if (request.Mode == RepairMode.ShortEdgeTransition &&
                     request.ReverseMappings != null &&
                     request.ReverseMappings.Length > 0)
            {
                if (!TryRebuildConnectorDirection(
                        request,
                        subLanes,
                        request.ReverseMappings,
                        request.ReverseSourceLanes,
                        request.ReverseTargetLanes,
                        request.PocketEdge,
                        request.OuterEdge,
                        "short-edge-transition-reverse",
                        ref nextNodeLaneIndex,
                        ref stats,
                        out reverseReason))
                {
                    stats.Reason = reverseReason;
                    return false;
                }
            }

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            for (int i = 0; i < m_StaleConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_StaleConnectorLanes[i];
                QueueDeleteConnector(connector.Entity);
                m_RemoveSubLaneIndexes.Add(connector.SubLaneIndex);
                stats.Deleted++;
                stats.DeletedUturn++;
            }

            string trackForwardReason = "trackForward deferred until final Traffic write";
            string trackReverseReason = "trackReverse deferred until final Traffic write";
            if (request.FinalTrackTrafficWritten || !HasTrackPreservationMappings(request))
            {
                RestoreTrackConnectorDirection(
                    request,
                    subLanes,
                    request.TrackForwardMappings,
                    request.TrackForwardSourceLanes,
                    request.TrackForwardTargetLanes,
                    request.OuterEdge,
                    request.PocketEdge,
                    "trackForward",
                    ref nextNodeLaneIndex,
                    ref stats,
                    out trackForwardReason);
                RestoreTrackConnectorDirection(
                    request,
                    subLanes,
                    request.TrackReverseMappings,
                    request.TrackReverseSourceLanes,
                    request.TrackReverseTargetLanes,
                    request.PocketEdge,
                    request.OuterEdge,
                    "trackReverse",
                    ref nextNodeLaneIndex,
                    ref stats,
                    out trackReverseReason);
            }

            m_RemoveSubLaneIndexes.Sort();
            int lastRemovedIndex = -1;
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
                    lastRemovedIndex = index;
                }
            }

            stats.Reason = "ok";
            if (stats.Kept > 0 || stats.Cloned > 0 || stats.Deleted > 0 || stats.TrackKept > 0 || stats.TrackCloned > 0)
            {
                MarkUpdatedIfExists(request.SplitNode);
                MarkUpdatedIfExists(request.OuterEdge);
                MarkUpdatedIfExists(request.PocketEdge);
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct rebuild result splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} expected={FormatMappings(request.Mappings)} reverseExpected={FormatMappings(request.ReverseMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] staleUturn={m_StaleConnectorLanes.Count} reverseReason={reverseReason} trackForward={trackForwardReason} trackReverse={trackReverseReason} kept={stats.Kept} cloned={stats.Cloned} deleted={stats.Deleted} deletedUturn={stats.DeletedUturn} trackKept={stats.TrackKept} trackCloned={stats.TrackCloned} trackSkipped={stats.TrackSkipped} updated={stats.Updated}.");
            return true;
        }

        private bool TryPreflightRebuildConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            LaneMapping[] mappings,
            LaneEndpoint[] sourceLanes,
            LaneEndpoint[] targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            int nextNodeLaneIndex,
            out int missingCloneCount,
            out string reason)
        {
            missingCloneCount = 0;
            reason = string.Empty;
            if (mappings == null || mappings.Length == 0)
            {
                reason = $"{direction} missing expected mappings";
                return false;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_ConnectorLanes);
            if (m_ConnectorLanes.Count == 0)
            {
                reason = $"{direction} no existing connector lanes to use as templates sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)}";
                return false;
            }

            HashSet<ConnectionKey> existingKeys = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                existingKeys.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (existingKeys.Contains(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out _) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out _))
                {
                    reason = $"{direction} missing endpoint source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (!TryFindConnectorTemplate(mapping, out _))
                {
                    reason = $"{direction} missing clone template source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                missingCloneCount++;
            }

            if (nextNodeLaneIndex + missingCloneCount > ushort.MaxValue)
            {
                reason = $"{direction} node lane index exhausted next={nextNodeLaneIndex} missing={missingCloneCount}";
                return false;
            }

            reason = $"{direction} preflight-ok expected={FormatConnectionSet(expected)} existing={existingKeys.Count} missingClones={missingCloneCount}";
            return true;
        }

        private bool TryRebuildConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            LaneMapping[] mappings,
            LaneEndpoint[] sourceLanes,
            LaneEndpoint[] targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            ref int nextNodeLaneIndex,
            ref DirectRebuildStats stats,
            out string reason)
        {
            reason = string.Empty;
            if (mappings == null || mappings.Length == 0)
            {
                reason = $"{direction} missing expected mappings";
                return false;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_ConnectorLanes);
            if (m_ConnectorLanes.Count == 0)
            {
                reason = $"{direction} no existing connector lanes to use as templates sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)}";
                return false;
            }

            HashSet<ConnectionKey> existingKeys = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                existingKeys.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
            }

            int missingCloneCount = 0;
            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (existingKeys.Contains(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out _) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out _))
                {
                    reason = $"{direction} missing endpoint source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (!TryFindConnectorTemplate(mapping, out _))
                {
                    reason = $"{direction} missing clone template source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                missingCloneCount++;
            }

            if (nextNodeLaneIndex + missingCloneCount > ushort.MaxValue)
            {
                reason = $"{direction} node lane index exhausted next={nextNodeLaneIndex} missing={missingCloneCount}";
                return false;
            }

            Dictionary<ConnectionKey, ConnectorLane> kept = new Dictionary<ConnectionKey, ConnectorLane>();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                ConnectionKey key = new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex);
                if (expected.Contains(key) && !kept.ContainsKey(key))
                {
                    kept.Add(key, connector);
                    ClearUnsafeFlags(connector.Entity);
                    MarkUpdatedIfExists(connector.Entity);
                    stats.Kept++;
                    stats.Updated++;
                    continue;
                }

                QueueDeleteConnector(connector.Entity);
                m_RemoveSubLaneIndexes.Add(connector.SubLaneIndex);
                stats.Deleted++;
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (kept.ContainsKey(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out LaneEndpoint source) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out LaneEndpoint target))
                {
                    reason = $"{direction} missing endpoint source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (!TryFindConnectorTemplate(mapping, out ConnectorLane template))
                {
                    reason = $"{direction} missing clone template source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (nextNodeLaneIndex > ushort.MaxValue)
                {
                    reason = $"{direction} node lane index exhausted next={nextNodeLaneIndex}";
                    return false;
                }

                Entity clone = CloneConnectorLane(request, template, source, target, (ushort)nextNodeLaneIndex++);
                PathMethod roadOnlyPathMethods = GetRoadFixMethod(template.PathMethods);
                subLanes.Add(new SubLane
                {
                    m_SubLane = clone,
                    m_PathMethods = roadOnlyPathMethods
                });
                kept.Add(key, new ConnectorLane
                {
                    Entity = clone,
                    SubLaneIndex = subLanes.Length - 1,
                    PathMethods = roadOnlyPathMethods,
                    SourceLaneIndex = mapping.SourceLaneIndex,
                    TargetLaneIndex = mapping.TargetLaneIndex
                });
                stats.Cloned++;
                stats.Updated++;
            }

            reason = $"{direction} ok expected={FormatConnectionSet(expected)} existing={existingKeys.Count} kept={kept.Count} missingClones={missingCloneCount}";
            return true;
        }

        private void RestoreTrackConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            LaneMapping[] mappings,
            LaneEndpoint[] sourceLanes,
            LaneEndpoint[] targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            ref int nextNodeLaneIndex,
            ref DirectRebuildStats stats,
            out string reason)
        {
            if (mappings == null || mappings.Length == 0)
            {
                reason = $"{direction} no expected track mappings";
                return;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            Dictionary<ConnectionKey, LaneMapping> expectedMappings = new Dictionary<ConnectionKey, LaneMapping>();
            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                expected.Add(key);
                if (!expectedMappings.ContainsKey(key))
                {
                    expectedMappings.Add(key, mapping);
                }
            }

            CollectTrackConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_TrackConnectorLanes);
            HashSet<ConnectionKey> actual = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                ConnectionKey key = new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex);
                if (!expected.Contains(key))
                {
                    continue;
                }

                if ((connector.PathMethods & PathMethod.Track) != 0)
                {
                    actual.Add(key);
                    ClearUnsafeFlags(connector.Entity);
                    MarkUpdatedIfExists(connector.Entity);
                    stats.TrackKept++;
                    stats.Updated++;
                    continue;
                }

                if (!expectedMappings.TryGetValue(key, out LaneMapping mapping) ||
                    (mapping.Method & PathMethod.Track) == 0 ||
                    connector.SubLaneIndex < 0 ||
                    connector.SubLaneIndex >= subLanes.Length)
                {
                    continue;
                }

                SubLane subLane = subLanes[connector.SubLaneIndex];
                PathMethod restoredMethods = SanitizeTrafficPathMethod(subLane.m_PathMethods | mapping.Method);
                if ((restoredMethods & PathMethod.Track) == 0)
                {
                    continue;
                }

                subLane.m_PathMethods = restoredMethods;
                subLanes[connector.SubLaneIndex] = subLane;
                actual.Add(key);
                MarkUpdatedIfExists(connector.Entity);
                stats.TrackKept++;
                stats.Updated++;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Restored shared track method on existing connector direction={direction} connector={FormatEntity(connector.Entity)} splitNode={FormatEntity(request.SplitNode)} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex} oldMethods=[{connector.PathMethods}] newMethods=[{restoredMethods}] mappingMethod=[{mapping.Method}].");
            }

            int cloned = 0;
            int skipped = 0;
            List<string> skipReasons = new List<string>(4);
            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (actual.Contains(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out LaneEndpoint source) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out LaneEndpoint target))
                {
                    skipped++;
                    skipReasons.Add($"endpointMissing {mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}");
                    continue;
                }

                if (!TryFindTrackConnectorTemplate(mapping, out ConnectorLane template) &&
                    !TryFindSnapshotTrackConnectorTemplate(mapping, out template))
                {
                    skipped++;
                    skipReasons.Add($"templateMissing {mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}");
                    continue;
                }

                if (nextNodeLaneIndex > ushort.MaxValue)
                {
                    skipped++;
                    skipReasons.Add($"nodeLaneIndexExhausted next={nextNodeLaneIndex}");
                    continue;
                }

                PathMethod restoredPathMethods = GetTrackFixMethod(mapping.Method);
                if ((restoredPathMethods & PathMethod.Track) == 0)
                {
                    skipped++;
                    skipReasons.Add($"methodMissingTrack {mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}");
                    continue;
                }

                Entity clone = CloneConnectorLane(request, template, source, target, (ushort)nextNodeLaneIndex++);
                subLanes.Add(new SubLane
                {
                    m_SubLane = clone,
                    m_PathMethods = restoredPathMethods
                });
                actual.Add(key);
                cloned++;
                stats.TrackCloned++;
                stats.Updated++;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Restored track connector lane direction={direction} clone={FormatEntity(clone)} template={FormatEntity(template.Entity)} splitNode={FormatEntity(request.SplitNode)} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} source={mapping.SourceLaneIndex}/{FormatEntity(source.LaneEntity)} target={mapping.TargetLaneIndex}/{FormatEntity(target.LaneEntity)} method=[{restoredPathMethods}] snapshotMethod=[{mapping.Method}] templateMethods=[{template.PathMethods}].");
            }

            stats.TrackSkipped += skipped;
            reason = $"{direction} expected={FormatConnectionSet(expected)} actualBefore={FormatConnectionSet(actual)} templates={m_TrackConnectorLanes.Count} cloned={cloned} skipped={skipped} trackSkippedReason={FormatStringList(skipReasons)}";
            if (skipped > 0)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Track connector restore skipped splitNode={FormatEntity(request.SplitNode)} direction={direction} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} expected={FormatMappings(mappings)} actual={FormatConnectionSet(actual)} trackSkippedReason={FormatStringList(skipReasons)} trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}).");
            }
        }

        private bool TryFindSnapshotTrackConnectorTemplate(LaneMapping mapping, out ConnectorLane template)
        {
            if (mapping.TemplateEntity != Entity.Null &&
                EntityManager.Exists(mapping.TemplateEntity) &&
                (mapping.TemplatePathMethods & PathMethod.Track) != 0)
            {
                template = new ConnectorLane
                {
                    Entity = mapping.TemplateEntity,
                    PathMethods = mapping.TemplatePathMethods,
                    SourceEdge = mapping.SourceEdge,
                    TargetEdge = mapping.TargetEdge,
                    SourceLaneIndex = mapping.SourceLaneIndex,
                    TargetLaneIndex = mapping.TargetLaneIndex
                };
                return true;
            }

            template = default;
            return false;
        }

        private bool TryFindTrackConnectorTemplate(LaneMapping mapping, out ConnectorLane template)
        {
            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                if ((connector.PathMethods & PathMethod.Track) != 0 &&
                    connector.SourceLaneIndex == mapping.SourceLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                if ((connector.PathMethods & PathMethod.Track) != 0 &&
                    connector.TargetLaneIndex == mapping.TargetLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                if ((connector.PathMethods & PathMethod.Track) != 0)
                {
                    template = connector;
                    return true;
                }
            }

            template = default;
            return false;
        }

        private Entity CloneConnectorLane(Request request, ConnectorLane template, LaneEndpoint source, LaneEndpoint target, ushort middleLaneIndex)
        {
            Entity clone = EntityManager.Instantiate(template.Entity);
            if (EntityManager.HasComponent<Deleted>(clone))
            {
                EntityManager.RemoveComponent<Deleted>(clone);
            }

            Lane lane = EntityManager.GetComponentData<Lane>(clone);
            lane.m_StartNode = source.PathNode;
            lane.m_MiddleNode = new PathNode(request.SplitNode, middleLaneIndex);
            lane.m_EndNode = target.PathNode;
            EntityManager.SetComponentData(clone, lane);

            if (EntityManager.TryGetComponent(clone, out Curve curve))
            {
                curve.m_Bezier = BuildConnectorCurve(source, target);
                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(clone, curve);
            }

            ClearUnsafeFlags(clone);
            if (EntityManager.HasComponent<Owner>(clone))
            {
                EntityManager.SetComponentData(clone, new Owner { m_Owner = request.SplitNode });
            }
            else
            {
                EntityManager.AddComponentData(clone, new Owner { m_Owner = request.SplitNode });
            }

            AddMarkerIfMissing<Created>(clone);
            AddMarkerIfMissing<Updated>(clone);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cloned connector lane clone={FormatEntity(clone)} template={FormatEntity(template.Entity)} splitNode={FormatEntity(request.SplitNode)} source={source.LaneIndex}/{FormatEntity(source.LaneEntity)} target={target.LaneIndex}/{FormatEntity(target.LaneEntity)} middleIndex={middleLaneIndex}.");
            return clone;
        }

        private static Bezier4x3 BuildConnectorCurve(LaneEndpoint source, LaneEndpoint target)
        {
            float distance = math.max(1f, math.distance(source.Position.xz, target.Position.xz));
            float tangentLength = math.min(12f, distance * 0.5f);
            float3 sourceTangent = new float3(source.TravelDirection.x, 0f, source.TravelDirection.y) * tangentLength;
            float3 targetTangent = new float3(target.TravelDirection.x, 0f, target.TravelDirection.y) * tangentLength;
            return NetUtils.FitCurve(source.Position, sourceTangent, -targetTangent, target.Position);
        }

        private bool TryFindConnectorTemplate(LaneMapping mapping, out ConnectorLane template)
        {
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                if (connector.SourceLaneIndex == mapping.SourceLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                if (connector.TargetLaneIndex == mapping.TargetLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            template = m_ConnectorLanes[0];
            return template.Entity != Entity.Null;
        }

        private static bool TryFindLaneEndpoint(IReadOnlyList<LaneEndpoint> lanes, int laneIndex, out LaneEndpoint lane)
        {
            if (lanes != null)
            {
                for (int i = 0; i < lanes.Count; i++)
                {
                    if (lanes[i].LaneIndex == laneIndex)
                    {
                        lane = lanes[i];
                        return true;
                    }
                }
            }

            lane = default;
            return false;
        }

        private static bool TryFindMappingEndpoint(Request request, Entity edge, int laneIndex, bool source, out LaneEndpoint lane)
        {
            if (source)
            {
                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.SourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.TrackForwardSourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.ReverseSourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.TrackReverseSourceLanes, laneIndex, out lane))
                {
                    return true;
                }
            }
            else
            {
                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.TargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.TrackForwardTargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.ReverseTargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.TrackReverseTargetLanes, laneIndex, out lane))
                {
                    return true;
                }
            }

            lane = default;
            return false;
        }

        private int GetNextNodeLaneIndex(Entity node, DynamicBuffer<SubLane> subLanes)
        {
            int maxIndex = -1;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity laneEntity = subLanes[i].m_SubLane;
                if (laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane))
                {
                    continue;
                }

                IncludeNodePathIndex(node, lane.m_StartNode, ref maxIndex);
                IncludeNodePathIndex(node, lane.m_MiddleNode, ref maxIndex);
                IncludeNodePathIndex(node, lane.m_EndNode, ref maxIndex);
            }

            return maxIndex + 1;
        }

        private static void IncludeNodePathIndex(Entity node, PathNode pathNode, ref int maxIndex)
        {
            if (pathNode.GetOwnerIndex() == node.Index)
            {
                maxIndex = math.max(maxIndex, pathNode.GetLaneIndex());
            }
        }

        private void QueueDeleteConnector(Entity laneEntity)
        {
            if (laneEntity == Entity.Null || !EntityManager.Exists(laneEntity))
            {
                return;
            }

            AddMarkerIfMissing<Deleted>(laneEntity);
            AddMarkerIfMissing<Updated>(laneEntity);
        }

        private void ClearUnsafeFlags(Entity laneEntity)
        {
            if (EntityManager.TryGetComponent(laneEntity, out NetCarLane carLane))
            {
                CarLaneFlags oldFlags = carLane.m_Flags;
                carLane.m_Flags &= ~(CarLaneFlags.Unsafe | CarLaneFlags.Forbidden);
                if (carLane.m_Flags != oldFlags)
                {
                    EntityManager.SetComponentData(laneEntity, carLane);
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cleared unsafe flags connector={FormatEntity(laneEntity)} oldFlags={oldFlags} newFlags={carLane.m_Flags}.");
                }
            }
        }

        private void AddMarkerIfMissing<T>(Entity entity) where T : unmanaged, IComponentData
        {
            if (!EntityManager.HasComponent<T>(entity))
            {
                EntityManager.AddComponent<T>(entity);
            }
        }

        private bool VerifyConnectorLanes(Request request)
        {
            if (request.Mappings == null || request.Mappings.Length == 0)
            {
                return false;
            }

            bool forwardMatches = VerifyConnectorDirection(
                request,
                request.Mappings,
                request.OuterEdge,
                request.PocketEdge,
                "forward",
                out string forwardDetail);
            bool reverseMatches = true;
            string reverseDetail = "standard-reverse-not-verified";
            if (request.Mode == RepairMode.BalancedOppositeTarget)
            {
                reverseMatches = request.ReverseMappings != null &&
                                 request.ReverseMappings.Length > 0 &&
                                 VerifyConnectorDirection(
                                     request,
                                     request.ReverseMappings,
                                     request.PocketEdge,
                                     request.OuterEdge,
                                     "balanced-reverse",
                                     out reverseDetail);
            }
            else if (request.Mode == RepairMode.ShortEdgeTransition)
            {
                if (request.ReverseMappings != null && request.ReverseMappings.Length > 0)
                {
                    reverseMatches = VerifyConnectorDirection(
                        request,
                        request.ReverseMappings,
                        request.PocketEdge,
                        request.OuterEdge,
                        "short-edge-transition-reverse",
                        out reverseDetail);
                }
                else
                {
                    reverseDetail = "short-edge-transition-reverse-skipped";
                }
            }

            bool verifyTrack = request.FinalTrackTrafficWritten || !HasTrackPreservationMappings(request);
            bool trackForwardMatches = true;
            bool trackReverseMatches = true;
            string trackForwardDetail = request.FinalTrackTrafficPending
                ? "trackForward pendingPreLaneFinalTrafficWrite"
                : "trackForward deferredUntilFinalTrafficWrite";
            string trackReverseDetail = request.FinalTrackTrafficPending
                ? "trackReverse pendingPreLaneFinalTrafficWrite"
                : "trackReverse deferredUntilFinalTrafficWrite";
            if (verifyTrack)
            {
                trackForwardMatches = VerifyTrackConnectorDirection(
                    request,
                    request.TrackForwardMappings,
                    request.OuterEdge,
                    request.PocketEdge,
                    "trackForward",
                    out trackForwardDetail);
                trackReverseMatches = VerifyTrackConnectorDirection(
                    request,
                    request.TrackReverseMappings,
                    request.PocketEdge,
                    request.OuterEdge,
                    "trackReverse",
                    out trackReverseDetail);
            }

            int staleUturnCount = CountStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, out string staleUturnSummary);
            bool matches = forwardMatches && reverseMatches && trackForwardMatches && trackReverseMatches && staleUturnCount == 0;
            if (!matches)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Connector verification mismatch splitNode={FormatEntity(request.SplitNode)} mode={request.Mode} verifyTrack={verifyTrack} finalTrackTrafficWritten={request.FinalTrackTrafficWritten} forward={forwardDetail} reverse={reverseDetail} trackForward={trackForwardDetail} trackReverse={trackReverseDetail} staleUturnCount={staleUturnCount} staleUturns={staleUturnSummary}.");
            }

            return matches;
        }

        private bool VerifyConnectorDirection(
            Request request,
            LaneMapping[] mappings,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            out string detail)
        {
            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            HashSet<ConnectionKey> actual = new HashSet<ConnectionKey>();
            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, m_ExistingConnectorLanes);
            for (int i = 0; i < m_ExistingConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ExistingConnectorLanes[i];
                actual.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
            }

            bool matches = expected.SetEquals(actual);
            detail = $"{direction} source={FormatEntity(sourceEdge)} target={FormatEntity(targetEdge)} expected={FormatConnectionSet(expected)} actual={FormatConnectionSet(actual)} connectors={m_ExistingConnectorLanes.Count}";
            return matches;
        }

        private bool VerifyTrackConnectorDirection(
            Request request,
            LaneMapping[] mappings,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            out string detail)
        {
            if (mappings == null || mappings.Length == 0)
            {
                detail = $"{direction} source={FormatEntity(sourceEdge)} target={FormatEntity(targetEdge)} expected=<none>";
                return true;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            HashSet<ConnectionKey> actual = new HashSet<ConnectionKey>();
            if (EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                CollectTrackConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_TrackConnectorLanes);
                for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
                {
                    ConnectorLane connector = m_TrackConnectorLanes[i];
                    if ((connector.PathMethods & PathMethod.Track) == 0)
                    {
                        continue;
                    }

                    actual.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
                }
            }
            else
            {
                m_TrackConnectorLanes.Clear();
            }

            bool matches = expected.SetEquals(actual);
            detail = $"{direction} source={FormatEntity(sourceEdge)} target={FormatEntity(targetEdge)} expected={FormatConnectionSet(expected)} actual={FormatConnectionSet(actual)} connectors={m_TrackConnectorLanes.Count} expectedMappings={FormatMappings(mappings)}";
            return matches;
        }

        private int CollectConnectorLanes(Entity splitNode, Entity outerEdge, Entity pocketEdge, List<ConnectorLane> output)
        {
            output.Clear();
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return 0;
            }

            CollectConnectorLanes(splitNode, outerEdge, pocketEdge, subLanes, output);
            return output.Count;
        }

        private void CollectConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, splitNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    sourceEdge != outerEdge ||
                    targetEdge != pocketEdge)
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private void CollectTrackConnectorLanes(
            Entity splitNode,
            Entity sourceEdge,
            Entity targetEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if (laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !IsTrackConnectorCandidate(laneEntity, subLane) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, splitNode, lane, out Entity actualSourceEdge, out Entity actualTargetEdge) ||
                    actualSourceEdge != sourceEdge ||
                    actualTargetEdge != targetEdge)
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, actualSourceEdge, actualTargetEdge));
            }
        }

        private ConnectorLane CreateConnectorLane(
            Entity laneEntity,
            int subLaneIndex,
            SubLane subLane,
            Lane lane,
            Entity sourceEdge,
            Entity targetEdge)
        {
            NetCarLane carLane = EntityManager.TryGetComponent(laneEntity, out NetCarLane laneComponent)
                ? laneComponent
                : default;
            LaneFlags laneFlags = default;
            TrackTypes trackTypes = default;
            bool hasTrackLaneData = false;
            if (EntityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef))
            {
                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData))
                {
                    laneFlags = laneData.m_Flags;
                }

                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out TrackLaneData trackLaneData))
                {
                    hasTrackLaneData = true;
                    trackTypes = trackLaneData.m_TrackTypes;
                }
            }

            return new ConnectorLane
            {
                Entity = laneEntity,
                SubLaneIndex = subLaneIndex,
                PathMethods = subLane.m_PathMethods,
                CarFlags = carLane.m_Flags,
                SourceEdge = sourceEdge,
                TargetEdge = targetEdge,
                SourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff,
                TargetLaneIndex = lane.m_EndNode.GetLaneIndex() & 0xff,
                LaneFlags = laneFlags,
                TrackTypes = trackTypes,
                HasTrackLaneData = hasTrackLaneData,
                HasNetTrackLane = EntityManager.HasComponent<NetTrackLane>(laneEntity)
            };
        }

        private bool IsTrackConnectorCandidate(Entity laneEntity, SubLane subLane)
        {
            if ((subLane.m_PathMethods & PathMethod.Track) != 0 ||
                EntityManager.HasComponent<NetTrackLane>(laneEntity))
            {
                return true;
            }

            if (EntityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef))
            {
                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out TrackLaneData _))
                {
                    return true;
                }

                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData) &&
                    (laneData.m_Flags & LaneFlags.Track) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void CollectSplitNodeConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            bool restrictToSplitPair = outerEdge != Entity.Null;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, splitNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    (restrictToSplitPair &&
                     (sourceEdge != outerEdge && sourceEdge != pocketEdge ||
                      targetEdge != outerEdge && targetEdge != pocketEdge)))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private int CountStaleSplitNodeUturnConnectorLanes(Entity splitNode, Entity outerEdge, Entity pocketEdge, out string summary)
        {
            summary = string.Empty;
            m_StaleConnectorLanes.Clear();
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return 0;
            }

            CollectStaleSplitNodeUturnConnectorLanes(splitNode, outerEdge, pocketEdge, subLanes, m_StaleConnectorLanes);
            summary = FormatConnectorLanes(m_StaleConnectorLanes);
            return m_StaleConnectorLanes.Count;
        }

        private void CollectStaleSplitNodeUturnConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            bool restrictToSplitPair = outerEdge != Entity.Null;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if (laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 &&
                     !IsTrackConnectorCandidate(laneEntity, subLane)) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !lane.m_StartNode.OwnerEquals(lane.m_EndNode) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, splitNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    sourceEdge != targetEdge ||
                    (restrictToSplitPair && sourceEdge != outerEdge && sourceEdge != pocketEdge))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private bool TryFindCleanupLaneEndpoint(
            Entity splitNode,
            Entity edge,
            int laneIndex,
            EndpointRole role,
            out LaneEndpoint endpoint)
        {
            List<LaneEndpoint> scratch = role == EndpointRole.SourceEndAtNode ? m_SourceLanes : m_TargetLanes;
            CollectEdgeTrafficLaneEndpoints(edge, splitNode, role, scratch);
            for (int i = 0; i < scratch.Count; i++)
            {
                LaneEndpoint candidate = scratch[i];
                if (candidate.LaneIndex == laneIndex)
                {
                    endpoint = candidate;
                    return true;
                }
            }

            endpoint = default;
            return false;
        }

        private int CountConnectorLanes(Entity splitNode, Entity outerEdge, Entity pocketEdge, out string summary)
        {
            int count = CollectConnectorLanes(splitNode, outerEdge, pocketEdge, m_ExistingConnectorLanes);
            summary = FormatConnectorLanes(m_ExistingConnectorLanes);
            return count;
        }

        private static float3 GetLaneCompositionPosition(LaneEndpoint[] lanes, int laneIndex)
        {
            if (lanes != null)
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    if (lanes[i].LaneIndex == laneIndex)
                    {
                        return lanes[i].LanePosition;
                    }
                }
            }

            return default;
        }

        private static int2 GetLaneCarriagewayAndGroup(LaneEndpoint[] lanes, int laneIndex)
        {
            if (lanes != null)
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    if (lanes[i].LaneIndex == laneIndex)
                    {
                        return lanes[i].CarriagewayAndGroup;
                    }
                }
            }

            return new int2(-1, -1);
        }

        private void MarkForLaneRebuild(Request request)
        {
            int updatedNodes = MarkUpdatedIfExists(request.SplitNode, out bool splitAlreadyUpdated) ? 1 : 0;
            int alreadyUpdatedNodes = splitAlreadyUpdated ? 1 : 0;
            int updatedEdges = 0;
            int alreadyUpdatedEdges = 0;
            int scannedEdges = 0;

            if (EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                scannedEdges = connectedEdges.Length;
                for (int i = 0; i < connectedEdges.Length; i++)
                {
                    Entity edgeEntity = connectedEdges[i].m_Edge;
                    if (edgeEntity == Entity.Null ||
                        !EntityManager.Exists(edgeEntity) ||
                        EntityManager.HasComponent<Deleted>(edgeEntity))
                    {
                        continue;
                    }

                    if (MarkUpdatedIfExists(edgeEntity, out bool edgeAlreadyUpdated))
                    {
                        updatedEdges++;
                    }
                    else if (edgeAlreadyUpdated)
                    {
                        alreadyUpdatedEdges++;
                    }

                    if (EntityManager.TryGetComponent(edgeEntity, out NetEdge edge))
                    {
                        Entity otherNode = edge.m_Start == request.SplitNode
                            ? edge.m_End
                            : edge.m_End == request.SplitNode
                                ? edge.m_Start
                                : Entity.Null;
                        if (MarkUpdatedIfExists(otherNode, out bool otherNodeAlreadyUpdated))
                        {
                            updatedNodes++;
                        }
                        else if (otherNodeAlreadyUpdated)
                        {
                            alreadyUpdatedNodes++;
                        }
                    }
                }
            }

            if (MarkUpdatedIfExists(request.PocketEdge, out bool pocketAlreadyUpdated))
            {
                updatedEdges++;
            }
            else if (pocketAlreadyUpdated)
            {
                alreadyUpdatedEdges++;
            }

            if (MarkUpdatedIfExists(request.OuterEdge, out bool outerAlreadyUpdated))
            {
                updatedEdges++;
            }
            else if (outerAlreadyUpdated)
            {
                alreadyUpdatedEdges++;
            }

            if (MarkUpdatedIfExists(request.OriginalEdge, out bool originalAlreadyUpdated))
            {
                updatedEdges++;
            }
            else if (originalAlreadyUpdated)
            {
                alreadyUpdatedEdges++;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Marked lane rebuild neighborhood splitNode={FormatEntity(request.SplitNode)} scannedConnectedEdges={scannedEdges} addedUpdatedNodes={updatedNodes} alreadyUpdatedNodes={alreadyUpdatedNodes} addedUpdatedEdges={updatedEdges} alreadyUpdatedEdges={alreadyUpdatedEdges} pocketEdge={FormatEntity(request.PocketEdge)} outerEdge={FormatEntity(request.OuterEdge)} originalEdge={FormatEntity(request.OriginalEdge)} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
        }

        private bool MarkUpdatedIfExists(Entity entity)
        {
            return MarkUpdatedIfExists(entity, out _);
        }

        private bool MarkUpdatedIfExists(Entity entity, out bool alreadyUpdated)
        {
            alreadyUpdated = false;
            if (entity != Entity.Null &&
                EntityManager.Exists(entity))
            {
                alreadyUpdated = EntityManager.HasComponent<Updated>(entity);
                if (!alreadyUpdated)
                {
                    EntityManager.AddComponent<Updated>(entity);
                    return true;
                }
            }

            return false;
        }

        private bool TryGetTrafficApi(out TrafficApi trafficApi, out string error)
        {
            if (m_TrafficApi != null)
            {
                trafficApi = m_TrafficApi;
                error = string.Empty;
                return true;
            }

            if (TrafficApi.TryCreate(out m_TrafficApi, out error))
            {
                trafficApi = m_TrafficApi;
                Mod.UpdateTrafficRuntimeStatus(true, "none", 0);
                Mod.LogEssential("[SplitLaneConnectionFix] Traffic runtime detected; connection repair is enabled.");
                return true;
            }

            trafficApi = null;
            return false;
        }

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
            if (lanes == null || lanes.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", lanes.Select(lane => $"{lane.Endpoint}{lane.LaneIndex}|C{lane.OppositeLaneIndex}@{lane.Lateral:0.##}/{FormatEntity(lane.LaneEntity)} lanePos={FormatFloat3(lane.LanePosition)} cg={lane.CarriagewayAndGroup} methods=[{lane.PathMethods}] laneFlags=[{lane.LaneFlags}] carFlags=[{lane.CarFlags}] roadTypes=[{lane.RoadTypes}] trackTypes=[{lane.TrackTypes}] hasCarData={lane.HasCarLaneData} hasTrackData={lane.HasTrackLaneData} netTrack={lane.HasNetTrackLane}"));
        }

        private static string FormatFloat3(float3 value)
        {
            return DiagnosticFormat.Float3(value);
        }

        private static string FormatMappings(IReadOnlyList<LaneMapping> mappings)
        {
            if (mappings == null || mappings.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", mappings.Select(FormatMapping));
        }

        private static string FormatSnapshot(TransitionConnectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "<none>";
            }

            int count = snapshot.Mappings?.Length ?? 0;
            return $"{snapshot.Source}/{count} node={FormatEntity(snapshot.Node)} source={FormatEntity(snapshot.SourceEdge)} target={FormatEntity(snapshot.TargetEdge)} detail=({snapshot.Detail})";
        }

        private static string FormatSnapshotMappings(IReadOnlyList<TransitionConnectionSnapshotMapping> mappings)
        {
            if (mappings == null || mappings.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", mappings.Select(mapping => $"{mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}[{mapping.Method}] unsafe={mapping.IsUnsafe} srcLat={mapping.SourceLateral:0.##} tgtLat={mapping.TargetLateral:0.##}"));
        }

        private static string FormatMapping(LaneMapping mapping)
        {
            return $"{FormatEntity(mapping.SourceEdge)}:{mapping.SourceLaneIndex}->{FormatEntity(mapping.TargetEdge)}:{mapping.TargetLaneIndex}[{mapping.Method}]{(mapping.IsBranch ? "*" : string.Empty)}{(mapping.IsTrackPreservation ? "#track" : string.Empty)}";
        }

        private static string FormatConnectorLanes(IReadOnlyList<ConnectorLane> connectors)
        {
            if (connectors == null || connectors.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", connectors.Select(connector => $"{FormatEntity(connector.SourceEdge)}:{connector.SourceLaneIndex}->{FormatEntity(connector.TargetEdge)}:{connector.TargetLaneIndex}[{connector.PathMethods}] flags=[{connector.LaneFlags}] trackTypes=[{connector.TrackTypes}] trackData={connector.HasTrackLaneData} netTrack={connector.HasNetTrackLane}/{FormatEntity(connector.Entity)}"));
        }

        private static string FormatStringList(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "<none>";
            }

            return string.Join(" || ", values);
        }

        private static string FormatSourceLaneKeys(IEnumerable<SourceLaneKey> sourceLaneKeys)
        {
            if (sourceLaneKeys == null)
            {
                return "<none>";
            }

            string[] formatted = sourceLaneKeys
                .OrderBy(key => key.Edge.Index)
                .ThenBy(key => key.LaneIndex)
                .Select(key => $"{FormatEntity(key.Edge)}:{key.LaneIndex}")
                .ToArray();
            return formatted.Length == 0 ? "<none>" : string.Join(",", formatted);
        }

        private static string FormatConnectionSet(IEnumerable<ConnectionKey> set)
        {
            return string.Join(",", set.OrderBy(item => item.SourceLaneIndex).ThenBy(item => item.TargetLaneIndex).Select(item => $"{item.SourceLaneIndex}->{item.TargetLaneIndex}"));
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

        private enum EndpointRole
        {
            SourceEndAtNode,
            TargetStartAtNode
        }

        private enum TurnDirection
        {
            Ambiguous,
            Left,
            Right
        }

        private struct Request
        {
            public Entity IntersectionNode;
            public Entity FarIntersectionNode;
            public Entity SplitNode;
            public Entity OriginalEdge;
            public Entity OuterEdge;
            public Entity PocketEdge;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public RepairMode Mode;
            public int QueuedFrame;
            public int LaneDataRetries;
            public bool TrafficWritten;
            public bool FinalTrackTrafficWritten;
            public bool FinalTrackTrafficPending;
            public bool TrackSnapshotCaptured;
            public int TrafficWriteFrame;
            public int VerificationAttempts;
            public int StableVerificationFrames;
            public bool UturnCleanupPending;
            public bool RemoveAfterUturnCleanup;
            public bool ReverseTrackAuditLogged;
            public string UturnCleanupReason;
            public LaneEndpoint[] SourceLanes;
            public LaneEndpoint[] TargetLanes;
            public LaneEndpoint[] ReverseSourceLanes;
            public LaneEndpoint[] ReverseTargetLanes;
            public LaneEndpoint[] TrackForwardSourceLanes;
            public LaneEndpoint[] TrackForwardTargetLanes;
            public LaneEndpoint[] TrackReverseSourceLanes;
            public LaneEndpoint[] TrackReverseTargetLanes;
            public LaneMapping[] Mappings;
            public LaneMapping[] ReverseMappings;
            public LaneMapping[] TrackForwardMappings;
            public LaneMapping[] TrackReverseMappings;
            public string TrackSkippedReason;
            public TransitionConnectionSnapshot TransitionReverseSnapshot;
            public int BranchSourceLaneIndex;
            public int ExtraTargetLaneIndex;
            public TurnDirection Turn;
        }

        private struct LaneEndpoint
        {
            public Entity LaneEntity;
            public Entity Edge;
            public int LaneIndex;
            public int OppositeLaneIndex;
            public PathNode PathNode;
            public PathNode OppositePathNode;
            public float3 Position;
            public float3 LanePosition;
            public float2 TravelDirection;
            public int2 CarriagewayAndGroup;
            public float Lateral;
            public string Endpoint;
            public PathMethod PathMethods;
            public LaneFlags LaneFlags;
            public CarLaneFlags CarFlags;
            public RoadTypes RoadTypes;
            public TrackTypes TrackTypes;
            public bool HasCarLaneData;
            public bool HasTrackLaneData;
            public bool HasNetTrackLane;
        }

        private struct ConnectorLane
        {
            public Entity Entity;
            public int SubLaneIndex;
            public PathMethod PathMethods;
            public CarLaneFlags CarFlags;
            public Entity SourceEdge;
            public Entity TargetEdge;
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public LaneFlags LaneFlags;
            public TrackTypes TrackTypes;
            public bool HasTrackLaneData;
            public bool HasNetTrackLane;
        }

        private struct UturnCleanupSourcePlan
        {
            public SourceLaneKey Key;
            public LaneEndpoint Source;
            public int FirstConnection;
            public int ConnectionCount;
        }

        private struct UturnCleanupConnectionPlan
        {
            public Entity TargetEdge;
            public int TargetLaneIndex;
            public LaneEndpoint Target;
            public PathMethod Method;
            public bool IsUnsafe;
            public bool FromTrackSnapshot;
        }

        private struct LaneMapping
        {
            public Entity SourceEdge;
            public Entity TargetEdge;
            public Entity TemplateEntity;
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public PathMethod Method;
            public PathMethod TemplatePathMethods;
            public bool IsBranch;
            public bool IsTrackPreservation;
        }

        private struct SnapshotLaneOrder
        {
            public int LaneIndex;
            public float LateralSum;
            public int Count;
            public int FirstSnapshotOrder;

            public float AverageLateral => Count > 0 ? LateralSum / Count : 0f;
        }

        private struct CenterTurnCandidate
        {
            public Entity LaneEntity;
            public int SourceLaneIndex;
            public int TargetListIndex;
            public int TargetLaneIndex;
            public Entity TargetEdge;
            public TurnDirection Turn;
            public CarLaneFlags Flags;
        }

        private struct DirectRebuildStats
        {
            public int Kept;
            public int Cloned;
            public int Deleted;
            public int DeletedUturn;
            public int TrackKept;
            public int TrackCloned;
            public int TrackSkipped;
            public int Updated;
            public string Reason;
        }

        private struct TrackMappingStats
        {
            public int Connectors;
            public int Mappings;
            public int EndpointMisses;
            public int Skipped;
            public int TrackOnlyTargets;
            public int SharedTrackConnections;
        }

        private struct UturnCleanupWriteStats
        {
            public int StaleSourceLanes;
            public int WrittenSources;
            public int PreservedConnections;
            public int PreservedTrackConnections;
            public int TrackWrittenConnections;
            public int TrackSnapshotConnections;
            public int TrackSnapshotSkipped;
            public int TrackOnlyTargetConnections;
            public int SharedTrackConnections;
            public int EmptySources;
            public int NormalizedMethods;
            public int RemovedExisting;
            public int EndpointMisses;
            public string SourceLanes;
            public string RewriteSourceLanes;
            public string Reason;
        }

        private struct FinalTrackWriteStats
        {
            public int WrittenSources;
            public int RoadConnectionsPreserved;
            public int TrackConnections;
            public int Skipped;
            public int RemovedExisting;
            public string Reason;
        }

        private readonly struct ConnectionKey : IEquatable<ConnectionKey>
        {
            public readonly int SourceLaneIndex;
            public readonly int TargetLaneIndex;

            public ConnectionKey(int sourceLaneIndex, int targetLaneIndex)
            {
                SourceLaneIndex = sourceLaneIndex;
                TargetLaneIndex = targetLaneIndex;
            }

            public bool Equals(ConnectionKey other)
            {
                return SourceLaneIndex == other.SourceLaneIndex && TargetLaneIndex == other.TargetLaneIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is ConnectionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (SourceLaneIndex * 397) ^ TargetLaneIndex;
                }
            }
        }

        private readonly struct SourceLaneKey : IEquatable<SourceLaneKey>
        {
            public readonly Entity Edge;
            public readonly int LaneIndex;

            public SourceLaneKey(Entity edge, int laneIndex)
            {
                Edge = edge;
                LaneIndex = laneIndex;
            }

            public bool Equals(SourceLaneKey other)
            {
                return Edge == other.Edge && LaneIndex == other.LaneIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is SourceLaneKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Edge.GetHashCode() * 397) ^ LaneIndex;
                }
            }
        }

        private readonly struct TargetLaneKey : IEquatable<TargetLaneKey>
        {
            public readonly Entity Edge;
            public readonly int LaneIndex;

            public TargetLaneKey(Entity edge, int laneIndex)
            {
                Edge = edge;
                LaneIndex = laneIndex;
            }

            public bool Equals(TargetLaneKey other)
            {
                return Edge == other.Edge && LaneIndex == other.LaneIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is TargetLaneKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Edge.GetHashCode() * 397) ^ LaneIndex;
                }
            }
        }

        private sealed class TrafficApi
        {
            private readonly Type m_ModifiedConnectionsType;
            private readonly Type m_DataOwnerType;
            private readonly Type m_ModifiedLaneConnectionsType;
            private readonly Type m_GeneratedConnectionType;
            private readonly Entity m_FakePrefabRef;
            private readonly MethodInfo m_AddModifiedLaneConnectionsBuffer;
            private readonly MethodInfo m_GetModifiedLaneConnectionsBuffer;
            private readonly MethodInfo m_HasModifiedLaneConnectionsBuffer;
            private readonly MethodInfo m_AddGeneratedConnectionBuffer;
            private readonly MethodInfo m_GetGeneratedConnectionBuffer;
            private readonly MethodInfo m_HasGeneratedConnectionBuffer;
            private readonly MethodInfo m_SetDataOwner;
            private readonly FieldInfo m_DataOwnerEntityField;
            private readonly FieldInfo m_ModifiedLaneIndexField;
            private readonly FieldInfo m_ModifiedCarriagewayAndGroupField;
            private readonly FieldInfo m_ModifiedLanePositionField;
            private readonly FieldInfo m_ModifiedEdgeField;
            private readonly FieldInfo m_ModifiedConnectionsField;
            private readonly FieldInfo m_GeneratedSourceField;
            private readonly FieldInfo m_GeneratedTargetField;
            private readonly FieldInfo m_GeneratedLaneIndexMapField;
            private readonly FieldInfo m_GeneratedCarriagewayAndGroupIndexMapField;
            private readonly FieldInfo m_GeneratedLanePositionMapField;
            private readonly FieldInfo m_GeneratedMethodField;
            private readonly FieldInfo m_GeneratedUnsafeField;

            private TrafficApi(
                Type modifiedConnectionsType,
                Type dataOwnerType,
                Type modifiedLaneConnectionsType,
                Type generatedConnectionType,
                Entity fakePrefabRef)
            {
                m_ModifiedConnectionsType = modifiedConnectionsType;
                m_DataOwnerType = dataOwnerType;
                m_ModifiedLaneConnectionsType = modifiedLaneConnectionsType;
                m_GeneratedConnectionType = generatedConnectionType;
                m_FakePrefabRef = fakePrefabRef;

                m_AddModifiedLaneConnectionsBuffer = MakeEntityManagerGeneric(nameof(EntityManager.AddBuffer), modifiedLaneConnectionsType, typeof(Entity));
                m_GetModifiedLaneConnectionsBuffer = MakeEntityManagerGeneric(nameof(EntityManager.GetBuffer), modifiedLaneConnectionsType, typeof(Entity), typeof(bool));
                m_HasModifiedLaneConnectionsBuffer = MakeEntityManagerGeneric(nameof(EntityManager.HasBuffer), modifiedLaneConnectionsType, typeof(Entity));
                m_AddGeneratedConnectionBuffer = MakeEntityManagerGeneric(nameof(EntityManager.AddBuffer), generatedConnectionType, typeof(Entity));
                m_GetGeneratedConnectionBuffer = MakeEntityManagerGeneric(nameof(EntityManager.GetBuffer), generatedConnectionType, typeof(Entity), typeof(bool));
                m_HasGeneratedConnectionBuffer = MakeEntityManagerGeneric(nameof(EntityManager.HasBuffer), generatedConnectionType, typeof(Entity));
                m_SetDataOwner = MakeEntityManagerGeneric(nameof(EntityManager.SetComponentData), dataOwnerType, typeof(Entity), dataOwnerType);

                m_DataOwnerEntityField = RequireField(dataOwnerType, "entity");
                m_ModifiedLaneIndexField = RequireField(modifiedLaneConnectionsType, "laneIndex");
                m_ModifiedCarriagewayAndGroupField = RequireField(modifiedLaneConnectionsType, "carriagewayAndGroup");
                m_ModifiedLanePositionField = RequireField(modifiedLaneConnectionsType, "lanePosition");
                m_ModifiedEdgeField = RequireField(modifiedLaneConnectionsType, "edgeEntity");
                m_ModifiedConnectionsField = RequireField(modifiedLaneConnectionsType, "modifiedConnections");
                m_GeneratedSourceField = RequireField(generatedConnectionType, "sourceEntity");
                m_GeneratedTargetField = RequireField(generatedConnectionType, "targetEntity");
                m_GeneratedLaneIndexMapField = RequireField(generatedConnectionType, "laneIndexMap");
                m_GeneratedCarriagewayAndGroupIndexMapField = RequireField(generatedConnectionType, "carriagewayAndGroupIndexMap");
                m_GeneratedLanePositionMapField = RequireField(generatedConnectionType, "lanePositionMap");
                m_GeneratedMethodField = RequireField(generatedConnectionType, "method");
                m_GeneratedUnsafeField = RequireField(generatedConnectionType, "isUnsafe");
            }

            public static bool TryCreate(out TrafficApi api, out string error)
            {
                api = null;
                error = string.Empty;

                Type modifiedConnectionsType = FindType("Traffic.Components.ModifiedConnections");
                Type dataOwnerType = FindType("Traffic.Components.DataOwner");
                Type modifiedLaneConnectionsType = FindType("Traffic.Components.LaneConnections.ModifiedLaneConnections");
                Type generatedConnectionType = FindType("Traffic.Components.LaneConnections.GeneratedConnection");
                Type modDefaultsSystemType = FindType("Traffic.Systems.ModDefaultsSystem");

                if (modifiedConnectionsType == null ||
                    dataOwnerType == null ||
                    modifiedLaneConnectionsType == null ||
                    generatedConnectionType == null ||
                    modDefaultsSystemType == null)
                {
                    error = $"missingTypes modifiedConnections={modifiedConnectionsType != null} dataOwner={dataOwnerType != null} modifiedLaneConnections={modifiedLaneConnectionsType != null} generatedConnection={generatedConnectionType != null} modDefaults={modDefaultsSystemType != null}";
                    return false;
                }

                FieldInfo fakePrefabRefField = modDefaultsSystemType.GetField("FakePrefabRef", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fakePrefabRefField == null)
                {
                    error = "missing Traffic.Systems.ModDefaultsSystem.FakePrefabRef";
                    return false;
                }

                Entity fakePrefabRef = (Entity)fakePrefabRefField.GetValue(null);
                if (fakePrefabRef == Entity.Null)
                {
                    error = "Traffic FakePrefabRef is Entity.Null; Traffic defaults have not initialized yet";
                    return false;
                }

                try
                {
                    api = new TrafficApi(
                        modifiedConnectionsType,
                        dataOwnerType,
                        modifiedLaneConnectionsType,
                        generatedConnectionType,
                        fakePrefabRef);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public object GetOrAddModifiedLaneConnectionsBuffer(EntityManager entityManager, Entity node)
            {
                bool hasBuffer = (bool)m_HasModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node });
                return hasBuffer
                    ? m_GetModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node, false })
                    : m_AddModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node });
            }

            public bool HasModifiedLaneConnectionsBuffer(EntityManager entityManager, Entity node)
            {
                return (bool)m_HasModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node });
            }

            public object GetModifiedLaneConnectionsBuffer(EntityManager entityManager, Entity node, bool readOnly)
            {
                return m_GetModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node, readOnly });
            }

            public object AddGeneratedConnectionBuffer(EntityManager entityManager, Entity entity)
            {
                return m_AddGeneratedConnectionBuffer.Invoke(entityManager, new object[] { entity });
            }

            public bool HasGeneratedConnectionBuffer(EntityManager entityManager, Entity entity)
            {
                return (bool)m_HasGeneratedConnectionBuffer.Invoke(entityManager, new object[] { entity });
            }

            public object GetGeneratedConnectionBuffer(EntityManager entityManager, Entity entity, bool readOnly)
            {
                return m_GetGeneratedConnectionBuffer.Invoke(entityManager, new object[] { entity, readOnly });
            }

            public int GetBufferLength(object buffer)
            {
                return (int)buffer.GetType().GetProperty("Length").GetValue(buffer);
            }

            public object GetBufferItem(object buffer, int index)
            {
                return buffer.GetType().GetProperty("Item").GetValue(buffer, new object[] { index });
            }

            public void ClearBuffer(object buffer)
            {
                buffer.GetType().GetMethod("Clear").Invoke(buffer, null);
            }

            public void AddBufferElement(object buffer, object element)
            {
                buffer.GetType().GetMethod("Add").Invoke(buffer, new[] { element });
            }

            public Entity GetModifiedConnectionEdge(object element)
            {
                return (Entity)m_ModifiedEdgeField.GetValue(element);
            }

            public int GetModifiedConnectionLaneIndex(object element)
            {
                return (int)m_ModifiedLaneIndexField.GetValue(element);
            }

            public Entity GetModifiedConnectionEntity(object element)
            {
                return (Entity)m_ModifiedConnectionsField.GetValue(element);
            }

            public Entity GetGeneratedConnectionSource(object element)
            {
                return (Entity)m_GeneratedSourceField.GetValue(element);
            }

            public Entity GetGeneratedConnectionTarget(object element)
            {
                return (Entity)m_GeneratedTargetField.GetValue(element);
            }

            public int2 GetGeneratedConnectionLaneIndexMap(object element)
            {
                return (int2)m_GeneratedLaneIndexMapField.GetValue(element);
            }

            public PathMethod GetGeneratedConnectionMethod(object element)
            {
                return (PathMethod)m_GeneratedMethodField.GetValue(element);
            }

            public bool GetGeneratedConnectionUnsafe(object element)
            {
                return (bool)m_GeneratedUnsafeField.GetValue(element);
            }

            public object CreateModifiedLaneConnection(
                int laneIndex,
                int2 carriagewayAndGroup,
                float3 lanePosition,
                Entity edgeEntity,
                Entity modifiedConnections)
            {
                object value = Activator.CreateInstance(m_ModifiedLaneConnectionsType);
                m_ModifiedLaneIndexField.SetValue(value, laneIndex);
                m_ModifiedCarriagewayAndGroupField.SetValue(value, carriagewayAndGroup);
                m_ModifiedLanePositionField.SetValue(value, lanePosition);
                m_ModifiedEdgeField.SetValue(value, edgeEntity);
                m_ModifiedConnectionsField.SetValue(value, modifiedConnections);
                return value;
            }

            public object CreateGeneratedConnection(
                Entity sourceEntity,
                Entity targetEntity,
                int sourceLaneIndex,
                int targetLaneIndex,
                float3x2 lanePositionMap,
                int4 carriagewayAndGroupIndexMap,
                PathMethod method,
                bool isUnsafe)
            {
                object value = Activator.CreateInstance(m_GeneratedConnectionType);
                m_GeneratedSourceField.SetValue(value, sourceEntity);
                m_GeneratedTargetField.SetValue(value, targetEntity);
                m_GeneratedLaneIndexMapField.SetValue(value, new int2(sourceLaneIndex & 0xff, targetLaneIndex & 0xff));
                m_GeneratedCarriagewayAndGroupIndexMapField.SetValue(value, carriagewayAndGroupIndexMap);
                m_GeneratedLanePositionMapField.SetValue(value, lanePositionMap);
                m_GeneratedMethodField.SetValue(value, method);
                m_GeneratedUnsafeField.SetValue(value, isUnsafe);
                return value;
            }

            public void AddDataOwner(EntityManager entityManager, Entity entity, Entity owner)
            {
                ComponentType componentType = ComponentType.ReadWrite(m_DataOwnerType);
                if (!entityManager.HasComponent(entity, componentType))
                {
                    entityManager.AddComponent(entity, componentType);
                }

                object dataOwner = Activator.CreateInstance(m_DataOwnerType);
                m_DataOwnerEntityField.SetValue(dataOwner, owner);
                m_SetDataOwner.Invoke(entityManager, new[] { entity, dataOwner });
            }

            public void AddFakePrefabRef(EntityManager entityManager, Entity entity)
            {
                if (entityManager.HasComponent<PrefabRef>(entity))
                {
                    entityManager.SetComponentData(entity, new PrefabRef(m_FakePrefabRef));
                }
                else
                {
                    entityManager.AddComponentData(entity, new PrefabRef(m_FakePrefabRef));
                }
            }

            public void EnsureModifiedConnectionsTag(EntityManager entityManager, Entity node)
            {
                ComponentType componentType = ComponentType.ReadWrite(m_ModifiedConnectionsType);
                if (!entityManager.HasComponent(node, componentType))
                {
                    entityManager.AddComponent(node, componentType);
                }
            }

            private static Type FindType(string fullName)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }

                return null;
            }

            private static FieldInfo RequireField(Type type, string name)
            {
                return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? throw new MissingFieldException(type.FullName, name);
            }

            private static MethodInfo MakeEntityManagerGeneric(string name, Type genericType, params Type[] parameterTypes)
            {
                MethodInfo method = typeof(EntityManager)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(candidate =>
                        candidate.Name == name &&
                        candidate.IsGenericMethodDefinition &&
                        ParametersMatch(candidate, genericType, parameterTypes));

                if (method == null)
                {
                    throw new MissingMethodException(typeof(EntityManager).FullName, name);
                }

                return method.MakeGenericMethod(genericType);
            }

            private static bool ParametersMatch(MethodInfo method, Type genericType, Type[] parameterTypes)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                {
                    return false;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    Type parameterType = parameters[i].ParameterType;
                    if (parameterType.IsGenericParameter)
                    {
                        if (parameterTypes[i] != genericType)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (parameterType != parameterTypes[i])
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
