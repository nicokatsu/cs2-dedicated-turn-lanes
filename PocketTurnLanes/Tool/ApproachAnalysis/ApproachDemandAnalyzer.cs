using System.Collections.Generic;

namespace PocketTurnLanes.Tool.ApproachAnalysis
{
    internal sealed class ApproachDemandAnalysis
    {
        public readonly HashSet<ApproachMovementKey> DedicatedTurnKeys;
        public readonly HashSet<ApproachMovementKey> MixedTurnKeys;
        public readonly List<ApproachMovementKey> UnmetTurnKeys;
        public readonly int DistinctMovementKeyCount;
        public readonly int MixedLaneCount;
        public readonly int AmbiguousKeyCount;
        public readonly bool NeedsLeft;
        public readonly bool NeedsRight;

        public ApproachDemandAnalysis(
            HashSet<ApproachMovementKey> dedicatedTurnKeys,
            HashSet<ApproachMovementKey> mixedTurnKeys,
            List<ApproachMovementKey> unmetTurnKeys,
            int distinctMovementKeyCount,
            int mixedLaneCount,
            int ambiguousKeyCount,
            bool needsLeft,
            bool needsRight)
        {
            DedicatedTurnKeys = dedicatedTurnKeys;
            MixedTurnKeys = mixedTurnKeys;
            UnmetTurnKeys = unmetTurnKeys;
            DistinctMovementKeyCount = distinctMovementKeyCount;
            MixedLaneCount = mixedLaneCount;
            AmbiguousKeyCount = ambiguousKeyCount;
            NeedsLeft = needsLeft;
            NeedsRight = needsRight;
        }

        public bool NeedsPocketLane => NeedsLeft || NeedsRight;
    }

    internal static class ApproachDemandAnalyzer
    {
        public static ApproachDemandAnalysis Analyze(
            Dictionary<int, HashSet<ApproachMovementKey>> laneMovementKeys)
        {
            HashSet<ApproachMovementKey> dedicatedTurnKeys = new HashSet<ApproachMovementKey>();
            HashSet<ApproachMovementKey> mixedTurnKeys = new HashSet<ApproachMovementKey>();
            HashSet<ApproachMovementKey> allKeys = new HashSet<ApproachMovementKey>();
            int mixedLaneCount = 0;
            int ambiguousKeyCount = 0;

            if (laneMovementKeys != null)
            {
                foreach (KeyValuePair<int, HashSet<ApproachMovementKey>> pair in laneMovementKeys)
                {
                    bool laneMixed = pair.Value.Count > 1;
                    if (laneMixed)
                    {
                        mixedLaneCount++;
                    }

                    foreach (ApproachMovementKey key in pair.Value)
                    {
                        allKeys.Add(key);
                        if (key.Movement == ApproachMovement.Ambiguous)
                        {
                            ambiguousKeyCount++;
                        }

                        if (!key.IsTurn)
                        {
                            continue;
                        }

                        if (laneMixed)
                        {
                            mixedTurnKeys.Add(key);
                        }
                        else
                        {
                            dedicatedTurnKeys.Add(key);
                        }
                    }
                }
            }

            bool needsLeft = false;
            bool needsRight = false;
            List<ApproachMovementKey> unmetTurnKeys = new List<ApproachMovementKey>();
            foreach (ApproachMovementKey key in mixedTurnKeys)
            {
                if (dedicatedTurnKeys.Contains(key))
                {
                    continue;
                }

                unmetTurnKeys.Add(key);
                if (key.Movement == ApproachMovement.Left)
                {
                    needsLeft = true;
                }
                else if (key.Movement == ApproachMovement.Right)
                {
                    needsRight = true;
                }
            }

            return new ApproachDemandAnalysis(
                dedicatedTurnKeys,
                mixedTurnKeys,
                unmetTurnKeys,
                allKeys.Count,
                mixedLaneCount,
                ambiguousKeyCount,
                needsLeft,
                needsRight);
        }
    }
}
