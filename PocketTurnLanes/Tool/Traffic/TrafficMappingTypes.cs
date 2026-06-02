using System;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal struct LaneMapping
    {
        public Entity SourceEdge;
        public Entity TargetEdge;
        public Entity TemplateEntity;
        public int SourceLaneIndex;
        public int TargetLaneIndex;
        public float3x2 TrafficLanePositionMap;
        public int4 TrafficCarriagewayAndGroupIndexMap;
        public PathMethod Method;
        public PathMethod TemplatePathMethods;
        public bool IsBranch;
        public bool IsPreservationOnly;
        public bool IsUnsafe;
        public bool HasTrafficMaps;
        public bool HasPreservedPathMethods;
    }

    internal readonly struct ConnectionKey : IEquatable<ConnectionKey>
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

    internal readonly struct SourceLaneKey : IEquatable<SourceLaneKey>
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

    internal readonly struct TargetLaneKey : IEquatable<TargetLaneKey>
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
}
