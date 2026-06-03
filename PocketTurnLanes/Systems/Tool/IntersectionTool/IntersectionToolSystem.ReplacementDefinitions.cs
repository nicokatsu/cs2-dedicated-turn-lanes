using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Tool.PrefabMatching;
using Unity.Entities;
using Unity.Jobs;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private JobHandle ScheduleSplitDefinition(SplitDefinitionRequest request, JobHandle inputDeps)
        {
            JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
            {
                Request = request,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(inputDeps);

            m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
            return createDefinitionJobHandle;
        }

        private JobHandle ScheduleReplacementDefinition(ReplacementDefinitionRequest request, JobHandle inputDeps)
        {
            JobHandle createDefinitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = request,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(inputDeps);

            m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
            return createDefinitionJobHandle;
        }

        private static SplitCandidate CreateSplitCandidate(
            Entity node,
            Entity edge,
            SplitDefinitionPlan splitPlan,
            ReplacementPrefabMatch prefabMatch)
        {
            return new SplitCandidate
            {
                Node = node,
                Edge = edge,
                SourcePrefab = splitPlan.Request.Prefab,
                TargetPrefab = prefabMatch.Prefab,
                InvertTarget = prefabMatch.Invert,
                HasTargetUpgrade = prefabMatch.HasTargetUpgrade,
                TargetUpgrade = prefabMatch.TargetUpgrade,
                CurvePosition = splitPlan.CurvePosition,
                HitPosition = splitPlan.Request.HitPosition,
                TargetDistance = splitPlan.TargetDistance,
                TargetPocketLength = splitPlan.TargetPocketLength,
                SplitDistance = splitPlan.SplitDistance,
                IntersectionDistance = splitPlan.IntersectionDistance,
                PocketDistance = splitPlan.PocketDistance,
                OriginalForwardLanes = prefabMatch.OriginalCounts.Forward,
                OriginalBackwardLanes = prefabMatch.OriginalCounts.Backward,
                TargetForwardLanes = prefabMatch.TargetCounts.Forward,
                TargetBackwardLanes = prefabMatch.TargetCounts.Backward,
                Attempt = 0
            };
        }

        private static SplitCandidate UpdateSplitCandidate(
            SplitCandidate candidate,
            SplitDefinitionPlan splitPlan,
            int attempt)
        {
            candidate.CurvePosition = splitPlan.CurvePosition;
            candidate.HitPosition = splitPlan.Request.HitPosition;
            candidate.TargetDistance = splitPlan.TargetDistance;
            candidate.TargetPocketLength = splitPlan.TargetPocketLength;
            candidate.SplitDistance = splitPlan.SplitDistance;
            candidate.IntersectionDistance = splitPlan.IntersectionDistance;
            candidate.PocketDistance = splitPlan.PocketDistance;
            candidate.Attempt = attempt;
            return candidate;
        }

        private static SplitCandidate CreateSplitCandidate(
            NodeMergeCandidate candidate,
            Entity edge,
            SplitDefinitionRequest request,
            float curvePosition,
            float splitDistance,
            float intersectionDistance,
            float pocketDistance,
            float targetDistance,
            float targetPocketLength)
        {
            return new SplitCandidate
            {
                Node = candidate.Node,
                FarNode = candidate.FarNode,
                Edge = edge,
                SourcePrefab = candidate.MergeRequest.Prefab,
                TargetPrefab = candidate.TargetPrefab,
                LaneRepairMode = candidate.LaneRepairMode,
                InvertTarget = candidate.PostMergeInvertTarget,
                HasTargetUpgrade = candidate.HasTargetUpgrade,
                TargetUpgrade = candidate.TargetUpgrade,
                CurvePosition = curvePosition,
                HitPosition = request.HitPosition,
                TargetDistance = targetDistance,
                TargetPocketLength = targetPocketLength,
                SplitDistance = splitDistance,
                IntersectionDistance = intersectionDistance,
                PocketDistance = pocketDistance,
                OriginalForwardLanes = candidate.OriginalForwardLanes,
                OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                TargetForwardLanes = candidate.TargetForwardLanes,
                TargetBackwardLanes = candidate.TargetBackwardLanes,
                Attempt = 0,
                FarIntersectionSnapshot = candidate.FarIntersectionSnapshot
            };
        }

        private static ReplacementCandidate CreateReplacementCandidate(
            SplitCandidate candidate,
            Entity splitNode,
            Entity pocketEdge)
        {
            return new ReplacementCandidate
            {
                Node = candidate.Node,
                FarNode = candidate.FarNode,
                SplitNode = splitNode,
                OriginalEdge = candidate.Edge,
                PocketEdge = pocketEdge,
                SourcePrefab = candidate.SourcePrefab,
                TargetPrefab = candidate.TargetPrefab,
                LaneRepairMode = candidate.LaneRepairMode,
                InvertTarget = candidate.InvertTarget,
                HasTargetUpgrade = candidate.HasTargetUpgrade,
                TargetUpgrade = candidate.TargetUpgrade,
                HitPosition = candidate.HitPosition,
                OriginalForwardLanes = candidate.OriginalForwardLanes,
                OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                TargetForwardLanes = candidate.TargetForwardLanes,
                TargetBackwardLanes = candidate.TargetBackwardLanes,
                FarIntersectionSnapshot = candidate.FarIntersectionSnapshot
            };
        }

        private static ReplacementCandidate CreateShortEdgeReplacementCandidate(
            NodeMergeCandidate candidate,
            Entity splitNode,
            Entity pocketEdge,
            bool includeTransitionState)
        {
            ReplacementCandidate replacementCandidate = new ReplacementCandidate
            {
                Node = candidate.Node,
                FarNode = includeTransitionState ? candidate.FarNode : Entity.Null,
                SplitNode = splitNode,
                OriginalEdge = candidate.ShortEdge,
                PocketEdge = pocketEdge,
                SourcePrefab = candidate.SourcePrefab,
                TargetPrefab = candidate.TargetPrefab,
                LaneRepairMode = candidate.LaneRepairMode,
                InvertTarget = candidate.InvertTarget,
                HasTargetUpgrade = candidate.HasTargetUpgrade,
                TargetUpgrade = candidate.TargetUpgrade,
                HitPosition = candidate.ExpectedHitPosition,
                OriginalForwardLanes = candidate.OriginalForwardLanes,
                OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                TargetForwardLanes = candidate.TargetForwardLanes,
                TargetBackwardLanes = candidate.TargetBackwardLanes,
                FarIntersectionSnapshot = candidate.FarIntersectionSnapshot
            };

            if (includeTransitionState)
            {
                replacementCandidate.TransitionOuterEdge = candidate.ContinuationEdge;
                replacementCandidate.TransitionReverseSnapshot = candidate.TransitionReverseSnapshot;
            }

            return replacementCandidate;
        }

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
