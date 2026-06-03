using Game.Prefabs;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal struct RoadLaneCounts
    {
        public int Forward;
        public int Backward;
        public int BicycleOnly;

        public int Total => Forward + Backward;

        public bool IsEmpty => Forward == 0 && Backward == 0;

        public RoadLaneCounts Swapped()
        {
            return new RoadLaneCounts
            {
                Forward = Backward,
                Backward = Forward,
                BicycleOnly = BicycleOnly
            };
        }

        public static RoadLaneCounts Add(RoadLaneCounts first, RoadLaneCounts second)
        {
            return new RoadLaneCounts
            {
                Forward = first.Forward + second.Forward,
                Backward = first.Backward + second.Backward,
                BicycleOnly = first.BicycleOnly + second.BicycleOnly
            };
        }

        public override string ToString()
        {
            return BicycleOnly > 0
                ? $"{Forward}/{Backward};bikeOnly={BicycleOnly}"
                : $"{Forward}/{Backward}";
        }
    }

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
