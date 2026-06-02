using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal struct LaneEndpoint
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

    internal struct ConnectorLane
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
}
