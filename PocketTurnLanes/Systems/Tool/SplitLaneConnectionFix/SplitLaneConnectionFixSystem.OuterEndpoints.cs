using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryFindOuterEdge(Request request, out Entity outerEdge)
        {
            outerEdge = Entity.Null;
            if (request.OuterEdge != Entity.Null &&
                EntityManager.Exists(request.OuterEdge) &&
                !EntityManager.HasComponent<Deleted>(request.OuterEdge) &&
                EntityManager.TryGetComponent(request.OuterEdge, out NetEdge explicitEdge) &&
                (explicitEdge.m_Start == request.SplitNode || explicitEdge.m_End == request.SplitNode))
            {
                outerEdge = request.OuterEdge;
                return true;
            }

            if (!EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            float bestScore = float.MinValue;
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (edgeEntity == request.PocketEdge ||
                    edgeEntity == Entity.Null ||
                    !EntityManager.Exists(edgeEntity) ||
                    EntityManager.HasComponent<Deleted>(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) ||
                    !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool connectsSplit = edge.m_Start == request.SplitNode || edge.m_End == request.SplitNode;
                if (!connectsSplit)
                {
                    continue;
                }

                float score = 0f;
                if (edgeEntity == request.OriginalEdge)
                {
                    score += 1000f;
                }

                if (prefabRef.m_Prefab == request.SourcePrefab)
                {
                    score += 100f;
                }

                Entity otherNode = edge.m_Start == request.SplitNode ? edge.m_End : edge.m_Start;
                if (otherNode != request.IntersectionNode)
                {
                    score += 10f;
                }

                if (EntityManager.TryGetComponent(edgeEntity, out Curve curve))
                {
                    score += math.min(curve.m_Length, 100f) * 0.01f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    outerEdge = edgeEntity;
                }
            }

            return outerEdge != Entity.Null;
        }
    }
}
