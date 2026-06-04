using Unity.Entities;

namespace PocketTurnLanes.Tool
{
    internal struct EdgeLookupRejectedCandidate
    {
        public Entity Edge;
        public float LengthError;
        public float NodeDistance;
        public float Score;

        public static EdgeLookupRejectedCandidate CreateLength()
        {
            return new EdgeLookupRejectedCandidate
            {
                Edge = Entity.Null,
                LengthError = float.MaxValue,
                NodeDistance = float.MaxValue,
                Score = float.MaxValue
            };
        }

        public static EdgeLookupRejectedCandidate CreateScored()
        {
            return CreateLength();
        }

        public void RecordLength(Entity edge, float lengthError)
        {
            if (lengthError < LengthError)
            {
                Edge = edge;
                LengthError = lengthError;
            }
        }

        public void RecordScore(Entity edge, float score, float nodeDistance, float lengthError)
        {
            if (score < Score)
            {
                Edge = edge;
                Score = score;
                NodeDistance = nodeDistance;
                LengthError = lengthError;
            }
        }
    }
}
