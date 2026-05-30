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

            if (!TryGetRoadLaneCounts(edgeEntity, sourcePrefabRef.m_Prefab, out RoadLaneCounts originalCounts, out string laneSource))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: no default road lane counts were found.");
                return false;
            }

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
            int widthMatchCount = 0;
            int laneMatchCount = 0;
            int missingLaneCount = 0;
            int bestScore = int.MaxValue;
            ReplacementPrefabMatch bestMatch = default;

            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity candidatePrefab = prefabEntities[i];
                    scannedCount++;

                    if (candidatePrefab == sourcePrefabRef.m_Prefab ||
                        !EntityManager.TryGetComponent(candidatePrefab, out NetGeometryData candidateGeometry) ||
                        math.abs(candidateGeometry.m_DefaultWidth - sourceGeometry.m_DefaultWidth) > PrefabWidthTolerance)
                    {
                        continue;
                    }

                    widthMatchCount++;
                    if (!TryGetDefaultRoadLaneCounts(candidatePrefab, out RoadLaneCounts candidateCounts, out _))
                    {
                        missingLaneCount++;
                        continue;
                    }

                    if (!RoadLaneCountMatcher.TryMatch(candidateCounts, desiredCounts, out bool invert))
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
                        invert);

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
                            CandidateCounts = candidateCounts,
                            Score = score
                        };
                    }
                }
            }

            if (!found)
            {
                Mod.log.Warn($"[IntersectionTool] No pocket lane replacement prefab found sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={laneSource} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={originalCounts} desiredLanes={desiredCounts} scanned={scannedCount} widthMatches={widthMatchCount} laneMatches=0 missingLaneData={missingLaneCount}.");
                return false;
            }

            match = bestMatch;
            Mod.log.Info($"[IntersectionTool] Replacement prefab selected sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} targetPrefab={GetPrefabNameFromPrefab(match.Prefab)} orientation={(match.Invert ? "reversed" : "direct")} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={laneSource} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={match.OriginalCounts} desiredLanes={match.TargetCounts} candidateLanes={match.CandidateCounts} scanned={scannedCount} widthMatches={widthMatchCount} laneMatches={laneMatchCount} missingLaneData={missingLaneCount} score={match.Score}.");
            return true;
        }

        private bool TryGetRoadLaneCounts(Entity edgeEntity, Entity fallbackPrefab, out RoadLaneCounts counts, out string source)
        {
            if (EntityManager.TryGetComponent(edgeEntity, out Composition composition) &&
                TryGetCompositionRoadLaneCounts(composition.m_Edge, out counts))
            {
                source = $"Composition:{FormatEntity(composition.m_Edge)}";
                return true;
            }

            if (TryGetDefaultRoadLaneCounts(fallbackPrefab, out counts, out string prefabLaneSource))
            {
                source = prefabLaneSource;
                return true;
            }

            counts = default;
            source = "Missing";
            return false;
        }

        private bool TryGetCompositionRoadLaneCounts(Entity compositionEntity, out RoadLaneCounts counts)
        {
            counts = default;
            if (compositionEntity == Entity.Null ||
                !EntityManager.TryGetBuffer(compositionEntity, true, out DynamicBuffer<NetCompositionLane> lanes))
            {
                return false;
            }

            for (int i = 0; i < lanes.Length; i++)
            {
                RoadLaneCountMatcher.CountRoadLane(lanes[i].m_Flags, ref counts);
            }

            return counts.Total > 0;
        }

        private bool TryGetDefaultRoadLaneCounts(Entity prefabEntity, out RoadLaneCounts counts, out string source)
        {
            counts = default;
            source = "Missing";

            if (prefabEntity == Entity.Null)
            {
                return false;
            }

            if (EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<DefaultNetLane> lanes))
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    RoadLaneCountMatcher.CountRoadLane(lanes[i].m_Flags, ref counts);
                }

                if (counts.Total > 0)
                {
                    source = "DefaultNetLane";
                    return true;
                }
            }

            if (TryGetCompositionLaneCounts(prefabEntity, default, out counts))
            {
                source = "NetGeometryComposition:default";
                return true;
            }

            if (TryCalculateDefaultRoadLaneCounts(prefabEntity, out counts))
            {
                source = "NetGeometrySection:calculated";
                return true;
            }

            counts = default;
            return false;
        }

        private bool TryGetCompositionLaneCounts(Entity prefabEntity, CompositionFlags mask, out RoadLaneCounts counts)
        {
            counts = default;

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
                    RoadLaneCountMatcher.CountRoadLane(lanes[j].m_Flags, ref counts);
                }

                return counts.Total > 0;
            }

            return false;
        }

        private bool TryCalculateDefaultRoadLaneCounts(Entity prefabEntity, out RoadLaneCounts counts)
        {
            counts = default;

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
                    RoadLaneCountMatcher.CountRoadLane(lanes[i].m_Flags, ref counts);
                }

                return counts.Total > 0;
            }
            catch (Exception ex)
            {
                Mod.log.Warn(ex, $"[IntersectionTool] Failed to calculate default road lanes for prefab={GetPrefabNameFromPrefab(prefabEntity)} entity={FormatEntity(prefabEntity)}.");
                counts = default;
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

        private static int GetReplacementPrefabScore(
            RoadData sourceRoadData,
            NetData sourceNetData,
            NetGeometryData sourceGeometry,
            RoadData candidateRoadData,
            NetData candidateNetData,
            NetGeometryData candidateGeometry,
            bool invert)
        {
            int score = invert ? 1000 : 0;

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
