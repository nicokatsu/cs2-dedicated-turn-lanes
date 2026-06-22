namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal static class CustomRoadAssetSourceFeatureClassifier
    {
        private const CustomRoadAssetSourceFeatures TransportFeatures =
            CustomRoadAssetSourceFeatures.Tram |
            CustomRoadAssetSourceFeatures.PublicTransport;

        public static CustomRoadAssetSourceFeatures GetMandatoryFeatures(RoadLaneProfile profile)
        {
            return CustomRoadAssetSourceFeatureUtility.Normalize(profile.MandatorySourceFeatures) &
                   TransportFeatures;
        }

        public static CustomRoadAssetSourceFeatures GetAvailableFeatures(
            RoadLaneProfile profile,
            CustomRoadAssetSourceFeatures nativeUpgradeFeatures,
            CustomRoadAssetSourceFeatures roadBuilderFeatures)
        {
            return CustomRoadAssetSourceFeatureUtility.Normalize(
                    GetMandatoryFeatures(profile) |
                    nativeUpgradeFeatures |
                    roadBuilderFeatures) &
                TransportFeatures;
        }

        public static CustomRoadAssetSourceFeatures GetRuntimeFeatures(
            RoadLaneProfile profile,
            RoadLaneCounts originalCounts,
            bool nodeIsStart)
        {
            CustomRoadAssetSourceFeatures features = CustomRoadAssetSourceFeatures.None;
            if (!profile.TramTrackCounts.IsEmpty)
            {
                features |= CustomRoadAssetSourceFeatures.Tram;
            }

            if (HasPublicTransport(profile))
            {
                features |= CustomRoadAssetSourceFeatures.PublicTransport;
            }

            if (IsReverseAsymmetricSourceSide(originalCounts, nodeIsStart))
            {
                features |= CustomRoadAssetSourceFeatures.Reverse;
            }

            return CustomRoadAssetSourceFeatureUtility.Normalize(features);
        }

        public static bool HasPublicTransport(RoadLaneProfile profile)
        {
            return profile.BusLaneLayout.HasAny ||
                   profile.DedicatedPublicTransportLaneLayout.HasAny ||
                   !profile.PublicTransportTramCounts.IsEmpty;
        }

        public static bool IsReverseAsymmetricSourceSide(
            RoadLaneCounts originalCounts,
            bool nodeIsStart)
        {
            return originalCounts.IsAsymmetric &&
                   originalCounts.GetIncomingAtNode(nodeIsStart) < originalCounts.GetOutgoingAtNode(nodeIsStart);
        }
    }
}
