using System;

namespace PocketTurnLanes.Diagnostics
{
    internal static class ModLogger
    {
        private static Func<bool> s_IsDiagnosticLoggingEnabled = () => false;

        public static bool DiagnosticLoggingEnabled => s_IsDiagnosticLoggingEnabled();

        public static void SetDiagnosticLoggingProvider(Func<bool> isDiagnosticLoggingEnabled)
        {
            s_IsDiagnosticLoggingEnabled = isDiagnosticLoggingEnabled ?? (() => false);
        }

        public static void LogEssential(string message)
        {
            Mod.log.Info(message);
        }

        public static void LogDiagnostic(string message)
        {
            if (DiagnosticLoggingEnabled)
            {
                Mod.log.Info(message);
            }
        }

        public static void LogDiagnostic(Exception exception, string message)
        {
            if (DiagnosticLoggingEnabled)
            {
                Mod.log.Info(exception, message);
            }
        }

        public static void LogException(Exception exception, string message)
        {
            Mod.log.Info(exception, $"[ERROR] {message}");
        }
    }
}
