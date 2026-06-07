using System;

namespace PocketTurnLanes.Diagnostics
{
    internal static class ModLogger
    {
        private const int MaxDiagnosticMessageCharacters = 2048;
        private const int MaxDiagnosticMessagesPerSecond = 60;

        private static readonly object s_DiagnosticRateLimitLock = new object();
        private static Func<bool> s_IsDiagnosticLoggingEnabled = () => false;
        private static DateTime s_DiagnosticWindowStartUtc = DateTime.MinValue;
        private static int s_DiagnosticWindowMessageCount;
        private static int s_SuppressedDiagnosticCount;
        private static long s_SuppressedDiagnosticCharacters;

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
            DateTime nowUtc = DateTime.UtcNow;

            lock (s_DiagnosticRateLimitLock)
            {
                if (s_DiagnosticWindowStartUtc == DateTime.MinValue ||
                    (nowUtc - s_DiagnosticWindowStartUtc).TotalSeconds >= 1)
                {
                    s_DiagnosticWindowStartUtc = nowUtc;
                    s_DiagnosticWindowMessageCount = 0;
                    if (s_SuppressedDiagnosticCount > 0)
                    {
                        suppressedSummary = $"[Diagnostics] suppressed={s_SuppressedDiagnosticCount} chars={s_SuppressedDiagnosticCharacters} reason=rateLimit";
                        s_SuppressedDiagnosticCount = 0;
                        s_SuppressedDiagnosticCharacters = 0;
                        s_DiagnosticWindowMessageCount++;
                    }
                }

                if (s_DiagnosticWindowMessageCount >= MaxDiagnosticMessagesPerSecond)
                {
                    s_SuppressedDiagnosticCount++;
                    s_SuppressedDiagnosticCharacters += diagnosticCharacters;
                    return false;
                }

                s_DiagnosticWindowMessageCount++;
                return true;
            }
        }
    }
}
