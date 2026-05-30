using System;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Tool;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class IntersectionToolSystem
    {
        private enum ApproachMovement
        {
            Ambiguous,
            Straight,
            Left,
            Right
        }

        private readonly struct ApproachMovementKey : IEquatable<ApproachMovementKey>
        {
            public readonly ApproachMovement Movement;
            public readonly Entity TargetEdge;

            public ApproachMovementKey(ApproachMovement movement, Entity targetEdge)
            {
                Movement = movement;
                TargetEdge = targetEdge;
            }

            public bool IsTurn => Movement == ApproachMovement.Left || Movement == ApproachMovement.Right;

            public bool Equals(ApproachMovementKey other)
            {
                return Movement == other.Movement && TargetEdge == other.TargetEdge;
            }

            public override bool Equals(object obj)
            {
                return obj is ApproachMovementKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)Movement;
                    hash = (hash * 397) ^ TargetEdge.Index;
                    hash = (hash * 397) ^ TargetEdge.Version;
                    return hash;
                }
            }
        }

        private struct ApproachLaneUsage
        {
            public int LaneIndex;
            public int Straight;
            public int Left;
            public int Right;
            public int Ambiguous;

            public int KnownMovementCount
            {
                get
                {
                    int count = 0;
                    if (Straight > 0)
                    {
                        count++;
                    }

                    if (Left > 0)
                    {
                        count++;
                    }

                    if (Right > 0)
                    {
                        count++;
                    }

                    return count;
                }
            }

            public void Add(ApproachMovement movement)
            {
                switch (movement)
                {
                    case ApproachMovement.Straight:
                        Straight++;
                        break;
                    case ApproachMovement.Left:
                        Left++;
                        break;
                    case ApproachMovement.Right:
                        Right++;
                        break;
                    default:
                        Ambiguous++;
                        break;
                }
            }

            public bool IsDedicated(ApproachMovement movement)
            {
                if (Ambiguous > 0 || KnownMovementCount != 1)
                {
                    return false;
                }

                switch (movement)
                {
                    case ApproachMovement.Left:
                        return Left > 0;
                    case ApproachMovement.Right:
                        return Right > 0;
                    case ApproachMovement.Straight:
                        return Straight > 0;
                    default:
                        return false;
                }
            }
        }

        private struct SplitDefinitionRequest
        {
            public Entity Edge;
            public Entity Prefab;
            public float3 HitPosition;
            public float CurvePosition;
            public int RandomSeed;
        }

        private struct EdgeDeletionDefinitionRequest
        {
            public Entity Edge;
            public Entity StartNode;
            public Entity EndNode;
            public Bezier4x3 Curve;
            public float Length;
        }

        private struct NodeMergeDefinitionRequest
        {
            public Entity Prefab;
            public Entity RemovedNode;
            public Entity StartNode;
            public Entity EndNode;
            public Bezier4x3 MergedCurve;
            public float MergedLength;
            public int RandomSeed;
            public bool HasUpgraded;
            public Upgraded Upgraded;
            public EdgeDeletionDefinitionRequest FirstDeletion;
            public EdgeDeletionDefinitionRequest SecondDeletion;
        }

        private struct SplitCandidate
        {
            public Entity Node;
            public Entity Edge;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public bool InvertTarget;
            public float CurvePosition;
            public float3 HitPosition;
            public float TargetDistance;
            public float SplitDistance;
            public float IntersectionDistance;
            public float PocketDistance;
            public int OriginalForwardLanes;
            public int OriginalBackwardLanes;
            public int TargetForwardLanes;
            public int TargetBackwardLanes;
            public int Attempt;
        }

        private struct NodeMergeCandidate
        {
            public Entity Node;
            public Entity ShortEdge;
            public Entity RemovableNode;
            public Entity ContinuationEdge;
            public Entity FarNode;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public bool InvertTarget;
            public float ShortEdgeLength;
            public float ContinuationEdgeLength;
            public float MergedLength;
            public float ExpectedSplitPosition;
            public float ExpectedSplitDistance;
            public float ExpectedIntersectionDistance;
            public float ExpectedPocketDistance;
            public float ExpectedTargetDistance;
            public float3 ExpectedHitPosition;
            public int OriginalForwardLanes;
            public int OriginalBackwardLanes;
            public int TargetForwardLanes;
            public int TargetBackwardLanes;
            public NodeMergeDefinitionRequest MergeRequest;
        }

        private struct ReplacementDefinitionRequest
        {
            public Entity OriginalEdge;
            public Entity Prefab;
            public Entity StartNode;
            public Entity EndNode;
            public Bezier4x3 Curve;
            public float Length;
            public CreationFlags Flags;
            public int FixedIndex;
            public int RandomSeed;
            public bool PreviewOnly;
        }

        private struct ReplacementPreviewDefinition : IComponentData
        {
        }

        private struct ReplacementPrefabMatch
        {
            public Entity Prefab;
            public bool Invert;
            public RoadLaneCounts OriginalCounts;
            public RoadLaneCounts TargetCounts;
            public RoadLaneCounts CandidateCounts;
            public int Score;
        }

        private struct ReplacementCandidate
        {
            public Entity Node;
            public Entity SplitNode;
            public Entity OriginalEdge;
            public Entity PocketEdge;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public bool InvertTarget;
            public float3 HitPosition;
            public int OriginalForwardLanes;
            public int OriginalBackwardLanes;
            public int TargetForwardLanes;
            public int TargetBackwardLanes;
        }

        private struct CreateNodeMergeDefinitionJob : IJob
        {
            [ReadOnly]
            public NodeMergeDefinitionRequest Request;

            public EntityCommandBuffer ECB;

            public void Execute()
            {
                if (Request.Prefab == Entity.Null ||
                    Request.StartNode == Entity.Null ||
                    Request.EndNode == Entity.Null ||
                    Request.FirstDeletion.Edge == Entity.Null ||
                    Request.SecondDeletion.Edge == Entity.Null)
                {
                    return;
                }

                Entity definitionEntity = ECB.CreateEntity();
                ECB.AddComponent(definitionEntity, new CreationDefinition
                {
                    m_Original = Entity.Null,
                    m_Prefab = Request.Prefab,
                    m_Flags = CreationFlags.Construction | CreationFlags.Upgrade,
                    m_RandomSeed = Request.RandomSeed
                });
                if (Request.HasUpgraded)
                {
                    ECB.AddComponent(definitionEntity, Request.Upgraded);
                }

                ECB.AddComponent<Updated>(definitionEntity);
                ECB.AddComponent(definitionEntity, new NetCourse
                {
                    m_Curve = Request.MergedCurve,
                    m_Length = Request.MergedLength,
                    m_FixedIndex = -1,
                    m_Elevation = default,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = Request.StartNode,
                        m_Position = Request.MergedCurve.a,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(Request.MergedCurve)),
                        m_CourseDelta = 0f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsFirst,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = Request.EndNode,
                        m_Position = Request.MergedCurve.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(Request.MergedCurve)),
                        m_CourseDelta = 1f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsLast,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    }
                });

                AddDeleteDefinition(Request.FirstDeletion);
                AddDeleteDefinition(Request.SecondDeletion);
            }

            private void AddDeleteDefinition(EdgeDeletionDefinitionRequest deletion)
            {
                if (deletion.Edge == Entity.Null ||
                    deletion.StartNode == Entity.Null ||
                    deletion.EndNode == Entity.Null)
                {
                    return;
                }

                Entity definitionEntity = ECB.CreateEntity();
                ECB.AddComponent(definitionEntity, new CreationDefinition
                {
                    m_Original = deletion.Edge,
                    m_Flags = CreationFlags.Delete | CreationFlags.Hidden
                });
                ECB.AddComponent<Updated>(definitionEntity);
                ECB.AddComponent(definitionEntity, new NetCourse
                {
                    m_Curve = deletion.Curve,
                    m_Length = deletion.Length,
                    m_FixedIndex = -1,
                    m_Elevation = default,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = deletion.StartNode,
                        m_Position = deletion.Curve.a,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(deletion.Curve)),
                        m_CourseDelta = 0f,
                        m_Elevation = default,
                        m_Flags = default,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = deletion.EndNode,
                        m_Position = deletion.Curve.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(deletion.Curve)),
                        m_CourseDelta = 1f,
                        m_Elevation = default,
                        m_Flags = default,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    }
                });
            }
        }

        private struct CreateSplitDefinitionJob : IJob
        {
            [ReadOnly]
            public SplitDefinitionRequest Request;

            public EntityCommandBuffer ECB;

            public void Execute()
            {
                if (Request.Prefab == Entity.Null)
                {
                    return;
                }

                Entity definitionEntity = ECB.CreateEntity();
                ECB.AddComponent(definitionEntity, new CreationDefinition
                {
                    m_Original = Entity.Null,
                    m_Prefab = Request.Prefab,
                    m_Flags = CreationFlags.Construction,
                    m_RandomSeed = Request.RandomSeed
                });
                ECB.AddComponent<Updated>(definitionEntity);
                ECB.AddComponent(definitionEntity, new NetCourse
                {
                    m_Curve = new Bezier4x3(Request.HitPosition, Request.HitPosition, Request.HitPosition, Request.HitPosition),
                    m_Length = 0f,
                    m_FixedIndex = -1,
                    m_Elevation = default,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = Request.Edge,
                        m_Position = Request.HitPosition,
                        m_Rotation = default,
                        m_CourseDelta = 0f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsRight | CoursePosFlags.IsLeft,
                        m_ParentMesh = -1,
                        m_SplitPosition = Request.CurvePosition
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = Request.Edge,
                        m_Position = Request.HitPosition,
                        m_Rotation = default,
                        m_CourseDelta = 1f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsRight | CoursePosFlags.IsLeft,
                        m_ParentMesh = -1,
                        m_SplitPosition = Request.CurvePosition
                    }
                });
            }
        }

        private struct CreateReplacementDefinitionJob : IJob
        {
            [ReadOnly]
            public ReplacementDefinitionRequest Request;

            public EntityCommandBuffer ECB;

            public void Execute()
            {
                if (Request.Prefab == Entity.Null ||
                    Request.OriginalEdge == Entity.Null ||
                    Request.StartNode == Entity.Null ||
                    Request.EndNode == Entity.Null)
                {
                    return;
                }

                Entity definitionEntity = ECB.CreateEntity();
                ECB.AddComponent(definitionEntity, new CreationDefinition
                {
                    m_Original = Request.OriginalEdge,
                    m_Prefab = Request.Prefab,
                    m_Flags = Request.Flags,
                    m_RandomSeed = Request.RandomSeed
                });
                if (Request.PreviewOnly)
                {
                    ECB.AddComponent<ReplacementPreviewDefinition>(definitionEntity);
                }

                ECB.AddComponent<Updated>(definitionEntity);
                ECB.AddComponent(definitionEntity, new NetCourse
                {
                    m_Curve = Request.Curve,
                    m_Length = Request.Length,
                    m_FixedIndex = Request.FixedIndex,
                    m_Elevation = default,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = Request.StartNode,
                        m_Position = Request.Curve.a,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(Request.Curve)),
                        m_CourseDelta = 0f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsFirst,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = Request.EndNode,
                        m_Position = Request.Curve.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(Request.Curve)),
                        m_CourseDelta = 1f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsLast,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    }
                });
            }
        }
    }
}
