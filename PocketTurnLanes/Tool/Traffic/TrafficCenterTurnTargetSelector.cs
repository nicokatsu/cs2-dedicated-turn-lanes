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

        public static bool TryFindTargetByEndpointLaneIndexes(
            IReadOnlyList<LaneEndpoint> targets,
            LaneEndpoint endpoint,
            out int targetListIndex,
            out string matchDetail)
        {
            if (TryFindTargetByCenterLaneIndex(targets, endpoint.LaneIndex, out targetListIndex))
            {
                matchDetail = $"lane={endpoint.LaneIndex}";
                return true;
            }

            if (endpoint.OppositeLaneIndex >= 0 &&
                TryFindTargetByCenterLaneIndex(targets, endpoint.OppositeLaneIndex, out targetListIndex))
            {
                matchDetail = $"oppositeLane={endpoint.OppositeLaneIndex}";
                return true;
            }

            matchDetail = $"laneMissing lane={endpoint.LaneIndex} oppositeLane={endpoint.OppositeLaneIndex}";
            return false;
        }

        public static void AddTurnCount(
            TurnDirection connectorTurn,
            int targetListIndex,
            int[] leftCounts,
            int[] rightCounts,
            int[] straightCounts)
        {
            if (targetListIndex < 0 ||
                leftCounts == null ||
                rightCounts == null ||
                straightCounts == null ||
                targetListIndex >= leftCounts.Length ||
                targetListIndex >= rightCounts.Length ||
                targetListIndex >= straightCounts.Length)
            {
                return;
            }

            if (connectorTurn == TurnDirection.Left)
            {
                leftCounts[targetListIndex]++;
            }
            else if (connectorTurn == TurnDirection.Right)
            {
                rightCounts[targetListIndex]++;
            }
            else
            {
                straightCounts[targetListIndex]++;
            }
        }

        public static string FormatTargetTurnCounts(
            IReadOnlyList<LaneEndpoint> targets,
            IReadOnlyList<int> leftCounts,
            IReadOnlyList<int> rightCounts,
            IReadOnlyList<int> straightCounts)
        {
            if (targets == null ||
                leftCounts == null ||
                rightCounts == null ||
                straightCounts == null ||
                targets.Count == 0)
            {
                return "<none>";
            }

            List<string> values = new List<string>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                int left = i < leftCounts.Count ? leftCounts[i] : 0;
                int right = i < rightCounts.Count ? rightCounts[i] : 0;
                int straight = i < straightCounts.Count ? straightCounts[i] : 0;
                values.Add($"target{targets[i].LaneIndex}/center{targets[i].OppositeLaneIndex}:L{left} R{right} S{straight}");
            }

            return string.Join("|", values);
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
