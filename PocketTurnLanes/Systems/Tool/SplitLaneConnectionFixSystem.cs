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
using SubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class SplitLaneConnectionFixSystem : GameSystemBase
    {
        private const int MaxLaneDataRetries = 12;
        private const int MaxVerificationRetries = 8;
        private const int MaxTrafficRuntimeWaitFrames = 120;
        private const int RequiredStableVerificationFrames = 3;

        private readonly List<Request> m_Requests = new List<Request>();
        private readonly List<LaneEndpoint> m_SourceLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_TargetLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_ReverseSourceLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneEndpoint> m_ReverseTargetLanes = new List<LaneEndpoint>(8);
        private readonly List<LaneMapping> m_Mappings = new List<LaneMapping>(12);
        private readonly List<object> m_KeptTrafficConnections = new List<object>(16);
        private readonly List<ConnectorLane> m_ConnectorLanes = new List<ConnectorLane>(16);
        private readonly List<ConnectorLane> m_ExistingConnectorLanes = new List<ConnectorLane>(16);
        private readonly List<ConnectorLane> m_StaleConnectorLanes = new List<ConnectorLane>(16);
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
            Mod.log.Info("[SplitLaneConnectionFix] Created. Traffic lane connection writer runs only after final apply verification and before TrafficLaneSystem when ordered registration is available.");
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
                        Mod.log.Info($"[SplitLaneConnectionFix] Traffic runtime types are unavailable and Traffic was not detected as enabled; queued split-node connection repairs will be skipped. Install/enable Traffic dependency 80095 to enable this repair path. error={trafficError}");
                        m_TrafficUnavailableLogged = true;
                    }

                    m_Requests.Clear();
                    return;
                }

                m_TrafficRuntimeWaitFrames++;
                if (m_TrafficRuntimeWaitFrames > MaxTrafficRuntimeWaitFrames)
                {
                    Mod.log.Info($"[SplitLaneConnectionFix] Traffic runtime did not become ready after {m_TrafficRuntimeWaitFrames} frames; skipping queued split-node connection repairs. trafficEnabled={Mod.TrafficLaneConnectionFixEnabled} error={trafficError}");
                    m_Requests.Clear();
                    m_TrafficRuntimeWaitFrames = 0;
                    return;
                }

                if (!m_TrafficUnavailableLogged || m_TrafficRuntimeWaitFrames % 30 == 0)
                {
                    Mod.log.Info($"[SplitLaneConnectionFix] Waiting for Traffic runtime before repairing split-node connections frameWait={m_TrafficRuntimeWaitFrames}/{MaxTrafficRuntimeWaitFrames} trafficEnabled={Mod.TrafficLaneConnectionFixEnabled} missingTypes={missingTrafficTypes} error={trafficError}");
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
                            if (!request.UturnCleanupPending)
                            {
                                QueueUturnCleanup(ref request, request.OuterEdge, "lane-data retries exhausted after prepare failure");
                            }

                            request.RemoveAfterUturnCleanup = true;
                            Mod.log.Info($"[SplitLaneConnectionFix] Exhausted lane-data retries; waiting for post-lane U-turn cleanup before skipping request splitNode={FormatEntity(request.SplitNode)} intersection={FormatEntity(request.IntersectionNode)} original={FormatEntity(request.OriginalEdge)} outerEdge={FormatEntity(request.OuterEdge)} pocket={FormatEntity(request.PocketEdge)} sourcePrefab={FormatEntity(request.SourcePrefab)} targetPrefab={FormatEntity(request.TargetPrefab)} reason={request.UturnCleanupReason}.");
                            m_Requests[i] = request;
                            continue;
                        }

                        Mod.log.Info($"[SplitLaneConnectionFix] Lane data not ready splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)} retry={request.LaneDataRetries}/{MaxLaneDataRetries}.");
                        m_Requests[i] = request;
                        continue;
                    }

                    if (!WriteTrafficMappings(trafficApi, request))
                    {
                        QueueUturnCleanup(ref request, request.OuterEdge, "Traffic mapping write failed");
                        request.RemoveAfterUturnCleanup = true;
                        Mod.log.Info($"[SplitLaneConnectionFix] Traffic mapping write failed; queued post-lane U-turn cleanup before skipping request splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)}.");
                        m_Requests[i] = request;
                        continue;
                    }

                    request.TrafficWritten = true;
                    request.TrafficWriteFrame = UnityEngine.Time.frameCount;
                    request.StableVerificationFrames = 0;
                    Mod.log.Info($"[SplitLaneConnectionFix] Wrote Traffic lane mapping; direct connector cleanup deferred until post-lane phase splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} sourceOrder={FormatLaneOrder(request.SourceLanes)} targetOrder={FormatLaneOrder(request.TargetLanes)} reverseSourceOrder={FormatLaneOrder(request.ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(request.ReverseTargetLanes)} extraLane={request.ExtraTargetLaneIndex} turn={request.Turn} branchSource={request.BranchSourceLaneIndex} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()} leftHandTraffic={m_CityConfigurationSystem.leftHandTraffic}.");
                    m_Requests[i] = request;
                }
                catch (Exception ex)
                {
                    Mod.log.Info(ex, $"[SplitLaneConnectionFix] Unhandled exception while repairing splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)}.");
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
                                Mod.log.Info($"[SplitLaneConnectionFix] Skipped lane connection repair after U-turn cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} deletedUturn={deletedUturn} reason={request.UturnCleanupReason} retries={request.LaneDataRetries}/{MaxLaneDataRetries}.");
                                m_Requests.RemoveAt(i);
                                continue;
                            }

                            m_Requests[i] = request;
                        }
                        catch (Exception ex)
                        {
                            Mod.log.Info(ex, $"[SplitLaneConnectionFix] Unhandled exception during U-turn cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={request.UturnCleanupReason}.");
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
                            Mod.log.Info($"[SplitLaneConnectionFix] Completed splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} leftHandTraffic={m_CityConfigurationSystem.leftHandTraffic} verificationAttempts={request.VerificationAttempts} stableFrames={request.StableVerificationFrames}/{RequiredStableVerificationFrames}.");
                            m_Requests.RemoveAt(i);
                            continue;
                        }

                        Mod.log.Info($"[SplitLaneConnectionFix] Post-lane connector verification stable splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)} stableFrames={request.StableVerificationFrames}/{RequiredStableVerificationFrames}.");
                        m_Requests[i] = request;
                        continue;
                    }

                    request.StableVerificationFrames = 0;
                    if (!TryRebuildConnectorLanes(ref request, out DirectRebuildStats retryStats))
                    {
                        int deletedUturn = DeleteStaleSplitNodeUturnConnectorLanes(request, request.OuterEdge, $"direct rebuild failed: {retryStats.Reason}");
                        request.VerificationAttempts++;
                        Mod.log.Info($"[SplitLaneConnectionFix] Post-lane direct connector rebuild retry failed; cleaned stale U-turns only splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)} reason={retryStats.Reason} deletedUturn={deletedUturn} attempt={request.VerificationAttempts}/{MaxVerificationRetries} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
                        if (request.VerificationAttempts > MaxVerificationRetries)
                        {
                            Mod.log.Info($"[SplitLaneConnectionFix] Verification exhausted splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)}; post-lane direct connector rebuild could not be re-applied.");
                            m_Requests.RemoveAt(i);
                            continue;
                        }

                        m_Requests[i] = request;
                        continue;
                    }

                    if (request.VerificationAttempts > MaxVerificationRetries)
                    {
                        Mod.log.Info($"[SplitLaneConnectionFix] Verification exhausted splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)}; post-lane direct connector rebuild was applied but verification never stabilized.");
                        m_Requests.RemoveAt(i);
                        continue;
                    }

                    request.VerificationAttempts++;
                    request.TrafficWriteFrame = UnityEngine.Time.frameCount;
                    request.StableVerificationFrames = 0;
                    Mod.log.Info($"[SplitLaneConnectionFix] Post-lane direct connector rebuild pending verification splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} expected={FormatMappings(request.Mappings)} kept={retryStats.Kept} cloned={retryStats.Cloned} deleted={retryStats.Deleted} deletedUturn={retryStats.DeletedUturn} updated={retryStats.Updated} attempt={request.VerificationAttempts}/{MaxVerificationRetries} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
                    m_Requests[i] = request;
                }
                catch (Exception ex)
                {
                    Mod.log.Info(ex, $"[SplitLaneConnectionFix] Unhandled exception during post-lane cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)}.");
                    m_Requests.RemoveAt(i);
                }
            }

            if (processed > 0 || pendingTrafficWrite > 0)
            {
                Mod.log.Info($"[SplitLaneConnectionFix] Post-lane cleanup pass frame={UnityEngine.Time.frameCount} processed={processed} pendingTrafficWrite={pendingTrafficWrite} remaining={m_Requests.Count} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
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
                Mod.log.Info($"[SplitLaneConnectionFix] Queue skipped: invalid splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} mode={mode} farIntersection={FormatEntity(farIntersectionNode)}.");
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
                    m_Requests[i] = existing;
                    Mod.log.Info($"[SplitLaneConnectionFix] Updated queued request splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} explicitOuter={FormatEntity(explicitOuterEdge)} sourcePrefab={FormatEntity(sourcePrefab)} targetPrefab={FormatEntity(targetPrefab)} mode={mode} farIntersection={FormatEntity(farIntersectionNode)} reverseSnapshot={FormatSnapshot(reverseSnapshot)} frame={UnityEngine.Time.frameCount}.");
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
            Mod.log.Info($"[SplitLaneConnectionFix] Queued split-node connection repair splitNode={FormatEntity(splitNode)} pocketEdge={FormatEntity(pocketEdge)} original={FormatEntity(originalEdge)} explicitOuter={FormatEntity(explicitOuterEdge)} intersection={FormatEntity(intersectionNode)} farIntersection={FormatEntity(farIntersectionNode)} sourcePrefab={FormatEntity(sourcePrefab)} targetPrefab={FormatEntity(targetPrefab)} mode={mode} reverseSnapshot={FormatSnapshot(reverseSnapshot)} frame={UnityEngine.Time.frameCount} preMarkedUpdated=split:{FormatUpdateMarker(splitUpdated, splitAlreadyUpdated)},pocket:{FormatUpdateMarker(pocketUpdated, pocketAlreadyUpdated)},original:{FormatUpdateMarker(originalUpdated, originalAlreadyUpdated)}. Repair waits for post-apply lane generation; preview is intentionally not modified.");
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
            Mod.log.Info($"[SplitLaneConnectionFix] Captured transition reverse connection snapshot node={FormatEntity(transitionNode)} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} {snapshot.Detail} mappings={FormatSnapshotMappings(snapshot.Mappings)} sourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} targetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
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
                Mod.log.Info($"[SplitLaneConnectionFix] Missing split node or pocket edge splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)}.");
                return false;
            }

            if (!TryFindOuterEdge(request, out Entity outerEdge))
            {
                string reason = "outer edge not found";
                QueueUturnCleanup(ref request, Entity.Null, reason);
                Mod.log.Info($"[SplitLaneConnectionFix] Cannot identify outer edge splitNode={FormatEntity(request.SplitNode)} pocket={FormatEntity(request.PocketEdge)} original={FormatEntity(request.OriginalEdge)} sourcePrefab={FormatEntity(request.SourcePrefab)}.");
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
                Mod.log.Info($"[SplitLaneConnectionFix] Missing lane data splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count}.");
                return false;
            }

            if (m_TargetLanes.Count < m_SourceLanes.Count + 1)
            {
                string reason = $"forward lane count mismatch source={m_SourceLanes.Count} target={m_TargetLanes.Count} expectedTargetAtLeast={m_SourceLanes.Count + 1}";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.log.Info($"[SplitLaneConnectionFix] Cannot apply N->N+1 rule splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count}; expected at least one extra target lane.");
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
                Mod.log.Info($"[SplitLaneConnectionFix] Cannot select an N->N+1 target subset splitNode={FormatEntity(request.SplitNode)} sourceOrder={FormatLaneOrder(m_SourceLanes)} targetOrder={FormatLaneOrder(m_TargetLanes)}.");
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
                    Mod.log.Info($"[SplitLaneConnectionFix] Center connector turn target overrides split lateral target splitNode={FormatEntity(request.SplitNode)} oldExtra={selectedTargets[extraTargetListIndex].LaneIndex}/{turn} newExtra={selectedTargets[centerExtraTargetListIndex].LaneIndex}/{centerTurn} diagnostics={centerTurnDiagnostic}.");
                }

                extraTargetListIndex = centerExtraTargetListIndex;
                turn = centerTurn;
            }

            if (turn == TurnDirection.Ambiguous)
            {
                string reason = $"ambiguous turn extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}";
                QueueUturnCleanup(ref request, outerEdge, reason);
                Mod.log.Info($"[SplitLaneConnectionFix] Cannot determine turn side splitNode={FormatEntity(request.SplitNode)} selectedTargets={FormatLaneOrder(selectedTargets)} extraIndex={extraTargetListIndex} centerDiagnostics={centerTurnDiagnostic}; leaving connectors unchanged.");
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
                Mod.log.Info($"[SplitLaneConnectionFix] Waiting for generated split-node connectors splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count}; direct rebuild needs an existing connector template.");
                return false;
            }

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
                Mod.log.Info($"[SplitLaneConnectionFix] Cannot build desired lane mapping splitNode={FormatEntity(request.SplitNode)} sourceOrder={FormatLaneOrder(m_SourceLanes)} selectedTargets={FormatLaneOrder(selectedTargets)} extraTarget={extraTargetLaneIndex} branchSource={branchSourceLaneIndex} existing={FormatConnectorLanes(m_ExistingConnectorLanes)} reason={mappingReason}.");
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
                QueueUturnCleanup(ref request, outerEdge, $"reverse mapping failed: {reverseMappingReason}");
                Mod.log.Info($"[SplitLaneConnectionFix] Cannot prepare reverse split-node mapping splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reverseMappingReason}; leaving connectors unchanged to avoid partial Traffic data.");
                return false;
            }

            Mod.log.Info($"[SplitLaneConnectionFix] Prepared Traffic mapping splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} farIntersection={FormatEntity(request.FarIntersectionNode)} sourceCount={m_SourceLanes.Count} targetCount={m_TargetLanes.Count} selectedTargetCount={selectedTargets.Count} mappingScore={mappingScore:0.###} mappingSource={mappingSource} turn={turn} branchSource={branchSourceLaneIndex} extraTarget={extraTargetLaneIndex} centerDiagnostics={centerTurnDiagnostic} existingConnectors={m_ExistingConnectorLanes.Count} existing={FormatConnectorLanes(m_ExistingConnectorLanes)} mappings={FormatMappings(request.Mappings)} reverseSourceCount={request.ReverseSourceLanes?.Length ?? 0} reverseTargetCount={request.ReverseTargetLanes?.Length ?? 0} reverseMappingSource={reverseMappingSource} reverseMappings={FormatMappings(request.ReverseMappings)}.");
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
                        Mod.log.Info($"[SplitLaneConnectionFix] Far-center connector turn target overrides balanced reverse split lateral target splitNode={FormatEntity(request.SplitNode)} oldExtra={selectedReverseTargets[extraTargetListIndex].LaneIndex}/{turn} newExtra={selectedReverseTargets[centerExtraTargetListIndex].LaneIndex}/{centerTurn} diagnostics={centerTurnDiagnostic}.");
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
                    Mod.log.Info($"[SplitLaneConnectionFix] Short-edge transition reverse restore skipped splitNode={FormatEntity(request.SplitNode)} source={FormatEntity(request.PocketEdge)} target={FormatEntity(outerEdge)} reason={snapshotReason} snapshot={FormatSnapshot(request.TransitionReverseSnapshot)} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)}.");
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
                reason = $"{buildReason} reverseSourceOrder={FormatLaneOrder(m_ReverseSourceLanes)} reverseTargetOrder={FormatLaneOrder(m_ReverseTargetLanes)} existingReverse={FormatConnectorLanes(m_ExistingConnectorLanes)}";
                return false;
            }

            request.ReverseSourceLanes = m_ReverseSourceLanes.ToArray();
            request.ReverseTargetLanes = m_ReverseTargetLanes.ToArray();
            request.ReverseMappings = reverseMappings;
            return true;
        }

        private void QueueUturnCleanup(ref Request request, Entity outerEdge, string reason)
        {
            if (outerEdge != Entity.Null)
            {
                request.OuterEdge = outerEdge;
            }

            request.UturnCleanupPending = true;
            request.UturnCleanupReason = reason;
            Mod.log.Info($"[SplitLaneConnectionFix] Queued post-lane U-turn cleanup splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reason}.");
        }

        private int DeleteStaleSplitNodeUturnConnectorLanes(Request request, Entity outerEdge, string reason)
        {
            if (!EntityManager.TryGetBuffer(request.SplitNode, false, out DynamicBuffer<SubLane> subLanes))
            {
                Mod.log.Info($"[SplitLaneConnectionFix] Cannot delete stale split-node U-turn connectors after skip: split node has no SubLane buffer splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} reason={reason}.");
                return 0;
            }

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, outerEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            if (m_StaleConnectorLanes.Count == 0)
            {
                return 0;
            }

            string staleSummary = FormatConnectorLanes(m_StaleConnectorLanes);
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

            Mod.log.Info($"[SplitLaneConnectionFix] Deleted stale split-node U-turn connectors after skip splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(outerEdge)} pocketEdge={FormatEntity(request.PocketEdge)} deletedUturn={m_StaleConnectorLanes.Count} removedSubLanes={removedSubLanes} reason={reason} connectors={staleSummary} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
            return m_StaleConnectorLanes.Count;
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
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !EntityManager.TryGetComponent(laneEntity, out EdgeLane edgeLane) ||
                    !EntityManager.TryGetComponent(laneEntity, out Curve curve) ||
                    !EntityManager.TryGetComponent(laneEntity, out PrefabRef lanePrefab) ||
                    !EntityManager.TryGetComponent(lanePrefab.m_Prefab, out NetLaneData laneData) ||
                    !EntityManager.TryGetComponent(lanePrefab.m_Prefab, out CarLaneData carLaneData) ||
                    EntityManager.HasComponent<Game.Net.SecondaryLane>(laneEntity))
                {
                    continue;
                }

                if ((laneData.m_Flags & LaneFlags.Road) == 0 ||
                    (carLaneData.m_RoadTypes & RoadTypes.Car) == 0 ||
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
                    Mod.log.Info($"[SplitLaneConnectionFix] Skipped lane endpoint without Traffic composition match edge={FormatEntity(edgeEntity)} splitNode={FormatEntity(splitNode)} lane={FormatEntity(laneEntity)} role={role} edgeDelta={edgeLane.m_EdgeDelta} laneFlags={laneData.m_Flags}.");
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
                    RoadTypes = carLaneData.m_RoadTypes
                });
            }
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

        private static PathMethod GetMappingMethod(LaneEndpoint source, LaneEndpoint target)
        {
            PathMethod method = PathMethod.Road;
            bool sourceHasTrack = (source.PathMethods & PathMethod.Track) != 0 &&
                                  (source.LaneFlags & LaneFlags.Track) != 0;
            bool targetHasTrack = (target.PathMethods & PathMethod.Track) != 0 &&
                                  (target.LaneFlags & LaneFlags.Track) != 0;
            if (sourceHasTrack && targetHasTrack)
            {
                method |= PathMethod.Track;
            }

            return method;
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
            List<LaneMapping> allMappings = GetAllMappings(request);
            if (allMappings.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < allMappings.Count; i++)
            {
                LaneMapping mapping = allMappings[i];
                if (!TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out _) ||
                    !TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out _))
                {
                    Mod.log.Info($"[SplitLaneConnectionFix] Traffic mapping preflight failed splitNode={FormatEntity(request.SplitNode)} mapping={FormatMapping(mapping)} sourceFound={TryFindMappingEndpoint(request, mapping.SourceEdge, mapping.SourceLaneIndex, source: true, out _)} targetFound={TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out _)}.");
                    return false;
                }
            }

            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, request.SplitNode);
            if (modifiedBuffer == null)
            {
                return false;
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
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                if (edge == request.OuterEdge || (removePocketExisting && edge == request.PocketEdge))
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

            Dictionary<SourceLaneKey, List<LaneMapping>> bySource = new Dictionary<SourceLaneKey, List<LaneMapping>>();
            for (int i = 0; i < allMappings.Count; i++)
            {
                LaneMapping mapping = allMappings[i];
                SourceLaneKey key = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
                if (!bySource.TryGetValue(key, out List<LaneMapping> list))
                {
                    list = new List<LaneMapping>(2);
                    bySource.Add(key, list);
                }

                list.Add(mapping);
            }

            int writtenSources = 0;
            int writtenConnections = 0;
            foreach (KeyValuePair<SourceLaneKey, List<LaneMapping>> pair in bySource)
            {
                if (!TryFindMappingEndpoint(request, pair.Key.Edge, pair.Key.LaneIndex, source: true, out LaneEndpoint sourceEndpoint))
                {
                    continue;
                }

                Entity modifiedConnectionEntity = EntityManager.CreateEntity();
                trafficApi.AddDataOwner(EntityManager, modifiedConnectionEntity, request.SplitNode);
                trafficApi.AddFakePrefabRef(EntityManager, modifiedConnectionEntity);
                object generatedBuffer = trafficApi.AddGeneratedConnectionBuffer(EntityManager, modifiedConnectionEntity);

                foreach (LaneMapping mapping in pair.Value)
                {
                    if (!TryFindMappingEndpoint(request, mapping.TargetEdge, mapping.TargetLaneIndex, source: false, out LaneEndpoint targetEndpoint))
                    {
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
                        mapping.Method,
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
            Mod.log.Info($"[SplitLaneConnectionFix] Traffic write counts splitNode={FormatEntity(request.SplitNode)} removedExisting={removedExisting} preservedExisting={m_KeptTrafficConnections.Count} writtenSources={writtenSources} writtenConnections={writtenConnections} forwardMappings={FormatMappings(request.Mappings)} reverseMappings={FormatMappings(request.ReverseMappings)}.");
            return writtenSources > 0 && writtenConnections > 0;
        }

        private static List<LaneMapping> GetAllMappings(Request request)
        {
            List<LaneMapping> allMappings = new List<LaneMapping>(
                (request.Mappings?.Length ?? 0) + (request.ReverseMappings?.Length ?? 0));
            if (request.Mappings != null)
            {
                allMappings.AddRange(request.Mappings);
            }

            if (request.ReverseMappings != null)
            {
                allMappings.AddRange(request.ReverseMappings);
            }

            return allMappings;
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

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            int nextNodeLaneIndex = GetNextNodeLaneIndex(request.SplitNode, subLanes);
            m_RemoveSubLaneIndexes.Clear();
            for (int i = 0; i < m_StaleConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_StaleConnectorLanes[i];
                QueueDeleteConnector(connector.Entity);
                m_RemoveSubLaneIndexes.Add(connector.SubLaneIndex);
                stats.Deleted++;
                stats.DeletedUturn++;
            }

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
                    out string forwardReason))
            {
                stats.Reason = forwardReason;
                return false;
            }

            string reverseReason = request.Mode == RepairMode.ShortEdgeTransition
                ? "short-edge-transition-reverse-not-restored"
                : "standard-reverse-not-rebuilt";
            if (request.Mode == RepairMode.BalancedOppositeTarget)
            {
                if (request.ReverseMappings == null || request.ReverseMappings.Length == 0)
                {
                    stats.Reason = "balanced reverse mappings missing";
                    return false;
                }

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
            if (stats.Kept > 0 || stats.Cloned > 0 || stats.Deleted > 0)
            {
                MarkUpdatedIfExists(request.SplitNode);
                MarkUpdatedIfExists(request.OuterEdge);
                MarkUpdatedIfExists(request.PocketEdge);
            }

            Mod.log.Info($"[SplitLaneConnectionFix] Direct rebuild result splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} expected={FormatMappings(request.Mappings)} reverseExpected={FormatMappings(request.ReverseMappings)} staleUturn={m_StaleConnectorLanes.Count} reverseReason={reverseReason} kept={stats.Kept} cloned={stats.Cloned} deleted={stats.Deleted} deletedUturn={stats.DeletedUturn} updated={stats.Updated}.");
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
                subLanes.Add(new SubLane
                {
                    m_SubLane = clone,
                    m_PathMethods = template.PathMethods
                });
                kept.Add(key, new ConnectorLane
                {
                    Entity = clone,
                    SubLaneIndex = subLanes.Length - 1,
                    PathMethods = template.PathMethods,
                    SourceLaneIndex = mapping.SourceLaneIndex,
                    TargetLaneIndex = mapping.TargetLaneIndex
                });
                stats.Cloned++;
                stats.Updated++;
            }

            reason = $"{direction} ok expected={FormatConnectionSet(expected)} existing={existingKeys.Count} kept={kept.Count} missingClones={missingCloneCount}";
            return true;
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
            Mod.log.Info($"[SplitLaneConnectionFix] Cloned connector lane clone={FormatEntity(clone)} template={FormatEntity(template.Entity)} splitNode={FormatEntity(request.SplitNode)} source={source.LaneIndex}/{FormatEntity(source.LaneEntity)} target={target.LaneIndex}/{FormatEntity(target.LaneEntity)} middleIndex={middleLaneIndex}.");
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

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.ReverseSourceLanes, laneIndex, out lane))
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

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.ReverseTargetLanes, laneIndex, out lane))
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
                    Mod.log.Info($"[SplitLaneConnectionFix] Cleared unsafe flags connector={FormatEntity(laneEntity)} oldFlags={oldFlags} newFlags={carLane.m_Flags}.");
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

            int staleUturnCount = CountStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, out string staleUturnSummary);
            bool matches = forwardMatches && reverseMatches && staleUturnCount == 0;
            if (!matches)
            {
                Mod.log.Info($"[SplitLaneConnectionFix] Connector verification mismatch splitNode={FormatEntity(request.SplitNode)} mode={request.Mode} forward={forwardDetail} reverse={reverseDetail} staleUturnCount={staleUturnCount} staleUturns={staleUturnSummary}.");
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

                output.Add(new ConnectorLane
                {
                    Entity = laneEntity,
                    SubLaneIndex = i,
                    PathMethods = subLane.m_PathMethods,
                    SourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff,
                    TargetLaneIndex = lane.m_EndNode.GetLaneIndex() & 0xff
                });
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
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !lane.m_StartNode.OwnerEquals(lane.m_EndNode) ||
                    !NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, splitNode, lane, out Entity sourceEdge, out Entity targetEdge) ||
                    sourceEdge != targetEdge ||
                    (restrictToSplitPair && sourceEdge != outerEdge && sourceEdge != pocketEdge))
                {
                    continue;
                }

                output.Add(new ConnectorLane
                {
                    Entity = laneEntity,
                    SubLaneIndex = i,
                    PathMethods = subLane.m_PathMethods,
                    SourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff,
                    TargetLaneIndex = lane.m_EndNode.GetLaneIndex() & 0xff
                });
            }
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

            Mod.log.Info($"[SplitLaneConnectionFix] Marked lane rebuild neighborhood splitNode={FormatEntity(request.SplitNode)} scannedConnectedEdges={scannedEdges} addedUpdatedNodes={updatedNodes} alreadyUpdatedNodes={alreadyUpdatedNodes} addedUpdatedEdges={updatedEdges} alreadyUpdatedEdges={alreadyUpdatedEdges} pocketEdge={FormatEntity(request.PocketEdge)} outerEdge={FormatEntity(request.OuterEdge)} originalEdge={FormatEntity(request.OriginalEdge)} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
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
                Mod.log.Info("[SplitLaneConnectionFix] Traffic runtime detected; connection repair is enabled.");
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

            return string.Join(",", lanes.Select(lane => $"{lane.Endpoint}{lane.LaneIndex}|C{lane.OppositeLaneIndex}@{lane.Lateral:0.##}/{FormatEntity(lane.LaneEntity)} lanePos={FormatFloat3(lane.LanePosition)} cg={lane.CarriagewayAndGroup} methods=[{lane.PathMethods}] laneFlags=[{lane.LaneFlags}] carFlags=[{lane.CarFlags}] roadTypes=[{lane.RoadTypes}]"));
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
            return $"{FormatEntity(mapping.SourceEdge)}:{mapping.SourceLaneIndex}->{FormatEntity(mapping.TargetEdge)}:{mapping.TargetLaneIndex}[{mapping.Method}]{(mapping.IsBranch ? "*" : string.Empty)}";
        }

        private static string FormatConnectorLanes(IReadOnlyList<ConnectorLane> connectors)
        {
            if (connectors == null || connectors.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", connectors.Select(connector => $"{connector.SourceLaneIndex}->{connector.TargetLaneIndex}/{FormatEntity(connector.Entity)}"));
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
            public int TrafficWriteFrame;
            public int VerificationAttempts;
            public int StableVerificationFrames;
            public bool UturnCleanupPending;
            public bool RemoveAfterUturnCleanup;
            public string UturnCleanupReason;
            public LaneEndpoint[] SourceLanes;
            public LaneEndpoint[] TargetLanes;
            public LaneEndpoint[] ReverseSourceLanes;
            public LaneEndpoint[] ReverseTargetLanes;
            public LaneMapping[] Mappings;
            public LaneMapping[] ReverseMappings;
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
        }

        private struct ConnectorLane
        {
            public Entity Entity;
            public int SubLaneIndex;
            public PathMethod PathMethods;
            public int SourceLaneIndex;
            public int TargetLaneIndex;
        }

        private struct LaneMapping
        {
            public Entity SourceEdge;
            public Entity TargetEdge;
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public PathMethod Method;
            public bool IsBranch;
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
            public int Updated;
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
