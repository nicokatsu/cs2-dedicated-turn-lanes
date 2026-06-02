using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;

namespace PocketTurnLanes.Tool.Traffic
{
    internal enum TrafficPathMethodMergeMode
    {
        FinalRepair,
        CenterRewrite
    }

    internal readonly struct TrafficLaneCapabilities
    {
        public readonly PathMethod PathMethods;
        public readonly LaneFlags LaneFlags;
        public readonly RoadTypes RoadTypes;
        public readonly TrackTypes TrackTypes;
        public readonly bool HasTrackLaneData;
        public readonly bool HasNetTrackLane;

        public TrafficLaneCapabilities(
            PathMethod pathMethods,
            LaneFlags laneFlags,
            RoadTypes roadTypes,
            TrackTypes trackTypes,
            bool hasTrackLaneData,
            bool hasNetTrackLane)
        {
            PathMethods = pathMethods;
            LaneFlags = laneFlags;
            RoadTypes = roadTypes;
            TrackTypes = trackTypes;
            HasTrackLaneData = hasTrackLaneData;
            HasNetTrackLane = hasNetTrackLane;
        }
    }

    internal static class TrafficPathMethods
    {
        public static PathMethod SanitizeMappingMethod(
            PathMethod method,
            TrafficPathMethodMergeMode mode,
            bool preservePathMethods)
        {
            if (preservePathMethods)
            {
                return SanitizePreservedTrafficPathMethod(method);
            }

            return mode == TrafficPathMethodMergeMode.CenterRewrite
                ? SanitizeCenterTrafficPathMethod(method)
                : SanitizeTrafficPathMethod(method);
        }

        public static PathMethod SanitizeCenterTrafficPathMethod(PathMethod method)
        {
            return SanitizePreservedTrafficPathMethod(method);
        }

        public static PathMethod RestrictCenterTrafficPathMethodToEndpoints(
            PathMethod method,
            TrafficLaneCapabilities source,
            TrafficLaneCapabilities target)
        {
            return RestrictPreservedTrafficPathMethodToEndpoints(method, source, target);
        }

        public static PathMethod SanitizePreservedTrafficPathMethod(PathMethod method)
        {
            return method;
        }

        public static PathMethod GetLayerPreservationPathMethod(PathMethod method, bool preserveUturn)
        {
            if (method == 0)
            {
                return 0;
            }

            if (preserveUturn)
            {
                return SanitizePreservedTrafficPathMethod(method);
            }

            PathMethod preserved = method & PathMethod.Track;
            if ((method & PathMethod.Road) == 0)
            {
                preserved |= method & ~PathMethod.Road;
            }

            return SanitizePreservedTrafficPathMethod(preserved);
        }

        public static PathMethod RestrictPreservedTrafficPathMethodToEndpoints(
            PathMethod method,
            TrafficLaneCapabilities source,
            TrafficLaneCapabilities target)
        {
            PathMethod roadAndTrack = RestrictTrafficPathMethodToEndpoints(
                method & (PathMethod.Road | PathMethod.Track),
                source,
                target);
            PathMethod otherMethods = method & ~(PathMethod.Road | PathMethod.Track);
            return SanitizePreservedTrafficPathMethod(roadAndTrack | otherMethods);
        }

        public static PathMethod GetMappingMethod(
            TrafficLaneCapabilities source,
            TrafficLaneCapabilities target)
        {
            PathMethod method = 0;
            if (SupportsRoadPath(source) && SupportsRoadPath(target))
            {
                method |= PathMethod.Road;
                if (SupportsBicycleRoadPath(source) && SupportsBicycleRoadPath(target))
                {
                    method |= PathMethod.Bicycle;
                }
            }

            if (SupportsTrackPath(source) &&
                SupportsTrackPath(target) &&
                TrackTypesCompatible(source.TrackTypes, target.TrackTypes))
            {
                method |= PathMethod.Track;
            }

            if (method == 0)
            {
                method = SupportsTrackPath(source) && SupportsTrackPath(target)
                    ? PathMethod.Track
                    : PathMethod.Road;
            }

            return SanitizeTrafficPathMethod(method);
        }

        public static PathMethod SanitizeTrafficPathMethod(PathMethod method)
        {
            method &= PathMethod.Road | PathMethod.Track | PathMethod.Bicycle;
            return method == 0 ? PathMethod.Road : method;
        }

        public static PathMethod RestrictTrafficPathMethodToEndpoints(
            PathMethod method,
            TrafficLaneCapabilities source,
            TrafficLaneCapabilities target)
        {
            method &= PathMethod.Road | PathMethod.Track | PathMethod.Bicycle;
            if (!SupportsRoadPath(source) || !SupportsRoadPath(target))
            {
                method &= ~(PathMethod.Road | PathMethod.Bicycle);
            }

            if ((method & PathMethod.Road) == 0 ||
                !SupportsBicycleRoadPath(source) ||
                !SupportsBicycleRoadPath(target))
            {
                method &= ~PathMethod.Bicycle;
            }

            if (!SupportsTrackPath(source) ||
                !SupportsTrackPath(target) ||
                !TrackTypesCompatible(source.TrackTypes, target.TrackTypes))
            {
                method &= ~PathMethod.Track;
            }

            return method;
        }

        public static bool SupportsRoadPath(TrafficLaneCapabilities endpoint)
        {
            return (endpoint.PathMethods & PathMethod.Road) != 0 &&
                   (endpoint.LaneFlags & LaneFlags.Road) != 0 &&
                   (endpoint.RoadTypes & RoadTypes.Car) != 0;
        }

        public static bool SupportsBicycleRoadPath(TrafficLaneCapabilities endpoint)
        {
            return SupportsRoadPath(endpoint) &&
                   (endpoint.PathMethods & PathMethod.Bicycle) != 0 &&
                   (endpoint.RoadTypes & RoadTypes.Bicycle) != 0;
        }

        public static bool SupportsTrackPath(TrafficLaneCapabilities endpoint)
        {
            return (endpoint.LaneFlags & LaneFlags.Track) != 0 &&
                   ((endpoint.PathMethods & PathMethod.Track) != 0 ||
                    endpoint.HasTrackLaneData ||
                    endpoint.HasNetTrackLane);
        }

        public static bool IsTrackOnlyEndpoint(TrafficLaneCapabilities endpoint)
        {
            return SupportsTrackPath(endpoint) && !SupportsRoadPath(endpoint);
        }

        public static bool TrackTypesCompatible(TrackTypes source, TrackTypes target)
        {
            return EqualityComparer<TrackTypes>.Default.Equals(source, default) ||
                   EqualityComparer<TrackTypes>.Default.Equals(target, default) ||
                   !EqualityComparer<TrackTypes>.Default.Equals(source & target, default);
        }
    }
}
