using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
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
}
