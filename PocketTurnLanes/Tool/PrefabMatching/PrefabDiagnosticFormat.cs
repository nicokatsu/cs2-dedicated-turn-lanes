using Game.Prefabs;
using Unity.Entities;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal static class PrefabDiagnosticFormat
    {
        public static string GetPrefabName(PrefabSystem prefabSystem, Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null)
            {
                return "<null prefab>";
            }

            if (prefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                return prefabBase.name;
            }

            return $"<unresolved {DiagnosticFormat.Entity(prefabEntity)}>";
        }
    }
}
