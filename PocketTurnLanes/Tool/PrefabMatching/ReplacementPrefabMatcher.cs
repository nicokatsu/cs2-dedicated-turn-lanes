using System;
using Colossal.Entities;
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
        private readonly EntityQuery m_RoadPrefabQuery;
        private readonly RoadPrefabEligibility m_RoadPrefabEligibility;
        private readonly RoadBuilderPrefabSemantics m_RoadBuilderPrefabSemantics;
        private readonly RoadLaneProfileBuilder m_RoadLaneProfileBuilder;
        private readonly ReplacementRoadUpgradeMatcher m_RoadUpgradeMatcher;

        internal ReplacementPrefabMatcher(
            EntityManager entityManager,
            PrefabSystem prefabSystem,
            EntityQuery roadPrefabQuery,
            Func<BufferLookup<NetSubSection>> getNetSubSectionLookup,
            Func<BufferLookup<NetSectionPiece>> getNetSectionPieceLookup,
            Func<ComponentLookup<NetLaneData>> getNetLaneDataLookup,
            Func<BufferLookup<NetPieceLane>> getNetPieceLaneLookup)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
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
                Mod.LogDiagnostic($"[IntersectionTool] No pocket lane replacement prefab found sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={sourceProfile.IndependentTramCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} sourcePublicTransportTramDetail={sourceProfile.PublicTransportTramDetail} sourceTramTracks={sourceProfile.TramTrackCounts} sourceTramTrackLayout={sourceProfile.TramTrackLayout} sourceTramDetail={sourceProfile.TramTrackDetail} sourceHasUpgraded={sourceHasUpgraded} sourceTramUpgradeFlags={sourceTramUpgradeFlags} sourceBusLayout={sourceProfile.BusLaneLayout} sourceBusDetail={sourceProfile.BusLaneDetail} originalLanes={originalCounts} desiredLanes={desiredCounts} originalEffectiveLanes={originalEffectiveCounts} desiredEffectiveLanes={desiredEffectiveCounts} width={sourceGeometry.m_DefaultWidth:0.##}m scanned={stats.Scanned} bridgeQueryExcluded=True highwayExcluded={stats.HighwayExcluded} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} widthCandidateSample={stats.WidthCandidateSample} roadBuilderCandidateSample={stats.RoadBuilderCandidateSample} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderDiscardedSample={stats.RoadBuilderDiscardedSample} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderNotInPlaysetSample={stats.RoadBuilderNotInPlaysetSample} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} roadBuilderVisibilityUnknownSample={stats.RoadBuilderVisibilityUnknownSample} parkingExcluded={stats.ParkingExcluded} independentTramCandidates={stats.IndependentTramCandidates} publicTransportTramCandidates={stats.PublicTransportTramCandidates} tramUpgradeCandidates={stats.TramUpgradeCandidates} tramUpgradeRejected={stats.TramUpgradeRejected} tramUpgradeRejectSample={stats.TramUpgradeRejectSample} busUpgradeCandidates={stats.BusUpgradeCandidates} busUpgradeRejected={stats.BusUpgradeRejected} busUpgradeRejectSample={stats.BusUpgradeRejectSample} roadBuilderBusUpgradeSample={stats.RoadBuilderBusUpgradeSample} layoutScored={stats.LayoutScored} busLayoutCandidates={stats.BusLayoutCandidates} bestBusLayoutCandidate={stats.BestBusLayoutCandidateDetail} sourcePrefabLaneMatches={stats.SourcePrefabLaneMatches} laneMatches=0 missingLaneData={stats.MissingLaneData}.");
                return false;
            }

            match = bestMatch;
            Mod.LogDiagnostic($"[IntersectionTool] Replacement prefab selected sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} targetPrefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, match.Prefab)} targetIsSourcePrefab={match.TargetIsSourcePrefab} targetDlc={match.TargetIsDlc} targetContent={match.TargetContentDetail} orientation={(match.Invert ? "reversed" : "direct")} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={match.SourceIndependentTramCounts} targetIndependentTram={match.TargetIndependentTramCounts} sourcePublicTransportTram={match.SourcePublicTransportTramCounts} targetPublicTransportTram={match.TargetPublicTransportTramCounts} sourceTramTracks={match.SourceTramTrackCounts} targetTramTracks={match.TargetTramTrackCounts} targetHasIndependentTram={match.TargetHasIndependentTram} targetHasPublicTransportTram={match.TargetHasPublicTransportTram} tramUpgradeFallback={match.TargetUsesTramUpgradeFallback} targetUpgrade={(match.HasTargetUpgrade ? match.TargetUpgrade.m_Flags.ToString() : "none")} tramMatch={match.TramMatchDetail} sourceTramTrackLayout={match.SourceTramTrackLayout} targetTramTrackLayout={match.TargetTramTrackLayout} sourceBusLayout={match.SourceBusLaneLayout} sourceBusDetail={match.SourceBusLaneDetail} targetBusLayout={match.TargetBusLaneLayout} targetBusDetail={match.TargetBusLaneDetail} layoutScore={match.LayoutScore} tramLayoutScore={match.TramLayoutScore} busLayoutScore={match.BusLayoutScore} layoutDetail={match.LayoutScoreDetail} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={match.OriginalCounts} desiredLanes={match.TargetCounts} originalEffectiveLanes={match.OriginalEffectiveCounts} desiredEffectiveLanes={match.TargetEffectiveCounts} candidateLanes={match.CandidateCounts} scanned={stats.Scanned} bridgeQueryExcluded=True highwayExcluded={stats.HighwayExcluded} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} widthCandidateSample={stats.WidthCandidateSample} roadBuilderCandidateSample={stats.RoadBuilderCandidateSample} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderDiscardedSample={stats.RoadBuilderDiscardedSample} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderNotInPlaysetSample={stats.RoadBuilderNotInPlaysetSample} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} roadBuilderVisibilityUnknownSample={stats.RoadBuilderVisibilityUnknownSample} parkingExcluded={stats.ParkingExcluded} independentTramCandidates={stats.IndependentTramCandidates} publicTransportTramCandidates={stats.PublicTransportTramCandidates} tramUpgradeCandidates={stats.TramUpgradeCandidates} tramUpgradeRejected={stats.TramUpgradeRejected} busUpgradeCandidates={stats.BusUpgradeCandidates} busUpgradeRejected={stats.BusUpgradeRejected} busUpgradeRejectSample={stats.BusUpgradeRejectSample} roadBuilderBusUpgradeSample={stats.RoadBuilderBusUpgradeSample} layoutScored={stats.LayoutScored} busLayoutCandidates={stats.BusLayoutCandidates} bestBusLayoutCandidate={stats.BestBusLayoutCandidateDetail} sourcePrefabLaneMatches={stats.SourcePrefabLaneMatches} laneMatches={stats.LaneMatches} missingLaneData={stats.MissingLaneData} score={match.Score}.");
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
            RoadLaneCounts desiredCounts = GetDesiredPocketLaneCounts(originalCounts, nodeIsStart);

            bool sourceHasUpgraded = EntityManager.TryGetComponent(edgeEntity, out Upgraded sourceUpgraded);
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
                TramUpgradeFlags = sourceHasUpgraded
                    ? ReplacementRoadUpgradeMatcher.GetTramTrackUpgradeFlags(sourceUpgraded.m_Flags)
                    : default,
                OriginalEffectiveCounts = RoadLaneCounts.Add(originalCounts, sourceProfile.IndependentTramCounts),
                DesiredEffectiveCounts = RoadLaneCounts.Add(desiredCounts, sourceProfile.IndependentTramCounts)
            };
            return true;
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
