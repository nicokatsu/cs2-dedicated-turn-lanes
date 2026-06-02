namespace PocketTurnLanes.Tool.Traffic
{
    internal sealed class TrafficIntegrationStatus
    {
        public bool DetectionCompleted { get; set; }
        public bool DetectedOnLoad { get; set; }
        public bool RepairSystemsEnabled { get; set; }
        public bool GuardedFallback { get; set; }
        public bool RuntimeReady { get; set; }
        public int RuntimeWaitFrames { get; set; }
        public string LastRuntimeError { get; set; } = "not checked";
        public string EnabledModMatches { get; set; } = "<none>";
        public int EnabledModsCount { get; set; }
        public string EnabledModsError { get; set; } = "not checked";
        public string LoadedAssembly { get; set; } = "<none>";
        public int RuntimeTypesFound { get; set; }
        public int RuntimeTypesExpected { get; set; }
        public string RuntimeTypes { get; set; } = "<none>";

        public static TrafficIntegrationStatus NotChecked()
        {
            return new TrafficIntegrationStatus();
        }

        public string ToSummaryString()
        {
            if (!DetectionCompleted)
            {
                return "Traffic detection has not run yet.";
            }

            return $"Traffic detectedOnLoad={DetectedOnLoad} repairSystemsEnabled={RepairSystemsEnabled} guardedFallback={GuardedFallback} runtimeReady={RuntimeReady} lastRuntimeError={LastRuntimeError}";
        }

        public string ToDisplayString()
        {
            if (!DetectionCompleted)
            {
                return "Not checked yet";
            }

            return DetectedOnLoad ? "Detected" : "Not detected";
        }

        public string ToDiagnosticString()
        {
            return $"{ToSummaryString()} enabledModMatches={EnabledModMatches} enabledModsCount={EnabledModsCount} enabledModsError={EnabledModsError} loadedAssembly={LoadedAssembly} runtimeTypesFound={RuntimeTypesFound}/{RuntimeTypesExpected} runtimeTypes={RuntimeTypes}";
        }
    }
}
