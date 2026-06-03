using Colossal.Entities;
using Game.Net;
using Game.Prefabs;
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

        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }

        internal string BuildTramUpgradeRejectSample(
            Entity candidatePrefab,
            RoadLaneProfile candidateProfile,
            bool invert,
            string tramUpgradeDetail)
        {
            return $"candidate={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, candidatePrefab)} candidateRoad={candidateProfile.RoadCounts} candidateTramTracks={candidateProfile.TramTrackCounts} invert={invert} {tramUpgradeDetail}";
        }

        internal string BuildBusUpgradeRejectSample(
            Entity candidatePrefab,
            string candidateName,
            bool candidateIsSourcePrefab,
            RoadLaneProfile candidateProfile,
            bool defaultLaneMatch,
            string busUpgradeDetail)
        {
            return $"candidate={candidateName} candidateEntity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} candidateRoad={candidateProfile.RoadCounts} candidateBusLayout={candidateProfile.BusLaneLayout} candidateSource={candidateProfile.Source} defaultLaneMatch={defaultLaneMatch} {busUpgradeDetail}";
        }

        internal string BuildRoadBuilderBusUpgradeSample(
            Entity candidatePrefab,
            string candidateName,
            bool candidateIsSourcePrefab,
            RoadLaneProfile candidateProfile,
            bool defaultLaneMatch,
            string busUpgradeDetail)
        {
            return $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} defaultLaneMatch={defaultLaneMatch} candidateRoad={candidateProfile.RoadCounts} candidateBusLayout={candidateProfile.BusLaneLayout} {busUpgradeDetail}";
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
            targetProfile = RoadLaneProfile.CreateEmpty("TramUpgrade:missing");
            detail = "tramUpgrade=not-scanned";

            if (prefabEntity == Entity.Null || requiredTramCounts.IsEmpty)
            {
                detail = $"tramUpgrade=skipped prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} requiredTram={requiredTramCounts}";
                return false;
            }

            if (!TryGetUpgradeCompositions(prefabEntity, "tramUpgrade", out DynamicBuffer<NetGeometryComposition> compositions, out detail))
            {
                return false;
            }

            RoadLaneCounts orientedDesiredRoadCounts = invertTarget ? desiredRoadCounts.Swapped() : desiredRoadCounts;
            RoadLaneCounts orientedDesiredEffectiveCounts = invertTarget ? desiredEffectiveCounts.Swapped() : desiredEffectiveCounts;
            RoadLaneCounts orientedRequiredTramCounts = invertTarget ? requiredTramCounts.Swapped() : requiredTramCounts;
            TramUpgradeScanStats stats = default;
            int bestScore = int.MaxValue;
            CompositionFlags bestUpgradeFlags = default;
            RoadLaneProfile bestProfile = default;

            for (int i = 0; i < compositions.Length; i++)
            {
                stats.ScannedCompositions++;
                NetGeometryComposition composition = compositions[i];
                CompositionFlags upgradeFlags = GetTramTrackUpgradeFlags(composition.m_Mask);
                if (upgradeFlags == default(CompositionFlags))
                {
                    continue;
                }

                stats.TrackMasks++;
                if (!m_RoadLaneProfileBuilder.TryGetCompositionRoadLaneProfile(composition.m_Composition, out RoadLaneProfile profile))
                {
                    continue;
                }

                stats.LaneProfiles++;
                RoadLaneCounts effectiveCounts = RoadLaneCounts.Add(profile.RoadCounts, profile.IndependentTramCounts);
                if (!CountsEqual(effectiveCounts, orientedDesiredEffectiveCounts))
                {
                    continue;
                }

                stats.EffectiveMatches++;
                if (!CountsEqual(profile.TramTrackCounts, orientedRequiredTramCounts))
                {
                    continue;
                }

                stats.TramMatches++;
                bool independentTramMatch = CountsEqual(profile.IndependentTramCounts, orientedRequiredTramCounts);
                bool publicTransportTramMatch = CountsEqual(profile.PublicTransportTramCounts, orientedRequiredTramCounts);
                if (independentTramMatch)
                {
                    stats.IndependentTramMatches++;
                }

                if (publicTransportTramMatch)
                {
                    stats.PublicTransportTramMatches++;
                }

                bool preferredRoadCounts = CountsEqual(profile.RoadCounts, orientedDesiredRoadCounts);
                if (preferredRoadCounts)
                {
                    stats.RoadPreferredMatches++;
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
                if (stats.TrackMasks == 0 &&
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
                    detail = $"tramUpgrade=source-flags-fallback prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} {stats.Format()} {sourceUpgradeDetail}";
                    return true;
                }

                detail = $"tramUpgrade=no-match prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} {stats.Format()} desiredRoad={desiredRoadCounts} desiredEffective={desiredEffectiveCounts} requiredTram={requiredTramCounts} orientedDesiredRoad={orientedDesiredRoadCounts} orientedDesiredEffective={orientedDesiredEffectiveCounts} orientedRequiredTram={orientedRequiredTramCounts} invertTarget={invertTarget}";
                return false;
            }

            CompositionFlags targetFlags = invertTarget
                ? NetCompositionHelpers.InvertCompositionFlags(bestUpgradeFlags)
                : bestUpgradeFlags;
            targetUpgrade = new Upgraded { m_Flags = targetFlags };
            targetProfile = bestProfile;
            targetProfile.Source = $"TramUpgrade:{targetFlags}";
            RoadLaneCounts bestEffectiveCounts = RoadLaneCounts.Add(bestProfile.RoadCounts, bestProfile.IndependentTramCounts);
            detail = $"tramUpgrade=matched prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} upgradeFlags={targetFlags} rawUpgradeFlags={bestUpgradeFlags} upgradedRoad={bestProfile.RoadCounts} upgradedEffective={bestEffectiveCounts} upgradedIndependentTram={bestProfile.IndependentTramCounts} upgradedPublicTransportTram={bestProfile.PublicTransportTramCounts} upgradedTramTracks={bestProfile.TramTrackCounts} upgradedTramTrackLayout={bestProfile.TramTrackLayout} upgradedTramDetail={bestProfile.TramTrackDetail} upgradedPublicTransportTramDetail={bestProfile.PublicTransportTramDetail} upgradedBusLayout={bestProfile.BusLaneLayout} upgradedBusDetail={bestProfile.BusLaneDetail} {stats.Format()} invertTarget={invertTarget}";
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
            targetProfile = RoadLaneProfile.CreateEmpty("BusUpgrade:missing");
            detail = "busUpgrade=not-scanned";

            if (prefabEntity == Entity.Null || !sourceProfile.BusLaneLayout.HasAny)
            {
                detail = $"busUpgrade=skipped prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} sourceBusLayout={sourceProfile.BusLaneLayout}";
                return false;
            }

            if (!TryGetUpgradeCompositions(prefabEntity, "busUpgrade", out DynamicBuffer<NetGeometryComposition> compositions, out detail))
            {
                return false;
            }

            BusUpgradeScanStats stats = default;
            BusUpgradeBestCandidate best = BusUpgradeBestCandidate.Create();

            for (int i = 0; i < compositions.Length; i++)
            {
                stats.ScannedCompositions++;
                NetGeometryComposition composition = compositions[i];
                if (composition.m_Mask == default(CompositionFlags))
                {
                    continue;
                }

                stats.NonDefaultMasks++;
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
                    stats.LaneProfiles++;
                    stats.CalculatedProfiles++;
                    TryAcceptBusUpgradeProfile(
                        calculatedRoadProfile,
                        desiredRoadCounts,
                        sourceProfile,
                        composition.m_Mask,
                        ref stats,
                        ref best);
                }

                if (directProfile)
                {
                    stats.LaneProfiles++;
                    stats.DirectProfiles++;
                    TryAcceptBusUpgradeProfile(
                        directRoadProfile,
                        desiredRoadCounts,
                        sourceProfile,
                        composition.m_Mask,
                        ref stats,
                        ref best);
                }
            }

            if (!best.HasMatch)
            {
                detail = $"busUpgrade=no-match prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} {stats.Format()} desiredRoad={desiredRoadCounts} sourceBusLayout={sourceProfile.BusLaneLayout}";
                return false;
            }

            invertTarget = best.Invert;
            CompositionFlags targetFlags = invertTarget
                ? NetCompositionHelpers.InvertCompositionFlags(best.UpgradeFlags)
                : best.UpgradeFlags;
            targetUpgrade = new Upgraded { m_Flags = targetFlags };
            targetProfile = best.Profile;
            targetProfile.Source = $"BusUpgrade:{targetFlags}";
            detail = $"busUpgrade=matched prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} upgradeFlags={targetFlags} rawUpgradeFlags={best.UpgradeFlags} upgradedRoad={best.Profile.RoadCounts} upgradedBusLayout={best.Profile.BusLaneLayout} upgradedBusDetail={best.Profile.BusLaneDetail} upgradedTramTracks={best.Profile.TramTrackCounts} upgradedPublicTransportTram={best.Profile.PublicTransportTramCounts} {stats.Format()} layoutScore={best.Score} invertTarget={invertTarget}";
            return true;
        }

        private bool TryAcceptBusUpgradeProfile(
            RoadLaneProfile profile,
            RoadLaneCounts desiredRoadCounts,
            RoadLaneProfile sourceProfile,
            CompositionFlags compositionMask,
            ref BusUpgradeScanStats stats,
            ref BusUpgradeBestCandidate best)
        {
            if (profile.HasMarkedParking)
            {
                stats.ParkingProfiles++;
                return false;
            }

            if (!profile.BusLaneLayout.HasAny)
            {
                return false;
            }

            stats.BusProfiles++;
            if (!profile.TramTrackCounts.IsEmpty)
            {
                stats.PublicTransportTramProfiles++;
            }

            if (!RoadLaneCountMatcher.TryMatch(profile.RoadCounts, desiredRoadCounts, out bool candidateInvert))
            {
                return false;
            }

            stats.LaneMatches++;
            DirectionalLaneOffsetProfile orientedBusLayout = profile.BusLaneLayout.Oriented(candidateInvert);
            int score = ReplacementPrefabScoring.GetDirectionalLayoutOffsetScore(sourceProfile.BusLaneLayout, orientedBusLayout) +
                        (candidateInvert ? 1000 : 0);
            if (score >= best.Score)
            {
                return false;
            }

            best.Score = score;
            best.UpgradeFlags = compositionMask;
            best.Profile = profile;
            best.Invert = candidateInvert;
            return true;
        }

        private bool TryGetUpgradeCompositions(
            Entity prefabEntity,
            string detailPrefix,
            out DynamicBuffer<NetGeometryComposition> compositions,
            out string detail)
        {
            if (EntityManager.TryGetBuffer(prefabEntity, true, out compositions))
            {
                detail = $"{detailPrefix}=compositions-found";
                return true;
            }

            detail = $"{detailPrefix}=missing-compositions prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)}";
            return false;
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

        private struct TramUpgradeScanStats
        {
            public int ScannedCompositions;
            public int TrackMasks;
            public int LaneProfiles;
            public int EffectiveMatches;
            public int TramMatches;
            public int IndependentTramMatches;
            public int PublicTransportTramMatches;
            public int RoadPreferredMatches;

            public string Format()
            {
                return $"scannedCompositions={ScannedCompositions} trackMasks={TrackMasks} laneProfiles={LaneProfiles} effectiveMatches={EffectiveMatches} tramMatches={TramMatches} independentTramMatches={IndependentTramMatches} publicTransportTramMatches={PublicTransportTramMatches} roadPreferredMatches={RoadPreferredMatches}";
            }
        }

        private struct BusUpgradeScanStats
        {
            public int ScannedCompositions;
            public int NonDefaultMasks;
            public int LaneProfiles;
            public int CalculatedProfiles;
            public int DirectProfiles;
            public int ParkingProfiles;
            public int BusProfiles;
            public int PublicTransportTramProfiles;
            public int LaneMatches;

            public string Format()
            {
                return $"scannedCompositions={ScannedCompositions} nonDefaultMasks={NonDefaultMasks} laneProfiles={LaneProfiles} calculatedProfiles={CalculatedProfiles} directProfiles={DirectProfiles} parkingProfiles={ParkingProfiles} busProfiles={BusProfiles} publicTransportTramProfiles={PublicTransportTramProfiles} laneMatches={LaneMatches}";
            }
        }

        private struct BusUpgradeBestCandidate
        {
            public int Score;
            public CompositionFlags UpgradeFlags;
            public RoadLaneProfile Profile;
            public bool Invert;

            public bool HasMatch => UpgradeFlags != default(CompositionFlags);

            public static BusUpgradeBestCandidate Create()
            {
                return new BusUpgradeBestCandidate
                {
                    Score = int.MaxValue
                };
            }
        }
    }
}
