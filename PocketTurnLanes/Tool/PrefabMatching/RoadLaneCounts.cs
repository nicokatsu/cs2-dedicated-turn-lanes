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
}
