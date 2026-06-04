namespace PocketTurnLanes.Tool.PrefabMatching
{
    internal struct ReplacementSearchStats
    {
        public int Scanned;
        public int DlcBlocked;
        public int WidthMatches;
        public int ParkingExcluded;
        public int LaneMatches;
        public int MissingLaneData;
        public int IndependentTramCandidates;
        public int PublicTransportTramCandidates;
        public int TramUpgradeCandidates;
        public int TramUpgradeRejected;
        public int BusUpgradeCandidates;
        public int BusUpgradeRejected;
        public int LayoutScored;
        public int BusLayoutCandidates;
        public int SourcePrefabLaneMatches;
        public int RoadBuilderDiscarded;
        public int RoadBuilderNotInPlaysetExcluded;
        public int RoadBuilderVisibilityUnknown;
        public int HighwayExcluded;
        public string TramUpgradeRejectSample;
        public string BusUpgradeRejectSample;
        public string WidthCandidateSample;
        public string RoadBuilderCandidateSample;
        public string RoadBuilderBusUpgradeSample;
        public string RoadBuilderDiscardedSample;
        public string RoadBuilderNotInPlaysetSample;
        public string RoadBuilderVisibilityUnknownSample;
        public string BestBusLayoutCandidateDetail;
        private int m_BusUpgradeRejectSampleCount;
        private int m_WidthCandidateSampleCount;
        private int m_RoadBuilderCandidateSampleCount;
        private int m_RoadBuilderBusUpgradeSampleCount;
        private int m_RoadBuilderDiscardedSampleCount;
        private int m_RoadBuilderNotInPlaysetSampleCount;
        private int m_RoadBuilderVisibilityUnknownSampleCount;
        private int m_BestBusLayoutCandidateScore;

        public static ReplacementSearchStats Create()
        {
            return new ReplacementSearchStats
            {
                TramUpgradeRejectSample = "<none>",
                BusUpgradeRejectSample = "<none>",
                WidthCandidateSample = "<none>",
                RoadBuilderCandidateSample = "<none>",
                RoadBuilderBusUpgradeSample = "<none>",
                RoadBuilderDiscardedSample = "<none>",
                RoadBuilderNotInPlaysetSample = "<none>",
                RoadBuilderVisibilityUnknownSample = "<none>",
                BestBusLayoutCandidateDetail = "<none>",
                m_BestBusLayoutCandidateScore = int.MaxValue
            };
        }

        public void AddTramUpgradeRejection(string sample)
        {
            TramUpgradeRejected++;
            if (TramUpgradeRejectSample == "<none>")
            {
                TramUpgradeRejectSample = sample;
            }
        }

        public void AddBusUpgradeRejection(string sample, int maxSamples)
        {
            BusUpgradeRejected++;
            ReplacementPrefabDiagnostics.AppendLogSample(ref BusUpgradeRejectSample, ref m_BusUpgradeRejectSampleCount, sample, maxSamples);
        }

        public void AddWidthCandidateSample(string sample, int maxSamples)
        {
            ReplacementPrefabDiagnostics.AppendLogSample(ref WidthCandidateSample, ref m_WidthCandidateSampleCount, sample, maxSamples);
        }

        public void AddRoadBuilderCandidateSample(string sample, int maxSamples)
        {
            ReplacementPrefabDiagnostics.AppendLogSample(ref RoadBuilderCandidateSample, ref m_RoadBuilderCandidateSampleCount, sample, maxSamples);
        }

        public void AddRoadBuilderBusUpgradeSample(string sample, int maxSamples)
        {
            ReplacementPrefabDiagnostics.AppendLogSample(ref RoadBuilderBusUpgradeSample, ref m_RoadBuilderBusUpgradeSampleCount, sample, maxSamples);
        }

        public void AddRoadBuilderDiscardedSample(string sample, int maxSamples)
        {
            ReplacementPrefabDiagnostics.AppendLogSample(ref RoadBuilderDiscardedSample, ref m_RoadBuilderDiscardedSampleCount, sample, maxSamples);
        }

        public void AddRoadBuilderNotInPlaysetSample(string sample, int maxSamples)
        {
            ReplacementPrefabDiagnostics.AppendLogSample(ref RoadBuilderNotInPlaysetSample, ref m_RoadBuilderNotInPlaysetSampleCount, sample, maxSamples);
        }

        public void AddRoadBuilderVisibilityUnknownSample(string sample, int maxSamples)
        {
            ReplacementPrefabDiagnostics.AppendLogSample(ref RoadBuilderVisibilityUnknownSample, ref m_RoadBuilderVisibilityUnknownSampleCount, sample, maxSamples);
        }

        public void RecordBusLayoutCandidate(int score, string detail)
        {
            BusLayoutCandidates++;
            if (score >= m_BestBusLayoutCandidateScore)
            {
                return;
            }

            m_BestBusLayoutCandidateScore = score;
            BestBusLayoutCandidateDetail = detail;
        }
    }

    internal static class ReplacementPrefabDiagnostics
    {
        public static void AppendLogSample(
            ref string samples,
            ref int sampleCount,
            string sample,
            int maxSamples)
        {
            if (sampleCount >= maxSamples)
            {
                return;
            }

            if (samples == "<none>")
            {
                samples = sample;
            }
            else
            {
                samples += " || " + sample;
            }

            sampleCount++;
        }
    }
}
