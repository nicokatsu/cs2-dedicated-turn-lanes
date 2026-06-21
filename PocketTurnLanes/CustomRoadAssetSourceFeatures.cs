using System;

namespace PocketTurnLanes
{
    [Flags]
    internal enum CustomRoadAssetSourceFeatures
    {
        None = 0,
        Tram = 1,
        PublicTransport = 2,
        Reverse = 4
    }

    internal readonly struct CustomRoadAssetMatchRule
    {
        public CustomRoadAssetMatchRule(
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures,
            string targetPrefabName)
        {
            SourcePrefabName = sourcePrefabName;
            SourceFeatures = sourceFeatures;
            TargetPrefabName = targetPrefabName;
        }

        public string SourcePrefabName { get; }

        public CustomRoadAssetSourceFeatures SourceFeatures { get; }

        public string TargetPrefabName { get; }
    }

    internal static class CustomRoadAssetSourceFeatureUtility
    {
        private const CustomRoadAssetSourceFeatures SupportedFeatures =
            CustomRoadAssetSourceFeatures.Tram |
            CustomRoadAssetSourceFeatures.PublicTransport |
            CustomRoadAssetSourceFeatures.Reverse;

        public static CustomRoadAssetSourceFeatures Normalize(CustomRoadAssetSourceFeatures features)
        {
            return features & SupportedFeatures;
        }

        public static bool TryParse(string value, out CustomRoadAssetSourceFeatures features)
        {
            features = CustomRoadAssetSourceFeatures.None;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (int.TryParse(value.Trim(), out int numeric))
            {
                features = Normalize((CustomRoadAssetSourceFeatures)numeric);
                return true;
            }

            if (Enum.TryParse(value.Trim(), true, out CustomRoadAssetSourceFeatures parsed))
            {
                features = Normalize(parsed);
                return true;
            }

            return false;
        }

        public static string Format(CustomRoadAssetSourceFeatures features)
        {
            features = Normalize(features);
            return features == CustomRoadAssetSourceFeatures.None
                ? "none"
                : features.ToString();
        }
    }
}
