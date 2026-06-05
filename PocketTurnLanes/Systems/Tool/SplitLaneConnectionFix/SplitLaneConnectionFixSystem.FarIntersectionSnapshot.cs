using System;
using System.Collections.Generic;
using Game.Common;
using PocketTurnLanes.Tool.Traffic;
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
                Entries = Array.Empty<TrafficSourceSnapshot>()
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

            List<TrafficSourceSnapshot> entries = new List<TrafficSourceSnapshot>(8);
            TryReadTrafficSourceSnapshots(
                trafficApi,
                farNode,
                null,
                null,
                entries,
                out TrafficSnapshotReadStats readStats,
                out _);

            HashSet<Entity> sourceEdges = new HashSet<Entity>();
            HashSet<Entity> targetEdges = new HashSet<Entity>();
            int generatedConnections = 0;
            int continuationSourceEntries = 0;
            int continuationGeneratedReferences = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                TrafficSourceSnapshot entry = entries[i];
                sourceEdges.Add(entry.SourceEdge);
                entry.SourceEndpoint = CaptureFarSnapshotEndpoint(
                    farNode,
                    entry.SourceEdge,
                    EndpointRole.SourceEndAtNode,
                    entry.SourceLaneIndex);

                TrafficGeneratedSnapshot[] connections =
                    entry.Connections ?? Array.Empty<TrafficGeneratedSnapshot>();
                for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                {
                    TrafficGeneratedSnapshot connection = connections[connectionIndex];
                    targetEdges.Add(connection.TargetEdge);
                    connection.SourceEndpoint = CaptureFarSnapshotEndpoint(
                        farNode,
                        connection.SourceEdge,
                        EndpointRole.SourceEndAtNode,
                        connection.SourceLaneIndex);
                    connection.TargetEndpoint = CaptureFarSnapshotEndpoint(
                        farNode,
                        connection.TargetEdge,
                        EndpointRole.TargetStartAtNode,
                        connection.TargetLaneIndex);
                    connections[connectionIndex] = connection;
                    generatedConnections++;
                    if (connection.SourceEdge == continuationEdge ||
                        connection.TargetEdge == continuationEdge)
                    {
                        continuationGeneratedReferences++;
                    }
                }

                if (entry.SourceEdge == continuationEdge)
                {
                    continuationSourceEntries++;
                }

                entry.Connections = connections;
                entries[i] = entry;
            }

            snapshot.Source = entries.Count > 0 ? "traffic" : "empty";
            snapshot.Entries = entries.ToArray();
            snapshot.Detail = $"snapshotSource={snapshot.Source} modifiedSources={entries.Count} generatedConnections={generatedConnections} sourceEdges={sourceEdges.Count} targetEdges={targetEdges.Count} continuationSourceEntries={continuationSourceEntries} continuationGeneratedReferences={continuationGeneratedReferences} missingGeneratedBuffers={readStats.MissingGeneratedBuffers}";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Captured far intersection Traffic snapshot farNode={FormatEntity(farNode)} continuation={FormatEntity(continuationEdge)} {snapshot.Detail}.");
            return snapshot;
        }

        private bool TryRefineBalancedReverseExtraTargetFromFarSnapshot(
            Request request,
            Entity outerEdge,
            IReadOnlyList<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string diagnostics)
        {
            extraTargetIndex = -1;
            turn = TurnDirection.Ambiguous;

            FarIntersectionTrafficSnapshot snapshot = request.FarIntersectionSnapshot;
            string snapshotSummary = FormatFarSnapshot(snapshot);
            if (selectedTargets == null || selectedTargets.Count == 0)
            {
                diagnostics = $"farSnapshotSelection=skipped reason=noSelectedTargets snapshot=({snapshotSummary}) liveFarConnectorDetection=disabled";
                return false;
            }

            if (snapshot == null ||
                snapshot.Entries == null ||
                snapshot.Entries.Length == 0)
            {
                diagnostics = $"farSnapshotSelection=skipped reason=emptySnapshot snapshot=({snapshotSummary}) liveFarConnectorDetection=disabled";
                return false;
            }

            if (snapshot.Node == Entity.Null ||
                !EntityManager.Exists(snapshot.Node) ||
                snapshot.ContinuationEdge == Entity.Null)
            {
                diagnostics = $"farSnapshotSelection=skipped reason=invalidSnapshotNodeOrContinuation snapshot=({snapshotSummary}) farNode={FormatEntity(snapshot?.Node ?? Entity.Null)} continuation={FormatEntity(snapshot?.ContinuationEdge ?? Entity.Null)} liveFarConnectorDetection=disabled";
                return false;
            }

            int[] leftCounts = new int[selectedTargets.Count];
            int[] rightCounts = new int[selectedTargets.Count];
            int[] straightCounts = new int[selectedTargets.Count];
            int continuationSourceEntries = 0;
            int nonContinuationSourceEntries = 0;
            int continuationGeneratedSources = 0;
            int generatedSourceNotContinuation = 0;
            int resolvedSources = 0;
            int mappedSelectedTargets = 0;
            int classifiedConnections = 0;
            int skippedConnections = 0;
            int skippedSelfReferences = 0;
            List<string> evidenceSamples = new List<string>(8);
            List<string> skipSamples = new List<string>(8);

            for (int entryIndex = 0; entryIndex < snapshot.Entries.Length; entryIndex++)
            {
                TrafficSourceSnapshot entry = snapshot.Entries[entryIndex];
                if (entry.SourceEdge != snapshot.ContinuationEdge)
                {
                    nonContinuationSourceEntries++;
                    continue;
                }

                continuationSourceEntries++;
                TrafficGeneratedSnapshot[] connections = entry.Connections ?? Array.Empty<TrafficGeneratedSnapshot>();
                for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                {
                    TrafficGeneratedSnapshot connection = connections[connectionIndex];
                    if (connection.SourceEdge != snapshot.ContinuationEdge)
                    {
                        generatedSourceNotContinuation++;
                        continue;
                    }

                    continuationGeneratedSources++;
                    if (!TryMapFarSnapshotEdge(
                            snapshot,
                            request,
                            connection.SourceEdge,
                            out Entity sourceEdge,
                            out _,
                            out string sourceEdgeReason))
                    {
                        skippedConnections++;
                        AddFarSnapshotSkipSample(skipSamples, $"sourceEdge {FormatEntity(connection.SourceEdge)}:{connection.SourceLaneIndex} {sourceEdgeReason}");
                        continue;
                    }

                    if (sourceEdge != outerEdge)
                    {
                        AddFarSnapshotSkipSample(skipSamples, $"sourceEdgeMappedUnexpected {FormatEntity(connection.SourceEdge)}->{FormatEntity(sourceEdge)} expectedOuter={FormatEntity(outerEdge)}");
                    }

                    TrafficEndpointSnapshot sourceAnchor = connection.SourceEndpoint;
                    if (!TryResolveFarSnapshotEndpoint(
                            snapshot.Node,
                            sourceEdge,
                            EndpointRole.SourceEndAtNode,
                            connection.SourceLaneIndex,
                            sourceAnchor.HasEndpoint,
                            sourceAnchor.Lateral,
                            sourceAnchor.Order,
                            out LaneEndpoint resolvedSource,
                            out string sourceResolveDetail))
                    {
                        skippedConnections++;
                        AddFarSnapshotSkipSample(skipSamples, $"sourceResolve {FormatEntity(connection.SourceEdge)}:{connection.SourceLaneIndex}->{FormatEntity(sourceEdge)} {sourceResolveDetail}");
                        continue;
                    }

                    resolvedSources++;
                    if (!TrafficCenterTurnTargetSelector.TryFindTargetByEndpointLaneIndexes(
                        selectedTargets,
                        resolvedSource,
                        out int targetListIndex,
                        out string selectedTargetMatch))
                    {
                        skippedConnections++;
                        AddFarSnapshotSkipSample(skipSamples, $"sourceTargetMap {FormatEntity(sourceEdge)}:{resolvedSource.LaneIndex}/C{resolvedSource.OppositeLaneIndex} {selectedTargetMatch} selectedTargets={FormatLaneOrder(selectedTargets)}");
                        continue;
                    }

                    mappedSelectedTargets++;
                    if (!TryMapFarSnapshotEdge(
                            snapshot,
                            request,
                            connection.TargetEdge,
                            out Entity targetEdge,
                            out _,
                            out string targetEdgeReason))
                    {
                        skippedConnections++;
                        AddFarSnapshotSkipSample(skipSamples, $"targetEdge {FormatEntity(connection.TargetEdge)}:{connection.TargetLaneIndex} {targetEdgeReason}");
                        continue;
                    }

                    if (targetEdge == sourceEdge)
                    {
                        skippedConnections++;
                        skippedSelfReferences++;
                        AddFarSnapshotSkipSample(skipSamples, $"selfReference source={FormatEntity(sourceEdge)}:{resolvedSource.LaneIndex} target={FormatEntity(targetEdge)}:{connection.TargetLaneIndex}");
                        continue;
                    }

                    TurnDirection connectorTurn = TrafficConnectorMovementClassifier.ClassifyCenterConnectorTurn(
                        EntityManager,
                        snapshot.Node,
                        sourceEdge,
                        targetEdge,
                        default);
                    TrafficCenterTurnTargetSelector.AddTurnCount(
                        connectorTurn,
                        targetListIndex,
                        leftCounts,
                        rightCounts,
                        straightCounts);
                    classifiedConnections++;
                    AddFarSnapshotSkipSample(
                        evidenceSamples,
                        $"{resolvedSource.LaneIndex}->target{selectedTargets[targetListIndex].LaneIndex}/{connectorTurn}/{FormatEntity(targetEdge)}/{selectedTargetMatch}");
                }
            }

            string countDiagnostics = FormatCenterTurnDiagnostics(
                selectedTargets,
                leftCounts,
                rightCounts,
                straightCounts,
                null);
            if (!TrafficCenterTurnTargetSelector.TrySelectExtraTarget(
                    selectedTargets,
                    leftCounts,
                    rightCounts,
                    straightCounts,
                    out extraTargetIndex,
                    out turn,
                    out string selectionDiagnostic))
            {
                diagnostics = $"farSnapshotSelection=failed snapshot=({snapshotSummary}) continuationSourceEntries={continuationSourceEntries} nonContinuationSourceEntries={nonContinuationSourceEntries} continuationGeneratedSources={continuationGeneratedSources} generatedSourceNotContinuation={generatedSourceNotContinuation} resolvedSources={resolvedSources} mappedSelectedTargets={mappedSelectedTargets} classifiedConnections={classifiedConnections} skippedConnections={skippedConnections} skippedSelfReferences={skippedSelfReferences} counts=({countDiagnostics}) {selectionDiagnostic} evidenceSamples={FormatStringList(evidenceSamples)} skipSamples={FormatStringList(skipSamples)} liveFarConnectorDetection=disabled";
                return false;
            }

            diagnostics = $"farSnapshotSelection=selected snapshot=({snapshotSummary}) continuationSourceEntries={continuationSourceEntries} nonContinuationSourceEntries={nonContinuationSourceEntries} continuationGeneratedSources={continuationGeneratedSources} generatedSourceNotContinuation={generatedSourceNotContinuation} resolvedSources={resolvedSources} mappedSelectedTargets={mappedSelectedTargets} classifiedConnections={classifiedConnections} skippedConnections={skippedConnections} skippedSelfReferences={skippedSelfReferences} selectedExtra={selectedTargets[extraTargetIndex].LaneIndex}/{turn} counts=({countDiagnostics}) {selectionDiagnostic} evidenceSamples={FormatStringList(evidenceSamples)} skipSamples={FormatStringList(skipSamples)} liveFarConnectorDetection=disabled";
            return true;
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

            if (!TryCreateTrafficLoadValidationContext(
                    snapshot.Node,
                    "far-restore",
                    out TrafficLoadValidationContext loadValidationContext,
                    out string loadValidationContextReason))
            {
                Mod.LogEssential($"[SplitLaneConnectionFix] Far intersection Traffic restore skipped before mutation because owner node cannot pass Traffic load validation splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(snapshot.Node)} outerEdge={FormatEntity(request.OuterEdge)} reason={loadValidationContextReason} snapshot={detail}.");
                return false;
            }

            int writtenSources = 0;
            int writtenConnections = 0;
            int removedExisting = 0;
            int skippedSources = 0;
            int skippedConnections = 0;
            int remappedSourceEntries = 0;
            int remappedGeneratedEdges = 0;
            int remappedLaneIndexes = 0;
            List<string> skipSamples = new List<string>(8);
            List<TrafficSourceSnapshot> sourcesToWrite = new List<TrafficSourceSnapshot>(snapshot.Entries.Length);
            HashSet<SourceLaneKey> rewriteSourceKeys = new HashSet<SourceLaneKey>();
            TrafficLoadValidationStats loadValidationStats = CreateTrafficLoadValidationStats();

            for (int i = 0; i < snapshot.Entries.Length; i++)
            {
                TrafficSourceSnapshot entry = snapshot.Entries[i];
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
                TrafficEndpointSnapshot sourceAnchor = entry.SourceEndpoint;
                if (sourceEdgeRemapped)
                {
                    if (!TryResolveFarSnapshotEndpoint(
                            snapshot.Node,
                            sourceEdge,
                            EndpointRole.SourceEndAtNode,
                            entry.SourceLaneIndex,
                            sourceAnchor.HasEndpoint,
                            sourceAnchor.Lateral,
                            sourceAnchor.Order,
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

                List<TrafficGeneratedSnapshot> remappedConnections = new List<TrafficGeneratedSnapshot>(
                    entry.Connections?.Length ?? 0);
                TrafficGeneratedSnapshot[] connections =
                    entry.Connections ?? Array.Empty<TrafficGeneratedSnapshot>();
                for (int connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
                {
                    TrafficGeneratedSnapshot connection = connections[connectionIndex];
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
                    TrafficEndpointSnapshot generatedSourceAnchor = connection.SourceEndpoint;
                    TrafficEndpointSnapshot targetAnchor = connection.TargetEndpoint;

                    if (generatedSourceRemapped)
                    {
                        if (!TryResolveFarSnapshotEndpoint(
                                snapshot.Node,
                                generatedSourceEdge,
                                EndpointRole.SourceEndAtNode,
                                connection.SourceLaneIndex,
                                generatedSourceAnchor.HasEndpoint,
                                generatedSourceAnchor.Lateral,
                                generatedSourceAnchor.Order,
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
                                targetAnchor.HasEndpoint,
                                targetAnchor.Lateral,
                                targetAnchor.Order,
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

                    remappedConnections.Add(new TrafficGeneratedSnapshot
                    {
                        SourceEdge = generatedSourceEdge,
                        TargetEdge = targetEdge,
                        SourceLaneIndex = generatedSourceLaneIndex,
                        TargetLaneIndex = targetLaneIndex,
                        LanePositionMap = lanePositionMap,
                        CarriagewayAndGroupIndexMap = carriagewayAndGroupIndexMap,
                        Method = connection.Method,
                        IsUnsafe = connection.IsUnsafe
                    });
                }

                TrafficSourceSnapshot sourceToWrite = entry;
                sourceToWrite.SourceEdge = sourceEdge;
                sourceToWrite.SourceLaneIndex = sourceLaneIndex;
                sourceToWrite.SourceCarriagewayAndGroup = sourceCarriagewayAndGroup;
                sourceToWrite.SourceLanePosition = sourceLanePosition;
                sourceToWrite.Connections = remappedConnections.ToArray();

                bool allowEmptySource = connections.Length == 0;
                if (remappedConnections.Count == 0 && connections.Length > 0)
                {
                    skippedSources++;
                    AddFarSnapshotSkipSample(skipSamples, $"sourceNoRemappedConnections {FormatEntity(sourceEdge)}:{sourceLaneIndex} originalConnections={connections.Length}");
                    continue;
                }

                if (!TrySanitizeTrafficSourceSnapshotForLoad(
                        loadValidationContext,
                        sourceToWrite,
                        allowEmptySource,
                        "far-restore",
                        out TrafficSourceSnapshot sanitizedSource,
                        ref loadValidationStats))
                {
                    skippedSources++;
                    continue;
                }

                sourcesToWrite.Add(sanitizedSource);
                rewriteSourceKeys.Add(new SourceLaneKey(sanitizedSource.SourceEdge, sanitizedSource.SourceLaneIndex));
            }

            if (sourcesToWrite.Count == 0)
            {
                detail = $"snapshot={FormatFarSnapshot(snapshot)} removedExisting=0 writtenSources=0 expectedSources={snapshot.Entries.Length} writtenConnections=0 skippedSources={skippedSources} skippedConnections={skippedConnections} remappedSourceEntries={remappedSourceEntries} remappedGeneratedEdges={remappedGeneratedEdges} remappedLaneIndexes={remappedLaneIndexes} trafficLoadValidation=({FormatTrafficLoadValidationStats(loadValidationStats, rewriteSourceKeys)}) skipSamples={FormatStringList(skipSamples)}";
                Mod.LogEssential($"[SplitLaneConnectionFix] Far intersection Traffic restore skipped before mutation because no remapped source survived load validation splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(snapshot.Node)} continuation={FormatEntity(snapshot.ContinuationEdge)} outerEdge={FormatEntity(request.OuterEdge)} trafficWriteOrder={GetTrafficWriteOrder(request.Mode)} {detail}.");
                return false;
            }

            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, snapshot.Node);
            if (modifiedBuffer == null)
            {
                detail = $"snapshot={FormatFarSnapshot(snapshot)} reason=modifiedBufferUnavailable trafficLoadValidation=({FormatTrafficLoadValidationStats(loadValidationStats, rewriteSourceKeys)})";
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Far intersection Traffic restore failed before mutation splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(snapshot.Node)} outerEdge={FormatEntity(request.OuterEdge)} {detail}.");
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

            for (int i = 0; i < sourcesToWrite.Count; i++)
            {
                TrafficSourceSnapshot sourceToWrite = sourcesToWrite[i];
                if (!TryWriteTrafficSourceSnapshot(
                        trafficApi,
                        snapshot.Node,
                        modifiedBuffer,
                        sourceToWrite,
                        out int sourceWrittenConnections))
                {
                    skippedSources++;
                    AddFarSnapshotSkipSample(skipSamples, $"writeSource {FormatEntity(sourceToWrite.SourceEdge)}:{sourceToWrite.SourceLaneIndex} failed");
                    continue;
                }

                writtenConnections += sourceWrittenConnections;
                writtenSources++;
            }

            trafficApi.EnsureModifiedConnectionsTag(EntityManager, snapshot.Node);
            MarkCenterForLaneRebuild(snapshot.Node);
            detail = $"snapshot={FormatFarSnapshot(snapshot)} removedExisting={removedExisting} preservedExisting={m_KeptTrafficConnections.Count} writtenSources={writtenSources} expectedSources={snapshot.Entries.Length} writtenConnections={writtenConnections} skippedSources={skippedSources} skippedConnections={skippedConnections} remappedSourceEntries={remappedSourceEntries} remappedGeneratedEdges={remappedGeneratedEdges} remappedLaneIndexes={remappedLaneIndexes} trafficLoadValidation=({FormatTrafficLoadValidationStats(loadValidationStats, rewriteSourceKeys)}) skipSamples={FormatStringList(skipSamples)}";
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Restored far intersection Traffic snapshot splitNode={FormatEntity(request.SplitNode)} farNode={FormatEntity(snapshot.Node)} continuation={FormatEntity(snapshot.ContinuationEdge)} outerEdge={FormatEntity(request.OuterEdge)} trafficWriteOrder={GetTrafficWriteOrder(request.Mode)} {detail}.");
            return skippedSources == 0 && skippedConnections == 0;
        }

        private TrafficEndpointSnapshot CaptureFarSnapshotEndpoint(
            Entity node,
            Entity edge,
            EndpointRole role,
            int laneIndex)
        {
            if (node == Entity.Null ||
                edge == Entity.Null ||
                !EntityManager.Exists(node) ||
                !IsEdgeConnectedToNode(edge, node))
            {
                return TrafficSnapshotLaneMapper.CreateMissingEndpointSnapshot();
            }

            List<LaneEndpoint> endpoints = new List<LaneEndpoint>(8);
            CollectEdgePreservationLaneEndpoints(edge, node, role, endpoints);
            TrafficLaneEndpointHelpers.SortByLateral(endpoints);
            return TrafficSnapshotLaneMapper.CaptureEndpointSnapshot(endpoints, laneIndex);
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
            CollectEdgePreservationLaneEndpoints(edge, node, role, endpoints);
            TrafficLaneEndpointHelpers.SortByLateral(endpoints);
            if (endpoints.Count == 0)
            {
                detail = $"noEndpoints edge={FormatEntity(edge)} node={FormatEntity(node)} role={role}";
                return false;
            }

            return TrafficSnapshotLaneMapper.TryResolveEndpoint(
                endpoints,
                snapshotLaneIndex,
                hasSnapshotEndpoint,
                snapshotLateral,
                snapshotOrder,
                FormatLaneOrder,
                out endpoint,
                out detail);
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
