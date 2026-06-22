using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game.SceneFlow;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using static PocketTurnLanes.Tool.PrefabMatching.RoadLaneCountMatcher;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal sealed partial class ReplacementPrefabMatcher
    {
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

            mandatoryFeatures = CustomRoadAssetSourceFeatureClassifier.GetMandatoryFeatures(profile);
            detail = $"sourcePrefabName={sourcePrefabName} mandatorySourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(mandatoryFeatures)} profileMandatorySourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(profile.MandatorySourceFeatures)} independentTram={profile.IndependentTramCounts} publicTransportTram={profile.PublicTransportTramCounts} dedicatedPublicTransport={profile.DedicatedPublicTransportLaneLayout} tramTracks={profile.TramTrackCounts} busLayout={profile.BusLaneLayout} profileSource={profile.Source}";
            return true;
        }

        private CustomRoadAssetSourceFeatures GetAvailableCustomRoadAssetSourceFeatures(
            Entity prefabEntity,
            RoadLaneProfile profile)
        {
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

            return CustomRoadAssetSourceFeatureClassifier.GetAvailableFeatures(
                profile,
                nativeUpgradeFeatures,
                roadBuilderFeatures);
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

            if (CustomRoadAssetSourceFeatureClassifier.HasPublicTransport(profile))
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
            return CustomRoadAssetSourceFeatureClassifier.GetRuntimeFeatures(
                source.Profile,
                source.OriginalCounts,
                source.NodeIsStart);
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
            bool isReverseSide = CustomRoadAssetSourceFeatureClassifier.IsReverseAsymmetricSourceSide(
                originalCounts,
                nodeIsStart);
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
                CustomRoadAssetSourceFeatureClassifier.GetMandatoryFeatures(profile);
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
                ForwardRoadLanes = profile.RoadCounts.Forward,
                BackwardRoadLanes = profile.RoadCounts.Backward,
                TotalRoadLanes = profile.RoadCounts.Forward + profile.RoadCounts.Backward,
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
            options.Sort((left, right) =>
            {
                int laneCompare = left.TotalRoadLanes.CompareTo(right.TotalRoadLanes);
                if (laneCompare != 0)
                {
                    return laneCompare;
                }

                return string.Compare(
                    left.DisplayName,
                    right.DisplayName,
                    StringComparison.OrdinalIgnoreCase);
            });

            if (maxCount > 0 && options.Count > maxCount)
            {
                options.RemoveRange(maxCount, options.Count - maxCount);
            }
        }

        private static string FormatCustomRuleStats(ReplacementSearchStats stats)
        {
            return $"lockedExcluded={stats.LockedExcluded} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} parkingExcluded={stats.ParkingExcluded} highwayExcluded={stats.HighwayExcluded} missingLaneData={stats.MissingLaneData} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} tramUpgradeRejected={stats.TramUpgradeRejected} busUpgradeRejected={stats.BusUpgradeRejected} laneMatches={stats.LaneMatches}";
        }

        private struct SourceTargetAvailability
        {
            public bool HasForwardTargetCandidates;
            public bool HasReverseTargetCandidates;

            public bool HasAnyTargetCandidates => HasForwardTargetCandidates || HasReverseTargetCandidates;
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
    }
}
