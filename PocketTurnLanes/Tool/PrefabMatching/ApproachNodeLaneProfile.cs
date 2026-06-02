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
    }
}
