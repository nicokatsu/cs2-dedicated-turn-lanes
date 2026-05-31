using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using PocketTurnLanes.Systems.Overlay;
using Unity.Entities;
using Unity.Jobs;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class IntersectionToolSystem : ToolBaseSystem
    {
        private const float SplitGridSize = 8f;
        private const float PocketLengthGridSize = 8f;
        private const float FallbackPocketLaneLength = 24f;
        private const float MinimumWidthBasedPocketLaneLength = 8f;
        private const float MaximumWidthBasedPocketLaneLength = 32f;
        private const float MaximumRetryPocketLaneLength = 64f;
        private const float DrivableLaneEnvelopeBuffer = 8f;
        private const float SplitGridAlignmentTolerance = 0.05f;
        private const float IntersectionExitBuffer = 0f;
        private const float MinimumPocketLaneLength = 8f;
        private const float MinimumPocketLaneLengthTolerance = 0.05f;
        private const float MinimumIntersectionSin = 0.2f;
        private const float MaxIntersectionExitDistance = 40f;
        private const float SplitLengthBuffer = 0.16f;
        private const float MaxNodePickDistance = 16f;
        private const float SplitRetryStep = PocketLengthGridSize;
        private const int MaxSplitRetryAttempts = 8;
        private const float MinimumRetryProgress = 2f;
        private const float PreviewSplitNodeTolerance = 0.004f;
        private const float BalancedRetryPreviewSplitNodePositionTolerance = 1f;
        private const int BalancedRetryMinimumApplyDelayFrames = 2;
        private const int MaxReplacementPreviewWaitFrames = 6;
        private const float PrefabWidthTolerance = 0.05f;
        private const float SplitNodePositionTolerance = 2.5f;
        private const float PocketEdgeLengthTolerance = 4f;
        private const float MergedEdgeLengthTolerance = 12f;
        private const float NodeMergePreviewEdgeExitDistance = 6f;
        private const float NodeMergePreviewIntersectionBoundsMargin = 0.75f;
        private const float MinimumMarkedParkingSlotAngleDegrees = 15f;
        private const int DlcSourceNonDlcCandidatePenalty = 5000;
        private const int TramUpgradeFallbackPenalty = 20000;
        private const int IndependentTramTargetPreference = 1000;
        private const int PublicTransportTramTargetPenalty = 500;
        private const int OtherTramTargetPenalty = 1000;
        private const int MissingTramTargetPenalty = 50000;
        private const int PublicTransportTramUpgradeLaneTypePenalty = 50;
        private const int OtherTramUpgradeLaneTypePenalty = 100;
        private const float PublicTransportLayoutOffsetScoreScale = 100f;
        private const int PublicTransportLayoutMissingDirectionPenalty = 2500;
        private const int PublicTransportLayoutCountMismatchPenalty = 750;

        public override string toolID => $"{Mod.ModId} Intersection Tool";
        public override bool allowUnderground => true;

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
        private bool m_VerifyAppliedNodeMerges;
        private bool m_HasReplacementPreviewDefinitions;
        private bool m_HasShortEdgeReplacementPreviewDefinitions;
        private bool m_NormalReplacementPreviewDefinitionsQueued;
        private bool m_ShortEdgeReplacementPreviewAttempted;
        private bool m_NodeMergeDefinitionsReadyForApply;
        private int m_PreviewCreatedFrame = -1;
        private int m_PreviewEdgeCount;
        private int m_ShortEdgeReplacementPreviewQueuedCount;
        private readonly List<SplitCandidate> m_PreviewCandidates = new List<SplitCandidate>();
        private readonly List<SplitCandidate> m_NextPreviewCandidates = new List<SplitCandidate>();
        private readonly List<SplitCandidate> m_AppliedCandidates = new List<SplitCandidate>();
        private readonly List<NodeMergeCandidate> m_PreviewNodeMergeCandidates = new List<NodeMergeCandidate>();
        private readonly List<NodeMergeCandidate> m_AppliedNodeMergeCandidates = new List<NodeMergeCandidate>();
        private readonly List<ReplacementCandidate> m_QueuedReplacementCandidates = new List<ReplacementCandidate>();
        private readonly List<ReplacementCandidate> m_AppliedReplacementCandidates = new List<ReplacementCandidate>();
        private readonly List<ReplacementCandidate> m_PendingLaneRepairCandidates = new List<ReplacementCandidate>();

        public event Action<bool> ToolEnabledChanged;

        public bool IsToolEnabled { get; private set; }
        public bool Underground { get; set; }

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
                ComponentType.Exclude<BridgeData>(),
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

                if (TryQueueAppliedNodeMergeSplits(ref result))
                {
                    return result;
                }

                QueuePendingSplitLaneConnectionFixes("all apply phases are complete");
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
            if (SynchronizeUndergroundMode(ref result))
            {
                return result;
            }

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

                bool hasBalancedRetrySplit = HasBalancedRetrySplitCandidate();
                if (hasBalancedRetrySplit &&
                    !AreBalancedRetrySplitNodesReady(out string balancedRetryDetail))
                {
                    int waitedFrames = Math.Max(0, UnityEngine.Time.frameCount - m_PreviewCreatedFrame);
                    if (waitedFrames <= MaxReplacementPreviewWaitFrames)
                    {
                        Mod.log.Info($"[IntersectionTool] Waiting for balanced road-node retry split preview before apply waitedFrames={waitedFrames}/{MaxReplacementPreviewWaitFrames} detail={balancedRetryDetail}.");
                        return result;
                    }

                    result = ClearPreviewDefinitions(result, $"balanced road-node retry split preview did not materialize before apply; waitedFrames={waitedFrames} detail={balancedRetryDetail}");
                    return result;
                }
                if (hasBalancedRetrySplit)
                {
                    int waitedFrames = Math.Max(0, UnityEngine.Time.frameCount - m_PreviewCreatedFrame);
                    if (waitedFrames < BalancedRetryMinimumApplyDelayFrames)
                    {
                        Mod.log.Info($"[IntersectionTool] Balanced road-node retry split preview is ready but waiting one more frame before apply waitedFrames={waitedFrames}/{BalancedRetryMinimumApplyDelayFrames}.");
                        return result;
                    }

                    AreBalancedRetrySplitNodesReady(out string balancedRetryReadyDetail);
                    Mod.log.Info($"[IntersectionTool] Balanced road-node retry split preview is ready for apply detail={balancedRetryReadyDetail}.");
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
                int replacementPreviewDefinitionCount = m_ReplacementPreviewDefinitionQuery.CalculateEntityCount();
                bool hasReplacementPreviewDefinitions =
                    m_HasReplacementPreviewDefinitions &&
                    replacementPreviewDefinitionCount > 0;
                bool needsNodeMergeApplyDefinitions =
                    m_PreviewNodeMergeCandidates.Count > 0 &&
                    !m_NodeMergeDefinitionsReadyForApply;
                if (hasReplacementPreviewDefinitions || needsNodeMergeApplyDefinitions)
                {
                    applyMode = ApplyMode.Clear;
                    result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, result);
                    m_HasReplacementPreviewDefinitions = false;
                    m_HasShortEdgeReplacementPreviewDefinitions = false;
                    m_NormalReplacementPreviewDefinitionsQueued = false;
                    m_ShortEdgeReplacementPreviewAttempted = false;
                    m_ShortEdgeReplacementPreviewQueuedCount = 0;
                    m_RebuildSplitPreviewForApply = true;
                    m_ApplyPreviewNextFrame = true;
                    m_PreviewCreatedFrame = UnityEngine.Time.frameCount;
                    Mod.log.Info($"[IntersectionTool] Preparing clean apply definitions node={FormatEntity(m_PreviewIntersection)} edges={m_PreviewEdgeCount} replacementPreviewDefinitions={hasReplacementPreviewDefinitions} replacementPreviewDefinitionEntities={replacementPreviewDefinitionCount} roadNodeMergeCandidates={m_PreviewNodeMergeCandidates.Count}; fresh split/merge definitions will be rebuilt before apply after preview definitions are cleared.");
                    return result;
                }

                if (UnityEngine.Time.frameCount <= m_PreviewCreatedFrame)
                {
                    m_ApplyPreviewNextFrame = true;
                    return result;
                }

                CaptureAppliedCandidates();
                CaptureAppliedReplacementCandidates();
                CaptureAppliedNodeMergeCandidates();
                applyMode = ApplyMode.Apply;
                m_ClearSplitDefinitions = true;
                m_ApplyPreviewNextFrame = false;
                Mod.log.Info($"[IntersectionTool] Applying prepared split preview node={FormatEntity(m_PreviewIntersection)} edges={m_PreviewEdgeCount} lastEdge={FormatEntity(m_PreviewEdge)} replacementDefinitions={m_AppliedReplacementCandidates.Count}.");
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

        public override void SetUnderground(bool isUnderground)
        {
            if (Underground == isUnderground)
            {
                return;
            }

            Underground = isUnderground;
            Mod.log.Info($"[IntersectionTool] Underground mode requested underground={Underground} active={IsToolEnabled} previousRequireUnderground={requireUnderground} collisionMask={GetCurrentCollisionMask()}.");
        }

        public override void ElevationUp()
        {
            SetUnderground(false);
        }

        public override void ElevationDown()
        {
            SetUnderground(true);
        }

        public override void ElevationScroll()
        {
            SetUnderground(!Underground);
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

            m_ToolRaycastSystem.collisionMask = GetCurrentCollisionMask();
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

        private CollisionMask GetCurrentCollisionMask()
        {
            return Underground
                ? CollisionMask.Underground
                : CollisionMask.OnGround | CollisionMask.Overground;
        }

        private bool SynchronizeUndergroundMode(ref JobHandle result)
        {
            bool previousRequireUnderground = requireUnderground;
            requireUnderground = Underground;
            if (previousRequireUnderground == Underground)
            {
                return false;
            }

            Entity previousHover = m_HoveredIntersection;
            Entity previousPreview = m_PreviewIntersection;
            int previousPreviewEdges = m_PreviewEdgeCount;
            bool hadPreviewState = HasPreviewState();

            UpdateHoveredIntersection(Entity.Null);
            ResetPreviewState();
            applyMode = ApplyMode.Clear;
            result = DestroyDefinitions(m_DefinitionQuery, m_ToolOutputBarrier, result);

            Mod.log.Info($"[IntersectionTool] Underground mode synchronized underground={Underground} previousRequireUnderground={previousRequireUnderground} requireUnderground={requireUnderground} collisionMask={GetCurrentCollisionMask()} clearedPreview={hadPreviewState} previousHover={FormatEntity(previousHover)} previousPreview={FormatEntity(previousPreview)} previousPreviewEdges={previousPreviewEdges}.");
            return true;
        }

    }
}
