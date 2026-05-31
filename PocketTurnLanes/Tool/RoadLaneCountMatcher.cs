using Game.Prefabs;

namespace PocketTurnLanes.Tool
{
    internal static class RoadLaneCountMatcher
    {
        public static void CountRoadLane(LaneFlags flags, ref RoadLaneCounts counts)
        {
            if ((flags & (LaneFlags.Master | LaneFlags.Road)) != LaneFlags.Road)
            {
                return;
            }

            if ((flags & LaneFlags.BicyclesOnly) != (LaneFlags)0)
            {
                counts.BicycleOnly++;
                return;
            }

            if ((flags & LaneFlags.Invert) != (LaneFlags)0)
            {
                counts.Backward++;
            }
            else
            {
                counts.Forward++;
            }
        }

        public static bool TryMatch(RoadLaneCounts candidateCounts, RoadLaneCounts desiredCounts, out bool invert)
        {
            if (candidateCounts.Forward == desiredCounts.Forward &&
                candidateCounts.Backward == desiredCounts.Backward)
            {
                invert = false;
                return true;
            }

            if (candidateCounts.Forward == desiredCounts.Backward &&
                candidateCounts.Backward == desiredCounts.Forward)
            {
                invert = true;
                return true;
            }

            invert = false;
            return false;
        }

        public static bool CountsEqual(RoadLaneCounts first, RoadLaneCounts second)
        {
            return first.Forward == second.Forward &&
                   first.Backward == second.Backward;
        }

        public static bool CountsMatchForOrientation(
            RoadLaneCounts candidateCounts,
            RoadLaneCounts desiredCounts,
            bool invert)
        {
            return invert
                ? CountsEqual(candidateCounts, desiredCounts.Swapped())
                : CountsEqual(candidateCounts, desiredCounts);
        }
    }
}
