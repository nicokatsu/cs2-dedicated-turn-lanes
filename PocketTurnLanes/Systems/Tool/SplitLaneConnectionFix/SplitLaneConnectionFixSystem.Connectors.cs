using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using NetCarLane = Game.Net.CarLane;
using NetTrackLane = Game.Net.TrackLane;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
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

    }
}
