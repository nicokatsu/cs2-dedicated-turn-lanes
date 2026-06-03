using Game.Prefabs;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal static class ReplacementPrefabScoring
    {
        private const int DlcSourceNonDlcCandidatePenalty = 5000;
        private const int TramUpgradeFallbackPenalty = 20000;
        private const int IndependentTramTargetPreference = 1000;
        private const int PublicTransportTramTargetPenalty = 500;
        private const int OtherTramTargetPenalty = 1000;
        private const int MissingTramTargetPenalty = 50000;
        private const float PublicTransportLayoutOffsetScoreScale = 100f;
        private const int PublicTransportLayoutMissingDirectionPenalty = 2500;
        private const int PublicTransportLayoutCountMismatchPenalty = 750;

        public static int GetReplacementLayoutScore(
            RoadLaneProfile sourceProfile,
            RoadLaneProfile candidateProfile,
            bool invert,
            out int tramLayoutScore,
            out int busLayoutScore,
            out string detail,
            out DirectionalLaneOffsetProfile orientedCandidateTramLayout,
            out DirectionalLaneOffsetProfile orientedCandidateBusLayout)
        {
            orientedCandidateTramLayout = candidateProfile.TramTrackLayout.Oriented(invert);
            orientedCandidateBusLayout = candidateProfile.BusLaneLayout.Oriented(invert);
            tramLayoutScore = GetDirectionalLayoutOffsetScore(
                sourceProfile.TramTrackLayout,
                orientedCandidateTramLayout);
            busLayoutScore = GetDirectionalLayoutOffsetScore(
                sourceProfile.BusLaneLayout,
                orientedCandidateBusLayout);
            detail = $"targetLayoutSource={candidateProfile.Source} sourceTram={sourceProfile.TramTrackLayout} targetTram={orientedCandidateTramLayout} sourceBus={sourceProfile.BusLaneLayout} targetBus={orientedCandidateBusLayout}";
            return tramLayoutScore + busLayoutScore;
        }

        public static int GetTramTargetScoreAdjustment(
            bool sourceHasTramTracks,
            RoadLaneCounts requiredTramCounts,
            bool targetUsesTramUpgradeFallback,
            bool targetHasTramTrackMatch,
            bool targetHasIndependentTram,
            RoadLaneCounts targetIndependentTramCounts,
            bool targetHasPublicTransportTram,
            RoadLaneCounts targetPublicTransportTramCounts,
            bool invert)
        {
            int score = targetUsesTramUpgradeFallback
                ? TramUpgradeFallbackPenalty
                : 0;
            if (!sourceHasTramTracks)
            {
                return score;
            }

            if (!targetHasTramTrackMatch)
            {
                return score + MissingTramTargetPenalty;
            }

            if (targetHasIndependentTram &&
                RoadLaneCountMatcher.CountsMatchForOrientation(targetIndependentTramCounts, requiredTramCounts, invert))
            {
                return score - IndependentTramTargetPreference;
            }

            if (targetHasPublicTransportTram &&
                RoadLaneCountMatcher.CountsMatchForOrientation(targetPublicTransportTramCounts, requiredTramCounts, invert))
            {
                return score + PublicTransportTramTargetPenalty;
            }

            return score + OtherTramTargetPenalty;
        }

        public static int GetDirectionalLayoutOffsetScore(
            DirectionalLaneOffsetProfile source,
            DirectionalLaneOffsetProfile candidate)
        {
            if (!source.HasAny)
            {
                return 0;
            }

            if (!candidate.HasAny)
            {
                return PublicTransportLayoutMissingDirectionPenalty;
            }

            return GetDirectionLayoutOffsetScore(
                       source.ForwardCount,
                       source.ForwardOffsetSum,
                       candidate.ForwardCount,
                       candidate.ForwardOffsetSum) +
                   GetDirectionLayoutOffsetScore(
                       source.BackwardCount,
                       source.BackwardOffsetSum,
                       candidate.BackwardCount,
                       candidate.BackwardOffsetSum);
        }

        public static int GetReplacementPrefabScore(
            RoadData sourceRoadData,
            NetData sourceNetData,
            NetGeometryData sourceGeometry,
            RoadData candidateRoadData,
            NetData candidateNetData,
            NetGeometryData candidateGeometry,
            bool invert,
            bool sourceIsDlc,
            bool candidateIsDlc)
        {
            int score = invert ? 1000 : 0;
            if (sourceIsDlc && !candidateIsDlc)
            {
                score += DlcSourceNonDlcCandidatePenalty;
            }

            RoadFlags comparableRoadFlags = RoadFlags.EnableZoning;
            if (sourceNetData.m_RequiredLayers != candidateNetData.m_RequiredLayers)
            {
                score += 200;
            }

            if ((sourceRoadData.m_Flags & comparableRoadFlags) != (candidateRoadData.m_Flags & comparableRoadFlags))
            {
                score += 100;
            }

            score += (int)math.round(math.abs(sourceRoadData.m_SpeedLimit - candidateRoadData.m_SpeedLimit) * 10f);
            score += (int)math.round(math.abs(sourceGeometry.m_DefaultWidth - candidateGeometry.m_DefaultWidth) * 100f);
            return score;
        }

        private static int GetDirectionLayoutOffsetScore(
            int sourceCount,
            float sourceOffsetSum,
            int candidateCount,
            float candidateOffsetSum)
        {
            if (sourceCount == 0)
            {
                return candidateCount == 0
                    ? 0
                    : PublicTransportLayoutCountMismatchPenalty * candidateCount;
            }

            if (candidateCount == 0)
            {
                return PublicTransportLayoutMissingDirectionPenalty;
            }

            float sourceAverage = sourceOffsetSum / sourceCount;
            float candidateAverage = candidateOffsetSum / candidateCount;
            int offsetScore = (int)math.round(math.abs(sourceAverage - candidateAverage) * PublicTransportLayoutOffsetScoreScale);
            int countScore = math.abs(sourceCount - candidateCount) * PublicTransportLayoutCountMismatchPenalty;
            return offsetScore + countScore;
        }
    }
}
