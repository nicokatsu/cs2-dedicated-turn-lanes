using Colossal.Entities;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Entities;
using static PocketTurnLanes.Tool.PrefabMatching.RoadLaneCountMatcher;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal sealed class ReplacementRoadUpgradeMatcher
    {
        private const int PublicTransportTramUpgradeLaneTypePenalty = 50;
        private const int OtherTramUpgradeLaneTypePenalty = 100;

        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;
        private readonly RoadLaneProfileBuilder m_RoadLaneProfileBuilder;

        internal ReplacementRoadUpgradeMatcher(
            EntityManager entityManager,
            PrefabSystem prefabSystem,
            RoadLaneProfileBuilder roadLaneProfileBuilder)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
            m_RoadLaneProfileBuilder = roadLaneProfileBuilder;
        }

        private EntityManager EntityManager => m_EntityManager;

        internal string BuildTramUpgradeRejectSample(
            Entity candidatePrefab,
            RoadLaneProfile candidateProfile,
            bool invert,
            string tramUpgradeDetail)
        {
            return $"candidate={GetPrefabNameFromPrefab(candidatePrefab)} candidateRoad={candidateProfile.RoadCounts} candidateTramTracks={candidateProfile.TramTrackCounts} invert={invert} {tramUpgradeDetail}";
        }

        internal bool TryFindMatchingTramUpgrade(
            Entity prefabEntity,
            RoadLaneProfile candidateDefaultProfile,
            RoadLaneProfile sourceProfile,
            RoadLaneCounts desiredRoadCounts,
            RoadLaneCounts desiredEffectiveCounts,
            RoadLaneCounts requiredTramCounts,
            CompositionFlags sourceTramUpgradeFlags,
            bool invertTarget,
            out Upgraded targetUpgrade,
            out RoadLaneProfile targetProfile,
            out string detail)
        {
            targetUpgrade = default;
            targetProfile = RoadLaneProfileBuilder.CreateEmptyRoadLaneProfile("TramUpgrade:missing");
            detail = "tramUpgrade=not-scanned";

            if (prefabEntity == Entity.Null || requiredTramCounts.IsEmpty)
            {
                detail = $"tramUpgrade=skipped prefab={GetPrefabNameFromPrefab(prefabEntity)} requiredTram={requiredTramCounts}";
                return false;
            }

            if (!EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<NetGeometryComposition> compositions))
            {
                detail = $"tramUpgrade=missing-compositions prefab={GetPrefabNameFromPrefab(prefabEntity)}";
                return false;
            }

            RoadLaneCounts orientedDesiredRoadCounts = invertTarget ? desiredRoadCounts.Swapped() : desiredRoadCounts;
            RoadLaneCounts orientedDesiredEffectiveCounts = invertTarget ? desiredEffectiveCounts.Swapped() : desiredEffectiveCounts;
            RoadLaneCounts orientedRequiredTramCounts = invertTarget ? requiredTramCounts.Swapped() : requiredTramCounts;
            int scanned = 0;
            int trackMasks = 0;
            int laneProfiles = 0;
            int effectiveMatches = 0;
            int tramMatches = 0;
            int independentTramMatches = 0;
            int publicTransportTramMatches = 0;
            int roadPreferredMatches = 0;
            int bestScore = int.MaxValue;
            CompositionFlags bestUpgradeFlags = default;
            RoadLaneProfile bestProfile = default;

            for (int i = 0; i < compositions.Length; i++)
            {
                scanned++;
                NetGeometryComposition composition = compositions[i];
                CompositionFlags upgradeFlags = GetTramTrackUpgradeFlags(composition.m_Mask);
                if (upgradeFlags == default(CompositionFlags))
                {
                    continue;
                }

                trackMasks++;
                if (!m_RoadLaneProfileBuilder.TryGetCompositionRoadLaneProfile(composition.m_Composition, out RoadLaneProfile profile))
                {
                    continue;
                }

                laneProfiles++;
                RoadLaneCounts effectiveCounts = RoadLaneCounts.Add(profile.RoadCounts, profile.IndependentTramCounts);
                if (!CountsEqual(effectiveCounts, orientedDesiredEffectiveCounts))
                {
                    continue;
                }

                effectiveMatches++;
                if (!CountsEqual(profile.TramTrackCounts, orientedRequiredTramCounts))
                {
                    continue;
                }

                tramMatches++;
                bool independentTramMatch = CountsEqual(profile.IndependentTramCounts, orientedRequiredTramCounts);
                bool publicTransportTramMatch = CountsEqual(profile.PublicTransportTramCounts, orientedRequiredTramCounts);
                if (independentTramMatch)
                {
                    independentTramMatches++;
                }

                if (publicTransportTramMatch)
                {
                    publicTransportTramMatches++;
                }

                bool preferredRoadCounts = CountsEqual(profile.RoadCounts, orientedDesiredRoadCounts);
                if (preferredRoadCounts)
                {
                    roadPreferredMatches++;
                }

                int laneTypeScore = independentTramMatch
                    ? 0
                    : publicTransportTramMatch
                        ? PublicTransportTramUpgradeLaneTypePenalty
                        : OtherTramUpgradeLaneTypePenalty;
                int score = CountTramTrackUpgradeFlags(upgradeFlags) + (preferredRoadCounts ? 0 : 100) + laneTypeScore;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestUpgradeFlags = upgradeFlags;
                    bestProfile = profile;
                }
            }

            if (bestUpgradeFlags == default(CompositionFlags))
            {
                if (trackMasks == 0 &&
                    sourceTramUpgradeFlags != default(CompositionFlags) &&
                    TryBuildSourceTramUpgradeFallbackProfile(
                        candidateDefaultProfile,
                        sourceProfile,
                        orientedDesiredEffectiveCounts,
                        orientedRequiredTramCounts,
                        sourceTramUpgradeFlags,
                        invertTarget,
                        out targetUpgrade,
                        out targetProfile,
                        out string sourceUpgradeDetail))
                {
                    detail = $"tramUpgrade=source-flags-fallback prefab={GetPrefabNameFromPrefab(prefabEntity)} scannedCompositions={scanned} trackMasks={trackMasks} laneProfiles={laneProfiles} effectiveMatches={effectiveMatches} tramMatches={tramMatches} independentTramMatches={independentTramMatches} publicTransportTramMatches={publicTransportTramMatches} roadPreferredMatches={roadPreferredMatches} {sourceUpgradeDetail}";
                    return true;
                }

                detail = $"tramUpgrade=no-match prefab={GetPrefabNameFromPrefab(prefabEntity)} scannedCompositions={scanned} trackMasks={trackMasks} laneProfiles={laneProfiles} effectiveMatches={effectiveMatches} tramMatches={tramMatches} independentTramMatches={independentTramMatches} publicTransportTramMatches={publicTransportTramMatches} roadPreferredMatches={roadPreferredMatches} desiredRoad={desiredRoadCounts} desiredEffective={desiredEffectiveCounts} requiredTram={requiredTramCounts} orientedDesiredRoad={orientedDesiredRoadCounts} orientedDesiredEffective={orientedDesiredEffectiveCounts} orientedRequiredTram={orientedRequiredTramCounts} invertTarget={invertTarget}";
                return false;
            }

            CompositionFlags targetFlags = invertTarget
                ? NetCompositionHelpers.InvertCompositionFlags(bestUpgradeFlags)
                : bestUpgradeFlags;
            targetUpgrade = new Upgraded { m_Flags = targetFlags };
            targetProfile = bestProfile;
            targetProfile.Source = $"TramUpgrade:{targetFlags}";
            RoadLaneCounts bestEffectiveCounts = RoadLaneCounts.Add(bestProfile.RoadCounts, bestProfile.IndependentTramCounts);
            detail = $"tramUpgrade=matched prefab={GetPrefabNameFromPrefab(prefabEntity)} upgradeFlags={targetFlags} rawUpgradeFlags={bestUpgradeFlags} upgradedRoad={bestProfile.RoadCounts} upgradedEffective={bestEffectiveCounts} upgradedIndependentTram={bestProfile.IndependentTramCounts} upgradedPublicTransportTram={bestProfile.PublicTransportTramCounts} upgradedTramTracks={bestProfile.TramTrackCounts} upgradedTramTrackLayout={bestProfile.TramTrackLayout} upgradedTramDetail={bestProfile.TramTrackDetail} upgradedPublicTransportTramDetail={bestProfile.PublicTransportTramDetail} upgradedBusLayout={bestProfile.BusLaneLayout} upgradedBusDetail={bestProfile.BusLaneDetail} scannedCompositions={scanned} trackMasks={trackMasks} laneProfiles={laneProfiles} effectiveMatches={effectiveMatches} tramMatches={tramMatches} independentTramMatches={independentTramMatches} publicTransportTramMatches={publicTransportTramMatches} roadPreferredMatches={roadPreferredMatches} invertTarget={invertTarget}";
            return true;
        }

        internal bool TryFindMatchingBusUpgrade(
            Entity prefabEntity,
            RoadLaneProfile sourceProfile,
            RoadLaneCounts desiredRoadCounts,
            out bool invertTarget,
            out Upgraded targetUpgrade,
            out RoadLaneProfile targetProfile,
            out string detail)
        {
            invertTarget = false;
            targetUpgrade = default;
            targetProfile = RoadLaneProfileBuilder.CreateEmptyRoadLaneProfile("BusUpgrade:missing");
            detail = "busUpgrade=not-scanned";

            if (prefabEntity == Entity.Null || !sourceProfile.BusLaneLayout.HasAny)
            {
                detail = $"busUpgrade=skipped prefab={GetPrefabNameFromPrefab(prefabEntity)} sourceBusLayout={sourceProfile.BusLaneLayout}";
                return false;
            }

            if (!EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<NetGeometryComposition> compositions))
            {
                detail = $"busUpgrade=missing-compositions prefab={GetPrefabNameFromPrefab(prefabEntity)}";
                return false;
            }

            int scanned = 0;
            int nonDefaultMasks = 0;
            int laneProfiles = 0;
            int calculatedProfiles = 0;
            int directProfiles = 0;
            int parkingProfiles = 0;
            int busProfiles = 0;
            int publicTransportTramProfiles = 0;
            int laneMatches = 0;
            int bestScore = int.MaxValue;
            CompositionFlags bestUpgradeFlags = default;
            RoadLaneProfile bestProfile = default;
            bool bestInvert = false;

            for (int i = 0; i < compositions.Length; i++)
            {
                scanned++;
                NetGeometryComposition composition = compositions[i];
                if (composition.m_Mask == default(CompositionFlags))
                {
                    continue;
                }

                nonDefaultMasks++;
                bool calculatedProfile = m_RoadLaneProfileBuilder.TryCalculateRoadLaneProfile(
                    prefabEntity,
                    composition.m_Mask,
                    $"NetGeometrySection:upgrade:{composition.m_Mask}",
                    out RoadLaneProfile calculatedRoadProfile);
                bool directProfile = m_RoadLaneProfileBuilder.TryGetCompositionRoadLaneProfile(composition.m_Composition, out RoadLaneProfile directRoadProfile);
                if (directProfile)
                {
                    directRoadProfile.Source = $"NetGeometryComposition:upgrade:{composition.m_Mask}";
                }

                if (!calculatedProfile && !directProfile)
                {
                    continue;
                }

                if (calculatedProfile)
                {
                    laneProfiles++;
                    calculatedProfiles++;
                    TryAcceptBusUpgradeProfile(
                        calculatedRoadProfile,
                        desiredRoadCounts,
                        sourceProfile,
                        composition.m_Mask,
                        ref parkingProfiles,
                        ref busProfiles,
                        ref publicTransportTramProfiles,
                        ref laneMatches,
                        ref bestScore,
                        ref bestUpgradeFlags,
                        ref bestProfile,
                        ref bestInvert);
                }

                if (directProfile)
                {
                    laneProfiles++;
                    directProfiles++;
                    TryAcceptBusUpgradeProfile(
                        directRoadProfile,
                        desiredRoadCounts,
                        sourceProfile,
                        composition.m_Mask,
                        ref parkingProfiles,
                        ref busProfiles,
                        ref publicTransportTramProfiles,
                        ref laneMatches,
                        ref bestScore,
                        ref bestUpgradeFlags,
                        ref bestProfile,
                        ref bestInvert);
                }
            }

            if (bestUpgradeFlags == default(CompositionFlags))
            {
                detail = $"busUpgrade=no-match prefab={GetPrefabNameFromPrefab(prefabEntity)} scannedCompositions={scanned} nonDefaultMasks={nonDefaultMasks} laneProfiles={laneProfiles} calculatedProfiles={calculatedProfiles} directProfiles={directProfiles} parkingProfiles={parkingProfiles} busProfiles={busProfiles} publicTransportTramProfiles={publicTransportTramProfiles} laneMatches={laneMatches} desiredRoad={desiredRoadCounts} sourceBusLayout={sourceProfile.BusLaneLayout}";
                return false;
            }

            invertTarget = bestInvert;
            CompositionFlags targetFlags = invertTarget
                ? NetCompositionHelpers.InvertCompositionFlags(bestUpgradeFlags)
                : bestUpgradeFlags;
            targetUpgrade = new Upgraded { m_Flags = targetFlags };
            targetProfile = bestProfile;
            targetProfile.Source = $"BusUpgrade:{targetFlags}";
            detail = $"busUpgrade=matched prefab={GetPrefabNameFromPrefab(prefabEntity)} upgradeFlags={targetFlags} rawUpgradeFlags={bestUpgradeFlags} upgradedRoad={bestProfile.RoadCounts} upgradedBusLayout={bestProfile.BusLaneLayout} upgradedBusDetail={bestProfile.BusLaneDetail} upgradedTramTracks={bestProfile.TramTrackCounts} upgradedPublicTransportTram={bestProfile.PublicTransportTramCounts} scannedCompositions={scanned} nonDefaultMasks={nonDefaultMasks} laneProfiles={laneProfiles} calculatedProfiles={calculatedProfiles} directProfiles={directProfiles} parkingProfiles={parkingProfiles} busProfiles={busProfiles} publicTransportTramProfiles={publicTransportTramProfiles} laneMatches={laneMatches} layoutScore={bestScore} invertTarget={invertTarget}";
            return true;
        }

        private bool TryAcceptBusUpgradeProfile(
            RoadLaneProfile profile,
            RoadLaneCounts desiredRoadCounts,
            RoadLaneProfile sourceProfile,
            CompositionFlags compositionMask,
            ref int parkingProfiles,
            ref int busProfiles,
            ref int publicTransportTramProfiles,
            ref int laneMatches,
            ref int bestScore,
            ref CompositionFlags bestUpgradeFlags,
            ref RoadLaneProfile bestProfile,
            ref bool bestInvert)
        {
            if (profile.HasMarkedParking)
            {
                parkingProfiles++;
                return false;
            }

            if (!profile.BusLaneLayout.HasAny)
            {
                return false;
            }

            busProfiles++;
            if (!profile.TramTrackCounts.IsEmpty)
            {
                publicTransportTramProfiles++;
            }

            if (!RoadLaneCountMatcher.TryMatch(profile.RoadCounts, desiredRoadCounts, out bool candidateInvert))
            {
                return false;
            }

            laneMatches++;
            DirectionalLaneOffsetProfile orientedBusLayout = profile.BusLaneLayout.Oriented(candidateInvert);
            int score = ReplacementPrefabScoring.GetDirectionalLayoutOffsetScore(sourceProfile.BusLaneLayout, orientedBusLayout) +
                        (candidateInvert ? 1000 : 0);
            if (score >= bestScore)
            {
                return false;
            }

            bestScore = score;
            bestUpgradeFlags = compositionMask;
            bestProfile = profile;
            bestInvert = candidateInvert;
            return true;
        }

        private static bool TryBuildSourceTramUpgradeFallbackProfile(
            RoadLaneProfile candidateDefaultProfile,
            RoadLaneProfile sourceProfile,
            RoadLaneCounts orientedDesiredEffectiveCounts,
            RoadLaneCounts orientedRequiredTramCounts,
            CompositionFlags sourceTramUpgradeFlags,
            bool invertTarget,
            out Upgraded targetUpgrade,
            out RoadLaneProfile targetProfile,
            out string detail)
        {
            targetUpgrade = default;
            targetProfile = candidateDefaultProfile;
            detail = "sourceUpgradeFallback=not-used";

            if (!CountsEqual(candidateDefaultProfile.RoadCounts, orientedDesiredEffectiveCounts) ||
                !CountsEqual(sourceProfile.TramTrackCounts, sourceProfile.IndependentTramCounts) ||
                !CountsEqual(orientedRequiredTramCounts, invertTarget ? sourceProfile.IndependentTramCounts.Swapped() : sourceProfile.IndependentTramCounts))
            {
                detail = $"sourceUpgradeFallback=rejected candidateRoad={candidateDefaultProfile.RoadCounts} orientedDesiredEffective={orientedDesiredEffectiveCounts} sourceIndependentTram={sourceProfile.IndependentTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} orientedRequiredTram={orientedRequiredTramCounts} invertTarget={invertTarget}";
                return false;
            }

            CompositionFlags targetFlags = invertTarget
                ? NetCompositionHelpers.InvertCompositionFlags(sourceTramUpgradeFlags)
                : sourceTramUpgradeFlags;
            targetUpgrade = new Upgraded { m_Flags = targetFlags };
            targetProfile.Source = $"SourceTramUpgrade:{targetFlags}";
            targetProfile.IndependentTramCounts = orientedRequiredTramCounts;
            targetProfile.TramTrackCounts = orientedRequiredTramCounts;
            targetProfile.IndependentTramLayout = sourceProfile.IndependentTramLayout.Oriented(invertTarget);
            targetProfile.TramTrackLayout = sourceProfile.TramTrackLayout.Oriented(invertTarget);
            targetProfile.IndependentTramDetail = sourceProfile.IndependentTramDetail;
            targetProfile.TramTrackDetail = sourceProfile.TramTrackDetail;
            detail = $"sourceUpgradeFallback=matched sourceTrackFlags={sourceTramUpgradeFlags} targetFlags={targetFlags} candidateRoad={candidateDefaultProfile.RoadCounts} orientedDesiredEffective={orientedDesiredEffectiveCounts} sourceTramTracks={sourceProfile.TramTrackCounts} rawTargetTram={targetProfile.TramTrackCounts} rawTargetLayout={targetProfile.TramTrackLayout} invertTarget={invertTarget}";
            return true;
        }

        internal static CompositionFlags GetTramTrackUpgradeFlags(CompositionFlags flags)
        {
            CompositionFlags.Side trackFlags = GetTramTrackSideFlags();
            return new CompositionFlags(
                default,
                flags.m_Left & trackFlags,
                flags.m_Right & trackFlags);
        }

        private static CompositionFlags.Side GetTramTrackSideFlags()
        {
            return CompositionFlags.Side.PrimaryTrack |
                   CompositionFlags.Side.SecondaryTrack |
                   CompositionFlags.Side.TertiaryTrack |
                   CompositionFlags.Side.QuaternaryTrack;
        }

        private static int CountTramTrackUpgradeFlags(CompositionFlags flags)
        {
            return CountTramTrackUpgradeFlags(flags.m_Left) + CountTramTrackUpgradeFlags(flags.m_Right);
        }

        private static int CountTramTrackUpgradeFlags(CompositionFlags.Side flags)
        {
            int count = 0;
            if ((flags & CompositionFlags.Side.PrimaryTrack) != 0)
            {
                count++;
            }

            if ((flags & CompositionFlags.Side.SecondaryTrack) != 0)
            {
                count++;
            }

            if ((flags & CompositionFlags.Side.TertiaryTrack) != 0)
            {
                count++;
            }

            if ((flags & CompositionFlags.Side.QuaternaryTrack) != 0)
            {
                count++;
            }

            return count;
        }

        private string GetPrefabNameFromPrefab(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null)
            {
                return "<null prefab>";
            }

            if (m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                return prefabBase.name;
            }

            return $"<unresolved {FormatEntity(prefabEntity)}>";
        }

        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }
    }
}
