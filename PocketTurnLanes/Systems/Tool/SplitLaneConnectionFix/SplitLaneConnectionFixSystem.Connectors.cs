using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Entities;
using NetCarLane = Game.Net.CarLane;
using NetTrackLane = Game.Net.TrackLane;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private int CollectConnectorLanes(Entity splitNode, Entity outerEdge, Entity pocketEdge, List<ConnectorLane> output)
        {
            output.Clear();
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return 0;
            }

            CollectConnectorLanes(splitNode, outerEdge, pocketEdge, subLanes, output);
            return output.Count;
        }

        private void CollectConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    !TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    sourceEdge != outerEdge ||
                    targetEdge != pocketEdge)
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private void CollectTrackConnectorLanes(
            Entity splitNode,
            Entity sourceEdge,
            Entity targetEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if (!TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity actualSourceEdge, out Entity actualTargetEdge) ||
                    !IsTrackConnectorCandidate(laneEntity, subLane) ||
                    actualSourceEdge != sourceEdge ||
                    actualTargetEdge != targetEdge)
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, actualSourceEdge, actualTargetEdge));
            }
        }

        private void CollectCenterConnectorLanes(
            Entity centerNode,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output,
            bool roadOnly)
        {
            output.Clear();
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                PathMethod pathMethods = subLane.m_PathMethods;
                if ((roadOnly ? (pathMethods & PathMethod.Road) == 0 : pathMethods == 0) ||
                    !TryGetConnectorLaneEdges(centerNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    (roadOnly && !EntityManager.HasComponent<NetCarLane>(laneEntity)) ||
                    sourceEdge == Entity.Null ||
                    targetEdge == Entity.Null ||
                    !IsEdgeConnectedToNode(sourceEdge, centerNode) ||
                    !IsEdgeConnectedToNode(targetEdge, centerNode))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private bool TryGetConnectorLaneEdges(
            Entity node,
            SubLane subLane,
            out Entity laneEntity,
            out Lane lane,
            out Entity sourceEdge,
            out Entity targetEdge)
        {
            laneEntity = subLane.m_SubLane;
            lane = default;
            sourceEdge = Entity.Null;
            targetEdge = Entity.Null;
            return laneEntity != Entity.Null &&
                   EntityManager.Exists(laneEntity) &&
                   !EntityManager.HasComponent<Deleted>(laneEntity) &&
                   !NetTopologyHelpers.IsMasterConnectorLane(EntityManager, laneEntity) &&
                   EntityManager.TryGetComponent(laneEntity, out lane) &&
                   NetTopologyHelpers.TryGetConnectedEdgesFromLane(EntityManager, node, lane, out sourceEdge, out targetEdge);
        }

        private ConnectorLane CreateConnectorLane(
            Entity laneEntity,
            int subLaneIndex,
            SubLane subLane,
            Lane lane,
            Entity sourceEdge,
            Entity targetEdge)
        {
            NetCarLane carLane = EntityManager.TryGetComponent(laneEntity, out NetCarLane laneComponent)
                ? laneComponent
                : default;
            LaneFlags laneFlags = default;
            TrackTypes trackTypes = default;
            bool hasTrackLaneData = false;
            if (EntityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef))
            {
                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData))
                {
                    laneFlags = laneData.m_Flags;
                }

                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out TrackLaneData trackLaneData))
                {
                    hasTrackLaneData = true;
                    trackTypes = trackLaneData.m_TrackTypes;
                }
            }

            return new ConnectorLane
            {
                Entity = laneEntity,
                SubLaneIndex = subLaneIndex,
                PathMethods = subLane.m_PathMethods,
                CarFlags = carLane.m_Flags,
                SourceEdge = sourceEdge,
                TargetEdge = targetEdge,
                SourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff,
                TargetLaneIndex = lane.m_EndNode.GetLaneIndex() & 0xff,
                LaneFlags = laneFlags,
                TrackTypes = trackTypes,
                HasTrackLaneData = hasTrackLaneData,
                HasNetTrackLane = EntityManager.HasComponent<NetTrackLane>(laneEntity)
            };
        }

        private bool IsTrackConnectorCandidate(Entity laneEntity, SubLane subLane)
        {
            if ((subLane.m_PathMethods & PathMethod.Track) != 0 ||
                EntityManager.HasComponent<NetTrackLane>(laneEntity))
            {
                return true;
            }

            if (EntityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef))
            {
                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out TrackLaneData _))
                {
                    return true;
                }

                if (EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData) &&
                    (laneData.m_Flags & LaneFlags.Track) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void CollectSplitNodeConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            bool restrictToSplitPair = outerEdge != Entity.Null;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 ||
                    !TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    (restrictToSplitPair &&
                     (sourceEdge != outerEdge && sourceEdge != pocketEdge ||
                      targetEdge != outerEdge && targetEdge != pocketEdge)))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

        private int CountStaleSplitNodeUturnConnectorLanes(Entity splitNode, Entity outerEdge, Entity pocketEdge, out string summary)
        {
            summary = string.Empty;
            m_StaleConnectorLanes.Clear();
            if (!EntityManager.TryGetBuffer(splitNode, true, out DynamicBuffer<SubLane> subLanes))
            {
                return 0;
            }

            CollectStaleSplitNodeUturnConnectorLanes(splitNode, outerEdge, pocketEdge, subLanes, m_StaleConnectorLanes);
            summary = FormatConnectorLanes(m_StaleConnectorLanes);
            return m_StaleConnectorLanes.Count;
        }

        private void CollectStaleSplitNodeUturnConnectorLanes(
            Entity splitNode,
            Entity outerEdge,
            Entity pocketEdge,
            DynamicBuffer<SubLane> subLanes,
            List<ConnectorLane> output)
        {
            output.Clear();
            bool restrictToSplitPair = outerEdge != Entity.Null;
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                if (!TryGetConnectorLaneEdges(splitNode, subLane, out Entity laneEntity, out Lane lane, out Entity sourceEdge, out Entity targetEdge) ||
                    ((subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0 &&
                     !IsTrackConnectorCandidate(laneEntity, subLane)) ||
                    !lane.m_StartNode.OwnerEquals(lane.m_EndNode) ||
                    sourceEdge != targetEdge ||
                    (restrictToSplitPair && sourceEdge != outerEdge && sourceEdge != pocketEdge))
                {
                    continue;
                }

                output.Add(CreateConnectorLane(laneEntity, i, subLane, lane, sourceEdge, targetEdge));
            }
        }

    }
}
