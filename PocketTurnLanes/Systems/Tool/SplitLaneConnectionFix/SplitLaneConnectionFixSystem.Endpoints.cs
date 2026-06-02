using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using PocketTurnLanes.Tool;
using Unity.Entities;
using Unity.Mathematics;
using NetCarLane = Game.Net.CarLane;
using NetEdge = Game.Net.Edge;
using NetTrackLane = Game.Net.TrackLane;
using SubLane = Game.Net.SubLane;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryFindOuterEdge(Request request, out Entity outerEdge)
        {
            outerEdge = Entity.Null;
            if (request.OuterEdge != Entity.Null &&
                EntityManager.Exists(request.OuterEdge) &&
                !EntityManager.HasComponent<Deleted>(request.OuterEdge) &&
                EntityManager.TryGetComponent(request.OuterEdge, out NetEdge explicitEdge) &&
                (explicitEdge.m_Start == request.SplitNode || explicitEdge.m_End == request.SplitNode))
            {
                outerEdge = request.OuterEdge;
                return true;
            }

            if (!EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            float bestScore = float.MinValue;
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (edgeEntity == request.PocketEdge ||
                    edgeEntity == Entity.Null ||
                    !EntityManager.Exists(edgeEntity) ||
                    EntityManager.HasComponent<Deleted>(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) ||
                    !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                {
                    continue;
                }

                bool connectsSplit = edge.m_Start == request.SplitNode || edge.m_End == request.SplitNode;
                if (!connectsSplit)
                {
                    continue;
                }

                float score = 0f;
                if (edgeEntity == request.OriginalEdge)
                {
                    score += 1000f;
                }

                if (prefabRef.m_Prefab == request.SourcePrefab)
                {
                    score += 100f;
                }

                Entity otherNode = edge.m_Start == request.SplitNode ? edge.m_End : edge.m_Start;
                if (otherNode != request.IntersectionNode)
                {
                    score += 10f;
                }

                if (EntityManager.TryGetComponent(edgeEntity, out Curve curve))
                {
                    score += math.min(curve.m_Length, 100f) * 0.01f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    outerEdge = edgeEntity;
                }
            }

            return outerEdge != Entity.Null;
        }

        private void CollectEdgeCarLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output)
        {
            CollectEdgeLaneEndpoints(edgeEntity, splitNode, role, output, includeTrackOnly: false);
        }

        private void CollectEdgeTrafficLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output)
        {
            CollectEdgeLaneEndpoints(edgeEntity, splitNode, role, output, includeTrackOnly: true);
        }

        private void CollectEdgePreservationLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output)
        {
            CollectEdgeLaneEndpoints(edgeEntity, splitNode, role, output, includeTrackOnly: true, includeNonRoadPathMethods: true);
        }

        private bool TryFindCenterTargetEndpoint(
            Entity centerNode,
            Entity targetEdge,
            int laneIndex,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out LaneEndpoint targetEndpoint)
        {
            targetEndpoint = default;
            if (!TryGetCenterTargetEndpoints(centerNode, targetEdge, targetEndpointCache, out List<LaneEndpoint> targetEndpoints))
            {
                return false;
            }

            return TryFindLaneEndpoint(targetEndpoints, laneIndex, out targetEndpoint);
        }

        private bool TryFindCenterPreservationTargetEndpoint(
            Entity centerNode,
            Entity targetEdge,
            int laneIndex,
            Dictionary<Entity, List<LaneEndpoint>> roadTargetEndpointCache,
            Dictionary<Entity, List<LaneEndpoint>> preservationTargetEndpointCache,
            out LaneEndpoint targetEndpoint)
        {
            if (TryFindCenterTargetEndpoint(
                    centerNode,
                    targetEdge,
                    laneIndex,
                    roadTargetEndpointCache,
                    out targetEndpoint))
            {
                return true;
            }

            if (!preservationTargetEndpointCache.TryGetValue(targetEdge, out List<LaneEndpoint> targetEndpoints))
            {
                targetEndpoints = new List<LaneEndpoint>(8);
                CollectEdgePreservationLaneEndpoints(
                    targetEdge,
                    centerNode,
                    EndpointRole.TargetStartAtNode,
                    targetEndpoints);
                SortLaneEndpointsByLateral(targetEndpoints);
                preservationTargetEndpointCache.Add(targetEdge, targetEndpoints);
            }

            return TryFindLaneEndpoint(targetEndpoints, laneIndex, out targetEndpoint);
        }

        private bool TryGetCenterTargetEndpoints(
            Entity centerNode,
            Entity targetEdge,
            Dictionary<Entity, List<LaneEndpoint>> targetEndpointCache,
            out List<LaneEndpoint> targetEndpoints)
        {
            if (!targetEndpointCache.TryGetValue(targetEdge, out targetEndpoints))
            {
                targetEndpoints = new List<LaneEndpoint>(8);
                CollectEdgeCarLaneEndpoints(targetEdge, centerNode, EndpointRole.TargetStartAtNode, targetEndpoints);
                SortLaneEndpointsByLateral(targetEndpoints);
                targetEndpointCache.Add(targetEdge, targetEndpoints);
            }

            return targetEndpoints.Count > 0;
        }

        private static int FindLaneEndpointOrder(IReadOnlyList<LaneEndpoint> lanes, int laneIndex)
        {
            if (lanes == null)
            {
                return -1;
            }

            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i].LaneIndex == laneIndex)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void SortLaneEndpointsByLateral(List<LaneEndpoint> lanes)
        {
            lanes.Sort((a, b) => a.Lateral.CompareTo(b.Lateral));
        }

        private bool IsEdgeConnectedToNode(Entity edgeEntity, Entity node)
        {
            return edgeEntity != Entity.Null &&
                   node != Entity.Null &&
                   EntityManager.Exists(edgeEntity) &&
                   !EntityManager.HasComponent<Deleted>(edgeEntity) &&
                   EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) &&
                   (edge.m_Start == node || edge.m_End == node);
        }

        private void CollectEdgeLaneEndpoints(
            Entity edgeEntity,
            Entity splitNode,
            EndpointRole role,
            List<LaneEndpoint> output,
            bool includeTrackOnly,
            bool trackCandidateMode = false,
            bool includeNonRoadPathMethods = false)
        {
            output.Clear();

            if (!EntityManager.TryGetComponent(edgeEntity, out NetEdge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Composition composition) ||
                !EntityManager.TryGetComponent(edgeEntity, out EdgeGeometry edgeGeometry) ||
                !EntityManager.TryGetComponent(composition.m_Edge, out NetCompositionData compositionData) ||
                !EntityManager.TryGetBuffer(composition.m_Edge, true, out DynamicBuffer<NetCompositionLane> compositionLanes) ||
                !EntityManager.TryGetBuffer(edgeEntity, true, out DynamicBuffer<SubLane> subLanes))
            {
                return;
            }

            bool splitIsStart = edge.m_Start == splitNode;
            bool splitIsEnd = edge.m_End == splitNode;
            if (!splitIsStart && !splitIsEnd)
            {
                return;
            }

            bool isEnd = splitIsEnd;
            float endpointDelta = isEnd ? 1f : 0f;
            if (isEnd)
            {
                edgeGeometry.m_Start.m_Left = MathUtils.Invert(edgeGeometry.m_End.m_Right);
                edgeGeometry.m_Start.m_Right = MathUtils.Invert(edgeGeometry.m_End.m_Left);
            }

            bool[] visitedCompositionLanes = new bool[compositionLanes.Length];
            for (int i = 0; i < subLanes.Length; i++)
            {
                SubLane subLane = subLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if (laneEntity == Entity.Null ||
                    (includeNonRoadPathMethods
                        ? subLane.m_PathMethods == 0
                        : (subLane.m_PathMethods & (PathMethod.Road | PathMethod.Track)) == 0) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !EntityManager.TryGetComponent(laneEntity, out EdgeLane edgeLane) ||
                    !EntityManager.TryGetComponent(laneEntity, out Curve curve) ||
                    !EntityManager.TryGetComponent(laneEntity, out PrefabRef lanePrefab) ||
                    !EntityManager.TryGetComponent(lanePrefab.m_Prefab, out NetLaneData laneData))
                {
                    continue;
                }

                bool hasCarLaneData = EntityManager.TryGetComponent(lanePrefab.m_Prefab, out CarLaneData carLaneData);
                bool hasTrackLaneData = EntityManager.TryGetComponent(lanePrefab.m_Prefab, out TrackLaneData trackLaneData);
                bool hasNetTrackLane = EntityManager.HasComponent<NetTrackLane>(laneEntity);
                bool hasSecondaryLane = EntityManager.HasComponent<Game.Net.SecondaryLane>(laneEntity);
                bool laneFlagsTrack = (laneData.m_Flags & LaneFlags.Track) != 0;
                bool hasTrackPathMethod = (subLane.m_PathMethods & PathMethod.Track) != 0;
                bool hasTrackEvidence = hasTrackPathMethod || hasTrackLaneData || hasNetTrackLane;
                bool isCarRoadLane = (subLane.m_PathMethods & PathMethod.Road) != 0 &&
                                     (laneData.m_Flags & LaneFlags.Road) != 0 &&
                                     hasCarLaneData &&
                                     (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
                bool isTrackLane = includeTrackOnly &&
                                   laneFlagsTrack &&
                                   hasTrackEvidence;
                bool isNonRoadTrafficLane = includeNonRoadPathMethods &&
                                            (subLane.m_PathMethods & ~(PathMethod.Road | PathMethod.Track)) != 0;
                bool includeLane = trackCandidateMode ? isTrackLane : isCarRoadLane || isTrackLane || isNonRoadTrafficLane;
                if (!includeLane ||
                    (!includeNonRoadPathMethods && hasSecondaryLane && !isTrackLane) ||
                    (!includeNonRoadPathMethods &&
                     (laneData.m_Flags & (LaneFlags.Utility | LaneFlags.Pedestrian | LaneFlags.Parking | LaneFlags.ParkingLeft | LaneFlags.ParkingRight)) != 0))
                {
                    continue;
                }

                bool startAtSplit = math.abs(edgeLane.m_EdgeDelta.x - endpointDelta) <= 0.001f;
                bool endAtSplit = math.abs(edgeLane.m_EdgeDelta.y - endpointDelta) <= 0.001f;
                if (!startAtSplit && !endAtSplit)
                {
                    continue;
                }

                bool isSourceEndpoint = endAtSplit;
                if ((role == EndpointRole.SourceEndAtNode && !isSourceEndpoint) ||
                    (role == EndpointRole.TargetStartAtNode && isSourceEndpoint))
                {
                    continue;
                }

                if (isSourceEndpoint)
                {
                    curve.m_Bezier = MathUtils.Invert(curve.m_Bezier);
                }

                if (!TryFindTrafficCompositionLane(
                        laneEntity,
                        curve,
                        laneData,
                        compositionData,
                        compositionLanes,
                        edgeGeometry,
                        isEnd,
                        isSourceEndpoint,
                        visitedCompositionLanes,
                        out NetCompositionLane compositionLane,
                        out float order))
                {
                    Mod.LogDiagnostic($"[SplitLaneConnectionFix] Skipped lane endpoint without Traffic composition match edge={FormatEntity(edgeEntity)} splitNode={FormatEntity(splitNode)} lane={FormatEntity(laneEntity)} role={role} edgeDelta={edgeLane.m_EdgeDelta} laneFlags={laneData.m_Flags} methods={subLane.m_PathMethods} includeTrackOnly={includeTrackOnly} trackCandidateMode={trackCandidateMode} hasCarData={hasCarLaneData} hasTrackData={hasTrackLaneData} netTrack={hasNetTrackLane} secondary={hasSecondaryLane} trackTypes={trackLaneData.m_TrackTypes}.");
                    continue;
                }

                float3 tangent = MathUtils.StartTangent(curve.m_Bezier);
                float2 travelDirection = -tangent.xz;
                if (math.lengthsq(travelDirection) <= 0.0001f)
                {
                    continue;
                }

                travelDirection = math.normalize(travelDirection);
                PathNode pathNode = isSourceEndpoint ? lane.m_EndNode : lane.m_StartNode;
                PathNode oppositePathNode = isSourceEndpoint ? lane.m_StartNode : lane.m_EndNode;
                NetCarLane carLane = EntityManager.TryGetComponent(laneEntity, out NetCarLane laneComponent)
                    ? laneComponent
                    : default;
                output.Add(new LaneEndpoint
                {
                    LaneEntity = laneEntity,
                    Edge = edgeEntity,
                    LaneIndex = pathNode.GetLaneIndex() & 0xff,
                    OppositeLaneIndex = oppositePathNode.GetLaneIndex() & 0xff,
                    PathNode = pathNode,
                    OppositePathNode = oppositePathNode,
                    Position = curve.m_Bezier.a,
                    LanePosition = compositionLane.m_Position,
                    TravelDirection = travelDirection,
                    CarriagewayAndGroup = new int2(compositionLane.m_Carriageway, compositionLane.m_Group),
                    Lateral = order,
                    Endpoint = isSourceEndpoint ? "E" : "S",
                    PathMethods = subLane.m_PathMethods,
                    LaneFlags = compositionLane.m_Flags,
                    CarFlags = carLane.m_Flags,
                    RoadTypes = hasCarLaneData ? carLaneData.m_RoadTypes : default,
                    TrackTypes = hasTrackLaneData ? trackLaneData.m_TrackTypes : default,
                    HasCarLaneData = hasCarLaneData,
                    HasTrackLaneData = hasTrackLaneData,
                    HasNetTrackLane = hasNetTrackLane
                });
            }
        }

        private bool TryFindTrafficCompositionLane(
            Entity laneEntity,
            Curve laneCurve,
            NetLaneData laneData,
            NetCompositionData compositionData,
            DynamicBuffer<NetCompositionLane> compositionLanes,
            EdgeGeometry edgeGeometry,
            bool isEnd,
            bool isSourceEndpoint,
            bool[] visitedCompositionLanes,
            out NetCompositionLane result,
            out float order)
        {
            result = default;
            order = 0f;

            LaneFlags disconnectedFlag = isSourceEndpoint ? LaneFlags.DisconnectedEnd : LaneFlags.DisconnectedStart;
            if (EntityManager.HasComponent<MasterLane>(laneEntity))
            {
                return false;
            }

            LaneFlags expectedFlags = laneData.m_Flags & (LaneFlags.Road | LaneFlags.Track | LaneFlags.Underground);
            LaneFlags mask = LaneFlags.Invert | LaneFlags.Slave | LaneFlags.Road | LaneFlags.Track | LaneFlags.Underground | disconnectedFlag;

            if (isSourceEndpoint != isEnd)
            {
                expectedFlags |= LaneFlags.Invert;
            }

            if (EntityManager.HasComponent<SlaveLane>(laneEntity))
            {
                expectedFlags |= LaneFlags.Slave;
            }

            if ((laneData.m_Flags & disconnectedFlag) != 0)
            {
                return false;
            }

            int bestIndex = -1;
            float bestError = float.MaxValue;
            Line2 edgeLine = new Line2(edgeGeometry.m_Start.m_Right.a.xz, edgeGeometry.m_Start.m_Left.a.xz);
            Line2 laneLine = new Line2(laneCurve.m_Bezier.a.xz, laneCurve.m_Bezier.b.xz);

            for (int i = 0; i < compositionLanes.Length; i++)
            {
                if (visitedCompositionLanes[i])
                {
                    continue;
                }

                NetCompositionLane compositionLane = compositionLanes[i];
                if ((compositionLane.m_Flags & mask) != expectedFlags)
                {
                    continue;
                }

                compositionLane.m_Position.x = math.select(-compositionLane.m_Position.x, compositionLane.m_Position.x, isEnd);
                float candidateOrder = compositionLane.m_Position.x / math.max(1f, compositionData.m_Width) + 0.5f;
                if (!MathUtils.Intersect(edgeLine, laneLine, out float2 t))
                {
                    continue;
                }

                float error = math.abs(candidateOrder - t.x);
                if (error < bestError)
                {
                    bestIndex = i;
                    bestError = error;
                    order = candidateOrder;
                    result = compositionLane;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            visitedCompositionLanes[bestIndex] = true;
            return true;
        }

        private static float2 GetAveragePosition(List<LaneEndpoint> lanes)
        {
            float2 origin = default;
            for (int i = 0; i < lanes.Count; i++)
            {
                origin += lanes[i].Position.xz;
            }

            return origin / math.max(1, lanes.Count);
        }
    }
}
