using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.SplitGeometry
{
    internal struct SafeSplitTargetPlan
    {
        public float MaxDistanceFromNode;
        public float TargetDistance;
        public float CurvePosition;
        public float SplitDistance;
        public float PocketDistance;
        public float3 HitPosition;
    }

    internal enum HalfSplitTargetPlanFailure
    {
        None,
        PocketTooShort,
        OutsideSafeRange
    }

    internal struct HalfSplitTargetPlan
    {
        public float UsableLength;
        public float HalfPocketLength;
        public float TargetDistance;
        public float CurvePosition;
        public float SplitDistance;
        public float PocketDistance;
        public float3 HitPosition;
    }

    internal struct SplitRetryRequest
    {
        public int NextAttempt;
        public float RequestedPocketLength;
        public bool RequiresMinimumProgress;
        public string Detail;
    }

    internal static class SplitGeometryMath
    {
        internal const float SplitGridSize = 8f;
        internal const float PocketLengthGridSize = 8f;
        internal const float FallbackPocketLaneLength = 32f;
        internal const float MinimumWidthBasedPocketLaneLength = 24f;
        internal const float MaximumWidthBasedPocketLaneLength = 40f;
        internal const float MaximumRetryPocketLaneLength = 64f;
        internal const float DrivableLaneEnvelopeBuffer = 16f;
        internal const float CompactRetryPocketLaneLength = 24f;
        internal const float SplitGridAlignmentTolerance = 0.05f;
        internal const float MinimumPocketLaneLength = 8f;
        internal const float MinimumPocketLaneLengthTolerance = 0.05f;
        internal const float SplitLengthBuffer = 0.16f;
        internal const float SplitRetryStep = PocketLengthGridSize;
        internal const int MaxSplitRetryAttempts = 8;
        internal const float MinimumRetryProgress = 2f;

        internal static bool TryGetSegmentWidthAtNode(float3 nodePosition, Segment segment, out float width)
        {
            float2 node = nodePosition.xz;
            float2 centerA = ((segment.m_Left.a + segment.m_Right.a) * 0.5f).xz;
            float2 centerD = ((segment.m_Left.d + segment.m_Right.d) * 0.5f).xz;

            bool useA = math.distancesq(centerA, node) <= math.distancesq(centerD, node);
            float2 left = useA ? segment.m_Left.a.xz : segment.m_Left.d.xz;
            float2 right = useA ? segment.m_Right.a.xz : segment.m_Right.d.xz;
            width = math.distance(left, right);
            return width > 0.01f && !float.IsNaN(width) && !float.IsInfinity(width);
        }

        internal static float GetGridAlignedSplitDistance(
            float intersectionDistance,
            float targetPocketLength,
            float minimumPocketLength,
            float maximumDistance)
        {
            if (maximumDistance <= 0f)
            {
                return 0f;
            }

            float maxPocketLength = maximumDistance - intersectionDistance;
            if (maxPocketLength <= 0f)
            {
                return maximumDistance;
            }

            float alignedPocketLength = AlignLengthUpToSplitGrid(targetPocketLength);
            if (alignedPocketLength <= maxPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            alignedPocketLength = AlignLengthDownToSplitGrid(maxPocketLength);
            if (alignedPocketLength >= minimumPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            return maximumDistance;
        }

        internal static float AlignLengthUpToSplitGrid(float length)
        {
            if (length <= 0f)
            {
                return 0f;
            }

            return math.ceil(math.max(0f, length - SplitGridAlignmentTolerance) / SplitGridSize) * SplitGridSize;
        }

        internal static float AlignLengthUpToPocketLengthGrid(float length)
        {
            if (length <= 0f)
            {
                return 0f;
            }

            return math.ceil(math.max(0f, length - SplitGridAlignmentTolerance) / PocketLengthGridSize) * PocketLengthGridSize;
        }

        internal static float AlignLengthDownToSplitGrid(float length)
        {
            if (length <= 0f)
            {
                return 0f;
            }

            return math.floor((length + SplitGridAlignmentTolerance) / SplitGridSize) * SplitGridSize;
        }

        internal static bool HasMinimumPocketLength(float pocketLength)
        {
            return pocketLength + SplitLengthBuffer + MinimumPocketLaneLengthTolerance >= MinimumPocketLaneLength;
        }

        internal static float GetWidthBasedPocketLaneLength(float roadWidth)
        {
            if (roadWidth <= 0f)
            {
                return FallbackPocketLaneLength;
            }

            return AlignLengthUpToPocketLengthGrid(math.clamp(
                roadWidth + DrivableLaneEnvelopeBuffer,
                MinimumWidthBasedPocketLaneLength,
                MaximumWidthBasedPocketLaneLength));
        }

        internal static float ResolveTargetPocketLength(
            float adaptiveTargetPocketLength,
            float requestedTargetPocketLength,
            out float requestedTargetPocketLengthBeforeCap,
            out float maximumRequestedPocketLength,
            out bool retryOverride)
        {
            retryOverride = requestedTargetPocketLength > 0f;
            requestedTargetPocketLengthBeforeCap = retryOverride
                ? requestedTargetPocketLength
                : adaptiveTargetPocketLength;
            maximumRequestedPocketLength = retryOverride
                ? MaximumRetryPocketLaneLength
                : MaximumWidthBasedPocketLaneLength;
            return retryOverride
                ? math.clamp(requestedTargetPocketLength, MinimumPocketLaneLength, maximumRequestedPocketLength)
                : adaptiveTargetPocketLength;
        }

        internal static float GetEffectiveMinimumPocketLength()
        {
            return math.max(0f, MinimumPocketLaneLength - SplitLengthBuffer - MinimumPocketLaneLengthTolerance);
        }

        internal static bool TryCreateSplitRetryRequest(int currentAttempt, float currentTargetPocketLength, out SplitRetryRequest request)
        {
            request = default;
            if (currentAttempt >= MaxSplitRetryAttempts)
            {
                return false;
            }

            request.NextAttempt = currentAttempt + 1;
            if (currentAttempt == 0 &&
                currentTargetPocketLength > CompactRetryPocketLaneLength + SplitGridAlignmentTolerance)
            {
                request.RequestedPocketLength = CompactRetryPocketLaneLength;
                request.RequiresMinimumProgress = false;
                request.Detail = $"mode=compact-compat compactPocket={CompactRetryPocketLaneLength:0.##}m previousPocket={currentTargetPocketLength:0.##}m";
                return true;
            }

            request.RequestedPocketLength = currentTargetPocketLength + SplitRetryStep;
            request.RequiresMinimumProgress = true;
            request.Detail = $"mode=fixed-step step={SplitRetryStep:0.##}m previousPocket={currentTargetPocketLength:0.##}m";
            return true;
        }

        internal static bool HasMinimumRetryProgress(float previousSplitDistance, float nextSplitDistance)
        {
            return nextSplitDistance >= previousSplitDistance + MinimumRetryProgress;
        }

        internal static float GetCurvePositionAtDistance(Curve curve, bool fromStart, float distance)
        {
            return GetCurvePositionAtDistance(curve.m_Bezier, fromStart, distance);
        }

        internal static float GetCurvePositionAtDistance(Bezier4x3 bezier, bool fromStart, float distance)
        {
            if (distance <= 0f)
            {
                return fromStart ? 0f : 1f;
            }

            Bezier4x3 orientedBezier = fromStart ? bezier : MathUtils.Invert(bezier);
            Bounds1 range = new Bounds1(0f, 1f);
            float remainingDistance = distance;
            float position = MathUtils.ClampLength(orientedBezier, ref range, ref remainingDistance) ? range.max : 1f;
            return fromStart ? position : 1f - position;
        }

        internal static float GetCurveDistanceFromNode(Curve curve, bool fromStart, float position)
        {
            return GetCurveDistanceFromNode(curve.m_Bezier, fromStart, position);
        }

        internal static float GetCurveDistanceFromNode(Bezier4x3 bezier, bool fromStart, float position)
        {
            position = math.saturate(position);
            Bounds1 range = fromStart ? new Bounds1(0f, position) : new Bounds1(position, 1f);
            return MathUtils.Length(bezier, range);
        }

        internal static float GetMaximumSplitDistanceFromNode(
            Curve curve,
            bool fromStart,
            float minSplit,
            float maxSplit)
        {
            return GetMaximumSplitDistanceFromNode(curve.m_Bezier, fromStart, minSplit, maxSplit);
        }

        internal static float GetMaximumSplitDistanceFromNode(
            Bezier4x3 bezier,
            bool fromStart,
            float minSplit,
            float maxSplit)
        {
            float furthestSafePosition = fromStart ? maxSplit : minSplit;
            return GetCurveDistanceFromNode(bezier, fromStart, furthestSafePosition);
        }

        internal static bool TryCalculateSafeSplitTargetPlan(
            Bezier4x3 bezier,
            bool fromStart,
            float minSplit,
            float maxSplit,
            float intersectionDistance,
            float targetPocketLength,
            out SafeSplitTargetPlan plan)
        {
            plan = default;
            plan.MaxDistanceFromNode = GetMaximumSplitDistanceFromNode(bezier, fromStart, minSplit, maxSplit);
            if (!HasMinimumPocketLength(plan.MaxDistanceFromNode - intersectionDistance))
            {
                return false;
            }

            plan.TargetDistance = GetGridAlignedSplitDistance(
                intersectionDistance,
                targetPocketLength,
                MinimumPocketLaneLength,
                plan.MaxDistanceFromNode);
            float desiredPosition = GetCurvePositionAtDistance(bezier, fromStart, plan.TargetDistance);
            plan.CurvePosition = math.clamp(desiredPosition, minSplit, maxSplit);
            plan.SplitDistance = GetCurveDistanceFromNode(bezier, fromStart, plan.CurvePosition);
            plan.PocketDistance = math.max(0f, plan.SplitDistance - intersectionDistance);
            plan.HitPosition = MathUtils.Position(bezier, plan.CurvePosition);
            return true;
        }

        internal static bool TryCalculateHalfSplitTargetPlan(
            Bezier4x3 bezier,
            bool fromStart,
            float minSplit,
            float maxSplit,
            float splitLength,
            float nearIntersectionDistance,
            float farIntersectionDistance,
            out HalfSplitTargetPlan plan,
            out HalfSplitTargetPlanFailure failure)
        {
            plan = default;
            failure = HalfSplitTargetPlanFailure.None;

            plan.UsableLength = splitLength - nearIntersectionDistance - farIntersectionDistance;
            plan.HalfPocketLength = plan.UsableLength * 0.5f;
            plan.TargetDistance = nearIntersectionDistance + plan.HalfPocketLength;

            if (!HasMinimumPocketLength(plan.HalfPocketLength))
            {
                failure = HalfSplitTargetPlanFailure.PocketTooShort;
                return false;
            }

            plan.CurvePosition = GetCurvePositionAtDistance(bezier, fromStart, plan.TargetDistance);
            if (plan.CurvePosition < minSplit || plan.CurvePosition > maxSplit)
            {
                failure = HalfSplitTargetPlanFailure.OutsideSafeRange;
                return false;
            }

            plan.SplitDistance = GetCurveDistanceFromNode(bezier, fromStart, plan.CurvePosition);
            plan.PocketDistance = math.max(0f, plan.SplitDistance - nearIntersectionDistance);
            plan.HitPosition = MathUtils.Position(bezier, plan.CurvePosition);
            return true;
        }

        internal static Bezier4x3 ComputeMergedBezier(Entity nodeEntity, Edge edge1, Curve curve1, Edge edge2, Curve curve2)
        {
            Bezier4x3 bezier1 = edge1.m_Start == nodeEntity ? MathUtils.Invert(curve1.m_Bezier) : curve1.m_Bezier;
            Bezier4x3 bezier2 = edge2.m_End == nodeEntity ? MathUtils.Invert(curve2.m_Bezier) : curve2.m_Bezier;

            float3 startTangent = bezier1.b - bezier1.a;
            float3 endTangent = bezier2.c - bezier2.d;
            if (math.lengthsq(startTangent) <= 0.0001f)
            {
                startTangent = bezier1.d - bezier1.a;
            }

            if (math.lengthsq(endTangent) <= 0.0001f)
            {
                endTangent = bezier2.a - bezier2.d;
            }

            startTangent = math.normalizesafe(startTangent, new float3(0f, 0f, 1f));
            endTangent = math.normalizesafe(endTangent, new float3(0f, 0f, -1f));

            float lengthA = math.distance(bezier1.a, bezier1.b);
            float lengthB = math.distance(bezier2.c, bezier2.d);
            float totalDistance = math.distance(bezier1.a, bezier2.d);
            float q1Length = lengthA + totalDistance * 0.1f;
            float q2Length = lengthB + totalDistance * 0.1f;

            float3 q0 = bezier1.a;
            float3 q3 = bezier2.d;
            return new Bezier4x3(
                q0,
                q0 + startTangent * q1Length,
                q3 + endTangent * q2Length,
                q3);
        }

        internal static float GetMinimumSplitDistance(float edgeLength, float roadWidth, float minEdgeLengthRange)
        {
            if (edgeLength <= 0f)
            {
                return 0.5f;
            }

            float halfWidth = roadWidth * 0.5f;
            float minEdgeLength = math.max(halfWidth, minEdgeLengthRange) + SplitLengthBuffer;
            return math.saturate(minEdgeLength / edgeLength);
        }

        internal static void GetMinMaxSplitPositions(
            float edgeLength,
            float roadWidth,
            float minEdgeLengthRange,
            out float minSplit,
            out float maxSplit)
        {
            if (edgeLength <= 0f)
            {
                minSplit = 0.5f;
                maxSplit = 0.5f;
                return;
            }

            float baseMinimum = GetMinimumSplitDistance(edgeLength, roadWidth, minEdgeLengthRange);
            minSplit = baseMinimum;
            maxSplit = 1f - baseMinimum;

            if (minSplit >= maxSplit)
            {
                minSplit = 0.5f;
                maxSplit = 0.5f;
            }
        }
    }
}
