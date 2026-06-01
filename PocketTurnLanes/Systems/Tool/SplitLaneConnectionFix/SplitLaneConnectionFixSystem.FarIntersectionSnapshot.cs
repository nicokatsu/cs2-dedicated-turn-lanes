using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        public FarIntersectionTrafficSnapshot CaptureFarIntersectionTrafficSnapshot(
            Entity farNode,
            Entity continuationEdge)
        {
            FarIntersectionTrafficSnapshot snapshot = new FarIntersectionTrafficSnapshot
            {
                Node = farNode,
                ContinuationEdge = continuationEdge,
                Source = "none",
                Detail = "not-started",
                Entries = Array.Empty<FarIntersectionTrafficSnapshotEntry>()
            };

            if (farNode == Entity.Null ||
                !EntityManager.Exists(farNode))
            {
                snapshot.Source = "invalid-node";
                snapshot.Detail = $"farNode={FormatEntity(farNode)} continuation={FormatEntity(continuationEdge)}";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic snapshot skipped: invalid far node {snapshot.Detail}.");
                return snapshot;
            }

            if (!TryGetTrafficApi(out TrafficApi trafficApi, out string trafficError))
            {
                snapshot.Source = "traffic-unavailable";
                snapshot.Detail = trafficError;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic snapshot skipped farNode={FormatEntity(farNode)} continuation={FormatEntity(continuationEdge)} reason={trafficError}.");
                return snapshot;
            }

            if (!trafficApi.HasModifiedLaneConnectionsBuffer(EntityManager, farNode))
            {
                snapshot.Source = "empty";
                snapshot.Detail = $"trafficBuffer=missing farNode={FormatEntity(farNode)} continuation={FormatEntity(continuationEdge)}";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Captured empty far intersection Traffic snapshot farNode={FormatEntity(farNode)} continuation={FormatEntity(continuationEdge)} detail={snapshot.Detail}.");
                return snapshot;
            }

            object modifiedBuffer = trafficApi.GetModifiedLaneConnectionsBuffer(EntityManager, farNode, true);
            int modifiedLength = trafficApi.GetBufferLength(modifiedBuffer);
            List<FarIntersectionTrafficSnapshotEntry> entries = new List<FarIntersectionTrafficSnapshotEntry>(modifiedLength);
            HashSet<Entity> sourceEdges = new HashSet<Entity>();
            HashSet<Entity> targetEdges = new HashSet<Entity>();
            int generatedConnections = 0;
            int missingGeneratedBuffers = 0;
            int continuationSourceEntries = 0;
            int continuationGeneratedReferences = 0;

            for (int i = 0; i < modifiedLength; i++)
            {
                object modified = trafficApi.GetBufferItem(modifiedBuffer, i);
                Entity sourceEdge = trafficApi.GetModifiedConnectionEdge(modified);
                int sourceLaneIndex = trafficApi.GetModifiedConnectionLaneIndex(modified);
                sourceEdges.Add(sourceEdge);
                TryCaptureFarSnapshotEndpoint(
                    farNode,
                    sourceEdge,
                    EndpointRole.SourceEndAtNode,
                    sourceLaneIndex,
                    out bool hasSourceEndpoint,
                    out float sourceLateral,
                    out int sourceOrder);

                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(modified);
                List<FarIntersectionTrafficSnapshotConnection> connections = new List<FarIntersectionTrafficSnapshotConnection>(4);
                if (modifiedEntity == Entity.Null ||
                    !EntityManager.Exists(modifiedEntity) ||
                    !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
                {
                    missingGeneratedBuffers++;
                }
                else
                {
                    object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
                    int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
                    for (int generatedIndex = 0; generatedIndex < generatedLength; generatedIndex++)
                    {
                        object generated = trafficApi.GetBufferItem(generatedBuffer, generatedIndex);
                        Entity generatedSource = trafficApi.GetGeneratedConnectionSource(generated);
                        Entity generatedTarget = trafficApi.GetGeneratedConnectionTarget(generated);
                        int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
                        int generatedSourceLane = laneIndexMap.x & 0xff;
                        int generatedTargetLane = laneIndexMap.y & 0xff;
                        targetEdges.Add(generatedTarget);
                        TryCaptureFarSnapshotEndpoint(
                            farNode,
                            generatedSource,
                            EndpointRole.SourceEndAtNode,
                            generatedSourceLane,
                            out bool hasGeneratedSourceEndpoint,
                            out float generatedSourceLateral,
                            out int generatedSourceOrder);
                        TryCaptureFarSnapshotEndpoint(
                            farNode,
                            generatedTarget,
                            EndpointRole.TargetStartAtNode,
                            generatedTargetLane,
                            out bool hasTargetEndpoint,
                            out float targetLateral,
                            out int targetOrder);

                        connections.Add(new FarIntersectionTrafficSnapshotConnection
                        {
                            SourceEdge = generatedSource,
                            TargetEdge = generatedTarget,
                            SourceLaneIndex = generatedSourceLane,
                            TargetLaneIndex = generatedTargetLane,
                            LanePositionMap = trafficApi.GetGeneratedConnectionLanePositionMap(generated),
                            CarriagewayAndGroupIndexMap = trafficApi.GetGeneratedConnectionCarriagewayAndGroupIndexMap(generated),
                            Method = trafficApi.GetGeneratedConnectionMethod(generated),
                            IsUnsafe = trafficApi.GetGeneratedConnectionUnsafe(generated),
                            HasSourceEndpoint = hasGeneratedSourceEndpoint,
                            HasTargetEndpoint = hasTargetEndpoint,
                            SourceLateral = generatedSourceLateral,
                            TargetLateral = targetLateral,
                            SourceOrder = generatedSourceOrder,
                            TargetOrder = targetOrder
                        });
                        generatedConnections++;
                        if (generatedSource == continuationEdge ||
                            generatedTarget == continuationEdge)
                        {
                            continuationGeneratedReferences++;
                        }
                    }
                }

                if (sourceEdge == continuationEdge)
                {
                    continuationSourceEntries++;
                }

                entries.Add(new FarIntersectionTrafficSnapshotEntry
                {
                    SourceEdge = sourceEdge,
                    SourceLaneIndex = sourceLaneIndex,
                    SourceCarriagewayAndGroup = trafficApi.GetModifiedConnectionCarriagewayAndGroup(modified),
                    SourceLanePosition = trafficApi.GetModifiedConnectionLanePosition(modified),
                    HasSourceEndpoint = hasSourceEndpoint,
                    SourceLateral = sourceLateral,
                    SourceOrder = sourceOrder,
                    Connections = connections.ToArray()
                });
            }

            snapshot.Source = entries.Count > 0 ? "traffic" : "empty";
            snapshot.Entries = entries.ToArray();
            snapshot.Detail = $"snapshotSource={snapshot.Source} modifiedSources={entries.Count} generatedConnections={generatedConnections} sourceEdges={sourceEdges.Count} targetEdges={targetEdges.Count} continuationSourceEntries={continuationSourceEntries} continuationGeneratedReferences={continuationGeneratedReferences} missingGeneratedBuffers={missingGeneratedBuffers}";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Captured far intersection Traffic snapshot farNode={FormatEntity(farNode)} continuation={FormatEntity(continuationEdge)} {snapshot.Detail}.");
            return snapshot;
        }

        private bool TryRestoreFarIntersectionTrafficSnapshot(
            TrafficApi trafficApi,
            Request request,
            out string detail)
        {
            FarIntersectionTrafficSnapshot snapshot = request.FarIntersectionSnapshot;
            detail = FormatFarSnapshot(snapshot);
            if (request.Mode != RepairMode.BalancedOppositeTarget)
            {
                return true;
            }

            if (snapshot == null ||
                snapshot.Entries == null ||
                snapshot.Entries.Length == 0)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic restore skipped splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(request.FarIntersectionNode)} outerEdge={FormatEntity(request.OuterEdge)} reason=emptySnapshot snapshot={detail}.");
                return true;
            }

            if (trafficApi == null ||
                snapshot.Node == Entity.Null ||
                !EntityManager.Exists(snapshot.Node))
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic restore failed before mutation splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(snapshot?.Node ?? Entity.Null)} outerEdge={FormatEntity(request.OuterEdge)} reason=invalidApiOrNode snapshot={detail}.");
                return false;
            }

            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, snapshot.Node);
            if (modifiedBuffer == null)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic restore failed before mutation splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(snapshot.Node)} outerEdge={FormatEntity(request.OuterEdge)} reason=modifiedBufferUnavailable snapshot={detail}.");
                return false;
            }

            int removedExisting = 0;
            int originalLength = trafficApi.GetBufferLength(modifiedBuffer);
            for (int i = 0; i < originalLength; i++)
            {
                object existing = trafficApi.GetBufferItem(modifiedBuffer, i);
                Entity modifiedEntity = trafficApi.GetModifiedConnectionEntity(existing);
                if (modifiedEntity != Entity.Null && EntityManager.Exists(modifiedEntity))
                {
                    AddMarkerIfMissing<Deleted>(modifiedEntity);
                }

                removedExisting++;
            }

            trafficApi.ClearBuffer(modifiedBuffer);

            int writtenSources = 0;
            int writtenConnections = 0;
            int skippedSources = 0;
            int skippedConnections = 0;
            int remappedSourceEntries = 0;
            int remappedGeneratedEdges = 0;
            int remappedLaneIndexes = 0;
            List<string> skipSamples = new List<string>(8);

            for (int i = 0; i < snapshot.Entries.Length; i++)
            {
                FarIntersectionTrafficSnapshotEntry entry = snapshot.Entries[i];
                if (!TryMapFarSnapshotEdge(
                        snapshot,
                        request,
                        entry.SourceEdge,
                        out Entity sourceEdge,
                        out bool sourceEdgeRemapped,
                        out string sourceEdgeReason))
                {
                    skippedSources++;
                    AddFarSnapshotSkipSample(skipSamples, $"source {FormatEntity(entry.SourceEdge)}:{entry.SourceLaneIndex} {sourceEdgeReason}");
                    continue;
                }

                int sourceLaneIndex = entry.SourceLaneIndex;
                int2 sourceCarriagewayAndGroup = entry.SourceCarriagewayAndGroup;
                float3 sourceLanePosition = entry.SourceLanePosition;
                if (sourceEdgeRemapped)
                {
                    if (!TryResolveFarSnapshotEndpoint(
                            snapshot.Node,
                            sourceEdge,
                            EndpointRole.SourceEndAtNode,
                            entry.SourceLaneIndex,
                            entry.HasSourceEndpoint,
                            entry.SourceLateral,
                            entry.SourceOrder,
                            out LaneEndpoint remappedSource,
                            out string sourceRemapDetail))
                    {
                        skippedSources++;
                        AddFarSnapshotSkipSample(skipSamples, $"sourceRemap {FormatEntity(entry.SourceEdge)}:{entry.SourceLaneIndex}->{FormatEntity(sourceEdge)} {sourceRemapDetail}");
                        continue;
                    }

                    sourceLaneIndex = remappedSource.LaneIndex;
                    sourceCarriagewayAndGroup = remappedSource.CarriagewayAndGroup;
                    sourceLanePosition = remappedSource.LanePosition;
                    remappedSourceEntries++;
                    if (sourceLaneIndex != entry.SourceLaneIndex)
                    {
                        remappedLaneIndexes++;
                    }
                }

                Entity modifiedConnectionEntity = CreateTrafficModifiedConnectionEntity(
                    trafficApi,
                    snapshot.Node,
                    out object generatedBuffer);

                FarIntersectionTrafficSnapshotConnection[] connections =
                    entry.Connections ?? Array.Empty<FarIntersectionTrafficSnapshotConnection>();
                for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                {
                    FarIntersectionTrafficSnapshotConnection connection = connections[connectionIndex];
                    if (!TryMapFarSnapshotEdge(
                            snapshot,
                            request,
                            connection.SourceEdge,
                            out Entity generatedSourceEdge,
                            out bool generatedSourceRemapped,
                            out string generatedSourceReason))
                    {
                        skippedConnections++;
                        AddFarSnapshotSkipSample(skipSamples, $"generatedSource {FormatEntity(connection.SourceEdge)}:{connection.SourceLaneIndex} {generatedSourceReason}");
                        continue;
                    }

                    if (!TryMapFarSnapshotEdge(
                            snapshot,
                            request,
                            connection.TargetEdge,
                            out Entity targetEdge,
                            out bool targetEdgeRemapped,
                            out string targetEdgeReason))
                    {
                        skippedConnections++;
                        AddFarSnapshotSkipSample(skipSamples, $"target {FormatEntity(connection.TargetEdge)}:{connection.TargetLaneIndex} {targetEdgeReason}");
                        continue;
                    }

                    int generatedSourceLaneIndex = connection.SourceLaneIndex;
                    int targetLaneIndex = connection.TargetLaneIndex;
                    LaneEndpoint generatedSourceEndpoint = default;
                    LaneEndpoint targetEndpoint = default;
                    bool hasGeneratedSourceEndpoint = false;
                    bool hasTargetEndpoint = false;

                    if (generatedSourceRemapped)
                    {
                        if (!TryResolveFarSnapshotEndpoint(
                                snapshot.Node,
                                generatedSourceEdge,
                                EndpointRole.SourceEndAtNode,
                                connection.SourceLaneIndex,
                                connection.HasSourceEndpoint,
                                connection.SourceLateral,
                                connection.SourceOrder,
                                out generatedSourceEndpoint,
                                out string generatedSourceRemapDetail))
                        {
                            skippedConnections++;
                            AddFarSnapshotSkipSample(skipSamples, $"generatedSourceRemap {FormatEntity(connection.SourceEdge)}:{connection.SourceLaneIndex}->{FormatEntity(generatedSourceEdge)} {generatedSourceRemapDetail}");
                            continue;
                        }

                        generatedSourceLaneIndex = generatedSourceEndpoint.LaneIndex;
                        hasGeneratedSourceEndpoint = true;
                        remappedGeneratedEdges++;
                        if (generatedSourceLaneIndex != connection.SourceLaneIndex)
                        {
                            remappedLaneIndexes++;
                        }
                    }

                    if (targetEdgeRemapped)
                    {
                        if (!TryResolveFarSnapshotEndpoint(
                                snapshot.Node,
                                targetEdge,
                                EndpointRole.TargetStartAtNode,
                                connection.TargetLaneIndex,
                                connection.HasTargetEndpoint,
                                connection.TargetLateral,
                                connection.TargetOrder,
                                out targetEndpoint,
                                out string targetRemapDetail))
                        {
                            skippedConnections++;
                            AddFarSnapshotSkipSample(skipSamples, $"targetRemap {FormatEntity(connection.TargetEdge)}:{connection.TargetLaneIndex}->{FormatEntity(targetEdge)} {targetRemapDetail}");
                            continue;
                        }

                        targetLaneIndex = targetEndpoint.LaneIndex;
                        hasTargetEndpoint = true;
                        remappedGeneratedEdges++;
                        if (targetLaneIndex != connection.TargetLaneIndex)
                        {
                            remappedLaneIndexes++;
                        }
                    }

                    float3x2 lanePositionMap = connection.LanePositionMap;
                    int4 carriagewayAndGroupIndexMap = connection.CarriagewayAndGroupIndexMap;
                    if (hasGeneratedSourceEndpoint || hasTargetEndpoint)
                    {
                        int2 originalSourceCarriagewayAndGroup = new int2(
                            connection.CarriagewayAndGroupIndexMap.x,
                            connection.CarriagewayAndGroupIndexMap.y);
                        int2 originalTargetCarriagewayAndGroup = new int2(
                            connection.CarriagewayAndGroupIndexMap.z,
                            connection.CarriagewayAndGroupIndexMap.w);
                        lanePositionMap = new float3x2(
                            hasGeneratedSourceEndpoint ? generatedSourceEndpoint.LanePosition : connection.LanePositionMap.c0,
                            hasTargetEndpoint ? targetEndpoint.LanePosition : connection.LanePositionMap.c1);
                        carriagewayAndGroupIndexMap = new int4(
                            hasGeneratedSourceEndpoint ? generatedSourceEndpoint.CarriagewayAndGroup : originalSourceCarriagewayAndGroup,
                            hasTargetEndpoint ? targetEndpoint.CarriagewayAndGroup : originalTargetCarriagewayAndGroup);
                    }

                    trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                        generatedSourceEdge,
                        targetEdge,
                        generatedSourceLaneIndex,
                        targetLaneIndex,
                        lanePositionMap,
                        carriagewayAndGroupIndexMap,
                        connection.Method,
                        connection.IsUnsafe));
                    writtenConnections++;
                }

                trafficApi.AddBufferElement(modifiedBuffer, trafficApi.CreateModifiedLaneConnection(
                    sourceLaneIndex,
                    sourceCarriagewayAndGroup,
                    sourceLanePosition,
                    sourceEdge,
                    modifiedConnectionEntity));
                writtenSources++;
            }

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, snapshot.Node);
            MarkCenterForLaneRebuild(snapshot.Node);
            detail = $"snapshot={FormatFarSnapshot(snapshot)} removedExisting={removedExisting} writtenSources={writtenSources} expectedSources={snapshot.Entries.Length} writtenConnections={writtenConnections} skippedSources={skippedSources} skippedConnections={skippedConnections} remappedSourceEntries={remappedSourceEntries} remappedGeneratedEdges={remappedGeneratedEdges} remappedLaneIndexes={remappedLaneIndexes} skipSamples={FormatStringList(skipSamples)}";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Restored far intersection Traffic snapshot splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(snapshot.Node)} continuation={FormatEntity(snapshot.ContinuationEdge)} outerEdge={FormatEntity(request.OuterEdge)} trafficWriteOrder={GetTrafficWriteOrder(request.Mode)} {detail}.");
            return skippedSources == 0 && skippedConnections == 0;
        }

        private void TryCaptureFarSnapshotEndpoint(
            Entity node,
            Entity edge,
            EndpointRole role,
            int laneIndex,
            out bool hasEndpoint,
            out float lateral,
            out int order)
        {
            hasEndpoint = false;
            lateral = 0f;
            order = -1;
            if (node == Entity.Null ||
                edge == Entity.Null ||
                !EntityManager.Exists(node) ||
                !IsEdgeConnectedToNode(edge, node))
            {
                return;
            }

            List<LaneEndpoint> endpoints = new List<LaneEndpoint>(8);
            CollectEdgeCenterPreservationLaneEndpoints(edge, node, role, endpoints);
            SortLaneEndpointsByLateral(endpoints);
            order = FindLaneEndpointOrder(endpoints, laneIndex);
            if (order < 0)
            {
                return;
            }

            hasEndpoint = true;
            lateral = endpoints[order].Lateral;
        }

        private bool TryMapFarSnapshotEdge(
            FarIntersectionTrafficSnapshot snapshot,
            Request request,
            Entity snapshotEdge,
            out Entity mappedEdge,
            out bool remapped,
            out string reason)
        {
            remapped = snapshotEdge == snapshot.ContinuationEdge;
            mappedEdge = remapped ? request.OuterEdge : snapshotEdge;
            reason = string.Empty;
            if (mappedEdge == Entity.Null)
            {
                reason = remapped ? "mappedOuterEdgeNull" : "edgeNull";
                return false;
            }

            if (!EntityManager.Exists(mappedEdge))
            {
                reason = $"edgeMissing mapped={FormatEntity(mappedEdge)}";
                return false;
            }

            if (EntityManager.HasComponent<Deleted>(mappedEdge))
            {
                reason = $"edgeDeleted mapped={FormatEntity(mappedEdge)}";
                return false;
            }

            if (!IsEdgeConnectedToNode(mappedEdge, snapshot.Node))
            {
                reason = $"edgeNotConnectedToFarNode mapped={FormatEntity(mappedEdge)} farNode={FormatEntity(snapshot.Node)}";
                return false;
            }

            return true;
        }

        private bool TryResolveFarSnapshotEndpoint(
            Entity node,
            Entity edge,
            EndpointRole role,
            int snapshotLaneIndex,
            bool hasSnapshotEndpoint,
            float snapshotLateral,
            int snapshotOrder,
            out LaneEndpoint endpoint,
            out string detail)
        {
            endpoint = default;
            detail = string.Empty;
            List<LaneEndpoint> endpoints = new List<LaneEndpoint>(8);
            CollectEdgeCenterPreservationLaneEndpoints(edge, node, role, endpoints);
            SortLaneEndpointsByLateral(endpoints);
            if (endpoints.Count == 0)
            {
                detail = $"noEndpoints edge={FormatEntity(edge)} node={FormatEntity(node)} role={role}";
                return false;
            }

            if (TryFindLaneEndpoint(endpoints, snapshotLaneIndex, out endpoint))
            {
                detail = $"sameLaneIndex {snapshotLaneIndex}->{endpoint.LaneIndex}";
                return true;
            }

            if (hasSnapshotEndpoint &&
                snapshotOrder >= 0 &&
                snapshotOrder < endpoints.Count)
            {
                endpoint = endpoints[snapshotOrder];
                detail = $"rankFallback {snapshotLaneIndex}->{endpoint.LaneIndex} order={snapshotOrder}";
                return true;
            }

            if (hasSnapshotEndpoint)
            {
                float bestError = float.MaxValue;
                int bestIndex = -1;
                for (int i = 0; i < endpoints.Count; i++)
                {
                    float error = math.abs(endpoints[i].Lateral - snapshotLateral);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    endpoint = endpoints[bestIndex];
                    detail = $"lateralFallback {snapshotLaneIndex}->{endpoint.LaneIndex} snapshotLateral={snapshotLateral:0.##} currentLateral={endpoint.Lateral:0.##} error={bestError:0.##}";
                    return true;
                }
            }

            detail = $"laneMissing lane={snapshotLaneIndex} endpoints={FormatLaneOrder(endpoints)}";
            return false;
        }

        private static void AddFarSnapshotSkipSample(List<string> samples, string value)
        {
            if (samples.Count < 8)
            {
                samples.Add(value);
            }
        }
    }
}
