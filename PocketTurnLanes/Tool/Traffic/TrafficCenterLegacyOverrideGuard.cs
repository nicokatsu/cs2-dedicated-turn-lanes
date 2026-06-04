using System.Collections.Generic;
using Game.Pathfind;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficCenterLegacyOverrideGuard
    {
        public static bool LooksLikeLegacyCenterOverride(
            IReadOnlyList<TrafficGeneratedSnapshot> generatedSnapshots,
            SourceLaneKey sourceKey)
        {
            if (generatedSnapshots == null ||
                generatedSnapshots.Count <= 0 ||
                generatedSnapshots.Count > 4)
            {
                return false;
            }

            for (int i = 0; i < generatedSnapshots.Count; i++)
            {
                TrafficGeneratedSnapshot generated = generatedSnapshots[i];
                PathMethod method = TrafficPathMethods.SanitizeCenterTrafficPathMethod(generated.Method);
                if (generated.SourceEdge != sourceKey.Edge ||
                    generated.SourceLaneIndex != sourceKey.LaneIndex ||
                    (method & ~PathMethod.Road) != 0 ||
                    generated.IsUnsafe)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
