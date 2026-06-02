using System;
using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
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

        public sealed class TransitionConnectionSnapshot
        {
            public Entity Node;
            public Entity SourceEdge;
            public Entity TargetEdge;
            public string Source;
            public string Detail;
            public TransitionConnectionSnapshotMapping[] Mappings;
        }

        public struct TransitionConnectionSnapshotMapping
        {
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public float SourceLateral;
            public float TargetLateral;
            public float3 SourceLanePosition;
            public float3 TargetLanePosition;
            public int2 SourceCarriagewayAndGroup;
            public int2 TargetCarriagewayAndGroup;
            public PathMethod Method;
            public bool IsUnsafe;
        }

        public struct TrafficEndpointSnapshot
        {
            public bool HasEndpoint;
            public float Lateral;
            public int Order;
        }

        public struct TrafficGeneratedSnapshot
        {
            public Entity SourceEdge;
            public Entity TargetEdge;
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public float3x2 LanePositionMap;
            public int4 CarriagewayAndGroupIndexMap;
            public PathMethod Method;
            public bool IsUnsafe;
            public TrafficEndpointSnapshot SourceEndpoint;
            public TrafficEndpointSnapshot TargetEndpoint;
        }

        public struct TrafficSourceSnapshot
        {
            public Entity SourceEdge;
            public int SourceLaneIndex;
            public int2 SourceCarriagewayAndGroup;
            public float3 SourceLanePosition;
            public Entity ModifiedConnectionEntity;
            public bool HasGeneratedBuffer;
            public TrafficEndpointSnapshot SourceEndpoint;
            public TrafficGeneratedSnapshot[] Connections;
        }

        public sealed class FarIntersectionTrafficSnapshot
        {
            public Entity Node;
            public Entity ContinuationEdge;
            public string Source;
            public string Detail;
            public TrafficSourceSnapshot[] Entries;
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

        private enum TurnDirection
        {
            Ambiguous,
            Left,
            Right
        }

        private enum CenterRewriteMovement
        {
            Ambiguous,
            Straight,
            SmallTurn,
            BigTurn,
            Uturn
        }

        private enum TrafficMappingMergeMode
        {
            FinalRepair,
            CenterRewrite
        }

        private enum TrafficPlanUturnPolicy
        {
            Preserve,
            Suppress
        }

        private struct TrafficPlanAuditPolicy
        {
            public string Name;
            public TrafficPlanUturnPolicy UturnPolicy;
            public bool AllowEmptyUturnSuppression;

            public TrafficPlanAuditPolicy(
                string name,
                TrafficPlanUturnPolicy uturnPolicy,
                bool allowEmptyUturnSuppression)
            {
                Name = name;
                UturnPolicy = uturnPolicy;
                AllowEmptyUturnSuppression = allowEmptyUturnSuppression;
            }
        }

        private sealed class CenterRewritePlan
        {
            public readonly Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> BySource = new Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>>();
            public readonly Dictionary<SourceLaneKey, LaneEndpoint> SourceEndpoints = new Dictionary<SourceLaneKey, LaneEndpoint>();
            public readonly Dictionary<TargetLaneKey, LaneEndpoint> TargetEndpoints = new Dictionary<TargetLaneKey, LaneEndpoint>();
            public readonly HashSet<SourceLaneKey> LegacyOffScopeSourceKeys = new HashSet<SourceLaneKey>();
            public readonly List<string> Diagnostics = new List<string>(16);
            public int ApproachesScanned;
            public int ApproachesRewritten;
            public int ApproachesSkipped;
            public int OffScopeApproaches;
            public int CenterConnectors;
            public int BigTurnApproaches;
            public int PlannedConnections;
            public int StraightConnectionsWrittenSafe;
            public int StraightUnsafeCleared;
            public int SmallTurnConnectionsClearedFromStraightLane;
            public int PreservedRuntimeConnections;
            public int PreservedSnapshotConnections;
            public int PreservedUturnConnections;
            public int PreservedNonRoadConnections;
            public int PreservedUnsafeConnections;
            public int PreservationSkipped;
            public int BicycleConnectionsWrittenWithRoad;
            public bool LeftHandTraffic;
            public TurnDirection BigTurn;
            public TurnDirection SmallTurn;
            public TrafficPlanAuditStats AuditStats;
        }

        private struct CenterPreservationStats
        {
            public int Connections;
            public int UturnConnections;
            public int NonRoadConnections;
            public int UnsafeConnections;
            public int Skipped;
        }

        private readonly struct CenterConnectorCandidate
        {
            public readonly ConnectorLane Connector;
            public readonly CenterRewriteMovement Movement;
            public readonly LaneEndpoint SourceEndpoint;
            public readonly LaneEndpoint TargetEndpoint;
            public readonly bool HasTargetEndpoint;

            public CenterConnectorCandidate(
                ConnectorLane connector,
                CenterRewriteMovement movement,
                LaneEndpoint sourceEndpoint,
                LaneEndpoint targetEndpoint,
                bool hasTargetEndpoint)
            {
                Connector = connector;
                Movement = movement;
                SourceEndpoint = sourceEndpoint;
                TargetEndpoint = targetEndpoint;
                HasTargetEndpoint = hasTargetEndpoint;
            }
        }

        private sealed class CenterLaneMovementSummary
        {
            public readonly LaneEndpoint SourceEndpoint;
            public readonly List<CenterConnectorCandidate> Straight = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> SmallTurn = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> BigTurn = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> Uturn = new List<CenterConnectorCandidate>(2);
            public readonly List<CenterConnectorCandidate> Other = new List<CenterConnectorCandidate>(2);

            public CenterLaneMovementSummary(LaneEndpoint sourceEndpoint)
            {
                SourceEndpoint = sourceEndpoint;
            }

            public bool IsSmallTurnExclusive =>
                SmallTurn.Count > 0 &&
                Straight.Count == 0 &&
                BigTurn.Count == 0 &&
                Other.Count == 0;

            public bool IsBigTurnAndStraight =>
                BigTurn.Count > 0 &&
                Straight.Count > 0 &&
                SmallTurn.Count == 0 &&
                Other.Count == 0;

            public bool IsBigTurnExclusive =>
                BigTurn.Count > 0 &&
                Straight.Count == 0 &&
                SmallTurn.Count == 0 &&
                Other.Count == 0;

            public bool IsSmallTurnAndStraight =>
                SmallTurn.Count > 0 &&
                Straight.Count > 0 &&
                BigTurn.Count == 0 &&
                Other.Count == 0;

            public void Add(CenterConnectorCandidate candidate)
            {
                switch (candidate.Movement)
                {
                    case CenterRewriteMovement.Straight:
                        Straight.Add(candidate);
                        break;
                    case CenterRewriteMovement.SmallTurn:
                        SmallTurn.Add(candidate);
                        break;
                    case CenterRewriteMovement.BigTurn:
                        BigTurn.Add(candidate);
                        break;
                    case CenterRewriteMovement.Uturn:
                        Uturn.Add(candidate);
                        break;
                    default:
                        Other.Add(candidate);
                        break;
                }
            }
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

        private struct LaneEndpoint
        {
            public Entity LaneEntity;
            public Entity Edge;
            public int LaneIndex;
            public int OppositeLaneIndex;
            public PathNode PathNode;
            public PathNode OppositePathNode;
            public float3 Position;
            public float3 LanePosition;
            public float2 TravelDirection;
            public int2 CarriagewayAndGroup;
            public float Lateral;
            public string Endpoint;
            public PathMethod PathMethods;
            public LaneFlags LaneFlags;
            public CarLaneFlags CarFlags;
            public RoadTypes RoadTypes;
            public TrackTypes TrackTypes;
            public bool HasCarLaneData;
            public bool HasTrackLaneData;
            public bool HasNetTrackLane;
        }

        private struct ConnectorLane
        {
            public Entity Entity;
            public int SubLaneIndex;
            public PathMethod PathMethods;
            public CarLaneFlags CarFlags;
            public Entity SourceEdge;
            public Entity TargetEdge;
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public LaneFlags LaneFlags;
            public TrackTypes TrackTypes;
            public bool HasTrackLaneData;
            public bool HasNetTrackLane;
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

        private struct LaneMapping
        {
            public Entity SourceEdge;
            public Entity TargetEdge;
            public Entity TemplateEntity;
            public int SourceLaneIndex;
            public int TargetLaneIndex;
            public float3x2 TrafficLanePositionMap;
            public int4 TrafficCarriagewayAndGroupIndexMap;
            public PathMethod Method;
            public PathMethod TemplatePathMethods;
            public bool IsBranch;
            public bool IsPreservationOnly;
            public bool IsUnsafe;
            public bool HasTrafficMaps;
            public bool HasPreservedPathMethods;
        }

        private sealed class TrafficMappingPlan
        {
            public readonly Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> BySource = new Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>>();
            public readonly HashSet<SourceLaneKey> RoadRepairSourceKeys = new HashSet<SourceLaneKey>();
            public readonly HashSet<SourceLaneKey> PreservationSourceKeys = new HashSet<SourceLaneKey>();
            public readonly HashSet<SourceLaneKey> StaleUturnSourceKeys = new HashSet<SourceLaneKey>();
            public readonly HashSet<SourceLaneKey> RuntimeNonUturnSourceKeys = new HashSet<SourceLaneKey>();
            public int RoadRepairConnections;
            public int PreservationTrafficSnapshotConnections;
            public int PreservationRuntimeConnections;
            public int PreservationSkipped;
            public int ForwardPreservationConnections;
            public int ReversePreservationConnections;
            public int PreservationOverlaySnapshotConnections;
            public int PreservationOverlayRuntimeConnections;
            public int PreservationNonRoadConnections;
            public int PreservationUnsafeConnections;
            public int PreservationSuppressedUturnConnections;
            public int PreservationTrackConnections;
            public int PreservationTrackOnlyTargets;
            public int PreservationSharedTrackConnections;
            public int StaleUturnConnections;
            public int UturnSourcesCoveredByPlan;
            public int UturnSourcesCoveredByEmptyOverride;
            public int UturnSourcesLeftForDirectCleanup;
            public int RuntimeNonUturnSuppressionSkipped;
            public TrafficPlanAuditStats AuditStats;
        }

        private struct TrafficPlanAuditStats
        {
            public string Policy;
            public int InitialSources;
            public int FinalSources;
            public int RoadSources;
            public int PreservationSources;
            public int RoadConnections;
            public int PreservationConnections;
            public int PreservedUturnConnections;
            public int SuppressedUturnConnections;
            public int EmptyOverrideSources;
            public int RemovedEmptySources;
            public int SkippedSources;
            public int UnsafeConnections;
            public int TrackConnections;
            public int UturnSourcesCoveredByPlan;
            public int UturnSourcesCoveredByEmptyOverride;
            public int UturnSourcesLeftForDirectCleanup;
            public int RuntimeNonUturnSuppressionSkipped;
            public string SourceDecisions;
        }

        private struct SnapshotLaneOrder
        {
            public int LaneIndex;
            public float LateralSum;
            public int Count;
            public int FirstSnapshotOrder;

            public float AverageLateral => Count > 0 ? LateralSum / Count : 0f;
        }

        private struct CenterTurnCandidate
        {
            public Entity LaneEntity;
            public int SourceLaneIndex;
            public int TargetListIndex;
            public int TargetLaneIndex;
            public Entity TargetEdge;
            public TurnDirection Turn;
            public CarLaneFlags Flags;
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

        private readonly struct ConnectionKey : IEquatable<ConnectionKey>
        {
            public readonly int SourceLaneIndex;
            public readonly int TargetLaneIndex;

            public ConnectionKey(int sourceLaneIndex, int targetLaneIndex)
            {
                SourceLaneIndex = sourceLaneIndex;
                TargetLaneIndex = targetLaneIndex;
            }

            public bool Equals(ConnectionKey other)
            {
                return SourceLaneIndex == other.SourceLaneIndex && TargetLaneIndex == other.TargetLaneIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is ConnectionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (SourceLaneIndex * 397) ^ TargetLaneIndex;
                }
            }
        }

        private readonly struct SourceLaneKey : IEquatable<SourceLaneKey>
        {
            public readonly Entity Edge;
            public readonly int LaneIndex;

            public SourceLaneKey(Entity edge, int laneIndex)
            {
                Edge = edge;
                LaneIndex = laneIndex;
            }

            public bool Equals(SourceLaneKey other)
            {
                return Edge == other.Edge && LaneIndex == other.LaneIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is SourceLaneKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Edge.GetHashCode() * 397) ^ LaneIndex;
                }
            }
        }

        private readonly struct TargetLaneKey : IEquatable<TargetLaneKey>
        {
            public readonly Entity Edge;
            public readonly int LaneIndex;

            public TargetLaneKey(Entity edge, int laneIndex)
            {
                Edge = edge;
                LaneIndex = laneIndex;
            }

            public bool Equals(TargetLaneKey other)
            {
                return Edge == other.Edge && LaneIndex == other.LaneIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is TargetLaneKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Edge.GetHashCode() * 397) ^ LaneIndex;
                }
            }
        }
    }
}
