using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal sealed class RoadLaneProfileBuilder
    {
        private const float MinimumMarkedParkingSlotAngleDegrees = 15f;
        private const float CenterLaneOffsetTolerance = 0.05f;

        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;
        private readonly Func<BufferLookup<NetSubSection>> m_GetNetSubSectionLookup;
        private readonly Func<BufferLookup<NetSectionPiece>> m_GetNetSectionPieceLookup;
        private readonly Func<ComponentLookup<NetLaneData>> m_GetNetLaneDataLookup;
        private readonly Func<BufferLookup<NetPieceLane>> m_GetNetPieceLaneLookup;
        private readonly RoadBuilderPrefabSemantics m_RoadBuilderPrefabSemantics;

        internal RoadLaneProfileBuilder(
            EntityManager entityManager,
            PrefabSystem prefabSystem,
            Func<BufferLookup<NetSubSection>> getNetSubSectionLookup,
            Func<BufferLookup<NetSectionPiece>> getNetSectionPieceLookup,
            Func<ComponentLookup<NetLaneData>> getNetLaneDataLookup,
            Func<BufferLookup<NetPieceLane>> getNetPieceLaneLookup,
            RoadBuilderPrefabSemantics roadBuilderPrefabSemantics)
        {
            m_EntityManager = entityManager;
            m_PrefabSystem = prefabSystem;
            m_GetNetSubSectionLookup = getNetSubSectionLookup;
            m_GetNetSectionPieceLookup = getNetSectionPieceLookup;
            m_GetNetLaneDataLookup = getNetLaneDataLookup;
            m_GetNetPieceLaneLookup = getNetPieceLaneLookup;
            m_RoadBuilderPrefabSemantics = roadBuilderPrefabSemantics;
        }

        private EntityManager EntityManager => m_EntityManager;

        internal bool TryGetRoadLaneProfile(
            Entity edgeEntity,
            Entity fallbackPrefab,
            out RoadLaneProfile profile)
        {
            if (EntityManager.TryGetComponent(edgeEntity, out Composition composition) &&
                TryGetCompositionRoadLaneProfile(
                    composition.m_Edge,
                    out profile))
            {
                profile.Source = $"Composition:{FormatEntity(composition.m_Edge)}";
                m_RoadBuilderPrefabSemantics.ApplyConfigSemantics(fallbackPrefab, ref profile);
                return true;
            }

            if (TryGetDefaultRoadLaneProfile(
                    fallbackPrefab,
                    out profile))
            {
                return true;
            }

            profile = RoadLaneProfile.CreateEmpty("Missing");
            return false;
        }

        internal bool TryGetCompositionRoadLaneProfile(
            Entity compositionEntity,
            out RoadLaneProfile profile)
        {
            profile = RoadLaneProfile.CreateEmpty("Composition");
            if (compositionEntity == Entity.Null ||
                !EntityManager.TryGetBuffer(compositionEntity, true, out DynamicBuffer<NetCompositionLane> lanes))
            {
                return false;
            }

            AccumulateCompositionLanes(lanes, ref profile);

            return profile.RoadCounts.Total > 0;
        }

        internal bool TryGetDefaultRoadLaneProfile(
            Entity prefabEntity,
            out RoadLaneProfile profile)
        {
            profile = RoadLaneProfile.CreateEmpty("Missing");

            if (prefabEntity == Entity.Null)
            {
                return false;
            }

            if (EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<DefaultNetLane> lanes))
            {
                AccumulateDefaultNetLanes(lanes, ref profile);

                if (profile.RoadCounts.Total > 0)
                {
                    profile.Source = "DefaultNetLane";
                    m_RoadBuilderPrefabSemantics.ApplyConfigSemantics(prefabEntity, ref profile);
                    return true;
                }
            }

            if (TryGetCompositionLaneProfile(prefabEntity, default, out profile))
            {
                profile.Source = "NetGeometryComposition:default";
                m_RoadBuilderPrefabSemantics.ApplyConfigSemantics(prefabEntity, ref profile);
                return true;
            }

            if (TryCalculateDefaultRoadLaneProfile(prefabEntity, out profile))
            {
                profile.Source = "NetGeometrySection:calculated";
                m_RoadBuilderPrefabSemantics.ApplyConfigSemantics(prefabEntity, ref profile);
                return true;
            }

            profile = RoadLaneProfile.CreateEmpty("Missing");
            return false;
        }

        private bool TryGetCompositionLaneProfile(
            Entity prefabEntity,
            CompositionFlags mask,
            out RoadLaneProfile profile)
        {
            profile = RoadLaneProfile.CreateEmpty("NetGeometryComposition");

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

                AccumulateCompositionLanes(lanes, ref profile);

                return profile.RoadCounts.Total > 0;
            }

            return false;
        }

        private bool TryCalculateDefaultRoadLaneProfile(
            Entity prefabEntity,
            out RoadLaneProfile profile)
        {
            return TryCalculateRoadLaneProfile(
                prefabEntity,
                default,
                "NetGeometrySection:calculated",
                out profile);
        }

        internal bool TryCalculateRoadLaneProfile(
            Entity prefabEntity,
            CompositionFlags compositionFlags,
            string source,
            out RoadLaneProfile profile)
        {
            profile = RoadLaneProfile.CreateEmpty(source);

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
                    compositionFlags,
                    m_GetNetSubSectionLookup(),
                    m_GetNetSectionPieceLookup());
                NetCompositionHelpers.AddCompositionLanes(
                    Entity.Null,
                    ref compositionData,
                    pieces,
                    lanes,
                    default,
                    m_GetNetLaneDataLookup(),
                    m_GetNetPieceLaneLookup());

                AccumulateCompositionLanes(lanes, ref profile);

                profile.Source = source;
                return profile.RoadCounts.Total > 0;
            }
            catch (Exception ex)
            {
                Mod.LogException(ex, $"[IntersectionTool] Failed to calculate road lanes for prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} entity={FormatEntity(prefabEntity)} compositionFlags={compositionFlags} source={source}.");
                profile = RoadLaneProfile.CreateEmpty(source);
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

        internal bool TryGetPrefabCompositionOptionSideFlags(
            Entity prefabEntity,
            out CompositionFlags.Side tramTrackSideFlags,
            out CompositionFlags.Side publicTransportLaneSideFlags,
            out string detail)
        {
            if (!TryGetPrefabCompositionOptionSideFlags(
                    prefabEntity,
                    out CompositionFlags.Side tramTrackLeftSideFlags,
                    out CompositionFlags.Side tramTrackRightSideFlags,
                    out CompositionFlags.Side publicTransportLaneLeftSideFlags,
                    out CompositionFlags.Side publicTransportLaneRightSideFlags,
                    out detail))
            {
                tramTrackSideFlags = default;
                publicTransportLaneSideFlags = default;
                return false;
            }

            tramTrackSideFlags = tramTrackLeftSideFlags | tramTrackRightSideFlags;
            publicTransportLaneSideFlags = publicTransportLaneLeftSideFlags | publicTransportLaneRightSideFlags;
            return true;
        }

        internal bool TryGetPrefabCompositionOptionSideFlags(
            Entity prefabEntity,
            out CompositionFlags.Side tramTrackLeftSideFlags,
            out CompositionFlags.Side tramTrackRightSideFlags,
            out CompositionFlags.Side publicTransportLaneLeftSideFlags,
            out CompositionFlags.Side publicTransportLaneRightSideFlags,
            out string detail)
        {
            tramTrackLeftSideFlags = default;
            tramTrackRightSideFlags = default;
            publicTransportLaneLeftSideFlags = default;
            publicTransportLaneRightSideFlags = default;
            detail = "sectionOptionFlags=missing";

            if (!EntityManager.TryGetBuffer(prefabEntity, true, out DynamicBuffer<NetGeometrySection> sections))
            {
                detail = "sectionOptionFlags=missing-sections";
                return false;
            }

            int topLevelSections = sections.Length;
            int scannedSections = 0;
            int scannedSubSections = 0;
            int scannedPieces = 0;
            HashSet<Entity> visitedSections = new HashSet<Entity>();
            BufferLookup<NetSubSection> subSectionLookup = m_GetNetSubSectionLookup();
            BufferLookup<NetSectionPiece> sectionPieceLookup = m_GetNetSectionPieceLookup();

            try
            {
                for (int i = 0; i < sections.Length; i++)
                {
                    NetGeometrySection section = sections[i];
                    AccumulateCompositionOptionSideFlags(
                        section.m_CompositionAll | section.m_CompositionAny,
                        ref tramTrackLeftSideFlags,
                        ref tramTrackRightSideFlags,
                        ref publicTransportLaneLeftSideFlags,
                        ref publicTransportLaneRightSideFlags);
                    ScanSectionDefinitionOptionSideFlags(
                        section.m_Section,
                        subSectionLookup,
                        sectionPieceLookup,
                        visitedSections,
                        ref tramTrackLeftSideFlags,
                        ref tramTrackRightSideFlags,
                        ref publicTransportLaneLeftSideFlags,
                        ref publicTransportLaneRightSideFlags,
                        ref scannedSections,
                        ref scannedSubSections,
                        ref scannedPieces);
                }
            }
            catch (Exception ex)
            {
                Mod.LogException(ex, $"[IntersectionTool] Failed to scan composition option flags for prefab={PrefabDiagnosticFormat.GetPrefabName(m_PrefabSystem, prefabEntity)} entity={FormatEntity(prefabEntity)}.");
                detail = $"sectionOptionFlags=exception topLevelSections={topLevelSections} scannedSections={scannedSections} scannedSubSections={scannedSubSections} scannedPieces={scannedPieces}";
                return false;
            }

            detail = $"sectionOptionFlags=ok topLevelSections={topLevelSections} scannedSections={scannedSections} scannedSubSections={scannedSubSections} scannedPieces={scannedPieces} tramTrackSideFlags={FormatSideFlags(tramTrackLeftSideFlags | tramTrackRightSideFlags)} tramTrackLeftSideFlags={FormatSideFlags(tramTrackLeftSideFlags)} tramTrackRightSideFlags={FormatSideFlags(tramTrackRightSideFlags)} publicTransportLaneSideFlags={FormatSideFlags(publicTransportLaneLeftSideFlags | publicTransportLaneRightSideFlags)} publicTransportLaneLeftSideFlags={FormatSideFlags(publicTransportLaneLeftSideFlags)} publicTransportLaneRightSideFlags={FormatSideFlags(publicTransportLaneRightSideFlags)}";
            return true;
        }

        private static void ScanSectionDefinitionOptionSideFlags(
            Entity sectionEntity,
            BufferLookup<NetSubSection> subSectionLookup,
            BufferLookup<NetSectionPiece> sectionPieceLookup,
            HashSet<Entity> visitedSections,
            ref CompositionFlags.Side tramTrackLeftSideFlags,
            ref CompositionFlags.Side tramTrackRightSideFlags,
            ref CompositionFlags.Side publicTransportLaneLeftSideFlags,
            ref CompositionFlags.Side publicTransportLaneRightSideFlags,
            ref int scannedSections,
            ref int scannedSubSections,
            ref int scannedPieces)
        {
            if (sectionEntity == Entity.Null ||
                !visitedSections.Add(sectionEntity))
            {
                return;
            }

            scannedSections++;
            if (subSectionLookup.HasBuffer(sectionEntity))
            {
                DynamicBuffer<NetSubSection> subSections = subSectionLookup[sectionEntity];
                for (int i = 0; i < subSections.Length; i++)
                {
                    scannedSubSections++;
                    NetSubSection subSection = subSections[i];
                    AccumulateCompositionOptionSideFlags(
                        subSection.m_CompositionAll | subSection.m_CompositionAny,
                        ref tramTrackLeftSideFlags,
                        ref tramTrackRightSideFlags,
                        ref publicTransportLaneLeftSideFlags,
                        ref publicTransportLaneRightSideFlags);
                    ScanSectionDefinitionOptionSideFlags(
                        subSection.m_SubSection,
                        subSectionLookup,
                        sectionPieceLookup,
                        visitedSections,
                        ref tramTrackLeftSideFlags,
                        ref tramTrackRightSideFlags,
                        ref publicTransportLaneLeftSideFlags,
                        ref publicTransportLaneRightSideFlags,
                        ref scannedSections,
                        ref scannedSubSections,
                        ref scannedPieces);
                }
            }

            if (!sectionPieceLookup.HasBuffer(sectionEntity))
            {
                return;
            }

            DynamicBuffer<NetSectionPiece> pieces = sectionPieceLookup[sectionEntity];
            for (int i = 0; i < pieces.Length; i++)
            {
                scannedPieces++;
                NetSectionPiece piece = pieces[i];
                AccumulateCompositionOptionSideFlags(
                    piece.m_CompositionAll | piece.m_CompositionAny,
                    ref tramTrackLeftSideFlags,
                    ref tramTrackRightSideFlags,
                    ref publicTransportLaneLeftSideFlags,
                    ref publicTransportLaneRightSideFlags);
            }
        }

        private static void AccumulateCompositionOptionSideFlags(
            CompositionFlags flags,
            ref CompositionFlags.Side tramTrackLeftSideFlags,
            ref CompositionFlags.Side tramTrackRightSideFlags,
            ref CompositionFlags.Side publicTransportLaneLeftSideFlags,
            ref CompositionFlags.Side publicTransportLaneRightSideFlags)
        {
            tramTrackLeftSideFlags |= flags.m_Left & GetTramTrackSideFlags();
            tramTrackRightSideFlags |= flags.m_Right & GetTramTrackSideFlags();
            publicTransportLaneLeftSideFlags |= flags.m_Left & GetPublicTransportLaneSideFlags();
            publicTransportLaneRightSideFlags |= flags.m_Right & GetPublicTransportLaneSideFlags();
        }

        private static CompositionFlags.Side GetTramTrackSideFlags()
        {
            return CompositionFlags.Side.PrimaryTrack |
                   CompositionFlags.Side.SecondaryTrack |
                   CompositionFlags.Side.TertiaryTrack |
                   CompositionFlags.Side.QuaternaryTrack;
        }

        private static CompositionFlags.Side GetPublicTransportLaneSideFlags()
        {
            return CompositionFlags.Side.PrimaryLane |
                   CompositionFlags.Side.SecondaryLane |
                   CompositionFlags.Side.TertiaryLane |
                   CompositionFlags.Side.QuaternaryLane;
        }

        private static string FormatSideFlags(CompositionFlags.Side flags)
        {
            return flags == default(CompositionFlags.Side)
                ? "none"
                : flags.ToString();
        }

        private void AccumulateDefaultNetLanes(
            DynamicBuffer<DefaultNetLane> lanes,
            ref RoadLaneProfile profile)
        {
            List<LaneProfileObservation> observations = new List<LaneProfileObservation>(lanes.Length);
            for (int i = 0; i < lanes.Length; i++)
            {
                observations.Add(CreateLaneObservation(
                    lanes[i].m_Flags,
                    lanes[i].m_Lane,
                    lanes[i].m_Position.x));
            }

            AccumulateLaneObservations(observations, ref profile);
        }

        private void AccumulateCompositionLanes(
            DynamicBuffer<NetCompositionLane> lanes,
            ref RoadLaneProfile profile)
        {
            List<LaneProfileObservation> observations = new List<LaneProfileObservation>(lanes.Length);
            for (int i = 0; i < lanes.Length; i++)
            {
                observations.Add(CreateLaneObservation(
                    lanes[i].m_Flags,
                    lanes[i].m_Lane,
                    lanes[i].m_Position.x));
            }

            AccumulateLaneObservations(observations, ref profile);
        }

        private void AccumulateCompositionLanes(
            NativeList<NetCompositionLane> lanes,
            ref RoadLaneProfile profile)
        {
            List<LaneProfileObservation> observations = new List<LaneProfileObservation>(lanes.Length);
            for (int i = 0; i < lanes.Length; i++)
            {
                observations.Add(CreateLaneObservation(
                    lanes[i].m_Flags,
                    lanes[i].m_Lane,
                    lanes[i].m_Position.x));
            }

            AccumulateLaneObservations(observations, ref profile);
        }

        private LaneProfileObservation CreateLaneObservation(
            LaneFlags flags,
            Entity lanePrefab,
            float lateralOffset)
        {
            LaneFlags effectiveFlags = GetEffectiveLaneFlags(flags, lanePrefab);
            return new LaneProfileObservation
            {
                EffectiveFlags = effectiveFlags,
                LanePrefab = lanePrefab,
                LateralOffset = lateralOffset,
                FlagMergeDetail = FormatEffectiveLaneFlags(flags, effectiveFlags)
            };
        }

        private void AccumulateLaneObservations(
            List<LaneProfileObservation> observations,
            ref RoadLaneProfile profile)
        {
            ApplySeparatedCenterTramHeuristic(observations);
            for (int i = 0; i < observations.Count; i++)
            {
                AccumulateLaneProfile(observations[i], ref profile);
            }
        }

        private void AccumulateLaneProfile(
            LaneProfileObservation observation,
            ref RoadLaneProfile profile)
        {
            AccumulateRoadAndEnvelope(
                observation.EffectiveFlags,
                observation.LanePrefab,
                observation.LateralOffset,
                observation.FlagMergeDetail,
                observation.ForceIndependentTram,
                ref profile);
            TryRecordMarkedParking(
                observation.EffectiveFlags,
                observation.LanePrefab,
                observation.FlagMergeDetail,
                ref profile);
            AccumulateTramSemantics(
                observation.EffectiveFlags,
                observation.LanePrefab,
                observation.LateralOffset,
                observation.FlagMergeDetail,
                observation.ForceIndependentTram,
                ref profile);
            AccumulateBusSemantics(
                observation.EffectiveFlags,
                observation.LanePrefab,
                observation.LateralOffset,
                observation.FlagMergeDetail,
                observation.ForceIndependentTram,
                ref profile);
        }

        private void ApplySeparatedCenterTramHeuristic(List<LaneProfileObservation> observations)
        {
            if (observations == null || observations.Count == 0)
            {
                return;
            }

            RoadLaneCounts roadCounts = default;
            List<int> forwardCenterTramCandidates = new List<int>(1);
            List<int> backwardCenterTramCandidates = new List<int>(1);
            for (int i = 0; i < observations.Count; i++)
            {
                LaneProfileObservation observation = observations[i];
                RoadLaneCountMatcher.CountRoadLane(observation.EffectiveFlags, ref roadCounts);
                if (!IsSeparatedCenterTramCandidate(observation, out bool isForward))
                {
                    continue;
                }

                if (isForward)
                {
                    forwardCenterTramCandidates.Add(i);
                }
                else
                {
                    backwardCenterTramCandidates.Add(i);
                }
            }

            if (roadCounts.Forward != roadCounts.Backward ||
                roadCounts.Forward < 2 ||
                forwardCenterTramCandidates.Count != 1 ||
                backwardCenterTramCandidates.Count != 1)
            {
                return;
            }

            int forwardCandidate = forwardCenterTramCandidates[0];
            int backwardCandidate = backwardCenterTramCandidates[0];
            if (!TryGetUniqueInnermostRoadLane(observations, true, out int forwardInnermost) ||
                !TryGetUniqueInnermostRoadLane(observations, false, out int backwardInnermost) ||
                forwardInnermost != forwardCandidate ||
                backwardInnermost != backwardCandidate ||
                !IsCenteredPair(observations[forwardCandidate].LateralOffset, observations[backwardCandidate].LateralOffset))
            {
                return;
            }

            LaneProfileObservation forwardObservation = observations[forwardCandidate];
            forwardObservation.ForceIndependentTram = true;
            observations[forwardCandidate] = forwardObservation;

            LaneProfileObservation backwardObservation = observations[backwardCandidate];
            backwardObservation.ForceIndependentTram = true;
            observations[backwardCandidate] = backwardObservation;
        }

        private bool IsSeparatedCenterTramCandidate(
            LaneProfileObservation observation,
            out bool isForward)
        {
            isForward = false;
            LaneFlags flags = observation.EffectiveFlags;
            if ((flags & (LaneFlags.Master | LaneFlags.Road | LaneFlags.Track)) !=
                (LaneFlags.Road | LaneFlags.Track))
            {
                return false;
            }

            const LaneFlags excluded =
                LaneFlags.Twoway |
                LaneFlags.BicyclesOnly |
                LaneFlags.PublicOnly |
                LaneFlags.Parking |
                LaneFlags.Pedestrian |
                LaneFlags.Utility;
            if ((flags & excluded) != 0 ||
                !IsTramTrackLane(flags, observation.LanePrefab, out _))
            {
                return false;
            }

            isForward = (flags & LaneFlags.Invert) == 0;
            return true;
        }

        private static bool TryGetUniqueInnermostRoadLane(
            List<LaneProfileObservation> observations,
            bool isForward,
            out int index)
        {
            index = -1;
            float bestOffsetDistance = float.MaxValue;
            int bestCount = 0;
            for (int i = 0; i < observations.Count; i++)
            {
                LaneFlags flags = observations[i].EffectiveFlags;
                if (!IsSingleDirectionRoadLane(flags) ||
                    ((flags & LaneFlags.Invert) == 0) != isForward)
                {
                    continue;
                }

                float offsetDistance = math.abs(observations[i].LateralOffset);
                if (offsetDistance + CenterLaneOffsetTolerance < bestOffsetDistance)
                {
                    bestOffsetDistance = offsetDistance;
                    index = i;
                    bestCount = 1;
                }
                else if (math.abs(offsetDistance - bestOffsetDistance) <= CenterLaneOffsetTolerance)
                {
                    bestCount++;
                }
            }

            return index >= 0 && bestCount == 1;
        }

        private static bool IsSingleDirectionRoadLane(LaneFlags flags)
        {
            if ((flags & (LaneFlags.Master | LaneFlags.Road)) != LaneFlags.Road)
            {
                return false;
            }

            const LaneFlags excluded =
                LaneFlags.Twoway |
                LaneFlags.BicyclesOnly |
                LaneFlags.Parking |
                LaneFlags.Pedestrian |
                LaneFlags.Utility;
            return (flags & excluded) == 0;
        }

        private static bool IsCenteredPair(float firstOffset, float secondOffset)
        {
            bool firstOnLeft = firstOffset <= CenterLaneOffsetTolerance;
            bool firstOnRight = firstOffset >= -CenterLaneOffsetTolerance;
            bool secondOnLeft = secondOffset <= CenterLaneOffsetTolerance;
            bool secondOnRight = secondOffset >= -CenterLaneOffsetTolerance;
            return (firstOnLeft && secondOnRight) || (secondOnLeft && firstOnRight);
        }

        private void AccumulateRoadAndEnvelope(
            LaneFlags effectiveFlags,
            Entity lanePrefab,
            float lateralOffset,
            string flagMergeDetail,
            bool suppressRoadAndEnvelope,
            ref RoadLaneProfile profile)
        {
            if (suppressRoadAndEnvelope)
            {
                return;
            }

            RoadLaneCountMatcher.CountRoadLane(effectiveFlags, ref profile.RoadCounts);
            if (IsDrivablePocketLengthLane(effectiveFlags) &&
                TryGetLanePrefabWidth(lanePrefab, out float laneWidth))
            {
                AddDrivableLaneEnvelope(effectiveFlags, lateralOffset, laneWidth, flagMergeDetail, ref profile);
            }
        }

        private void TryRecordMarkedParking(
            LaneFlags effectiveFlags,
            Entity lanePrefab,
            string flagMergeDetail,
            ref RoadLaneProfile profile)
        {
            if (!profile.HasMarkedParking &&
                IsMarkedParkingLane(effectiveFlags, lanePrefab, out string detail))
            {
                profile.HasMarkedParking = true;
                profile.MarkedParkingDetail = detail + flagMergeDetail;
            }
        }

        private void AccumulateTramSemantics(
            LaneFlags effectiveFlags,
            Entity lanePrefab,
            float lateralOffset,
            string flagMergeDetail,
            bool forceIndependentTram,
            ref RoadLaneProfile profile)
        {
            if (IsTramTrackLane(effectiveFlags, lanePrefab, out string tramDetail))
            {
                string effectiveTramDetail = forceIndependentTram
                    ? tramDetail + flagMergeDetail + " separatedCenterTram=forcedIndependent"
                    : tramDetail + flagMergeDetail;

                AddDirectionalLane(effectiveFlags, ref profile.TramTrackCounts);
                AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.TramTrackLayout);
                if (profile.TramTrackDetail == "<none>")
                {
                    profile.TramTrackDetail = effectiveTramDetail;
                }
                else if (forceIndependentTram)
                {
                    EnsureSeparatedCenterTramDetail(ref profile.TramTrackDetail);
                }

                if (forceIndependentTram || IsIndependentTramTrackLane(effectiveFlags))
                {
                    profile.MandatorySourceFeatures |= CustomRoadAssetSourceFeatures.Tram;
                    AddDirectionalLane(effectiveFlags, ref profile.IndependentTramCounts);
                    AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.IndependentTramLayout);
                    if (profile.IndependentTramDetail == "<none>")
                    {
                        profile.IndependentTramDetail = effectiveTramDetail;
                    }
                    else if (forceIndependentTram)
                    {
                        EnsureSeparatedCenterTramDetail(ref profile.IndependentTramDetail);
                    }
                }

                if (!forceIndependentTram &&
                    IsPublicTransportTramTrackLane(effectiveFlags, lanePrefab, out string publicTransportTramDetail))
                {
                    profile.MandatorySourceFeatures |= CustomRoadAssetSourceFeatures.Tram |
                                                       CustomRoadAssetSourceFeatures.PublicTransport;
                    AddDirectionalLane(effectiveFlags, ref profile.PublicTransportTramCounts);
                    AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.PublicTransportTramLayout);
                    if (profile.PublicTransportTramDetail == "<none>")
                    {
                        profile.PublicTransportTramDetail = publicTransportTramDetail + flagMergeDetail;
                    }
                }
            }
        }

        private void AccumulateBusSemantics(
            LaneFlags effectiveFlags,
            Entity lanePrefab,
            float lateralOffset,
            string flagMergeDetail,
            bool suppressBus,
            ref RoadLaneProfile profile)
        {
            if (suppressBus)
            {
                return;
            }

            if (IsBusRoadLane(effectiveFlags, lanePrefab, out string busDetail))
            {
                profile.MandatorySourceFeatures |= CustomRoadAssetSourceFeatures.PublicTransport;
                AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.BusLaneLayout);
                if (profile.BusLaneDetail == "<none>")
                {
                    profile.BusLaneDetail = busDetail + flagMergeDetail;
                }

                AddDirectionalOffset(effectiveFlags, lateralOffset, ref profile.DedicatedPublicTransportLaneLayout);
                if (profile.DedicatedPublicTransportLaneDetail == "<none>")
                {
                    profile.DedicatedPublicTransportLaneDetail = busDetail + flagMergeDetail;
                }
            }
        }

        private static bool IsDrivablePocketLengthLane(LaneFlags flags)
        {
            if ((flags & (LaneFlags.Master | LaneFlags.Road)) != LaneFlags.Road)
            {
                return false;
            }

            const LaneFlags excluded =
                LaneFlags.BicyclesOnly |
                LaneFlags.Parking |
                LaneFlags.Pedestrian |
                LaneFlags.Utility;
            return (flags & excluded) == 0;
        }

        private bool TryGetLanePrefabWidth(Entity lanePrefab, out float width)
        {
            width = 0f;
            if (lanePrefab == Entity.Null ||
                !EntityManager.TryGetComponent(lanePrefab, out NetLaneData laneData) ||
                laneData.m_Width <= 0.01f)
            {
                return false;
            }

            width = laneData.m_Width;
            return true;
        }

        private static void AddDrivableLaneEnvelope(
            LaneFlags flags,
            float lateralOffset,
            float laneWidth,
            string flagMergeDetail,
            ref RoadLaneProfile profile)
        {
            float halfWidth = laneWidth * 0.5f;
            float min = lateralOffset - halfWidth;
            float max = lateralOffset + halfWidth;

            if (profile.DrivableLaneEnvelopeCount == 0)
            {
                profile.DrivableLaneEnvelopeMin = min;
                profile.DrivableLaneEnvelopeMax = max;
                profile.DrivableLaneEnvelopeDetail = $"firstLane offset={lateralOffset:0.##}m width={laneWidth:0.##}m flags={flags}{flagMergeDetail}";
            }
            else
            {
                profile.DrivableLaneEnvelopeMin = math.min(profile.DrivableLaneEnvelopeMin, min);
                profile.DrivableLaneEnvelopeMax = math.max(profile.DrivableLaneEnvelopeMax, max);
            }

            profile.DrivableLaneEnvelopeCount++;
            profile.DrivableLaneEnvelopeWidth = math.max(
                0f,
                profile.DrivableLaneEnvelopeMax - profile.DrivableLaneEnvelopeMin);
        }

        private LaneFlags GetEffectiveLaneFlags(LaneFlags flags, Entity lanePrefab)
        {
            if (lanePrefab == Entity.Null ||
                !EntityManager.TryGetComponent(lanePrefab, out NetLaneData laneData))
            {
                return flags;
            }

            const LaneFlags semanticLaneFlags =
                LaneFlags.Road |
                LaneFlags.Parking |
                LaneFlags.Track |
                LaneFlags.Pedestrian |
                LaneFlags.PublicOnly |
                LaneFlags.BicyclesOnly |
                LaneFlags.Twoway;
            return flags | (laneData.m_Flags & semanticLaneFlags);
        }

        private static string FormatEffectiveLaneFlags(LaneFlags rawFlags, LaneFlags effectiveFlags)
        {
            return rawFlags == effectiveFlags
                ? string.Empty
                : $" rawFlags={rawFlags} effectiveFlags={effectiveFlags}";
        }

        private static void AddDirectionalLane(LaneFlags flags, ref RoadLaneCounts counts)
        {
            if ((flags & LaneFlags.Twoway) != (LaneFlags)0)
            {
                counts.Forward++;
                counts.Backward++;
            }
            else if ((flags & LaneFlags.Invert) != (LaneFlags)0)
            {
                counts.Backward++;
            }
            else
            {
                counts.Forward++;
            }
        }

        private static void AddDirectionalOffset(
            LaneFlags flags,
            float lateralOffset,
            ref DirectionalLaneOffsetProfile profile)
        {
            if ((flags & LaneFlags.Twoway) != (LaneFlags)0)
            {
                profile.ForwardCount++;
                profile.ForwardOffsetSum += lateralOffset;
                profile.BackwardCount++;
                profile.BackwardOffsetSum += lateralOffset;
            }
            else if ((flags & LaneFlags.Invert) != (LaneFlags)0)
            {
                profile.BackwardCount++;
                profile.BackwardOffsetSum += lateralOffset;
            }
            else
            {
                profile.ForwardCount++;
                profile.ForwardOffsetSum += lateralOffset;
            }
        }

        private bool IsTramTrackLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & LaneFlags.Track) == 0 ||
                (flags & LaneFlags.Master) != 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out TrackLaneData trackLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackLaneData=missing";
                return false;
            }

            if ((trackLaneData.m_TrackTypes & TrackTypes.Tram) == 0)
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackTypes={trackLaneData.m_TrackTypes}";
                return false;
            }

            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackTypes={trackLaneData.m_TrackTypes} fallback={FormatEntity(trackLaneData.m_FallbackPrefab)} endObject={FormatEntity(trackLaneData.m_EndObjectPrefab)}";
            return true;
        }

        private static bool IsIndependentTramTrackLane(LaneFlags flags)
        {
            return (flags & LaneFlags.Road) == 0;
        }

        private bool IsPublicTransportTramTrackLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & (LaneFlags.Road | LaneFlags.Track)) != (LaneFlags.Road | LaneFlags.Track) ||
                (flags & LaneFlags.Master) != 0 ||
                (flags & LaneFlags.BicyclesOnly) != 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out TrackLaneData trackLaneData) ||
                (trackLaneData.m_TrackTypes & TrackTypes.Tram) == 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out CarLaneData carLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} publicTransportTram=False carLaneData=missing trackTypes={trackLaneData.m_TrackTypes}";
                return false;
            }

            bool publicOnly = (flags & LaneFlags.PublicOnly) != 0;
            bool hasNonBusFallback = carLaneData.m_NotBusLanePrefab != Entity.Null;
            bool supportsCars = (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} trackTypes={trackLaneData.m_TrackTypes} roadTypes={carLaneData.m_RoadTypes} supportsCars={supportsCars} publicOnly={publicOnly} notBusFallback={FormatEntity(carLaneData.m_NotBusLanePrefab)}";
            return publicOnly || hasNonBusFallback;
        }

        private bool IsBusRoadLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & LaneFlags.Road) == 0 ||
                (flags & LaneFlags.Master) != 0 ||
                (flags & LaneFlags.BicyclesOnly) != 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out CarLaneData carLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} carLaneData=missing";
                return false;
            }

            bool publicOnly = (flags & LaneFlags.PublicOnly) != 0;
            bool hasNonBusFallback = carLaneData.m_NotBusLanePrefab != Entity.Null;
            bool supportsCars = (carLaneData.m_RoadTypes & RoadTypes.Car) != 0;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} roadTypes={carLaneData.m_RoadTypes} supportsCars={supportsCars} publicOnly={publicOnly} notBusFallback={FormatEntity(carLaneData.m_NotBusLanePrefab)}";
            return publicOnly || hasNonBusFallback;
        }

        private bool IsMarkedParkingLane(LaneFlags flags, Entity lanePrefab, out string detail)
        {
            detail = "<none>";
            if ((flags & LaneFlags.Parking) == 0)
            {
                return false;
            }

            if (!EntityManager.TryGetComponent(lanePrefab, out ParkingLaneData parkingLaneData))
            {
                detail = $"lane={FormatEntity(lanePrefab)} flags={flags} parkingLaneData=missing";
                return false;
            }

            float angleDegrees = math.degrees(parkingLaneData.m_SlotAngle);
            bool markedParking = angleDegrees >= MinimumMarkedParkingSlotAngleDegrees;
            detail = $"lane={FormatEntity(lanePrefab)} flags={flags} slotAngle={angleDegrees:0.#}deg slotSize=({parkingLaneData.m_SlotSize.x:0.##},{parkingLaneData.m_SlotSize.y:0.##}) threshold={MinimumMarkedParkingSlotAngleDegrees:0.#}deg";
            return markedParking;
        }

        private static void EnsureSeparatedCenterTramDetail(ref string detail)
        {
            const string forcedDetail = "separatedCenterTram=forcedIndependent";
            if (string.IsNullOrEmpty(detail) ||
                detail.IndexOf(forcedDetail, StringComparison.Ordinal) >= 0)
            {
                return;
            }

            detail += " " + forcedDetail;
        }

        private struct LaneProfileObservation
        {
            public LaneFlags EffectiveFlags;
            public Entity LanePrefab;
            public float LateralOffset;
            public string FlagMergeDetail;
            public bool ForceIndependentTram;
        }

        private static string FormatEntity(Entity entity)
        {
            return DiagnosticFormat.Entity(entity);
        }
    }
}
