using Game.Pathfind;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficMappingPlanPreservation
    {
        public static LaneMapping CreatePreservationMapping(
            TrafficGeneratedSnapshot snapshot,
            PathMethod method)
        {
            return new LaneMapping
            {
                SourceEdge = snapshot.SourceEdge,
                TargetEdge = snapshot.TargetEdge,
                SourceLaneIndex = snapshot.SourceLaneIndex,
                TargetLaneIndex = snapshot.TargetLaneIndex,
                TrafficLanePositionMap = snapshot.LanePositionMap,
                TrafficCarriagewayAndGroupIndexMap = snapshot.CarriagewayAndGroupIndexMap,
                Method = method,
                IsBranch = false,
                IsPreservationOnly = true,
                IsUnsafe = snapshot.IsUnsafe,
                HasTrafficMaps = true,
                HasPreservedPathMethods = true
            };
        }

        public static void CountTrackStats(
            TrafficMappingPlan plan,
            PathMethod method,
            LaneEndpoint targetEndpoint)
        {
            CountPreservedTrackStats(plan, method, targetEndpoint);
        }

        public static void CountPreservedConnectionStats(
            TrafficMappingPlan plan,
            PathMethod method,
            bool isUnsafe,
            LaneEndpoint targetEndpoint)
        {
            if ((method & ~PathMethod.Road) != 0)
            {
                plan.PreservationNonRoadConnections++;
            }

            if (isUnsafe)
            {
                plan.PreservationUnsafeConnections++;
            }

            CountPreservedTrackStats(plan, method, targetEndpoint);
        }

        public static void AddOrMergePreservationMapping(
            TrafficMappingPlan plan,
            LaneMapping mapping,
            SourceLaneKey sourceKey,
            LaneEndpoint targetEndpoint)
        {
            TrafficMappingPlanMerge.AddOrMergeFinal(plan.BySource, mapping);
            plan.PreservationSourceKeys.Add(sourceKey);
            CountPreservedConnectionStats(
                plan,
                mapping.Method,
                mapping.IsUnsafe,
                targetEndpoint);
        }

        private static void CountPreservedTrackStats(
            TrafficMappingPlan plan,
            PathMethod method,
            LaneEndpoint targetEndpoint)
        {
            if ((method & PathMethod.Track) == 0)
            {
                return;
            }

            plan.PreservationTrackConnections++;
            if (TrafficPathMethods.IsTrackOnlyEndpoint(targetEndpoint))
            {
                plan.PreservationTrackOnlyTargets++;
            }

            if ((method & (PathMethod.Road | PathMethod.Track)) == (PathMethod.Road | PathMethod.Track))
            {
                plan.PreservationSharedTrackConnections++;
            }
        }
    }
}
