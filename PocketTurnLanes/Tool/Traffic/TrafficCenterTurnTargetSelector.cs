using System;
using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficCenterTurnTargetSelector
    {
        public static bool TryFindTargetByCenterLaneIndex(
            IReadOnlyList<LaneEndpoint> targets,
            int centerLaneIndex,
            out int targetListIndex)
        {
            if (targets == null)
            {
                targetListIndex = -1;
                return false;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].OppositeLaneIndex == centerLaneIndex)
                {
                    targetListIndex = i;
                    return true;
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].LaneIndex == centerLaneIndex)
                {
                    targetListIndex = i;
                    return true;
                }
            }

            targetListIndex = -1;
            return false;
        }

        public static bool TrySelectExtraTarget(
            IReadOnlyList<LaneEndpoint> selectedTargets,
            IReadOnlyList<int> leftCounts,
            IReadOnlyList<int> rightCounts,
            IReadOnlyList<int> straightCounts,
            out int extraTargetIndex,
            out TurnDirection turn,
            out string selectionDiagnostic)
        {
            extraTargetIndex = -1;
            turn = TurnDirection.Ambiguous;
            selectionDiagnostic = "centerSelection=none";

            if (selectedTargets == null ||
                leftCounts == null ||
                rightCounts == null ||
                straightCounts == null ||
                selectedTargets.Count == 0 ||
                leftCounts.Count < selectedTargets.Count ||
                rightCounts.Count < selectedTargets.Count ||
                straightCounts.Count < selectedTargets.Count)
            {
                return false;
            }

            int bestIndex = -1;
            int bestScore = int.MinValue;
            TurnDirection bestTurn = TurnDirection.Ambiguous;
            bool tied = false;

            for (int i = 0; i < selectedTargets.Count; i++)
            {
                bool edgeTarget = i == 0 || i == selectedTargets.Count - 1;
                if (!edgeTarget)
                {
                    continue;
                }

                int left = leftCounts[i];
                int right = rightCounts[i];
                if (left == right)
                {
                    continue;
                }

                TurnDirection candidateTurn = left > right ? TurnDirection.Left : TurnDirection.Right;
                int turnCount = Math.Max(left, right);
                int oppositeTurnCount = Math.Min(left, right);
                int score = turnCount * 16 - oppositeTurnCount * 8 - straightCounts[i] * 3;
                if (straightCounts[i] == 0)
                {
                    score += 1000;
                }

                if (score > bestScore)
                {
                    bestIndex = i;
                    bestScore = score;
                    bestTurn = candidateTurn;
                    tied = false;
                }
                else if (score == bestScore)
                {
                    tied = true;
                }
            }

            if (bestIndex < 0 || tied)
            {
                selectionDiagnostic = $"centerSelection={(bestIndex < 0 ? "none" : "tie")}";
                return false;
            }

            extraTargetIndex = bestIndex;
            turn = bestTurn;
            selectionDiagnostic = $"centerSelection=target{selectedTargets[bestIndex].LaneIndex}/{bestTurn}/score{bestScore}{(straightCounts[bestIndex] == 0 ? "/turnOnly" : string.Empty)}";
            return true;
        }
    }
}
