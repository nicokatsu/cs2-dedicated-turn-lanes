using System;
using System.Collections.Generic;
using System.Text;
using PocketTurnLanes.Options;
using PocketTurnLanes.Tool.PrefabMatching;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private const int MaxCustomRoadAssetDropdownOptions = 0;

        internal string GetCustomRoadAssetMatchStateJson()
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("state-read");
            return BuildCustomRoadAssetMatchStateJson("state", cleaned, string.Empty);
        }

        internal string SearchCustomRoadAssetSourcesJson(string query)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("source-search");
            List<RoadAssetPrefabOption> options = new List<RoadAssetPrefabOption>();
            m_ReplacementPrefabMatcher?.GetRoadAssetSourceOptions(
                query,
                options,
                MaxCustomRoadAssetDropdownOptions);
            return BuildOptionListJson("source-search", query, options, cleaned, string.Empty);
        }

        internal string SelectCustomRoadAssetSourceJson(string sourcePrefabName)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("source-select");
            List<RoadAssetPrefabOption> targets = new List<RoadAssetPrefabOption>();
            RoadAssetPrefabOption sourceOption = default;
            string detail = "matcher-unavailable";
            bool success = m_ReplacementPrefabMatcher != null &&
                           m_ReplacementPrefabMatcher.TryGetCompatibleTargetOptions(
                               sourcePrefabName,
                               string.Empty,
                               targets,
                               MaxCustomRoadAssetDropdownOptions,
                               out sourceOption,
                               out detail);

            if (!success)
            {
                sourceOption = default;
                detail = string.IsNullOrEmpty(detail)
                    ? "matcher-unavailable"
                    : detail;
            }

            return BuildSourceSelectionJson(
                sourcePrefabName,
                success,
                sourceOption,
                targets,
                cleaned,
                detail);
        }

        internal string SearchCustomRoadAssetTargetsJson(string sourcePrefabName, string query)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("target-search");
            List<RoadAssetPrefabOption> targets = new List<RoadAssetPrefabOption>();
            RoadAssetPrefabOption sourceOption = default;
            string detail = "matcher-unavailable";
            bool success = m_ReplacementPrefabMatcher != null &&
                           m_ReplacementPrefabMatcher.TryGetCompatibleTargetOptions(
                               sourcePrefabName,
                               query,
                               targets,
                               MaxCustomRoadAssetDropdownOptions,
                               out sourceOption,
                               out detail);

            if (!success)
            {
                sourceOption = default;
                detail = string.IsNullOrEmpty(detail)
                    ? "matcher-unavailable"
                    : detail;
            }

            return BuildSourceSelectionJson(
                sourcePrefabName,
                success,
                sourceOption,
                targets,
                cleaned,
                detail);
        }

        internal string SetCustomRoadAssetMatchJson(string sourcePrefabName, string targetPrefabName)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("set-before");
            CustomRoadAssetMatchRuleStore store = Mod.CustomRoadAssetMatchRules;
            if (store == null || m_ReplacementPrefabMatcher == null)
            {
                return BuildCustomRoadAssetMatchStateJson("set", cleaned, "rule-store-or-matcher-unavailable");
            }

            if (!m_ReplacementPrefabMatcher.IsValidCustomRoadAssetMatch(
                    sourcePrefabName,
                    targetPrefabName,
                    out string validationDetail))
            {
                Mod.LogEssential($"[CustomRoadAssetMatch] Set rejected sourcePrefab={sourcePrefabName} targetPrefab={targetPrefabName} validation={validationDetail}.");
                return BuildCustomRoadAssetMatchStateJson("set", cleaned, $"invalid-rule {validationDetail}");
            }

            if (!store.SetRule(sourcePrefabName, targetPrefabName, out string setDetail))
            {
                return BuildCustomRoadAssetMatchStateJson("set", cleaned, setDetail);
            }

            int cleanedAfter = CleanInvalidCustomRoadAssetMatches("set-after");
            return BuildCustomRoadAssetMatchStateJson("set", cleaned + cleanedAfter, setDetail);
        }

        internal string DeleteCustomRoadAssetMatchJson(string sourcePrefabName)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("delete-before");
            CustomRoadAssetMatchRuleStore store = Mod.CustomRoadAssetMatchRules;
            string detail = "rule-store-unavailable";
            if (store != null)
            {
                store.DeleteRule(sourcePrefabName, out detail);
            }

            int cleanedAfter = CleanInvalidCustomRoadAssetMatches("delete-after");
            return BuildCustomRoadAssetMatchStateJson("delete", cleaned + cleanedAfter, detail);
        }

        private int CleanInvalidCustomRoadAssetMatches(string reason)
        {
            CustomRoadAssetMatchRuleStore store = Mod.CustomRoadAssetMatchRules;
            if (store == null || m_ReplacementPrefabMatcher == null)
            {
                return 0;
            }

            return store.CleanInvalidRules(
                m_ReplacementPrefabMatcher.IsValidCustomRoadAssetMatch,
                reason);
        }

        private string BuildCustomRoadAssetMatchStateJson(
            string action,
            int cleaned,
            string message)
        {
            Dictionary<string, string> snapshot = Mod.CustomRoadAssetMatchRules?.GetSnapshot() ??
                                                  new Dictionary<string, string>(StringComparer.Ordinal);
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            AppendJsonProperty(builder, "schemaVersion", 1);
            builder.Append(',');
            AppendJsonProperty(builder, "action", action);
            builder.Append(',');
            AppendJsonProperty(builder, "cleanedInvalidRules", cleaned);
            builder.Append(',');
            AppendJsonProperty(builder, "message", message ?? string.Empty);
            builder.Append(',');
            builder.Append("\"rules\":[");

            bool first = true;
            int appended = 0;
            foreach (KeyValuePair<string, string> rule in snapshot)
            {
                if (m_ReplacementPrefabMatcher == null ||
                    !m_ReplacementPrefabMatcher.TryGetRoadAssetPrefabOption(rule.Key, out RoadAssetPrefabOption source, out _) ||
                    !m_ReplacementPrefabMatcher.TryGetRoadAssetPrefabOption(rule.Value, out RoadAssetPrefabOption target, out _))
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                appended++;
                builder.Append('{');
                builder.Append("\"source\":");
                AppendOption(builder, source);
                builder.Append(',');
                builder.Append("\"target\":");
                AppendOption(builder, target);
                builder.Append('}');
            }

            builder.Append(']');
            builder.Append(',');
            AppendJsonProperty(builder, "ruleCount", appended);
            builder.Append('}');
            return builder.ToString();
        }

        private static string BuildOptionListJson(
            string action,
            string query,
            List<RoadAssetPrefabOption> options,
            int cleaned,
            string message)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            AppendJsonProperty(builder, "schemaVersion", 1);
            builder.Append(',');
            AppendJsonProperty(builder, "action", action);
            builder.Append(',');
            AppendJsonProperty(builder, "query", query ?? string.Empty);
            builder.Append(',');
            AppendJsonProperty(builder, "cleanedInvalidRules", cleaned);
            builder.Append(',');
            AppendJsonProperty(builder, "message", message ?? string.Empty);
            builder.Append(',');
            builder.Append("\"items\":");
            AppendOptions(builder, options);
            builder.Append('}');
            return builder.ToString();
        }

        private static string BuildSourceSelectionJson(
            string sourcePrefabName,
            bool success,
            RoadAssetPrefabOption source,
            List<RoadAssetPrefabOption> targets,
            int cleaned,
            string detail)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            AppendJsonProperty(builder, "schemaVersion", 1);
            builder.Append(',');
            AppendJsonProperty(builder, "action", "source-select");
            builder.Append(',');
            AppendJsonProperty(builder, "success", success);
            builder.Append(',');
            AppendJsonProperty(builder, "sourcePrefabName", sourcePrefabName ?? string.Empty);
            builder.Append(',');
            AppendJsonProperty(builder, "cleanedInvalidRules", cleaned);
            builder.Append(',');
            AppendJsonProperty(builder, "message", detail ?? string.Empty);
            builder.Append(',');
            builder.Append("\"source\":");
            if (success)
            {
                AppendOption(builder, source);
            }
            else
            {
                builder.Append("null");
            }

            builder.Append(',');
            builder.Append("\"targets\":");
            AppendOptions(builder, targets);
            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendOptions(
            StringBuilder builder,
            List<RoadAssetPrefabOption> options)
        {
            builder.Append('[');
            for (int i = 0; i < options.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                AppendOption(builder, options[i]);
            }

            builder.Append(']');
        }

        private static void AppendOption(
            StringBuilder builder,
            RoadAssetPrefabOption option)
        {
            builder.Append('{');
            AppendJsonProperty(builder, "prefabName", option.PrefabName ?? string.Empty);
            builder.Append(',');
            AppendJsonProperty(builder, "displayName", option.DisplayName ?? string.Empty);
            builder.Append(',');
            AppendJsonProperty(builder, "icon", option.Icon ?? string.Empty);
            builder.Append(',');
            AppendJsonProperty(builder, "summary", option.Summary ?? string.Empty);
            builder.Append(',');
            AppendJsonProperty(builder, "isDlc", option.IsDlc);
            builder.Append(',');
            AppendJsonProperty(builder, "contentDetail", option.ContentDetail ?? string.Empty);
            builder.Append('}');
        }

        private static void AppendJsonProperty(
            StringBuilder builder,
            string name,
            string value)
        {
            AppendJsonString(builder, name);
            builder.Append(':');
            AppendJsonString(builder, value);
        }

        private static void AppendJsonProperty(
            StringBuilder builder,
            string name,
            int value)
        {
            AppendJsonString(builder, name);
            builder.Append(':');
            builder.Append(value);
        }

        private static void AppendJsonProperty(
            StringBuilder builder,
            string name,
            bool value)
        {
            AppendJsonString(builder, name);
            builder.Append(':');
            builder.Append(value ? "true" : "false");
        }

        private static void AppendJsonString(StringBuilder builder, string value)
        {
            builder.Append('"');
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '\\':
                            builder.Append("\\\\");
                            break;
                        case '"':
                            builder.Append("\\\"");
                            break;
                        case '\b':
                            builder.Append("\\b");
                            break;
                        case '\f':
                            builder.Append("\\f");
                            break;
                        case '\n':
                            builder.Append("\\n");
                            break;
                        case '\r':
                            builder.Append("\\r");
                            break;
                        case '\t':
                            builder.Append("\\t");
                            break;
                        default:
                            if (c < 32)
                            {
                                builder.Append("\\u");
                                builder.Append(((int)c).ToString("x4"));
                            }
                            else
                            {
                                builder.Append(c);
                            }

                            break;
                    }
                }
            }

            builder.Append('"');
        }
    }
}
