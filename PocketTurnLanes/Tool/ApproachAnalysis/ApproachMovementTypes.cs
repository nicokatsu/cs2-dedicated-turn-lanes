using System;
using Unity.Entities;

namespace PocketTurnLanes.Tool.ApproachAnalysis
{
    internal enum ApproachMovement
    {
        Ambiguous,
        Straight,
        Left,
        Right
    }

    internal readonly struct ApproachMovementKey : IEquatable<ApproachMovementKey>
    {
        public readonly ApproachMovement Movement;
        public readonly Entity TargetEdge;

        public ApproachMovementKey(ApproachMovement movement, Entity targetEdge)
        {
            Movement = movement;
            TargetEdge = targetEdge;
        }

        public bool IsTurn => Movement == ApproachMovement.Left || Movement == ApproachMovement.Right;

        public bool Equals(ApproachMovementKey other)
        {
            return Movement == other.Movement && TargetEdge == other.TargetEdge;
        }

        public override bool Equals(object obj)
        {
            return obj is ApproachMovementKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Movement;
                hash = (hash * 397) ^ TargetEdge.Index;
                hash = (hash * 397) ^ TargetEdge.Version;
                return hash;
            }
        }
    }

    internal struct ApproachLaneUsage
    {
        public int LaneIndex;
        public int Straight;
        public int Left;
        public int Right;
        public int Ambiguous;

        public void Add(ApproachMovement movement)
        {
            switch (movement)
            {
                case ApproachMovement.Straight:
                    Straight++;
                    break;
                case ApproachMovement.Left:
                    Left++;
                    break;
                case ApproachMovement.Right:
                    Right++;
                    break;
                default:
                    Ambiguous++;
                    break;
            }
        }
    }
}
