using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal sealed class TrafficMappingPlan
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
        public int PreservationTrackConnections;
        public int PreservationTrackOnlyTargets;
        public int PreservationSharedTrackConnections;
        public int StaleUturnConnections;
        public TrafficPlanAuditStats AuditStats;
    }

    internal struct TrafficPlanAuditStats
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
}
