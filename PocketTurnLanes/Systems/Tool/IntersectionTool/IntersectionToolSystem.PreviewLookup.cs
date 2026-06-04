using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Tool;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private bool TryFindPreviewSplitNode(SplitCandidate candidate, out Entity splitNode)
        {
            splitNode = Entity.Null;

            using (NativeArray<Entity> entities = m_TempSplitNodeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempSplitNodeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    Temp temp = temps[i];
                    if ((temp.m_Flags & TempFlags.Replace) != TempFlags.Replace ||
                        !TempEntityHelpers.IsUsableTemp(temp))
                    {
                        continue;
                    }

                    if (temp.m_Original == candidate.Edge &&
                        math.abs(temp.m_CurvePosition - candidate.CurvePosition) <= PreviewSplitNodeTolerance)
                    {
                        splitNode = entities[i];
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryFindPreviewPocketEdge(
            SplitCandidate candidate,
            Entity splitNode,
            out Entity pocketEdge,
            out float lengthError)
        {
            pocketEdge = Entity.Null;
            lengthError = 0f;

            float bestScore = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            EdgeLookupRejectedCandidate bestRejected = EdgeLookupRejectedCandidate.CreateLength();
            int tempEdgeCount = 0;
            int connectedMatchCount = 0;
            int prefabMatchCount = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    Temp temp = temps[i];
                    if (!TempEntityHelpers.IsUsableTemp(temp) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    bool connectsSplitNode = edge.m_Start == splitNode || edge.m_End == splitNode;
                    Entity otherNode = edge.m_Start == splitNode ? edge.m_End : edge.m_Start;
                    bool connectsCandidateNode =
                        connectsSplitNode &&
                        TempEntityHelpers.IsSameOrTempOriginal(EntityManager, otherNode, candidate.Node);
                    if (!connectsCandidateNode)
                    {
                        continue;
                    }

                    connectedMatchCount++;
                    if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                        prefabRef.m_Prefab != candidate.TargetPrefab)
                    {
                        continue;
                    }

                    prefabMatchCount++;
                    float candidateLengthError = math.abs(curve.m_Length - candidate.SplitDistance);
                    if (candidateLengthError > PocketEdgeLengthTolerance)
                    {
                        bestRejected.RecordLength(edgeEntity, candidateLengthError);
                        continue;
                    }

                    float score = candidateLengthError;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestLengthError = candidateLengthError;
                        bestEdge = edgeEntity;
                    }
                }
            }

            if (bestEdge == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find preview pocket edge original={FormatEntity(candidate.Edge)} splitNode={FormatEntity(splitNode)} expectedDistance={candidate.SplitDistance:0.##}m tempEdges={tempEdgeCount} connectedMatches={connectedMatchCount} prefabMatches={prefabMatchCount} bestRejectedEdge={FormatEntity(bestRejected.Edge)} bestRejectedLengthError={FormatMeters(bestRejected.LengthError)}.");
                return false;
            }

            pocketEdge = bestEdge;
            lengthError = bestLengthError;
            return true;
        }

        private bool TryFindPreviewOuterEdge(
            SplitCandidate candidate,
            Entity splitNode,
            Entity pocketEdge,
            out Entity outerEdge,
            out float lengthError)
        {
            outerEdge = Entity.Null;
            lengthError = 0f;

            float expectedLength = -1f;
            if (EntityManager.TryGetComponent(candidate.Edge, out Curve originalCurve))
            {
                expectedLength = math.max(0f, originalCurve.m_Length - candidate.SplitDistance);
            }

            float bestScore = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            EdgeLookupRejectedCandidate bestRejected = EdgeLookupRejectedCandidate.CreateLength();
            int tempEdgeCount = 0;
            int connectedMatchCount = 0;
            int prefabMatchCount = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    if (edgeEntity == pocketEdge)
                    {
                        continue;
                    }

                    Temp temp = temps[i];
                    if (!TempEntityHelpers.IsUsableTemp(temp) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    if (edge.m_Start != splitNode && edge.m_End != splitNode)
                    {
                        continue;
                    }

                    Entity otherNode = edge.m_Start == splitNode ? edge.m_End : edge.m_Start;
                    if (TempEntityHelpers.IsSameOrTempOriginal(EntityManager, otherNode, candidate.Node))
                    {
                        continue;
                    }

                    connectedMatchCount++;
                    if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                        prefabRef.m_Prefab != candidate.TargetPrefab)
                    {
                        continue;
                    }

                    prefabMatchCount++;
                    float candidateLengthError = expectedLength >= 0f
                        ? math.abs(curve.m_Length - expectedLength)
                        : 0f;
                    if (expectedLength >= 0f && candidateLengthError > PocketEdgeLengthTolerance)
                    {
                        bestRejected.RecordLength(edgeEntity, candidateLengthError);
                        continue;
                    }

                    float sourcePrefabPenalty = prefabRef.m_Prefab == candidate.SourcePrefab ? 0f : PocketEdgeLengthTolerance;
                    float score = candidateLengthError + sourcePrefabPenalty;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestLengthError = candidateLengthError;
                        bestEdge = edgeEntity;
                    }
                }
            }

            if (bestEdge == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot find preview outer edge original={FormatEntity(candidate.Edge)} splitNode={FormatEntity(splitNode)} expectedLength={FormatMeters(expectedLength)} tempEdges={tempEdgeCount} connectedMatches={connectedMatchCount} prefabMatches={prefabMatchCount} bestRejectedEdge={FormatEntity(bestRejected.Edge)} bestRejectedLengthError={FormatMeters(bestRejected.LengthError)}.");
                return false;
            }

            outerEdge = bestEdge;
            lengthError = bestLengthError;
            return true;
        }
    }
}
