using System.Collections.Generic;
using Game.Pathfind;
using PocketTurnLanes.Tool;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficMappingPlanAudit
    {
        public static readonly TrafficPlanAuditPolicy OuterSuppressSplitPairUturns =
            new TrafficPlanAuditPolicy("outerSuppressSplitPairUturns", TrafficPlanUturnPolicy.Suppress, true);

        public static readonly TrafficPlanAuditPolicy CenterPreserveUturns =
            new TrafficPlanAuditPolicy("centerPreserveUturns", TrafficPlanUturnPolicy.Preserve, false);

        public static TrafficPlanAuditStats AuditAndNormalize(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            TrafficPlanAuditPolicy policy,
            HashSet<SourceLaneKey> roadRepairSourceKeys,
            HashSet<SourceLaneKey> preservationSourceKeys,
            HashSet<SourceLaneKey> uturnSuppressionSourceKeys,
            HashSet<SourceLaneKey> runtimeNonUturnSourceKeys)
        {
            TrafficPlanAuditStats stats = default;
            stats.Policy = policy.Name;
            stats.SourceDecisions = "<none>";

            if (bySource == null)
            {
                return stats;
            }

            stats.InitialSources = bySource.Count;
            List<string> decisions = new List<string>(bySource.Count);
            List<SourceLaneKey> sourceKeys = new List<SourceLaneKey>(bySource.Keys);
            HashSet<SourceLaneKey> auditedSources = uturnSuppressionSourceKeys == null
                ? null
                : new HashSet<SourceLaneKey>();
            for (int sourceIndex = 0; sourceIndex < sourceKeys.Count; sourceIndex++)
            {
                SourceLaneKey sourceKey = sourceKeys[sourceIndex];
                if (!bySource.TryGetValue(sourceKey, out Dictionary<TargetLaneKey, LaneMapping> byTarget))
                {
                    continue;
                }

                auditedSources?.Add(sourceKey);
                bool staleUturnSource = uturnSuppressionSourceKeys != null &&
                                        uturnSuppressionSourceKeys.Contains(sourceKey);
                bool runtimeNonUturnSource = runtimeNonUturnSourceKeys != null &&
                                             runtimeNonUturnSourceKeys.Contains(sourceKey);
                int initialConnections = byTarget.Count;
                int suppressedUturns = policy.UturnPolicy == TrafficPlanUturnPolicy.Suppress
                    ? SuppressTrafficPlanUturnMappings(byTarget)
                    : 0;
                stats.SuppressedUturnConnections += suppressedUturns;

                bool emptyOverride = ShouldKeepEmptyTrafficPlanSource(
                    policy,
                    staleUturnSource,
                    runtimeNonUturnSource,
                    byTarget.Count);
                bool removedEmpty = byTarget.Count == 0 && !emptyOverride;
                if (byTarget.Count == 0)
                {
                    if (emptyOverride)
                    {
                        stats.EmptyOverrideSources++;
                    }
                    else
                    {
                        bySource.Remove(sourceKey);
                        removedEmpty = true;
                        stats.RemovedEmptySources++;
                        stats.SkippedSources++;
                    }
                }

                TrafficPlanSourceAuditStats sourceStats = default;
                if (!removedEmpty)
                {
                    sourceStats = CountTrafficPlanSourceMappings(byTarget);
                    AddTrafficPlanSourceAuditStats(
                        ref stats,
                        sourceStats,
                        staleUturnSource,
                        emptyOverride);
                }
                else if (staleUturnSource)
                {
                    AddTrafficPlanDirectCleanupStats(ref stats, runtimeNonUturnSource);
                }

                bool roadSourceKey = roadRepairSourceKeys != null && roadRepairSourceKeys.Contains(sourceKey);
                bool preservationSourceKey = preservationSourceKeys != null && preservationSourceKeys.Contains(sourceKey);
                decisions.Add(FormatTrafficPlanSourceDecision(
                    sourceKey,
                    GetTrafficPlanSourceDecision(removedEmpty, emptyOverride, sourceStats),
                    initialConnections,
                    removedEmpty ? 0 : byTarget.Count,
                    sourceStats,
                    suppressedUturns,
                    roadSourceKey,
                    preservationSourceKey,
                    staleUturnSource,
                    runtimeNonUturnSource));
            }

            AddUnauditedUturnSuppressionDecisions(
                uturnSuppressionSourceKeys,
                runtimeNonUturnSourceKeys,
                auditedSources,
                decisions,
                ref stats);

            stats.FinalSources = bySource.Count;
            stats.SourceDecisions = decisions.Count == 0 ? "<none>" : string.Join(" | ", decisions);
            return stats;
        }

        public static string FormatStats(TrafficPlanAuditStats stats)
        {
            string policy = string.IsNullOrEmpty(stats.Policy) ? "notRun" : stats.Policy;
            string sourceDecisions = string.IsNullOrEmpty(stats.SourceDecisions) ? "<none>" : stats.SourceDecisions;
            return $"policy={policy} initialSources={stats.InitialSources} finalSources={stats.FinalSources} roadSources={stats.RoadSources} preservationSources={stats.PreservationSources} roadConnections={stats.RoadConnections} preservationConnections={stats.PreservationConnections} preservedUturn={stats.PreservedUturnConnections} suppressedUturn={stats.SuppressedUturnConnections} emptyOverrideSources={stats.EmptyOverrideSources} removedEmptySources={stats.RemovedEmptySources} skippedSources={stats.SkippedSources} unsafeConnections={stats.UnsafeConnections} trackConnections={stats.TrackConnections} uturnSourcesCovered={stats.UturnSourcesCoveredByPlan} uturnEmptyOverrides={stats.UturnSourcesCoveredByEmptyOverride} uturnDirectCleanupFallback={stats.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={stats.RuntimeNonUturnSuppressionSkipped} sourceDecisions=({sourceDecisions})";
        }

        private static int SuppressTrafficPlanUturnMappings(
            Dictionary<TargetLaneKey, LaneMapping> byTarget)
        {
            if (byTarget == null || byTarget.Count == 0)
            {
                return 0;
            }

            List<TargetLaneKey> removeTargets = new List<TargetLaneKey>(2);
            foreach (KeyValuePair<TargetLaneKey, LaneMapping> targetPair in byTarget)
            {
                if (IsSameEdgeTrafficMapping(targetPair.Value))
                {
                    removeTargets.Add(targetPair.Key);
                }
            }

            for (int i = 0; i < removeTargets.Count; i++)
            {
                byTarget.Remove(removeTargets[i]);
            }

            return removeTargets.Count;
        }

        private static bool ShouldKeepEmptyTrafficPlanSource(
            TrafficPlanAuditPolicy policy,
            bool staleUturnSource,
            bool runtimeNonUturnSource,
            int connectionCount)
        {
            return connectionCount == 0 &&
                   policy.UturnPolicy == TrafficPlanUturnPolicy.Suppress &&
                   policy.AllowEmptyUturnSuppression &&
                   staleUturnSource &&
                   !runtimeNonUturnSource;
        }

        private static TrafficPlanSourceAuditStats CountTrafficPlanSourceMappings(
            Dictionary<TargetLaneKey, LaneMapping> byTarget)
        {
            TrafficPlanSourceAuditStats stats = default;
            foreach (LaneMapping mapping in byTarget.Values)
            {
                if (mapping.IsPreservationOnly)
                {
                    stats.PreservationConnections++;
                }
                else
                {
                    stats.RoadConnections++;
                }

                if (IsSameEdgeTrafficMapping(mapping))
                {
                    stats.PreservedUturnConnections++;
                }

                if (mapping.IsUnsafe)
                {
                    stats.UnsafeConnections++;
                }

                if ((mapping.Method & PathMethod.Track) != 0)
                {
                    stats.TrackConnections++;
                }
            }

            return stats;
        }

        private static void AddTrafficPlanSourceAuditStats(
            ref TrafficPlanAuditStats stats,
            TrafficPlanSourceAuditStats sourceStats,
            bool staleUturnSource,
            bool emptyOverride)
        {
            if (sourceStats.RoadConnections > 0)
            {
                stats.RoadSources++;
            }

            if (sourceStats.PreservationConnections > 0)
            {
                stats.PreservationSources++;
            }

            if (staleUturnSource)
            {
                stats.UturnSourcesCoveredByPlan++;
                if (emptyOverride)
                {
                    stats.UturnSourcesCoveredByEmptyOverride++;
                }
            }

            stats.RoadConnections += sourceStats.RoadConnections;
            stats.PreservationConnections += sourceStats.PreservationConnections;
            stats.PreservedUturnConnections += sourceStats.PreservedUturnConnections;
            stats.UnsafeConnections += sourceStats.UnsafeConnections;
            stats.TrackConnections += sourceStats.TrackConnections;
        }

        private static void AddTrafficPlanDirectCleanupStats(
            ref TrafficPlanAuditStats stats,
            bool runtimeNonUturnSource)
        {
            stats.UturnSourcesLeftForDirectCleanup++;
            if (runtimeNonUturnSource)
            {
                stats.RuntimeNonUturnSuppressionSkipped++;
            }
        }

        private static void AddUnauditedUturnSuppressionDecisions(
            HashSet<SourceLaneKey> uturnSuppressionSourceKeys,
            HashSet<SourceLaneKey> runtimeNonUturnSourceKeys,
            HashSet<SourceLaneKey> auditedSources,
            List<string> decisions,
            ref TrafficPlanAuditStats stats)
        {
            if (uturnSuppressionSourceKeys == null)
            {
                return;
            }

            foreach (SourceLaneKey sourceKey in uturnSuppressionSourceKeys)
            {
                if (auditedSources != null && auditedSources.Contains(sourceKey))
                {
                    continue;
                }

                bool runtimeNonUturnSource = runtimeNonUturnSourceKeys != null &&
                                             runtimeNonUturnSourceKeys.Contains(sourceKey);
                AddTrafficPlanDirectCleanupStats(ref stats, runtimeNonUturnSource);
                decisions.Add(FormatTrafficPlanSourceDecision(
                    sourceKey,
                    "directCleanupOnly",
                    0,
                    0,
                    default,
                    0,
                    false,
                    false,
                    true,
                    runtimeNonUturnSource));
            }
        }

        private static string GetTrafficPlanSourceDecision(
            bool removedEmpty,
            bool emptyOverride,
            TrafficPlanSourceAuditStats sourceStats)
        {
            if (removedEmpty)
            {
                return "removedEmpty";
            }

            if (emptyOverride)
            {
                return "emptyOverride";
            }

            if (sourceStats.RoadConnections > 0 && sourceStats.PreservationConnections > 0)
            {
                return "road+preserve";
            }

            if (sourceStats.RoadConnections > 0)
            {
                return "road";
            }

            return sourceStats.PreservationConnections > 0 ? "preserve" : "empty";
        }

        private static string FormatTrafficPlanSourceDecision(
            SourceLaneKey sourceKey,
            string decision,
            int initialConnections,
            int finalConnections,
            TrafficPlanSourceAuditStats sourceStats,
            int suppressedUturns,
            bool roadSourceKey,
            bool preservationSourceKey,
            bool staleUturnSource,
            bool runtimeNonUturnSource)
        {
            return $"{DiagnosticFormat.Entity(sourceKey.Edge)}:{sourceKey.LaneIndex}" +
                   $" decision={decision}" +
                   $" initial={initialConnections}" +
                   $" final={finalConnections}" +
                   $" road={sourceStats.RoadConnections}" +
                   $" preserved={sourceStats.PreservationConnections}" +
                   $" preservedUturn={sourceStats.PreservedUturnConnections}" +
                   $" suppressedUturn={suppressedUturns}" +
                   $" unsafe={sourceStats.UnsafeConnections}" +
                   $" track={sourceStats.TrackConnections}" +
                   $" roadKey={roadSourceKey}" +
                   $" preservationKey={preservationSourceKey}" +
                   $" staleUturnKey={staleUturnSource}" +
                   $" runtimeNonUturn={runtimeNonUturnSource}";
        }

        private static bool IsSameEdgeTrafficMapping(LaneMapping mapping)
        {
            return mapping.SourceEdge == mapping.TargetEdge;
        }

        private struct TrafficPlanSourceAuditStats
        {
            public int RoadConnections;
            public int PreservationConnections;
            public int PreservedUturnConnections;
            public int UnsafeConnections;
            public int TrackConnections;
        }
    }

    internal enum TrafficPlanUturnPolicy
    {
        Preserve,
        Suppress
    }

    internal readonly struct TrafficPlanAuditPolicy
    {
        public readonly string Name;
        public readonly TrafficPlanUturnPolicy UturnPolicy;
        public readonly bool AllowEmptyUturnSuppression;

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
}
