using System.Collections.Generic;
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

    internal readonly struct TrafficSplitPairEndpointLookup
    {
        public readonly Entity OuterEdge;
        public readonly Entity PocketEdge;
        public readonly IReadOnlyList<LaneEndpoint> SourceLanes;
        public readonly IReadOnlyList<LaneEndpoint> TargetLanes;
        public readonly IReadOnlyList<LaneEndpoint> ReverseSourceLanes;
        public readonly IReadOnlyList<LaneEndpoint> ReverseTargetLanes;
        public readonly IReadOnlyList<LaneEndpoint> PreservationForwardSourceLanes;
        public readonly IReadOnlyList<LaneEndpoint> PreservationForwardTargetLanes;
        public readonly IReadOnlyList<LaneEndpoint> PreservationReverseSourceLanes;
        public readonly IReadOnlyList<LaneEndpoint> PreservationReverseTargetLanes;

        public TrafficSplitPairEndpointLookup(
            Entity outerEdge,
            Entity pocketEdge,
            IReadOnlyList<LaneEndpoint> sourceLanes,
            IReadOnlyList<LaneEndpoint> targetLanes,
            IReadOnlyList<LaneEndpoint> reverseSourceLanes,
            IReadOnlyList<LaneEndpoint> reverseTargetLanes,
            IReadOnlyList<LaneEndpoint> preservationForwardSourceLanes,
            IReadOnlyList<LaneEndpoint> preservationForwardTargetLanes,
            IReadOnlyList<LaneEndpoint> preservationReverseSourceLanes,
            IReadOnlyList<LaneEndpoint> preservationReverseTargetLanes)
        {
            OuterEdge = outerEdge;
            PocketEdge = pocketEdge;
            SourceLanes = sourceLanes;
            TargetLanes = targetLanes;
            ReverseSourceLanes = reverseSourceLanes;
            ReverseTargetLanes = reverseTargetLanes;
            PreservationForwardSourceLanes = preservationForwardSourceLanes;
            PreservationForwardTargetLanes = preservationForwardTargetLanes;
            PreservationReverseSourceLanes = preservationReverseSourceLanes;
            PreservationReverseTargetLanes = preservationReverseTargetLanes;
        }

        public bool TryFind(Entity edge, int laneIndex, bool source, out LaneEndpoint lane)
        {
            if (source)
            {
                return TryFindSource(edge, laneIndex, out lane);
            }

            return TryFindTarget(edge, laneIndex, out lane);
        }

        private bool TryFindSource(Entity edge, int laneIndex, out LaneEndpoint lane)
        {
            if (TryFindOnEdge(edge, OuterEdge, SourceLanes, laneIndex, out lane) ||
                TryFindOnEdge(edge, OuterEdge, PreservationForwardSourceLanes, laneIndex, out lane) ||
                TryFindOnEdge(edge, PocketEdge, ReverseSourceLanes, laneIndex, out lane) ||
                TryFindOnEdge(edge, PocketEdge, PreservationReverseSourceLanes, laneIndex, out lane))
            {
                return true;
            }

            lane = default;
            return false;
        }

        private bool TryFindTarget(Entity edge, int laneIndex, out LaneEndpoint lane)
        {
            if (TryFindOnEdge(edge, PocketEdge, TargetLanes, laneIndex, out lane) ||
                TryFindOnEdge(edge, PocketEdge, PreservationForwardTargetLanes, laneIndex, out lane) ||
                TryFindOnEdge(edge, OuterEdge, ReverseTargetLanes, laneIndex, out lane) ||
                TryFindOnEdge(edge, OuterEdge, PreservationReverseTargetLanes, laneIndex, out lane))
            {
                return true;
            }

            lane = default;
            return false;
        }

        private static bool TryFindOnEdge(
            Entity actualEdge,
            Entity expectedEdge,
            IReadOnlyList<LaneEndpoint> lanes,
            int laneIndex,
            out LaneEndpoint lane)
        {
            if (actualEdge == expectedEdge &&
                TrafficLaneEndpointHelpers.TryFind(lanes, laneIndex, out lane))
            {
                return true;
            }

            lane = default;
            return false;
        }
    }
}
