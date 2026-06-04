using System;
using PocketTurnLanes.Tool.Traffic;
using Unity.Entities;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private const int MaxTrafficMigrationSentinelWaitFrames = 8;

        private bool TryPassTrafficMigrationBarrier(TrafficApi trafficApi, ref Request request)
        {
            if (request.TrafficMigrationBarrierState == TrafficMigrationBarrierState.Passed)
            {
                return true;
            }

            Entity sentinelNode = GetTrafficMigrationSentinelNode(request);
            if (sentinelNode == Entity.Null || !EntityManager.Exists(sentinelNode))
            {
                request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.Passed;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier skipped because sentinel node is unavailable splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)}.");
                return true;
            }

            int frame = UnityEngine.Time.frameCount;
            if (request.TrafficMigrationBarrierState == TrafficMigrationBarrierState.SentinelQueued)
            {
                return ObserveTrafficMigrationSentinel(trafficApi, ref request, sentinelNode, frame);
            }

            if (trafficApi.TryGetModifiedLaneConnectionsLength(EntityManager, sentinelNode, out int existingLength))
            {
                if (existingLength > 0)
                {
                    request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.Passed;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier passed with existing non-empty Traffic data splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} existingConnections={existingLength} frame={frame}. Existing data is not touched.");
                    return true;
                }

                request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.SentinelQueued;
                request.TrafficMigrationSentinelNode = sentinelNode;
                request.TrafficMigrationSentinelFrame = frame;
                request.EarliestTrafficWriteFrame = Math.Max(request.EarliestTrafficWriteFrame, frame + 1);
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier adopted existing empty sentinel splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} sentinelFrame={frame} earliestWriteFrame={request.EarliestTrafficWriteFrame}.");
                return false;
            }

            int previousSentinelLength = trafficApi.EnsureEmptyModifiedLaneConnectionsSentinel(EntityManager, sentinelNode);

            request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.SentinelQueued;
            request.TrafficMigrationSentinelNode = sentinelNode;
            request.TrafficMigrationSentinelFrame = frame;
            request.EarliestTrafficWriteFrame = Math.Max(request.EarliestTrafficWriteFrame, frame + 1);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier queued empty Traffic sentinel splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} sentinelFrame={frame} earliestWriteFrame={request.EarliestTrafficWriteFrame} previousSentinelLength={previousSentinelLength} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}. Waiting for TrafficDataMigrationSystem to consume the empty buffer before real Traffic write.");
            return false;
        }

        private bool ObserveTrafficMigrationSentinel(
            TrafficApi trafficApi,
            ref Request request,
            Entity fallbackSentinelNode,
            int frame)
        {
            Entity sentinelNode = request.TrafficMigrationSentinelNode != Entity.Null
                ? request.TrafficMigrationSentinelNode
                : fallbackSentinelNode;
            if (sentinelNode == Entity.Null || !EntityManager.Exists(sentinelNode))
            {
                request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.Passed;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier passed because sentinel node no longer exists splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} frame={frame} sentinelFrame={request.TrafficMigrationSentinelFrame}.");
                return true;
            }

            if (!trafficApi.TryGetModifiedLaneConnectionsLength(EntityManager, sentinelNode, out int length))
            {
                request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.Passed;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier passed after Traffic removed empty sentinel splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} frame={frame} sentinelFrame={request.TrafficMigrationSentinelFrame} waitedFrames={frame - request.TrafficMigrationSentinelFrame}.");
                return true;
            }

            if (length > 0)
            {
                request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.Passed;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier passed because sentinel node now has non-empty Traffic data splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} frame={frame} sentinelFrame={request.TrafficMigrationSentinelFrame} connections={length}. Treating earlier empty sentinel as consumed or superseded.");
                return true;
            }

            int waitedFrames = frame - request.TrafficMigrationSentinelFrame;
            if (waitedFrames >= MaxTrafficMigrationSentinelWaitFrames)
            {
                bool removed = trafficApi.RemoveEmptyModifiedLaneConnectionsSentinel(EntityManager, sentinelNode);
                request.TrafficMigrationBarrierState = TrafficMigrationBarrierState.Passed;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Traffic migration barrier passed after local empty-sentinel cleanup splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} frame={frame} sentinelFrame={request.TrafficMigrationSentinelFrame} waitedFrames={waitedFrames} maxWaitFrames={MaxTrafficMigrationSentinelWaitFrames} removed={removed}. TrafficDataMigrationSystem did not consume the empty buffer, so it is assumed to be inactive for this session.");
                return true;
            }

            if (waitedFrames <= 1 || waitedFrames % 4 == 0)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Waiting for Traffic migration sentinel consumption splitNode={FormatEntity(request.SplitNode)} centerNode={FormatEntity(request.IntersectionNode)} sentinelNode={FormatEntity(sentinelNode)} frame={frame} sentinelFrame={request.TrafficMigrationSentinelFrame} waitedFrames={waitedFrames}/{MaxTrafficMigrationSentinelWaitFrames}.");
            }

            return false;
        }

        private static Entity GetTrafficMigrationSentinelNode(Request request)
        {
            return request.IntersectionNode != Entity.Null
                ? request.IntersectionNode
                : request.SplitNode;
        }
    }
}
