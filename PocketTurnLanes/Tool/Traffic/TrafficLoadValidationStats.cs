using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal enum TrafficLoadValidationState
    {
        Passed,
        Partial,
        Failed
    }

    internal struct TrafficLoadValidationStats
    {
        private const int MaxSamples = 8;

        public int ValidSources;
        public int InvalidSources;
        public int RemovedSources;
        public int ValidConnections;
        public int InvalidConnections;
        public int InvalidRoadRepairConnections;
        public int InvalidPreservationConnections;
        public int SanitizedConnections;
        public int EmptySourcesKept;
        public List<string> Samples;

        public TrafficLoadValidationState State
        {
            get
            {
                if (InvalidRoadRepairConnections > 0 ||
                    InvalidSources > 0 && ValidSources == 0)
                {
                    return TrafficLoadValidationState.Failed;
                }

                if (InvalidConnections > 0 ||
                    InvalidSources > 0 ||
                    SanitizedConnections > 0)
                {
                    return TrafficLoadValidationState.Partial;
                }

                return TrafficLoadValidationState.Passed;
            }
        }

        public bool ShouldLogAdjustment =>
            InvalidConnections > 0 ||
            InvalidSources > 0 ||
            SanitizedConnections > 0;

        public static TrafficLoadValidationStats Create()
        {
            return new TrafficLoadValidationStats
            {
                Samples = new List<string>(MaxSamples)
            };
        }

        public void AddSample(string sample)
        {
            if (Samples == null)
            {
                Samples = new List<string>(MaxSamples);
            }

            if (Samples.Count < MaxSamples)
            {
                Samples.Add(sample);
            }
        }

        public string Format(IEnumerable<SourceLaneKey> ownedSourceKeys)
        {
            return $"loadValidation={FormatState(State)} validSources={ValidSources} invalidSources={InvalidSources} removedSources={RemovedSources} validConnections={ValidConnections} invalidConnections={InvalidConnections} invalidRoadRepair={InvalidRoadRepairConnections} invalidPreservationDropped={InvalidPreservationConnections} sanitizedConnections={SanitizedConnections} emptySourcesKept={EmptySourcesKept} ownedSourceKeys={TrafficRepairDiagnosticFormat.FormatSourceLaneKeys(ownedSourceKeys)} samples={TrafficRepairDiagnosticFormat.FormatStringList(Samples)}";
        }

        private static string FormatState(TrafficLoadValidationState state)
        {
            switch (state)
            {
                case TrafficLoadValidationState.Failed:
                    return "failed";
                case TrafficLoadValidationState.Partial:
                    return "partial";
                default:
                    return "passed";
            }
        }
    }
}
