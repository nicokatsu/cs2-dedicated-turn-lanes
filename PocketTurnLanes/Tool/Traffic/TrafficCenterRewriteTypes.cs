using System.Collections.Generic;
using Game.Net;
using Unity.Entities;

namespace PocketTurnLanes.Tool.Traffic
{
    internal enum TurnDirection
    {
        Ambiguous,
        Left,
        Right
    }

    internal enum CenterRewriteMovement
    {
        Ambiguous,
        Straight,
        SmallTurn,
        BigTurn,
        Uturn
    }

    internal sealed class CenterRewritePlan
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

    internal struct CenterPreservationStats
    {
        public int Connections;
        public int UturnConnections;
        public int NonRoadConnections;
        public int UnsafeConnections;
        public int Skipped;
    }

    internal readonly struct CenterConnectorCandidate
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

    internal sealed class CenterLaneMovementSummary
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

    internal struct CenterTurnCandidate
    {
        public Entity LaneEntity;
        public int SourceLaneIndex;
        public int TargetListIndex;
        public int TargetLaneIndex;
        public Entity TargetEdge;
        public TurnDirection Turn;
        public CarLaneFlags Flags;
    }
}
