using System.Collections.Generic;
using Game.Pathfind;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficMappingPlanMerge
    {
        public static void AddOrMergeFinal(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping)
        {
            AddOrMerge(bySource, mapping, TrafficPathMethodMergeMode.FinalRepair);
        }

        public static List<LaneMapping> CreateRoadRepairMappings(
            LaneMapping[] forwardMappings,
            LaneMapping[] reverseMappings)
        {
            List<LaneMapping> allMappings = new List<LaneMapping>(
                (forwardMappings?.Length ?? 0) +
                (reverseMappings?.Length ?? 0));
            AddRoadRepairMappings(forwardMappings, allMappings);
            AddRoadRepairMappings(reverseMappings, allMappings);
            return allMappings;
        }

        public static void AddRoadRepairMappings(
            LaneMapping[] mappings,
            List<LaneMapping> output)
        {
            if (mappings == null)
            {
                return;
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                output.Add(NormalizeRoadRepairMapping(mappings[i]));
            }
        }

        public static LaneMapping NormalizeRoadRepairMapping(LaneMapping mapping)
        {
            mapping.Method = TrafficPathMethods.GetRoadRepairPathMethod(mapping.Method, mapping.PreserveSharedTrack);
            mapping.IsPreservationOnly = false;
            mapping.IsUnsafe = false;
            return mapping;
        }

        public static void AddOrMergeCenter(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping)
        {
            AddOrMerge(bySource, mapping, TrafficPathMethodMergeMode.Center);
        }

        public static void AddOrMerge(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            LaneMapping mapping,
            TrafficPathMethodMergeMode mode)
        {
            SourceLaneKey sourceKey = new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex);
            TargetLaneKey targetKey = new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex);
            if (!bySource.TryGetValue(sourceKey, out Dictionary<TargetLaneKey, LaneMapping> byTarget))
            {
                byTarget = new Dictionary<TargetLaneKey, LaneMapping>();
                bySource.Add(sourceKey, byTarget);
            }

            PathMethod mergeMethod = TrafficPathMethods.SanitizeMappingMethod(
                mapping.Method,
                mode,
                mapping.HasPreservedPathMethods || mode == TrafficPathMethodMergeMode.Center);
            if (mergeMethod == 0)
            {
                return;
            }

            if (byTarget.TryGetValue(targetKey, out LaneMapping existing))
            {
                bool preserveUnsafe = existing.IsPreservationOnly && mapping.IsPreservationOnly;
                bool hasPreservedPathMethods = existing.HasPreservedPathMethods || mapping.HasPreservedPathMethods;
                existing.Method = TrafficPathMethods.SanitizeMappingMethod(
                    existing.Method | mergeMethod,
                    mode,
                    hasPreservedPathMethods || mode == TrafficPathMethodMergeMode.Center);
                existing.IsBranch |= mapping.IsBranch;
                existing.IsPreservationOnly &= mapping.IsPreservationOnly;
                existing.HasPreservedPathMethods = hasPreservedPathMethods;
                existing.PreserveSharedTrack |= mapping.PreserveSharedTrack;
                existing.IsUnsafe = mode == TrafficPathMethodMergeMode.Center
                    ? existing.IsUnsafe || mapping.IsUnsafe
                    : preserveUnsafe && (existing.IsUnsafe || mapping.IsUnsafe);
                if (!existing.HasTrafficMaps && mapping.HasTrafficMaps)
                {
                    existing.TrafficLanePositionMap = mapping.TrafficLanePositionMap;
                    existing.TrafficCarriagewayAndGroupIndexMap = mapping.TrafficCarriagewayAndGroupIndexMap;
                    existing.HasTrafficMaps = true;
                }

                byTarget[targetKey] = existing;
                return;
            }

            mapping.Method = mergeMethod;
            byTarget.Add(targetKey, mapping);
        }
    }
}
