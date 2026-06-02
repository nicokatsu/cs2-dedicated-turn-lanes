using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal enum TrafficConnectorMovement
    {
        Ambiguous,
        Straight,
        Left,
        Right,
        Uturn
    }

    internal static class TrafficConnectorMovementClassifier
    {
        private const float StraightCrossThreshold = 0.25f;
        private const float CenterRewriteStraightDotThreshold = 0.25f;

        public static TrafficConnectorMovement ClassifyApproachDemand(
            EntityManager entityManager,
            Entity intersectionNode,
            Entity sourceEdge,
            Entity targetEdge,
            CarLaneFlags flags)
        {
            bool leftFlag = (flags & CarLaneFlags.TurnLeft) != 0;
            bool rightFlag = (flags & CarLaneFlags.TurnRight) != 0;
            if (leftFlag && !rightFlag)
            {
                return TrafficConnectorMovement.Left;
            }

            if (rightFlag && !leftFlag)
            {
                return TrafficConnectorMovement.Right;
            }

            return TryClassifyByGeometry(
                    entityManager,
                    intersectionNode,
                    sourceEdge,
                    targetEdge,
                    straightDotThreshold: 0f,
                    classifyOpposingAsUturn: false,
                    out TrafficConnectorMovement movement)
                ? movement
                : TrafficConnectorMovement.Ambiguous;
        }

        public static TrafficConnectorMovement ClassifyCenterRewrite(
            EntityManager entityManager,
            Entity centerNode,
            Entity sourceEdge,
            Entity targetEdge,
            CarLaneFlags flags)
        {
            if (sourceEdge == targetEdge ||
                (flags & (CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight)) != 0)
            {
                return TrafficConnectorMovement.Uturn;
            }

            bool left = (flags & (CarLaneFlags.TurnLeft | CarLaneFlags.GentleTurnLeft)) != 0;
            bool right = (flags & (CarLaneFlags.TurnRight | CarLaneFlags.GentleTurnRight)) != 0;
            if (left != right)
            {
                return left ? TrafficConnectorMovement.Left : TrafficConnectorMovement.Right;
            }

            if ((flags & CarLaneFlags.Forward) != 0)
            {
                return TrafficConnectorMovement.Straight;
            }

            return TryClassifyByGeometry(
                    entityManager,
                    centerNode,
                    sourceEdge,
                    targetEdge,
                    CenterRewriteStraightDotThreshold,
                    classifyOpposingAsUturn: true,
                    out TrafficConnectorMovement movement)
                ? movement
                : TrafficConnectorMovement.Ambiguous;
        }

        public static TurnDirection ClassifyCenterConnectorTurn(
            EntityManager entityManager,
            Entity intersectionNode,
            Entity sourceEdge,
            Entity targetEdge,
            CarLaneFlags flags)
        {
            if ((flags & CarLaneFlags.TurnLeft) != 0)
            {
                return TurnDirection.Left;
            }

            if ((flags & CarLaneFlags.TurnRight) != 0)
            {
                return TurnDirection.Right;
            }

            return TryClassifyTurnByGeometry(
                    entityManager,
                    intersectionNode,
                    sourceEdge,
                    targetEdge,
                    out TurnDirection turn)
                ? turn
                : TurnDirection.Ambiguous;
        }

        private static bool TryClassifyByGeometry(
            EntityManager entityManager,
            Entity node,
            Entity sourceEdge,
            Entity targetEdge,
            float straightDotThreshold,
            bool classifyOpposingAsUturn,
            out TrafficConnectorMovement movement)
        {
            movement = TrafficConnectorMovement.Ambiguous;
            if (!TryGetMovementGeometry(entityManager, node, sourceEdge, targetEdge, out float cross, out float dot))
            {
                return false;
            }

            if (math.abs(cross) < StraightCrossThreshold)
            {
                if (dot > straightDotThreshold)
                {
                    movement = TrafficConnectorMovement.Straight;
                    return true;
                }

                if (classifyOpposingAsUturn)
                {
                    movement = TrafficConnectorMovement.Uturn;
                    return true;
                }

                return true;
            }

            movement = cross > 0f ? TrafficConnectorMovement.Left : TrafficConnectorMovement.Right;
            return true;
        }

        private static bool TryClassifyTurnByGeometry(
            EntityManager entityManager,
            Entity node,
            Entity sourceEdge,
            Entity targetEdge,
            out TurnDirection turn)
        {
            turn = TurnDirection.Ambiguous;
            if (!TryGetMovementGeometry(entityManager, node, sourceEdge, targetEdge, out float cross, out _))
            {
                return false;
            }

            if (math.abs(cross) < StraightCrossThreshold)
            {
                return true;
            }

            turn = cross > 0f ? TurnDirection.Left : TurnDirection.Right;
            return true;
        }

        private static bool TryGetMovementGeometry(
            EntityManager entityManager,
            Entity node,
            Entity sourceEdge,
            Entity targetEdge,
            out float cross,
            out float dot)
        {
            cross = 0f;
            dot = 0f;
            if (!NetTopologyHelpers.TryGetEdgeDirectionFromNode(entityManager, sourceEdge, node, out float2 sourceOutward) ||
                !NetTopologyHelpers.TryGetEdgeDirectionFromNode(entityManager, targetEdge, node, out float2 targetOutward))
            {
                return false;
            }

            float2 incoming = -sourceOutward;
            cross = NetTopologyHelpers.Cross(incoming, targetOutward);
            dot = math.dot(incoming, targetOutward);
            return true;
        }
    }
}
