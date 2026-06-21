using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficCenterPatternSelector
    {
        public static bool TrySelect(
            IReadOnlyList<LaneEndpoint> sourceEndpoints,
            IReadOnlyList<CenterLaneMovementSummary> orderedSummaries,
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
            selection = CreateSelection("twoLaneShift");
            skipReason = string.Empty;

            if (smallExclusive.Count == 1 && bigStraight.Count == 1)
            {
                return TrySelectTwoLaneOrCascadePattern(
                    orderedSummaries,
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
            IReadOnlyList<CenterLaneMovementSummary> orderedSummaries,
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

            if (!TryBuildSourceRun(
                    orderedSummaries,
                    smallLane,
                    bigLane,
                    out List<CenterLaneMovementSummary> sourceRun,
                    out string sourceRunDetail))
            {
                skipReason = $"sourceRunBuildFailed detail=({sourceRunDetail})";
                return false;
            }

            if (sourceRun.Count > 2)
            {
                if (!TryValidateStraightRunMiddleLanes(sourceRun, out string middleDetail))
                {
                    skipReason = $"straightRunUnsupported detail=({middleDetail})";
                    return false;
                }

                if (!TryValidateSmallStraightLanesWithinRun(smallStraight, sourceRun, out string smallStraightDetail))
                {
                    skipReason = $"smallStraightOutsideRun detail=({smallStraightDetail})";
                    return false;
                }

                if (!TrafficCenterStraightTargetResolver.TryResolveStraightRunTargets(
                    sourceRun,
                    tryGetTargetEndpoints,
                    out List<CenterStraightRewrite> straightRewrites,
                    out string runShiftDetail))
                {
                    skipReason = $"straightTargetCascadeFailed detail=({runShiftDetail})";
                    return false;
                }

                selection.SourceRun = sourceRun;
                selection.StraightRewrites = straightRewrites;
                selection.RewriteMode = "cascadeStraightRun";
                selection.ShiftDetail = runShiftDetail;
                selection.SmallTurnsClearedFromStraightLane = CountSmallTurnsClearedFromIntermediateLanes(sourceRun);
                return true;
            }

            if (smallStraight.Count > 0)
            {
                skipReason = $"ambiguousSmallTurnStraightLane count={smallStraight.Count}";
                return false;
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

            selection.SourceRun = sourceRun;
            selection.StraightRewrites.Add(new CenterStraightRewrite(
                smallLane,
                bigCurrentStraight,
                smallLaneStraightTarget));
            selection.ShiftDetail = shiftDetail;
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

            selection.SourceRun.Add(smallLane);
            selection.SourceRun.Add(middleLane);
            selection.SourceRun.Add(bigLane);
            selection.StraightRewrites.Add(new CenterStraightRewrite(
                smallLane,
                smallCurrentStraight,
                smallLaneStraightTarget));
            selection.StraightRewrites.Add(new CenterStraightRewrite(
                middleLane,
                middleCurrentStraight,
                middleLaneStraightTarget));
            selection.RewriteMode = "repairSmallStraightConflict";
            selection.ShiftDetail = shiftDetail;
            selection.SmallTurnsClearedFromStraightLane = middleLane.SmallTurn.Count;
            return true;
        }

        private static CenterPatternSelection CreateSelection(string rewriteMode)
        {
            return new CenterPatternSelection
            {
                RewriteMode = rewriteMode,
                ShiftDetail = string.Empty,
                SourceRun = new List<CenterLaneMovementSummary>(4),
                StraightRewrites = new List<CenterStraightRewrite>(4)
            };
        }

        private static bool TryBuildSourceRun(
            IReadOnlyList<CenterLaneMovementSummary> orderedSummaries,
            CenterLaneMovementSummary smallLane,
            CenterLaneMovementSummary bigLane,
            out List<CenterLaneMovementSummary> sourceRun,
            out string detail)
        {
            sourceRun = null;
            detail = string.Empty;

            int smallOrder = FindSummaryOrder(orderedSummaries, smallLane);
            int bigOrder = FindSummaryOrder(orderedSummaries, bigLane);
            if (smallOrder < 0 || bigOrder < 0)
            {
                detail = $"source order missing small={smallLane.SourceEndpoint.LaneIndex}:{smallOrder} big={bigLane.SourceEndpoint.LaneIndex}:{bigOrder}";
                return false;
            }

            if (smallOrder == bigOrder)
            {
                detail = $"source order tie small={smallOrder} big={bigOrder}";
                return false;
            }

            int direction = bigOrder > smallOrder ? 1 : -1;
            sourceRun = new List<CenterLaneMovementSummary>(System.Math.Abs(bigOrder - smallOrder) + 1);
            for (int order = smallOrder;; order += direction)
            {
                sourceRun.Add(orderedSummaries[order]);
                if (order == bigOrder)
                {
                    break;
                }
            }

            detail = $"sourceRun={FormatSourceRun(sourceRun)} direction={direction}";
            return true;
        }

        private static bool TryValidateStraightRunMiddleLanes(
            IReadOnlyList<CenterLaneMovementSummary> sourceRun,
            out string detail)
        {
            detail = string.Empty;
            for (int i = 1; i < sourceRun.Count - 1; i++)
            {
                CenterLaneMovementSummary summary = sourceRun[i];
                if (summary.BigTurn.Count > 0 ||
                    summary.Other.Count > 0)
                {
                    detail = $"lane={summary.SourceEndpoint.LaneIndex} unsupportedMovement small={summary.SmallTurn.Count} straight={summary.Straight.Count} big={summary.BigTurn.Count} other={summary.Other.Count}";
                    return false;
                }

                if (summary.Straight.Count != 1)
                {
                    detail = $"lane={summary.SourceEndpoint.LaneIndex} straightCount={summary.Straight.Count}";
                    return false;
                }
            }

            detail = $"sourceRun={FormatSourceRun(sourceRun)}";
            return true;
        }

        private static bool TryValidateSmallStraightLanesWithinRun(
            IReadOnlyList<CenterLaneMovementSummary> smallStraight,
            IReadOnlyList<CenterLaneMovementSummary> sourceRun,
            out string detail)
        {
            detail = string.Empty;
            for (int i = 0; i < smallStraight.Count; i++)
            {
                CenterLaneMovementSummary lane = smallStraight[i];
                if (!ContainsSummary(sourceRun, lane))
                {
                    detail = $"lane={lane.SourceEndpoint.LaneIndex} sourceRun={FormatSourceRun(sourceRun)}";
                    return false;
                }
            }

            detail = $"smallStraightWithinRun count={smallStraight.Count}";
            return true;
        }

        private static int CountSmallTurnsClearedFromIntermediateLanes(IReadOnlyList<CenterLaneMovementSummary> sourceRun)
        {
            int count = 0;
            for (int i = 1; i < sourceRun.Count - 1; i++)
            {
                count += sourceRun[i].SmallTurn.Count;
            }

            return count;
        }

        private static int FindSummaryOrder(
            IReadOnlyList<CenterLaneMovementSummary> orderedSummaries,
            CenterLaneMovementSummary summary)
        {
            for (int i = 0; i < orderedSummaries.Count; i++)
            {
                if (orderedSummaries[i].SourceEndpoint.LaneIndex == summary.SourceEndpoint.LaneIndex)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool ContainsSummary(
            IReadOnlyList<CenterLaneMovementSummary> summaries,
            CenterLaneMovementSummary candidate)
        {
            return FindSummaryOrder(summaries, candidate) >= 0;
        }

        private static string FormatSourceRun(IReadOnlyList<CenterLaneMovementSummary> sourceRun)
        {
            if (sourceRun == null || sourceRun.Count == 0)
            {
                return "<none>";
            }

            List<string> lanes = new List<string>(sourceRun.Count);
            for (int i = 0; i < sourceRun.Count; i++)
            {
                CenterLaneMovementSummary summary = sourceRun[i];
                lanes.Add($"{summary.SourceEndpoint.LaneIndex}@{i}:small={summary.SmallTurn.Count},straight={summary.Straight.Count},big={summary.BigTurn.Count},other={summary.Other.Count}");
            }

            return string.Join("|", lanes);
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
