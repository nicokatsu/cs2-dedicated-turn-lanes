using System;
using System.Collections.Generic;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
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

    public sealed class FarIntersectionTrafficSnapshot
    {
        public Entity Node;
        public Entity ContinuationEdge;
        public string Source;
        public string Detail;
        public TrafficSourceSnapshot[] Entries;
    }

    public struct TrafficEndpointSnapshot
    {
        public bool HasEndpoint;
        public float Lateral;
        public int Order;
    }

    public struct TrafficGeneratedSnapshot
    {
        public Entity SourceEdge;
        public Entity TargetEdge;
        public int SourceLaneIndex;
        public int TargetLaneIndex;
        public float3x2 LanePositionMap;
        public int4 CarriagewayAndGroupIndexMap;
        public PathMethod Method;
        public bool IsUnsafe;
        public TrafficEndpointSnapshot SourceEndpoint;
        public TrafficEndpointSnapshot TargetEndpoint;
    }

    public struct TrafficSourceSnapshot
    {
        public Entity SourceEdge;
        public int SourceLaneIndex;
        public int2 SourceCarriagewayAndGroup;
        public float3 SourceLanePosition;
        public Entity ModifiedConnectionEntity;
        public bool HasGeneratedBuffer;
        public TrafficEndpointSnapshot SourceEndpoint;
        public TrafficGeneratedSnapshot[] Connections;
    }

    internal struct TrafficSnapshotReadStats
    {
        public int ModifiedSources;
        public int AcceptedSources;
        public int SkippedSources;
        public int GeneratedConnections;
        public int AcceptedGeneratedConnections;
        public int SkippedGeneratedConnections;
        public int MissingGeneratedBuffers;
    }

    internal static class TrafficSnapshotHelpers
    {
        public static TrafficSourceSnapshot CreateSourceSnapshot(TrafficApi trafficApi, object modified)
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

        public static TrafficGeneratedSnapshot CreateGeneratedSnapshot(TrafficApi trafficApi, object generated)
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

        public static void WriteGeneratedSnapshot(
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

        public static int CountConnections(IReadOnlyList<TrafficSourceSnapshot> snapshots)
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

        public static string FormatReadStats(TrafficSnapshotReadStats stats)
        {
            return $"modifiedSources={stats.ModifiedSources} acceptedSources={stats.AcceptedSources} generatedConnections={stats.GeneratedConnections} acceptedGeneratedConnections={stats.AcceptedGeneratedConnections} missingGeneratedBuffers={stats.MissingGeneratedBuffers} skippedSources={stats.SkippedSources} skippedGeneratedConnections={stats.SkippedGeneratedConnections}";
        }
    }
}
