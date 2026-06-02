using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficCenterMappingBuilder
    {
        public static bool TryAddCandidateMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            CenterConnectorCandidate candidate,
            LaneEndpoint sourceEndpoint,
            LaneEndpoint targetEndpoint,
            bool preserveUnsafe,
            bool forceSafeStraight,
            out string reason)
        {
            reason = string.Empty;
            if (!candidate.HasTargetEndpoint)
            {
                reason = $"missing target endpoint target={FormatEntity(candidate.Connector.TargetEdge)}:{candidate.Connector.TargetLaneIndex}";
                return false;
            }

            PathMethod method = GetCenterRoadRewriteMethod(
                candidate.Connector.PathMethods,
                sourceEndpoint,
                targetEndpoint);
            if ((method & PathMethod.Road) == 0)
            {
                reason = $"road method unavailable source={FormatEntity(sourceEndpoint.Edge)}:{sourceEndpoint.LaneIndex} target={FormatEntity(targetEndpoint.Edge)}:{targetEndpoint.LaneIndex}";
                return false;
            }

            bool unsafeConnection = preserveUnsafe &&
                                    !forceSafeStraight &&
                                    (candidate.Connector.CarFlags & (CarLaneFlags.Unsafe | CarLaneFlags.Forbidden)) != 0;
            LaneMapping mapping = new LaneMapping
            {
                SourceEdge = sourceEndpoint.Edge,
                TargetEdge = targetEndpoint.Edge,
                SourceLaneIndex = sourceEndpoint.LaneIndex,
                TargetLaneIndex = targetEndpoint.LaneIndex,
                TrafficLanePositionMap = new float3x2(sourceEndpoint.LanePosition, targetEndpoint.LanePosition),
                TrafficCarriagewayAndGroupIndexMap = new int4(sourceEndpoint.CarriagewayAndGroup, targetEndpoint.CarriagewayAndGroup),
                Method = method,
                TemplateEntity = candidate.Connector.Entity,
                TemplatePathMethods = candidate.Connector.PathMethods,
                IsUnsafe = unsafeConnection,
                HasTrafficMaps = true
            };
            TrafficMappingPlanMerge.AddOrMergeCenter(bySource, mapping);
            sourceEndpoints[new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex)] = sourceEndpoint;
            targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = targetEndpoint;
            return true;
        }

        public static bool TryAddShiftedStraightMapping(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            Dictionary<SourceLaneKey, LaneEndpoint> sourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> targetEndpoints,
            CenterConnectorCandidate straightCandidate,
            LaneEndpoint smallSourceEndpoint,
            LaneEndpoint shiftedTargetEndpoint,
            out string reason)
        {
            reason = string.Empty;
            PathMethod method = GetCenterRoadRewriteMethod(
                straightCandidate.Connector.PathMethods,
                smallSourceEndpoint,
                shiftedTargetEndpoint);
            if ((method & PathMethod.Road) == 0)
            {
                reason = $"road method unavailable source={FormatEntity(smallSourceEndpoint.Edge)}:{smallSourceEndpoint.LaneIndex} target={FormatEntity(shiftedTargetEndpoint.Edge)}:{shiftedTargetEndpoint.LaneIndex}";
                return false;
            }

            LaneMapping mapping = new LaneMapping
            {
                SourceEdge = smallSourceEndpoint.Edge,
                TargetEdge = shiftedTargetEndpoint.Edge,
                SourceLaneIndex = smallSourceEndpoint.LaneIndex,
                TargetLaneIndex = shiftedTargetEndpoint.LaneIndex,
                TrafficLanePositionMap = new float3x2(smallSourceEndpoint.LanePosition, shiftedTargetEndpoint.LanePosition),
                TrafficCarriagewayAndGroupIndexMap = new int4(smallSourceEndpoint.CarriagewayAndGroup, shiftedTargetEndpoint.CarriagewayAndGroup),
                Method = method,
                TemplateEntity = straightCandidate.Connector.Entity,
                TemplatePathMethods = straightCandidate.Connector.PathMethods,
                IsUnsafe = false,
                HasTrafficMaps = true
            };
            TrafficMappingPlanMerge.AddOrMergeCenter(bySource, mapping);
            sourceEndpoints[new SourceLaneKey(mapping.SourceEdge, mapping.SourceLaneIndex)] = smallSourceEndpoint;
            targetEndpoints[new TargetLaneKey(mapping.TargetEdge, mapping.TargetLaneIndex)] = shiftedTargetEndpoint;
            return true;
        }

        public static int CountRoadBicycleMappings(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource)
        {
            int count = 0;
            foreach (Dictionary<TargetLaneKey, LaneMapping> byTarget in bySource.Values)
            {
                foreach (LaneMapping mapping in byTarget.Values)
                {
                    if ((mapping.Method & PathMethod.Road) != 0 &&
                        (mapping.Method & PathMethod.Bicycle) != 0)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        public static int CountTrafficPlanConnections(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource)
        {
            int count = 0;
            foreach (Dictionary<TargetLaneKey, LaneMapping> byTarget in bySource.Values)
            {
                count += byTarget.Count;
            }

            return count;
        }

        public static void MergeApproachPlan(
            CenterPlan plan,
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> approachBySource,
            Dictionary<SourceLaneKey, LaneEndpoint> approachSourceEndpoints,
            Dictionary<TargetLaneKey, LaneEndpoint> approachTargetEndpoints)
        {
            foreach (KeyValuePair<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> sourcePair in approachBySource)
            {
                foreach (LaneMapping mapping in sourcePair.Value.Values)
                {
                    TrafficMappingPlanMerge.AddOrMergeCenter(plan.BySource, mapping);
                }
            }

            foreach (KeyValuePair<SourceLaneKey, LaneEndpoint> pair in approachSourceEndpoints)
            {
                plan.SourceEndpoints[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<TargetLaneKey, LaneEndpoint> pair in approachTargetEndpoints)
            {
                plan.TargetEndpoints[pair.Key] = pair.Value;
            }
        }

        private static PathMethod GetCenterRoadRewriteMethod(
            PathMethod templateMethod,
            LaneEndpoint source,
            LaneEndpoint target)
        {
            PathMethod method = TrafficPathMethods.RestrictTrafficPathMethodToEndpoints(
                PathMethod.Road,
                source,
                target);
            if ((method & PathMethod.Road) == 0)
            {
                return 0;
            }

            method |= templateMethod & PathMethod.Bicycle;
            return TrafficPathMethods.SanitizeCenterTrafficPathMethod(method);
        }

        private static string FormatEntity(Unity.Entities.Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }
    }
}
