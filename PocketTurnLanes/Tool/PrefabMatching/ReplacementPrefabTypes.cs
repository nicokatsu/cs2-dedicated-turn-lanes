using Game.Net;
using Game.Prefabs;
using Unity.Entities;

namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal struct ReplacementPrefabMatch
    {
        public Entity Prefab;
        public bool Invert;
        public RoadLaneCounts OriginalCounts;
        public RoadLaneCounts TargetCounts;
        public RoadLaneCounts CandidateCounts;
        public RoadLaneCounts OriginalEffectiveCounts;
        public RoadLaneCounts TargetEffectiveCounts;
        public RoadLaneCounts SourceIndependentTramCounts;
        public RoadLaneCounts TargetIndependentTramCounts;
        public RoadLaneCounts SourcePublicTransportTramCounts;
        public RoadLaneCounts TargetPublicTransportTramCounts;
        public RoadLaneCounts SourceTramTrackCounts;
        public RoadLaneCounts TargetTramTrackCounts;
        public bool TargetHasIndependentTram;
        public bool TargetHasPublicTransportTram;
        public bool TargetUsesTramUpgradeFallback;
        public bool HasTargetUpgrade;
        public Upgraded TargetUpgrade;
        public string TramMatchDetail;
        public string SourceTramTrackLayout;
        public string TargetTramTrackLayout;
        public string SourceBusLaneLayout;
        public string TargetBusLaneLayout;
        public DirectionalLaneOffsetProfile TargetTramTrackOffsetProfile;
        public DirectionalLaneOffsetProfile TargetBusLaneOffsetProfile;
        public string SourceBusLaneDetail;
        public string TargetBusLaneDetail;
        public ReplacementUtilityProfile SourceUtilityProfile;
        public ReplacementUtilityProfile TargetUtilityProfile;
        public CompositionFlags TargetUtilityFixFlags;
        public string UtilityMatchDetail;
        public string LayoutScoreDetail;
        public int LayoutScore;
        public int TramLayoutScore;
        public int BusLayoutScore;
        public bool TargetIsSourcePrefab;
        public bool TargetIsDlc;
        public string TargetContentDetail;
        public int Score;
    }

    internal struct RoadLaneProfile
    {
        public RoadLaneCounts RoadCounts;
        public RoadLaneCounts TramTrackCounts;
        public RoadLaneCounts IndependentTramCounts;
        public RoadLaneCounts PublicTransportTramCounts;
        public DirectionalLaneOffsetProfile TramTrackLayout;
        public DirectionalLaneOffsetProfile IndependentTramLayout;
        public DirectionalLaneOffsetProfile PublicTransportTramLayout;
        public DirectionalLaneOffsetProfile BusLaneLayout;
        public DirectionalLaneOffsetProfile UtilityLaneLayout;
        public UtilityTypes UtilityTypes;
        public bool ElectricityConnection;
        public bool WaterPipeConnection;
        public bool HasCompositionFlags;
        public CompositionFlags CompositionFlags;
        public int DrivableLaneEnvelopeCount;
        public float DrivableLaneEnvelopeMin;
        public float DrivableLaneEnvelopeMax;
        public float DrivableLaneEnvelopeWidth;
        public bool HasMarkedParking;
        public string DrivableLaneEnvelopeDetail;
        public string MarkedParkingDetail;
        public string TramTrackDetail;
        public string IndependentTramDetail;
        public string PublicTransportTramDetail;
        public string BusLaneDetail;
        public string UtilityLaneDetail;
        public string ElectricityConnectionDetail;
        public string WaterPipeConnectionDetail;
        public string CompositionFlagsDetail;
        public string Source;

        public static RoadLaneProfile CreateEmpty(string source)
        {
            return new RoadLaneProfile
            {
                DrivableLaneEnvelopeDetail = "<none>",
                MarkedParkingDetail = "<none>",
                TramTrackDetail = "<none>",
                IndependentTramDetail = "<none>",
                PublicTransportTramDetail = "<none>",
                BusLaneDetail = "<none>",
                UtilityLaneDetail = "<none>",
                ElectricityConnectionDetail = "<none>",
                WaterPipeConnectionDetail = "<none>",
                CompositionFlagsDetail = "<none>",
                Source = source
            };
        }

        public ReplacementUtilityProfile GetUtilityProfile()
        {
            return new ReplacementUtilityProfile
            {
                LaneLayout = UtilityLaneLayout,
                UtilityTypes = UtilityTypes,
                ElectricityConnection = ElectricityConnection,
                WaterPipeConnection = WaterPipeConnection,
                HasCompositionFlags = HasCompositionFlags,
                CompositionFlags = CompositionFlags,
                LaneDetail = UtilityLaneDetail,
                ElectricityDetail = ElectricityConnectionDetail,
                WaterDetail = WaterPipeConnectionDetail,
                CompositionDetail = CompositionFlagsDetail
            };
        }
    }

    internal struct ReplacementUtilityProfile
    {
        public DirectionalLaneOffsetProfile LaneLayout;
        public UtilityTypes UtilityTypes;
        public bool ElectricityConnection;
        public bool WaterPipeConnection;
        public bool HasCompositionFlags;
        public CompositionFlags CompositionFlags;
        public string LaneDetail;
        public string ElectricityDetail;
        public string WaterDetail;
        public string CompositionDetail;

        public bool RequiresAny => LaneLayout.HasAny || ElectricityConnection || WaterPipeConnection || UtilityTypes != Game.Net.UtilityTypes.None;

        public override string ToString()
        {
            return $"lanes={LaneLayout} types={UtilityTypes} electricity={ElectricityConnection} water={WaterPipeConnection} composition={(HasCompositionFlags ? CompositionFlags.ToString() : "<unknown>")}";
        }
    }

    internal struct DirectionalLaneOffsetProfile
    {
        public int ForwardCount;
        public int BackwardCount;
        public float ForwardOffsetSum;
        public float BackwardOffsetSum;

        public bool HasAny => ForwardCount > 0 || BackwardCount > 0;

        public int GetIncomingCountAtNode(bool nodeIsStart)
        {
            return nodeIsStart ? BackwardCount : ForwardCount;
        }

        public int GetOutgoingCountAtNode(bool nodeIsStart)
        {
            return nodeIsStart ? ForwardCount : BackwardCount;
        }

        public bool CountsMatchAtNodeSides(
            DirectionalLaneOffsetProfile continuationLayout,
            bool targetNodeIsStart,
            bool continuationNodeIsStart)
        {
            return GetIncomingCountAtNode(targetNodeIsStart) == continuationLayout.GetIncomingCountAtNode(continuationNodeIsStart) &&
                   GetOutgoingCountAtNode(targetNodeIsStart) == continuationLayout.GetOutgoingCountAtNode(continuationNodeIsStart);
        }

        public DirectionalLaneOffsetProfile Oriented(bool invert)
        {
            if (!invert)
            {
                return this;
            }

            return new DirectionalLaneOffsetProfile
            {
                ForwardCount = BackwardCount,
                ForwardOffsetSum = -BackwardOffsetSum,
                BackwardCount = ForwardCount,
                BackwardOffsetSum = -ForwardOffsetSum
            };
        }

        public override string ToString()
        {
            if (!HasAny)
            {
                return "none";
            }

            string forward = ForwardCount > 0
                ? $"{ForwardCount}@{ForwardOffsetSum / ForwardCount:0.##}m"
                : "0";
            string backward = BackwardCount > 0
                ? $"{BackwardCount}@{BackwardOffsetSum / BackwardCount:0.##}m"
                : "0";
            return $"F{forward}/B{backward}";
        }
    }
}
