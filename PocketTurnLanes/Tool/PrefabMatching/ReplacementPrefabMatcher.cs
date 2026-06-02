using System;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static PocketTurnLanes.Tool.PrefabMatching.RoadLaneCountMatcher;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal sealed class ReplacementPrefabMatcher
    {
        private const float PrefabWidthTolerance = 0.05f;
        private const int TramUpgradeFallbackPenalty = 20000;
        private const int IndependentTramTargetPreference = 1000;
        private const int PublicTransportTramTargetPenalty = 500;
        private const int OtherTramTargetPenalty = 1000;
        private const int MissingTramTargetPenalty = 50000;

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

            return GetPrefabNameFromPrefab(prefabRef.m_Prefab);
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

        internal bool TryFindPocketLaneReplacementPrefab(
            Entity nodeEntity,
            Entity edgeEntity,
            out ReplacementPrefabMatch match)
        {
            match = default;

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
            RoadLaneCounts desiredCounts = originalCounts;
            if (nodeIsEnd)
            {
                desiredCounts.Forward++;
            }
            else
            {
                desiredCounts.Backward++;
            }

            bool found = false;
            ReplacementSearchStats stats = ReplacementSearchStats.Create();
            int bestScore = int.MaxValue;
            ReplacementPrefabMatch bestMatch = default;
            bool sourceHasTramTracks = !sourceProfile.TramTrackCounts.IsEmpty;
            bool sourceHasIndependentTram = !sourceProfile.IndependentTramCounts.IsEmpty;
            bool sourceHasUpgraded = EntityManager.TryGetComponent(edgeEntity, out Upgraded sourceUpgraded);
            CompositionFlags sourceTramUpgradeFlags = sourceHasUpgraded
                ? ReplacementRoadUpgradeMatcher.GetTramTrackUpgradeFlags(sourceUpgraded.m_Flags)
                : default;
            RoadLaneCounts originalEffectiveCounts = RoadLaneCounts.Add(originalCounts, sourceProfile.IndependentTramCounts);
            RoadLaneCounts desiredEffectiveCounts = RoadLaneCounts.Add(desiredCounts, sourceProfile.IndependentTramCounts);

            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity candidatePrefab = prefabEntities[i];
                    stats.Scanned++;
                    bool candidateIsSourcePrefab = candidatePrefab == sourcePrefabRef.m_Prefab;
                    string candidateName = GetPrefabNameFromPrefab(candidatePrefab);
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
                        continue;
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
                                continue;
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
                        continue;
                    }

                    if (RoadPrefabEligibility.IsHighwayRoadData(candidateRoadData))
                    {
                        stats.HighwayExcluded++;
                        continue;
                    }

                    if (!EntityManager.TryGetComponent(candidatePrefab, out NetGeometryData candidateGeometry))
                    {
                        continue;
                    }

                    if (math.abs(candidateGeometry.m_DefaultWidth - sourceGeometry.m_DefaultWidth) > PrefabWidthTolerance)
                    {
                        continue;
                    }

                    stats.WidthMatches++;
                    if (!TryGetDefaultRoadLaneProfile(
                            candidatePrefab,
                            out RoadLaneProfile candidateProfile))
                    {
                        stats.MissingLaneData++;
                        if (sourceHasTramTracks || sourceProfile.BusLaneLayout.HasAny)
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

                        continue;
                    }

                    if (sourceHasTramTracks || sourceProfile.BusLaneLayout.HasAny)
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
                        continue;
                    }

                    m_RoadPrefabEligibility.GetRoadContentProfile(candidatePrefab, out bool candidateIsDlc, out string candidateContentDetail);
                    if (!sourceIsDlc && candidateIsDlc)
                    {
                        stats.DlcBlocked++;
                        continue;
                    }

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

                    EntityManager.TryGetComponent(candidatePrefab, out NetData candidateNetData);
                    int score = ReplacementPrefabScoring.GetReplacementPrefabScore(
                        sourceRoadData,
                        sourceNetData,
                        sourceGeometry,
                        candidateRoadData,
                        candidateNetData,
                        candidateGeometry,
                        candidateMatch.Invert,
                        sourceIsDlc,
                        candidateIsDlc);
                    int layoutScore = ReplacementPrefabScoring.GetReplacementLayoutScore(
                        sourceProfile,
                        candidateMatch.TargetLayoutProfile,
                        candidateMatch.Invert,
                        out int tramLayoutScore,
                        out int busLayoutScore,
                        out string layoutScoreDetail,
                        out DirectionalLaneOffsetProfile orientedTargetTramLayout,
                        out DirectionalLaneOffsetProfile orientedTargetBusLayout);
                    score += layoutScore;
                    if (sourceProfile.TramTrackLayout.HasAny ||
                        sourceProfile.BusLaneLayout.HasAny)
                    {
                        stats.LayoutScored++;
                    }

                    if (sourceProfile.BusLaneLayout.HasAny &&
                        orientedTargetBusLayout.HasAny)
                    {
                        stats.RecordBusLayoutCandidate(
                            score,
                            $"candidate={candidateName} orientation={(candidateMatch.Invert ? "reversed" : "direct")} score={score} candidateRoad={candidateProfile.RoadCounts} targetSource={candidateMatch.TargetLayoutProfile.Source} targetBus={orientedTargetBusLayout} targetBusDetail={candidateMatch.TargetLayoutProfile.BusLaneDetail} targetPublicTransportTram={candidateMatch.TargetPublicTransportTramCounts} targetUpgrade={(candidateMatch.HasTargetUpgrade ? candidateMatch.TargetUpgrade.m_Flags.ToString() : "none")}");
                    }

                    if (candidateMatch.TargetUsesTramUpgradeFallback)
                    {
                        score += TramUpgradeFallbackPenalty;
                    }

                    if (sourceHasTramTracks)
                    {
                        RoadLaneCounts requiredTramCounts = sourceHasIndependentTram
                            ? sourceProfile.IndependentTramCounts
                            : sourceProfile.TramTrackCounts;
                        if (!candidateMatch.TargetHasTramTrackMatch)
                        {
                            score += MissingTramTargetPenalty;
                        }
                        else if (candidateMatch.TargetHasIndependentTram &&
                                 CountsMatchForOrientation(candidateMatch.TargetIndependentTramCounts, requiredTramCounts, candidateMatch.Invert))
                        {
                            score -= IndependentTramTargetPreference;
                        }
                        else if (candidateMatch.TargetHasPublicTransportTram &&
                                 CountsMatchForOrientation(candidateMatch.TargetPublicTransportTramCounts, requiredTramCounts, candidateMatch.Invert))
                        {
                            score += PublicTransportTramTargetPenalty;
                        }
                        else
                        {
                            score += OtherTramTargetPenalty;
                        }
                    }

                    if (!found || score < bestScore)
                    {
                        found = true;
                        bestScore = score;
                        bestMatch = new ReplacementPrefabMatch
                        {
                            Prefab = candidatePrefab,
                            Invert = candidateMatch.Invert,
                            OriginalCounts = originalCounts,
                            TargetCounts = desiredCounts,
                            CandidateCounts = candidateProfile.RoadCounts,
                            OriginalEffectiveCounts = originalEffectiveCounts,
                            TargetEffectiveCounts = candidateMatch.TargetEffectiveCounts,
                            SourceIndependentTramCounts = sourceProfile.IndependentTramCounts,
                            TargetIndependentTramCounts = candidateMatch.TargetIndependentTramCounts,
                            SourcePublicTransportTramCounts = sourceProfile.PublicTransportTramCounts,
                            TargetPublicTransportTramCounts = candidateMatch.TargetPublicTransportTramCounts,
                            SourceTramTrackCounts = sourceProfile.TramTrackCounts,
                            TargetTramTrackCounts = candidateMatch.TargetTramTrackCounts,
                            TargetHasIndependentTram = candidateMatch.TargetHasIndependentTram,
                            TargetHasPublicTransportTram = candidateMatch.TargetHasPublicTransportTram,
                            TargetUsesTramUpgradeFallback = candidateMatch.TargetUsesTramUpgradeFallback,
                            HasTargetUpgrade = candidateMatch.HasTargetUpgrade,
                            TargetUpgrade = candidateMatch.TargetUpgrade,
                            TramMatchDetail = candidateMatch.TramMatchDetail,
                            SourceTramTrackLayout = sourceProfile.TramTrackLayout.ToString(),
                            TargetTramTrackLayout = orientedTargetTramLayout.ToString(),
                            SourceBusLaneLayout = sourceProfile.BusLaneLayout.ToString(),
                            TargetBusLaneLayout = orientedTargetBusLayout.ToString(),
                            TargetTramTrackOffsetProfile = orientedTargetTramLayout,
                            TargetBusLaneOffsetProfile = orientedTargetBusLayout,
                            SourceBusLaneDetail = sourceProfile.BusLaneDetail,
                            TargetBusLaneDetail = candidateMatch.TargetLayoutProfile.BusLaneDetail,
                            LayoutScoreDetail = layoutScoreDetail,
                            LayoutScore = layoutScore,
                            TramLayoutScore = tramLayoutScore,
                            BusLayoutScore = busLayoutScore,
                            TargetIsSourcePrefab = candidateIsSourcePrefab,
                            TargetIsDlc = candidateIsDlc,
                            TargetContentDetail = candidateContentDetail,
                            Score = score
                        };
                    }
                }
            }

            if (!found)
            {
                Mod.LogDiagnostic($"[IntersectionTool] No pocket lane replacement prefab found sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={sourceProfile.IndependentTramCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} sourcePublicTransportTramDetail={sourceProfile.PublicTransportTramDetail} sourceTramTracks={sourceProfile.TramTrackCounts} sourceTramTrackLayout={sourceProfile.TramTrackLayout} sourceTramDetail={sourceProfile.TramTrackDetail} sourceHasUpgraded={sourceHasUpgraded} sourceTramUpgradeFlags={sourceTramUpgradeFlags} sourceBusLayout={sourceProfile.BusLaneLayout} sourceBusDetail={sourceProfile.BusLaneDetail} originalLanes={originalCounts} desiredLanes={desiredCounts} originalEffectiveLanes={originalEffectiveCounts} desiredEffectiveLanes={desiredEffectiveCounts} width={sourceGeometry.m_DefaultWidth:0.##}m scanned={stats.Scanned} bridgeQueryExcluded=True highwayExcluded={stats.HighwayExcluded} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} widthCandidateSample={stats.WidthCandidateSample} roadBuilderCandidateSample={stats.RoadBuilderCandidateSample} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderDiscardedSample={stats.RoadBuilderDiscardedSample} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderNotInPlaysetSample={stats.RoadBuilderNotInPlaysetSample} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} roadBuilderVisibilityUnknownSample={stats.RoadBuilderVisibilityUnknownSample} parkingExcluded={stats.ParkingExcluded} independentTramCandidates={stats.IndependentTramCandidates} publicTransportTramCandidates={stats.PublicTransportTramCandidates} tramUpgradeCandidates={stats.TramUpgradeCandidates} tramUpgradeRejected={stats.TramUpgradeRejected} tramUpgradeRejectSample={stats.TramUpgradeRejectSample} busUpgradeCandidates={stats.BusUpgradeCandidates} busUpgradeRejected={stats.BusUpgradeRejected} busUpgradeRejectSample={stats.BusUpgradeRejectSample} roadBuilderBusUpgradeSample={stats.RoadBuilderBusUpgradeSample} layoutScored={stats.LayoutScored} busLayoutCandidates={stats.BusLayoutCandidates} bestBusLayoutCandidate={stats.BestBusLayoutCandidateDetail} sourcePrefabLaneMatches={stats.SourcePrefabLaneMatches} laneMatches=0 missingLaneData={stats.MissingLaneData}.");
                return false;
            }

            match = bestMatch;
            Mod.LogDiagnostic($"[IntersectionTool] Replacement prefab selected sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} targetPrefab={GetPrefabNameFromPrefab(match.Prefab)} targetIsSourcePrefab={match.TargetIsSourcePrefab} targetDlc={match.TargetIsDlc} targetContent={match.TargetContentDetail} orientation={(match.Invert ? "reversed" : "direct")} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={match.SourceIndependentTramCounts} targetIndependentTram={match.TargetIndependentTramCounts} sourcePublicTransportTram={match.SourcePublicTransportTramCounts} targetPublicTransportTram={match.TargetPublicTransportTramCounts} sourceTramTracks={match.SourceTramTrackCounts} targetTramTracks={match.TargetTramTrackCounts} targetHasIndependentTram={match.TargetHasIndependentTram} targetHasPublicTransportTram={match.TargetHasPublicTransportTram} tramUpgradeFallback={match.TargetUsesTramUpgradeFallback} targetUpgrade={(match.HasTargetUpgrade ? match.TargetUpgrade.m_Flags.ToString() : "none")} tramMatch={match.TramMatchDetail} sourceTramTrackLayout={match.SourceTramTrackLayout} targetTramTrackLayout={match.TargetTramTrackLayout} sourceBusLayout={match.SourceBusLaneLayout} sourceBusDetail={match.SourceBusLaneDetail} targetBusLayout={match.TargetBusLaneLayout} targetBusDetail={match.TargetBusLaneDetail} layoutScore={match.LayoutScore} tramLayoutScore={match.TramLayoutScore} busLayoutScore={match.BusLayoutScore} layoutDetail={match.LayoutScoreDetail} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={match.OriginalCounts} desiredLanes={match.TargetCounts} originalEffectiveLanes={match.OriginalEffectiveCounts} desiredEffectiveLanes={match.TargetEffectiveCounts} candidateLanes={match.CandidateCounts} scanned={stats.Scanned} bridgeQueryExcluded=True highwayExcluded={stats.HighwayExcluded} dlcBlocked={stats.DlcBlocked} widthMatches={stats.WidthMatches} widthCandidateSample={stats.WidthCandidateSample} roadBuilderCandidateSample={stats.RoadBuilderCandidateSample} roadBuilderDiscarded={stats.RoadBuilderDiscarded} roadBuilderDiscardedSample={stats.RoadBuilderDiscardedSample} roadBuilderNotInPlaysetExcluded={stats.RoadBuilderNotInPlaysetExcluded} roadBuilderNotInPlaysetSample={stats.RoadBuilderNotInPlaysetSample} roadBuilderVisibilityUnknown={stats.RoadBuilderVisibilityUnknown} roadBuilderVisibilityUnknownSample={stats.RoadBuilderVisibilityUnknownSample} parkingExcluded={stats.ParkingExcluded} independentTramCandidates={stats.IndependentTramCandidates} publicTransportTramCandidates={stats.PublicTransportTramCandidates} tramUpgradeCandidates={stats.TramUpgradeCandidates} tramUpgradeRejected={stats.TramUpgradeRejected} busUpgradeCandidates={stats.BusUpgradeCandidates} busUpgradeRejected={stats.BusUpgradeRejected} busUpgradeRejectSample={stats.BusUpgradeRejectSample} roadBuilderBusUpgradeSample={stats.RoadBuilderBusUpgradeSample} layoutScored={stats.LayoutScored} busLayoutCandidates={stats.BusLayoutCandidates} bestBusLayoutCandidate={stats.BestBusLayoutCandidateDetail} sourcePrefabLaneMatches={stats.SourcePrefabLaneMatches} laneMatches={stats.LaneMatches} missingLaneData={stats.MissingLaneData} score={match.Score}.");
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
                }
                else
                {
                    if (shouldScanBusUpgrade)
                    {
                        stats.AddBusUpgradeRejection(
                            $"candidate={candidateName} candidateEntity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} candidateRoad={candidateProfile.RoadCounts} candidateBusLayout={candidateProfile.BusLaneLayout} candidateSource={candidateProfile.Source} defaultLaneMatch={defaultLaneMatch} {busUpgradeDetail}",
                            6);
                    }

                    if (candidateLooksLikeRoadBuilder)
                    {
                        stats.AddRoadBuilderBusUpgradeSample(
                            $"candidate={candidateName} entity={FormatEntity(candidatePrefab)} isSource={candidateIsSourcePrefab} defaultLaneMatch={defaultLaneMatch} candidateRoad={candidateProfile.RoadCounts} candidateBusLayout={candidateProfile.BusLaneLayout} {busUpgradeDetail}",
                            16);
                    }

                    if (!defaultLaneMatch)
                    {
                        return false;
                    }
                }
            }

            match.Invert = invert;
            return true;
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
