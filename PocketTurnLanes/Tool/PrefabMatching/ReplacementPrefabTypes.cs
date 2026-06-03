using Game.Net;
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
                Source = source
            };
        }
    }

    internal struct DirectionalLaneOffsetProfile
    {
        public int ForwardCount;
        public int BackwardCount;
        public float ForwardOffsetSum;
        public float BackwardOffsetSum;

        public bool HasAny => ForwardCount > 0 || BackwardCount > 0;

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
