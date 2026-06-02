using Colossal.Entities;
using Game.Prefabs;
using Unity.Entities;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal sealed class RoadPrefabEligibility
    {
        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;

        internal RoadPrefabEligibility(
            EntityManager entityManager,
            PrefabSystem prefabSystem)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
        }

        private EntityManager EntityManager => m_EntityManager;

        internal bool IsBridgeRoadEdge(Entity edgeEntity, out string detail)
        {
            if (edgeEntity == Entity.Null ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
            {
                detail = "prefabRef=missing";
                return false;
            }

            return IsBridgeRoadPrefab(prefabRef.m_Prefab, out detail);
        }

        internal bool IsBridgeRoadPrefab(Entity prefabEntity, out string detail)
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

        internal bool IsHighwayRoadEdge(Entity edgeEntity, out string detail)
        {
            if (edgeEntity == Entity.Null ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
            {
                detail = "prefabRef=missing";
                return false;
            }

            return IsHighwayRoadPrefab(prefabRef.m_Prefab, out detail);
        }

        internal bool IsHighwayRoadPrefab(Entity prefabEntity, out string detail)
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

        internal static bool IsHighwayRoadData(RoadData roadData)
        {
            return (roadData.m_Flags & RoadFlags.UseHighwayRules) != 0;
        }

        internal void GetRoadContentProfile(Entity prefabEntity, out bool isDlc, out string detail)
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

        private string GetPrefabNameFromPrefab(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null)
            {
                return "<null prefab>";
            }

            if (m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                return prefabBase.name;
            }

            return $"<unresolved {FormatEntity(prefabEntity)}>";
        }

        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }
    }
}
