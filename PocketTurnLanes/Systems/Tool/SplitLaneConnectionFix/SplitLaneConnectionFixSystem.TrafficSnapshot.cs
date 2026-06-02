using System;
using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private delegate bool TrafficSourceSnapshotPredicate(TrafficSourceSnapshot source);

        private delegate bool TrafficGeneratedSnapshotPredicate(
            TrafficSourceSnapshot source,
            TrafficGeneratedSnapshot generated);

        private struct TrafficSnapshotReadStats
        {
            public int ModifiedSources;
            public int AcceptedSources;
            public int SkippedSources;
            public int GeneratedConnections;
            public int AcceptedGeneratedConnections;
            public int SkippedGeneratedConnections;
            public int MissingGeneratedBuffers;
        }

        private bool TryReadTrafficSourceSnapshots(
            TrafficApi trafficApi,
            Entity node,
            TrafficSourceSnapshotPredicate sourceFilter,
            TrafficGeneratedSnapshotPredicate generatedFilter,
            List<TrafficSourceSnapshot> snapshots,
            out TrafficSnapshotReadStats stats,
            out string detail)
        {
            stats = default;
            detail = "none";
            snapshots.Clear();
            if (trafficApi == null ||
                node == Entity.Null ||
                !EntityManager.Exists(node) ||
                !trafficApi.HasModifiedLaneConnectionsBuffer(EntityManager, node))
            {
                detail = "trafficBuffer=missing";
                return false;
            }

            object modifiedBuffer = trafficApi.GetModifiedLaneConnectionsBuffer(EntityManager, node, true);
            ReadTrafficSourceSnapshotsFromBuffer(
                trafficApi,
                modifiedBuffer,
                sourceFilter,
                generatedFilter,
                snapshots,
                ref stats);
            detail = FormatTrafficSnapshotReadStats(stats);
            return true;
        }

        private void ReadTrafficSourceSnapshotsFromBuffer(
            TrafficApi trafficApi,
            object modifiedBuffer,
            TrafficSourceSnapshotPredicate sourceFilter,
            TrafficGeneratedSnapshotPredicate generatedFilter,
            List<TrafficSourceSnapshot> snapshots,
            ref TrafficSnapshotReadStats stats)
        {
            snapshots.Clear();
            if (trafficApi == null || modifiedBuffer == null)
            {
                return;
            }

            int length = trafficApi.GetBufferLength(modifiedBuffer);
            stats.ModifiedSources = length;
            for (int i = 0; i < length; i++)
            {
                object modified = trafficApi.GetBufferItem(modifiedBuffer, i);
                TrafficSourceSnapshot source = CreateTrafficSourceSnapshot(trafficApi, modified);
                if (sourceFilter != null && !sourceFilter(source))
                {
                    stats.SkippedSources++;
                    continue;
                }

                stats.AcceptedSources++;
                Entity modifiedEntity = source.ModifiedConnectionEntity;
                if (modifiedEntity == Entity.Null ||
                    !EntityManager.Exists(modifiedEntity) ||
                    !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
                {
                    stats.MissingGeneratedBuffers++;
                    source.HasGeneratedBuffer = false;
                    source.Connections = Array.Empty<TrafficGeneratedSnapshot>();
                    snapshots.Add(source);
                    continue;
                }

                source.HasGeneratedBuffer = true;
                List<TrafficGeneratedSnapshot> generatedSnapshots = new List<TrafficGeneratedSnapshot>(4);
                object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
                int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
                for (int generatedIndex = 0; generatedIndex < generatedLength; generatedIndex++)
                {
                    object generated = trafficApi.GetBufferItem(generatedBuffer, generatedIndex);
                    TrafficGeneratedSnapshot generatedSnapshot = CreateTrafficGeneratedSnapshot(trafficApi, generated);
                    stats.GeneratedConnections++;
                    if (generatedFilter != null && !generatedFilter(source, generatedSnapshot))
                    {
                        stats.SkippedGeneratedConnections++;
                        continue;
                    }

                    stats.AcceptedGeneratedConnections++;
                    generatedSnapshots.Add(generatedSnapshot);
                }

                source.Connections = generatedSnapshots.ToArray();
                snapshots.Add(source);
            }
        }

        private bool TryReadTrafficGeneratedSnapshots(
            TrafficApi trafficApi,
            Entity modifiedEntity,
            List<TrafficGeneratedSnapshot> snapshots)
        {
            snapshots.Clear();
            if (trafficApi == null ||
                modifiedEntity == Entity.Null ||
                !EntityManager.Exists(modifiedEntity) ||
                !trafficApi.HasGeneratedConnectionBuffer(EntityManager, modifiedEntity))
            {
                return false;
            }

            object generatedBuffer = trafficApi.GetGeneratedConnectionBuffer(EntityManager, modifiedEntity, true);
            int generatedLength = trafficApi.GetBufferLength(generatedBuffer);
            for (int i = 0; i < generatedLength; i++)
            {
                snapshots.Add(CreateTrafficGeneratedSnapshot(trafficApi, trafficApi.GetBufferItem(generatedBuffer, i)));
            }

            return true;
        }

        private static TrafficSourceSnapshot CreateTrafficSourceSnapshot(TrafficApi trafficApi, object modified)
        {
            return new TrafficSourceSnapshot
            {
                SourceEdge = trafficApi.GetModifiedConnectionEdge(modified),
                SourceLaneIndex = trafficApi.GetModifiedConnectionLaneIndex(modified),
                SourceCarriagewayAndGroup = trafficApi.GetModifiedConnectionCarriagewayAndGroup(modified),
                SourceLanePosition = trafficApi.GetModifiedConnectionLanePosition(modified),
                ModifiedConnectionEntity = trafficApi.GetModifiedConnectionEntity(modified),
                Connections = Array.Empty<TrafficGeneratedSnapshot>()
            };
        }

        private static TrafficGeneratedSnapshot CreateTrafficGeneratedSnapshot(TrafficApi trafficApi, object generated)
        {
            int2 laneIndexMap = trafficApi.GetGeneratedConnectionLaneIndexMap(generated);
            return new TrafficGeneratedSnapshot
            {
                SourceEdge = trafficApi.GetGeneratedConnectionSource(generated),
                TargetEdge = trafficApi.GetGeneratedConnectionTarget(generated),
                SourceLaneIndex = laneIndexMap.x & 0xff,
                TargetLaneIndex = laneIndexMap.y & 0xff,
                LanePositionMap = trafficApi.GetGeneratedConnectionLanePositionMap(generated),
                CarriagewayAndGroupIndexMap = trafficApi.GetGeneratedConnectionCarriagewayAndGroupIndexMap(generated),
                Method = trafficApi.GetGeneratedConnectionMethod(generated),
                IsUnsafe = trafficApi.GetGeneratedConnectionUnsafe(generated)
            };
        }

        private object GetOrReplaceTrafficModifiedConnectionsBuffer(
            TrafficApi trafficApi,
            Entity node,
            out int removedExisting)
        {
            removedExisting = 0;
            object modifiedBuffer = trafficApi.GetOrAddModifiedLaneConnectionsBuffer(EntityManager, node);
            if (modifiedBuffer == null)
            {
                return null;
            }

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
            return modifiedBuffer;
        }

        private bool TryWriteTrafficSourceSnapshot(
            TrafficApi trafficApi,
            Entity ownerNode,
            object modifiedBuffer,
            TrafficSourceSnapshot source,
            out int writtenConnections)
        {
            writtenConnections = 0;
            if (trafficApi == null || modifiedBuffer == null)
            {
                return false;
            }

            Entity modifiedConnectionEntity = CreateTrafficModifiedConnectionEntity(
                trafficApi,
                ownerNode,
                out object generatedBuffer);

            TrafficGeneratedSnapshot[] connections = source.Connections ?? Array.Empty<TrafficGeneratedSnapshot>();
            for (int i = 0; i < connections.Length; i++)
            {
                WriteTrafficGeneratedSnapshot(trafficApi, generatedBuffer, connections[i]);
                writtenConnections++;
            }

            trafficApi.AddBufferElement(modifiedBuffer, trafficApi.CreateModifiedLaneConnection(
                source.SourceLaneIndex,
                source.SourceCarriagewayAndGroup,
                source.SourceLanePosition,
                source.SourceEdge,
                modifiedConnectionEntity));
            return true;
        }

        private static void WriteTrafficGeneratedSnapshot(
            TrafficApi trafficApi,
            object generatedBuffer,
            TrafficGeneratedSnapshot connection)
        {
            trafficApi.AddBufferElement(generatedBuffer, trafficApi.CreateGeneratedConnection(
                connection.SourceEdge,
                connection.TargetEdge,
                connection.SourceLaneIndex,
                connection.TargetLaneIndex,
                connection.LanePositionMap,
                connection.CarriagewayAndGroupIndexMap,
                connection.Method,
                connection.IsUnsafe));
        }

        private static LaneMapping CreateLaneMappingFromTrafficSnapshot(
            TrafficGeneratedSnapshot snapshot,
            PathMethod method)
        {
            return new LaneMapping
            {
                SourceEdge = snapshot.SourceEdge,
                TargetEdge = snapshot.TargetEdge,
                SourceLaneIndex = snapshot.SourceLaneIndex,
                TargetLaneIndex = snapshot.TargetLaneIndex,
                TrafficLanePositionMap = snapshot.LanePositionMap,
                TrafficCarriagewayAndGroupIndexMap = snapshot.CarriagewayAndGroupIndexMap,
                Method = method,
                IsBranch = false,
                IsPreservationOnly = true,
                IsUnsafe = snapshot.IsUnsafe,
                HasTrafficMaps = true,
                HasPreservedPathMethods = true
            };
        }

        private static int CountTrafficSnapshotConnections(IReadOnlyList<TrafficSourceSnapshot> snapshots)
        {
            int count = 0;
            if (snapshots == null)
            {
                return count;
            }

            for (int i = 0; i < snapshots.Count; i++)
            {
                count += snapshots[i].Connections?.Length ?? 0;
            }

            return count;
        }

        private static string FormatTrafficSnapshotReadStats(TrafficSnapshotReadStats stats)
        {
            return $"modifiedSources={stats.ModifiedSources} acceptedSources={stats.AcceptedSources} generatedConnections={stats.GeneratedConnections} acceptedGeneratedConnections={stats.AcceptedGeneratedConnections} missingGeneratedBuffers={stats.MissingGeneratedBuffers} skippedSources={stats.SkippedSources} skippedGeneratedConnections={stats.SkippedGeneratedConnections}";
        }
    }
}
