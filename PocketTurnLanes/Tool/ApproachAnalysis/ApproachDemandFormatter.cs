using System;
using System.Collections.Generic;
using System.Text;
using Unity.Entities;

namespace PocketTurnLanes.Tool.ApproachAnalysis
{
    internal static class ApproachDemandFormatter
    {
        public static string FormatLaneUsages(Dictionary<int, ApproachLaneUsage> laneUsages)
        {
            if (laneUsages.Count == 0)
            {
                return "<none>";
            }

            List<ApproachLaneUsage> usages = new List<ApproachLaneUsage>(laneUsages.Values);
            usages.Sort((a, b) => a.LaneIndex.CompareTo(b.LaneIndex));

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < usages.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                ApproachLaneUsage usage = usages[i];
                builder.Append("lane");
                builder.Append(usage.LaneIndex);
                builder.Append(":S");
                builder.Append(usage.Straight);
                builder.Append("/L");
                builder.Append(usage.Left);
                builder.Append("/R");
                builder.Append(usage.Right);
                builder.Append("/A");
                builder.Append(usage.Ambiguous);
            }

            return builder.ToString();
        }

        public static string FormatMovementKeys(
            Dictionary<int, HashSet<ApproachMovementKey>> laneMovementKeys,
            Func<Entity, string> formatEntity)
        {
            if (laneMovementKeys.Count == 0)
            {
                return "<none>";
            }

            List<int> laneIndexes = new List<int>(laneMovementKeys.Keys);
            laneIndexes.Sort();

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < laneIndexes.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                int laneIndex = laneIndexes[i];
                builder.Append("lane");
                builder.Append(laneIndex);
                builder.Append(":");
                builder.Append(FormatMovementKeySet(laneMovementKeys[laneIndex], formatEntity));
            }

            return builder.ToString();
        }

        public static string FormatMovementKeySet(
            IEnumerable<ApproachMovementKey> keys,
            Func<Entity, string> formatEntity)
        {
            if (keys == null)
            {
                return "<none>";
            }

            List<ApproachMovementKey> sortedKeys = new List<ApproachMovementKey>(keys);
            if (sortedKeys.Count == 0)
            {
                return "<none>";
            }

            sortedKeys.Sort((a, b) =>
            {
                int movementCompare = a.Movement.CompareTo(b.Movement);
                if (movementCompare != 0)
                {
                    return movementCompare;
                }

                int indexCompare = a.TargetEdge.Index.CompareTo(b.TargetEdge.Index);
                return indexCompare != 0 ? indexCompare : a.TargetEdge.Version.CompareTo(b.TargetEdge.Version);
            });

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < sortedKeys.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("|");
                }

                ApproachMovementKey key = sortedKeys[i];
                builder.Append(key.Movement);
                builder.Append("->");
                builder.Append(formatEntity(key.TargetEdge));
            }

            return builder.ToString();
        }
    }
}
