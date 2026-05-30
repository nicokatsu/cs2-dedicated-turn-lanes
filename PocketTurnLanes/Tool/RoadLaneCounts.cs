namespace PocketTurnLanes.Tool
{
    internal struct RoadLaneCounts
    {
        public int Forward;
        public int Backward;
        public int BicycleOnly;

        public int Total => Forward + Backward;

        public override string ToString()
        {
            return BicycleOnly > 0
                ? $"{Forward}/{Backward};bikeOnly={BicycleOnly}"
                : $"{Forward}/{Backward}";
        }
    }
}
