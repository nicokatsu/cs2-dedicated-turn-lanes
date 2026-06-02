namespace PocketTurnLanes.Tool.Traffic
{
    public static class TrafficIntegrationState
    {
        private static TrafficIntegrationStatus s_Status = TrafficIntegrationStatus.NotChecked();
        private static int s_Version;

        public static string StatusText
        {
            get
            {
                if (!s_Status.DetectionCompleted)
                {
                    return "Not checked yet";
                }

                return s_Status.DetectedOnLoad ? "Detected" : "Not detected";
            }
        }

        public static int GetVersion()
        {
            return s_Version;
        }

        public static bool ShowTrafficMissingWarning()
        {
            return s_Status.DetectionCompleted && !s_Status.DetectedOnLoad;
        }

        internal static void Update(TrafficIntegrationStatus status)
        {
            s_Status = status ?? TrafficIntegrationStatus.NotChecked();
            s_Version++;
            Mod.LogDiagnostic($"[TrafficStatus] {s_Status.ToDiagnosticString()} statusVersion={s_Version}");
        }

        internal static void UpdateRuntime(bool runtimeReady, string lastRuntimeError, int waitFrames)
        {
            s_Status.RuntimeReady = runtimeReady;
            s_Status.LastRuntimeError = string.IsNullOrWhiteSpace(lastRuntimeError) ? "none" : lastRuntimeError;
            s_Status.RuntimeWaitFrames = waitFrames;
            s_Version++;
            Mod.LogDiagnostic($"[TrafficStatus] Runtime status updated runtimeReady={runtimeReady} waitFrames={waitFrames} lastRuntimeError={s_Status.LastRuntimeError} statusVersion={s_Version}");
        }
    }
}
