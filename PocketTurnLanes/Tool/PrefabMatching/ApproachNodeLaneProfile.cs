using PocketTurnLanes.Tool;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal static class ApproachNodeLaneProfile
    {
        public static bool CountsMatchAtNodes(
            RoadLaneCounts targetCounts,
            bool targetNodeIsStart,
            RoadLaneCounts continuationCounts,
            bool continuationNodeIsStart)
        {
            return GetIncomingCount(targetCounts, targetNodeIsStart) == GetIncomingCount(continuationCounts, continuationNodeIsStart) &&
                   GetOutgoingCount(targetCounts, targetNodeIsStart) == GetOutgoingCount(continuationCounts, continuationNodeIsStart);
        }

        public static int GetIncomingCount(RoadLaneCounts counts, bool nodeIsStart)
        {
            return nodeIsStart ? counts.Backward : counts.Forward;
        }

        public static int GetOutgoingCount(RoadLaneCounts counts, bool nodeIsStart)
        {
            return nodeIsStart ? counts.Forward : counts.Backward;
        }

        public static string FormatCounts(RoadLaneCounts counts, bool nodeIsStart)
        {
            return $"raw={counts} nodeSide={(nodeIsStart ? "start" : "end")} incoming={GetIncomingCount(counts, nodeIsStart)} outgoing={GetOutgoingCount(counts, nodeIsStart)}";
        }

        public static bool LayoutCountsMatchAtNodes(
            DirectionalLaneOffsetProfile targetLayout,
            bool targetNodeIsStart,
            DirectionalLaneOffsetProfile continuationLayout,
            bool continuationNodeIsStart)
        {
            return GetIncomingLayoutCount(targetLayout, targetNodeIsStart) == GetIncomingLayoutCount(continuationLayout, continuationNodeIsStart) &&
                   GetOutgoingLayoutCount(targetLayout, targetNodeIsStart) == GetOutgoingLayoutCount(continuationLayout, continuationNodeIsStart);
        }

        public static int GetIncomingLayoutCount(DirectionalLaneOffsetProfile layout, bool nodeIsStart)
        {
            return nodeIsStart ? layout.BackwardCount : layout.ForwardCount;
        }

        public static int GetOutgoingLayoutCount(DirectionalLaneOffsetProfile layout, bool nodeIsStart)
        {
            return nodeIsStart ? layout.ForwardCount : layout.BackwardCount;
        }

        public static string FormatLayout(DirectionalLaneOffsetProfile layout, bool nodeIsStart)
        {
            return $"raw={layout} nodeSide={(nodeIsStart ? "start" : "end")} incoming={GetIncomingLayoutCount(layout, nodeIsStart)} outgoing={GetOutgoingLayoutCount(layout, nodeIsStart)}";
        }

        public static bool TryMatchContinuationToReplacementTarget(
            ReplacementPrefabMatch prefabMatch,
            RoadLaneProfile continuationProfile,
            bool currentNodeIsStartOnShortEdge,
            bool farNodeIsStartOnContinuation,
            out RoadLaneCounts continuationRoadCounts,
            out string detail)
        {
            continuationRoadCounts = continuationProfile.RoadCounts;
            if (!CountsMatchAtNodes(
                    prefabMatch.TargetCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.RoadCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} road={FormatCounts(continuationProfile.RoadCounts, farNodeIsStartOnContinuation)} expectedTarget={FormatCounts(prefabMatch.TargetCounts, currentNodeIsStartOnShortEdge)} rawTargetRoad={prefabMatch.TargetCounts}";
                return false;
            }

            if (!CountsMatchAtNodes(
                    prefabMatch.TargetTramTrackCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.TramTrackCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} tramTracks={FormatCounts(continuationProfile.TramTrackCounts, farNodeIsStartOnContinuation)} expectedTargetTramTracks={FormatCounts(prefabMatch.TargetTramTrackCounts, currentNodeIsStartOnShortEdge)} rawTargetTramTracks={prefabMatch.TargetTramTrackCounts}";
                return false;
            }

            if (!CountsMatchAtNodes(
                    prefabMatch.TargetIndependentTramCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.IndependentTramCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} independentTram={FormatCounts(continuationProfile.IndependentTramCounts, farNodeIsStartOnContinuation)} expectedTargetIndependentTram={FormatCounts(prefabMatch.TargetIndependentTramCounts, currentNodeIsStartOnShortEdge)} rawTargetIndependentTram={prefabMatch.TargetIndependentTramCounts}";
                return false;
            }

            if (!CountsMatchAtNodes(
                    prefabMatch.TargetPublicTransportTramCounts,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.PublicTransportTramCounts,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} publicTransportTram={FormatCounts(continuationProfile.PublicTransportTramCounts, farNodeIsStartOnContinuation)} expectedTargetPublicTransportTram={FormatCounts(prefabMatch.TargetPublicTransportTramCounts, currentNodeIsStartOnShortEdge)} rawTargetPublicTransportTram={prefabMatch.TargetPublicTransportTramCounts}";
                return false;
            }

            if (!LayoutCountsMatchAtNodes(
                    prefabMatch.TargetBusLaneOffsetProfile,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.BusLaneLayout,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} busLayout={FormatLayout(continuationProfile.BusLaneLayout, farNodeIsStartOnContinuation)} expectedTargetBus={FormatLayout(prefabMatch.TargetBusLaneOffsetProfile, currentNodeIsStartOnShortEdge)} rawTargetBus={prefabMatch.TargetBusLaneLayout}";
                return false;
            }

            if (!LayoutCountsMatchAtNodes(
                    prefabMatch.TargetTramTrackOffsetProfile,
                    currentNodeIsStartOnShortEdge,
                    continuationProfile.TramTrackLayout,
                    farNodeIsStartOnContinuation))
            {
                detail = $"continuationProfile={continuationProfile.Source} tramLayout={FormatLayout(continuationProfile.TramTrackLayout, farNodeIsStartOnContinuation)} expectedTargetTramLayout={FormatLayout(prefabMatch.TargetTramTrackOffsetProfile, currentNodeIsStartOnShortEdge)} rawTargetTramLayout={prefabMatch.TargetTramTrackLayout}";
                return false;
            }

            detail = $"continuationProfile={continuationProfile.Source} road={FormatCounts(continuationProfile.RoadCounts, farNodeIsStartOnContinuation)} expectedTarget={FormatCounts(prefabMatch.TargetCounts, currentNodeIsStartOnShortEdge)} bus={FormatLayout(continuationProfile.BusLaneLayout, farNodeIsStartOnContinuation)} tram={FormatLayout(continuationProfile.TramTrackLayout, farNodeIsStartOnContinuation)}";
            return true;
        }
    }
}
