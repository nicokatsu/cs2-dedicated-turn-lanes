using Colossal.Entities;
using Game.Tools;
using Unity.Entities;

namespace PocketTurnLanes.Tool
{
    internal static class TempEntityHelpers
    {
        internal static bool IsUsableTemp(Temp temp)
        {
            return (temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) == (TempFlags)0;
        }

        internal static bool IsSameOrTempOriginal(
            EntityManager entityManager,
            Entity entity,
            Entity original)
        {
            if (entity == original)
            {
                return true;
            }

            return entity != Entity.Null &&
                   entityManager.TryGetComponent(entity, out Temp temp) &&
                   temp.m_Original == original &&
                   IsUsableTemp(temp);
        }

        internal static Entity ResolveTempOriginal(
            EntityManager entityManager,
            Entity entity,
            int maxDepth = 4)
        {
            Entity original = entity;
            for (int i = 0; i < maxDepth; i++)
            {
                if (!entityManager.TryGetComponent(original, out Temp temp) ||
                    temp.m_Original == Entity.Null ||
                    !IsUsableTemp(temp))
                {
                    break;
                }

                original = temp.m_Original;
            }

            return original;
        }
    }
}
