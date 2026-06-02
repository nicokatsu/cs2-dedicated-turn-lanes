using System.Collections.Generic;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficLaneTargetSelector
    {
        public static bool TrySelectPocketTargets(
            List<LaneEndpoint> sourceLanes,
            List<LaneEndpoint> targetLanes,
            out List<LaneEndpoint> selectedTargets,
            out int extraTargetIndex,
            out float bestScore)
        {
            selectedTargets = null;
            extraTargetIndex = -1;
            bestScore = float.MaxValue;

            int desiredTargetCount = sourceLanes.Count + 1;
            if (targetLanes.Count < desiredTargetCount)
            {
                return false;
            }

            int maxStart = targetLanes.Count - desiredTargetCount;
            for (int start = 0; start <= maxStart; start++)
            {
                List<LaneEndpoint> subset = targetLanes.GetRange(start, desiredTargetCount);
                for (int extraCandidate = 0; extraCandidate < 2; extraCandidate++)
                {
                    int extraIndex = extraCandidate == 0 ? 0 : subset.Count - 1;
                    float score = 0f;
                    int sourceIndex = 0;
                    for (int targetIndex = 0; targetIndex < subset.Count; targetIndex++)
                    {
                        if (targetIndex == extraIndex)
                        {
                            continue;
                        }

                        score += math.abs(sourceLanes[sourceIndex].Lateral - subset[targetIndex].Lateral);
                        sourceIndex++;
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        extraTargetIndex = extraIndex;
                        selectedTargets = subset;
                    }
                }
            }

            return selectedTargets != null && extraTargetIndex >= 0;
        }

        public static TurnDirection DetermineTurn(IReadOnlyList<LaneEndpoint> selectedTargets, int extraTargetIndex)
        {
            if (extraTargetIndex == 0)
            {
                return TurnDirection.Left;
            }

            if (extraTargetIndex == selectedTargets.Count - 1)
            {
                return TurnDirection.Right;
            }

            return TurnDirection.Ambiguous;
        }
    }
}
