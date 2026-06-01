using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static PocketTurnLanes.Tool.RoadLaneCountMatcher;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
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

            GetRoadContentProfile(sourcePrefabRef.m_Prefab, out bool sourceIsDlc, out string sourceContentDetail);
            if (IsBridgeRoadPrefab(sourcePrefabRef.m_Prefab, out string sourceBridgeDetail))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Skip replacement prefab search sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} sourceDlc={sourceIsDlc} sourceContent={sourceContentDetail}: source road prefab is a bridge and bridge roads are excluded from selection and replacement matching. {sourceBridgeDetail}");
                return false;
            }

            if (IsHighwayRoadPrefab(sourcePrefabRef.m_Prefab, out string sourceHighwayDetail))
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
                ? GetTramTrackUpgradeFlags(sourceUpgraded.m_Flags)
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
                    GetRoadBuilderComponentProfile(
                        candidatePrefab,
                        out bool candidateHasRoadBuilderComponent,
                        out bool candidateIsDiscardedRoadBuilderPrefab,
                        out string candidateRoadBuilderComponentDetail);
                    bool candidateLooksLikeRoadBuilder =
                        candidateHasRoadBuilderComponent ||
                        LooksLikeRoadBuilderRoadPrefabName(candidateName);

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
                        if (TryGetRoadBuilderPrefabVisibility(
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

                    if (IsHighwayRoadData(candidateRoadData))
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

                    GetRoadContentProfile(candidatePrefab, out bool candidateIsDlc, out string candidateContentDetail);
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
                    int score = GetReplacementPrefabScore(
                        sourceRoadData,
                        sourceNetData,
                        sourceGeometry,
                        candidateRoadData,
                        candidateNetData,
                        candidateGeometry,
                        candidateMatch.Invert,
                        sourceIsDlc,
                        candidateIsDlc);
                    int layoutScore = GetReplacementLayoutScore(
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

        private bool IsHighwayRoadEdge(Entity edgeEntity, out string detail)
        {
            if (edgeEntity == Entity.Null ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
            {
                detail = "prefabRef=missing";
                return false;
            }

            return IsHighwayRoadPrefab(prefabRef.m_Prefab, out detail);
        }

        private bool IsHighwayRoadPrefab(Entity prefabEntity, out string detail)
        {
            if (prefabEntity == Entity.Null)
            {
                detail = "prefab=<null> roadData=missing useHighwayRules=False";
                return false;
            }

            if (!EntityManager.TryGetComponent(prefabEntity, out RoadData roadData))
            {
                detail = $"prefab={GetPrefabNameFromPrefab(prefabEntity)} prefabEntity={FormatEntity(prefabEntity)} roadData=missing useHighwayRules=False";
                return false;
            }

            bool isHighway = IsHighwayRoadData(roadData);
            detail = $"prefab={GetPrefabNameFromPrefab(prefabEntity)} prefabEntity={FormatEntity(prefabEntity)} roadFlags={roadData.m_Flags} useHighwayRules={isHighway}";
            return isHighway;
        }

        private static bool IsHighwayRoadData(RoadData roadData)
        {
            return (roadData.m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
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
            public int DrivableLaneEnvelopeCount;
            public float DrivableLaneEnvelopeMin;
            public float DrivableLaneEnvelopeMax;
            public float DrivableLaneEnvelopeWidth;
            public bool HasMarkedParking;
            public string DrivableLaneEnvelopeDetail;
            public string MarkedParkingDetail;
            public string TramTrackDetail;
            public string IndependentTramDetail;
            public string PublicTransportTramDetail;
            public string BusLaneDetail;
            public string Source;
        }

        private struct RoadBuilderLaneConfig
        {
            public string GroupPrefabName;
            public string SectionPrefabName;
            public Dictionary<string, string> GroupOptions;
            public bool Invert;
            public float Width;
            public bool IsBus;
            public bool IsTram;
            public bool IsIndependentTram;
            public bool IsPublicTransportTram;
            public string Semantic;
        }

        private struct ReplacementSearchStats
        {
            public int Scanned;
            public int DlcBlocked;
            public int WidthMatches;
            public int ParkingExcluded;
            public int LaneMatches;
            public int MissingLaneData;
            public int IndependentTramCandidates;
            public int PublicTransportTramCandidates;
            public int TramUpgradeCandidates;
            public int TramUpgradeRejected;
            public int BusUpgradeCandidates;
            public int BusUpgradeRejected;
            public int LayoutScored;
            public int BusLayoutCandidates;
            public int SourcePrefabLaneMatches;
            public int RoadBuilderDiscarded;
            public int RoadBuilderNotInPlaysetExcluded;
            public int RoadBuilderVisibilityUnknown;
            public int HighwayExcluded;
            public string TramUpgradeRejectSample;
            public string BusUpgradeRejectSample;
            public string WidthCandidateSample;
            public string RoadBuilderCandidateSample;
            public string RoadBuilderBusUpgradeSample;
            public string RoadBuilderDiscardedSample;
            public string RoadBuilderNotInPlaysetSample;
            public string RoadBuilderVisibilityUnknownSample;
            public string BestBusLayoutCandidateDetail;
            private int m_BusUpgradeRejectSampleCount;
            private int m_WidthCandidateSampleCount;
            private int m_RoadBuilderCandidateSampleCount;
            private int m_RoadBuilderBusUpgradeSampleCount;
            private int m_RoadBuilderDiscardedSampleCount;
            private int m_RoadBuilderNotInPlaysetSampleCount;
            private int m_RoadBuilderVisibilityUnknownSampleCount;
            private int m_BestBusLayoutCandidateScore;

            public static ReplacementSearchStats Create()
            {
                return new ReplacementSearchStats
                {
                    TramUpgradeRejectSample = "<none>",
                    BusUpgradeRejectSample = "<none>",
                    WidthCandidateSample = "<none>",
                    RoadBuilderCandidateSample = "<none>",
                    RoadBuilderBusUpgradeSample = "<none>",
                    RoadBuilderDiscardedSample = "<none>",
                    RoadBuilderNotInPlaysetSample = "<none>",
                    RoadBuilderVisibilityUnknownSample = "<none>",
                    BestBusLayoutCandidateDetail = "<none>",
                    m_BestBusLayoutCandidateScore = int.MaxValue
                };
            }

            public void AddTramUpgradeRejection(string sample)
            {
                TramUpgradeRejected++;
                if (TramUpgradeRejectSample == "<none>")
                {
                    TramUpgradeRejectSample = sample;
                }
            }

            public void AddBusUpgradeRejection(string sample, int maxSamples)
            {
                BusUpgradeRejected++;
                AppendLogSample(ref BusUpgradeRejectSample, ref m_BusUpgradeRejectSampleCount, sample, maxSamples);
            }

            public void AddWidthCandidateSample(string sample, int maxSamples)
            {
                AppendLogSample(ref WidthCandidateSample, ref m_WidthCandidateSampleCount, sample, maxSamples);
            }

            public void AddRoadBuilderCandidateSample(string sample, int maxSamples)
            {
                AppendLogSample(ref RoadBuilderCandidateSample, ref m_RoadBuilderCandidateSampleCount, sample, maxSamples);
            }

            public void AddRoadBuilderBusUpgradeSample(string sample, int maxSamples)
            {
                AppendLogSample(ref RoadBuilderBusUpgradeSample, ref m_RoadBuilderBusUpgradeSampleCount, sample, maxSamples);
            }

            public void AddRoadBuilderDiscardedSample(string sample, int maxSamples)
            {
                AppendLogSample(ref RoadBuilderDiscardedSample, ref m_RoadBuilderDiscardedSampleCount, sample, maxSamples);
            }

            public void AddRoadBuilderNotInPlaysetSample(string sample, int maxSamples)
            {
                AppendLogSample(ref RoadBuilderNotInPlaysetSample, ref m_RoadBuilderNotInPlaysetSampleCount, sample, maxSamples);
            }

            public void AddRoadBuilderVisibilityUnknownSample(string sample, int maxSamples)
            {
                AppendLogSample(ref RoadBuilderVisibilityUnknownSample, ref m_RoadBuilderVisibilityUnknownSampleCount, sample, maxSamples);
            }

            public void RecordBusLayoutCandidate(int score, string detail)
            {
                BusLayoutCandidates++;
                if (score >= m_BestBusLayoutCandidateScore)
                {
                    return;
                }

                m_BestBusLayoutCandidateScore = score;
                BestBusLayoutCandidateDetail = detail;
            }
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

        private static RoadLaneProfile CreateEmptyRoadLaneProfile(string source)
        {
            return new RoadLaneProfile
            {
                DrivableLaneEnvelopeDetail = "<none>",
                MarkedParkingDetail = "<none>",
                TramTrackDetail = "<none>",
                IndependentTramDetail = "<none>",
                PublicTransportTramDetail = "<none>",
                BusLaneDetail = "<none>",
                Source = source
            };
        }

        private void ApplyRoadBuilderConfigSemantics(Entity prefabEntity, ref RoadLaneProfile profile)
        {
            if (!TryBuildRoadBuilderConfigLaneProfile(prefabEntity, out RoadLaneProfile configProfile, out string detail))
            {
                return;
            }

            bool changed = false;
            if (configProfile.BusLaneLayout.HasAny)
            {
                profile.BusLaneLayout = configProfile.BusLaneLayout;
                profile.BusLaneDetail = configProfile.BusLaneDetail;
                changed = true;
            }

            if (!configProfile.TramTrackCounts.IsEmpty)
            {
                profile.TramTrackCounts = configProfile.TramTrackCounts;
                profile.TramTrackLayout = configProfile.TramTrackLayout;
                profile.TramTrackDetail = configProfile.TramTrackDetail;
                profile.IndependentTramCounts = configProfile.IndependentTramCounts;
                profile.IndependentTramLayout = configProfile.IndependentTramLayout;
                profile.IndependentTramDetail = configProfile.IndependentTramDetail;
                profile.PublicTransportTramCounts = configProfile.PublicTransportTramCounts;
                profile.PublicTransportTramLayout = configProfile.PublicTransportTramLayout;
                profile.PublicTransportTramDetail = configProfile.PublicTransportTramDetail;
                changed = true;
            }

            if (changed)
            {
                profile.Source = $"{profile.Source}+RoadBuilderConfig";
                if (profile.BusLaneDetail != "<none>")
                {
                    profile.BusLaneDetail = $"{profile.BusLaneDetail} {detail}";
                }

                if (profile.TramTrackDetail != "<none>")
                {
                    profile.TramTrackDetail = $"{profile.TramTrackDetail} {detail}";
                }
            }
        }

        private bool TryBuildRoadBuilderConfigLaneProfile(
            Entity prefabEntity,
            out RoadLaneProfile profile,
            out string detail)
        {
            profile = CreateEmptyRoadLaneProfile("RoadBuilderConfig");
            detail = "roadBuilderConfig=none";

            if (!TryGetRoadBuilderConfigLanes(prefabEntity, out List<RoadBuilderLaneConfig> lanes, out detail))
            {
                return false;
            }

            float totalWidth = 0f;
            for (int i = 0; i < lanes.Count; i++)
            {
                totalWidth += math.max(0f, lanes[i].Width);
            }

            if (totalWidth <= 0.01f)
            {
                detail = $"roadBuilderConfig=invalid-width lanes={lanes.Count}";
                return false;
            }

            int busLanes = 0;
            int tramLanes = 0;
            int independentTramLanes = 0;
            int publicTransportTramLanes = 0;
            string semanticSample = "<none>";
            int semanticSampleCount = 0;
            float offset = -totalWidth * 0.5f;

            for (int i = 0; i < lanes.Count; i++)
            {
                RoadBuilderLaneConfig lane = lanes[i];
                float laneWidth = math.max(0f, lane.Width);
                float centerOffset = offset + laneWidth * 0.5f;
                offset += laneWidth;

                if (!lane.IsBus && !lane.IsTram)
                {
                    continue;
                }

                // RoadBuilder writes LaneConfig.Invert into NetSectionInfo.m_Invert; for these lane groups that maps
                // to the runtime forward lane side, opposite to a raw LaneFlags.Invert interpretation.
                bool forward = lane.Invert;
                if (lane.IsBus)
                {
                    busLanes++;
                    AddRoadBuilderDirectionalOffset(forward, centerOffset, ref profile.BusLaneLayout);
                    if (profile.BusLaneDetail == "<none>")
                    {
                        profile.BusLaneDetail = "roadBuilderConfig";
                    }
                }

                if (lane.IsTram)
                {
                    tramLanes++;
                    AddRoadBuilderDirectionalLane(forward, ref profile.TramTrackCounts);
                    AddRoadBuilderDirectionalOffset(forward, centerOffset, ref profile.TramTrackLayout);
                    if (profile.TramTrackDetail == "<none>")
                    {
                        profile.TramTrackDetail = "roadBuilderConfig";
                    }
                }

                if (lane.IsIndependentTram)
                {
                    independentTramLanes++;
                    AddRoadBuilderDirectionalLane(forward, ref profile.IndependentTramCounts);
                    AddRoadBuilderDirectionalOffset(forward, centerOffset, ref profile.IndependentTramLayout);
                    if (profile.IndependentTramDetail == "<none>")
                    {
                        profile.IndependentTramDetail = "roadBuilderConfig";
                    }
                }

                if (lane.IsPublicTransportTram)
                {
                    publicTransportTramLanes++;
                    AddRoadBuilderDirectionalLane(forward, ref profile.PublicTransportTramCounts);
                    AddRoadBuilderDirectionalOffset(forward, centerOffset, ref profile.PublicTransportTramLayout);
                    if (profile.PublicTransportTramDetail == "<none>")
                    {
                        profile.PublicTransportTramDetail = "roadBuilderConfig";
                    }
                }

                AppendLogSample(
                    ref semanticSample,
                    ref semanticSampleCount,
                    $"{i}:{lane.Semantic}/{(forward ? "F" : "B")}@{centerOffset:0.##}m width={laneWidth:0.##}m group={ShortRoadBuilderPrefabName(lane.GroupPrefabName)} section={ShortRoadBuilderPrefabName(lane.SectionPrefabName)} transport={GetRoadBuilderOption(lane.GroupOptions, "Transport Option")}",
                    16);
            }

            detail = $"roadBuilderConfig=matched prefab={GetPrefabNameFromPrefab(prefabEntity)} lanes={lanes.Count} totalWidth={totalWidth:0.##}m busLanes={busLanes} tramLanes={tramLanes} independentTramLanes={independentTramLanes} publicTransportTramLanes={publicTransportTramLanes} busLayout={profile.BusLaneLayout} tramLayout={profile.TramTrackLayout} semanticSample={semanticSample}";
            return profile.BusLaneLayout.HasAny || !profile.TramTrackCounts.IsEmpty;
        }

        private bool TryGetRoadBuilderConfigLanes(
            Entity prefabEntity,
            out List<RoadBuilderLaneConfig> lanes,
            out string detail)
        {
            lanes = null;
            detail = "roadBuilderConfig=none";

            if (!m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                detail = $"roadBuilderConfig=missing-prefab prefabEntity={FormatEntity(prefabEntity)}";
                return false;
            }

            object config = TryGetPropertyValue(prefabBase, "Config");
            if (config == null ||
                config.GetType().FullName?.StartsWith("RoadBuilder.Domain.Configurations.", StringComparison.Ordinal) != true)
            {
                detail = $"roadBuilderConfig=not-roadbuilder prefabType={prefabBase.GetType().FullName}";
                return false;
            }

            object rawLanes = TryGetPropertyValue(config, "Lanes");
            if (!(rawLanes is IEnumerable enumerable))
            {
                detail = $"roadBuilderConfig=missing-lanes configType={config.GetType().FullName}";
                return false;
            }

            lanes = new List<RoadBuilderLaneConfig>();
            foreach (object laneObject in enumerable)
            {
                if (laneObject == null)
                {
                    continue;
                }

                RoadBuilderLaneConfig lane = new RoadBuilderLaneConfig
                {
                    GroupPrefabName = TryGetPropertyValue(laneObject, "GroupPrefabName") as string ?? string.Empty,
                    SectionPrefabName = TryGetPropertyValue(laneObject, "SectionPrefabName") as string ?? string.Empty,
                    GroupOptions = ReadRoadBuilderGroupOptions(TryGetPropertyValue(laneObject, "GroupOptions")),
                    Invert = TryGetPropertyValue(laneObject, "Invert") is bool invert && invert
                };
                lane.Width = GetRoadBuilderLaneWidth(lane);
                ClassifyRoadBuilderLane(ref lane);
                lanes.Add(lane);
            }

            detail = $"roadBuilderConfig=loaded prefab={prefabBase.name} lanes={lanes.Count} configType={config.GetType().FullName}";
            return lanes.Count > 0;
        }

        private void GetRoadBuilderComponentProfile(
            Entity prefabEntity,
            out bool hasRoadBuilderComponent,
            out bool isDiscarded,
            out string detail)
        {
            hasRoadBuilderComponent = false;
            isDiscarded = false;
            detail = "roadBuilderComponents=none";

            if (prefabEntity == Entity.Null || !EntityManager.Exists(prefabEntity))
            {
                detail = "roadBuilderComponents=missing-prefab";
                return;
            }

            string componentSample = "<none>";
            int componentSampleCount = 0;
            NativeArray<ComponentType> componentTypes = default;
            try
            {
                componentTypes = EntityManager.GetComponentTypes(prefabEntity, Allocator.Temp);
                for (int i = 0; i < componentTypes.Length; i++)
                {
                    string typeName = GetComponentTypeName(componentTypes[i]);
                    if (typeName.IndexOf("RoadBuilder", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    hasRoadBuilderComponent = true;
                    AppendLogSample(ref componentSample, ref componentSampleCount, typeName, 8);
                    if (typeName.EndsWith(".DiscardedRoadBuilderPrefab", StringComparison.Ordinal) ||
                        typeName.EndsWith(".RoadBuilderToBeDeletedComponent", StringComparison.Ordinal))
                    {
                        isDiscarded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                detail = $"roadBuilderComponents=error {ex.GetType().Name}:{ex.Message}";
                return;
            }
            finally
            {
                if (componentTypes.IsCreated)
                {
                    componentTypes.Dispose();
                }
            }

            detail = $"roadBuilderComponents={(hasRoadBuilderComponent ? "matched" : "none")} discarded={isDiscarded} sample={componentSample}";
        }

        private bool TryGetRoadBuilderPrefabVisibility(
            Entity prefabEntity,
            out bool isInPlayset,
            out string detail)
        {
            isInPlayset = true;

            if (!m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-prefab prefabEntity={FormatEntity(prefabEntity)}";
                return false;
            }

            object config = TryGetPropertyValue(prefabBase, "Config");
            if (config == null)
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-config prefab={prefabBase.name} prefabType={prefabBase.GetType().FullName}";
                return false;
            }

            Type configType = config.GetType();
            if (configType.FullName?.StartsWith("RoadBuilder.Domain.Configurations.", StringComparison.Ordinal) != true)
            {
                detail = $"roadBuilderVisibility=unknown reason=not-roadbuilder-config prefab={prefabBase.name} configType={configType.FullName}";
                return false;
            }

            string configDetail = GetRoadBuilderConfigVisibilityDetail(config);
            Type extensionType = configType.Assembly.GetType("RoadBuilder.Utilities.NetworkConfigExtensionsUtil");
            if (extensionType == null)
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-extension-type prefab={prefabBase.name} {configDetail}";
                return false;
            }

            MethodInfo isInPlaysetMethod = FindRoadBuilderIsInPlaysetMethod(extensionType, configType);
            if (isInPlaysetMethod == null)
            {
                detail = $"roadBuilderVisibility=unknown reason=missing-is-in-playset-method prefab={prefabBase.name} {configDetail}";
                return false;
            }

            try
            {
                object result = isInPlaysetMethod.Invoke(null, new[] { config });
                if (result is bool visible)
                {
                    isInPlayset = visible;
                    detail = $"roadBuilderVisibility=matched visible={visible} prefab={prefabBase.name} {configDetail}";
                    return true;
                }

                detail = $"roadBuilderVisibility=unknown reason=non-bool-result resultType={result?.GetType().FullName ?? "<null>"} prefab={prefabBase.name} {configDetail}";
                return false;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                detail = $"roadBuilderVisibility=unknown reason=invoke-error error={inner.GetType().Name}:{inner.Message} prefab={prefabBase.name} {configDetail}";
                return false;
            }
            catch (Exception ex)
            {
                detail = $"roadBuilderVisibility=unknown reason=reflection-error error={ex.GetType().Name}:{ex.Message} prefab={prefabBase.name} {configDetail}";
                return false;
            }
        }

        private static MethodInfo FindRoadBuilderIsInPlaysetMethod(Type extensionType, Type configType)
        {
            MethodInfo[] methods = extensionType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "IsInPlayset", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1 &&
                    parameters[0].ParameterType.IsAssignableFrom(configType))
                {
                    return method;
                }
            }

            return null;
        }

        private static string GetRoadBuilderConfigVisibilityDetail(object config)
        {
            Type configType = config.GetType();
            string configId = TryGetPropertyValue(config, "ID")?.ToString() ?? "<missing>";
            string configName = TryGetPropertyValue(config, "Name")?.ToString() ?? "<missing>";
            string playsets = FormatRoadBuilderPlaysets(TryGetPropertyValue(config, "Playsets"));
            string currentPlayset = TryGetStaticPropertyValue(
                    configType.Assembly.GetType("RoadBuilder.Utilities.PdxModsUtil"),
                    "CurrentPlayset")
                ?.ToString() ?? "<missing>";
            object settings = TryGetStaticPropertyValue(
                configType.Assembly.GetType("RoadBuilder.Mod"),
                "Settings");
            string noPlaysetIsolation = TryGetPropertyValue(settings, "NoPlaysetIsolation")?.ToString() ?? "<missing>";

            return $"configId={configId} configName={configName} playsets={playsets} currentPlayset={currentPlayset} noPlaysetIsolation={noPlaysetIsolation} configType={configType.FullName}";
        }

        private static object TryGetStaticPropertyValue(Type type, string propertyName)
        {
            if (type == null)
            {
                return null;
            }

            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(null);
        }

        private static string FormatRoadBuilderPlaysets(object rawPlaysets)
        {
            if (rawPlaysets == null)
            {
                return "<null>";
            }

            if (rawPlaysets is string playset)
            {
                return string.IsNullOrEmpty(playset) ? "<empty>" : playset;
            }

            if (rawPlaysets is IEnumerable enumerable)
            {
                string samples = "<none>";
                int sampleCount = 0;
                foreach (object item in enumerable)
                {
                    AppendLogSample(ref samples, ref sampleCount, item?.ToString() ?? "<null>", 16);
                }

                return samples;
            }

            return rawPlaysets.ToString() ?? "<unknown>";
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
                ApplyRoadBuilderConfigSemantics(fallbackPrefab, ref profile);
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
                    ApplyRoadBuilderConfigSemantics(prefabEntity, ref profile);
                    return true;
                }
            }

            if (TryGetCompositionLaneProfile(prefabEntity, default, out profile))
            {
                profile.Source = "NetGeometryComposition:default";
                ApplyRoadBuilderConfigSemantics(prefabEntity, ref profile);
                return true;
            }

            if (TryCalculateDefaultRoadLaneProfile(prefabEntity, out profile))
            {
                profile.Source = "NetGeometrySection:calculated";
                ApplyRoadBuilderConfigSemantics(prefabEntity, ref profile);
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
            return TryCalculateRoadLaneProfile(
                prefabEntity,
                default,
                "NetGeometrySection:calculated",
                out profile);
        }

        private bool TryCalculateRoadLaneProfile(
            Entity prefabEntity,
            CompositionFlags compositionFlags,
            string source,
            out RoadLaneProfile profile)
        {
            profile = CreateEmptyRoadLaneProfile(source);

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
                    compositionFlags,
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

                profile.Source = source;
                return profile.RoadCounts.Total > 0;
            }
            catch (Exception ex)
            {
                Mod.LogException(ex, $"[IntersectionTool] Failed to calculate road lanes for prefab={GetPrefabNameFromPrefab(prefabEntity)} entity={FormatEntity(prefabEntity)} compositionFlags={compositionFlags} source={source}.");
                profile = CreateEmptyRoadLaneProfile(source);
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
            LaneFlags effectiveFlags = GetEffectiveLaneFlags(flags, lanePrefab);
            string flagMergeDetail = FormatEffectiveLaneFlags(flags, effectiveFlags);

            RoadLaneCountMatcher.CountRoadLane(effectiveFlags, ref profile.RoadCounts);
            if (IsDrivablePocketLengthLane(effectiveFlags) &&
                TryGetLanePrefabWidth(lanePrefab, out float laneWidth))
            {
                AddDrivableLaneEnvelope(effectiveFlags, lateralOffset, laneWidth, flagMergeDetail, ref profile);
            }

            if (!profile.HasMarkedParking &&
                IsMarkedParkingLane(effectiveFlags, lanePrefab, out string detail))
            {
                profile.HasMarkedParking = true;
                profile.MarkedParkingDetail = detail + flagMergeDetail;
            }

            if (IsTramTrackLane(effectiveFlags, lanePrefab, out string tramDetail))
            {
                AddDirectionalLane(effectiveFlags, ref profile.TramTrackCounts);
                AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.TramTrackLayout);
                if (profile.TramTrackDetail == "<none>")
                {
                    profile.TramTrackDetail = tramDetail + flagMergeDetail;
                }

                if (IsIndependentTramTrackLane(effectiveFlags))
                {
                    AddDirectionalLane(effectiveFlags, ref profile.IndependentTramCounts);
                    AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.IndependentTramLayout);
                    if (profile.IndependentTramDetail == "<none>")
                    {
                        profile.IndependentTramDetail = tramDetail + flagMergeDetail;
                    }
                }

                if (IsPublicTransportTramTrackLane(effectiveFlags, lanePrefab, out string publicTransportTramDetail))
                {
                    AddDirectionalLane(effectiveFlags, ref profile.PublicTransportTramCounts);
                    AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.PublicTransportTramLayout);
                    if (profile.PublicTransportTramDetail == "<none>")
                    {
                        profile.PublicTransportTramDetail = publicTransportTramDetail + flagMergeDetail;
                    }
                }
            }

            if (IsBusRoadLane(effectiveFlags, lanePrefab, out string busDetail))
            {
                AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.BusLaneLayout);
                if (profile.BusLaneDetail == "<none>")
                {
                    profile.BusLaneDetail = busDetail + flagMergeDetail;
                }
            }
        }

        private static bool IsDrivablePocketLengthLane(LaneFlags flags)
        {
            if ((flags & (LaneFlags.Master | LaneFlags.Road)) != LaneFlags.Road)
            {
                return false;
            }

            const LaneFlags excluded =
                LaneFlags.BicyclesOnly |
                LaneFlags.Parking |
                LaneFlags.Pedestrian |
                LaneFlags.Utility;
            return (flags & excluded) == 0;
        }

        private bool TryGetLanePrefabWidth(Entity lanePrefab, out float width)
        {
            width = 0f;
            if (lanePrefab == Entity.Null ||
                !EntityManager.TryGetComponent(lanePrefab, out NetLaneData laneData) ||
                laneData.m_Width <= 0.01f)
            {
                return false;
            }

            width = laneData.m_Width;
            return true;
        }

        private static void AddDrivableLaneEnvelope(
            LaneFlags flags,
            float lateralOffset,
            float laneWidth,
            string flagMergeDetail,
            ref RoadLaneProfile profile)
        {
            float halfWidth = laneWidth * 0.5f;
            float min = lateralOffset - halfWidth;
            float max = lateralOffset + halfWidth;

            if (profile.DrivableLaneEnvelopeCount == 0)
            {
                profile.DrivableLaneEnvelopeMin = min;
                profile.DrivableLaneEnvelopeMax = max;
                profile.DrivableLaneEnvelopeDetail = $"firstLane offset={lateralOffset:0.##}m width={laneWidth:0.##}m flags={flags}{flagMergeDetail}";
            }
            else
            {
                profile.DrivableLaneEnvelopeMin = math.min(profile.DrivableLaneEnvelopeMin, min);
                profile.DrivableLaneEnvelopeMax = math.max(profile.DrivableLaneEnvelopeMax, max);
            }

            profile.DrivableLaneEnvelopeCount++;
            profile.DrivableLaneEnvelopeWidth = math.max(
                0f,
                profile.DrivableLaneEnvelopeMax - profile.DrivableLaneEnvelopeMin);
        }

        private LaneFlags GetEffectiveLaneFlags(LaneFlags flags, Entity lanePrefab)
        {
            if (lanePrefab == Entity.Null ||
                !EntityManager.TryGetComponent(lanePrefab, out NetLaneData laneData))
            {
                return flags;
            }

            const LaneFlags semanticLaneFlags =
                LaneFlags.Road |
                LaneFlags.Parking |
                LaneFlags.Track |
                LaneFlags.Pedestrian |
                LaneFlags.PublicOnly |
                LaneFlags.BicyclesOnly |
                LaneFlags.Twoway;
            return flags | (laneData.m_Flags & semanticLaneFlags);
        }

        private static string FormatEffectiveLaneFlags(LaneFlags rawFlags, LaneFlags effectiveFlags)
        {
            return rawFlags == effectiveFlags
                ? string.Empty
                : $" rawFlags={rawFlags} effectiveFlags={effectiveFlags}";
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

        private static void AddRoadBuilderDirectionalLane(bool forward, ref RoadLaneCounts counts)
        {
            if (forward)
            {
                counts.Forward++;
            }
            else
            {
                counts.Backward++;
            }
        }

        private static void AddRoadBuilderDirectionalOffset(
            bool forward,
            float lateralOffset,
            ref DirectionalLaneOffsetProfile profile)
        {
            if (forward)
            {
                profile.ForwardCount++;
                profile.ForwardOffsetSum += lateralOffset;
            }
            else
            {
                profile.BackwardCount++;
                profile.BackwardOffsetSum += lateralOffset;
            }
        }

        private static object TryGetPropertyValue(object instance, string propertyName)
        {
            if (instance == null)
            {
                return null;
            }

            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance);
        }

        private static Dictionary<string, string> ReadRoadBuilderGroupOptions(object rawOptions)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (rawOptions == null)
            {
                return result;
            }

            if (rawOptions is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    string key = entry.Key?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(key))
                    {
                        result[key] = entry.Value?.ToString() ?? string.Empty;
                    }
                }

                return result;
            }

            if (rawOptions is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    string key = TryGetPropertyValue(item, "Key")?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(key))
                    {
                        result[key] = TryGetPropertyValue(item, "Value")?.ToString() ?? string.Empty;
                    }
                }
            }

            return result;
        }

        private static string GetRoadBuilderOption(Dictionary<string, string> options, string optionName)
        {
            if (options == null)
            {
                return string.Empty;
            }

            return options.TryGetValue(optionName, out string value) ? value ?? string.Empty : string.Empty;
        }

        private static float GetRoadBuilderLaneWidth(RoadBuilderLaneConfig lane)
        {
            if (TryGetRoadBuilderOptionMeters(lane.GroupOptions, "Lane Width", out float laneWidth) ||
                TryGetRoadBuilderOptionMeters(lane.GroupOptions, "Median Width", out laneWidth) ||
                TryGetRoadBuilderOptionMeters(lane.GroupOptions, "Width", out laneWidth) ||
                TryGetRoadBuilderOptionMeters(lane.GroupOptions, "Shoulder Width", out laneWidth) ||
                TryGetRoadBuilderOptionMeters(lane.GroupOptions, "Parking Width", out laneWidth))
            {
                return laneWidth;
            }

            if (TryExtractMeterValue(lane.SectionPrefabName, out laneWidth))
            {
                return laneWidth;
            }

            if (RoadBuilderPrefabNameContains(lane.GroupPrefabName, "MedianGroupPrefab"))
            {
                return 1f;
            }

            if (RoadBuilderPrefabNameContains(lane.GroupPrefabName, "SidewalkGroupPrefab"))
            {
                return 3f;
            }

            if (RoadBuilderPrefabNameContains(lane.GroupPrefabName, "TramGroupPrefab") ||
                RoadBuilderPrefabNameContains(lane.GroupPrefabName, "BusGroupPrefab") ||
                RoadBuilderPrefabNameContains(lane.GroupPrefabName, "CarGroupPrefab") ||
                RoadBuilderPrefabNameContains(lane.GroupPrefabName, "HarborCarGroupPrefab"))
            {
                return 3f;
            }

            return 0f;
        }

        private static bool TryGetRoadBuilderOptionMeters(
            Dictionary<string, string> options,
            string optionName,
            out float meters)
        {
            meters = 0f;
            return options != null &&
                   options.TryGetValue(optionName, out string rawValue) &&
                   TryParseMeters(rawValue, out meters);
        }

        private static bool TryExtractMeterValue(string text, out float meters)
        {
            meters = 0f;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            Match match = Regex.Match(text, @"(?<!\d)(\d+(?:[\.,]\d+)?)\s*m\b", RegexOptions.IgnoreCase);
            return match.Success && TryParseMeters(match.Groups[1].Value, out meters);
        }

        private static bool TryParseMeters(string rawValue, out float meters)
        {
            meters = 0f;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            string value = rawValue.Trim();
            if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 1);
            }

            value = value.Replace(',', '.');
            return float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out meters);
        }

        private static void ClassifyRoadBuilderLane(ref RoadBuilderLaneConfig lane)
        {
            string transportOption = GetRoadBuilderOption(lane.GroupOptions, "Transport Option");
            bool carGroup = RoadBuilderPrefabNameContains(lane.GroupPrefabName, "CarGroupPrefab") ||
                            RoadBuilderPrefabNameContains(lane.GroupPrefabName, "HarborCarGroupPrefab");
            bool busGroup = RoadBuilderPrefabNameContains(lane.GroupPrefabName, "BusGroupPrefab");
            bool tramGroup = RoadBuilderPrefabNameContains(lane.GroupPrefabName, "TramGroupPrefab");
            bool sectionPublicTransport = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Public Transport Lane");
            bool sectionTransportOption = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Transport Option");
            bool sectionTramOption = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Tram Option") ||
                                     StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Transport Tram");
            bool sectionTramTrack = StringContainsOrdinalIgnoreCase(lane.SectionPrefabName, "Tram Track");
            bool transportIsBus = string.Equals(transportOption, "Transport", StringComparison.OrdinalIgnoreCase);
            bool transportIsTram = string.Equals(transportOption, "Tram", StringComparison.OrdinalIgnoreCase);

            lane.IsBus = busGroup ||
                         sectionPublicTransport ||
                         (carGroup && (transportIsBus || transportIsTram)) ||
                         sectionTransportOption;
            lane.IsIndependentTram = tramGroup ||
                                     (sectionTramTrack && !sectionPublicTransport && !sectionTransportOption);
            lane.IsPublicTransportTram = !lane.IsIndependentTram &&
                                         (transportIsTram || sectionTramOption) &&
                                         (lane.IsBus || carGroup || busGroup || sectionPublicTransport || sectionTransportOption);
            lane.IsTram = lane.IsIndependentTram || lane.IsPublicTransportTram;

            if (lane.IsIndependentTram)
            {
                lane.Semantic = "tram-independent";
            }
            else if (lane.IsPublicTransportTram && lane.IsBus)
            {
                lane.Semantic = "pt-tram";
            }
            else if (lane.IsBus)
            {
                lane.Semantic = "pt";
            }
            else if (lane.IsPublicTransportTram)
            {
                lane.Semantic = "tram-pt";
            }
            else
            {
                lane.Semantic = "none";
            }
        }

        private static bool RoadBuilderPrefabNameContains(string prefabName, string suffix)
        {
            return !string.IsNullOrEmpty(prefabName) &&
                   prefabName.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StringContainsOrdinalIgnoreCase(string value, string pattern)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ShortRoadBuilderPrefabName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return "<none>";
            }

            int lastDot = prefabName.LastIndexOf('.');
            return lastDot >= 0 && lastDot < prefabName.Length - 1
                ? prefabName.Substring(lastDot + 1)
                : prefabName;
        }

        private static string GetComponentTypeName(ComponentType componentType)
        {
            try
            {
                Type managedType = componentType.GetManagedType();
                return managedType?.FullName ?? componentType.ToString();
            }
            catch
            {
                return componentType.ToString();
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

            bool publicOnly = (flags & LaneFlags.PublicOnly) != 0;
            bool hasNonBusFallback = carLaneData.m_NotBusLanePrefab != Entity.Null;
            bool supportsCars = (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackTypes={trackLaneData.m_TrackTypes} roadTypes={carLaneData.m_RoadTypes} supportsCars={supportsCars} publicOnly={publicOnly} notBusFallback={FormatEntity(carLaneData.m_NotBusLanePrefab)}";
            return publicOnly || hasNonBusFallback;
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

            bool publicOnly = (flags & LaneFlags.PublicOnly) != 0;
            bool hasNonBusFallback = carLaneData.m_NotBusLanePrefab != Entity.Null;
            bool supportsCars = (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} roadTypes={carLaneData.m_RoadTypes} supportsCars={supportsCars} publicOnly={publicOnly} notBusFallback={FormatEntity(carLaneData.m_NotBusLanePrefab)}";
            return publicOnly || hasNonBusFallback;
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

                    if (!TryFindMatchingTramUpgrade(
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
                        stats.AddTramUpgradeRejection(BuildTramUpgradeRejectSample(candidatePrefab, candidateProfile, invert, tramUpgradeDetail));
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
                else if (TryFindMatchingTramUpgrade(
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
                    stats.AddTramUpgradeRejection(BuildTramUpgradeRejectSample(candidatePrefab, candidateProfile, invert, tramUpgradeDetail));
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
                    hasBusUpgradeMatch = TryFindMatchingBusUpgrade(
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

        private string BuildTramUpgradeRejectSample(
            Entity candidatePrefab,
            RoadLaneProfile candidateProfile,
            bool invert,
            string tramUpgradeDetail)
        {
            return $"candidate={GetPrefabNameFromPrefab(candidatePrefab)} candidateRoad={candidateProfile.RoadCounts} candidateTramTracks={candidateProfile.TramTrackCounts} invert={invert} {tramUpgradeDetail}";
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

        private bool TryFindMatchingBusUpgrade(
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
            targetProfile = CreateEmptyRoadLaneProfile("BusUpgrade:missing");
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
                bool calculatedProfile = TryCalculateRoadLaneProfile(
                    prefabEntity,
                    composition.m_Mask,
                    $"NetGeometrySection:upgrade:{composition.m_Mask}",
                    out RoadLaneProfile calculatedRoadProfile);
                bool directProfile = TryGetCompositionRoadLaneProfile(composition.m_Composition, out RoadLaneProfile directRoadProfile);
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
            int score = GetDirectionalLayoutOffsetScore(sourceProfile.BusLaneLayout, orientedBusLayout) +
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

        private static bool LooksLikeRoadBuilderRoadPrefabName(string prefabName)
        {
            return !string.IsNullOrEmpty(prefabName) &&
                   prefabName.Length > 2 &&
                   prefabName[0] == 'r' &&
                   prefabName.IndexOf("-765611", StringComparison.Ordinal) > 0;
        }

        private static void AppendLogSample(
            ref string samples,
            ref int sampleCount,
            string sample,
            int maxSamples)
        {
            if (sampleCount >= maxSamples)
            {
                return;
            }

            if (samples == "<none>")
            {
                samples = sample;
            }
            else
            {
                samples += " || " + sample;
            }

            sampleCount++;
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

            Game.Prefabs.RoadFlags comparableRoadFlags = Game.Prefabs.RoadFlags.EnableZoning;
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
