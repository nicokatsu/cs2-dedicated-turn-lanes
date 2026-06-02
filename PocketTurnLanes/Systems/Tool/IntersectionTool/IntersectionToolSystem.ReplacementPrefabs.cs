using PocketTurnLanes.Tool.PrefabMatching;
using Unity.Entities;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private bool TryFindPocketLaneReplacementPrefab(
            Entity nodeEntity,
            Entity edgeEntity,
            out ReplacementPrefabMatch match)
        {
            return m_ReplacementPrefabMatcher.TryFindPocketLaneReplacementPrefab(nodeEntity, edgeEntity, out match);
        }

        private bool TryGetRoadLaneProfile(
            Entity edgeEntity,
            Entity fallbackPrefab,
            out RoadLaneProfile profile)
        {
            return m_ReplacementPrefabMatcher.TryGetRoadLaneProfile(edgeEntity, fallbackPrefab, out profile);
        }

        private bool IsBridgeRoadEdge(Entity edgeEntity, out string detail)
        {
            return m_ReplacementPrefabMatcher.IsBridgeRoadEdge(edgeEntity, out detail);
        }

        private bool IsHighwayRoadEdge(Entity edgeEntity, out string detail)
        {
            return m_ReplacementPrefabMatcher.IsHighwayRoadEdge(edgeEntity, out detail);
        }
    }
}
