using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private bool TryBuildReplacementDefinitionRequest(
            ReplacementCandidate candidate,
            out ReplacementDefinitionRequest request)
        {
            request = default;

            if (!EntityManager.Exists(candidate.PocketEdge) ||
                !EntityManager.TryGetComponent(candidate.PocketEdge, out Edge edge) ||
                !EntityManager.TryGetComponent(candidate.PocketEdge, out Curve curve))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot build replacement definition pocket={FormatEntity(candidate.PocketEdge)}: missing Edge or Curve.");
                return false;
            }

            if (EntityManager.HasComponent<Owner>(candidate.PocketEdge))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Skip pocket lane replacement pocket={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)}: owned sub-net edges are not replaced yet.");
                return false;
            }

            Entity startNode = edge.m_Start;
            Entity endNode = edge.m_End;
            Bezier4x3 bezier = curve.m_Bezier;
            CreationFlags flags = CreationFlags.Align | CreationFlags.SubElevation;
            if (candidate.InvertTarget)
            {
                Entity oldStart = startNode;
                startNode = endNode;
                endNode = oldStart;
                bezier = MathUtils.Invert(bezier);
                flags |= CreationFlags.Invert;
            }

            int randomSeed = EntityManager.TryGetComponent(candidate.PocketEdge, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : candidate.PocketEdge.Index;
            int fixedIndex = EntityManager.TryGetComponent(candidate.PocketEdge, out Fixed fixedData)
                ? fixedData.m_Index
                : -1;

            request = new ReplacementDefinitionRequest
            {
                OriginalEdge = candidate.PocketEdge,
                Prefab = candidate.TargetPrefab,
                Curve = bezier,
                Length = MathUtils.Length(bezier),
                StartNode = startNode,
                EndNode = endNode,
                Flags = flags,
                FixedIndex = fixedIndex,
                RandomSeed = randomSeed,
                HasUpgraded = candidate.HasTargetUpgrade,
                Upgraded = candidate.TargetUpgrade
            };
            return true;
        }

        private bool TryBuildPreviewSourceDefinitionRequest(
            Entity sourceEdge,
            Entity sourcePrefab,
            out ReplacementDefinitionRequest request)
        {
            request = default;

            if (!EntityManager.Exists(sourceEdge) ||
                !EntityManager.TryGetComponent(sourceEdge, out Edge edge) ||
                !EntityManager.TryGetComponent(sourceEdge, out Curve curve))
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot build source preview definition edge={FormatEntity(sourceEdge)}: missing Edge or Curve.");
                return false;
            }

            Entity prefab = sourcePrefab;
            if (prefab == Entity.Null &&
                EntityManager.TryGetComponent(sourceEdge, out PrefabRef prefabRef))
            {
                prefab = prefabRef.m_Prefab;
            }

            if (prefab == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot build source preview definition edge={FormatEntity(sourceEdge)}: missing source prefab.");
                return false;
            }

            return TryBuildPreviewSourceDefinitionRequest(
                sourceEdge,
                prefab,
                edge.m_Start,
                edge.m_End,
                curve.m_Bezier,
                curve.m_Length,
                out request);
        }

        private bool TryBuildPreviewSourceDefinitionRequest(
            Entity sourceEdge,
            Entity sourcePrefab,
            Entity startNode,
            Entity endNode,
            Bezier4x3 curve,
            float length,
            out ReplacementDefinitionRequest request)
        {
            request = default;

            if (!EntityManager.Exists(sourceEdge) ||
                sourcePrefab == Entity.Null ||
                startNode == Entity.Null ||
                endNode == Entity.Null)
            {
                Mod.LogDiagnostic($"[IntersectionTool] Cannot build source preview definition edge={FormatEntity(sourceEdge)}: missing source prefab or endpoints prefab={GetPrefabNameFromPrefab(sourcePrefab)} start={FormatEntity(startNode)} end={FormatEntity(endNode)}.");
                return false;
            }

            int randomSeed = EntityManager.TryGetComponent(sourceEdge, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : sourceEdge.Index;
            int fixedIndex = EntityManager.TryGetComponent(sourceEdge, out Fixed fixedData)
                ? fixedData.m_Index
                : -1;

            request = new ReplacementDefinitionRequest
            {
                OriginalEdge = sourceEdge,
                Prefab = sourcePrefab,
                Curve = curve,
                Length = length > 0.01f ? length : MathUtils.Length(curve),
                StartNode = startNode,
                EndNode = endNode,
                Flags = CreationFlags.Recreate | CreationFlags.Align | CreationFlags.SubElevation,
                FixedIndex = fixedIndex,
                RandomSeed = randomSeed
            };
            return true;
        }
    }
}
