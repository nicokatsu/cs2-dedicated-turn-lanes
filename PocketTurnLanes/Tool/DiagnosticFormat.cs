using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool
{
    internal static class DiagnosticFormat
    {
        public static string Entity(Entity entity)
        {
            return $"{entity.Index}:{entity.Version}";
        }

        public static string Float3(float3 value)
        {
            return $"({value.x:0.##},{value.y:0.##},{value.z:0.##})";
        }
    }
}
