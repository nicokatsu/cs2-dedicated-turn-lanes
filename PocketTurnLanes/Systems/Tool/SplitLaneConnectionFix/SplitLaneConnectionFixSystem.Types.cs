using System;
using Game.Pathfind;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;
using Unity.Mathematics;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private enum RepairMode
        {
            Standard,
            BalancedOppositeTarget,
            ShortEdgeTransition
        }

        private enum RoadDirectionState
        {
            Skipped,
            Prepared
        }

        private enum RoadDirection
        {
            Forward,
            Reverse
        }

        private static void ResetRoadPreparation(ref Request request)
        {
            request.ForwardRoadState = RoadDirectionState.Skipped;
            request.ReverseRoadState = RoadDirectionState.Skipped;
            request.ForwardRoadSkipReason = null;
            request.ReverseRoadSkipReason = null;
            request.SourceLanes = null;
            request.TargetLanes = null;
            request.ReverseSourceLanes = null;
            request.ReverseTargetLanes = null;
            request.Mappings = null;
            request.ReverseMappings = null;
            request.BranchSourceLaneIndex = -1;
            request.ExtraTargetLaneIndex = -1;
            request.Turn = TurnDirection.Ambiguous;
        }

        private static string GetTrafficWriteOrder(RepairMode mode)
        {
            return mode == RepairMode.BalancedOppositeTarget
                ? "farRestoreFirstCenterSecondOuterThird"
                : "centerFirstOuterSecond";
        }

        private static void MarkForwardRoadSkipped(ref Request request, string reason)
        {
            request.ForwardRoadState = RoadDirectionState.Skipped;
            request.ForwardRoadSkipReason = reason;
            request.Mappings = Array.Empty<LaneMapping>();
        }

        private static void MarkReverseRoadSkipped(ref Request request, string reason)
        {
            request.ReverseRoadState = RoadDirectionState.Skipped;
            request.ReverseRoadSkipReason = reason;
            request.ReverseMappings = Array.Empty<LaneMapping>();
        }

        private static void MarkForwardRoadPrepared(ref Request request)
        {
            request.ForwardRoadState = RoadDirectionState.Prepared;
            request.ForwardRoadSkipReason = null;
        }

        private static void MarkReverseRoadPrepared(ref Request request)
        {
            request.ReverseRoadState = RoadDirectionState.Prepared;
            request.ReverseRoadSkipReason = null;
        }

        private static RoadDirectionPlan GetRoadDirectionPlan(Request request, RoadDirection direction)
        {
            if (direction == RoadDirection.Forward)
            {
                return new RoadDirectionPlan(
                    RoadDirection.Forward,
                    "forward",
                    "forward",
                    "forward-road",
                    request.OuterEdge,
                    request.PocketEdge,
                    request.SourceLanes,
                    request.TargetLanes,
                    request.Mappings,
                    request.ForwardRoadState,
                    request.ForwardRoadSkipReason);
            }

            string reverseLabel = GetReverseRoadDirectionLabel(request.Mode);
            return new RoadDirectionPlan(
                RoadDirection.Reverse,
                "reverse",
                reverseLabel,
                reverseLabel,
                request.PocketEdge,
                request.OuterEdge,
                request.ReverseSourceLanes,
                request.ReverseTargetLanes,
                request.ReverseMappings,
                request.ReverseRoadState,
                request.ReverseRoadSkipReason);
        }

        private static bool HasPreparedRoadMappings(RoadDirectionPlan direction)
        {
            return direction.State == RoadDirectionState.Prepared &&
                   direction.Mappings != null &&
                   direction.Mappings.Length > 0;
        }

        private static string GetRoadDirectionInitialReason(RoadDirectionPlan direction)
        {
            return direction.State == RoadDirectionState.Skipped
                ? $"{direction.StateLabel}-skipped:{direction.SkipReason}"
                : $"{direction.StateLabel}-not-prepared";
        }

        private enum EndpointRole
        {
            SourceEndAtNode,
            TargetStartAtNode
        }

        private struct Request
        {
            public Entity IntersectionNode;
            public Entity FarIntersectionNode;
            public Entity SplitNode;
            public Entity OriginalEdge;
            public Entity OuterEdge;
            public Entity PocketEdge;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public RepairMode Mode;
            public int QueuedFrame;
            public int LaneDataRetries;
            public bool TrafficWritten;
            public bool OuterPreservationSnapshotCaptured;
            public int TrafficWriteFrame;
            public int VerificationAttempts;
            public int StableVerificationFrames;
            public bool UturnCleanupPending;
            public bool RemoveAfterUturnCleanup;
            public RoadDirectionState ForwardRoadState;
            public RoadDirectionState ReverseRoadState;
            public string ForwardRoadSkipReason;
            public string ReverseRoadSkipReason;
            public string UturnCleanupReason;
            public LaneEndpoint[] SourceLanes;
            public LaneEndpoint[] TargetLanes;
            public LaneEndpoint[] ReverseSourceLanes;
            public LaneEndpoint[] ReverseTargetLanes;
            public LaneEndpoint[] PreservationForwardSourceLanes;
            public LaneEndpoint[] PreservationForwardTargetLanes;
            public LaneEndpoint[] PreservationReverseSourceLanes;
            public LaneEndpoint[] PreservationReverseTargetLanes;
            public LaneMapping[] Mappings;
            public LaneMapping[] ReverseMappings;
            public LaneMapping[] PreservationForwardMappings;
            public LaneMapping[] PreservationReverseMappings;
            public string PreservationSkippedReason;
            public TransitionConnectionSnapshot TransitionReverseSnapshot;
            public FarIntersectionTrafficSnapshot FarIntersectionSnapshot;
            public int BranchSourceLaneIndex;
            public int ExtraTargetLaneIndex;
            public TurnDirection Turn;
        }

        private readonly struct RoadDirectionPlan
        {
            public readonly RoadDirection Direction;
            public readonly string Name;
            public readonly string Label;
            public readonly string StateLabel;
            public readonly Entity SourceEdge;
            public readonly Entity TargetEdge;
            public readonly LaneEndpoint[] SourceLanes;
            public readonly LaneEndpoint[] TargetLanes;
            public readonly LaneMapping[] Mappings;
            public readonly RoadDirectionState State;
            public readonly string SkipReason;

            public RoadDirectionPlan(
                RoadDirection direction,
                string name,
                string label,
                string stateLabel,
                Entity sourceEdge,
                Entity targetEdge,
                LaneEndpoint[] sourceLanes,
                LaneEndpoint[] targetLanes,
                LaneMapping[] mappings,
                RoadDirectionState state,
                string skipReason)
            {
                Direction = direction;
                Name = name;
                Label = label;
                StateLabel = stateLabel;
                SourceEdge = sourceEdge;
                TargetEdge = targetEdge;
                SourceLanes = sourceLanes;
                TargetLanes = targetLanes;
                Mappings = mappings;
                State = state;
                SkipReason = skipReason;
            }
        }

        private struct ForwardRoadPreparationResult
        {
            public string MappingSource;
            public string MappingReason;
            public float MappingScore;
            public string CenterTurnDiagnostic;
            public TurnDirection Turn;
            public int BranchSourceLaneIndex;
            public int ExtraTargetLaneIndex;
        }

        private struct UturnCleanupSourcePlan
        {
            public SourceLaneKey Key;
            public int2 SourceCarriagewayAndGroup;
            public float3 SourceLanePosition;
            public int FirstConnection;
            public int ConnectionCount;
        }

        private struct UturnCleanupConnectionPlan
        {
            public Entity TargetEdge;
            public int TargetLaneIndex;
            public float3x2 LanePositionMap;
            public int4 CarriagewayAndGroupIndexMap;
            public PathMethod Method;
            public bool IsUnsafe;
        }

        private struct DirectRebuildStats
        {
            public int Kept;
            public int Cloned;
            public int Deleted;
            public int DeletedUturn;
            public int Updated;
            public string Reason;
        }

        private struct PreservationMappingStats
        {
            public int Connectors;
            public int Mappings;
            public int EndpointMisses;
            public int Skipped;
            public int UturnConnections;
            public int NonRoadConnections;
            public int UnsafeConnections;
            public int TrackConnections;
            public int TrackOnlyTargets;
            public int SharedTrackConnections;
        }

        private struct UturnCleanupWriteStats
        {
            public int StaleSourceLanes;
            public int WrittenSources;
            public int PreservedConnections;
            public int PreservedTrafficSnapshotConnections;
            public int PreservedTrackConnections;
            public int TrackWrittenConnections;
            public int TrackOnlyTargetConnections;
            public int SharedTrackConnections;
            public int EmptySources;
            public int NormalizedMethods;
            public int RemovedExisting;
            public int RuntimeFallbackSuppressedConnections;
            public int TrafficSnapshotSourceLanes;
            public int MissingTrafficSnapshotSources;
            public int MissingGeneratedBufferSources;
            public int UnsafePreservedConnections;
            public int SuppressedTrafficUturnConnections;
            public string SourceLanes;
            public string RewriteSourceLanes;
            public string Reason;
        }

    }
}
