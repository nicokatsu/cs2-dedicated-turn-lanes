using System;

namespace PocketTurnLanes.Diagnostics
{
    internal static class ModLogger
    {
        private const int MaxDiagnosticMessageCharacters = 20000;

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
            if (!TryPrepareDiagnosticLog(message, out string preparedMessage, out string suppressedSummary))
            {
                return;
            }

            if (!string.IsNullOrEmpty(suppressedSummary))
            {
                Mod.log.Info(suppressedSummary);
            }

            Mod.log.Info(preparedMessage);
        }

        public static void LogDiagnostic(Exception exception, string message)
        {
            if (!TryPrepareDiagnosticLog(message, out string preparedMessage, out string suppressedSummary))
            {
                return;
            }

            if (!string.IsNullOrEmpty(suppressedSummary))
            {
                Mod.log.Info(suppressedSummary);
            }

            Mod.log.Info(exception, preparedMessage);
        }

        public static void LogException(Exception exception, string message)
        {
            Mod.log.Info(exception, $"[ERROR] {message}");
        }

        private static bool TryPrepareDiagnosticLog(
            string message,
            out string preparedMessage,
            out string suppressedSummary)
        {
            preparedMessage = null;
            suppressedSummary = null;
            if (!DiagnosticLoggingEnabled)
            {
                return false;
            }

            preparedMessage = PrepareDiagnosticMessage(message);
            return TryEnterDiagnosticRateLimit(preparedMessage.Length, out suppressedSummary);
        }

        private static string PrepareDiagnosticMessage(string message)
        {
            string normalizedMessage = NormalizeDiagnosticMessage(message);
            if (normalizedMessage.Length <= MaxDiagnosticMessageCharacters)
            {
                return normalizedMessage;
            }

            string suffix = $" ... [truncated=True originalLength={normalizedMessage.Length}]";
            int prefixLength = Math.Max(0, MaxDiagnosticMessageCharacters - suffix.Length);
            return normalizedMessage.Substring(0, prefixLength) + suffix;
        }

        private static string NormalizeDiagnosticMessage(string message)
        {
            return (message ?? string.Empty)
                .Replace("\r\n", " | ")
                .Replace("\n", " | ")
                .Replace("\r", " | ");
        }

        private static bool TryEnterDiagnosticRateLimit(int diagnosticCharacters, out string suppressedSummary)
        {
            suppressedSummary = null;
            return true;
        }
    }
}
