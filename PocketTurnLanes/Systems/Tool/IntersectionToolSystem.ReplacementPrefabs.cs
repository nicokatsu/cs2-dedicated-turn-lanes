using System;
using Colossal.Entities;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class IntersectionToolSystem
    {
        private bool TryFindPocketLaneReplacementPrefab(
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
                Mod.log.Warn($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)}: missing edge or source prefab data.");
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            if (!nodeIsStart && !nodeIsEnd)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)}: node={FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            GetRoadContentProfile(sourcePrefabRef.m_Prefab, out bool sourceIsDlc, out string sourceContentDetail);
            if (IsBridgeRoadPrefab(sourcePrefabRef.m_Prefab, out string sourceBridgeDetail))
            {
                Mod.log.Info($"[IntersectionTool] Skip replacement prefab search sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail}: source road prefab is a bridge and bridge roads are excluded from selection and replacement matching. {sourceBridgeDetail}");
                return false;
            }

            if (!TryGetRoadLaneProfile(
                    edgeEntity,
                    sourcePrefabRef.m_Prefab,
                    out RoadLaneProfile sourceProfile))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: no default road lane counts were found.");
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
            int scannedCount = 0;
            int dlcBlockedCount = 0;
            int widthMatchCount = 0;
            int parkingExcludedCount = 0;
            int laneMatchCount = 0;
            int missingLaneCount = 0;
            int independentTramCandidateCount = 0;
            int publicTransportTramCandidateCount = 0;
            int tramUpgradeCandidateCount = 0;
            int tramUpgradeRejectedCount = 0;
            int layoutScoredCount = 0;
            string tramUpgradeRejectSample = "<none>";
            int bestScore = int.MaxValue;
            ReplacementPrefabMatch bestMatch = default;
            bool sourceHasTramTracks = !sourceProfile.TramTrackCounts.IsEmpty;
            bool sourceHasIndependentTram = !sourceProfile.IndependentTramCounts.IsEmpty;
            bool sourceHasUpgraded = EntityManager.TryGetComponent(edgeEntity, out Upgraded sourceUpgraded);
            CompositionFlags sourceTramUpgradeFlags = sourceHasUpgraded
                ? GetTramTrackUpgradeFlags(sourceUpgraded.m_Flags)
                : default;
            RoadLaneCounts originalEffectiveCounts = RoadLaneCounts.Add(originalCounts, sourceProfile.IndependentTramCounts);
            RoadLaneCounts desiredEffectiveCounts = RoadLaneCounts.Add(desiredCounts, sourceProfile.IndependentTramCounts);

            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity candidatePrefab = prefabEntities[i];
                    scannedCount++;

                    if (candidatePrefab == sourcePrefabRef.m_Prefab ||
                        !EntityManager.TryGetComponent(candidatePrefab, out NetGeometryData candidateGeometry))
                    {
                        continue;
                    }

                    if (math.abs(candidateGeometry.m_DefaultWidth - sourceGeometry.m_DefaultWidth) > PrefabWidthTolerance)
                    {
                        continue;
                    }

                    widthMatchCount++;
                    if (!TryGetDefaultRoadLaneProfile(
                            candidatePrefab,
                            out RoadLaneProfile candidateProfile))
                    {
                        missingLaneCount++;
                        continue;
                    }

                    if (candidateProfile.HasMarkedParking)
                    {
                        parkingExcludedCount++;
                        continue;
                    }

                    GetRoadContentProfile(candidatePrefab, out bool candidateIsDlc, out string candidateContentDetail);
                    if (!sourceIsDlc && candidateIsDlc)
                    {
                        dlcBlockedCount++;
                        continue;
                    }

                    bool invert;
                    RoadLaneCounts targetCounts = desiredCounts;
                    RoadLaneCounts targetEffectiveCounts = desiredCounts;
                    bool targetUsesTramUpgradeFallback = false;
                    RoadLaneCounts targetIndependentTramCounts = candidateProfile.IndependentTramCounts;
                    RoadLaneCounts targetPublicTransportTramCounts = candidateProfile.PublicTransportTramCounts;
                    RoadLaneCounts targetTramTrackCounts = candidateProfile.TramTrackCounts;
                    bool targetHasIndependentTram = !targetIndependentTramCounts.IsEmpty;
                    bool targetHasPublicTransportTram = !targetPublicTransportTramCounts.IsEmpty;
                    bool targetHasTramTrackMatch = !sourceHasTramTracks;
                    bool hasTargetUpgrade = false;
                    Upgraded targetUpgrade = default;
                    RoadLaneProfile targetLayoutProfile = candidateProfile;
                    string tramMatchDetail = sourceHasTramTracks
                        ? $"sourceIndependentTram={sourceProfile.IndependentTramCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} candidateIndependentTram={candidateProfile.IndependentTramCounts} candidatePublicTransportTram={candidateProfile.PublicTransportTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.TramTrackDetail}"
                        : "sourceTramTracks=none";

                    if (sourceHasIndependentTram)
                    {
                        if (targetHasIndependentTram)
                        {
                            independentTramCandidateCount++;
                        }

                        if (targetHasPublicTransportTram)
                        {
                            publicTransportTramCandidateCount++;
                        }

                        if (targetHasIndependentTram &&
                            RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredCounts, out invert) &&
                            CountsMatchForOrientation(candidateProfile.IndependentTramCounts, sourceProfile.IndependentTramCounts, invert))
                        {
                            targetEffectiveCounts = RoadLaneCounts.Add(desiredCounts, sourceProfile.IndependentTramCounts);
                            targetHasTramTrackMatch = true;
                            tramMatchDetail = $"mode=independent-tram sourceIndependentTram={sourceProfile.IndependentTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} candidateIndependentTram={candidateProfile.IndependentTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.IndependentTramDetail}";
                        }
                        else if (targetHasPublicTransportTram &&
                                 RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredEffectiveCounts, out invert) &&
                                 CountsMatchForOrientation(candidateProfile.PublicTransportTramCounts, sourceProfile.IndependentTramCounts, invert))
                        {
                            targetEffectiveCounts = desiredEffectiveCounts;
                            targetHasTramTrackMatch = true;
                            tramMatchDetail = $"mode=public-transport-tram sourceIndependentTram={sourceProfile.IndependentTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} candidatePublicTransportTram={candidateProfile.PublicTransportTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.PublicTransportTramDetail}";
                        }
                        else
                        {
                            if (!RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredEffectiveCounts, out invert))
                            {
                                continue;
                            }

                            if (!TryFindMatchingTramUpgrade(
                                    candidatePrefab,
                                    candidateProfile,
                                    sourceProfile,
                                    desiredCounts,
                                    desiredEffectiveCounts,
                                    sourceProfile.IndependentTramCounts,
                                    sourceTramUpgradeFlags,
                                    invert,
                                    out targetUpgrade,
                                    out targetLayoutProfile,
                                    out string tramUpgradeDetail))
                            {
                                tramUpgradeRejectedCount++;
                                if (tramUpgradeRejectSample == "<none>")
                                {
                                    tramUpgradeRejectSample = $"candidate={GetPrefabNameFromPrefab(candidatePrefab)} candidateRoad={candidateProfile.RoadCounts} candidateTramTracks={candidateProfile.TramTrackCounts} invert={invert} {tramUpgradeDetail}";
                                }

                                continue;
                            }

                            tramUpgradeCandidateCount++;
                            targetUsesTramUpgradeFallback = true;
                            hasTargetUpgrade = true;
                            targetEffectiveCounts = desiredEffectiveCounts;
                            targetIndependentTramCounts = targetLayoutProfile.IndependentTramCounts;
                            targetPublicTransportTramCounts = targetLayoutProfile.PublicTransportTramCounts;
                            targetTramTrackCounts = targetLayoutProfile.TramTrackCounts;
                            targetHasIndependentTram = !targetIndependentTramCounts.IsEmpty;
                            targetHasPublicTransportTram = !targetPublicTransportTramCounts.IsEmpty;
                            targetHasTramTrackMatch = true;
                            tramMatchDetail = $"mode=tram-upgrade-fallback sourceIndependentTram={sourceProfile.IndependentTramCounts} sourceTramTracks={sourceProfile.TramTrackCounts} adjustedOriginal={originalEffectiveCounts} adjustedDesired={desiredEffectiveCounts} targetUpgrade={targetUpgrade.m_Flags} {tramUpgradeDetail}";
                        }
                    }
                    else if (sourceHasTramTracks)
                    {
                        if (!RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredCounts, out invert))
                        {
                            continue;
                        }

                        bool hasIndependentTramMatch = targetHasIndependentTram &&
                                                       CountsMatchForOrientation(candidateProfile.IndependentTramCounts, sourceProfile.TramTrackCounts, invert);
                        bool hasPublicTransportTramMatch = targetHasPublicTransportTram &&
                                                           CountsMatchForOrientation(candidateProfile.PublicTransportTramCounts, sourceProfile.TramTrackCounts, invert);
                        bool hasAnyTramMatch = CountsMatchForOrientation(candidateProfile.TramTrackCounts, sourceProfile.TramTrackCounts, invert);

                        if (hasIndependentTramMatch)
                        {
                            independentTramCandidateCount++;
                            targetHasTramTrackMatch = true;
                            tramMatchDetail = $"mode=tram-default-independent sourceTramTracks={sourceProfile.TramTrackCounts} candidateIndependentTram={candidateProfile.IndependentTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.IndependentTramDetail}";
                        }
                        else if (hasPublicTransportTramMatch)
                        {
                            publicTransportTramCandidateCount++;
                            targetHasTramTrackMatch = true;
                            tramMatchDetail = $"mode=public-transport-tram sourceTramTracks={sourceProfile.TramTrackCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} candidatePublicTransportTram={candidateProfile.PublicTransportTramCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.PublicTransportTramDetail}";
                        }
                        else if (hasAnyTramMatch)
                        {
                            targetHasTramTrackMatch = true;
                            tramMatchDetail = $"mode=tram-default-embedded sourceTramTracks={sourceProfile.TramTrackCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.TramTrackDetail}";
                        }
                        else if (TryFindMatchingTramUpgrade(
                                     candidatePrefab,
                                     candidateProfile,
                                     sourceProfile,
                                     desiredCounts,
                                     desiredCounts,
                                     sourceProfile.TramTrackCounts,
                                     sourceTramUpgradeFlags,
                                     invert,
                                     out targetUpgrade,
                                     out targetLayoutProfile,
                                     out string tramUpgradeDetail))
                        {
                            tramUpgradeCandidateCount++;
                            targetUsesTramUpgradeFallback = true;
                            hasTargetUpgrade = true;
                            targetIndependentTramCounts = targetLayoutProfile.IndependentTramCounts;
                            targetPublicTransportTramCounts = targetLayoutProfile.PublicTransportTramCounts;
                            targetTramTrackCounts = targetLayoutProfile.TramTrackCounts;
                            targetHasIndependentTram = !targetIndependentTramCounts.IsEmpty;
                            targetHasPublicTransportTram = !targetPublicTransportTramCounts.IsEmpty;
                            targetHasTramTrackMatch = true;
                            tramMatchDetail = $"mode=tram-upgrade-preserve sourceTramTracks={sourceProfile.TramTrackCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} targetUpgrade={targetUpgrade.m_Flags} {tramUpgradeDetail}";
                        }
                        else
                        {
                            tramUpgradeRejectedCount++;
                            if (tramUpgradeRejectSample == "<none>")
                            {
                                tramUpgradeRejectSample = $"candidate={GetPrefabNameFromPrefab(candidatePrefab)} candidateRoad={candidateProfile.RoadCounts} candidateTramTracks={candidateProfile.TramTrackCounts} invert={invert} {tramUpgradeDetail}";
                            }

                            tramMatchDetail = $"mode=missing-tram-fallback sourceTramTracks={sourceProfile.TramTrackCounts} candidateTramTracks={candidateProfile.TramTrackCounts} candidateTramDetail={candidateProfile.TramTrackDetail} rejectedUpgrade={tramUpgradeDetail}";
                        }
                    }
                    else if (!RoadLaneCountMatcher.TryMatch(candidateProfile.RoadCounts, desiredCounts, out invert))
                    {
                        continue;
                    }

                    laneMatchCount++;
                    EntityManager.TryGetComponent(candidatePrefab, out RoadData candidateRoadData);
                    EntityManager.TryGetComponent(candidatePrefab, out NetData candidateNetData);
                    int score = GetReplacementPrefabScore(
                        sourceRoadData,
                        sourceNetData,
                        sourceGeometry,
                        candidateRoadData,
                        candidateNetData,
                        candidateGeometry,
                        invert,
                        sourceIsDlc,
                        candidateIsDlc);
                    int layoutScore = GetReplacementLayoutScore(
                        sourceProfile,
                        targetLayoutProfile,
                        invert,
                        out int tramLayoutScore,
                        out int busLayoutScore,
                        out string layoutScoreDetail,
                        out DirectionalLaneOffsetProfile orientedTargetTramLayout,
                        out DirectionalLaneOffsetProfile orientedTargetBusLayout);
                    score += layoutScore;
                    if (sourceProfile.TramTrackLayout.HasAny ||
                        sourceProfile.BusLaneLayout.HasAny)
                    {
                        layoutScoredCount++;
                    }

                    if (targetUsesTramUpgradeFallback)
                    {
                        score += TramUpgradeFallbackPenalty;
                    }

                    if (sourceHasTramTracks)
                    {
                        RoadLaneCounts requiredTramCounts = sourceHasIndependentTram
                            ? sourceProfile.IndependentTramCounts
                            : sourceProfile.TramTrackCounts;
                        if (!targetHasTramTrackMatch)
                        {
                            score += MissingTramTargetPenalty;
                        }
                        else if (targetHasIndependentTram &&
                                 CountsMatchForOrientation(targetIndependentTramCounts, requiredTramCounts, invert))
                        {
                            score -= IndependentTramTargetPreference;
                        }
                        else if (targetHasPublicTransportTram &&
                                 CountsMatchForOrientation(targetPublicTransportTramCounts, requiredTramCounts, invert))
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
                            Invert = invert,
                            OriginalCounts = originalCounts,
                            TargetCounts = desiredCounts,
                            CandidateCounts = candidateProfile.RoadCounts,
                            OriginalEffectiveCounts = originalEffectiveCounts,
                            TargetEffectiveCounts = targetEffectiveCounts,
                            SourceIndependentTramCounts = sourceProfile.IndependentTramCounts,
                            TargetIndependentTramCounts = targetIndependentTramCounts,
                            SourcePublicTransportTramCounts = sourceProfile.PublicTransportTramCounts,
                            TargetPublicTransportTramCounts = targetPublicTransportTramCounts,
                            SourceTramTrackCounts = sourceProfile.TramTrackCounts,
                            TargetTramTrackCounts = targetTramTrackCounts,
                            TargetHasIndependentTram = targetHasIndependentTram,
                            TargetHasPublicTransportTram = targetHasPublicTransportTram,
                            TargetUsesTramUpgradeFallback = targetUsesTramUpgradeFallback,
                            HasTargetUpgrade = hasTargetUpgrade,
                            TargetUpgrade = targetUpgrade,
                            TramMatchDetail = tramMatchDetail,
                            SourceTramTrackLayout = sourceProfile.TramTrackLayout.ToString(),
                            TargetTramTrackLayout = orientedTargetTramLayout.ToString(),
                            SourceBusLaneLayout = sourceProfile.BusLaneLayout.ToString(),
                            TargetBusLaneLayout = orientedTargetBusLayout.ToString(),
                            LayoutScoreDetail = layoutScoreDetail,
                            LayoutScore = layoutScore,
                            TramLayoutScore = tramLayoutScore,
                            BusLayoutScore = busLayoutScore,
                            TargetIsDlc = candidateIsDlc,
                            TargetContentDetail = candidateContentDetail,
                            Score = score
                        };
                    }
                }
            }

            if (!found)
            {
                Mod.log.Warn($"[IntersectionTool] No pocket lane replacement prefab found sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={sourceProfile.IndependentTramCounts} sourcePublicTransportTram={sourceProfile.PublicTransportTramCounts} sourcePublicTransportTramDetail={sourceProfile.PublicTransportTramDetail} sourceTramTracks={sourceProfile.TramTrackCounts} sourceTramTrackLayout={sourceProfile.TramTrackLayout} sourceTramDetail={sourceProfile.TramTrackDetail} sourceHasUpgraded={sourceHasUpgraded} sourceTramUpgradeFlags={sourceTramUpgradeFlags} sourceBusLayout={sourceProfile.BusLaneLayout} sourceBusDetail={sourceProfile.BusLaneDetail} originalLanes={originalCounts} desiredLanes={desiredCounts} originalEffectiveLanes={originalEffectiveCounts} desiredEffectiveLanes={desiredEffectiveCounts} width={sourceGeometry.m_DefaultWidth:0.##}m scanned={scannedCount} bridgeQueryExcluded=True dlcBlocked={dlcBlockedCount} widthMatches={widthMatchCount} parkingExcluded={parkingExcludedCount} independentTramCandidates={independentTramCandidateCount} publicTransportTramCandidates={publicTransportTramCandidateCount} tramUpgradeCandidates={tramUpgradeCandidateCount} tramUpgradeRejected={tramUpgradeRejectedCount} tramUpgradeRejectSample={tramUpgradeRejectSample} layoutScored={layoutScoredCount} laneMatches=0 missingLaneData={missingLaneCount}.");
                return false;
            }

            match = bestMatch;
            Mod.log.Info($"[IntersectionTool] Replacement prefab selected sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail} targetPrefab={GetPrefabNameFromPrefab(match.Prefab)} targetDlc={match.TargetIsDlc} targetContent={match.TargetContentDetail} orientation={(match.Invert ? "reversed" : "direct")} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={sourceProfile.Source} sourceMarkedParking={sourceProfile.HasMarkedParking} sourceMarkedParkingDetail={sourceProfile.MarkedParkingDetail} sourceIndependentTram={match.SourceIndependentTramCounts} targetIndependentTram={match.TargetIndependentTramCounts} sourcePublicTransportTram={match.SourcePublicTransportTramCounts} targetPublicTransportTram={match.TargetPublicTransportTramCounts} sourceTramTracks={match.SourceTramTrackCounts} targetTramTracks={match.TargetTramTrackCounts} targetHasIndependentTram={match.TargetHasIndependentTram} targetHasPublicTransportTram={match.TargetHasPublicTransportTram} tramUpgradeFallback={match.TargetUsesTramUpgradeFallback} targetUpgrade={(match.HasTargetUpgrade ? match.TargetUpgrade.m_Flags.ToString() : "none")} tramMatch={match.TramMatchDetail} sourceTramTrackLayout={match.SourceTramTrackLayout} targetTramTrackLayout={match.TargetTramTrackLayout} sourceBusLayout={match.SourceBusLaneLayout} targetBusLayout={match.TargetBusLaneLayout} layoutScore={match.LayoutScore} tramLayoutScore={match.TramLayoutScore} busLayoutScore={match.BusLayoutScore} layoutDetail={match.LayoutScoreDetail} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={match.OriginalCounts} desiredLanes={match.TargetCounts} originalEffectiveLanes={match.OriginalEffectiveCounts} desiredEffectiveLanes={match.TargetEffectiveCounts} candidateLanes={match.CandidateCounts} scanned={scannedCount} bridgeQueryExcluded=True dlcBlocked={dlcBlockedCount} widthMatches={widthMatchCount} parkingExcluded={parkingExcludedCount} independentTramCandidates={independentTramCandidateCount} publicTransportTramCandidates={publicTransportTramCandidateCount} tramUpgradeCandidates={tramUpgradeCandidateCount} tramUpgradeRejected={tramUpgradeRejectedCount} layoutScored={layoutScoredCount} laneMatches={laneMatchCount} missingLaneData={missingLaneCount} score={match.Score}.");
            return true;
        }

        private bool IsBridgeRoadEdge(Entity edgeEntity, out string detail)
        {
            if (edgeEntity == Entity.Null ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
            {
                detail = "prefabRef=missing";
                return false;
            }

            return IsBridgeRoadPrefab(prefabRef.m_Prefab, out detail);
        }

        private bool IsBridgeRoadPrefab(Entity prefabEntity, out string detail)
        {
            if (prefabEntity == Entity.Null)
            {
                detail = "prefab=<null> bridgeData=False";
                return false;
            }

            bool isBridge = EntityManager.HasComponent<BridgeData>(prefabEntity);
            detail = $"prefab={GetPrefabNameFromPrefab(prefabEntity)} prefabEntity={FormatEntity(prefabEntity)} bridgeData={isBridge}";
            return isBridge;
        }

        private void GetRoadContentProfile(Entity prefabEntity, out bool isDlc, out string detail)
        {
            isDlc = false;
            detail = "contentPrerequisite=<none> contentType=base";

            if (prefabEntity == Entity.Null ||
                !EntityManager.TryGetComponent(prefabEntity, out ContentPrerequisiteData prerequisiteData) ||
                prerequisiteData.m_ContentPrerequisite == Entity.Null)
            {
                return;
            }

            Entity contentEntity = prerequisiteData.m_ContentPrerequisite;
            string contentName = GetPrefabNameFromPrefab(contentEntity);
            string contentFlags = "<missing ContentData>";
            string dlcId = "<missing>";
            if (EntityManager.TryGetComponent(contentEntity, out ContentData contentData))
            {
                contentFlags = contentData.m_Flags.ToString();
                dlcId = contentData.m_DlcID.ToString();
                isDlc = (contentData.m_Flags & ContentFlags.RequireDlc) != 0;
            }

            detail = $"contentPrerequisite={contentName} contentEntity={FormatEntity(contentEntity)} contentFlags={contentFlags} dlcId={dlcId} contentType={(isDlc ? "dlc" : "base")}";
        }

        private struct RoadLaneProfile
        {
            public RoadLaneCounts RoadCounts;
            public RoadLaneCounts TramTrackCounts;
            public RoadLaneCounts IndependentTramCounts;
            public RoadLaneCounts PublicTransportTramCounts;
            public DirectionalLaneOffsetProfile TramTrackLayout;
            public DirectionalLaneOffsetProfile IndependentTramLayout;
            public DirectionalLaneOffsetProfile PublicTransportTramLayout;
            public DirectionalLaneOffsetProfile BusLaneLayout;
            public bool HasMarkedParking;
            public string MarkedParkingDetail;
            public string TramTrackDetail;
            public string IndependentTramDetail;
            public string PublicTransportTramDetail;
            public string BusLaneDetail;
            public string Source;
        }

        private static RoadLaneProfile CreateEmptyRoadLaneProfile(string source)
        {
            return new RoadLaneProfile
            {
                MarkedParkingDetail = "<none>",
                TramTrackDetail = "<none>",
                IndependentTramDetail = "<none>",
                PublicTransportTramDetail = "<none>",
                BusLaneDetail = "<none>",
                Source = source
            };
        }

        private struct DirectionalLaneOffsetProfile
        {
            public int ForwardCount;
            public int BackwardCount;
            public float ForwardOffsetSum;
            public float BackwardOffsetSum;

            public bool HasAny => ForwardCount > 0 || BackwardCount > 0;

            public DirectionalLaneOffsetProfile Oriented(bool invert)
            {
                if (!invert)
                {
                    return this;
                }

                return new DirectionalLaneOffsetProfile
                {
                    ForwardCount = BackwardCount,
                    ForwardOffsetSum = -BackwardOffsetSum,
                    BackwardCount = ForwardCount,
                    BackwardOffsetSum = -ForwardOffsetSum
                };
            }

            public override string ToString()
            {
                if (!HasAny)
                {
                    return "none";
                }

                string forward = ForwardCount > 0
                    ? $"{ForwardCount}@{ForwardOffsetSum / ForwardCount:0.##}m"
                    : "0";
                string backward = BackwardCount > 0
                    ? $"{BackwardCount}@{BackwardOffsetSum / BackwardCount:0.##}m"
                    : "0";
                return $"F{forward}/B{backward}";
            }
        }

        private bool TryGetRoadLaneProfile(
            Entity edgeEntity,
            Entity fallbackPrefab,
            out RoadLaneProfile profile)
        {
            if (EntityManager.TryGetComponent(edgeEntity, out Composition composition) &&
                TryGetCompositionRoadLaneProfile(
                    composition.m_Edge,
                    out profile))
            {
                profile.Source = $"Composition:{FormatEntity(composition.m_Edge)}";
                return true;
            }

            if (TryGetDefaultRoadLaneProfile(
                    fallbackPrefab,
                    out profile))
            {
                return true;
            }

            profile = CreateEmptyRoadLaneProfile("Missing");
            return false;
        }

        private bool TryGetCompositionRoadLaneProfile(
            Entity compositionEntity,
            out RoadLaneProfile profile)
        {
            profile = CreateEmptyRoadLaneProfile("Composition");
            if (compositionEntity == Entity.Null ||
                !EntityManager.TryGetBuffer(compositionEntity, true, out DynamicBuffer<NetCompositionLane> lanes))
            {
                return false;
            }

            for (int i = 0; i < lanes.Length; i++)
            {
                AccumulateLaneProfile(lanes[i].m_Flags, lanes[i].m_Lane, lanes[i].m_Position.x, ref profile);
            }

            return profile.RoadCounts.Total > 0;
        }

        private bool TryGetDefaultRoadLaneProfile(
            Entity prefabEntity,
            out RoadLaneProfile profile)
        {
            profile = CreateEmptyRoadLaneProfile("Missing");

            if (prefabEntity == Entity.Null)
            {
                return false;
            }

            if (EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<DefaultNetLane> lanes))
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    AccumulateLaneProfile(lanes[i].m_Flags, lanes[i].m_Lane, lanes[i].m_Position.x, ref profile);
                }

                if (profile.RoadCounts.Total > 0)
                {
                    profile.Source = "DefaultNetLane";
                    return true;
                }
            }

            if (TryGetCompositionLaneProfile(prefabEntity, default, out profile))
            {
                profile.Source = "NetGeometryComposition:default";
                return true;
            }

            if (TryCalculateDefaultRoadLaneProfile(prefabEntity, out profile))
            {
                profile.Source = "NetGeometrySection:calculated";
                return true;
            }

            profile = CreateEmptyRoadLaneProfile("Missing");
            return false;
        }

        private bool TryGetCompositionLaneProfile(
            Entity prefabEntity,
            CompositionFlags mask,
            out RoadLaneProfile profile)
        {
            profile = CreateEmptyRoadLaneProfile("NetGeometryComposition");

            if (!EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<NetGeometryComposition> compositions))
            {
                return false;
            }

            for (int i = 0; i < compositions.Length; i++)
            {
                NetGeometryComposition composition = compositions[i];
                if (composition.m_Mask != mask ||
                    !EntityManager.TryGetBuffer(composition.m_Composition, true, out DynamicBuffer<NetCompositionLane> lanes))
                {
                    continue;
                }

                for (int j = 0; j < lanes.Length; j++)
                {
                    AccumulateLaneProfile(lanes[j].m_Flags, lanes[j].m_Lane, lanes[j].m_Position.x, ref profile);
                }

                return profile.RoadCounts.Total > 0;
            }

            return false;
        }

        private bool TryCalculateDefaultRoadLaneProfile(
            Entity prefabEntity,
            out RoadLaneProfile profile)
        {
            profile = CreateEmptyRoadLaneProfile("NetGeometrySection:calculated");

            if (!EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<NetGeometrySection> sections))
            {
                return false;
            }

            NativeList<NetCompositionPiece> pieces = new NativeList<NetCompositionPiece>(32, Allocator.Temp);
            NativeList<NetCompositionLane> lanes = new NativeList<NetCompositionLane>(32, Allocator.Temp);
            try
            {
                NetCompositionData compositionData = default;
                NetCompositionHelpers.GetCompositionPieces(
                    pieces,
                    sections.AsNativeArray(),
                    default,
                    GetBufferLookup<NetSubSection>(true),
                    GetBufferLookup<NetSectionPiece>(true));
                NetCompositionHelpers.AddCompositionLanes(
                    Entity.Null,
                    ref compositionData,
                    pieces,
                    lanes,
                    default,
                    GetComponentLookup<NetLaneData>(true),
                    GetBufferLookup<NetPieceLane>(true));

                for (int i = 0; i < lanes.Length; i++)
                {
                    AccumulateLaneProfile(lanes[i].m_Flags, lanes[i].m_Lane, lanes[i].m_Position.x, ref profile);
                }

                return profile.RoadCounts.Total > 0;
            }
            catch (Exception ex)
            {
                Mod.log.Warn(ex, $"[IntersectionTool] Failed to calculate default road lanes for prefab={GetPrefabNameFromPrefab(prefabEntity)} entity={FormatEntity(prefabEntity)}.");
                profile = CreateEmptyRoadLaneProfile("NetGeometrySection:calculated");
                return false;
            }
            finally
            {
                if (lanes.IsCreated)
                {
                    lanes.Dispose();
                }

                if (pieces.IsCreated)
                {
                    pieces.Dispose();
                }
            }
        }

        private void AccumulateLaneProfile(
            LaneFlags flags,
            Entity lanePrefab,
            float lateralOffset,
            ref RoadLaneProfile profile)
        {
            RoadLaneCountMatcher.CountRoadLane(flags, ref profile.RoadCounts);

            if (!profile.HasMarkedParking &&
                IsMarkedParkingLane(flags, lanePrefab, out string detail))
            {
                profile.HasMarkedParking = true;
                profile.MarkedParkingDetail = detail;
            }

            if (IsTramTrackLane(flags, lanePrefab, out string tramDetail))
            {
                AddDirectionalLane(flags, ref profile.TramTrackCounts);
                AddDirectionalOffset(flags, lateralOffset, ref profile.TramTrackLayout);
                if (profile.TramTrackDetail == "<none>")
                {
                    profile.TramTrackDetail = tramDetail;
                }

                if (IsIndependentTramTrackLane(flags))
                {
                    AddDirectionalLane(flags, ref profile.IndependentTramCounts);
                    AddDirectionalOffset(flags, lateralOffset, ref profile.IndependentTramLayout);
                    if (profile.IndependentTramDetail == "<none>")
                    {
                        profile.IndependentTramDetail = tramDetail;
                    }
                }

                if (IsPublicTransportTramTrackLane(flags, lanePrefab, out string publicTransportTramDetail))
                {
                    AddDirectionalLane(flags, ref profile.PublicTransportTramCounts);
                    AddDirectionalOffset(flags, lateralOffset, ref profile.PublicTransportTramLayout);
                    if (profile.PublicTransportTramDetail == "<none>")
                    {
                        profile.PublicTransportTramDetail = publicTransportTramDetail;
                    }
                }
            }

            if (IsBusRoadLane(flags, lanePrefab, out string busDetail))
            {
                AddDirectionalOffset(flags, lateralOffset, ref profile.BusLaneLayout);
                if (profile.BusLaneDetail == "<none>")
                {
                    profile.BusLaneDetail = busDetail;
                }
            }
        }

        private static void AddDirectionalLane(LaneFlags flags, ref RoadLaneCounts counts)
        {
            if ((flags & LaneFlags.Twoway) != (LaneFlags)0)
            {
                counts.Forward++;
                counts.Backward++;
            }
            else if ((flags & LaneFlags.Invert) != (LaneFlags)0)
            {
                counts.Backward++;
            }
            else
            {
                counts.Forward++;
            }
        }

        private static void AddDirectionalOffset(
            LaneFlags flags,
            float lateralOffset,
            ref DirectionalLaneOffsetProfile profile)
        {
            if ((flags & LaneFlags.Twoway) != (LaneFlags)0)
            {
                profile.ForwardCount++;
                profile.ForwardOffsetSum += lateralOffset;
                profile.BackwardCount++;
                profile.BackwardOffsetSum += lateralOffset;
            }
            else if ((flags & LaneFlags.Invert) != (LaneFlags)0)
            {
                profile.BackwardCount++;
                profile.BackwardOffsetSum += lateralOffset;
            }
            else
            {
                profile.ForwardCount++;
                profile.ForwardOffsetSum += lateralOffset;
            }
        }

        private bool IsTramTrackLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & LaneFlags.Track) == 0 ||
                (flags & LaneFlags.Master) != 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out TrackLaneData trackLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackLaneData=missing";
                return false;
            }

            if ((trackLaneData.m_TrackTypes & TrackTypes.Tram) == 0)
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackTypes={trackLaneData.m_TrackTypes}";
                return false;
            }

            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackTypes={trackLaneData.m_TrackTypes} fallback={FormatEntity(trackLaneData.m_FallbackPrefab)} endObject={FormatEntity(trackLaneData.m_EndObjectPrefab)}";
            return true;
        }

        private static bool IsIndependentTramTrackLane(LaneFlags flags)
        {
            return (flags & LaneFlags.Road) == 0;
        }

        private bool IsPublicTransportTramTrackLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & (LaneFlags.Road | LaneFlags.Track)) != (LaneFlags.Road | LaneFlags.Track) ||
                (flags & LaneFlags.Master) != 0 ||
                (flags & LaneFlags.BicyclesOnly) != 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out TrackLaneData trackLaneData) ||
                (trackLaneData.m_TrackTypes & TrackTypes.Tram) == 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out CarLaneData carLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} publicTransportTram=False carLaneData=missing trackTypes={trackLaneData.m_TrackTypes}";
                return false;
            }

            bool supportsCars = (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
            bool publicOnly = (flags & LaneFlags.PublicOnly) != 0;
            bool hasNonBusFallback = carLaneData.m_NotBusLanePrefab != Entity.Null;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackTypes={trackLaneData.m_TrackTypes} roadTypes={carLaneData.m_RoadTypes} publicOnly={publicOnly} notBusFallback={FormatEntity(carLaneData.m_NotBusLanePrefab)}";
            return supportsCars && (publicOnly || hasNonBusFallback);
        }

        private bool IsBusRoadLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & LaneFlags.Road) == 0 ||
                (flags & LaneFlags.Master) != 0 ||
                (flags & LaneFlags.BicyclesOnly) != 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out CarLaneData carLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} carLaneData=missing";
                return false;
            }

            bool supportsCars = (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
            bool publicOnly = (flags & LaneFlags.PublicOnly) != 0;
            bool hasNonBusFallback = carLaneData.m_NotBusLanePrefab != Entity.Null;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} roadTypes={carLaneData.m_RoadTypes} publicOnly={publicOnly} notBusFallback={FormatEntity(carLaneData.m_NotBusLanePrefab)}";
            return supportsCars && (publicOnly || hasNonBusFallback);
        }

        private bool IsMarkedParkingLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & LaneFlags.Parking) == 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out ParkingLaneData parkingLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} parkingLaneData=missing";
                return false;
            }

            float angleDegrees = math.degrees(parkingLaneData.m_SlotAngle);
            bool markedParking = angleDegrees >= MinimumMarkedParkingSlotAngleDegrees;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} slotAngle={angleDegrees:0.#}deg slotSize=({parkingLaneData.m_SlotSize.x:0.##},{parkingLaneData.m_SlotSize.y:0.##}) threshold={MinimumMarkedParkingSlotAngleDegrees:0.#}deg";
            return markedParking;
        }

        private bool TryFindMatchingTramUpgrade(
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
            targetProfile = CreateEmptyRoadLaneProfile("TramUpgrade:missing");
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
                if (!TryGetCompositionRoadLaneProfile(composition.m_Composition, out RoadLaneProfile profile))
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

        private static bool CountsEqual(RoadLaneCounts first, RoadLaneCounts second)
        {
            return first.Forward == second.Forward &&
                   first.Backward == second.Backward;
        }

        private static bool CountsMatchForOrientation(
            RoadLaneCounts candidateCounts,
            RoadLaneCounts desiredCounts,
            bool invert)
        {
            return invert
                ? CountsEqual(candidateCounts, desiredCounts.Swapped())
                : CountsEqual(candidateCounts, desiredCounts);
        }

        private static CompositionFlags.Side GetTramTrackSideFlags()
        {
            return CompositionFlags.Side.PrimaryTrack |
                   CompositionFlags.Side.SecondaryTrack |
                   CompositionFlags.Side.TertiaryTrack |
                   CompositionFlags.Side.QuaternaryTrack;
        }

        private static CompositionFlags GetTramTrackUpgradeFlags(CompositionFlags flags)
        {
            CompositionFlags.Side trackFlags = GetTramTrackSideFlags();
            return new CompositionFlags(
                default,
                flags.m_Left & trackFlags,
                flags.m_Right & trackFlags);
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

        private static int GetReplacementLayoutScore(
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

        private static int GetDirectionalLayoutOffsetScore(
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

        private static int GetReplacementPrefabScore(
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

            if (sourceNetData.m_RequiredLayers != candidateNetData.m_RequiredLayers)
            {
                score += 200;
            }

            Game.Prefabs.RoadFlags comparableRoadFlags =
                Game.Prefabs.RoadFlags.EnableZoning |
                Game.Prefabs.RoadFlags.UseHighwayRules;
            if ((sourceRoadData.m_Flags & comparableRoadFlags) != (candidateRoadData.m_Flags & comparableRoadFlags))
            {
                score += 100;
            }

            score += (int)math.round(math.abs(sourceRoadData.m_SpeedLimit - candidateRoadData.m_SpeedLimit) * 10f);
            score += (int)math.round(math.abs(sourceGeometry.m_DefaultWidth - candidateGeometry.m_DefaultWidth) * 100f);
            return score;
        }
    }
}
