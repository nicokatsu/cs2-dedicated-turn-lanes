using System.Collections.Generic;
using Game.Pathfind;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private static readonly TrafficPlanAuditPolicy OuterTrafficPlanAuditPolicy =
            new TrafficPlanAuditPolicy("outerSuppressSplitPairUturns", TrafficPlanUturnPolicy.Suppress, true);

        private static readonly TrafficPlanAuditPolicy CenterTrafficPlanAuditPolicy =
            new TrafficPlanAuditPolicy("centerPreserveUturns", TrafficPlanUturnPolicy.Preserve, false);

        private static TrafficPlanAuditStats AuditTrafficMappingPlan(
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
            HashSet<SourceLaneKey> auditedSources = new HashSet<SourceLaneKey>();
            for (int sourceIndex = 0; sourceIndex < sourceKeys.Count; sourceIndex++)
            {
                SourceLaneKey sourceKey = sourceKeys[sourceIndex];
                if (!bySource.TryGetValue(sourceKey, out Dictionary<TargetLaneKey, LaneMapping> byTarget))
                {
                    continue;
                }

                auditedSources.Add(sourceKey);
                bool staleUturnSource = uturnSuppressionSourceKeys != null &&
                                        uturnSuppressionSourceKeys.Contains(sourceKey);
                bool runtimeNonUturnSource = runtimeNonUturnSourceKeys != null &&
                                             runtimeNonUturnSourceKeys.Contains(sourceKey);
                int initialConnections = byTarget.Count;
                int suppressedUturns = 0;
                if (policy.UturnPolicy == TrafficPlanUturnPolicy.Suppress && byTarget.Count > 0)
                {
                    List<TargetLaneKey> removeTargets = new List<TargetLaneKey>(2);
                    foreach (KeyValuePair<TargetLaneKey, LaneMapping> targetPair in byTarget)
                    {
                        if (targetPair.Value.SourceEdge == targetPair.Value.TargetEdge)
                        {
                            removeTargets.Add(targetPair.Key);
                        }
                    }

                    for (int i = 0; i < removeTargets.Count; i++)
                    {
                        byTarget.Remove(removeTargets[i]);
                        suppressedUturns++;
                    }
                }

                stats.SuppressedUturnConnections += suppressedUturns;

                bool emptyOverride = false;
                bool removedEmpty = false;
                if (byTarget.Count == 0)
                {
                    if (policy.UturnPolicy == TrafficPlanUturnPolicy.Suppress &&
                        policy.AllowEmptyUturnSuppression &&
                        staleUturnSource &&
                        !runtimeNonUturnSource)
                    {
                        emptyOverride = true;
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

                int roadConnections = 0;
                int preservationConnections = 0;
                int unsafeConnections = 0;
                int trackConnections = 0;
                int preservedUturns = 0;
                if (!removedEmpty)
                {
                    foreach (LaneMapping mapping in byTarget.Values)
                    {
                        if (mapping.IsPreservationOnly)
                        {
                            preservationConnections++;
                        }
                        else
                        {
                            roadConnections++;
                        }

                        if (mapping.SourceEdge == mapping.TargetEdge)
                        {
                            preservedUturns++;
                        }

                        if (mapping.IsUnsafe)
                        {
                            unsafeConnections++;
                        }

                        if ((mapping.Method & PathMethod.Track) != 0)
                        {
                            trackConnections++;
                        }
                    }

                    if (roadConnections > 0)
                    {
                        stats.RoadSources++;
                    }

                    if (preservationConnections > 0)
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
                }
                else if (staleUturnSource)
                {
                    stats.UturnSourcesLeftForDirectCleanup++;
                    if (runtimeNonUturnSource)
                    {
                        stats.RuntimeNonUturnSuppressionSkipped++;
                    }
                }

                stats.RoadConnections += roadConnections;
                stats.PreservationConnections += preservationConnections;
                stats.PreservedUturnConnections += preservedUturns;
                stats.UnsafeConnections += unsafeConnections;
                stats.TrackConnections += trackConnections;

                bool roadSourceKey = roadRepairSourceKeys != null && roadRepairSourceKeys.Contains(sourceKey);
                bool preservationSourceKey = preservationSourceKeys != null && preservationSourceKeys.Contains(sourceKey);
                string decision = removedEmpty
                    ? "removedEmpty"
                    : emptyOverride
                        ? "emptyOverride"
                        : roadConnections > 0 && preservationConnections > 0
                            ? "road+preserve"
                            : roadConnections > 0
                                ? "road"
                                : preservationConnections > 0
                                    ? "preserve"
                                    : "empty";
                decisions.Add(
                    $"{FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex}" +
                    $" decision={decision}" +
                    $" initial={initialConnections}" +
                    $" final={(removedEmpty ? 0 : byTarget.Count)}" +
                    $" road={roadConnections}" +
                    $" preserved={preservationConnections}" +
                    $" preservedUturn={preservedUturns}" +
                    $" suppressedUturn={suppressedUturns}" +
                    $" unsafe={unsafeConnections}" +
                    $" track={trackConnections}" +
                    $" roadKey={roadSourceKey}" +
                    $" preservationKey={preservationSourceKey}" +
                    $" staleUturnKey={staleUturnSource}" +
                    $" runtimeNonUturn={runtimeNonUturnSource}");
            }

            if (uturnSuppressionSourceKeys != null)
            {
                foreach (SourceLaneKey sourceKey in uturnSuppressionSourceKeys)
                {
                    if (auditedSources.Contains(sourceKey))
                    {
                        continue;
                    }

                    bool runtimeNonUturnSource = runtimeNonUturnSourceKeys != null &&
                                                 runtimeNonUturnSourceKeys.Contains(sourceKey);
                    stats.UturnSourcesLeftForDirectCleanup++;
                    if (runtimeNonUturnSource)
                    {
                        stats.RuntimeNonUturnSuppressionSkipped++;
                    }

                    decisions.Add(
                        $"{FormatEntity(sourceKey.Edge)}:{sourceKey.LaneIndex}" +
                        $" decision=directCleanupOnly" +
                        $" initial=0 final=0 road=0 preserved=0 preservedUturn=0 suppressedUturn=0 unsafe=0 track=0" +
                        $" roadKey=False preservationKey=False staleUturnKey=True runtimeNonUturn={runtimeNonUturnSource}");
                }
            }

            stats.FinalSources = bySource.Count;
            stats.SourceDecisions = decisions.Count == 0 ? "<none>" : string.Join(" | ", decisions);
            return stats;
        }

        private static string FormatTrafficPlanAuditStats(TrafficPlanAuditStats stats)
        {
            string policy = string.IsNullOrEmpty(stats.Policy) ? "notRun" : stats.Policy;
            string sourceDecisions = string.IsNullOrEmpty(stats.SourceDecisions) ? "<none>" : stats.SourceDecisions;
            return $"policy={policy} initialSources={stats.InitialSources} finalSources={stats.FinalSources} roadSources={stats.RoadSources} preservationSources={stats.PreservationSources} roadConnections={stats.RoadConnections} preservationConnections={stats.PreservationConnections} preservedUturn={stats.PreservedUturnConnections} suppressedUturn={stats.SuppressedUturnConnections} emptyOverrideSources={stats.EmptyOverrideSources} removedEmptySources={stats.RemovedEmptySources} skippedSources={stats.SkippedSources} unsafeConnections={stats.UnsafeConnections} trackConnections={stats.TrackConnections} uturnSourcesCovered={stats.UturnSourcesCoveredByPlan} uturnEmptyOverrides={stats.UturnSourcesCoveredByEmptyOverride} uturnDirectCleanupFallback={stats.UturnSourcesLeftForDirectCleanup} runtimeNonUturnSuppressionSkipped={stats.RuntimeNonUturnSuppressionSkipped} sourceDecisions=({sourceDecisions})";
        }
    }
}
