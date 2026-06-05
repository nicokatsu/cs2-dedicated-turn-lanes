using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficCenterPatternSelector
    {
        public static bool TrySelect(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            IReadOnlyList<CenterLaneMovementSummary> smallExclusive,
            IReadOnlyList<CenterLaneMovementSummary> bigStraight,
            IReadOnlyList<CenterLaneMovementSummary> bigExclusive,
            IReadOnlyList<CenterLaneMovementSummary> smallStraight,
            bool activePocketScope,
            int pocketExtraCenterLane,
            TrafficCenterStraightTargetResolver.TryGetTargetEndpoints tryGetTargetEndpoints,
            out CenterPatternSelection selection,
            out string skipReason)
        {
            selection = new CenterPatternSelection
            {
                RewriteMode = "twoLaneShift",
                ShiftDetail = string.Empty
            };
            skipReason = string.Empty;

            if (smallExclusive.Count == 1 && bigStraight.Count == 1)
            {
                return TrySelectTwoLaneOrCascadePattern(
                    sourceEndpoints,
                    smallExclusive[0],
                    bigStraight[0],
                    bigExclusive,
                    smallStraight,
                    activePocketScope,
                    pocketExtraCenterLane,
                    tryGetTargetEndpoints,
                    ref selection,
                    out skipReason);
            }

            if (activePocketScope &&
                smallExclusive.Count == 0 &&
                bigStraight.Count == 0 &&
                bigExclusive.Count == 1 &&
                smallStraight.Count == 2)
            {
                return TrySelectSmallStraightConflictPattern(
                    sourceEndpoints,
                    bigExclusive[0],
                    smallStraight,
                    pocketExtraCenterLane,
                    tryGetTargetEndpoints,
                    ref selection,
                    out skipReason);
            }

            skipReason = GetUnsupportedPatternReason(smallExclusive, bigStraight, bigExclusive, smallStraight);
            return false;
        }

        private static bool TrySelectTwoLaneOrCascadePattern(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary bigLane,
            IReadOnlyList<CenterLaneMovementSummary> bigExclusive,
            IReadOnlyList<CenterLaneMovementSummary> smallStraight,
            bool activePocketScope,
            int pocketExtraCenterLane,
            TrafficCenterStraightTargetResolver.TryGetTargetEndpoints tryGetTargetEndpoints,
            ref CenterPatternSelection selection,
            out string skipReason)
        {
            skipReason = string.Empty;
            selection.SmallLane = smallLane;
            selection.BigLane = bigLane;

            if (activePocketScope && smallLane.SourceEndpoint.LaneIndex != pocketExtraCenterLane)
            {
                skipReason = $"smallTurnNotPocketExtra expectedCenterLane={pocketExtraCenterLane} actual={smallLane.SourceEndpoint.LaneIndex}";
                return false;
            }

            if (bigLane.Straight.Count != 1)
            {
                skipReason = $"ambiguousBigStraightConnections count={bigLane.Straight.Count}";
                return false;
            }

            if (bigExclusive.Count > 0)
            {
                skipReason = $"alreadyHasBigTurnExclusive count={bigExclusive.Count}";
                return false;
            }

            if (smallStraight.Count > 1)
            {
                skipReason = $"ambiguousSmallTurnStraightLane count={smallStraight.Count}";
                return false;
            }

            if (smallStraight.Count == 1)
            {
                return TrySelectCascadePattern(
                    sourceEndpoints,
                    smallLane,
                    smallStraight[0],
                    bigLane,
                    bigLane.Straight[0],
                    tryGetTargetEndpoints,
                    ref selection,
                    out skipReason);
            }

            CenterConnectorCandidate bigCurrentStraight = bigLane.Straight[0];
            if (!TrafficCenterStraightTargetResolver.TryResolveShiftedStraightTarget(
                    smallLane.SourceEndpoint,
                    bigLane.SourceEndpoint,
                    bigCurrentStraight,
                    tryGetTargetEndpoints,
                    out LaneEndpoint smallLaneStraightTarget,
                    out string shiftDetail))
            {
                skipReason = $"straightTargetShiftFailed detail=({shiftDetail})";
                return false;
            }

            selection.SmallLaneStraightTemplate = bigCurrentStraight;
            selection.SmallLaneStraightTarget = smallLaneStraightTarget;
            selection.StraightMappingsWritten = 1;
            selection.ShiftDetail = shiftDetail;
            return true;
        }

        private static bool TrySelectCascadePattern(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary middleLane,
            CenterLaneMovementSummary bigLane,
            CenterConnectorCandidate bigCurrentStraight,
            TrafficCenterStraightTargetResolver.TryGetTargetEndpoints tryGetTargetEndpoints,
            ref CenterPatternSelection selection,
            out string skipReason)
        {
            skipReason = string.Empty;
            if (middleLane.Straight.Count != 1)
            {
                skipReason = $"ambiguousMiddleStraightConnections count={middleLane.Straight.Count}";
                return false;
            }

            CenterConnectorCandidate middleCurrentStraight = middleLane.Straight[0];
            if (!TrafficCenterStraightTargetResolver.TryResolveCascadeStraightTargets(
                    sourceEndpoints,
                    smallLane,
                    middleLane,
                    bigLane,
                    middleCurrentStraight,
                    bigCurrentStraight,
                    tryGetTargetEndpoints,
                    out LaneEndpoint smallLaneStraightTarget,
                    out LaneEndpoint middleLaneStraightTarget,
                    out string shiftDetail))
            {
                skipReason = $"straightTargetCascadeFailed detail=({shiftDetail})";
                return false;
            }

            selection.MiddleLane = middleLane;
            selection.SmallLaneStraightTemplate = middleCurrentStraight;
            selection.MiddleLaneStraightTemplate = bigCurrentStraight;
            selection.SmallLaneStraightTarget = smallLaneStraightTarget;
            selection.MiddleLaneStraightTarget = middleLaneStraightTarget;
            selection.RewriteMode = "cascadeThreeLane";
            selection.ShiftDetail = shiftDetail;
            selection.StraightMappingsWritten = 2;
            selection.SmallTurnsClearedFromStraightLane = middleLane.SmallTurn.Count;
            return true;
        }

        private static bool TrySelectSmallStraightConflictPattern(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            CenterLaneMovementSummary bigLane,
            IReadOnlyList<CenterLaneMovementSummary> smallStraight,
            int pocketExtraCenterLane,
            TrafficCenterStraightTargetResolver.TryGetTargetEndpoints tryGetTargetEndpoints,
            ref CenterPatternSelection selection,
            out string skipReason)
        {
            skipReason = string.Empty;
            if (!TrafficCenterStraightTargetResolver.TrySelectPocketExtraAndMiddleSmallStraightLane(
                    smallStraight,
                    pocketExtraCenterLane,
                    out CenterLaneMovementSummary smallLane,
                    out CenterLaneMovementSummary middleLane,
                    out string selectDetail))
            {
                skipReason = $"smallStraightConflictSelectFailed detail=({selectDetail})";
                return false;
            }

            selection.SmallLane = smallLane;
            selection.MiddleLane = middleLane;
            selection.BigLane = bigLane;
            if (smallLane.Straight.Count != 1 ||
                middleLane.Straight.Count != 1)
            {
                skipReason = $"ambiguousSmallStraightConflictStraightCounts small={smallLane.Straight.Count} middle={middleLane.Straight.Count}";
                return false;
            }

            CenterConnectorCandidate smallCurrentStraight = smallLane.Straight[0];
            CenterConnectorCandidate middleCurrentStraight = middleLane.Straight[0];
            if (!TrafficCenterStraightTargetResolver.TryResolveSmallStraightConflictTargets(
                    sourceEndpoints,
                    smallLane,
                    middleLane,
                    bigLane,
                    smallCurrentStraight,
                    middleCurrentStraight,
                    tryGetTargetEndpoints,
                    out LaneEndpoint smallLaneStraightTarget,
                    out LaneEndpoint middleLaneStraightTarget,
                    out string shiftDetail))
            {
                skipReason = $"smallStraightConflictTargetFailed detail=({shiftDetail})";
                return false;
            }

            selection.SmallLaneStraightTemplate = smallCurrentStraight;
            selection.MiddleLaneStraightTemplate = middleCurrentStraight;
            selection.SmallLaneStraightTarget = smallLaneStraightTarget;
            selection.MiddleLaneStraightTarget = middleLaneStraightTarget;
            selection.RewriteMode = "repairSmallStraightConflict";
            selection.ShiftDetail = shiftDetail;
            selection.StraightMappingsWritten = 2;
            selection.SmallTurnsClearedFromStraightLane = middleLane.SmallTurn.Count;
            return true;
        }

        private static string GetUnsupportedPatternReason(
            IReadOnlyList<CenterLaneMovementSummary> smallExclusive,
            IReadOnlyList<CenterLaneMovementSummary> bigStraight,
            IReadOnlyList<CenterLaneMovementSummary> bigExclusive,
            IReadOnlyList<CenterLaneMovementSummary> smallStraight)
        {
            if (smallExclusive.Count != 1)
            {
                return smallExclusive.Count == 0
                    ? (bigExclusive.Count > 0 && smallStraight.Count > 0 ? "alreadyBigTurnExclusiveOrPartialRewrite" : "noSmallTurnExclusive")
                    : $"ambiguousSmallTurnExclusive count={smallExclusive.Count}";
            }

            if (bigStraight.Count != 1)
            {
                return bigStraight.Count == 0
                    ? (bigExclusive.Count > 0 ? "alreadyBigTurnExclusive" : "noBigTurnStraightLane")
                    : $"ambiguousBigTurnStraightLane count={bigStraight.Count}";
            }

            return "unsupportedCenterPattern";
        }
    }
}
