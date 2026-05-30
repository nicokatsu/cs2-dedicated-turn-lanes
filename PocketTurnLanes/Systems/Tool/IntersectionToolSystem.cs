using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Systems.Overlay;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using PathMethod = Game.Pathfind.PathMethod;
using PathNode = Game.Pathfind.PathNode;
using NetCarLane = Game.Net.CarLane;
using NetSubLane = Game.Net.SubLane;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class IntersectionToolSystem : ToolBaseSystem
    {
        private const float PocketLaneLength = 24f;
        private const float SplitGridSize = 8f;
        private const float IntersectionExitBuffer = 0f;
        private const float MinimumPocketLaneLength = 8f;
        private const float MinimumIntersectionSin = 0.2f;
        private const float MaxIntersectionExitDistance = 40f;
        private const float SplitLengthBuffer = 0.16f;
        private const float MaxNodePickDistance = 16f;
        private const float SplitRetryStep = 8f;
        private const int MaxSplitRetryAttempts = 4;
        private const float MinimumRetryProgress = 2f;
        private const float PreviewSplitNodeTolerance = 0.004f;
        private const int MaxReplacementPreviewWaitFrames = 6;
        private const float PrefabWidthTolerance = 0.05f;
        private const float SplitNodePositionTolerance = 2.5f;
        private const float PocketEdgeLengthTolerance = 4f;

        public override string toolID => $"{Mod.ModId} Intersection Tool";

        private IntersectionOverlaySystem m_OverlaySystem;
        private ToolOutputBarrier m_ToolOutputBarrier;
        private ValidationSystem m_ValidationSystem;
        private NodeReductionSystem m_NodeReductionSystem;
        private SplitLaneConnectionFixSystem m_SplitLaneConnectionFixSystem;
        private EntityQuery m_DefinitionQuery;
        private EntityQuery m_ReplacementPreviewDefinitionQuery;
        private EntityQuery m_TempSplitNodeQuery;
        private EntityQuery m_TempPreviewEdgeQuery;
        private EntityQuery m_RoadPrefabQuery;
        private Entity m_HoveredIntersection = Entity.Null;
        private Entity m_PreviewIntersection = Entity.Null;
        private Entity m_PreviewEdge = Entity.Null;
        private PropertyInfo m_DisplayOverridePropertyInfo;
        private bool m_ClearSplitDefinitions;
        private bool m_PreviewDirty;
        private bool m_PreviewReady;
        private bool m_ApplyPreviewNextFrame;
        private bool m_ApplyRetryNextFrame;
        private bool m_ApplyReplacementNextFrame;
        private bool m_RebuildSplitPreviewForApply;
        private bool m_PreviewValidationPending;
        private bool m_VerifyAppliedSplits;
        private bool m_VerifyAppliedReplacements;
        private bool m_HasReplacementPreviewDefinitions;
        private int m_PreviewCreatedFrame = -1;
        private int m_PreviewEdgeCount;
        private readonly List<SplitCandidate> m_PreviewCandidates = new List<SplitCandidate>();
        private readonly List<SplitCandidate> m_NextPreviewCandidates = new List<SplitCandidate>();
        private readonly List<SplitCandidate> m_AppliedCandidates = new List<SplitCandidate>();
        private readonly List<ReplacementCandidate> m_QueuedReplacementCandidates = new List<ReplacementCandidate>();
        private readonly List<ReplacementCandidate> m_AppliedReplacementCandidates = new List<ReplacementCandidate>();

        public event Action<bool> ToolEnabledChanged;

        public bool IsToolEnabled { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_OverlaySystem = World.GetOrCreateSystemManaged<IntersectionOverlaySystem>();
            m_ToolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            m_ValidationSystem = World.GetOrCreateSystemManaged<ValidationSystem>();
            m_NodeReductionSystem = World.GetOrCreateSystemManaged<NodeReductionSystem>();
            m_SplitLaneConnectionFixSystem = Mod.TrafficLaneConnectionFixEnabled
                ? World.GetOrCreateSystemManaged<SplitLaneConnectionFixSystem>()
                : null;
            m_DefinitionQuery = GetDefinitionQuery();
            m_ReplacementPreviewDefinitionQuery = GetEntityQuery(
                ComponentType.ReadOnly<CreationDefinition>(),
                ComponentType.ReadOnly<ReplacementPreviewDefinition>());
            m_TempSplitNodeQuery = GetEntityQuery(
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Node>(),
                ComponentType.Exclude<Deleted>());
            m_TempPreviewEdgeQuery = GetEntityQuery(
                ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Curve>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());
            m_RoadPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<RoadData>(),
                ComponentType.ReadOnly<NetGeometryData>(),
                ComponentType.Exclude<Deleted>());
            m_DisplayOverridePropertyInfo = typeof(Game.Input.ProxyAction).GetProperty("displayOverride");
            m_ToolSystem.EventToolChanged += ToolChanged;

            m_ToolSystem.tools.Remove(this);
            m_ToolSystem.tools.Insert(0, this);
        }

        protected override void OnDestroy()
        {
            SetVanillaMutationSystemsEnabled(true);
            m_ToolSystem.EventToolChanged -= ToolChanged;
            base.OnDestroy();
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            JobHandle result = inputDeps;

            if (m_ClearSplitDefinitions)
            {
                applyMode = ApplyMode.Clear;
                result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, result);
                Mod.log.Info("[IntersectionTool] Split apply window closed; cleared split definition entities.");
                m_ClearSplitDefinitions = false;

                if (m_VerifyAppliedReplacements)
                {
                    VerifyAppliedReplacements();
                }

                if (TryQueueFailedSplitRetries(ref result))
                {
                    return result;
                }

                ResetPreviewState();
                return result;
            }
            else
            {
                applyMode = ApplyMode.None;
            }

            applyAction.shouldBeEnabled = IsToolEnabled;
            secondaryApplyAction.shouldBeEnabled = IsToolEnabled;
            DisableActionTooltips();

            if (!IsToolEnabled)
            {
                return result;
            }

            EnsureVanillaMutationSystemsDisabled();

            if (m_ApplyReplacementNextFrame)
            {
                if (UnityEngine.Time.frameCount <= m_PreviewCreatedFrame)
                {
                    return result;
                }

                CaptureAppliedReplacementCandidates();
                applyMode = ApplyMode.Apply;
                m_ClearSplitDefinitions = true;
                m_ApplyReplacementNextFrame = false;
                Mod.log.Info($"[IntersectionTool] Applying queued pocket lane replacement definitions count={m_AppliedReplacementCandidates.Count}.");
                return result;
            }

            if (m_RebuildSplitPreviewForApply)
            {
                if (UnityEngine.Time.frameCount <= m_PreviewCreatedFrame)
                {
                    return result;
                }

                result = RebuildSplitDefinitionsForApply(result);
                m_RebuildSplitPreviewForApply = false;
                m_ApplyPreviewNextFrame = true;
                m_PreviewReady = true;
                m_PreviewDirty = false;
                m_PreviewValidationPending = false;
                m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                return result;
            }

            if (m_ApplyRetryNextFrame)
            {
                if (UnityEngine.Time.frameCount <= m_PreviewCreatedFrame)
                {
                    return result;
                }

                CaptureAppliedCandidates();
                CaptureAppliedReplacementCandidates();
                applyMode = ApplyMode.Apply;
                m_ClearSplitDefinitions = true;
                m_ApplyRetryNextFrame = false;
                Mod.log.Info($"[IntersectionTool] Applying retry split preview node={FormatEntity(m_PreviewIntersection)} edges={m_PreviewEdgeCount} lastEdge={FormatEntity(m_PreviewEdge)} replacementDefinitions={m_AppliedReplacementCandidates.Count}.");
                return result;
            }

            if ((m_ToolRaycastSystem.raycastFlags & RaycastFlags.UIDisable) != 0)
            {
                bool hadPreview = HasPreviewState();
                UpdateHoveredIntersection(Entity.Null);
                if (hadPreview)
                {
                    result = ClearPreviewDefinitions(result, "raycast blocked by UI");
                }

                return result;
            }

            UpdateHoveredIntersection(GetCurrentRaycastNode());

            if (secondaryApplyAction.WasPressedThisFrame())
            {
                result = ClearPreviewDefinitions(result, "tool cancelled");
                DisableTool();
                return result;
            }

            bool applyPressed = m_HoveredIntersection != Entity.Null && applyAction.WasPressedThisFrame();
            if (applyPressed)
            {
                LogIntersection(m_HoveredIntersection, "clicked");
            }

            if (m_HoveredIntersection == Entity.Null)
            {
                if (HasPreviewState())
                {
                    result = ClearPreviewDefinitions(result, "no hovered intersection");
                }

                return result;
            }

            if (m_PreviewValidationPending)
            {
                if (m_PreviewIntersection != m_HoveredIntersection)
                {
                    result = QueueSplitPreview(m_HoveredIntersection, result);
                }
                else
                {
                    result = ValidateSplitPreview(result);
                }

                if (applyPressed)
                {
                    m_ApplyPreviewNextFrame = true;
                    Mod.log.Info(m_PreviewReady
                        ? "[IntersectionTool] Click arrived as split preview finished validation; applying on the next tool frame."
                        : "[IntersectionTool] Click arrived while split preview was validating; applying after preview retry finishes.");
                }

                return result;
            }

            if (m_PreviewDirty || !m_PreviewReady || m_PreviewIntersection != m_HoveredIntersection)
            {
                result = QueueSplitPreview(m_HoveredIntersection, result);
                if (applyPressed)
                {
                    m_ApplyPreviewNextFrame = true;
                    Mod.log.Info("[IntersectionTool] Click arrived before split preview was ready; applying on the next tool frame.");
                }

                return result;
            }

            if ((applyPressed || m_ApplyPreviewNextFrame) && m_PreviewEdgeCount > 0)
            {
                if (m_HasReplacementPreviewDefinitions &&
                    !m_ReplacementPreviewDefinitionQuery.IsEmptyIgnoreFilter)
                {
                    applyMode = ApplyMode.Clear;
                    result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, result);
                    m_HasReplacementPreviewDefinitions = false;
                    m_RebuildSplitPreviewForApply = true;
                    m_ApplyPreviewNextFrame = true;
                    m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                    Mod.log.Info($"[IntersectionTool] Cleared replacement preview definitions before applying split preview node={FormatEntity(m_PreviewIntersection)} edges={m_PreviewEdgeCount}; fresh split definitions will be rebuilt before apply.");
                    return result;
                }

                if (UnityEngine.Time.frameCount <= m_PreviewCreatedFrame)
                {
                    m_ApplyPreviewNextFrame = true;
                    return result;
                }

                CaptureAppliedCandidates();
                applyMode = ApplyMode.Apply;
                m_ClearSplitDefinitions = true;
                m_ApplyPreviewNextFrame = false;
                Mod.log.Info($"[IntersectionTool] Applying prepared split preview node={FormatEntity(m_PreviewIntersection)} edges={m_PreviewEdgeCount} lastEdge={FormatEntity(m_PreviewEdge)}.");
            }
            else if (applyPressed)
            {
                m_ApplyPreviewNextFrame = false;
                Mod.log.Info($"[IntersectionTool] Click ignored at node={FormatEntity(m_HoveredIntersection)}: no eligible split preview is ready.");
            }

            return result;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            return false;
        }

        public override PrefabBase GetPrefab()
        {
            return null;
        }

        public void EnableTool()
        {
            m_ToolSystem.activeTool = this;
            if (SetToolEnabled(true))
            {
                SetVanillaMutationSystemsEnabled(false);
                Mod.log.Info("[IntersectionTool] Enabled. Hover an intersection to log connected road prefab information.");
            }
        }

        public void DisableTool()
        {
            bool wasEnabled = IsToolEnabled;

            if (m_ToolSystem.activeTool == this)
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
            }

            UpdateHoveredIntersection(Entity.Null);
            ResetPreviewState();
            if (SetToolEnabled(false) || wasEnabled)
            {
                SetVanillaMutationSystemsEnabled(true);
                Mod.log.Info("[IntersectionTool] Disabled.");
            }
        }

        private Entity GetCurrentRaycastNode()
        {
            if (!GetRaycastResult(out Entity entity, out RaycastHit hit))
            {
                return Entity.Null;
            }

            if (IsValidIntersection(entity))
            {
                return entity;
            }

            if (!EntityManager.TryGetComponent(entity, out Edge edge))
            {
                return Entity.Null;
            }

            Entity closestNode = Entity.Null;
            float closestDistance = MaxNodePickDistance;
            if (EntityManager.TryGetComponent(edge.m_Start, out Node startNode))
            {
                float distance = math.distance(startNode.m_Position.xz, hit.m_Position.xz);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestNode = edge.m_Start;
                }
            }

            if (EntityManager.TryGetComponent(edge.m_End, out Node endNode))
            {
                float distance = math.distance(endNode.m_Position.xz, hit.m_Position.xz);
                if (distance < closestDistance)
                {
                    closestNode = edge.m_End;
                }
            }

            return IsValidIntersection(closestNode) ? closestNode : Entity.Null;
        }

        private bool IsValidIntersection(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
            {
                return false;
            }

            if (!EntityManager.HasComponent<Node>(entity) || EntityManager.HasComponent<Roundabout>(entity))
            {
                return false;
            }

            return EntityManager.TryGetBuffer(entity, true, out DynamicBuffer<ConnectedEdge> connectedEdges) && connectedEdges.Length >= 2;
        }

        private void UpdateHoveredIntersection(Entity entity)
        {
            if (entity == m_HoveredIntersection)
            {
                return;
            }

            m_HoveredIntersection = entity;
            m_PreviewDirty = entity != Entity.Null;
            m_PreviewReady = false;
            m_ApplyPreviewNextFrame = false;
            m_PreviewValidationPending = false;
            m_PreviewIntersection = Entity.Null;
            m_PreviewEdge = Entity.Null;

            if (entity == Entity.Null)
            {
                m_OverlaySystem.Clear();
                return;
            }

            m_OverlaySystem.Clear();

            LogIntersection(entity, "hover");
        }

        private void LogIntersection(Entity nodeEntity, string reason)
        {
            if (!EntityManager.Exists(nodeEntity))
            {
                return;
            }

            StringBuilder builder = new StringBuilder();
            builder.Append($"[IntersectionTool] {reason}: node={FormatEntity(nodeEntity)}");

            if (EntityManager.TryGetComponent(nodeEntity, out Node node))
            {
                builder.Append($" position=({node.m_Position.x:0.##}, {node.m_Position.y:0.##}, {node.m_Position.z:0.##})");
            }

            if (EntityManager.TryGetComponent(nodeEntity, out TrafficLights trafficLights))
            {
                builder.Append($" trafficLights={trafficLights.m_Flags}");
            }

            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                builder.Append(" connectedEdges=0");
                Mod.log.Info(builder.ToString());
                return;
            }

            builder.Append($" connectedEdges={connectedEdges.Length}");

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                builder.AppendLine();
                builder.Append("  ");
                builder.Append(i);
                builder.Append(": edge=");
                builder.Append(FormatEntity(edgeEntity));

                if (EntityManager.TryGetComponent(edgeEntity, out Edge edge))
                {
                    string nodeSide = edge.m_Start == nodeEntity ? "start" : edge.m_End == nodeEntity ? "end" : "unknown";
                    builder.Append($" nodeSide={nodeSide}");
                }

                builder.Append(" prefab=");
                builder.Append(GetPrefabName(edgeEntity));
            }

            Mod.log.Info(builder.ToString());
        }

        private JobHandle QueueSplitPreview(Entity nodeEntity, JobHandle inputDeps)
        {
            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                ResetPreviewState();
                Mod.log.Warn($"[IntersectionTool] Cannot preview split {FormatEntity(nodeEntity)}: no ConnectedEdge buffer.");
                return inputDeps;
            }

            applyMode = ApplyMode.Clear;
            inputDeps = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
            m_PreviewCandidates.Clear();

            int queuedCount = 0;
            int skippedCount = 0;
            JobHandle result = inputDeps;
            Entity lastQueuedEdge = Entity.Null;

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (!IsRoadEdge(edgeEntity))
                {
                    skippedCount++;
                    continue;
                }

                if (!HasIncomingCarLane(nodeEntity, edgeEntity, connectedEdges))
                {
                    skippedCount++;
                    Mod.log.Info($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: no car lane entering node {FormatEntity(nodeEntity)}.");
                    continue;
                }

                if (!ApproachNeedsPocketLane(nodeEntity, edgeEntity, connectedEdges, out string demandReason))
                {
                    skippedCount++;
                    Mod.log.Info($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: no dedicated turn lane demand at node {FormatEntity(nodeEntity)}. {demandReason}");
                    continue;
                }

                if (TryBuildSplitDefinitionRequest(
                        nodeEntity,
                        edgeEntity,
                        out SplitDefinitionRequest request,
                        out float splitPosition,
                        out float splitDistance,
                        out float intersectionDistance,
                        out float pocketDistance,
                        out float targetDistance))
                {
                    if (!TryFindPocketLaneReplacementPrefab(
                            nodeEntity,
                            edgeEntity,
                            out ReplacementPrefabMatch prefabMatch))
                    {
                        skippedCount++;
                        continue;
                    }

                    JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                    {
                        Request = request,
                        ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                    }.Schedule(result);

                    m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);

                    queuedCount++;
                    m_PreviewCandidates.Add(new SplitCandidate
                    {
                        Node = nodeEntity,
                        Edge = edgeEntity,
                        SourcePrefab = request.Prefab,
                        TargetPrefab = prefabMatch.Prefab,
                        InvertTarget = prefabMatch.Invert,
                        CurvePosition = splitPosition,
                        HitPosition = request.HitPosition,
                        TargetDistance = targetDistance,
                        SplitDistance = splitDistance,
                        IntersectionDistance = intersectionDistance,
                        PocketDistance = pocketDistance,
                        OriginalForwardLanes = prefabMatch.OriginalCounts.Forward,
                        OriginalBackwardLanes = prefabMatch.OriginalCounts.Backward,
                        TargetForwardLanes = prefabMatch.TargetCounts.Forward,
                        TargetBackwardLanes = prefabMatch.TargetCounts.Backward,
                        Attempt = 0
                    });
                    Mod.log.Info($"[IntersectionTool] Prepared split preview edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} split={splitPosition:0.###} target={targetDistance:0.##}m distance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m replacement={GetPrefabNameFromPrefab(prefabMatch.Prefab)} orientation={(prefabMatch.Invert ? "reversed" : "direct")} lanes={prefabMatch.OriginalCounts}->{prefabMatch.TargetCounts} frame={UnityEngine.Time.frameCount}.");
                    result = createDefinitionJobHandle;
                    lastQueuedEdge = edgeEntity;
                }
                else
                {
                    skippedCount++;
                }
            }

            if (queuedCount > 0)
            {
                m_PreviewIntersection = nodeEntity;
                m_PreviewEdge = lastQueuedEdge;
                m_PreviewEdgeCount = queuedCount;
                m_PreviewReady = false;
                m_PreviewValidationPending = true;
                m_PreviewDirty = false;
                m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                ShowExpandableIntersectionOverlay(nodeEntity);
                Mod.log.Info($"[IntersectionTool] Created {queuedCount} preview split definitions around node {FormatEntity(nodeEntity)}; skipped {skippedCount} edge(s). Validating visible split nodes before click apply.");
            }
            else
            {
                applyMode = ApplyMode.None;
                MarkNoSplitPreviewReady(nodeEntity);
                Mod.log.Info($"[IntersectionTool] No entering road edges were eligible to preview around node {FormatEntity(nodeEntity)}.");
            }

            return result;
        }

        private void ShowExpandableIntersectionOverlay(Entity nodeEntity)
        {
            if (EntityManager.TryGetComponent(nodeEntity, out NodeGeometry geometry))
            {
                m_OverlaySystem.ShowBounds(geometry.m_Bounds);
                return;
            }

            m_OverlaySystem.Clear();
        }

        private bool IsRoadEdge(Entity edgeEntity)
        {
            return edgeEntity != Entity.Null &&
                   EntityManager.Exists(edgeEntity) &&
                   EntityManager.HasComponent<Edge>(edgeEntity) &&
                   EntityManager.HasComponent<Road>(edgeEntity);
        }

        private bool HasIncomingCarLane(Entity nodeEntity, Entity edgeEntity, DynamicBuffer<ConnectedEdge> connectedEdges)
        {
            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<NetSubLane> nodeSubLanes))
            {
                return false;
            }

            for (int i = 0; i < nodeSubLanes.Length; i++)
            {
                Entity nodeLaneEntity = nodeSubLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<NetCarLane>(nodeLaneEntity) ||
                    !EntityManager.TryGetComponent(nodeLaneEntity, out Lane nodeLane))
                {
                    continue;
                }

                if (TryGetSourceEdgeFromNodeLane(nodeLane, connectedEdges, out Entity sourceEdge) && sourceEdge == edgeEntity)
                {
                    return true;
                }
            }

            return false;
        }

        private bool ApproachNeedsPocketLane(
            Entity nodeEntity,
            Entity edgeEntity,
            DynamicBuffer<ConnectedEdge> connectedEdges,
            out string reason)
        {
            int roadEdgeCount = CountRoadConnectedEdges(connectedEdges);
            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<NetSubLane> nodeSubLanes))
            {
                reason = $"roadEdges={roadEdgeCount}; missing node SubLane buffer, skipping because demand cannot be diagnosed from connector allocation.";
                return false;
            }

            Dictionary<int, ApproachLaneUsage> laneUsages = new Dictionary<int, ApproachLaneUsage>();
            Dictionary<int, HashSet<ApproachMovementKey>> laneMovementKeys = new Dictionary<int, HashSet<ApproachMovementKey>>();
            StringBuilder connectorSummary = new StringBuilder();
            int connectorCount = 0;
            int ignoredConnectorCount = 0;

            for (int i = 0; i < nodeSubLanes.Length; i++)
            {
                NetSubLane subLane = nodeSubLanes[i];
                Entity laneEntity = subLane.m_SubLane;
                if ((subLane.m_PathMethods & PathMethod.Road) == 0 ||
                    laneEntity == Entity.Null ||
                    !EntityManager.Exists(laneEntity) ||
                    EntityManager.HasComponent<Deleted>(laneEntity) ||
                    IsMasterConnectorLane(laneEntity) ||
                    !EntityManager.HasComponent<NetCarLane>(laneEntity) ||
                    !EntityManager.TryGetComponent(laneEntity, out Lane lane) ||
                    !TryGetConnectedEdgesFromLane(nodeEntity, lane, out Entity sourceEdge, out Entity targetEdge))
                {
                    continue;
                }

                if (sourceEdge != edgeEntity)
                {
                    continue;
                }

                if (targetEdge == edgeEntity || !IsRoadEdge(targetEdge))
                {
                    ignoredConnectorCount++;
                    continue;
                }

                connectorCount++;
                int sourceLaneIndex = lane.m_StartNode.GetLaneIndex() & 0xff;
                int targetLaneIndex = lane.m_EndNode.GetLaneIndex() & 0xff;
                NetCarLane carLane = EntityManager.GetComponentData<NetCarLane>(laneEntity);
                ApproachMovement movement = ClassifyCenterMovement(nodeEntity, edgeEntity, targetEdge, carLane.m_Flags);
                ApproachMovementKey movementKey = new ApproachMovementKey(movement, targetEdge);

                if (!laneUsages.TryGetValue(sourceLaneIndex, out ApproachLaneUsage usage))
                {
                    usage = new ApproachLaneUsage
                    {
                        LaneIndex = sourceLaneIndex
                    };
                }

                usage.Add(movement);
                laneUsages[sourceLaneIndex] = usage;

                if (!laneMovementKeys.TryGetValue(sourceLaneIndex, out HashSet<ApproachMovementKey> movementKeys))
                {
                    movementKeys = new HashSet<ApproachMovementKey>();
                    laneMovementKeys[sourceLaneIndex] = movementKeys;
                }

                movementKeys.Add(movementKey);

                if (connectorSummary.Length > 0)
                {
                    connectorSummary.Append(",");
                }

                connectorSummary.Append(sourceLaneIndex);
                connectorSummary.Append("->");
                connectorSummary.Append(targetLaneIndex);
                connectorSummary.Append("/");
                connectorSummary.Append(movement);
                connectorSummary.Append("/");
                connectorSummary.Append(FormatEntity(targetEdge));
                connectorSummary.Append("/");
                connectorSummary.Append(FormatEntity(laneEntity));
            }

            if (connectorCount == 0)
            {
                reason = $"roadEdges={roadEdgeCount}; no center connector evidence for source edge, skipping instead of guessing from road or lane counts.";
                return false;
            }

            HashSet<ApproachMovementKey> dedicatedTurnKeys = new HashSet<ApproachMovementKey>();
            HashSet<ApproachMovementKey> mixedTurnKeys = new HashSet<ApproachMovementKey>();
            HashSet<ApproachMovementKey> allKeys = new HashSet<ApproachMovementKey>();
            int mixedLaneCount = 0;
            int ambiguousLaneCount = 0;

            foreach (KeyValuePair<int, HashSet<ApproachMovementKey>> pair in laneMovementKeys)
            {
                bool laneMixed = pair.Value.Count > 1;
                if (laneMixed)
                {
                    mixedLaneCount++;
                }

                foreach (ApproachMovementKey key in pair.Value)
                {
                    allKeys.Add(key);
                    if (key.Movement == ApproachMovement.Ambiguous)
                    {
                        ambiguousLaneCount++;
                    }

                    if (!key.IsTurn)
                    {
                        continue;
                    }

                    if (laneMixed)
                    {
                        mixedTurnKeys.Add(key);
                    }
                    else
                    {
                        dedicatedTurnKeys.Add(key);
                    }
                }
            }

            bool needsLeft = false;
            bool needsRight = false;
            List<ApproachMovementKey> unmetTurnKeys = new List<ApproachMovementKey>();
            foreach (ApproachMovementKey key in mixedTurnKeys)
            {
                if (dedicatedTurnKeys.Contains(key))
                {
                    continue;
                }

                unmetTurnKeys.Add(key);
                if (key.Movement == ApproachMovement.Left)
                {
                    needsLeft = true;
                }
                else if (key.Movement == ApproachMovement.Right)
                {
                    needsRight = true;
                }
            }

            bool needsPocket = needsLeft || needsRight;
            string usageSummary = FormatApproachLaneUsages(laneUsages);
            string keySummary = FormatApproachMovementKeys(laneMovementKeys);
            string dedicatedSummary = FormatApproachMovementKeySet(dedicatedTurnKeys);
            string mixedSummary = FormatApproachMovementKeySet(mixedTurnKeys);
            string unmetSummary = FormatApproachMovementKeySet(unmetTurnKeys);
            string diagnostics = $"roadEdges={roadEdgeCount} connectors={connectorCount} ignored={ignoredConnectorCount} lanes={laneUsages.Count} distinctTargets={allKeys.Count} mixedLanes={mixedLaneCount} ambiguousKeys={ambiguousLaneCount} dedicatedTurnKeys=[{dedicatedSummary}] mixedTurnKeys=[{mixedSummary}] unmetTurnKeys=[{unmetSummary}] needsLeft={needsLeft} needsRight={needsRight} usage=[{usageSummary}] targetUsage=[{keySummary}] connectors=[{connectorSummary}]";

            if (!needsPocket)
            {
                reason = $"{diagnostics}; existing connector allocation is already turn-dedicated or has no split turn demand.";
                return false;
            }

            reason = diagnostics;
            Mod.log.Info($"[IntersectionTool] Edge {FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} has dedicated turn lane demand at node {FormatEntity(nodeEntity)}. {reason}");
            return true;
        }

        private int CountRoadConnectedEdges(DynamicBuffer<ConnectedEdge> connectedEdges)
        {
            int count = 0;
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                if (IsRoadEdge(connectedEdges[i].m_Edge))
                {
                    count++;
                }
            }

            return count;
        }

        private bool TryGetConnectedEdgesFromLane(Entity node, Lane lane, out Entity sourceEdge, out Entity targetEdge)
        {
            sourceEdge = Entity.Null;
            targetEdge = Entity.Null;
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            sourceEdge = FindEdgeByPathNode(connectedEdges, lane.m_StartNode);
            targetEdge = lane.m_StartNode.OwnerEquals(lane.m_EndNode)
                ? sourceEdge
                : FindEdgeByPathNode(connectedEdges, lane.m_EndNode);
            return sourceEdge != Entity.Null && targetEdge != Entity.Null;
        }

        private static Entity FindEdgeByPathNode(DynamicBuffer<ConnectedEdge> connectedEdges, PathNode node)
        {
            int ownerIndex = node.GetOwnerIndex();
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                if (connectedEdges[i].m_Edge.Index == ownerIndex)
                {
                    return connectedEdges[i].m_Edge;
                }
            }

            return Entity.Null;
        }

        private bool IsMasterConnectorLane(Entity laneEntity)
        {
            if (EntityManager.HasComponent<MasterLane>(laneEntity))
            {
                return true;
            }

            if (EntityManager.TryGetComponent(laneEntity, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetLaneData laneData))
            {
                return (laneData.m_Flags & LaneFlags.Master) != 0;
            }

            return false;
        }

        private ApproachMovement ClassifyCenterMovement(Entity intersectionNode, Entity sourceEdge, Entity targetEdge, CarLaneFlags flags)
        {
            bool leftFlag = (flags & CarLaneFlags.TurnLeft) != 0;
            bool rightFlag = (flags & CarLaneFlags.TurnRight) != 0;
            if (leftFlag && !rightFlag)
            {
                return ApproachMovement.Left;
            }

            if (rightFlag && !leftFlag)
            {
                return ApproachMovement.Right;
            }

            if (!TryGetEdgeDirectionFromNode(sourceEdge, intersectionNode, out float2 sourceOutward) ||
                !TryGetEdgeDirectionFromNode(targetEdge, intersectionNode, out float2 targetOutward))
            {
                return ApproachMovement.Ambiguous;
            }

            float2 incoming = -sourceOutward;
            float cross = Cross(incoming, targetOutward);
            float dot = math.dot(incoming, targetOutward);
            if (math.abs(cross) < 0.25f)
            {
                return dot > 0f ? ApproachMovement.Straight : ApproachMovement.Ambiguous;
            }

            return cross > 0f ? ApproachMovement.Left : ApproachMovement.Right;
        }

        private bool TryGetEdgeDirectionFromNode(Entity edgeEntity, Entity nodeEntity, out float2 direction)
        {
            direction = default;
            if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Curve curve))
            {
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            if (!nodeIsStart && !nodeIsEnd)
            {
                return false;
            }

            return TryGetOutwardDirection(edge, curve, nodeEntity, nodeIsStart, out direction);
        }

        private static string FormatApproachLaneUsages(Dictionary<int, ApproachLaneUsage> laneUsages)
        {
            if (laneUsages.Count == 0)
            {
                return "<none>";
            }

            List<ApproachLaneUsage> usages = new List<ApproachLaneUsage>(laneUsages.Values);
            usages.Sort((a, b) => a.LaneIndex.CompareTo(b.LaneIndex));

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < usages.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                ApproachLaneUsage usage = usages[i];
                builder.Append("lane");
                builder.Append(usage.LaneIndex);
                builder.Append(":S");
                builder.Append(usage.Straight);
                builder.Append("/L");
                builder.Append(usage.Left);
                builder.Append("/R");
                builder.Append(usage.Right);
                builder.Append("/A");
                builder.Append(usage.Ambiguous);
            }

            return builder.ToString();
        }

        private static string FormatApproachMovementKeys(Dictionary<int, HashSet<ApproachMovementKey>> laneMovementKeys)
        {
            if (laneMovementKeys.Count == 0)
            {
                return "<none>";
            }

            List<int> laneIndexes = new List<int>(laneMovementKeys.Keys);
            laneIndexes.Sort();

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < laneIndexes.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                int laneIndex = laneIndexes[i];
                builder.Append("lane");
                builder.Append(laneIndex);
                builder.Append(":");
                builder.Append(FormatApproachMovementKeySet(laneMovementKeys[laneIndex]));
            }

            return builder.ToString();
        }

        private static string FormatApproachMovementKeySet(IEnumerable<ApproachMovementKey> keys)
        {
            if (keys == null)
            {
                return "<none>";
            }

            List<ApproachMovementKey> sortedKeys = new List<ApproachMovementKey>(keys);
            if (sortedKeys.Count == 0)
            {
                return "<none>";
            }

            sortedKeys.Sort((a, b) =>
            {
                int movementCompare = a.Movement.CompareTo(b.Movement);
                if (movementCompare != 0)
                {
                    return movementCompare;
                }

                int indexCompare = a.TargetEdge.Index.CompareTo(b.TargetEdge.Index);
                return indexCompare != 0 ? indexCompare : a.TargetEdge.Version.CompareTo(b.TargetEdge.Version);
            });

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < sortedKeys.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("|");
                }

                ApproachMovementKey key = sortedKeys[i];
                builder.Append(key.Movement);
                builder.Append("->");
                builder.Append(FormatEntity(key.TargetEdge));
            }

            return builder.ToString();
        }

        private bool TryGetSourceEdgeFromNodeLane(Lane nodeLane, DynamicBuffer<ConnectedEdge> connectedEdges, out Entity sourceEdge)
        {
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity connectedEdge = connectedEdges[i].m_Edge;
                if (!EntityManager.TryGetBuffer(connectedEdge, true, out DynamicBuffer<NetSubLane> edgeSubLanes))
                {
                    continue;
                }

                for (int j = 0; j < edgeSubLanes.Length; j++)
                {
                    Entity edgeLaneEntity = edgeSubLanes[j].m_SubLane;
                    if (!EntityManager.HasComponent<NetCarLane>(edgeLaneEntity) ||
                        !EntityManager.TryGetComponent(edgeLaneEntity, out Lane edgeLane))
                    {
                        continue;
                    }

                    if (nodeLane.m_StartNode.Equals(edgeLane.m_EndNode) || nodeLane.m_StartNode.Equals(edgeLane.m_StartNode))
                    {
                        sourceEdge = connectedEdge;
                        return true;
                    }
                }
            }

            sourceEdge = Entity.Null;
            return false;
        }

        private bool TryFindPocketLaneReplacementPrefab(
            Entity nodeEntity,
            Entity edgeEntity,
            out ReplacementPrefabMatch match)
        {
            match = default;

            if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef sourcePrefabRef) ||
                !EntityManager.TryGetComponent(sourcePrefabRef.m_Prefab, out NetGeometryData sourceGeometry) ||
                !EntityManager.TryGetComponent(sourcePrefabRef.m_Prefab, out RoadData sourceRoadData) ||
                !EntityManager.TryGetComponent(sourcePrefabRef.m_Prefab, out NetData sourceNetData))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)}: missing edge or source prefab data.");
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            if (!nodeIsStart && !nodeIsEnd)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)}: node={FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            if (!TryGetRoadLaneCounts(edgeEntity, sourcePrefabRef.m_Prefab, out RoadLaneCounts originalCounts, out string laneSource))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot search replacement prefab for edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)}: no default road lane counts were found.");
                return false;
            }

            RoadLaneCounts desiredCounts = originalCounts;
            if (nodeIsEnd)
            {
                desiredCounts.Forward++;
            }
            else
            {
                desiredCounts.Backward++;
            }

            bool found = false;
            int scannedCount = 0;
            int widthMatchCount = 0;
            int laneMatchCount = 0;
            int missingLaneCount = 0;
            int bestScore = int.MaxValue;
            ReplacementPrefabMatch bestMatch = default;

            using (NativeArray<Entity> prefabEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    Entity candidatePrefab = prefabEntities[i];
                    scannedCount++;

                    if (candidatePrefab == sourcePrefabRef.m_Prefab ||
                        !EntityManager.TryGetComponent(candidatePrefab, out NetGeometryData candidateGeometry) ||
                        math.abs(candidateGeometry.m_DefaultWidth - sourceGeometry.m_DefaultWidth) > PrefabWidthTolerance)
                    {
                        continue;
                    }

                    widthMatchCount++;
                    if (!TryGetDefaultRoadLaneCounts(candidatePrefab, out RoadLaneCounts candidateCounts, out _))
                    {
                        missingLaneCount++;
                        continue;
                    }

                    if (!TryMatchRoadLaneCounts(candidateCounts, desiredCounts, out bool invert))
                    {
                        continue;
                    }

                    laneMatchCount++;
                    EntityManager.TryGetComponent(candidatePrefab, out RoadData candidateRoadData);
                    EntityManager.TryGetComponent(candidatePrefab, out NetData candidateNetData);
                    int score = GetReplacementPrefabScore(
                        sourceRoadData,
                        sourceNetData,
                        sourceGeometry,
                        candidateRoadData,
                        candidateNetData,
                        candidateGeometry,
                        invert);

                    if (!found || score < bestScore)
                    {
                        found = true;
                        bestScore = score;
                        bestMatch = new ReplacementPrefabMatch
                        {
                            Prefab = candidatePrefab,
                            Invert = invert,
                            OriginalCounts = originalCounts,
                            TargetCounts = desiredCounts,
                            CandidateCounts = candidateCounts,
                            Score = score
                        };
                    }
                }
            }

            if (!found)
            {
                Mod.log.Warn($"[IntersectionTool] No pocket lane replacement prefab found sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={laneSource} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={originalCounts} desiredLanes={desiredCounts} scanned={scannedCount} widthMatches={widthMatchCount} laneMatches=0 missingLaneData={missingLaneCount}.");
                return false;
            }

            match = bestMatch;
            Mod.log.Info($"[IntersectionTool] Replacement prefab selected sourceEdge={FormatEntity(edgeEntity)} sourcePrefab={GetPrefabName(edgeEntity)} targetPrefab={GetPrefabNameFromPrefab(match.Prefab)} orientation={(match.Invert ? "reversed" : "direct")} nodeSide={(nodeIsStart ? "start" : "end")} laneSource={laneSource} width={sourceGeometry.m_DefaultWidth:0.##}m originalLanes={match.OriginalCounts} desiredLanes={match.TargetCounts} candidateLanes={match.CandidateCounts} scanned={scannedCount} widthMatches={widthMatchCount} laneMatches={laneMatchCount} missingLaneData={missingLaneCount} score={match.Score}.");
            return true;
        }

        private bool TryGetRoadLaneCounts(Entity edgeEntity, Entity fallbackPrefab, out RoadLaneCounts counts, out string source)
        {
            if (EntityManager.TryGetComponent(edgeEntity, out Composition composition) &&
                TryGetCompositionRoadLaneCounts(composition.m_Edge, out counts))
            {
                source = $"Composition:{FormatEntity(composition.m_Edge)}";
                return true;
            }

            if (TryGetDefaultRoadLaneCounts(fallbackPrefab, out counts, out string prefabLaneSource))
            {
                source = prefabLaneSource;
                return true;
            }

            counts = default;
            source = "Missing";
            return false;
        }

        private bool TryGetCompositionRoadLaneCounts(Entity compositionEntity, out RoadLaneCounts counts)
        {
            counts = default;
            if (compositionEntity == Entity.Null ||
                !EntityManager.TryGetBuffer(compositionEntity, true, out DynamicBuffer<NetCompositionLane> lanes))
            {
                return false;
            }

            for (int i = 0; i < lanes.Length; i++)
            {
                CountRoadLane(lanes[i].m_Flags, ref counts);
            }

            return counts.Total > 0;
        }

        private bool TryGetDefaultRoadLaneCounts(Entity prefabEntity, out RoadLaneCounts counts, out string source)
        {
            counts = default;
            source = "Missing";

            if (prefabEntity == Entity.Null)
            {
                return false;
            }

            if (EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<DefaultNetLane> lanes))
            {
                for (int i = 0; i < lanes.Length; i++)
                {
                    CountRoadLane(lanes[i].m_Flags, ref counts);
                }

                if (counts.Total > 0)
                {
                    source = "DefaultNetLane";
                    return true;
                }
            }

            if (TryGetCompositionLaneCounts(prefabEntity, default, out counts))
            {
                source = "NetGeometryComposition:default";
                return true;
            }

            if (TryCalculateDefaultRoadLaneCounts(prefabEntity, out counts))
            {
                source = "NetGeometrySection:calculated";
                return true;
            }

            counts = default;
            return false;
        }

        private bool TryGetCompositionLaneCounts(Entity prefabEntity, CompositionFlags mask, out RoadLaneCounts counts)
        {
            counts = default;

            if (!EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<NetGeometryComposition> compositions))
            {
                return false;
            }

            for (int i = 0; i < compositions.Length; i++)
            {
                NetGeometryComposition composition = compositions[i];
                if (composition.m_Mask != mask ||
                    !EntityManager.TryGetBuffer(composition.m_Composition, true, out DynamicBuffer<NetCompositionLane> lanes))
                {
                    continue;
                }

                for (int j = 0; j < lanes.Length; j++)
                {
                    CountRoadLane(lanes[j].m_Flags, ref counts);
                }

                return counts.Total > 0;
            }

            return false;
        }

        private bool TryCalculateDefaultRoadLaneCounts(Entity prefabEntity, out RoadLaneCounts counts)
        {
            counts = default;

            if (!EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<NetGeometrySection> sections))
            {
                return false;
            }

            NativeList<NetCompositionPiece> pieces = new NativeList<NetCompositionPiece>(32, Allocator.Temp);
            NativeList<NetCompositionLane> lanes = new NativeList<NetCompositionLane>(32, Allocator.Temp);
            try
            {
                NetCompositionData compositionData = default;
                NetCompositionHelpers.GetCompositionPieces(
                    pieces,
                    sections.AsNativeArray(),
                    default,
                    GetBufferLookup<NetSubSection>(true),
                    GetBufferLookup<NetSectionPiece>(true));
                NetCompositionHelpers.AddCompositionLanes(
                    Entity.Null,
                    ref compositionData,
                    pieces,
                    lanes,
                    default,
                    GetComponentLookup<NetLaneData>(true),
                    GetBufferLookup<NetPieceLane>(true));

                for (int i = 0; i < lanes.Length; i++)
                {
                    CountRoadLane(lanes[i].m_Flags, ref counts);
                }

                return counts.Total > 0;
            }
            catch (Exception ex)
            {
                Mod.log.Warn(ex, $"[IntersectionTool] Failed to calculate default road lanes for prefab={GetPrefabNameFromPrefab(prefabEntity)} entity={FormatEntity(prefabEntity)}.");
                counts = default;
                return false;
            }
            finally
            {
                if (lanes.IsCreated)
                {
                    lanes.Dispose();
                }

                if (pieces.IsCreated)
                {
                    pieces.Dispose();
                }
            }
        }

        private static void CountRoadLane(LaneFlags flags, ref RoadLaneCounts counts)
        {
            if ((flags & (LaneFlags.Master | LaneFlags.Road)) != LaneFlags.Road)
            {
                return;
            }

            if ((flags & LaneFlags.Invert) != 0)
            {
                counts.Backward++;
            }
            else
            {
                counts.Forward++;
            }
        }

        private static bool TryMatchRoadLaneCounts(RoadLaneCounts candidateCounts, RoadLaneCounts desiredCounts, out bool invert)
        {
            if (candidateCounts.Forward == desiredCounts.Forward &&
                candidateCounts.Backward == desiredCounts.Backward)
            {
                invert = false;
                return true;
            }

            if (candidateCounts.Forward == desiredCounts.Backward &&
                candidateCounts.Backward == desiredCounts.Forward)
            {
                invert = true;
                return true;
            }

            invert = false;
            return false;
        }

        private static int GetReplacementPrefabScore(
            RoadData sourceRoadData,
            NetData sourceNetData,
            NetGeometryData sourceGeometry,
            RoadData candidateRoadData,
            NetData candidateNetData,
            NetGeometryData candidateGeometry,
            bool invert)
        {
            int score = invert ? 1000 : 0;

            if (sourceNetData.m_RequiredLayers != candidateNetData.m_RequiredLayers)
            {
                score += 200;
            }

            Game.Prefabs.RoadFlags comparableRoadFlags =
                Game.Prefabs.RoadFlags.EnableZoning |
                Game.Prefabs.RoadFlags.UseHighwayRules;
            if ((sourceRoadData.m_Flags & comparableRoadFlags) != (candidateRoadData.m_Flags & comparableRoadFlags))
            {
                score += 100;
            }

            score += (int)math.round(math.abs(sourceRoadData.m_SpeedLimit - candidateRoadData.m_SpeedLimit) * 10f);
            score += (int)math.round(math.abs(sourceGeometry.m_DefaultWidth - candidateGeometry.m_DefaultWidth) * 100f);
            return score;
        }

        private bool TryBuildSplitDefinitionRequest(
            Entity nodeEntity,
            Entity edgeEntity,
            out SplitDefinitionRequest request,
            out float splitPosition,
            out float splitDistance,
            out float intersectionDistance,
            out float pocketDistance,
            out float targetDistance,
            float targetPocketLength = PocketLaneLength)
        {
            request = default;
            splitPosition = 0f;
            splitDistance = 0f;
            intersectionDistance = 0f;
            pocketDistance = 0f;
            targetDistance = 0f;

            if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: missing Edge, Curve, or PrefabRef.");
                return false;
            }

            if (curve.m_Length <= 0.01f)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: curve length is {curve.m_Length:0.###}.");
                return false;
            }

            bool nodeIsStart = edge.m_Start == nodeEntity;
            bool nodeIsEnd = edge.m_End == nodeEntity;
            if (!nodeIsStart && !nodeIsEnd)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: node {FormatEntity(nodeEntity)} is not an endpoint.");
                return false;
            }

            if (!EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometryData))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot split edge {FormatEntity(edgeEntity)}: prefab {FormatEntity(prefabRef.m_Prefab)} has no NetGeometryData.");
                return false;
            }

            GetMinMaxSplitPositions(
                curve.m_Length,
                geometryData.m_DefaultWidth,
                geometryData.m_EdgeLengthRange.min,
                out float minSplit,
                out float maxSplit);

            if (minSplit >= maxSplit)
            {
                Mod.log.Info($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: too short to split safely (length={curve.m_Length:0.##}m).");
                return false;
            }

            intersectionDistance = GetIntersectionExitDistance(
                nodeEntity,
                edgeEntity,
                edge,
                curve,
                nodeIsStart);
            float maxDistanceFromNode = curve.m_Length * 0.5f;
            if (maxDistanceFromNode - intersectionDistance < MinimumPocketLaneLength)
            {
                Mod.log.Info($"[IntersectionTool] Skip edge {FormatEntity(edgeEntity)}: not enough room for an aligned pocket lane (length={curve.m_Length:0.##}m intersection={intersectionDistance:0.##}m).");
                return false;
            }

            float desiredDistance = GetGridAlignedSplitDistance(
                intersectionDistance,
                targetPocketLength,
                MinimumPocketLaneLength,
                maxDistanceFromNode);
            targetDistance = desiredDistance;
            float desiredPosition = GetCurvePositionAtDistance(curve, nodeIsStart, desiredDistance);
            splitPosition = math.clamp(desiredPosition, minSplit, maxSplit);
            splitDistance = GetCurveDistanceFromNode(curve, nodeIsStart, splitPosition);
            pocketDistance = math.max(0f, splitDistance - intersectionDistance);

            float3 hitPosition = MathUtils.Position(curve.m_Bezier, splitPosition);
            int randomSeed = EntityManager.TryGetComponent(edgeEntity, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : edgeEntity.Index;

            request = new SplitDefinitionRequest
            {
                Edge = edgeEntity,
                Prefab = prefabRef.m_Prefab,
                HitPosition = hitPosition,
                CurvePosition = splitPosition,
                RandomSeed = randomSeed
            };

            return true;
        }

        private float GetIntersectionExitDistance(
            Entity nodeEntity,
            Entity edgeEntity,
            Edge edge,
            Curve curve,
            bool nodeIsStart)
        {
            float fallbackDistance = IntersectionExitBuffer;
            if (!TryGetOutwardDirection(edge, curve, nodeEntity, nodeIsStart, out float2 currentDirection))
            {
                return fallbackDistance;
            }

            if (!EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return fallbackDistance;
            }

            float result = fallbackDistance;
            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity otherEdgeEntity = connectedEdges[i].m_Edge;
                if (otherEdgeEntity == edgeEntity ||
                    !IsRoadEdge(otherEdgeEntity) ||
                    !EntityManager.TryGetComponent(otherEdgeEntity, out Edge otherEdge) ||
                    !EntityManager.TryGetComponent(otherEdgeEntity, out Curve otherCurve))
                {
                    continue;
                }

                bool otherNodeIsStart = otherEdge.m_Start == nodeEntity;
                bool otherNodeIsEnd = otherEdge.m_End == nodeEntity;
                if ((!otherNodeIsStart && !otherNodeIsEnd) ||
                    !TryGetOutwardDirection(otherEdge, otherCurve, nodeEntity, otherNodeIsStart, out float2 otherDirection))
                {
                    continue;
                }

                float sinAngle = math.abs(Cross(currentDirection, otherDirection));
                if (sinAngle < MinimumIntersectionSin ||
                    !TryGetEdgeHalfWidthAtNode(
                        otherEdgeEntity,
                        nodeEntity,
                        otherNodeIsStart,
                        out float otherHalfWidth,
                        out string widthSource,
                        out float edgeGeometryWidth,
                        out float prefabWidth))
                {
                    continue;
                }

                float candidate = otherHalfWidth / sinAngle + IntersectionExitBuffer;
                result = math.max(result, math.min(candidate, MaxIntersectionExitDistance));
                Mod.log.Info($"[IntersectionTool] Exit width compare node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} crossEdge={FormatEntity(otherEdgeEntity)} crossPrefab={GetPrefabName(otherEdgeEntity)} source={widthSource} edgeGeometryWidth={FormatMeters(edgeGeometryWidth)} prefabWidth={FormatMeters(prefabWidth)} sin={sinAngle:0.###} candidate={candidate:0.##}m result={result:0.##}m.");
            }

            Mod.log.Info($"[IntersectionTool] Exit distance result node={FormatEntity(nodeEntity)} edge={FormatEntity(edgeEntity)} prefab={GetPrefabName(edgeEntity)} distance={result:0.##}m.");
            return result;
        }

        private bool TryGetEdgeHalfWidthAtNode(
            Entity edgeEntity,
            Entity nodeEntity,
            bool nodeIsStart,
            out float halfWidth,
            out string widthSource,
            out float edgeGeometryWidth,
            out float prefabWidth)
        {
            edgeGeometryWidth = 0f;
            prefabWidth = 0f;

            if (EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef) &&
                EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetGeometryData geometryData) &&
                geometryData.m_DefaultWidth > 0f)
            {
                prefabWidth = geometryData.m_DefaultWidth;
            }

            if (EntityManager.TryGetComponent(edgeEntity, out EdgeGeometry edgeGeometry) &&
                EntityManager.TryGetComponent(nodeEntity, out Node node) &&
                TryGetSegmentWidthAtNode(node.m_Position, nodeIsStart ? edgeGeometry.m_Start : edgeGeometry.m_End, out edgeGeometryWidth))
            {
                halfWidth = edgeGeometryWidth * 0.5f;
                widthSource = "EdgeGeometry";
                return true;
            }

            if (prefabWidth > 0f)
            {
                halfWidth = prefabWidth * 0.5f;
                widthSource = "PrefabFallback";
                return true;
            }

            halfWidth = 0f;
            widthSource = "Missing";
            return false;
        }

        private static string FormatMeters(float value)
        {
            return value > 0f ? $"{value:0.##}m" : "<missing>";
        }

        private static bool TryGetSegmentWidthAtNode(float3 nodePosition, Segment segment, out float width)
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

        private static float GetGridAlignedSplitDistance(
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

            float alignedPocketLength = math.ceil(targetPocketLength / SplitGridSize) * SplitGridSize;
            if (alignedPocketLength <= maxPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            alignedPocketLength = math.floor(maxPocketLength / SplitGridSize) * SplitGridSize;
            if (alignedPocketLength >= minimumPocketLength)
            {
                return intersectionDistance + alignedPocketLength;
            }

            return maximumDistance;
        }

        private static float GetCurvePositionAtDistance(Curve curve, bool fromStart, float distance)
        {
            if (distance <= 0f)
            {
                return fromStart ? 0f : 1f;
            }

            Bezier4x3 bezier = fromStart ? curve.m_Bezier : MathUtils.Invert(curve.m_Bezier);
            Bounds1 range = new Bounds1(0f, 1f);
            float remainingDistance = distance;
            float position = MathUtils.ClampLength(bezier, ref range, ref remainingDistance) ? range.max : 1f;
            return fromStart ? position : 1f - position;
        }

        private static float GetCurveDistanceFromNode(Curve curve, bool fromStart, float position)
        {
            position = math.saturate(position);
            Bounds1 range = fromStart ? new Bounds1(0f, position) : new Bounds1(position, 1f);
            return MathUtils.Length(curve.m_Bezier, range);
        }

        private static bool TryGetOutwardDirection(Edge edge, Curve curve, Entity nodeEntity, bool nodeIsStart, out float2 direction)
        {
            float3 tangent = MathUtils.Tangent(curve.m_Bezier, nodeIsStart ? 0f : 1f);
            if (!nodeIsStart)
            {
                tangent = -tangent;
            }

            direction = tangent.xz;
            if (math.lengthsq(direction) <= 0.0001f)
            {
                if (edge.m_Start == nodeEntity)
                {
                    direction = (curve.m_Bezier.d - curve.m_Bezier.a).xz;
                }
                else if (edge.m_End == nodeEntity)
                {
                    direction = (curve.m_Bezier.a - curve.m_Bezier.d).xz;
                }
            }

            float lengthSq = math.lengthsq(direction);
            if (lengthSq <= 0.0001f)
            {
                direction = default;
                return false;
            }

            direction *= math.rsqrt(lengthSq);
            return true;
        }

        private static float Cross(float2 a, float2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static float GetMinimumSplitDistance(float edgeLength, float roadWidth, float minEdgeLengthRange)
        {
            if (edgeLength <= 0f)
            {
                return 0.5f;
            }

            float halfWidth = roadWidth * 0.5f;
            float minEdgeLength = math.max(halfWidth, minEdgeLengthRange) + SplitLengthBuffer;
            return math.saturate(minEdgeLength / edgeLength);
        }

        private static void GetMinMaxSplitPositions(
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

        private string GetPrefabName(Entity entity)
        {
            if (!EntityManager.TryGetComponent(entity, out PrefabRef prefabRef))
            {
                return "<no PrefabRef>";
            }

            if (m_PrefabSystem.TryGetPrefab(prefabRef.m_Prefab, out PrefabBase prefabBase))
            {
                return prefabBase.name;
            }

            return $"<unresolved {FormatEntity(prefabRef.m_Prefab)}>";
        }

        private string GetPrefabNameFromPrefab(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null)
            {
                return "<null prefab>";
            }

            if (m_PrefabSystem.TryGetPrefab(prefabEntity, out PrefabBase prefabBase))
            {
                return prefabBase.name;
            }

            return $"<unresolved {FormatEntity(prefabEntity)}>";
        }

        private static string FormatEntity(Entity entity)
        {
            return $"{entity.Index}:{entity.Version}";
        }

        private bool SetToolEnabled(bool enabled)
        {
            if (IsToolEnabled == enabled)
            {
                return false;
            }

            IsToolEnabled = enabled;
            ToolEnabledChanged?.Invoke(enabled);
            return true;
        }

        private bool HasPreviewState()
        {
            return m_PreviewDirty ||
                   m_PreviewReady ||
                   m_PreviewValidationPending ||
                   m_ApplyPreviewNextFrame ||
                   m_RebuildSplitPreviewForApply ||
                   m_HasReplacementPreviewDefinitions ||
                   m_PreviewIntersection != Entity.Null ||
                   m_PreviewEdge != Entity.Null ||
                   m_PreviewEdgeCount > 0;
        }

        private void ResetPreviewState()
        {
            m_PreviewIntersection = Entity.Null;
            m_PreviewEdge = Entity.Null;
            m_PreviewDirty = false;
            m_PreviewReady = false;
            m_ApplyPreviewNextFrame = false;
            m_ApplyRetryNextFrame = false;
            m_ApplyReplacementNextFrame = false;
            m_RebuildSplitPreviewForApply = false;
            m_PreviewValidationPending = false;
            m_PreviewCreatedFrame = -1;
            m_PreviewEdgeCount = 0;
            m_HasReplacementPreviewDefinitions = false;
            m_PreviewCandidates.Clear();
            m_NextPreviewCandidates.Clear();
            m_AppliedCandidates.Clear();
            m_VerifyAppliedSplits = false;
            m_QueuedReplacementCandidates.Clear();
            m_AppliedReplacementCandidates.Clear();
            m_VerifyAppliedReplacements = false;
        }

        private JobHandle ClearPreviewDefinitions(JobHandle inputDeps, string reason)
        {
            applyMode = ApplyMode.Clear;
            JobHandle result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
            ResetPreviewState();
            Mod.log.Info($"[IntersectionTool] Cleared split preview definitions ({reason}).");
            return result;
        }

        private JobHandle ValidateSplitPreview(JobHandle inputDeps)
        {
            if (UnityEngine.Time.frameCount <= m_PreviewCreatedFrame)
            {
                return inputDeps;
            }

            if (m_PreviewCandidates.Count == 0)
            {
                m_PreviewValidationPending = false;
                m_PreviewReady = false;
                return inputDeps;
            }

            m_NextPreviewCandidates.Clear();
            int visibleCount = 0;
            int retryCount = 0;
            int exhaustedCount = 0;
            bool needsRetry = false;

            for (int i = 0; i < m_PreviewCandidates.Count; i++)
            {
                SplitCandidate candidate = m_PreviewCandidates[i];
                if (TryFindPreviewSplitNode(candidate, out _))
                {
                    visibleCount++;
                    m_NextPreviewCandidates.Add(candidate);
                    continue;
                }

                if (candidate.Attempt >= MaxSplitRetryAttempts)
                {
                    exhaustedCount++;
                    Mod.log.Warn($"[IntersectionTool] Preview split still has no generated node edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={candidate.Attempt} distance={candidate.SplitDistance:0.##}m; no more retry room.");
                    continue;
                }

                int nextAttempt = candidate.Attempt + 1;
                float retryPocketLength = PocketLaneLength + SplitRetryStep * nextAttempt;
                if (!TryBuildSplitDefinitionRequest(
                        candidate.Node,
                        candidate.Edge,
                        out SplitDefinitionRequest request,
                        out float splitPosition,
                        out float splitDistance,
                        out float intersectionDistance,
                        out float pocketDistance,
                        out float targetDistance,
                        retryPocketLength))
                {
                    exhaustedCount++;
                    Mod.log.Warn($"[IntersectionTool] Preview retry cannot be prepared edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} requestedPocket={retryPocketLength:0.##}m.");
                    continue;
                }

                if (splitDistance < candidate.SplitDistance + MinimumRetryProgress)
                {
                    exhaustedCount++;
                    Mod.log.Warn($"[IntersectionTool] Preview retry cannot move far enough edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} previous={candidate.SplitDistance:0.##}m next={splitDistance:0.##}m.");
                    continue;
                }

                retryCount++;
                needsRetry = true;
                m_NextPreviewCandidates.Add(new SplitCandidate
                {
                    Node = candidate.Node,
                    Edge = candidate.Edge,
                    SourcePrefab = candidate.SourcePrefab,
                    TargetPrefab = candidate.TargetPrefab,
                    InvertTarget = candidate.InvertTarget,
                    CurvePosition = splitPosition,
                    HitPosition = request.HitPosition,
                    TargetDistance = targetDistance,
                    SplitDistance = splitDistance,
                    IntersectionDistance = intersectionDistance,
                    PocketDistance = pocketDistance,
                    OriginalForwardLanes = candidate.OriginalForwardLanes,
                    OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                    TargetForwardLanes = candidate.TargetForwardLanes,
                    TargetBackwardLanes = candidate.TargetBackwardLanes,
                    Attempt = nextAttempt
                });
                Mod.log.Info($"[IntersectionTool] Preview split missing edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)}; retry attempt={nextAttempt} split={splitPosition:0.###} target={targetDistance:0.##}m distance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m.");
            }

            if (needsRetry)
            {
                return RequeueSplitPreview(m_NextPreviewCandidates, inputDeps, visibleCount, retryCount, exhaustedCount);
            }

            if (m_NextPreviewCandidates.Count == 0)
            {
                applyMode = ApplyMode.Clear;
                JobHandle clearResult = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);
                MarkNoSplitPreviewReady(m_PreviewIntersection);
                Mod.log.Info($"[IntersectionTool] Split preview validation complete node={FormatEntity(m_PreviewIntersection)} visible=0, retried=0, exhausted={exhaustedCount}; no visible split definitions remain.");
                return clearResult;
            }

            if (m_NextPreviewCandidates.Count != m_PreviewCandidates.Count)
            {
                return RequeueSplitPreview(m_NextPreviewCandidates, inputDeps, visibleCount, 0, exhaustedCount);
            }

            JobHandle result = inputDeps;
            int replacementPreviewCount = 0;
            for (int i = 0; i < m_PreviewCandidates.Count; i++)
            {
                if (TryApplyReplacementPreview(m_PreviewCandidates[i], ref result))
                {
                    replacementPreviewCount++;
                }
            }

            if (replacementPreviewCount < m_PreviewCandidates.Count &&
                UnityEngine.Time.frameCount - m_PreviewCreatedFrame < MaxReplacementPreviewWaitFrames)
            {
                m_PreviewValidationPending = true;
                m_PreviewReady = false;
                Mod.log.Info($"[IntersectionTool] Waiting for replacement preview edges node={FormatEntity(m_PreviewIntersection)} previewed={replacementPreviewCount}/{m_PreviewCandidates.Count} frameDelta={UnityEngine.Time.frameCount - m_PreviewCreatedFrame}.");
                return result;
            }

            m_NextPreviewCandidates.Clear();
            m_PreviewValidationPending = false;
            m_PreviewReady = true;
            m_PreviewDirty = false;
            Mod.log.Info($"[IntersectionTool] Split preview validation complete node={FormatEntity(m_PreviewIntersection)} visible={visibleCount}, retried=0, exhausted={exhaustedCount}, replacementPreviewed={replacementPreviewCount}. Ready for click apply.");
            return result;
        }

        private JobHandle RequeueSplitPreview(List<SplitCandidate> candidates, JobHandle inputDeps, int visibleCount, int retryCount, int exhaustedCount)
        {
            applyMode = ApplyMode.Clear;
            JobHandle result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, inputDeps);

            m_PreviewCandidates.Clear();
            int queuedCount = 0;
            Entity previewNode = Entity.Null;
            Entity lastQueuedEdge = Entity.Null;

            for (int i = 0; i < candidates.Count; i++)
            {
                SplitCandidate candidate = candidates[i];
                float targetPocketLength = PocketLaneLength + SplitRetryStep * candidate.Attempt;
                if (!TryBuildSplitDefinitionRequest(
                        candidate.Node,
                        candidate.Edge,
                        out SplitDefinitionRequest request,
                        out float splitPosition,
                        out float splitDistance,
                        out float intersectionDistance,
                        out float pocketDistance,
                        out float targetDistance,
                        targetPocketLength))
                {
                    Mod.log.Warn($"[IntersectionTool] Cannot rebuild preview split edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={candidate.Attempt}.");
                    continue;
                }

                JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                {
                    Request = request,
                    ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                }.Schedule(result);

                m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);

                m_PreviewCandidates.Add(new SplitCandidate
                {
                    Node = candidate.Node,
                    Edge = candidate.Edge,
                    SourcePrefab = candidate.SourcePrefab,
                    TargetPrefab = candidate.TargetPrefab,
                    InvertTarget = candidate.InvertTarget,
                    CurvePosition = splitPosition,
                    HitPosition = request.HitPosition,
                    TargetDistance = targetDistance,
                    SplitDistance = splitDistance,
                    IntersectionDistance = intersectionDistance,
                    PocketDistance = pocketDistance,
                    OriginalForwardLanes = candidate.OriginalForwardLanes,
                    OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                    TargetForwardLanes = candidate.TargetForwardLanes,
                    TargetBackwardLanes = candidate.TargetBackwardLanes,
                    Attempt = candidate.Attempt
                });

                queuedCount++;
                previewNode = candidate.Node;
                lastQueuedEdge = candidate.Edge;
                result = createDefinitionJobHandle;
            }

            if (queuedCount == 0)
            {
                applyMode = ApplyMode.None;
                m_OverlaySystem.Clear();
                ResetPreviewState();
                Mod.log.Warn("[IntersectionTool] Preview retry had no split definitions left to queue.");
                return result;
            }

            m_PreviewIntersection = previewNode;
            m_PreviewEdge = lastQueuedEdge;
            m_PreviewEdgeCount = queuedCount;
            m_PreviewReady = false;
            m_PreviewValidationPending = true;
            m_PreviewDirty = false;
            m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
            ShowExpandableIntersectionOverlay(previewNode);
            Mod.log.Info($"[IntersectionTool] Rebuilt preview split definitions for retry pass node={FormatEntity(previewNode)} definitions={queuedCount}, visible={visibleCount}, retrying={retryCount}, exhausted={exhaustedCount}.");
            return result;
        }

        private JobHandle RebuildSplitDefinitionsForApply(JobHandle inputDeps)
        {
            JobHandle result = inputDeps;
            int queuedCount = 0;
            Entity previewNode = Entity.Null;
            Entity lastQueuedEdge = Entity.Null;

            for (int i = 0; i < m_PreviewCandidates.Count; i++)
            {
                SplitCandidate candidate = m_PreviewCandidates[i];
                if (candidate.Edge == Entity.Null ||
                    candidate.SourcePrefab == Entity.Null ||
                    !EntityManager.Exists(candidate.Edge))
                {
                    Mod.log.Warn($"[IntersectionTool] Cannot rebuild clean apply split definition edge={FormatEntity(candidate.Edge)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)}: edge or prefab is missing.");
                    continue;
                }

                int randomSeed = EntityManager.TryGetComponent(candidate.Edge, out PseudoRandomSeed seed)
                    ? seed.m_Seed
                    : candidate.Edge.Index;

                SplitDefinitionRequest request = new SplitDefinitionRequest
                {
                    Edge = candidate.Edge,
                    Prefab = candidate.SourcePrefab,
                    HitPosition = candidate.HitPosition,
                    CurvePosition = candidate.CurvePosition,
                    RandomSeed = randomSeed
                };

                JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                {
                    Request = request,
                    ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                }.Schedule(result);

                m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
                result = createDefinitionJobHandle;
                queuedCount++;
                previewNode = candidate.Node;
                lastQueuedEdge = candidate.Edge;
            }

            m_PreviewIntersection = previewNode;
            m_PreviewEdge = lastQueuedEdge;
            m_PreviewEdgeCount = queuedCount;
            Mod.log.Info($"[IntersectionTool] Rebuilt clean split definitions for apply node={FormatEntity(previewNode)} definitions={queuedCount}; replacement preview definitions were discarded before apply.");
            return result;
        }

        private void MarkNoSplitPreviewReady(Entity nodeEntity)
        {
            m_OverlaySystem.Clear();
            m_PreviewIntersection = nodeEntity;
            m_PreviewEdge = Entity.Null;
            m_PreviewEdgeCount = 0;
            m_PreviewReady = true;
            m_PreviewValidationPending = false;
            m_PreviewDirty = false;
            m_ApplyPreviewNextFrame = false;
            m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
            m_PreviewCandidates.Clear();
            m_NextPreviewCandidates.Clear();
            m_QueuedReplacementCandidates.Clear();
        }

        private bool TryFindPreviewSplitNode(SplitCandidate candidate, out Entity splitNode)
        {
            splitNode = Entity.Null;

            using (NativeArray<Entity> entities = m_TempSplitNodeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempSplitNodeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    Temp temp = temps[i];
                    if (temp.m_Original != candidate.Edge ||
                        (temp.m_Flags & TempFlags.Replace) != TempFlags.Replace ||
                        (temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0)
                    {
                        continue;
                    }

                    if (math.abs(temp.m_CurvePosition - candidate.CurvePosition) <= PreviewSplitNodeTolerance)
                    {
                        splitNode = entities[i];
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryApplyReplacementPreview(SplitCandidate candidate, ref JobHandle result)
        {
            if (candidate.TargetPrefab == Entity.Null)
            {
                return false;
            }

            if (!TryFindPreviewSplitNode(candidate, out Entity splitNode) ||
                !TryFindPreviewPocketEdge(candidate, splitNode, out Entity pocketEdge, out float lengthError))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot preview pocket lane replacement original={FormatEntity(candidate.Edge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)}: preview pocket edge was not found.");
                return false;
            }

            if (!TryFindPreviewOuterEdge(
                candidate,
                splitNode,
                pocketEdge,
                out Entity outerEdge,
                out float outerLengthError))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot preview pocket lane replacement original={FormatEntity(candidate.Edge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)}: preview outer edge was not found.");
                return false;
            }

            ReplacementCandidate replacementCandidate = new ReplacementCandidate
            {
                Node = candidate.Node,
                SplitNode = splitNode,
                OriginalEdge = candidate.Edge,
                PocketEdge = pocketEdge,
                SourcePrefab = candidate.SourcePrefab,
                TargetPrefab = candidate.TargetPrefab,
                InvertTarget = candidate.InvertTarget,
                HitPosition = candidate.HitPosition,
                OriginalForwardLanes = candidate.OriginalForwardLanes,
                OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                TargetForwardLanes = candidate.TargetForwardLanes,
                TargetBackwardLanes = candidate.TargetBackwardLanes
            };

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest definitionRequest))
            {
                return false;
            }

            if (!TryBuildPreviewSourceDefinitionRequest(outerEdge, candidate.SourcePrefab, out ReplacementDefinitionRequest outerDefinitionRequest))
            {
                return false;
            }

            definitionRequest.PreviewOnly = true;
            outerDefinitionRequest.PreviewOnly = true;

            JobHandle definitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = definitionRequest,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(result);
            JobHandle outerDefinitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = outerDefinitionRequest,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(definitionJobHandle);

            m_ToolOutputBarrier.AddJobHandleForProducer(definitionJobHandle);
            m_ToolOutputBarrier.AddJobHandleForProducer(outerDefinitionJobHandle);
            result = outerDefinitionJobHandle;
            m_HasReplacementPreviewDefinitions = true;

            Mod.log.Info($"[IntersectionTool] Created pocket lane replacement definition preview original={FormatEntity(candidate.Edge)} pocket={FormatEntity(pocketEdge)} outer={FormatEntity(outerEdge)} splitNode={FormatEntity(splitNode)} splitNodePrefab=definition-driven sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} pocketFlags={definitionRequest.Flags} outerFlags={outerDefinitionRequest.Flags} collisionValidation=vanilla-disabled pocketComposition=definition-driven outerComposition=source-definition lanes={candidate.OriginalForwardLanes}/{candidate.OriginalBackwardLanes}->{candidate.TargetForwardLanes}/{candidate.TargetBackwardLanes} pocketLengthError={lengthError:0.##}m outerLengthError={outerLengthError:0.##}m.");
            return true;
        }

        private bool TryFindPreviewPocketEdge(
            SplitCandidate candidate,
            Entity splitNode,
            out Entity pocketEdge,
            out float lengthError)
        {
            pocketEdge = Entity.Null;
            lengthError = 0f;

            float bestScore = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            Entity bestRejectedEdge = Entity.Null;
            float bestRejectedLengthError = float.MaxValue;
            int tempEdgeCount = 0;
            int connectedMatchCount = 0;
            int prefabMatchCount = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    Temp temp = temps[i];
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0 ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    bool connectsSplitNode = edge.m_Start == splitNode || edge.m_End == splitNode;
                    Entity otherNode = edge.m_Start == splitNode ? edge.m_End : edge.m_Start;
                    bool connectsCandidateNode = connectsSplitNode && IsSameOrTempOriginalNode(otherNode, candidate.Node);
                    if (!connectsCandidateNode)
                    {
                        continue;
                    }

                    connectedMatchCount++;
                    if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                        prefabRef.m_Prefab != candidate.TargetPrefab)
                    {
                        continue;
                    }

                    prefabMatchCount++;
                    float candidateLengthError = math.abs(curve.m_Length - candidate.SplitDistance);
                    if (candidateLengthError > PocketEdgeLengthTolerance)
                    {
                        if (candidateLengthError < bestRejectedLengthError)
                        {
                            bestRejectedLengthError = candidateLengthError;
                            bestRejectedEdge = edgeEntity;
                        }

                        continue;
                    }

                    float score = candidateLengthError;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestLengthError = candidateLengthError;
                        bestEdge = edgeEntity;
                    }
                }
            }

            if (bestEdge == Entity.Null)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot find preview pocket edge original={FormatEntity(candidate.Edge)} splitNode={FormatEntity(splitNode)} expectedDistance={candidate.SplitDistance:0.##}m tempEdges={tempEdgeCount} connectedMatches={connectedMatchCount} prefabMatches={prefabMatchCount} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}.");
                return false;
            }

            pocketEdge = bestEdge;
            lengthError = bestLengthError;
            return true;
        }

        private bool TryFindPreviewOuterEdge(
            SplitCandidate candidate,
            Entity splitNode,
            Entity pocketEdge,
            out Entity outerEdge,
            out float lengthError)
        {
            outerEdge = Entity.Null;
            lengthError = 0f;

            float expectedLength = -1f;
            if (EntityManager.TryGetComponent(candidate.Edge, out Curve originalCurve))
            {
                expectedLength = math.max(0f, originalCurve.m_Length - candidate.SplitDistance);
            }

            float bestScore = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            Entity bestRejectedEdge = Entity.Null;
            float bestRejectedLengthError = float.MaxValue;
            int tempEdgeCount = 0;
            int connectedMatchCount = 0;
            int prefabMatchCount = 0;

            using (NativeArray<Entity> entities = m_TempPreviewEdgeQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempPreviewEdgeQuery.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity edgeEntity = entities[i];
                    if (edgeEntity == pocketEdge)
                    {
                        continue;
                    }

                    Temp temp = temps[i];
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) != (TempFlags)0 ||
                        !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                        !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                        !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                    {
                        continue;
                    }

                    tempEdgeCount++;
                    if (edge.m_Start != splitNode && edge.m_End != splitNode)
                    {
                        continue;
                    }

                    Entity otherNode = edge.m_Start == splitNode ? edge.m_End : edge.m_Start;
                    if (IsSameOrTempOriginalNode(otherNode, candidate.Node))
                    {
                        continue;
                    }

                    connectedMatchCount++;
                    if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                        prefabRef.m_Prefab != candidate.TargetPrefab)
                    {
                        continue;
                    }

                    prefabMatchCount++;
                    float candidateLengthError = expectedLength >= 0f
                        ? math.abs(curve.m_Length - expectedLength)
                        : 0f;
                    if (expectedLength >= 0f && candidateLengthError > PocketEdgeLengthTolerance)
                    {
                        if (candidateLengthError < bestRejectedLengthError)
                        {
                            bestRejectedLengthError = candidateLengthError;
                            bestRejectedEdge = edgeEntity;
                        }

                        continue;
                    }

                    float sourcePrefabPenalty = prefabRef.m_Prefab == candidate.SourcePrefab ? 0f : PocketEdgeLengthTolerance;
                    float score = candidateLengthError + sourcePrefabPenalty;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestLengthError = candidateLengthError;
                        bestEdge = edgeEntity;
                    }
                }
            }

            if (bestEdge == Entity.Null)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot find preview outer edge original={FormatEntity(candidate.Edge)} splitNode={FormatEntity(splitNode)} expectedLength={FormatMeters(expectedLength)} tempEdges={tempEdgeCount} connectedMatches={connectedMatchCount} prefabMatches={prefabMatchCount} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}.");
                return false;
            }

            outerEdge = bestEdge;
            lengthError = bestLengthError;
            return true;
        }

        private bool IsSameOrTempOriginalNode(Entity node, Entity originalNode)
        {
            if (node == originalNode)
            {
                return true;
            }

            return node != Entity.Null &&
                   EntityManager.TryGetComponent(node, out Temp temp) &&
                   temp.m_Original == originalNode &&
                   (temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel)) == (TempFlags)0;
        }

        private void SetVanillaMutationSystemsEnabled(bool enabled)
        {
            if (m_ValidationSystem != null)
            {
                m_ValidationSystem.Enabled = enabled;
            }

            if (m_NodeReductionSystem != null)
            {
                m_NodeReductionSystem.Enabled = enabled;
            }
        }

        private void EnsureVanillaMutationSystemsDisabled()
        {
            bool validationWasEnabled = m_ValidationSystem != null && m_ValidationSystem.Enabled;
            bool nodeReductionWasEnabled = m_NodeReductionSystem != null && m_NodeReductionSystem.Enabled;
            if (!validationWasEnabled && !nodeReductionWasEnabled)
            {
                return;
            }

            SetVanillaMutationSystemsEnabled(false);
            Mod.log.Warn($"[IntersectionTool] Vanilla mutation systems were enabled while the tool was active; disabled again to keep split/replacement previews isolated validationWasEnabled={validationWasEnabled} nodeReductionWasEnabled={nodeReductionWasEnabled}.");
        }

        private void CaptureAppliedCandidates()
        {
            m_AppliedCandidates.Clear();
            m_AppliedCandidates.AddRange(m_PreviewCandidates);
            m_VerifyAppliedSplits = m_AppliedCandidates.Count > 0;
        }

        private void CaptureAppliedReplacementCandidates()
        {
            m_AppliedReplacementCandidates.Clear();
            m_AppliedReplacementCandidates.AddRange(m_QueuedReplacementCandidates);
            m_QueuedReplacementCandidates.Clear();
            m_VerifyAppliedReplacements = m_AppliedReplacementCandidates.Count > 0;
        }

        private void VerifyAppliedReplacements()
        {
            m_VerifyAppliedReplacements = false;

            int verifiedCount = 0;
            int missingCount = 0;
            int replacedEntityCount = 0;

            for (int i = 0; i < m_AppliedReplacementCandidates.Count; i++)
            {
                ReplacementCandidate candidate = m_AppliedReplacementCandidates[i];
                if (EntityManager.Exists(candidate.PocketEdge) &&
                    !EntityManager.HasComponent<Deleted>(candidate.PocketEdge) &&
                    EntityManager.TryGetComponent(candidate.PocketEdge, out PrefabRef prefabRef) &&
                    prefabRef.m_Prefab == candidate.TargetPrefab)
                {
                    verifiedCount++;
                    Mod.log.Info($"[IntersectionTool] Pocket lane replacement verified edge={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} lanes={candidate.OriginalForwardLanes}/{candidate.OriginalBackwardLanes}->{candidate.TargetForwardLanes}/{candidate.TargetBackwardLanes}.");
                    QueueSplitLaneConnectionFix(candidate, candidate.PocketEdge);
                    continue;
                }

                if (TryFindReplacementResultEdge(candidate, out Entity resultEdge))
                {
                    replacedEntityCount++;
                    Mod.log.Info($"[IntersectionTool] Pocket lane replacement verified via replacement entity original={FormatEntity(candidate.PocketEdge)} result={FormatEntity(resultEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")}.");
                    QueueSplitLaneConnectionFix(candidate, resultEdge);
                    continue;
                }

                missingCount++;
                Mod.log.Warn($"[IntersectionTool] Pocket lane replacement not visible after apply original={FormatEntity(candidate.OriginalEdge)} pocket={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)} orientation={(candidate.InvertTarget ? "reversed" : "direct")} node={FormatEntity(candidate.Node)} splitNode={FormatEntity(candidate.SplitNode)}.");
            }

            Mod.log.Info($"[IntersectionTool] Pocket lane replacement verification complete verified={verifiedCount}, replacedEntity={replacedEntityCount}, missing={missingCount}.");
            m_AppliedReplacementCandidates.Clear();
        }

        private void QueueSplitLaneConnectionFix(ReplacementCandidate candidate, Entity finalPocketEdge)
        {
            if (m_SplitLaneConnectionFixSystem == null)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot queue split lane connection fix pocket={FormatEntity(finalPocketEdge)} splitNode={FormatEntity(candidate.SplitNode)}: fix system is not available.");
                return;
            }

            m_SplitLaneConnectionFixSystem.Queue(
                candidate.Node,
                candidate.SplitNode,
                candidate.OriginalEdge,
                finalPocketEdge,
                candidate.SourcePrefab,
                candidate.TargetPrefab);
        }

        private bool TryQueueFailedSplitRetries(ref JobHandle result)
        {
            if (!m_VerifyAppliedSplits)
            {
                return false;
            }

            m_VerifyAppliedSplits = false;
            m_PreviewCandidates.Clear();
            m_QueuedReplacementCandidates.Clear();

            int succeededCount = 0;
            int retryCount = 0;
            int replacementCount = 0;
            int exhaustedCount = 0;
            Entity retryNode = Entity.Null;
            Entity lastRetryEdge = Entity.Null;

            for (int i = 0; i < m_AppliedCandidates.Count; i++)
            {
                SplitCandidate candidate = m_AppliedCandidates[i];
                bool replacementQueued = TryQueuePocketLaneReplacement(candidate, ref result, out bool foundPocketEdge);
                if (foundPocketEdge || !IsEdgeStillConnectedToNode(candidate.Node, candidate.Edge))
                {
                    succeededCount++;
                    if (replacementQueued)
                    {
                        replacementCount++;
                    }

                    continue;
                }

                if (candidate.Attempt >= MaxSplitRetryAttempts)
                {
                    exhaustedCount++;
                    Mod.log.Warn($"[IntersectionTool] Split still failed edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} after {candidate.Attempt} retry attempt(s); leaving it unchanged.");
                    continue;
                }

                int nextAttempt = candidate.Attempt + 1;
                float retryPocketLength = PocketLaneLength + SplitRetryStep * nextAttempt;
                if (!TryBuildSplitDefinitionRequest(
                        candidate.Node,
                        candidate.Edge,
                        out SplitDefinitionRequest request,
                        out float splitPosition,
                        out float splitDistance,
                        out float intersectionDistance,
                        out float pocketDistance,
                        out float targetDistance,
                        retryPocketLength))
                {
                    exhaustedCount++;
                    Mod.log.Warn($"[IntersectionTool] Retry split cannot be prepared edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} requestedPocket={retryPocketLength:0.##}m.");
                    continue;
                }

                if (splitDistance < candidate.SplitDistance + MinimumRetryProgress)
                {
                    exhaustedCount++;
                    Mod.log.Warn($"[IntersectionTool] Retry split cannot move far enough edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} previous={candidate.SplitDistance:0.##}m next={splitDistance:0.##}m.");
                    continue;
                }

                JobHandle createDefinitionJobHandle = new CreateSplitDefinitionJob
                {
                    Request = request,
                    ECB = m_ToolOutputBarrier.CreateCommandBuffer()
                }.Schedule(result);

                m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);

                m_PreviewCandidates.Add(new SplitCandidate
                {
                    Node = candidate.Node,
                    Edge = candidate.Edge,
                    SourcePrefab = candidate.SourcePrefab,
                    TargetPrefab = candidate.TargetPrefab,
                    InvertTarget = candidate.InvertTarget,
                    CurvePosition = splitPosition,
                    HitPosition = request.HitPosition,
                    TargetDistance = targetDistance,
                    SplitDistance = splitDistance,
                    IntersectionDistance = intersectionDistance,
                    PocketDistance = pocketDistance,
                    OriginalForwardLanes = candidate.OriginalForwardLanes,
                    OriginalBackwardLanes = candidate.OriginalBackwardLanes,
                    TargetForwardLanes = candidate.TargetForwardLanes,
                    TargetBackwardLanes = candidate.TargetBackwardLanes,
                    Attempt = nextAttempt
                });

                retryCount++;
                retryNode = candidate.Node;
                lastRetryEdge = candidate.Edge;
                result = createDefinitionJobHandle;
                Mod.log.Info($"[IntersectionTool] Retrying failed split edge={FormatEntity(candidate.Edge)} prefab={GetPrefabName(candidate.Edge)} attempt={nextAttempt} split={splitPosition:0.###} target={targetDistance:0.##}m distance={splitDistance:0.##}m intersection={intersectionDistance:0.##}m pocket={pocketDistance:0.##}m.");
            }

            m_AppliedCandidates.Clear();
            if (retryCount == 0)
            {
                if (replacementCount > 0)
                {
                    m_PreviewIntersection = m_QueuedReplacementCandidates[0].Node;
                    m_PreviewEdge = Entity.Null;
                    m_PreviewEdgeCount = 0;
                    m_PreviewReady = true;
                    m_PreviewDirty = false;
                    m_ApplyPreviewNextFrame = false;
                    m_ApplyReplacementNextFrame = true;
                    m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                    Mod.log.Info($"[IntersectionTool] Split verification complete: succeeded={succeededCount}, retryQueued=0, exhausted={exhaustedCount}, replacementQueued={replacementCount}. Replacement definitions will be applied on the next tool frame.");
                    return true;
                }

                Mod.log.Info($"[IntersectionTool] Split verification complete: succeeded={succeededCount}, retryQueued=0, exhausted={exhaustedCount}, replacementQueued=0.");
                return false;
            }

            m_PreviewIntersection = retryNode;
            m_PreviewEdge = lastRetryEdge;
            m_PreviewEdgeCount = retryCount;
            m_PreviewReady = true;
            m_PreviewDirty = false;
            m_ApplyPreviewNextFrame = false;
            m_ApplyRetryNextFrame = true;
            m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
            Mod.log.Info($"[IntersectionTool] Split verification queued {retryCount} retry split definition(s); succeeded={succeededCount}, exhausted={exhaustedCount}, replacementQueued={replacementCount}. They will be applied on the next tool frame.");
            return true;
        }

        private bool IsEdgeStillConnectedToNode(Entity nodeEntity, Entity edgeEntity)
        {
            if (nodeEntity == Entity.Null ||
                edgeEntity == Entity.Null ||
                !EntityManager.Exists(nodeEntity) ||
                !EntityManager.Exists(edgeEntity) ||
                !EntityManager.TryGetBuffer(nodeEntity, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                if (connectedEdges[i].m_Edge == edgeEntity)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryQueuePocketLaneReplacement(
            SplitCandidate splitCandidate,
            ref JobHandle result,
            out bool foundPocketEdge)
        {
            foundPocketEdge = false;

            if (splitCandidate.TargetPrefab == Entity.Null)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot queue pocket lane replacement original={FormatEntity(splitCandidate.Edge)}: no target prefab was selected.");
                return false;
            }

            if (!TryFindPocketEdge(
                    splitCandidate,
                    out Entity pocketEdge,
                    out Entity splitNode,
                    out float splitNodeDistance,
                    out float lengthError,
                    true))
            {
                return false;
            }

            foundPocketEdge = true;

            if (EntityManager.TryGetComponent(pocketEdge, out PrefabRef pocketPrefabRef) &&
                pocketPrefabRef.m_Prefab == splitCandidate.TargetPrefab)
            {
                Mod.log.Info($"[IntersectionTool] Pocket lane replacement already present after split original={FormatEntity(splitCandidate.Edge)} pocket={FormatEntity(pocketEdge)} splitNode={FormatEntity(splitNode)} targetPrefab={GetPrefabNameFromPrefab(splitCandidate.TargetPrefab)} orientation={(splitCandidate.InvertTarget ? "reversed" : "direct")} splitNodeDistance={splitNodeDistance:0.##}m lengthError={lengthError:0.##}m.");
                if (m_SplitLaneConnectionFixSystem != null)
                {
                    m_SplitLaneConnectionFixSystem.Queue(
                        splitCandidate.Node,
                        splitNode,
                        splitCandidate.Edge,
                        pocketEdge,
                        splitCandidate.SourcePrefab,
                        splitCandidate.TargetPrefab);
                }

                return false;
            }

            ReplacementCandidate replacementCandidate = new ReplacementCandidate
            {
                Node = splitCandidate.Node,
                SplitNode = splitNode,
                OriginalEdge = splitCandidate.Edge,
                PocketEdge = pocketEdge,
                SourcePrefab = splitCandidate.SourcePrefab,
                TargetPrefab = splitCandidate.TargetPrefab,
                InvertTarget = splitCandidate.InvertTarget,
                HitPosition = splitCandidate.HitPosition,
                OriginalForwardLanes = splitCandidate.OriginalForwardLanes,
                OriginalBackwardLanes = splitCandidate.OriginalBackwardLanes,
                TargetForwardLanes = splitCandidate.TargetForwardLanes,
                TargetBackwardLanes = splitCandidate.TargetBackwardLanes
            };

            if (!TryBuildReplacementDefinitionRequest(replacementCandidate, out ReplacementDefinitionRequest request))
            {
                return false;
            }

            JobHandle createDefinitionJobHandle = new CreateReplacementDefinitionJob
            {
                Request = request,
                ECB = m_ToolOutputBarrier.CreateCommandBuffer()
            }.Schedule(result);

            m_ToolOutputBarrier.AddJobHandleForProducer(createDefinitionJobHandle);
            result = createDefinitionJobHandle;
            m_QueuedReplacementCandidates.Add(replacementCandidate);

            Mod.log.Info($"[IntersectionTool] Queued pocket lane replacement original={FormatEntity(splitCandidate.Edge)} pocket={FormatEntity(pocketEdge)} splitNode={FormatEntity(splitNode)} sourcePrefab={GetPrefabNameFromPrefab(splitCandidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(splitCandidate.TargetPrefab)} orientation={(splitCandidate.InvertTarget ? "reversed" : "direct")} lanes={splitCandidate.OriginalForwardLanes}/{splitCandidate.OriginalBackwardLanes}->{splitCandidate.TargetForwardLanes}/{splitCandidate.TargetBackwardLanes} splitNodeDistance={splitNodeDistance:0.##}m lengthError={lengthError:0.##}m reusedOriginal={(pocketEdge == splitCandidate.Edge ? "yes" : "no")}.");
            return true;
        }

        private bool TryFindPocketEdge(
            SplitCandidate candidate,
            out Entity pocketEdge,
            out Entity splitNode,
            out float splitNodeDistance,
            out float lengthError,
            bool allowOriginalEdgeAsPocket = false)
        {
            pocketEdge = Entity.Null;
            splitNode = Entity.Null;
            splitNodeDistance = 0f;
            lengthError = 0f;

            if (!EntityManager.TryGetBuffer(candidate.Node, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot find pocket edge for original={FormatEntity(candidate.Edge)}: node={FormatEntity(candidate.Node)} has no ConnectedEdge buffer.");
                return false;
            }

            float bestValidScore = float.MaxValue;
            float bestNodeDistance = float.MaxValue;
            float bestLengthError = float.MaxValue;
            Entity bestEdge = Entity.Null;
            Entity bestSplitNode = Entity.Null;
            float bestRejectedScore = float.MaxValue;
            float bestRejectedNodeDistance = float.MaxValue;
            float bestRejectedLengthError = float.MaxValue;
            Entity bestRejectedEdge = Entity.Null;
            int scannedCount = 0;
            int prefabMatchCount = 0;

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if ((edgeEntity == candidate.Edge && !allowOriginalEdgeAsPocket) ||
                    !IsRoadEdge(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Curve curve) ||
                    !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef))
                {
                    continue;
                }

                scannedCount++;
                if (prefabRef.m_Prefab != candidate.SourcePrefab &&
                    prefabRef.m_Prefab != candidate.TargetPrefab)
                {
                    continue;
                }

                prefabMatchCount++;
                Entity otherNode = edge.m_Start == candidate.Node ? edge.m_End : edge.m_End == candidate.Node ? edge.m_Start : Entity.Null;
                if (otherNode == Entity.Null ||
                    !EntityManager.TryGetComponent(otherNode, out Node otherNodeData))
                {
                    continue;
                }

                float candidateNodeDistance = math.distance(otherNodeData.m_Position.xz, candidate.HitPosition.xz);
                float candidateLengthError = math.abs(curve.m_Length - candidate.SplitDistance);
                float score = candidateNodeDistance + candidateLengthError * 0.25f;
                if (candidateNodeDistance > SplitNodePositionTolerance ||
                    candidateLengthError > PocketEdgeLengthTolerance)
                {
                    if (score < bestRejectedScore)
                    {
                        bestRejectedScore = score;
                        bestRejectedNodeDistance = candidateNodeDistance;
                        bestRejectedLengthError = candidateLengthError;
                        bestRejectedEdge = edgeEntity;
                    }

                    continue;
                }

                if (score < bestValidScore)
                {
                    bestValidScore = score;
                    bestNodeDistance = candidateNodeDistance;
                    bestLengthError = candidateLengthError;
                    bestEdge = edgeEntity;
                    bestSplitNode = otherNode;
                }
            }

            if (bestEdge == Entity.Null ||
                bestNodeDistance > SplitNodePositionTolerance ||
                bestLengthError > PocketEdgeLengthTolerance)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot find generated pocket edge original={FormatEntity(candidate.Edge)} sourcePrefab={GetPrefabNameFromPrefab(candidate.SourcePrefab)} targetPrefab={GetPrefabNameFromPrefab(candidate.TargetPrefab)} node={FormatEntity(candidate.Node)} expectedSplit=({candidate.HitPosition.x:0.##},{candidate.HitPosition.y:0.##},{candidate.HitPosition.z:0.##}) expectedDistance={candidate.SplitDistance:0.##}m scanned={scannedCount} sourceOrTargetPrefabMatches={prefabMatchCount} allowOriginal={allowOriginalEdgeAsPocket} bestRejectedEdge={FormatEntity(bestRejectedEdge)} bestRejectedNodeDistance={FormatMeters(bestRejectedNodeDistance)} bestRejectedLengthError={FormatMeters(bestRejectedLengthError)}.");
                return false;
            }

            pocketEdge = bestEdge;
            splitNode = bestSplitNode;
            splitNodeDistance = bestNodeDistance;
            lengthError = bestLengthError;
            return true;
        }

        private bool TryBuildReplacementDefinitionRequest(
            ReplacementCandidate candidate,
            out ReplacementDefinitionRequest request)
        {
            request = default;

            if (!EntityManager.Exists(candidate.PocketEdge) ||
                !EntityManager.TryGetComponent(candidate.PocketEdge, out Edge edge) ||
                !EntityManager.TryGetComponent(candidate.PocketEdge, out Curve curve))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot build replacement definition pocket={FormatEntity(candidate.PocketEdge)}: missing Edge or Curve.");
                return false;
            }

            if (EntityManager.HasComponent<Owner>(candidate.PocketEdge))
            {
                Mod.log.Warn($"[IntersectionTool] Skip pocket lane replacement pocket={FormatEntity(candidate.PocketEdge)} target={GetPrefabNameFromPrefab(candidate.TargetPrefab)}: owned sub-net edges are not replaced yet.");
                return false;
            }

            Entity startNode = edge.m_Start;
            Entity endNode = edge.m_End;
            Bezier4x3 bezier = curve.m_Bezier;
            CreationFlags flags = CreationFlags.Align | CreationFlags.SubElevation;
            if (candidate.InvertTarget)
            {
                Entity oldStart = startNode;
                startNode = endNode;
                endNode = oldStart;
                bezier = MathUtils.Invert(bezier);
                flags |= CreationFlags.Invert;
            }

            int randomSeed = EntityManager.TryGetComponent(candidate.PocketEdge, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : candidate.PocketEdge.Index;
            int fixedIndex = EntityManager.TryGetComponent(candidate.PocketEdge, out Fixed fixedData)
                ? fixedData.m_Index
                : -1;

            request = new ReplacementDefinitionRequest
            {
                OriginalEdge = candidate.PocketEdge,
                Prefab = candidate.TargetPrefab,
                Curve = bezier,
                Length = MathUtils.Length(bezier),
                StartNode = startNode,
                EndNode = endNode,
                Flags = flags,
                FixedIndex = fixedIndex,
                RandomSeed = randomSeed
            };
            return true;
        }

        private bool TryBuildPreviewSourceDefinitionRequest(
            Entity sourceEdge,
            Entity sourcePrefab,
            out ReplacementDefinitionRequest request)
        {
            request = default;

            if (!EntityManager.Exists(sourceEdge) ||
                !EntityManager.TryGetComponent(sourceEdge, out Edge edge) ||
                !EntityManager.TryGetComponent(sourceEdge, out Curve curve))
            {
                Mod.log.Warn($"[IntersectionTool] Cannot build source preview definition edge={FormatEntity(sourceEdge)}: missing Edge or Curve.");
                return false;
            }

            Entity prefab = sourcePrefab;
            if (prefab == Entity.Null &&
                EntityManager.TryGetComponent(sourceEdge, out PrefabRef prefabRef))
            {
                prefab = prefabRef.m_Prefab;
            }

            if (prefab == Entity.Null)
            {
                Mod.log.Warn($"[IntersectionTool] Cannot build source preview definition edge={FormatEntity(sourceEdge)}: missing source prefab.");
                return false;
            }

            int randomSeed = EntityManager.TryGetComponent(sourceEdge, out PseudoRandomSeed seed)
                ? seed.m_Seed
                : sourceEdge.Index;
            int fixedIndex = EntityManager.TryGetComponent(sourceEdge, out Fixed fixedData)
                ? fixedData.m_Index
                : -1;

            request = new ReplacementDefinitionRequest
            {
                OriginalEdge = sourceEdge,
                Prefab = prefab,
                Curve = curve.m_Bezier,
                Length = curve.m_Length > 0.01f ? curve.m_Length : MathUtils.Length(curve.m_Bezier),
                StartNode = edge.m_Start,
                EndNode = edge.m_End,
                Flags = CreationFlags.Recreate | CreationFlags.Align | CreationFlags.SubElevation,
                FixedIndex = fixedIndex,
                RandomSeed = randomSeed
            };
            return true;
        }

        private bool TryFindReplacementResultEdge(ReplacementCandidate candidate, out Entity resultEdge)
        {
            resultEdge = Entity.Null;
            if (candidate.Node == Entity.Null ||
                candidate.SplitNode == Entity.Null ||
                !EntityManager.Exists(candidate.Node) ||
                !EntityManager.Exists(candidate.SplitNode) ||
                !EntityManager.TryGetBuffer(candidate.Node, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                return false;
            }

            for (int i = 0; i < connectedEdges.Length; i++)
            {
                Entity edgeEntity = connectedEdges[i].m_Edge;
                if (edgeEntity == Entity.Null ||
                    !EntityManager.Exists(edgeEntity) ||
                    EntityManager.HasComponent<Deleted>(edgeEntity) ||
                    !EntityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                    !EntityManager.TryGetComponent(edgeEntity, out PrefabRef prefabRef) ||
                    prefabRef.m_Prefab != candidate.TargetPrefab)
                {
                    continue;
                }

                if ((edge.m_Start == candidate.Node && edge.m_End == candidate.SplitNode) ||
                    (edge.m_Start == candidate.SplitNode && edge.m_End == candidate.Node))
                {
                    resultEdge = edgeEntity;
                    return true;
                }
            }

            return false;
        }

        private void ToolChanged(ToolBaseSystem system)
        {
            if (system != this && IsToolEnabled)
            {
                UpdateHoveredIntersection(Entity.Null);
                SetToolEnabled(false);
                SetVanillaMutationSystemsEnabled(true);
                Mod.log.Info($"[IntersectionTool] Disabled because active tool changed to {system?.toolID ?? "<null>"}; restored vanilla mutation systems.");
            }
        }

        private void DisableActionTooltips()
        {
            if (m_DisplayOverridePropertyInfo == null)
            {
                return;
            }

            if (applyAction is Game.Input.UIInputAction.State applyActionState)
            {
                m_DisplayOverridePropertyInfo.SetValue(applyActionState.action, null);
            }

            if (secondaryApplyAction is Game.Input.UIInputAction.State secondaryApplyActionState)
            {
                m_DisplayOverridePropertyInfo.SetValue(secondaryApplyActionState.action, null);
            }
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();

            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground | CollisionMask.Underground;
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.All;
            m_ToolRaycastSystem.iconLayerMask = IconLayerMask.None;
            m_ToolRaycastSystem.utilityTypeMask = UtilityTypes.None;
            m_ToolRaycastSystem.raycastFlags = RaycastFlags.Markers |
                                               RaycastFlags.ElevateOffset |
                                               RaycastFlags.SubElements |
                                               RaycastFlags.Cargo |
                                               RaycastFlags.Passenger;
        }

        private enum ApproachMovement
        {
            Ambiguous,
            Straight,
            Left,
            Right
        }

        private readonly struct ApproachMovementKey : IEquatable<ApproachMovementKey>
        {
            public readonly ApproachMovement Movement;
            public readonly Entity TargetEdge;

            public ApproachMovementKey(ApproachMovement movement, Entity targetEdge)
            {
                Movement = movement;
                TargetEdge = targetEdge;
            }

            public bool IsTurn => Movement == ApproachMovement.Left || Movement == ApproachMovement.Right;

            public bool Equals(ApproachMovementKey other)
            {
                return Movement == other.Movement && TargetEdge == other.TargetEdge;
            }

            public override bool Equals(object obj)
            {
                return obj is ApproachMovementKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)Movement;
                    hash = (hash * 397) ^ TargetEdge.Index;
                    hash = (hash * 397) ^ TargetEdge.Version;
                    return hash;
                }
            }
        }

        private struct ApproachLaneUsage
        {
            public int LaneIndex;
            public int Straight;
            public int Left;
            public int Right;
            public int Ambiguous;

            public int KnownMovementCount
            {
                get
                {
                    int count = 0;
                    if (Straight > 0)
                    {
                        count++;
                    }

                    if (Left > 0)
                    {
                        count++;
                    }

                    if (Right > 0)
                    {
                        count++;
                    }

                    return count;
                }
            }

            public void Add(ApproachMovement movement)
            {
                switch (movement)
                {
                    case ApproachMovement.Straight:
                        Straight++;
                        break;
                    case ApproachMovement.Left:
                        Left++;
                        break;
                    case ApproachMovement.Right:
                        Right++;
                        break;
                    default:
                        Ambiguous++;
                        break;
                }
            }

            public bool IsDedicated(ApproachMovement movement)
            {
                if (Ambiguous > 0 || KnownMovementCount != 1)
                {
                    return false;
                }

                switch (movement)
                {
                    case ApproachMovement.Left:
                        return Left > 0;
                    case ApproachMovement.Right:
                        return Right > 0;
                    case ApproachMovement.Straight:
                        return Straight > 0;
                    default:
                        return false;
                }
            }
        }

        private struct SplitDefinitionRequest
        {
            public Entity Edge;
            public Entity Prefab;
            public float3 HitPosition;
            public float CurvePosition;
            public int RandomSeed;
        }

        private struct SplitCandidate
        {
            public Entity Node;
            public Entity Edge;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public bool InvertTarget;
            public float CurvePosition;
            public float3 HitPosition;
            public float TargetDistance;
            public float SplitDistance;
            public float IntersectionDistance;
            public float PocketDistance;
            public int OriginalForwardLanes;
            public int OriginalBackwardLanes;
            public int TargetForwardLanes;
            public int TargetBackwardLanes;
            public int Attempt;
        }

        private struct ReplacementDefinitionRequest
        {
            public Entity OriginalEdge;
            public Entity Prefab;
            public Entity StartNode;
            public Entity EndNode;
            public Bezier4x3 Curve;
            public float Length;
            public CreationFlags Flags;
            public int FixedIndex;
            public int RandomSeed;
            public bool PreviewOnly;
        }

        private struct ReplacementPreviewDefinition : IComponentData
        {
        }

        private struct RoadLaneCounts
        {
            public int Forward;
            public int Backward;

            public int Total => Forward + Backward;

            public override string ToString()
            {
                return $"{Forward}/{Backward}";
            }
        }

        private struct ReplacementPrefabMatch
        {
            public Entity Prefab;
            public bool Invert;
            public RoadLaneCounts OriginalCounts;
            public RoadLaneCounts TargetCounts;
            public RoadLaneCounts CandidateCounts;
            public int Score;
        }

        private struct ReplacementCandidate
        {
            public Entity Node;
            public Entity SplitNode;
            public Entity OriginalEdge;
            public Entity PocketEdge;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public bool InvertTarget;
            public float3 HitPosition;
            public int OriginalForwardLanes;
            public int OriginalBackwardLanes;
            public int TargetForwardLanes;
            public int TargetBackwardLanes;
        }

        private struct CreateSplitDefinitionJob : IJob
        {
            [ReadOnly]
            public SplitDefinitionRequest Request;

            public EntityCommandBuffer ECB;

            public void Execute()
            {
                if (Request.Prefab == Entity.Null)
                {
                    return;
                }

                Entity definitionEntity = ECB.CreateEntity();
                ECB.AddComponent(definitionEntity, new CreationDefinition
                {
                    m_Original = Entity.Null,
                    m_Prefab = Request.Prefab,
                    m_Flags = CreationFlags.Construction,
                    m_RandomSeed = Request.RandomSeed
                });
                ECB.AddComponent<Updated>(definitionEntity);
                ECB.AddComponent(definitionEntity, new NetCourse
                {
                    m_Curve = new Bezier4x3(Request.HitPosition, Request.HitPosition, Request.HitPosition, Request.HitPosition),
                    m_Length = 0f,
                    m_FixedIndex = -1,
                    m_Elevation = default,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = Request.Edge,
                        m_Position = Request.HitPosition,
                        m_Rotation = default,
                        m_CourseDelta = 0f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsRight | CoursePosFlags.IsLeft,
                        m_ParentMesh = -1,
                        m_SplitPosition = Request.CurvePosition
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = Request.Edge,
                        m_Position = Request.HitPosition,
                        m_Rotation = default,
                        m_CourseDelta = 1f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLast | CoursePosFlags.IsRight | CoursePosFlags.IsLeft,
                        m_ParentMesh = -1,
                        m_SplitPosition = Request.CurvePosition
                    }
                });
            }
        }

        private struct CreateReplacementDefinitionJob : IJob
        {
            [ReadOnly]
            public ReplacementDefinitionRequest Request;

            public EntityCommandBuffer ECB;

            public void Execute()
            {
                if (Request.Prefab == Entity.Null ||
                    Request.OriginalEdge == Entity.Null ||
                    Request.StartNode == Entity.Null ||
                    Request.EndNode == Entity.Null)
                {
                    return;
                }

                Entity definitionEntity = ECB.CreateEntity();
                ECB.AddComponent(definitionEntity, new CreationDefinition
                {
                    m_Original = Request.OriginalEdge,
                    m_Prefab = Request.Prefab,
                    m_Flags = Request.Flags,
                    m_RandomSeed = Request.RandomSeed
                });
                if (Request.PreviewOnly)
                {
                    ECB.AddComponent<ReplacementPreviewDefinition>(definitionEntity);
                }

                ECB.AddComponent<Updated>(definitionEntity);
                ECB.AddComponent(definitionEntity, new NetCourse
                {
                    m_Curve = Request.Curve,
                    m_Length = Request.Length,
                    m_FixedIndex = Request.FixedIndex,
                    m_Elevation = default,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = Request.StartNode,
                        m_Position = Request.Curve.a,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(Request.Curve)),
                        m_CourseDelta = 0f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsFirst,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = Request.EndNode,
                        m_Position = Request.Curve.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(Request.Curve)),
                        m_CourseDelta = 1f,
                        m_Elevation = default,
                        m_Flags = CoursePosFlags.IsLast,
                        m_ParentMesh = -1,
                        m_SplitPosition = 0f
                    }
                });
            }
        }

    }
}
