using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game.SceneFlow;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static PocketTurnLanes.Tool.PrefabMatching.RoadLaneCountMatcher;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal sealed class ReplacementPrefabMatcher
    {
        private const float PrefabWidthTolerance = 0.05f;

        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;
        private readonly UnlockSystem m_UnlockSystem;
        private readonly EntityQuery m_RoadPrefabQuery;
        private readonly RoadPrefabEligibility m_RoadPrefabEligibility;
        private readonly RoadBuilderPrefabSemantics m_RoadBuilderPrefabSemantics;
        private readonly RoadLaneProfileBuilder m_RoadLaneProfileBuilder;
        private readonly ReplacementRoadUpgradeMatcher m_RoadUpgradeMatcher;
        private readonly Func<string, CustomRoadAssetSourceFeatures, IReadOnlyList<CustomRoadAssetMatchRule>> m_GetCustomRoadAssetMatchRules;

        internal ReplacementPrefabMatcher(
            EntityManager entityManager,
            PrefabSystem prefabSystem,
            UnlockSystem unlockSystem,
            EntityQuery roadPrefabQuery,
            Func<BufferLookup<NetSubSection>> getNetSubSectionLookup,
            Func<BufferLookup<NetSectionPiece>> getNetSectionPieceLookup,
            Func<ComponentLookup<NetLaneData>> getNetLaneDataLookup,
            Func<BufferLookup<NetPieceLane>> getNetPieceLaneLookup,
            Func<string, CustomRoadAssetSourceFeatures, IReadOnlyList<CustomRoadAssetMatchRule>> getCustomRoadAssetMatchRules = null)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
            m_UnlockSystem = unlockSystem;
            m_RoadPrefabQuery = roadPrefabQuery;
            m_RoadPrefabEligibility = new RoadPrefabEligibility(entityManager, prefabSystem);
            m_RoadBuilderPrefabSemantics = new RoadBuilderPrefabSemantics(entityManager, prefabSystem);
            m_RoadLaneProfileBuilder = new RoadLaneProfileBuilder(
                entityManager,
                prefabSystem,
                getNetSubSectionLookup,
                getNetSectionPieceLookup,
                getNetLaneDataLookup,
                getNetPieceLaneLookup,
                m_RoadBuilderPrefabSemantics);
            m_RoadUpgradeMatcher = new ReplacementRoadUpgradeMatcher(
                entityManager,
                prefabSystem,
                m_RoadLaneProfileBuilder);
            m_GetCustomRoadAssetMatchRules = getCustomRoadAssetMatchRules;
        }

        private EntityManager EntityManager => m_EntityManager;

        private string GetPrefabName(Entity entity)
        {
            if (!EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
            {
                return "<no PrefabRef>";
            }

            return PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabRef.m_Prefab);
        }

        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }

        internal bool TryFindPocketLaneReplacementPrefab(
            Entity nodeEntity,
            Entity edgeEntity,
            out ReplacementPrefabMatch match)
        {
            match = default;

            if (!TryBuildSourceReplacementContext(
                    nodeEntity,
                    edgeEntity,
                    out SourceReplacementContext source))
            {
                return false;
            }

            NetGeometryData sourceGeometry = source.Geometry;
            bool nodeIsStart = source.NodeIsStart;
            bool sourceIsDlc = source.IsDlc;
            string sourceContentDetail = source.ContentDetail;
            RoadLaneProfile sourceProfile = source.Profile;
            RoadLaneCounts originalCounts = source.OriginalCounts;
            RoadLaneCounts desiredCounts = source.DesiredCounts;
            bool found = false;
            ReplacementSearchStats stats = ReplacementSearchStats.Create();
            int bestScore = int.MaxValue;
            ReplacementPrefabMatch bestMatch = default;
            bool sourceHasTramTracks = source.HasTramTracks;
            bool sourceHasIndependentTram = source.HasIndependentTram;
            bool sourceHasUpgraded = source.HasUpgraded;
            CompositionFlags sourceTramUpgradeFlags = source.TramUpgradeFlags;
            RoadLaneCounts originalEffectiveCounts = source.OriginalEffectiveCounts;
            RoadLaneCounts desiredEffectiveCounts = source.DesiredEffectiveCounts;

            CustomRoadAssetSourceFeatures sourceFeatures = GetSourceFeatures(source);
            if (TryGetCustomRoadAssetMatchRules(source.Prefab, sourceFeatures, out IReadOnlyList<CustomRoadAssetMatchRule> customRules))
            {
                for (int i = 0; i < customRules.Count; i++)
                {
                    CustomRoadAssetMatchRule rule = customRules[i];
                    if (TryBuildCustomRoadAssetMatch(
                            source,
                            rule.TargetPrefabName,
                            out match,
                            out ReplacementSearchStats customStats,
                            out string customDetail))
                    {
                        Mod.LogEssential($"[CustomRoadAssetMatch] Preferred target selected sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} ruleFeatures={CustomRoadAssetSourceFeatureUtility.Format(rule.SourceFeatures)} targetPrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, match.Prefab)} orientation={(match.Invert ? "reversed" : "direct")} nodeSide={(nodeIsStart ? "start" : "end")} candidateIndex={i + 1}/{customRules.Count} score={match.Score} scanned={customStats.Scanned} detail={customDetail}.");
                        return true;
                    }

                    Mod.LogEssential($"[CustomRoadAssetMatch] Preferred target rejected; trying next custom rule or automatic matcher sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} ruleFeatures={CustomRoadAssetSourceFeatureUtility.Format(rule.SourceFeatures)} targetPrefabName={rule.TargetPrefabName} nodeSide={(nodeIsStart ? "start" : "end")} candidateIndex={i + 1}/{customRules.Count} reason={customDetail}.");
                }
            }

            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity candidatePrefab = prefabEntities[i];
                    stats.Scanned++;
                    if (!TryBuildCandidateReplacementContext(
                            candidatePrefab,
                            source,
                            ref stats,
                            out CandidateReplacementContext candidate))
                    {
                        continue;
                    }

                    bool candidateIsSourcePrefab = candidate.IsSourcePrefab;
                    string candidateName = candidate.Name;
                    bool candidateLooksLikeRoadBuilder = candidate.LooksLikeRoadBuilder;
                    RoadLaneProfile candidateProfile = candidate.Profile;

                    if (!TryMatchReplacementCandidateLaneProfile(
                            candidatePrefab,
                            candidateName,
                            candidateIsSourcePrefab,
                            candidateLooksLikeRoadBuilder,
                            candidateProfile,
                            sourceProfile,
                            desiredCounts,
                            originalEffectiveCounts,
                            desiredEffectiveCounts,
                            sourceHasTramTracks,
                            sourceHasIndependentTram,
                            sourceTramUpgradeFlags,
                            ref stats,
                            out CandidateLaneMatch candidateMatch))
                    {
                        continue;
                    }

                    stats.LaneMatches++;
                    if (candidateIsSourcePrefab)
                    {
                        stats.SourcePrefabLaneMatches++;
                    }

                    CandidateScoreResult scoreResult = CalculateCandidateScore(
                        source,
                        candidate,
                        candidateMatch,
                        ref stats);
                    int score = scoreResult.Score;

                    if (!found || score < bestScore)
                    {
                        found = true;
                        bestScore = score;
                        bestMatch = BuildReplacementPrefabMatch(source, candidate, candidateMatch, scoreResult);
                    }
                }
            }

            if (!found)
            {
                Mod.LogDiagnostic($"[IntersectionTool] No pocket lane replacement prefab found sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={sourceProfile.IndependentTramCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} sourcePublicTransportTramDetail={sourceProfile.PublicTransportTramDetail} sourceTramTracks={sourceProfile.TramTrackCounts} sourceTramTrackLayout={sourceProfile.TramTrackLayout} sourceTramDetail={sourceProfile.TramTrackDetail} sourceHasUpgraded={sourceHasUpgraded} sourceTramUpgradeFlags={sourceTramUpgradeFlags} sourceBusLayout={sourceProfile.BusLaneLayout} sourceBusDetail={sourceProfile.BusLaneDetail} originalLanes={originalCounts} desiredLanes={desiredCounts} originalEffectiveLanes={originalEffectiveCounts} desiredEffectiveLanes={desiredEffectiveCounts} width={sourceGeometry.m_DefaultWidth:0.##}m scanned={stats.Scanned} bridgeQueryExcluded=True highwayExcluded={stats.HighwayExcluded} lockedExcluded={stats.LockedExcluded} lockedSample={stats.LockedSample} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} widthCandidateSample={stats.WidthCandidateSample} roadBuilderCandidateSample={stats.RoadBuilderCandidateSample} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderDiscardedSample={stats.RoadBuilderDiscardedSample} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderNotInPlaysetSample={stats.RoadBuilderNotInPlaysetSample} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} roadBuilderVisibilityUnknownSample={stats.RoadBuilderVisibilityUnknownSample} parkingExcluded={stats.ParkingExcluded} independentTramCandidates={stats.IndependentTramCandidates} publicTransportTramCandidates={stats.PublicTransportTramCandidates} tramUpgradeCandidates={stats.TramUpgradeCandidates} tramUpgradeRejected={stats.TramUpgradeRejected} tramUpgradeRejectSample={stats.TramUpgradeRejectSample} busUpgradeCandidates={stats.BusUpgradeCandidates} busUpgradeRejected={stats.BusUpgradeRejected} busUpgradeRejectSample={stats.BusUpgradeRejectSample} roadBuilderBusUpgradeSample={stats.RoadBuilderBusUpgradeSample} layoutScored={stats.LayoutScored} busLayoutCandidates={stats.BusLayoutCandidates} bestBusLayoutCandidate={stats.BestBusLayoutCandidateDetail} sourcePrefabLaneMatches={stats.SourcePrefabLaneMatches} laneMatches=0 missingLaneData={stats.MissingLaneData}.");
                return false;
            }

            match = bestMatch;
            Mod.LogDiagnostic($"[IntersectionTool] Replacement prefab selected sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} targetPrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, match.Prefab)} targetIsSourcePrefab={match.TargetIsSourcePrefab} targetDlc={match.TargetIsDlc} targetContent={match.TargetContentDetail} orientation={(match.Invert ? "reversed" : "direct")} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={match.SourceIndependentTramCounts} targetIndependentTram={match.TargetIndependentTramCounts} sourcePublicTransportTram={match.SourcePublicTransportTramCounts} targetPublicTransportTram={match.TargetPublicTransportTramCounts} sourceTramTracks={match.SourceTramTrackCounts} targetTramTracks={match.TargetTramTrackCounts} targetHasIndependentTram={match.TargetHasIndependentTram} targetHasPublicTransportTram={match.TargetHasPublicTransportTram} tramUpgradeFallback={match.TargetUsesTramUpgradeFallback} targetUpgrade={(match.HasTargetUpgrade ? match.TargetUpgrade.m_Flags.ToString() : "none")} tramMatch={match.TramMatchDetail} sourceTramTrackLayout={match.SourceTramTrackLayout} targetTramTrackLayout={match.TargetTramTrackLayout} sourceBusLayout={match.SourceBusLaneLayout} sourceBusDetail={match.SourceBusLaneDetail} targetBusLayout={match.TargetBusLaneLayout} targetBusDetail={match.TargetBusLaneDetail} layoutScore={match.LayoutScore} tramLayoutScore={match.TramLayoutScore} busLayoutScore={match.BusLayoutScore} layoutDetail={match.LayoutScoreDetail} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={match.OriginalCounts} desiredLanes={match.TargetCounts} originalEffectiveLanes={match.OriginalEffectiveCounts} desiredEffectiveLanes={match.TargetEffectiveCounts} candidateLanes={match.CandidateCounts} scanned={stats.Scanned} bridgeQueryExcluded=True highwayExcluded={stats.HighwayExcluded} lockedExcluded={stats.LockedExcluded} lockedSample={stats.LockedSample} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} widthCandidateSample={stats.WidthCandidateSample} roadBuilderCandidateSample={stats.RoadBuilderCandidateSample} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderDiscardedSample={stats.RoadBuilderDiscardedSample} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderNotInPlaysetSample={stats.RoadBuilderNotInPlaysetSample} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} roadBuilderVisibilityUnknownSample={stats.RoadBuilderVisibilityUnknownSample} parkingExcluded={stats.ParkingExcluded} independentTramCandidates={stats.IndependentTramCandidates} publicTransportTramCandidates={stats.PublicTransportTramCandidates} tramUpgradeCandidates={stats.TramUpgradeCandidates} tramUpgradeRejected={stats.TramUpgradeRejected} busUpgradeCandidates={stats.BusUpgradeCandidates} busUpgradeRejected={stats.BusUpgradeRejected} busUpgradeRejectSample={stats.BusUpgradeRejectSample} roadBuilderBusUpgradeSample={stats.RoadBuilderBusUpgradeSample} layoutScored={stats.LayoutScored} busLayoutCandidates={stats.BusLayoutCandidates} bestBusLayoutCandidate={stats.BestBusLayoutCandidateDetail} sourcePrefabLaneMatches={stats.SourcePrefabLaneMatches} laneMatches={stats.LaneMatches} missingLaneData={stats.MissingLaneData} score={match.Score}.");
            return true;
        }

        internal bool IsBridgeRoadEdge(Entity edgeEntity, out string detail)
        {
            return m_RoadPrefabEligibility.IsBridgeRoadEdge(edgeEntity, out detail);
        }

        internal bool IsHighwayRoadEdge(Entity edgeEntity, out string detail)
        {
            return m_RoadPrefabEligibility.IsHighwayRoadEdge(edgeEntity, out detail);
        }

        internal void GetRoadAssetSourceOptions(
            string query,
            List<RoadAssetPrefabOption> options,
            int maxCount)
        {
            options?.Clear();
            if (options == null)
            {
                return;
            }

            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity sourcePrefab = prefabEntities[i];
                    if (!TryBuildRoadAssetPrefabOption(sourcePrefab, out RoadAssetPrefabOption option) ||
                        !OptionMatchesQuery(option, query))
                    {
                        continue;
                    }

                    if (!TryGetSourceTargetAvailability(
                            sourcePrefab,
                            prefabEntities,
                            out SourceTargetAvailability availability,
                            out string availabilityDetail))
                    {
                        if (!string.IsNullOrEmpty(query))
                        {
                            Mod.LogDiagnostic($"[CustomRoadAssetMatch] Source search skipped prefab={option.PrefabName} entity={FormatEntity(sourcePrefab)} reason=no-compatible-targets detail={availabilityDetail}.");
                        }

                        continue;
                    }

                    ApplySourceTargetAvailability(ref option, availability);
                    options.Add(option);
                }
            }

            SortAndTrimOptions(options, maxCount);
        }

        internal bool TryGetCompatibleTargetOptions(
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures,
            string query,
            List<RoadAssetPrefabOption> options,
            int maxCount,
            out RoadAssetPrefabOption sourceOption,
            out string detail)
        {
            sourceOption = default;
            detail = string.Empty;
            options?.Clear();
            if (options == null)
            {
                detail = "options=null";
                return false;
            }

            sourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            if (!TryFindRoadPrefabByName(sourcePrefabName, out Entity sourcePrefab))
            {
                detail = $"sourceMissing sourcePrefabName={sourcePrefabName}";
                return false;
            }

            if (!TryBuildRoadAssetPrefabOption(sourcePrefab, out sourceOption))
            {
                detail = $"sourceInvalid sourcePrefabName={sourcePrefabName}";
                return false;
            }

            if (TryGetSourceTargetAvailability(
                    sourcePrefab,
                    out SourceTargetAvailability availability,
                    out _))
            {
                ApplySourceTargetAvailability(ref sourceOption, availability);
            }

            bool hasEndContext = TryBuildDefaultSourceReplacementContext(
                sourcePrefab,
                sourceFeatures,
                false,
                out SourceReplacementContext endContext,
                out string endDetail);
            bool hasStartContext = TryBuildDefaultSourceReplacementContext(
                sourcePrefab,
                sourceFeatures,
                true,
                out SourceReplacementContext startContext,
                out string startDetail);

            if (!hasEndContext && !hasStartContext)
            {
                detail = $"sourceInvalid sourcePrefabName={sourcePrefabName} endValidation={endDetail} startValidation={startDetail}";
                return false;
            }

            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity targetPrefab = prefabEntities[i];
                    if (!TryBuildRoadAssetPrefabOption(targetPrefab, out RoadAssetPrefabOption targetOption) ||
                        !OptionMatchesQuery(targetOption, query))
                    {
                        continue;
                    }

                    bool compatible =
                        hasEndContext &&
                        TryBuildCustomRoadAssetMatch(
                            endContext,
                            targetPrefab,
                            out _,
                            out _,
                            out _);
                    if (!compatible && hasStartContext)
                    {
                        compatible = TryBuildCustomRoadAssetMatch(
                            startContext,
                            targetPrefab,
                            out _,
                            out _,
                            out _);
                    }

                    if (!compatible)
                    {
                        continue;
                    }

                    options.Add(targetOption);
                }
            }

            SortAndTrimOptions(options, maxCount);
            string contextDetail = sourceFeatures == CustomRoadAssetSourceFeatures.None
                ? string.Empty
                : $" endValidation=({endDetail}) startValidation=({startDetail})";
            detail = $"sourcePrefabName={sourcePrefabName} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} targets={options.Count} endContext={hasEndContext} startContext={hasStartContext}{contextDetail}";
            return true;
        }

        internal bool IsValidCustomRoadAssetMatch(
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures,
            string targetPrefabName,
            out string detail)
        {
            detail = string.Empty;
            sourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            if (!TryFindRoadPrefabByName(sourcePrefabName, out Entity sourcePrefab))
            {
                detail = $"sourceMissing sourcePrefabName={sourcePrefabName}";
                return false;
            }

            if (!TryFindRoadPrefabByName(targetPrefabName, out Entity targetPrefab))
            {
                detail = $"targetMissing targetPrefabName={targetPrefabName}";
                return false;
            }

            bool hasEndContext = TryBuildDefaultSourceReplacementContext(
                sourcePrefab,
                sourceFeatures,
                false,
                out SourceReplacementContext endContext,
                out string endDetail);
            bool hasStartContext = TryBuildDefaultSourceReplacementContext(
                sourcePrefab,
                sourceFeatures,
                true,
                out SourceReplacementContext startContext,
                out string startDetail);

            if (hasEndContext &&
                TryBuildCustomRoadAssetMatch(
                    endContext,
                    targetPrefab,
                    out _,
                    out ReplacementSearchStats endStats,
                    out string validEndDetail))
            {
                detail = $"valid sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} nodeSide=end scanned={endStats.Scanned} {validEndDetail}";
                return true;
            }

            if (hasStartContext &&
                TryBuildCustomRoadAssetMatch(
                    startContext,
                    targetPrefab,
                    out _,
                    out ReplacementSearchStats startStats,
                    out string validStartDetail))
            {
                detail = $"valid sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} nodeSide=start scanned={startStats.Scanned} {validStartDetail}";
                return true;
            }

            detail = $"incompatible sourcePrefabName={sourcePrefabName} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} targetPrefabName={targetPrefabName} endContext={hasEndContext} endValidation={endDetail} startContext={hasStartContext} startValidation={startDetail}";
            return false;
        }

        internal bool TryGetMandatoryCustomRoadAssetSourceFeatures(
            string sourcePrefabName,
            out CustomRoadAssetSourceFeatures mandatoryFeatures,
            out string detail)
        {
            mandatoryFeatures = CustomRoadAssetSourceFeatures.None;
            detail = string.Empty;
            if (!TryFindRoadPrefabByName(sourcePrefabName, out Entity sourcePrefab))
            {
                detail = $"sourceMissing sourcePrefabName={sourcePrefabName}";
                return false;
            }

            if (!TryGetDefaultRoadLaneProfile(sourcePrefab, out RoadLaneProfile profile))
            {
                detail = $"sourceProfileMissing sourcePrefabName={sourcePrefabName}";
                return false;
            }

            mandatoryFeatures = GetMandatoryCustomRoadAssetSourceFeatures(profile);
            detail = $"sourcePrefabName={sourcePrefabName} mandatorySourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(mandatoryFeatures)} profileMandatorySourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(profile.MandatorySourceFeatures)} independentTram={profile.IndependentTramCounts} publicTransportTram={profile.PublicTransportTramCounts} dedicatedPublicTransport={profile.DedicatedPublicTransportLaneLayout} tramTracks={profile.TramTrackCounts} busLayout={profile.BusLaneLayout} profileSource={profile.Source}";
            return true;
        }

        private static CustomRoadAssetSourceFeatures GetMandatoryCustomRoadAssetSourceFeatures(
            RoadLaneProfile profile)
        {
            return CustomRoadAssetSourceFeatureUtility.Normalize(profile.MandatorySourceFeatures) &
                   (CustomRoadAssetSourceFeatures.Tram |
                    CustomRoadAssetSourceFeatures.PublicTransport);
        }

        private CustomRoadAssetSourceFeatures GetAvailableCustomRoadAssetSourceFeatures(
            Entity prefabEntity,
            RoadLaneProfile profile)
        {
            CustomRoadAssetSourceFeatures mandatoryFeatures =
                GetMandatoryCustomRoadAssetSourceFeatures(profile);
            CustomRoadAssetSourceFeatures nativeUpgradeFeatures =
                GetNativeUpgradeSourceFeatureAvailability(prefabEntity);
            CustomRoadAssetSourceFeatures roadBuilderFeatures = CustomRoadAssetSourceFeatures.None;
            if (m_RoadBuilderPrefabSemantics.TryGetConfigSourceFeatureAvailability(
                    prefabEntity,
                    out CustomRoadAssetSourceFeatures configFeatures,
                    out _))
            {
                roadBuilderFeatures = configFeatures;
            }

            return CustomRoadAssetSourceFeatureUtility.Normalize(
                    mandatoryFeatures |
                    nativeUpgradeFeatures |
                    roadBuilderFeatures) &
                (CustomRoadAssetSourceFeatures.Tram |
                 CustomRoadAssetSourceFeatures.PublicTransport);
        }

        private CustomRoadAssetSourceFeatures GetNativeUpgradeSourceFeatureAvailability(
            Entity prefabEntity)
        {
            if (!m_RoadLaneProfileBuilder.TryGetPrefabCompositionOptionSideFlags(
                prefabEntity,
                out CompositionFlags.Side tramTrackProbeSideFlags,
                out CompositionFlags.Side publicTransportLaneProbeSideFlags,
                out _))
            {
                return CustomRoadAssetSourceFeatures.None;
            }

            CustomRoadAssetSourceFeatures features = CustomRoadAssetSourceFeatures.None;
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                tramTrackProbeSideFlags,
                publicTransportLaneProbeSideFlags,
                ref features);
            return CustomRoadAssetSourceFeatureUtility.Normalize(features) &
                   (CustomRoadAssetSourceFeatures.Tram |
                    CustomRoadAssetSourceFeatures.PublicTransport);
        }

        private void AddCalculatedSourceFeatureAvailability(
            Entity prefabEntity,
            CompositionFlags.Side tramTrackProbeSideFlags,
            CompositionFlags.Side publicTransportLaneProbeSideFlags,
            ref CustomRoadAssetSourceFeatures features)
        {
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                tramTrackProbeSideFlags,
                true,
                ref features);
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                publicTransportLaneProbeSideFlags,
                false,
                ref features);
        }

        private void AddCalculatedSourceFeatureAvailability(
            Entity prefabEntity,
            CompositionFlags.Side sideFlags,
            bool tramTrackFlags,
            ref CustomRoadAssetSourceFeatures features)
        {
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                sideFlags,
                tramTrackFlags,
                CompositionFlags.Side.PrimaryTrack,
                CompositionFlags.Side.PrimaryLane,
                ref features);
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                sideFlags,
                tramTrackFlags,
                CompositionFlags.Side.SecondaryTrack,
                CompositionFlags.Side.SecondaryLane,
                ref features);
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                sideFlags,
                tramTrackFlags,
                CompositionFlags.Side.TertiaryTrack,
                CompositionFlags.Side.TertiaryLane,
                ref features);
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                sideFlags,
                tramTrackFlags,
                CompositionFlags.Side.QuaternaryTrack,
                CompositionFlags.Side.QuaternaryLane,
                ref features);
        }

        private void AddCalculatedSourceFeatureAvailability(
            Entity prefabEntity,
            CompositionFlags.Side sideFlags,
            bool tramTrackFlags,
            CompositionFlags.Side trackFlag,
            CompositionFlags.Side laneFlag,
            ref CustomRoadAssetSourceFeatures features)
        {
            CompositionFlags.Side flag = tramTrackFlags ? trackFlag : laneFlag;
            if ((sideFlags & flag) == 0)
            {
                return;
            }

            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                new CompositionFlags(default, flag, default),
                ref features);
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                new CompositionFlags(default, default, flag),
                ref features);
            AddCalculatedSourceFeatureAvailability(
                prefabEntity,
                new CompositionFlags(default, flag, flag),
                ref features);
        }

        private void AddCalculatedSourceFeatureAvailability(
            Entity prefabEntity,
            CompositionFlags compositionFlags,
            ref CustomRoadAssetSourceFeatures features)
        {
            if (!m_RoadLaneProfileBuilder.TryCalculateRoadLaneProfile(
                    prefabEntity,
                    compositionFlags,
                    $"NetGeometrySection:feature-probe:{compositionFlags}",
                    out RoadLaneProfile calculatedProfile))
            {
                return;
            }

            AddUpgradeProfileFeatureAvailability(calculatedProfile, ref features);
        }

        private static int CountEnabledSideFlags(CompositionFlags.Side flags)
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

            if ((flags & CompositionFlags.Side.PrimaryLane) != 0)
            {
                count++;
            }

            if ((flags & CompositionFlags.Side.SecondaryLane) != 0)
            {
                count++;
            }

            if ((flags & CompositionFlags.Side.TertiaryLane) != 0)
            {
                count++;
            }

            if ((flags & CompositionFlags.Side.QuaternaryLane) != 0)
            {
                count++;
            }

            return count;
        }

        private static void AddUpgradeProfileFeatureAvailability(
            RoadLaneProfile profile,
            ref CustomRoadAssetSourceFeatures features)
        {
            if (!profile.TramTrackCounts.IsEmpty)
            {
                features |= CustomRoadAssetSourceFeatures.Tram;
            }

            if (profile.BusLaneLayout.HasAny ||
                profile.DedicatedPublicTransportLaneLayout.HasAny ||
                !profile.PublicTransportTramCounts.IsEmpty)
            {
                features |= CustomRoadAssetSourceFeatures.PublicTransport;
            }
        }

        internal bool TryGetRoadAssetPrefabOption(
            string prefabName,
            out RoadAssetPrefabOption option,
            out string detail)
        {
            option = default;
            if (!TryFindRoadPrefabByName(prefabName, out Entity prefabEntity))
            {
                detail = $"prefabMissing prefabName={prefabName}";
                return false;
            }

            if (!TryBuildRoadAssetPrefabOption(prefabEntity, out option))
            {
                detail = $"prefabInvalid prefabName={prefabName}";
                return false;
            }

            if (TryGetSourceTargetAvailability(
                    prefabEntity,
                    out SourceTargetAvailability availability,
                    out _))
            {
                ApplySourceTargetAvailability(ref option, availability);
            }
            else
            {
                option.HasForwardTargetCandidates = false;
                option.HasReverseTargetCandidates = false;
                option.HasReverseSourceSide = false;
            }

            detail = "ok";
            return true;
        }

        private bool TryGetSourceTargetAvailability(
            Entity sourcePrefab,
            out SourceTargetAvailability availability,
            out string detail)
        {
            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                return TryGetSourceTargetAvailability(
                    sourcePrefab,
                    prefabEntities,
                    out availability,
                    out detail);
            }
        }

        private bool TryGetSourceTargetAvailability(
            Entity sourcePrefab,
            NativeArray<Entity> prefabEntities,
            out SourceTargetAvailability availability,
            out string detail)
        {
            availability = default;
            bool hasForwardCandidates = HasCompatibleTargetForSourceFeatures(
                sourcePrefab,
                CustomRoadAssetSourceFeatures.None,
                prefabEntities,
                out string forwardDetail);
            bool canReverse = CanHaveReverseSourceSide(sourcePrefab, out string reverseSourceDetail);
            bool hasReverseCandidates = false;
            string reverseDetail = "reverse-source-unavailable";
            if (canReverse)
            {
                hasReverseCandidates = HasCompatibleTargetForSourceFeatures(
                    sourcePrefab,
                    CustomRoadAssetSourceFeatures.Reverse,
                    prefabEntities,
                    out reverseDetail);
            }

            availability = new SourceTargetAvailability
            {
                HasForwardTargetCandidates = hasForwardCandidates,
                HasReverseTargetCandidates = hasReverseCandidates
            };
            detail = $"forward={hasForwardCandidates} forwardDetail=({forwardDetail}) reverseSource=({reverseSourceDetail}) reverse={hasReverseCandidates} reverseDetail=({reverseDetail})";
            return availability.HasAnyTargetCandidates;
        }

        private bool HasCompatibleTargetForSourceFeatures(
            Entity sourcePrefab,
            CustomRoadAssetSourceFeatures sourceFeatures,
            NativeArray<Entity> prefabEntities,
            out string detail)
        {
            bool hasEndContext = TryBuildDefaultSourceReplacementContext(
                sourcePrefab,
                sourceFeatures,
                false,
                out SourceReplacementContext endContext,
                out string endDetail);
            bool hasStartContext = TryBuildDefaultSourceReplacementContext(
                sourcePrefab,
                sourceFeatures,
                true,
                out SourceReplacementContext startContext,
                out string startDetail);

            if (!hasEndContext && !hasStartContext)
            {
                detail = $"no-source-context endValidation={endDetail} startValidation={startDetail}";
                return false;
            }

            for (int i = 0; i < prefabEntities.Length; i++)
            {
                Entity targetPrefab = prefabEntities[i];
                if (hasEndContext &&
                    TryBuildCustomRoadAssetMatch(
                        endContext,
                        targetPrefab,
                        out _,
                        out _,
                        out string endMatchDetail))
                {
                    detail = $"nodeSide=end targetPrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, targetPrefab)} {endMatchDetail}";
                    return true;
                }

                if (hasStartContext &&
                    TryBuildCustomRoadAssetMatch(
                        startContext,
                        targetPrefab,
                        out _,
                        out _,
                        out string startMatchDetail))
                {
                    detail = $"nodeSide=start targetPrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, targetPrefab)} {startMatchDetail}";
                    return true;
                }
            }

            detail = $"no-compatible-targets endContext={hasEndContext} endValidation={endDetail} startContext={hasStartContext} startValidation={startDetail}";
            return false;
        }

        private bool CanHaveReverseSourceSide(Entity sourcePrefab, out string detail)
        {
            if (!TryGetDefaultRoadLaneProfile(sourcePrefab, out RoadLaneProfile profile))
            {
                detail = "sourceLaneProfileMissing";
                return false;
            }

            bool canReverse = profile.RoadCounts.IsAsymmetric &&
                              profile.RoadCounts.Forward > 0 &&
                              profile.RoadCounts.Backward > 0;
            detail = $"sourceLanes={profile.RoadCounts} asymmetric={profile.RoadCounts.IsAsymmetric} forward={profile.RoadCounts.Forward} backward={profile.RoadCounts.Backward}";
            return canReverse;
        }

        private static void ApplySourceTargetAvailability(
            ref RoadAssetPrefabOption option,
            SourceTargetAvailability availability)
        {
            option.HasForwardTargetCandidates = availability.HasForwardTargetCandidates;
            option.HasReverseTargetCandidates = availability.HasReverseTargetCandidates;
            option.HasReverseSourceSide = availability.HasReverseTargetCandidates;
        }

        private bool TryGetCustomRoadAssetMatchRules(
            Entity sourcePrefab,
            CustomRoadAssetSourceFeatures sourceFeatures,
            out IReadOnlyList<CustomRoadAssetMatchRule> rules)
        {
            rules = null;
            if (m_GetCustomRoadAssetMatchRules == null || sourcePrefab == Entity.Null)
            {
                return false;
            }

            string sourcePrefabName = PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, sourcePrefab);
            if (string.IsNullOrWhiteSpace(sourcePrefabName) ||
                sourcePrefabName[0] == '<')
            {
                return false;
            }

            rules = m_GetCustomRoadAssetMatchRules(sourcePrefabName, sourceFeatures);
            return rules != null && rules.Count > 0;
        }

        private static CustomRoadAssetSourceFeatures GetSourceFeatures(SourceReplacementContext source)
        {
            RoadLaneProfile sourceProfile = source.Profile;
            CustomRoadAssetSourceFeatures features = CustomRoadAssetSourceFeatures.None;
            if (!sourceProfile.TramTrackCounts.IsEmpty)
            {
                features |= CustomRoadAssetSourceFeatures.Tram;
            }

            if (sourceProfile.BusLaneLayout.HasAny)
            {
                features |= CustomRoadAssetSourceFeatures.PublicTransport;
            }

            if (IsReverseAsymmetricSourceSide(source.OriginalCounts, source.NodeIsStart))
            {
                features |= CustomRoadAssetSourceFeatures.Reverse;
            }

            return features;
        }

        private static bool IsReverseAsymmetricSourceSide(RoadLaneCounts originalCounts, bool nodeIsStart)
        {
            return originalCounts.IsAsymmetric &&
                   originalCounts.GetIncomingAtNode(nodeIsStart) < originalCounts.GetOutgoingAtNode(nodeIsStart);
        }

        private bool TryBuildCustomRoadAssetMatch(
            SourceReplacementContext source,
            string targetPrefabName,
            out ReplacementPrefabMatch match,
            out ReplacementSearchStats stats,
            out string detail)
        {
            match = default;
            stats = ReplacementSearchStats.Create();
            if (!TryFindRoadPrefabByName(targetPrefabName, out Entity targetPrefab))
            {
                detail = $"targetMissing targetPrefabName={targetPrefabName}";
                return false;
            }

            return TryBuildCustomRoadAssetMatch(
                source,
                targetPrefab,
                out match,
                out stats,
                out detail);
        }

        private bool TryBuildCustomRoadAssetMatch(
            SourceReplacementContext source,
            Entity targetPrefab,
            out ReplacementPrefabMatch match,
            out ReplacementSearchStats stats,
            out string detail)
        {
            match = default;
            stats = ReplacementSearchStats.Create();
            stats.Scanned = 1;

            if (!TryBuildCandidateReplacementContext(
                    targetPrefab,
                    source,
                    ref stats,
                    out CandidateReplacementContext candidate))
            {
                detail = $"candidateRejected targetPrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, targetPrefab)} stats=({FormatCustomRuleStats(stats)})";
                return false;
            }

            if (!TryMatchReplacementCandidateLaneProfile(
                    targetPrefab,
                    candidate.Name,
                    candidate.IsSourcePrefab,
                    candidate.LooksLikeRoadBuilder,
                    candidate.Profile,
                    source.Profile,
                    source.DesiredCounts,
                    source.OriginalEffectiveCounts,
                    source.DesiredEffectiveCounts,
                    source.HasTramTracks,
                    source.HasIndependentTram,
                    source.TramUpgradeFlags,
                    ref stats,
                    out CandidateLaneMatch candidateMatch))
            {
                detail = $"laneProfileMismatch targetPrefab={candidate.Name} sourceLanes={source.OriginalCounts} desiredLanes={source.DesiredCounts} candidateLanes={candidate.Profile.RoadCounts} stats=({FormatCustomRuleStats(stats)})";
                return false;
            }

            stats.LaneMatches++;
            if (candidate.IsSourcePrefab)
            {
                stats.SourcePrefabLaneMatches++;
            }

            CandidateScoreResult scoreResult = CalculateCandidateScore(
                source,
                candidate,
                candidateMatch,
                ref stats);
            match = BuildReplacementPrefabMatch(source, candidate, candidateMatch, scoreResult);
            detail = $"targetPrefab={candidate.Name} targetEntity={FormatEntity(targetPrefab)} sourceLanes={source.OriginalCounts} desiredLanes={source.DesiredCounts} candidateLanes={candidate.Profile.RoadCounts} targetEffectiveLanes={candidateMatch.TargetEffectiveCounts} orientation={(candidateMatch.Invert ? "reversed" : "direct")} tramMatch={candidateMatch.TramMatchDetail} layoutScore={scoreResult.LayoutScore} score={scoreResult.Score} stats=({FormatCustomRuleStats(stats)})";
            return true;
        }

        private bool TryBuildSourceReplacementContext(
            Entity nodeEntity,
            Entity edgeEntity,
            out SourceReplacementContext context)
        {
            context = default;

            if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef sourcePrefabRef) ||
                !EntityManager.TryGetComponent(sourcePrefabRef.m_Prefab, out NetGeometryData sourceGeometry) ||
                !EntityManager.TryGetComponent(sourcePrefabRef.m_Prefab, out RoadData sourceRoadData) ||
                !EntityManager.TryGetComponent(sourcePrefabRef.m_Prefab, out NetData sourceNetData))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)}: missing edge or source prefab data.");
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            if (!nodeIsStart && !nodeIsEnd)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)}: node={FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            m_RoadPrefabEligibility.GetRoadContentProfile(sourcePrefabRef.m_Prefab, out bool sourceIsDlc, out string sourceContentDetail);
            if (m_RoadPrefabEligibility.IsBridgeRoadPrefab(sourcePrefabRef.m_Prefab, out string sourceBridgeDetail))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Skip replacement prefab search sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail}: source road prefab is a bridge and bridge roads are excluded from selection and replacement matching. {sourceBridgeDetail}");
                return false;
            }

            if (m_RoadPrefabEligibility.IsHighwayRoadPrefab(sourcePrefabRef.m_Prefab, out string sourceHighwayDetail))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Skip replacement prefab search sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail}: source road prefab uses highway rules and highway roads are excluded from selection and replacement matching. {sourceHighwayDetail}");
                return false;
            }

            if (!TryGetRoadLaneProfile(
                    edgeEntity,
                    sourcePrefabRef.m_Prefab,
                    out RoadLaneProfile sourceProfile))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: no default road lane counts were found.");
                return false;
            }

            RoadLaneCounts originalCounts = sourceProfile.RoadCounts;
            int originalIncomingCount = originalCounts.GetIncomingAtNode(nodeIsStart);
            if (originalIncomingCount <= 0)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Skip replacement prefab search sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} nodeSide={(nodeIsStart ? "start" : "end")}: source side has no incoming road lanes; originalLanes={originalCounts} incoming={originalIncomingCount} outgoing={originalCounts.GetOutgoingAtNode(nodeIsStart)}.");
                return false;
            }

            RoadLaneCounts desiredCounts = GetDesiredPocketLaneCounts(originalCounts, nodeIsStart);

            bool sourceHasUpgraded = EntityManager.TryGetComponent(edgeEntity, out Upgraded sourceUpgraded);
            CompositionFlags sourceTramUpgradeFlags = sourceHasUpgraded
                ? ReplacementRoadUpgradeMatcher.GetTramTrackUpgradeFlags(sourceUpgraded.m_Flags)
                : default;
            if (sourceTramUpgradeFlags == default(CompositionFlags))
            {
                sourceTramUpgradeFlags = GetSynthesizedIndependentTramUpgradeFlags(sourceProfile);
            }
            context = new SourceReplacementContext
            {
                Prefab = sourcePrefabRef.m_Prefab,
                Geometry = sourceGeometry,
                RoadData = sourceRoadData,
                NetData = sourceNetData,
                IsDlc = sourceIsDlc,
                ContentDetail = sourceContentDetail,
                NodeIsStart = nodeIsStart,
                Profile = sourceProfile,
                OriginalCounts = originalCounts,
                DesiredCounts = desiredCounts,
                HasTramTracks = !sourceProfile.TramTrackCounts.IsEmpty,
                HasIndependentTram = !sourceProfile.IndependentTramCounts.IsEmpty,
                HasUpgraded = sourceHasUpgraded,
                TramUpgradeFlags = sourceTramUpgradeFlags,
                OriginalEffectiveCounts = RoadLaneCounts.Add(originalCounts, sourceProfile.IndependentTramCounts),
                DesiredEffectiveCounts = RoadLaneCounts.Add(desiredCounts, sourceProfile.IndependentTramCounts)
            };
            return true;
        }

        private bool TryBuildDefaultSourceReplacementContext(
            Entity sourcePrefab,
            CustomRoadAssetSourceFeatures sourceFeatures,
            bool nodeIsStart,
            out SourceReplacementContext context,
            out string detail)
        {
            context = default;
            detail = string.Empty;

            if (sourcePrefab == Entity.Null ||
                !EntityManager.TryGetComponent(sourcePrefab, out NetGeometryData sourceGeometry) ||
                !EntityManager.TryGetComponent(sourcePrefab, out RoadData sourceRoadData) ||
                !EntityManager.TryGetComponent(sourcePrefab, out NetData sourceNetData))
            {
                detail = $"missingSourcePrefabData sourcePrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, sourcePrefab)} entity={FormatEntity(sourcePrefab)}";
                return false;
            }

            m_RoadPrefabEligibility.GetRoadContentProfile(sourcePrefab, out bool sourceIsDlc, out string sourceContentDetail);
            if (m_RoadPrefabEligibility.IsBridgeRoadPrefab(sourcePrefab, out string sourceBridgeDetail))
            {
                detail = $"sourceBridgeExcluded {sourceBridgeDetail}";
                return false;
            }

            if (m_RoadPrefabEligibility.IsHighwayRoadPrefab(sourcePrefab, out string sourceHighwayDetail))
            {
                detail = $"sourceHighwayExcluded {sourceHighwayDetail}";
                return false;
            }

            if (!TryGetDefaultRoadLaneProfile(
                    sourcePrefab,
                    out RoadLaneProfile sourceProfile))
            {
                detail = $"sourceLaneProfileMissing sourcePrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, sourcePrefab)} entity={FormatEntity(sourcePrefab)}";
                return false;
            }

            sourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            string requestedSourceFeatureDetail = "requestedSourceTram=not-requested";
            CompositionFlags requestedSourceTramFlags = default;
            if ((sourceFeatures & CustomRoadAssetSourceFeatures.Tram) != 0 &&
                !TryApplyRequestedSourceTramFeature(
                    sourcePrefab,
                    ref sourceProfile,
                    out requestedSourceTramFlags,
                    out requestedSourceFeatureDetail))
            {
                detail = $"requestedSourceTramUnavailable sourcePrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, sourcePrefab)} entity={FormatEntity(sourcePrefab)} {requestedSourceFeatureDetail}";
                return false;
            }

            RoadLaneCounts originalCounts = sourceProfile.RoadCounts;
            bool wantsReverse = (sourceFeatures & CustomRoadAssetSourceFeatures.Reverse) != 0;
            bool isReverseSide = IsReverseAsymmetricSourceSide(originalCounts, nodeIsStart);
            int incomingCount = originalCounts.GetIncomingAtNode(nodeIsStart);
            int outgoingCount = originalCounts.GetOutgoingAtNode(nodeIsStart);
            if (wantsReverse && !originalCounts.IsAsymmetric)
            {
                detail = $"sourceReverseNotAvailable sourceLanes={originalCounts} nodeSide={(nodeIsStart ? "start" : "end")} incoming={incomingCount} outgoing={outgoingCount}";
                return false;
            }

            if (wantsReverse != isReverseSide)
            {
                detail = wantsReverse
                    ? $"sourceNodeSideIsForward sourceLanes={originalCounts} nodeSide={(nodeIsStart ? "start" : "end")} incoming={incomingCount} outgoing={outgoingCount}"
                    : $"sourceNodeSideIsReverse sourceLanes={originalCounts} nodeSide={(nodeIsStart ? "start" : "end")} incoming={incomingCount} outgoing={outgoingCount}";
                return false;
            }

            int originalIncomingCount = originalCounts.GetIncomingAtNode(nodeIsStart);
            if (originalIncomingCount <= 0)
            {
                detail = $"sourceNodeSideHasNoIncomingLanes sourceLanes={originalCounts} nodeSide={(nodeIsStart ? "start" : "end")} incoming={originalIncomingCount} outgoing={originalCounts.GetOutgoingAtNode(nodeIsStart)}";
                return false;
            }

            RoadLaneCounts desiredCounts = GetDesiredPocketLaneCounts(originalCounts, nodeIsStart);
            CompositionFlags sourceTramUpgradeFlags = requestedSourceTramFlags;
            if (sourceTramUpgradeFlags == default(CompositionFlags))
            {
                sourceTramUpgradeFlags = GetSynthesizedIndependentTramUpgradeFlags(sourceProfile);
            }
            context = new SourceReplacementContext
            {
                Prefab = sourcePrefab,
                Geometry = sourceGeometry,
                RoadData = sourceRoadData,
                NetData = sourceNetData,
                IsDlc = sourceIsDlc,
                ContentDetail = sourceContentDetail,
                NodeIsStart = nodeIsStart,
                Profile = sourceProfile,
                OriginalCounts = originalCounts,
                DesiredCounts = desiredCounts,
                HasTramTracks = !sourceProfile.TramTrackCounts.IsEmpty,
                HasIndependentTram = !sourceProfile.IndependentTramCounts.IsEmpty,
                HasUpgraded = requestedSourceTramFlags != default(CompositionFlags),
                TramUpgradeFlags = sourceTramUpgradeFlags,
                OriginalEffectiveCounts = RoadLaneCounts.Add(originalCounts, sourceProfile.IndependentTramCounts),
                DesiredEffectiveCounts = RoadLaneCounts.Add(desiredCounts, sourceProfile.IndependentTramCounts)
            };
            detail = $"ok {requestedSourceFeatureDetail}";
            return true;
        }

        private static CompositionFlags GetSynthesizedIndependentTramUpgradeFlags(
            RoadLaneProfile sourceProfile)
        {
            if (sourceProfile.TramTrackCounts.IsEmpty ||
                !CountsEqual(sourceProfile.TramTrackCounts, sourceProfile.IndependentTramCounts) ||
                !sourceProfile.PublicTransportTramCounts.IsEmpty ||
                !TryGetTrackSideFlags(
                    sourceProfile.IndependentTramCounts.Forward,
                    out CompositionFlags.Side forwardFlags) ||
                !TryGetTrackSideFlags(
                    sourceProfile.IndependentTramCounts.Backward,
                    out CompositionFlags.Side backwardFlags))
            {
                return default;
            }

            return new CompositionFlags(default, forwardFlags, backwardFlags);
        }

        private static bool TryGetTrackSideFlags(
            int count,
            out CompositionFlags.Side sideFlags)
        {
            sideFlags = default;
            if (count < 0 || count > 4)
            {
                return false;
            }

            CompositionFlags.Side[] orderedFlags =
            {
                CompositionFlags.Side.PrimaryTrack,
                CompositionFlags.Side.SecondaryTrack,
                CompositionFlags.Side.TertiaryTrack,
                CompositionFlags.Side.QuaternaryTrack
            };

            for (int i = 0; i < count; i++)
            {
                sideFlags |= orderedFlags[i];
            }

            return true;
        }

        private bool TryApplyRequestedSourceTramFeature(
            Entity sourcePrefab,
            ref RoadLaneProfile sourceProfile,
            out CompositionFlags requestedSourceTramFlags,
            out string detail)
        {
            requestedSourceTramFlags = default;
            if (!sourceProfile.TramTrackCounts.IsEmpty)
            {
                detail = $"requestedSourceTram=default profile={sourceProfile.Source} road={sourceProfile.RoadCounts} tram={sourceProfile.TramTrackCounts} independentTram={sourceProfile.IndependentTramCounts} publicTransportTram={sourceProfile.PublicTransportTramCounts} tramDetail={sourceProfile.TramTrackDetail}";
                return true;
            }

            bool found = false;
            int bestScore = int.MaxValue;
            CompositionFlags bestFlags = default;
            RoadLaneProfile bestProfile = default;
            string bestDetail = "bestProfile=none";
            RequestedSourceTramScanStats stats = default;

            if (!m_RoadLaneProfileBuilder.TryGetPrefabCompositionOptionSideFlags(
                    sourcePrefab,
                    out CompositionFlags.Side tramTrackLeftProbeSideFlags,
                    out CompositionFlags.Side tramTrackRightProbeSideFlags,
                    out _,
                    out _,
                    out string sectionOptionDetail))
            {
                detail = $"requestedSourceTram=missing-section-option-flags {sectionOptionDetail}";
                return false;
            }

            stats.SectionOptionDetail = sectionOptionDetail;
            stats.SectionTrackSideFlags = CountEnabledSideFlags(tramTrackLeftProbeSideFlags | tramTrackRightProbeSideFlags);
            ScanRequestedSourceTramSectionProfiles(
                sourcePrefab,
                tramTrackLeftProbeSideFlags,
                tramTrackRightProbeSideFlags,
                sourceProfile.RoadCounts,
                ref found,
                ref bestScore,
                ref bestFlags,
                ref bestProfile,
                ref bestDetail,
                ref stats);

            if (!found)
            {
                detail = $"requestedSourceTram=no-profile {stats.Format()}";
                return false;
            }

            sourceProfile = bestProfile;
            requestedSourceTramFlags = bestFlags;
            detail = $"requestedSourceTram=applied {bestDetail} {stats.Format()}";
            return true;
        }

        private void ScanRequestedSourceTramSectionProfiles(
            Entity sourcePrefab,
            CompositionFlags.Side tramTrackLeftProbeSideFlags,
            CompositionFlags.Side tramTrackRightProbeSideFlags,
            RoadLaneCounts defaultRoadCounts,
            ref bool found,
            ref int bestScore,
            ref CompositionFlags bestFlags,
            ref RoadLaneProfile bestProfile,
            ref string bestDetail,
            ref RequestedSourceTramScanStats stats)
        {
            CompositionFlags.Side[] trackFlags =
            {
                CompositionFlags.Side.PrimaryTrack,
                CompositionFlags.Side.SecondaryTrack,
                CompositionFlags.Side.TertiaryTrack,
                CompositionFlags.Side.QuaternaryTrack
            };

            List<CompositionFlags.Side> leftSubsets = BuildEnabledSideFlagSubsets(
                tramTrackLeftProbeSideFlags,
                trackFlags);
            List<CompositionFlags.Side> rightSubsets = BuildEnabledSideFlagSubsets(
                tramTrackRightProbeSideFlags,
                trackFlags);

            for (int leftIndex = 0; leftIndex < leftSubsets.Count; leftIndex++)
            {
                for (int rightIndex = 0; rightIndex < rightSubsets.Count; rightIndex++)
                {
                    CompositionFlags.Side leftSubset = leftSubsets[leftIndex];
                    CompositionFlags.Side rightSubset = rightSubsets[rightIndex];
                    if (leftSubset == default && rightSubset == default)
                    {
                        continue;
                    }

                    ScanRequestedSourceTramSectionProfileMask(
                        sourcePrefab,
                        leftSubset,
                        rightSubset,
                        defaultRoadCounts,
                        ref found,
                        ref bestScore,
                        ref bestFlags,
                        ref bestProfile,
                        ref bestDetail,
                        ref stats);
                }
            }
        }

        private void ScanRequestedSourceTramSectionProfileMask(
            Entity sourcePrefab,
            CompositionFlags.Side leftTrackFlags,
            CompositionFlags.Side rightTrackFlags,
            RoadLaneCounts defaultRoadCounts,
            ref bool found,
            ref int bestScore,
            ref CompositionFlags bestFlags,
            ref RoadLaneProfile bestProfile,
            ref string bestDetail,
            ref RequestedSourceTramScanStats stats)
        {
            if (leftTrackFlags == default && rightTrackFlags == default)
            {
                return;
            }

            TryAcceptRequestedSourceTramSectionProfile(
                sourcePrefab,
                new CompositionFlags(default, leftTrackFlags, rightTrackFlags),
                defaultRoadCounts,
                ref found,
                ref bestScore,
                ref bestFlags,
                ref bestProfile,
                ref bestDetail,
                ref stats);
        }

        private static List<CompositionFlags.Side> BuildEnabledSideFlagSubsets(
            CompositionFlags.Side availableFlags,
            CompositionFlags.Side[] knownFlags)
        {
            List<CompositionFlags.Side> subsets = new List<CompositionFlags.Side>
            {
                default
            };
            int subsetLimit = 1 << knownFlags.Length;
            for (int subsetBits = 1; subsetBits < subsetLimit; subsetBits++)
            {
                CompositionFlags.Side subset = default;
                for (int i = 0; i < knownFlags.Length; i++)
                {
                    if ((subsetBits & (1 << i)) != 0)
                    {
                        subset |= knownFlags[i];
                    }
                }

                if ((availableFlags & subset) == subset)
                {
                    subsets.Add(subset);
                }
            }

            return subsets;
        }

        private void TryAcceptRequestedSourceTramSectionProfile(
            Entity sourcePrefab,
            CompositionFlags compositionMask,
            RoadLaneCounts defaultRoadCounts,
            ref bool found,
            ref int bestScore,
            ref CompositionFlags bestFlags,
            ref RoadLaneProfile bestProfile,
            ref string bestDetail,
            ref RequestedSourceTramScanStats stats)
        {
            stats.SectionProbeMasks++;
            if (!m_RoadLaneProfileBuilder.TryCalculateRoadLaneProfile(
                    sourcePrefab,
                    compositionMask,
                    $"NetGeometrySection:requested-source-tram-probe:{compositionMask}",
                    out RoadLaneProfile calculatedProfile))
            {
                return;
            }

            stats.SectionProbeProfiles++;
            stats.CalculatedProfiles++;
            TryAcceptRequestedSourceTramProfile(
                calculatedProfile,
                compositionMask,
                defaultRoadCounts,
                ref found,
                ref bestScore,
                ref bestFlags,
                ref bestProfile,
                ref bestDetail,
                ref stats);
        }

        private static void TryAcceptRequestedSourceTramProfile(
            RoadLaneProfile profile,
            CompositionFlags compositionMask,
            RoadLaneCounts defaultRoadCounts,
            ref bool found,
            ref int bestScore,
            ref CompositionFlags bestFlags,
            ref RoadLaneProfile bestProfile,
            ref string bestDetail,
            ref RequestedSourceTramScanStats stats)
        {
            stats.LaneProfiles++;
            if (profile.TramTrackCounts.IsEmpty)
            {
                return;
            }

            stats.TramProfiles++;
            if (!profile.IndependentTramCounts.IsEmpty)
            {
                stats.IndependentTramProfiles++;
            }

            if (!profile.PublicTransportTramCounts.IsEmpty)
            {
                stats.PublicTransportTramProfiles++;
            }

            int score = GetRequestedSourceTramProfileScore(profile, defaultRoadCounts);
            if (found && score >= bestScore)
            {
                return;
            }

            found = true;
            bestScore = score;
            bestFlags = compositionMask;
            bestProfile = profile;
            bestDetail = $"profile={profile.Source} mask={compositionMask} score={score} road={profile.RoadCounts} tram={profile.TramTrackCounts} independentTram={profile.IndependentTramCounts} publicTransportTram={profile.PublicTransportTramCounts} tramDetail={profile.TramTrackDetail} independentTramDetail={profile.IndependentTramDetail}";
        }

        private static int GetRequestedSourceTramProfileScore(
            RoadLaneProfile profile,
            RoadLaneCounts defaultRoadCounts)
        {
            int score = 0;
            if (profile.IndependentTramCounts.IsEmpty)
            {
                score += profile.PublicTransportTramCounts.IsEmpty ? 1000 : 500;
            }

            if (defaultRoadCounts.Forward == defaultRoadCounts.Backward)
            {
                int roadAsymmetry = Math.Abs(profile.RoadCounts.Forward - profile.RoadCounts.Backward);
                int tramAsymmetry = Math.Abs(profile.TramTrackCounts.Forward - profile.TramTrackCounts.Backward);
                int independentTramAsymmetry = Math.Abs(profile.IndependentTramCounts.Forward - profile.IndependentTramCounts.Backward);
                score += (roadAsymmetry + tramAsymmetry + independentTramAsymmetry) * 250;
                if (profile.TramTrackCounts.Forward == 0 ||
                    profile.TramTrackCounts.Backward == 0)
                {
                    score += 500;
                }
            }

            score += (Math.Abs(defaultRoadCounts.Forward - profile.RoadCounts.Forward) +
                      Math.Abs(defaultRoadCounts.Backward - profile.RoadCounts.Backward)) * 10;
            return score;
        }

        private bool TryBuildCandidateReplacementContext(
            Entity candidatePrefab,
            SourceReplacementContext source,
            ref ReplacementSearchStats stats,
            out CandidateReplacementContext context)
        {
            context = default;

            bool candidateIsSourcePrefab = candidatePrefab == source.Prefab;
            string candidateName = PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, candidatePrefab);
            if (!candidateIsSourcePrefab &&
                m_UnlockSystem != null &&
                m_PrefabSystem.TryGetPrefab(candidatePrefab, out PrefabBase candidatePrefabBase) &&
                m_UnlockSystem.IsLocked(candidatePrefabBase))
            {
                stats.LockedExcluded++;
                stats.AddLockedSample(
                    $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} locked=True",
                    12);
                return false;
            }

            m_RoadBuilderPrefabSemantics.GetComponentProfile(
                candidatePrefab,
                out bool candidateHasRoadBuilderComponent,
                out bool candidateIsDiscardedRoadBuilderPrefab,
                out string candidateRoadBuilderComponentDetail);
            bool candidateLooksLikeRoadBuilder =
                candidateHasRoadBuilderComponent ||
                RoadBuilderPrefabSemantics.LooksLikeRoadPrefabName(candidateName);

            if (candidateIsDiscardedRoadBuilderPrefab)
            {
                stats.RoadBuilderDiscarded++;
                stats.AddRoadBuilderDiscardedSample(
                    $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} {candidateRoadBuilderComponentDetail}",
                    8);
                return false;
            }

            if (candidateLooksLikeRoadBuilder)
            {
                if (m_RoadBuilderPrefabSemantics.TryGetPrefabVisibility(
                        candidatePrefab,
                        out bool candidateIsInRoadBuilderPlayset,
                        out string candidateRoadBuilderVisibilityDetail))
                {
                    if (!candidateIsInRoadBuilderPlayset && !candidateIsSourcePrefab)
                    {
                        stats.RoadBuilderNotInPlaysetExcluded++;
                        stats.AddRoadBuilderNotInPlaysetSample(
                            $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} {candidateRoadBuilderComponentDetail} {candidateRoadBuilderVisibilityDetail}",
                            8);
                        return false;
                    }
                }
                else
                {
                    stats.RoadBuilderVisibilityUnknown++;
                    stats.AddRoadBuilderVisibilityUnknownSample(
                        $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} {candidateRoadBuilderComponentDetail} {candidateRoadBuilderVisibilityDetail}",
                        8);
                }
            }

            if (!EntityManager.TryGetComponent(candidatePrefab, out RoadData candidateRoadData))
            {
                return false;
            }

            if (RoadPrefabEligibility.IsHighwayRoadData(candidateRoadData))
            {
                stats.HighwayExcluded++;
                return false;
            }

            if (!EntityManager.TryGetComponent(candidatePrefab, out NetGeometryData candidateGeometry))
            {
                return false;
            }

            if (math.abs(candidateGeometry.m_DefaultWidth - source.Geometry.m_DefaultWidth) > PrefabWidthTolerance)
            {
                return false;
            }

            stats.WidthMatches++;
            if (!TryGetDefaultRoadLaneProfile(
                    candidatePrefab,
                    out RoadLaneProfile candidateProfile))
            {
                stats.MissingLaneData++;
                if (source.HasTramTracks || source.Profile.BusLaneLayout.HasAny)
                {
                    stats.AddWidthCandidateSample(
                        $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} status=missingLaneData deleted={EntityManager.HasComponent<Deleted>(candidatePrefab)}",
                        24);
                    if (candidateLooksLikeRoadBuilder)
                    {
                        stats.AddRoadBuilderCandidateSample(
                            $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} status=missingLaneData deleted={EntityManager.HasComponent<Deleted>(candidatePrefab)}",
                            16);
                    }
                }

                return false;
            }

            if (source.HasTramTracks || source.Profile.BusLaneLayout.HasAny)
            {
                stats.AddWidthCandidateSample(
                    $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} defaultRoad={candidateProfile.RoadCounts} defaultBus={candidateProfile.BusLaneLayout} defaultTram={candidateProfile.TramTrackCounts} profileSource={candidateProfile.Source} markedParking={candidateProfile.HasMarkedParking} deleted={EntityManager.HasComponent<Deleted>(candidatePrefab)}",
                    24);
                if (candidateLooksLikeRoadBuilder)
                {
                    stats.AddRoadBuilderCandidateSample(
                        $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} defaultRoad={candidateProfile.RoadCounts} defaultBus={candidateProfile.BusLaneLayout} defaultTram={candidateProfile.TramTrackCounts} profileSource={candidateProfile.Source} markedParking={candidateProfile.HasMarkedParking} deleted={EntityManager.HasComponent<Deleted>(candidatePrefab)}",
                        16);
                }
            }

            if (candidateProfile.HasMarkedParking)
            {
                stats.ParkingExcluded++;
                return false;
            }

            m_RoadPrefabEligibility.GetRoadContentProfile(candidatePrefab, out bool candidateIsDlc, out string candidateContentDetail);
            if (!source.IsDlc && candidateIsDlc)
            {
                stats.DlcBlocked++;
                return false;
            }

            context = new CandidateReplacementContext
            {
                Prefab = candidatePrefab,
                Name = candidateName,
                IsSourcePrefab = candidateIsSourcePrefab,
                LooksLikeRoadBuilder = candidateLooksLikeRoadBuilder,
                RoadData = candidateRoadData,
                Geometry = candidateGeometry,
                Profile = candidateProfile,
                IsDlc = candidateIsDlc,
                ContentDetail = candidateContentDetail
            };
            return true;
        }

        private CandidateScoreResult CalculateCandidateScore(
            SourceReplacementContext source,
            CandidateReplacementContext candidate,
            CandidateLaneMatch candidateMatch,
            ref ReplacementSearchStats stats)
        {
            EntityManager.TryGetComponent(candidate.Prefab, out NetData candidateNetData);
            int score = ReplacementPrefabScoring.GetReplacementPrefabScore(
                source.RoadData,
                source.NetData,
                source.Geometry,
                candidate.RoadData,
                candidateNetData,
                candidate.Geometry,
                candidateMatch.Invert,
                source.IsDlc,
                candidate.IsDlc);
            int layoutScore = ReplacementPrefabScoring.GetReplacementLayoutScore(
                source.Profile,
                candidateMatch.TargetLayoutProfile,
                candidateMatch.Invert,
                out int tramLayoutScore,
                out int busLayoutScore,
                out string layoutScoreDetail,
                out DirectionalLaneOffsetProfile orientedTargetTramLayout,
                out DirectionalLaneOffsetProfile orientedTargetBusLayout);
            score += layoutScore;
            if (source.Profile.TramTrackLayout.HasAny ||
                source.Profile.BusLaneLayout.HasAny)
            {
                stats.LayoutScored++;
            }

            if (source.Profile.BusLaneLayout.HasAny &&
                orientedTargetBusLayout.HasAny)
            {
                stats.RecordBusLayoutCandidate(
                    score,
                    $"candidate={candidate.Name} orientation={(candidateMatch.Invert ? "reversed" : "direct")} score={score} candidateRoad={candidate.Profile.RoadCounts} targetSource={candidateMatch.TargetLayoutProfile.Source} targetBus={orientedTargetBusLayout} targetBusDetail={candidateMatch.TargetLayoutProfile.BusLaneDetail} targetPublicTransportTram={candidateMatch.TargetPublicTransportTramCounts} targetUpgrade={(candidateMatch.HasTargetUpgrade ? candidateMatch.TargetUpgrade.m_Flags.ToString() : "none")}");
            }

            RoadLaneCounts requiredTramCounts = source.HasIndependentTram
                ? source.Profile.IndependentTramCounts
                : source.Profile.TramTrackCounts;
            score += ReplacementPrefabScoring.GetTramTargetScoreAdjustment(
                source.HasTramTracks,
                requiredTramCounts,
                candidateMatch.TargetUsesTramUpgradeFallback,
                candidateMatch.TargetHasTramTrackMatch,
                candidateMatch.TargetHasIndependentTram,
                candidateMatch.TargetIndependentTramCounts,
                candidateMatch.TargetHasPublicTransportTram,
                candidateMatch.TargetPublicTransportTramCounts,
                candidateMatch.Invert);

            return new CandidateScoreResult
            {
                Score = score,
                LayoutScore = layoutScore,
                TramLayoutScore = tramLayoutScore,
                BusLayoutScore = busLayoutScore,
                LayoutScoreDetail = layoutScoreDetail,
                OrientedTargetTramLayout = orientedTargetTramLayout,
                OrientedTargetBusLayout = orientedTargetBusLayout
            };
        }

        private static ReplacementPrefabMatch BuildReplacementPrefabMatch(
            SourceReplacementContext source,
            CandidateReplacementContext candidate,
            CandidateLaneMatch candidateMatch,
            CandidateScoreResult scoreResult)
        {
            return new ReplacementPrefabMatch
            {
                Prefab = candidate.Prefab,
                Invert = candidateMatch.Invert,
                OriginalCounts = source.OriginalCounts,
                TargetCounts = source.DesiredCounts,
                CandidateCounts = candidate.Profile.RoadCounts,
                OriginalEffectiveCounts = source.OriginalEffectiveCounts,
                TargetEffectiveCounts = candidateMatch.TargetEffectiveCounts,
                SourceIndependentTramCounts = source.Profile.IndependentTramCounts,
                TargetIndependentTramCounts = candidateMatch.TargetIndependentTramCounts,
                SourcePublicTransportTramCounts = source.Profile.PublicTransportTramCounts,
                TargetPublicTransportTramCounts = candidateMatch.TargetPublicTransportTramCounts,
                SourceTramTrackCounts = source.Profile.TramTrackCounts,
                TargetTramTrackCounts = candidateMatch.TargetTramTrackCounts,
                TargetHasIndependentTram = candidateMatch.TargetHasIndependentTram,
                TargetHasPublicTransportTram = candidateMatch.TargetHasPublicTransportTram,
                TargetUsesTramUpgradeFallback = candidateMatch.TargetUsesTramUpgradeFallback,
                HasTargetUpgrade = candidateMatch.HasTargetUpgrade,
                TargetUpgrade = candidateMatch.TargetUpgrade,
                TramMatchDetail = candidateMatch.TramMatchDetail,
                SourceTramTrackLayout = source.Profile.TramTrackLayout.ToString(),
                TargetTramTrackLayout = scoreResult.OrientedTargetTramLayout.ToString(),
                SourceBusLaneLayout = source.Profile.BusLaneLayout.ToString(),
                TargetBusLaneLayout = scoreResult.OrientedTargetBusLayout.ToString(),
                TargetTramTrackOffsetProfile = scoreResult.OrientedTargetTramLayout,
                TargetBusLaneOffsetProfile = scoreResult.OrientedTargetBusLayout,
                SourceBusLaneDetail = source.Profile.BusLaneDetail,
                TargetBusLaneDetail = candidateMatch.TargetLayoutProfile.BusLaneDetail,
                LayoutScoreDetail = scoreResult.LayoutScoreDetail,
                LayoutScore = scoreResult.LayoutScore,
                TramLayoutScore = scoreResult.TramLayoutScore,
                BusLayoutScore = scoreResult.BusLayoutScore,
                TargetIsSourcePrefab = candidate.IsSourcePrefab,
                TargetIsDlc = candidate.IsDlc,
                TargetContentDetail = candidate.ContentDetail,
                Score = scoreResult.Score
            };
        }

        private static RoadLaneCounts GetDesiredPocketLaneCounts(RoadLaneCounts originalCounts, bool currentNodeIsStart)
        {
            return originalCounts.WithAddedIncomingAtNode(currentNodeIsStart);
        }

        private bool TryFindRoadPrefabByName(string prefabName, out Entity prefabEntity)
        {
            prefabEntity = Entity.Null;
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                return false;
            }

            string normalizedName = prefabName.Trim();
            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity candidate = prefabEntities[i];
                    if (!m_PrefabSystem.TryGetPrefab(candidate, out PrefabBase prefabBase))
                    {
                        continue;
                    }

                    if (!string.Equals(prefabBase.name, normalizedName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    prefabEntity = candidate;
                    return true;
                }
            }

            return false;
        }

        private bool TryBuildRoadAssetPrefabOption(
            Entity prefabEntity,
            out RoadAssetPrefabOption option)
        {
            option = default;
            if (prefabEntity == Entity.Null ||
                !m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase) ||
                !EntityManager.TryGetComponent(prefabEntity, out NetGeometryData geometry) ||
                !TryGetDefaultRoadLaneProfile(prefabEntity, out RoadLaneProfile profile))
            {
                return false;
            }

            m_RoadPrefabEligibility.GetRoadContentProfile(prefabEntity, out bool isDlc, out string contentDetail);
            string transitSummary = string.Empty;
            if (!profile.TramTrackCounts.IsEmpty)
            {
                transitSummary += $" tram={profile.TramTrackCounts}";
            }

            if (profile.BusLaneLayout.HasAny)
            {
                transitSummary += $" bus={profile.BusLaneLayout}";
            }

            if (profile.DedicatedPublicTransportLaneLayout.HasAny)
            {
                transitSummary += $" dedicatedPT={profile.DedicatedPublicTransportLaneLayout}";
            }

            if (profile.HasMarkedParking)
            {
                transitSummary += " markedParking=True";
            }

            CustomRoadAssetSourceFeatures mandatoryFeatures =
                GetMandatoryCustomRoadAssetSourceFeatures(profile);
            CustomRoadAssetSourceFeatures availableFeatures =
                GetAvailableCustomRoadAssetSourceFeatures(
                    prefabEntity,
                    profile);
            option = new RoadAssetPrefabOption
            {
                PrefabName = prefabBase.name,
                DisplayName = GetLocalizedAssetName(prefabBase),
                Icon = GetRoadAssetIcon(prefabBase),
                Summary = $"lanes={profile.RoadCounts} width={geometry.m_DefaultWidth:0.##}m content={(isDlc ? "dlc" : "base")} profile={profile.Source}{transitSummary}",
                IsDlc = isDlc,
                ContentDetail = contentDetail,
                HasTram = (mandatoryFeatures & CustomRoadAssetSourceFeatures.Tram) != 0,
                HasPublicTransport = (mandatoryFeatures & CustomRoadAssetSourceFeatures.PublicTransport) != 0,
                CanHaveTram = (availableFeatures & CustomRoadAssetSourceFeatures.Tram) != 0,
                CanHavePublicTransport = (availableFeatures & CustomRoadAssetSourceFeatures.PublicTransport) != 0,
                HasAsymmetricRoadLanes = profile.RoadCounts.IsAsymmetric,
                HasReverseSourceSide = profile.RoadCounts.IsAsymmetric &&
                                       profile.RoadCounts.Forward > 0 &&
                                       profile.RoadCounts.Backward > 0
            };
            return true;
        }

        private static string GetRoadAssetIcon(PrefabBase prefabBase)
        {
            if (prefabBase != null)
            {
                UIObject uiObject = prefabBase.GetComponent<UIObject>();
                if (uiObject != null && !string.IsNullOrEmpty(uiObject.m_Icon))
                {
                    return uiObject.m_Icon;
                }

                if (!string.IsNullOrEmpty(prefabBase.thumbnailUrl))
                {
                    return prefabBase.thumbnailUrl;
                }
            }

            return "Media/Editor/DefaultObject.svg";
        }

        private static string GetLocalizedAssetName(PrefabBase prefabBase)
        {
            if (prefabBase == null)
            {
                return string.Empty;
            }

            string prefabName = prefabBase.name ?? string.Empty;
            if (TryGetLocalizedAssetName(prefabName, out string localizedName))
            {
                return localizedName;
            }

            if (prefabBase.asset != null)
            {
                if (TryGetLocalizedAssetName(prefabBase.asset.identifier, out localizedName) ||
                    TryGetLocalizedAssetName(prefabBase.asset.uniqueName, out localizedName) ||
                    TryGetLocalizedAssetName(prefabBase.asset.name, out localizedName))
                {
                    return localizedName;
                }
            }

            return prefabName;
        }

        private static bool TryGetLocalizedAssetName(string assetId, out string localizedName)
        {
            localizedName = string.Empty;
            if (string.IsNullOrWhiteSpace(assetId))
            {
                return false;
            }

            try
            {
                string key = $"Assets.NAME[{assetId}]";
                return GameManager.instance?.localizationManager?.activeDictionary?.TryGetValue(key, out localizedName) == true &&
                       !string.IsNullOrWhiteSpace(localizedName);
            }
            catch
            {
                localizedName = string.Empty;
                return false;
            }
        }

        private static bool OptionMatchesQuery(RoadAssetPrefabOption option, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            return MatchesQuery(option.PrefabName, query) ||
                   MatchesQuery(option.DisplayName, query) ||
                   MatchesQuery(option.Summary, query);
        }

        private static bool MatchesQuery(string value, string query)
        {
            return string.IsNullOrWhiteSpace(query) ||
                   (!string.IsNullOrEmpty(value) &&
                    value.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void SortAndTrimOptions(List<RoadAssetPrefabOption> options, int maxCount)
        {
            options.Sort((left, right) => string.Compare(
                left.DisplayName,
                right.DisplayName,
                StringComparison.OrdinalIgnoreCase));

            if (maxCount > 0 && options.Count > maxCount)
            {
                options.RemoveRange(maxCount, options.Count - maxCount);
            }
        }

        private static string FormatCustomRuleStats(ReplacementSearchStats stats)
        {
            return $"lockedExcluded={stats.LockedExcluded} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} parkingExcluded={stats.ParkingExcluded} highwayExcluded={stats.HighwayExcluded} missingLaneData={stats.MissingLaneData} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} tramUpgradeRejected={stats.TramUpgradeRejected} busUpgradeRejected={stats.BusUpgradeRejected} laneMatches={stats.LaneMatches}";
        }

        private struct SourceReplacementContext
        {
            public Entity Prefab;
            public NetGeometryData Geometry;
            public RoadData RoadData;
            public NetData NetData;
            public bool IsDlc;
            public string ContentDetail;
            public bool NodeIsStart;
            public RoadLaneProfile Profile;
            public RoadLaneCounts OriginalCounts;
            public RoadLaneCounts DesiredCounts;
            public bool HasTramTracks;
            public bool HasIndependentTram;
            public bool HasUpgraded;
            public CompositionFlags TramUpgradeFlags;
            public RoadLaneCounts OriginalEffectiveCounts;
            public RoadLaneCounts DesiredEffectiveCounts;
        }

        private struct SourceTargetAvailability
        {
            public bool HasForwardTargetCandidates;
            public bool HasReverseTargetCandidates;

            public bool HasAnyTargetCandidates => HasForwardTargetCandidates || HasReverseTargetCandidates;
        }

        private struct CandidateReplacementContext
        {
            public Entity Prefab;
            public string Name;
            public bool IsSourcePrefab;
            public bool LooksLikeRoadBuilder;
            public RoadData RoadData;
            public NetGeometryData Geometry;
            public RoadLaneProfile Profile;
            public bool IsDlc;
            public string ContentDetail;
        }

        private struct CandidateScoreResult
        {
            public int Score;
            public int LayoutScore;
            public int TramLayoutScore;
            public int BusLayoutScore;
            public string LayoutScoreDetail;
            public DirectionalLaneOffsetProfile OrientedTargetTramLayout;
            public DirectionalLaneOffsetProfile OrientedTargetBusLayout;
        }

        private struct CandidateLaneMatch
        {
            public bool Invert;
            public RoadLaneCounts TargetEffectiveCounts;
            public RoadLaneCounts TargetIndependentTramCounts;
            public RoadLaneCounts TargetPublicTransportTramCounts;
            public RoadLaneCounts TargetTramTrackCounts;
            public bool TargetHasIndependentTram;
            public bool TargetHasPublicTransportTram;
            public bool TargetHasTramTrackMatch;
            public bool TargetUsesTramUpgradeFallback;
            public bool HasTargetUpgrade;
            public Upgraded TargetUpgrade;
            public RoadLaneProfile TargetLayoutProfile;
            public string TramMatchDetail;
        }

        private struct RequestedSourceTramScanStats
        {
            public string SectionOptionDetail;
            public int SectionTrackSideFlags;
            public int SectionProbeMasks;
            public int SectionProbeProfiles;
            public int LaneProfiles;
            public int CalculatedProfiles;
            public int TramProfiles;
            public int IndependentTramProfiles;
            public int PublicTransportTramProfiles;

            public string Format()
            {
                return $"{(string.IsNullOrEmpty(SectionOptionDetail) ? "sectionOptionFlags=not-scanned" : SectionOptionDetail)} sectionTrackSideFlags={SectionTrackSideFlags} sectionProbeMasks={SectionProbeMasks} sectionProbeProfiles={SectionProbeProfiles} laneProfiles={LaneProfiles} calculatedProfiles={CalculatedProfiles} tramProfiles={TramProfiles} independentTramProfiles={IndependentTramProfiles} publicTransportTramProfiles={PublicTransportTramProfiles}";
            }
        }

        internal bool TryGetRoadLaneProfile(
            Entity edgeEntity,
            Entity fallbackPrefab,
            out RoadLaneProfile profile)
        {
            return m_RoadLaneProfileBuilder.TryGetRoadLaneProfile(
                edgeEntity,
                fallbackPrefab,
                out profile);
        }

        private bool TryGetDefaultRoadLaneProfile(
            Entity prefabEntity,
            out RoadLaneProfile profile)
        {
            return m_RoadLaneProfileBuilder.TryGetDefaultRoadLaneProfile(
                prefabEntity,
                out profile);
        }

        private bool TryMatchReplacementCandidateLaneProfile(
            Entity candidatePrefab,
            string candidateName,
            bool candidateIsSourcePrefab,
            bool candidateLooksLikeRoadBuilder,
            RoadLaneProfile candidateProfile,
            RoadLaneProfile sourceProfile,
            RoadLaneCounts desiredCounts,
            RoadLaneCounts originalEffectiveCounts,
            RoadLaneCounts desiredEffectiveCounts,
            bool sourceHasTramTracks,
            bool sourceHasIndependentTram,
            CompositionFlags sourceTramUpgradeFlags,
            ref ReplacementSearchStats stats,
            out CandidateLaneMatch match)
        {
            match = new CandidateLaneMatch
            {
                TargetEffectiveCounts = desiredCounts,
                TargetIndependentTramCounts = candidateProfile.IndependentTramCounts,
                TargetPublicTransportTramCounts = candidateProfile.PublicTransportTramCounts,
                TargetTramTrackCounts = candidateProfile.TramTrackCounts,
                TargetHasIndependentTram = !candidateProfile.IndependentTramCounts.IsEmpty,
                TargetHasPublicTransportTram = !candidateProfile.PublicTransportTramCounts.IsEmpty,
                TargetHasTramTrackMatch = !sourceHasTramTracks,
                TargetLayoutProfile = candidateProfile,
                TramMatchDetail = sourceHasTramTracks
                    ? $"sourceIndependentTram={sourceProfile.IndependentTramCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} candidateIndependentTram={candidateProfile.IndependentTramCounts} candidatePublicTransportTram={candidateProfile.PublicTransportTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.TramTrackDetail}"
                    : "sourceTramTracks=none"
            };

            bool invert = false;
            if (sourceHasIndependentTram)
            {
                if (match.TargetHasIndependentTram)
                {
                    stats.IndependentTramCandidates++;
                }

                if (match.TargetHasPublicTransportTram)
                {
                    stats.PublicTransportTramCandidates++;
                }

                if (match.TargetHasIndependentTram &&
                    RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredCounts, out invert) &&
                    CountsMatchForOrientation(candidateProfile.IndependentTramCounts, sourceProfile.IndependentTramCounts, invert))
                {
                    match.TargetEffectiveCounts = RoadLaneCounts.Add(desiredCounts, sourceProfile.IndependentTramCounts);
                    match.TargetHasTramTrackMatch = true;
                    match.TramMatchDetail = $"mode=independent-tram sourceIndependentTram={sourceProfile.IndependentTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} candidateIndependentTram={candidateProfile.IndependentTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.IndependentTramDetail}";
                }
                else if (match.TargetHasPublicTransportTram &&
                         RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredEffectiveCounts, out invert) &&
                         CountsMatchForOrientation(candidateProfile.PublicTransportTramCounts, sourceProfile.IndependentTramCounts, invert))
                {
                    match.TargetEffectiveCounts = desiredEffectiveCounts;
                    match.TargetHasTramTrackMatch = true;
                    match.TramMatchDetail = $"mode=public-transport-tram sourceIndependentTram={sourceProfile.IndependentTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} candidatePublicTransportTram={candidateProfile.PublicTransportTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.PublicTransportTramDetail}";
                }
                else
                {
                    if (!RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredEffectiveCounts, out invert))
                    {
                        return false;
                    }

                    if (!m_RoadUpgradeMatcher.TryFindMatchingTramUpgrade(
                            candidatePrefab,
                            candidateProfile,
                            sourceProfile,
                            desiredCounts,
                            desiredEffectiveCounts,
                            sourceProfile.IndependentTramCounts,
                            sourceTramUpgradeFlags,
                            invert,
                            out Upgraded targetUpgrade,
                            out RoadLaneProfile targetLayoutProfile,
                            out string tramUpgradeDetail))
                    {
                        stats.AddTramUpgradeRejection(m_RoadUpgradeMatcher.BuildTramUpgradeRejectSample(candidatePrefab, candidateProfile, invert, tramUpgradeDetail));
                        return false;
                    }

                    stats.TramUpgradeCandidates++;
                    match.TargetUsesTramUpgradeFallback = true;
                    match.HasTargetUpgrade = true;
                    match.TargetUpgrade = targetUpgrade;
                    match.TargetEffectiveCounts = desiredEffectiveCounts;
                    match.TargetLayoutProfile = targetLayoutProfile;
                    CopyTargetTramProfile(targetLayoutProfile, ref match);
                    match.TargetHasTramTrackMatch = true;
                    match.TramMatchDetail = $"mode=tram-upgrade-fallback sourceIndependentTram={sourceProfile.IndependentTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} adjustedOriginal={originalEffectiveCounts} adjustedDesired={desiredEffectiveCounts} targetUpgrade={targetUpgrade.m_Flags} {tramUpgradeDetail}";
                }
            }
            else if (sourceHasTramTracks)
            {
                if (!RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredCounts, out invert))
                {
                    return false;
                }

                bool hasIndependentTramMatch = match.TargetHasIndependentTram &&
                                               CountsMatchForOrientation(candidateProfile.IndependentTramCounts, sourceProfile.TramTrackCounts, invert);
                bool hasPublicTransportTramMatch = match.TargetHasPublicTransportTram &&
                                                   CountsMatchForOrientation(candidateProfile.PublicTransportTramCounts, sourceProfile.TramTrackCounts, invert);
                bool hasAnyTramMatch = CountsMatchForOrientation(candidateProfile.TramTrackCounts, sourceProfile.TramTrackCounts, invert);

                if (hasIndependentTramMatch)
                {
                    stats.IndependentTramCandidates++;
                    match.TargetHasTramTrackMatch = true;
                    match.TramMatchDetail = $"mode=tram-default-independent sourceTramTracks={sourceProfile.TramTrackCounts} candidateIndependentTram={candidateProfile.IndependentTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.IndependentTramDetail}";
                }
                else if (hasPublicTransportTramMatch)
                {
                    stats.PublicTransportTramCandidates++;
                    match.TargetHasTramTrackMatch = true;
                    match.TramMatchDetail = $"mode=public-transport-tram sourceTramTracks={sourceProfile.TramTrackCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} candidatePublicTransportTram={candidateProfile.PublicTransportTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.PublicTransportTramDetail}";
                }
                else if (hasAnyTramMatch)
                {
                    match.TargetHasTramTrackMatch = true;
                    match.TramMatchDetail = $"mode=tram-default-embedded sourceTramTracks={sourceProfile.TramTrackCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.TramTrackDetail}";
                }
                else if (m_RoadUpgradeMatcher.TryFindMatchingTramUpgrade(
                             candidatePrefab,
                             candidateProfile,
                             sourceProfile,
                             desiredCounts,
                             desiredCounts,
                             sourceProfile.TramTrackCounts,
                             sourceTramUpgradeFlags,
                             invert,
                             out Upgraded targetUpgrade,
                             out RoadLaneProfile targetLayoutProfile,
                             out string tramUpgradeDetail))
                {
                    stats.TramUpgradeCandidates++;
                    match.TargetUsesTramUpgradeFallback = true;
                    match.HasTargetUpgrade = true;
                    match.TargetUpgrade = targetUpgrade;
                    match.TargetLayoutProfile = targetLayoutProfile;
                    CopyTargetTramProfile(targetLayoutProfile, ref match);
                    match.TargetHasTramTrackMatch = true;
                    match.TramMatchDetail = $"mode=tram-upgrade-preserve sourceTramTracks={sourceProfile.TramTrackCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} targetUpgrade={targetUpgrade.m_Flags} {tramUpgradeDetail}";
                }
                else
                {
                    stats.AddTramUpgradeRejection(m_RoadUpgradeMatcher.BuildTramUpgradeRejectSample(candidatePrefab, candidateProfile, invert, tramUpgradeDetail));
                    match.TramMatchDetail = $"mode=missing-tram-fallback sourceTramTracks={sourceProfile.TramTrackCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.TramTrackDetail} rejectedUpgrade={tramUpgradeDetail}";
                }
            }
            else
            {
                if (!TryMatchBusReplacementCandidate(
                        candidatePrefab,
                        candidateName,
                        candidateIsSourcePrefab,
                        candidateLooksLikeRoadBuilder,
                        candidateProfile,
                        sourceProfile,
                        desiredCounts,
                        ref stats,
                        ref match,
                        out invert))
                {
                    return false;
                }
            }

            match.Invert = invert;
            return true;
        }

        private bool TryMatchBusReplacementCandidate(
            Entity candidatePrefab,
            string candidateName,
            bool candidateIsSourcePrefab,
            bool candidateLooksLikeRoadBuilder,
            RoadLaneProfile candidateProfile,
            RoadLaneProfile sourceProfile,
            RoadLaneCounts desiredCounts,
            ref ReplacementSearchStats stats,
            ref CandidateLaneMatch match,
            out bool invert)
        {
            bool defaultLaneMatch = RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredCounts, out invert);
            bool shouldScanBusUpgrade = sourceProfile.BusLaneLayout.HasAny &&
                                        (!defaultLaneMatch || !candidateProfile.BusLaneLayout.HasAny);
            string busUpgradeDetail = "busUpgrade=not-scanned";
            bool hasBusUpgradeMatch = false;
            bool busUpgradeInvert = false;
            Upgraded busTargetUpgrade = default;
            RoadLaneProfile busTargetProfile = default;

            if (shouldScanBusUpgrade)
            {
                hasBusUpgradeMatch = m_RoadUpgradeMatcher.TryFindMatchingBusUpgrade(
                    candidatePrefab,
                    sourceProfile,
                    desiredCounts,
                    out busUpgradeInvert,
                    out busTargetUpgrade,
                    out busTargetProfile,
                    out busUpgradeDetail);
            }

            if (hasBusUpgradeMatch)
            {
                stats.BusUpgradeCandidates++;
                invert = busUpgradeInvert;
                match.HasTargetUpgrade = true;
                match.TargetUpgrade = busTargetUpgrade;
                match.TargetLayoutProfile = busTargetProfile;
                CopyTargetTramProfile(busTargetProfile, ref match);
                match.TramMatchDetail = $"mode=bus-upgrade-preserve sourceBusLayout={sourceProfile.BusLaneLayout} targetUpgrade={busTargetUpgrade.m_Flags} {busUpgradeDetail}";
                return true;
            }

            if (shouldScanBusUpgrade)
            {
                stats.AddBusUpgradeRejection(
                    m_RoadUpgradeMatcher.BuildBusUpgradeRejectSample(
                        candidatePrefab,
                        candidateName,
                        candidateIsSourcePrefab,
                        candidateProfile,
                        defaultLaneMatch,
                        busUpgradeDetail),
                    6);
            }

            if (candidateLooksLikeRoadBuilder)
            {
                stats.AddRoadBuilderBusUpgradeSample(
                    m_RoadUpgradeMatcher.BuildRoadBuilderBusUpgradeSample(
                        candidatePrefab,
                        candidateName,
                        candidateIsSourcePrefab,
                        candidateProfile,
                        defaultLaneMatch,
                        busUpgradeDetail),
                    16);
            }

            return defaultLaneMatch;
        }

        private static void CopyTargetTramProfile(
            RoadLaneProfile targetLayoutProfile,
            ref CandidateLaneMatch match)
        {
            match.TargetIndependentTramCounts = targetLayoutProfile.IndependentTramCounts;
            match.TargetPublicTransportTramCounts = targetLayoutProfile.PublicTransportTramCounts;
            match.TargetTramTrackCounts = targetLayoutProfile.TramTrackCounts;
            match.TargetHasIndependentTram = !match.TargetIndependentTramCounts.IsEmpty;
            match.TargetHasPublicTransportTram = !match.TargetPublicTransportTramCounts.IsEmpty;
        }

    }
}
