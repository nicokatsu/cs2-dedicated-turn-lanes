using Colossal.Entities;
using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using NetEdge = Game.Net.Edge;
using PathNode = Game.Pathfind.PathNode;

namespace PocketTurnLanes.Tool
{
    internal static class NetTopologyHelpers
    {
        private const float DirectionEpsilonSq = 0.0001f;

        public static bool IsMasterConnectorLane(EntityManager entityManager, Entity laneEntity)
        {
            if (entityManager.HasComponent<MasterLane>(laneEntity))
            {
                return true;
            }

            if (entityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef) &&
                entityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData))
            {
                return (laneData.m_Flags & LaneFlags.Master) != 0;
            }

            return false;
        }

        public static bool TryGetConnectedEdgesFromLane(
            EntityManager entityManager,
            Entity node,
            Lane lane,
            out Entity sourceEdge,
            out Entity targetEdge)
        {
            sourceEdge = Entity.Null;
            targetEdge = Entity.Null;
            if (!entityManager.TryGetBuffer(node, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            sourceEdge = FindEdgeByPathNode(connectedEdges, lane.m_StartNode);
            targetEdge = lane.m_StartNode.OwnerEquals(lane.m_EndNode)
                ? sourceEdge
                : FindEdgeByPathNode(connectedEdges, lane.m_EndNode);
            return sourceEdge != Entity.Null && targetEdge != Entity.Null;
        }

        public static Entity FindEdgeByPathNode(DynamicBuffer<ConnectedEdge> connectedEdges, PathNode node)
        {
            int ownerIndex = node.GetOwnerIndex();
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                if (connectedEdges[i].m_Edge.Index == ownerIndex)
                {
                    return connectedEdges[i].m_Edge;
                }
            }

            return Entity.Null;
        }

        public static bool HasMotorRoadLaneSemantics(PathMethod pathMethods, RoadTypes roadTypes)
        {
            return (pathMethods & PathMethod.Road) != 0 &&
                   (roadTypes & RoadTypes.Car) != 0;
        }

        public static bool TryGetEdgeDirectionFromNode(
            EntityManager entityManager,
            Entity edgeEntity,
            Entity nodeEntity,
            out float2 direction)
        {
            direction = default;
            if (!entityManager.TryGetComponent(edgeEntity, out NetEdge edge) ||
                !entityManager.TryGetComponent(edgeEntity, out Curve curve))
            {
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            return (nodeIsStart || nodeIsEnd) &&
                   TryGetOutwardDirection(edge, curve, nodeEntity, nodeIsStart, out direction);
        }

        public static bool TryGetOutwardDirection(
            NetEdge edge,
            Curve curve,
            Entity nodeEntity,
            bool nodeIsStart,
            out float2 direction)
        {
            float3 tangent = MathUtils.Tangent(curve.m_Bezier, nodeIsStart ? 0f : 1f);
            if (!nodeIsStart)
            {
                tangent = -tangent;
            }

            direction = tangent.xz;
            if (math.lengthsq(direction) <= DirectionEpsilonSq)
            {
                if (edge.m_Start == nodeEntity)
                {
                    direction = (curve.m_Bezier.d - curve.m_Bezier.a).xz;
                }
                else if (edge.m_End == nodeEntity)
                {
                    direction = (curve.m_Bezier.a - curve.m_Bezier.d).xz;
                }
            }

            float lengthSq = math.lengthsq(direction);
            if (lengthSq <= DirectionEpsilonSq)
            {
                direction = default;
                return false;
            }

            direction *= math.rsqrt(lengthSq);
            return true;
        }

        public static float Cross(float2 a, float2 b)
        {
            return a.x * b.y - a.y * b.x;
        }
    }
}
