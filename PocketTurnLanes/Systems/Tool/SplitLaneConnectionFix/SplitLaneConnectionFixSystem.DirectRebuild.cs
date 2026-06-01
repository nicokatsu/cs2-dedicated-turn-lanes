using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryRebuildConnectorLanes(ref Request request, out DirectRebuildStats stats)
        {
            stats = default;
            RoadDirectionPlan forwardDirection = GetRoadDirectionPlan(request, RoadDirection.Forward);
            RoadDirectionPlan reverseDirection = GetRoadDirectionPlan(request, RoadDirection.Reverse);
            bool rebuildForwardRoad = HasPreparedRoadMappings(forwardDirection);
            bool rebuildReverseRoad = HasPreparedRoadMappings(reverseDirection);

            if (!EntityManager.TryGetBuffer(request.SplitNode, false, out DynamicBuffer<SubLane> subLanes))
            {
                stats.Reason = "split node has no SubLane buffer";
                return false;
            }

            int nextNodeLaneIndex = GetNextNodeLaneIndex(request.SplitNode, subLanes);
            m_RemoveSubLaneIndexes.Clear();

            int preflightNextNodeLaneIndex = nextNodeLaneIndex;
            string forwardReason = GetRoadDirectionInitialReason(forwardDirection);
            if (rebuildForwardRoad)
            {
                if (!TryPreflightRebuildConnectorDirection(
                        request,
                        subLanes,
                        forwardDirection,
                        preflightNextNodeLaneIndex,
                        out int forwardMissingClones,
                        out forwardReason))
                {
                    stats.Reason = forwardReason;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight skipped before mutation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason}.");
                    return false;
                }

                preflightNextNodeLaneIndex += forwardMissingClones;
            }

            string reverseReason = GetRoadDirectionInitialReason(reverseDirection);
            if (rebuildReverseRoad)
            {
                if (!TryPreflightRebuildConnectorDirection(
                        request,
                        subLanes,
                        reverseDirection,
                        preflightNextNodeLaneIndex,
                        out int reverseMissingClones,
                        out reverseReason))
                {
                    stats.Reason = reverseReason;
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight skipped before mutation splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason} reverse={reverseReason}.");
                    return false;
                }

                preflightNextNodeLaneIndex += reverseMissingClones;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct road rebuild preflight ok splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} forward={forwardReason} reverse={reverseReason} startNodeLaneIndex={nextNodeLaneIndex} preflightNextNodeLaneIndex={preflightNextNodeLaneIndex}.");

            if (rebuildForwardRoad)
            {
                if (!TryRebuildConnectorDirection(
                        request,
                        subLanes,
                        forwardDirection,
                        ref nextNodeLaneIndex,
                        ref stats,
                        out forwardReason))
                {
                    stats.Reason = forwardReason;
                    return false;
                }
            }

            if (rebuildReverseRoad)
            {
                if (!TryRebuildConnectorDirection(
                        request,
                        subLanes,
                        reverseDirection,
                        ref nextNodeLaneIndex,
                        ref stats,
                        out reverseReason))
                {
                    stats.Reason = reverseReason;
                    return false;
                }
            }

            CollectStaleSplitNodeUturnConnectorLanes(request.SplitNode, request.OuterEdge, request.PocketEdge, subLanes, m_StaleConnectorLanes);
            for (int i = 0; i < m_StaleConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_StaleConnectorLanes[i];
                QueueDeleteConnector(connector.Entity);
                m_RemoveSubLaneIndexes.Add(connector.SubLaneIndex);
                stats.Deleted++;
                stats.DeletedUturn++;
            }

            string trackForwardReason = "trackForward deferred until final Traffic write";
            string trackReverseReason = "trackReverse deferred until final Traffic write";
            if (request.FinalTrackTrafficWritten || !HasTrackPreservationMappings(request))
            {
                RestoreTrackConnectorDirection(
                    request,
                    subLanes,
                    request.TrackForwardMappings,
                    request.TrackForwardSourceLanes,
                    request.TrackForwardTargetLanes,
                    request.OuterEdge,
                    request.PocketEdge,
                    "trackForward",
                    ref nextNodeLaneIndex,
                    ref stats,
                    out trackForwardReason);
                RestoreTrackConnectorDirection(
                    request,
                    subLanes,
                    request.TrackReverseMappings,
                    request.TrackReverseSourceLanes,
                    request.TrackReverseTargetLanes,
                    request.PocketEdge,
                    request.OuterEdge,
                    "trackReverse",
                    ref nextNodeLaneIndex,
                    ref stats,
                    out trackReverseReason);
            }

            m_RemoveSubLaneIndexes.Sort();
            int lastRemovedIndex = -1;
            for (int i = m_RemoveSubLaneIndexes.Count - 1; i >= 0; i--)
            {
                int index = m_RemoveSubLaneIndexes[i];
                if (index == lastRemovedIndex)
                {
                    continue;
                }

                if (index >= 0 && index < subLanes.Length)
                {
                    subLanes.RemoveAt(index);
                    lastRemovedIndex = index;
                }
            }

            stats.Reason = "ok";
            if (stats.Kept > 0 || stats.Cloned > 0 || stats.Deleted > 0 || stats.TrackKept > 0 || stats.TrackCloned > 0)
            {
                MarkUpdatedIfExists(request.SplitNode);
                MarkUpdatedIfExists(request.OuterEdge);
                MarkUpdatedIfExists(request.PocketEdge);
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Direct rebuild result splitNode={FormatEntity(request.SplitNode)} outerEdge={FormatEntity(request.OuterEdge)} pocketEdge={FormatEntity(request.PocketEdge)} mode={request.Mode} expected={FormatMappings(request.Mappings)} reverseExpected={FormatMappings(request.ReverseMappings)} trackMappings=forward[{FormatMappings(request.TrackForwardMappings)}] reverse[{FormatMappings(request.TrackReverseMappings)}] staleUturn={m_StaleConnectorLanes.Count} reverseReason={reverseReason} trackForward={trackForwardReason} trackReverse={trackReverseReason} kept={stats.Kept} cloned={stats.Cloned} deleted={stats.Deleted} deletedUturn={stats.DeletedUturn} trackKept={stats.TrackKept} trackCloned={stats.TrackCloned} trackSkipped={stats.TrackSkipped} updated={stats.Updated}.");
            return true;
        }

        private bool TryPreflightRebuildConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            RoadDirectionPlan direction,
            int nextNodeLaneIndex,
            out int missingCloneCount,
            out string reason)
        {
            return TryPreflightRebuildConnectorDirection(
                request,
                subLanes,
                direction.Mappings,
                direction.SourceLanes,
                direction.TargetLanes,
                direction.SourceEdge,
                direction.TargetEdge,
                direction.Label,
                nextNodeLaneIndex,
                out missingCloneCount,
                out reason);
        }

        private bool TryPreflightRebuildConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            LaneMapping[] mappings,
            LaneEndpoint[] sourceLanes,
            LaneEndpoint[] targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            int nextNodeLaneIndex,
            out int missingCloneCount,
            out string reason)
        {
            missingCloneCount = 0;
            reason = string.Empty;
            if (mappings == null || mappings.Length == 0)
            {
                reason = $"{direction} missing expected mappings";
                return false;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_ConnectorLanes);
            if (m_ConnectorLanes.Count == 0)
            {
                reason = $"{direction} no existing connector lanes to use as templates sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)}";
                return false;
            }

            HashSet<ConnectionKey> existingKeys = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                existingKeys.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (existingKeys.Contains(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out _) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out _))
                {
                    reason = $"{direction} missing endpoint source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (!TryFindConnectorTemplate(mapping, out _))
                {
                    reason = $"{direction} missing clone template source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                missingCloneCount++;
            }

            if (nextNodeLaneIndex + missingCloneCount > ushort.MaxValue)
            {
                reason = $"{direction} node lane index exhausted next={nextNodeLaneIndex} missing={missingCloneCount}";
                return false;
            }

            reason = $"{direction} preflight-ok expected={FormatConnectionSet(expected)} existing={existingKeys.Count} missingClones={missingCloneCount}";
            return true;
        }

        private bool TryRebuildConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            RoadDirectionPlan direction,
            ref int nextNodeLaneIndex,
            ref DirectRebuildStats stats,
            out string reason)
        {
            return TryRebuildConnectorDirection(
                request,
                subLanes,
                direction.Mappings,
                direction.SourceLanes,
                direction.TargetLanes,
                direction.SourceEdge,
                direction.TargetEdge,
                direction.Label,
                ref nextNodeLaneIndex,
                ref stats,
                out reason);
        }

        private bool TryRebuildConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            LaneMapping[] mappings,
            LaneEndpoint[] sourceLanes,
            LaneEndpoint[] targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            ref int nextNodeLaneIndex,
            ref DirectRebuildStats stats,
            out string reason)
        {
            reason = string.Empty;
            if (mappings == null || mappings.Length == 0)
            {
                reason = $"{direction} missing expected mappings";
                return false;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            for (int i = 0; i < mappings.Length; i++)
            {
                expected.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            CollectConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_ConnectorLanes);
            if (m_ConnectorLanes.Count == 0)
            {
                reason = $"{direction} no existing connector lanes to use as templates sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)}";
                return false;
            }

            HashSet<ConnectionKey> existingKeys = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                existingKeys.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
            }

            int missingCloneCount = 0;
            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (existingKeys.Contains(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out _) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out _))
                {
                    reason = $"{direction} missing endpoint source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (!TryFindConnectorTemplate(mapping, out _))
                {
                    reason = $"{direction} missing clone template source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                missingCloneCount++;
            }

            if (nextNodeLaneIndex + missingCloneCount > ushort.MaxValue)
            {
                reason = $"{direction} node lane index exhausted next={nextNodeLaneIndex} missing={missingCloneCount}";
                return false;
            }

            Dictionary<ConnectionKey, ConnectorLane> kept = new Dictionary<ConnectionKey, ConnectorLane>();
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                ConnectionKey key = new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex);
                if (expected.Contains(key) && !kept.ContainsKey(key))
                {
                    kept.Add(key, connector);
                    ClearUnsafeFlags(connector.Entity);
                    MarkUpdatedIfExists(connector.Entity);
                    stats.Kept++;
                    stats.Updated++;
                    continue;
                }

                QueueDeleteConnector(connector.Entity);
                m_RemoveSubLaneIndexes.Add(connector.SubLaneIndex);
                stats.Deleted++;
            }

            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (kept.ContainsKey(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out LaneEndpoint source) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out LaneEndpoint target))
                {
                    reason = $"{direction} missing endpoint source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (!TryFindConnectorTemplate(mapping, out ConnectorLane template))
                {
                    reason = $"{direction} missing clone template source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex}";
                    return false;
                }

                if (nextNodeLaneIndex > ushort.MaxValue)
                {
                    reason = $"{direction} node lane index exhausted next={nextNodeLaneIndex}";
                    return false;
                }

                Entity clone = CloneConnectorLane(request, template, source, target, (ushort)nextNodeLaneIndex++);
                PathMethod roadOnlyPathMethods = GetRoadFixMethod(template.PathMethods | mapping.Method);
                subLanes.Add(new SubLane
                {
                    m_SubLane = clone,
                    m_PathMethods = roadOnlyPathMethods
                });
                kept.Add(key, new ConnectorLane
                {
                    Entity = clone,
                    SubLaneIndex = subLanes.Length - 1,
                    PathMethods = roadOnlyPathMethods,
                    SourceLaneIndex = mapping.SourceLaneIndex,
                    TargetLaneIndex = mapping.TargetLaneIndex
                });
                stats.Cloned++;
                stats.Updated++;
            }

            reason = $"{direction} ok expected={FormatConnectionSet(expected)} existing={existingKeys.Count} kept={kept.Count} missingClones={missingCloneCount}";
            return true;
        }

        private void RestoreTrackConnectorDirection(
            Request request,
            DynamicBuffer<SubLane> subLanes,
            LaneMapping[] mappings,
            LaneEndpoint[] sourceLanes,
            LaneEndpoint[] targetLanes,
            Entity sourceEdge,
            Entity targetEdge,
            string direction,
            ref int nextNodeLaneIndex,
            ref DirectRebuildStats stats,
            out string reason)
        {
            if (mappings == null || mappings.Length == 0)
            {
                reason = $"{direction} no expected track mappings";
                return;
            }

            HashSet<ConnectionKey> expected = new HashSet<ConnectionKey>();
            Dictionary<ConnectionKey, LaneMapping> expectedMappings = new Dictionary<ConnectionKey, LaneMapping>();
            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                expected.Add(key);
                if (!expectedMappings.ContainsKey(key))
                {
                    expectedMappings.Add(key, mapping);
                }
            }

            CollectTrackConnectorLanes(request.SplitNode, sourceEdge, targetEdge, subLanes, m_TrackConnectorLanes);
            HashSet<ConnectionKey> actual = new HashSet<ConnectionKey>();
            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                ConnectionKey key = new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex);
                if (!expected.Contains(key))
                {
                    continue;
                }

                if ((connector.PathMethods & PathMethod.Track) != 0)
                {
                    actual.Add(key);
                    ClearUnsafeFlags(connector.Entity);
                    MarkUpdatedIfExists(connector.Entity);
                    stats.TrackKept++;
                    stats.Updated++;
                    continue;
                }

                if (!expectedMappings.TryGetValue(key, out LaneMapping mapping) ||
                    (mapping.Method & PathMethod.Track) == 0 ||
                    connector.SubLaneIndex < 0 ||
                    connector.SubLaneIndex >= subLanes.Length)
                {
                    continue;
                }

                SubLane subLane = subLanes[connector.SubLaneIndex];
                PathMethod restoredMethods = SanitizeTrafficPathMethod(subLane.m_PathMethods | mapping.Method);
                if ((restoredMethods & PathMethod.Track) == 0)
                {
                    continue;
                }

                subLane.m_PathMethods = restoredMethods;
                subLanes[connector.SubLaneIndex] = subLane;
                actual.Add(key);
                MarkUpdatedIfExists(connector.Entity);
                stats.TrackKept++;
                stats.Updated++;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Restored shared track method on existing connector direction={direction} connector={FormatEntity(connector.Entity)} splitNode={FormatEntity(request.SplitNode)} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} source={mapping.SourceLaneIndex} target={mapping.TargetLaneIndex} oldMethods=[{connector.PathMethods}] newMethods=[{restoredMethods}] mappingMethod=[{mapping.Method}].");
            }

            int cloned = 0;
            int skipped = 0;
            List<string> skipReasons = new List<string>(4);
            for (int i = 0; i < mappings.Length; i++)
            {
                LaneMapping mapping = mappings[i];
                ConnectionKey key = new ConnectionKey(mapping.SourceLaneIndex, mapping.TargetLaneIndex);
                if (actual.Contains(key))
                {
                    continue;
                }

                if (!TryFindLaneEndpoint(sourceLanes, mapping.SourceLaneIndex, out LaneEndpoint source) ||
                    !TryFindLaneEndpoint(targetLanes, mapping.TargetLaneIndex, out LaneEndpoint target))
                {
                    skipped++;
                    skipReasons.Add($"endpointMissing {mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}");
                    continue;
                }

                if (!TryFindTrackConnectorTemplate(mapping, out ConnectorLane template) &&
                    !TryFindSnapshotTrackConnectorTemplate(mapping, out template))
                {
                    skipped++;
                    skipReasons.Add($"templateMissing {mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}");
                    continue;
                }

                if (nextNodeLaneIndex > ushort.MaxValue)
                {
                    skipped++;
                    skipReasons.Add($"nodeLaneIndexExhausted next={nextNodeLaneIndex}");
                    continue;
                }

                PathMethod restoredPathMethods = GetTrackFixMethod(mapping.Method);
                if ((restoredPathMethods & PathMethod.Track) == 0)
                {
                    skipped++;
                    skipReasons.Add($"methodMissingTrack {mapping.SourceLaneIndex}->{mapping.TargetLaneIndex}");
                    continue;
                }

                Entity clone = CloneConnectorLane(request, template, source, target, (ushort)nextNodeLaneIndex++);
                subLanes.Add(new SubLane
                {
                    m_SubLane = clone,
                    m_PathMethods = restoredPathMethods
                });
                actual.Add(key);
                cloned++;
                stats.TrackCloned++;
                stats.Updated++;
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Restored track connector lane direction={direction} clone={FormatEntity(clone)} template={FormatEntity(template.Entity)} splitNode={FormatEntity(request.SplitNode)} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} source={mapping.SourceLaneIndex}/{FormatEntity(source.LaneEntity)} target={mapping.TargetLaneIndex}/{FormatEntity(target.LaneEntity)} method=[{restoredPathMethods}] snapshotMethod=[{mapping.Method}] templateMethods=[{template.PathMethods}].");
            }

            stats.TrackSkipped += skipped;
            reason = $"{direction} expected={FormatConnectionSet(expected)} actualBefore={FormatConnectionSet(actual)} templates={m_TrackConnectorLanes.Count} cloned={cloned} skipped={skipped} trackSkippedReason={FormatStringList(skipReasons)}";
            if (skipped > 0)
            {
                Mod.LogDiagnostic($"[SplitLaneConnectionFix] Track connector restore skipped splitNode={FormatEntity(request.SplitNode)} direction={direction} sourceEdge={FormatEntity(sourceEdge)} targetEdge={FormatEntity(targetEdge)} expected={FormatMappings(mappings)} actual={FormatConnectionSet(actual)} trackSkippedReason={FormatStringList(skipReasons)} trackForwardSource=({FormatLaneOrder(request.TrackForwardSourceLanes)}) trackForwardTarget=({FormatLaneOrder(request.TrackForwardTargetLanes)}) trackReverseSource=({FormatLaneOrder(request.TrackReverseSourceLanes)}) trackReverseTarget=({FormatLaneOrder(request.TrackReverseTargetLanes)}).");
            }
        }

        private bool TryFindSnapshotTrackConnectorTemplate(LaneMapping mapping, out ConnectorLane template)
        {
            if (mapping.TemplateEntity != Entity.Null &&
                EntityManager.Exists(mapping.TemplateEntity) &&
                (mapping.TemplatePathMethods & PathMethod.Track) != 0)
            {
                template = new ConnectorLane
                {
                    Entity = mapping.TemplateEntity,
                    PathMethods = mapping.TemplatePathMethods,
                    SourceEdge = mapping.SourceEdge,
                    TargetEdge = mapping.TargetEdge,
                    SourceLaneIndex = mapping.SourceLaneIndex,
                    TargetLaneIndex = mapping.TargetLaneIndex
                };
                return true;
            }

            template = default;
            return false;
        }

        private bool TryFindTrackConnectorTemplate(LaneMapping mapping, out ConnectorLane template)
        {
            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                if ((connector.PathMethods & PathMethod.Track) != 0 &&
                    connector.SourceLaneIndex == mapping.SourceLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                if ((connector.PathMethods & PathMethod.Track) != 0 &&
                    connector.TargetLaneIndex == mapping.TargetLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            for (int i = 0; i < m_TrackConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_TrackConnectorLanes[i];
                if ((connector.PathMethods & PathMethod.Track) != 0)
                {
                    template = connector;
                    return true;
                }
            }

            template = default;
            return false;
        }

        private Entity CloneConnectorLane(Request request, ConnectorLane template, LaneEndpoint source, LaneEndpoint target, ushort middleLaneIndex)
        {
            Entity clone = EntityManager.Instantiate(template.Entity);
            if (EntityManager.HasComponent<Deleted>(clone))
            {
                EntityManager.RemoveComponent<Deleted>(clone);
            }

            Lane lane = EntityManager.GetComponentData<Lane>(clone);
            lane.m_StartNode = source.PathNode;
            lane.m_MiddleNode = new PathNode(request.SplitNode, middleLaneIndex);
            lane.m_EndNode = target.PathNode;
            EntityManager.SetComponentData(clone, lane);

            if (EntityManager.TryGetComponent(clone, out Curve curve))
            {
                curve.m_Bezier = BuildConnectorCurve(source, target);
                curve.m_Length = MathUtils.Length(curve.m_Bezier);
                EntityManager.SetComponentData(clone, curve);
            }

            ClearUnsafeFlags(clone);
            if (EntityManager.HasComponent<Owner>(clone))
            {
                EntityManager.SetComponentData(clone, new Owner { m_Owner = request.SplitNode });
            }
            else
            {
                EntityManager.AddComponentData(clone, new Owner { m_Owner = request.SplitNode });
            }

            AddMarkerIfMissing<Created>(clone);
            AddMarkerIfMissing<Updated>(clone);
            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cloned connector lane clone={FormatEntity(clone)} template={FormatEntity(template.Entity)} splitNode={FormatEntity(request.SplitNode)} source={source.LaneIndex}/{FormatEntity(source.LaneEntity)} target={target.LaneIndex}/{FormatEntity(target.LaneEntity)} middleIndex={middleLaneIndex}.");
            return clone;
        }

        private static Bezier4x3 BuildConnectorCurve(LaneEndpoint source, LaneEndpoint target)
        {
            float distance = math.max(1f, math.distance(source.Position.xz, target.Position.xz));
            float tangentLength = math.min(12f, distance * 0.5f);
            float3 sourceTangent = new float3(source.TravelDirection.x, 0f, source.TravelDirection.y) * tangentLength;
            float3 targetTangent = new float3(target.TravelDirection.x, 0f, target.TravelDirection.y) * tangentLength;
            return NetUtils.FitCurve(source.Position, sourceTangent, -targetTangent, target.Position);
        }

        private bool TryFindConnectorTemplate(LaneMapping mapping, out ConnectorLane template)
        {
            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                if (connector.SourceLaneIndex == mapping.SourceLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            for (int i = 0; i < m_ConnectorLanes.Count; i++)
            {
                ConnectorLane connector = m_ConnectorLanes[i];
                if (connector.TargetLaneIndex == mapping.TargetLaneIndex)
                {
                    template = connector;
                    return true;
                }
            }

            template = m_ConnectorLanes[0];
            return template.Entity != Entity.Null;
        }

        private static bool TryFindLaneEndpoint(IReadOnlyList<LaneEndpoint> lanes, int laneIndex, out LaneEndpoint lane)
        {
            if (lanes != null)
            {
                for (int i = 0; i < lanes.Count; i++)
                {
                    if (lanes[i].LaneIndex == laneIndex)
                    {
                        lane = lanes[i];
                        return true;
                    }
                }
            }

            lane = default;
            return false;
        }

        private static bool TryFindMappingEndpoint(Request request, Entity edge, int laneIndex, bool source, out LaneEndpoint lane)
        {
            if (source)
            {
                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.SourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.TrackForwardSourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.PreservationForwardSourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.ReverseSourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.TrackReverseSourceLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.PreservationReverseSourceLanes, laneIndex, out lane))
                {
                    return true;
                }
            }
            else
            {
                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.TargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.TrackForwardTargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.PocketEdge && TryFindLaneEndpoint(request.PreservationForwardTargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.ReverseTargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.TrackReverseTargetLanes, laneIndex, out lane))
                {
                    return true;
                }

                if (edge == request.OuterEdge && TryFindLaneEndpoint(request.PreservationReverseTargetLanes, laneIndex, out lane))
                {
                    return true;
                }
            }

            lane = default;
            return false;
        }

        private int GetNextNodeLaneIndex(Entity node, DynamicBuffer<SubLane> subLanes)
        {
            int maxIndex = -1;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity laneEntity = subLanes[i].m_SubLane;
                if (laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane))
                {
                    continue;
                }

                IncludeNodePathIndex(node, lane.m_StartNode, ref maxIndex);
                IncludeNodePathIndex(node, lane.m_MiddleNode, ref maxIndex);
                IncludeNodePathIndex(node, lane.m_EndNode, ref maxIndex);
            }

            return maxIndex + 1;
        }

        private static void IncludeNodePathIndex(Entity node, PathNode pathNode, ref int maxIndex)
        {
            if (pathNode.GetOwnerIndex() == node.Index)
            {
                maxIndex = math.max(maxIndex, pathNode.GetLaneIndex());
            }
        }

        private void QueueDeleteConnector(Entity laneEntity)
        {
            if (laneEntity == Entity.Null || !EntityManager.Exists(laneEntity))
            {
                return;
            }

            AddMarkerIfMissing<Deleted>(laneEntity);
            AddMarkerIfMissing<Updated>(laneEntity);
        }

        private void ClearUnsafeFlags(Entity laneEntity)
        {
            if (EntityManager.TryGetComponent(laneEntity, out NetCarLane carLane))
            {
                CarLaneFlags oldFlags = carLane.m_Flags;
                carLane.m_Flags &= ~(CarLaneFlags.Unsafe | CarLaneFlags.Forbidden);
                if (carLane.m_Flags != oldFlags)
                {
                    EntityManager.SetComponentData(laneEntity, carLane);
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Cleared unsafe flags connector={FormatEntity(laneEntity)} oldFlags={oldFlags} newFlags={carLane.m_Flags}.");
                }
            }
        }

        private void AddMarkerIfMissing<T>(Entity entity) where T : unmanaged, IComponentData
        {
            if (!EntityManager.HasComponent<T>(entity))
            {
                EntityManager.AddComponent<T>(entity);
            }
        }
    }
}
