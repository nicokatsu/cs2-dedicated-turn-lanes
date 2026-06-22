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
            Mod.LogDiagnostic($"[CustomRoadAssetMatch] Source search query=\"{query ?? string.Empty}\" options={options.Count} cleaned={cleaned} sample={FormatRoadAssetOptionSample(options)}.");
            return BuildOptionListJson("source-search", query, options, cleaned, string.Empty);
        }

        internal string SelectCustomRoadAssetSourceJson(string sourcePrefabName, string sourceFeatureMask)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("source-select");
            List<RoadAssetPrefabOption> targets = new List<RoadAssetPrefabOption>();
            RoadAssetPrefabOption sourceOption = default;
            string detail = "matcher-unavailable";
            bool success = false;
            if (!CustomRoadAssetSourceFeatureUtility.TryParse(sourceFeatureMask, out CustomRoadAssetSourceFeatures sourceFeatures))
            {
                detail = $"invalid-source-features {sourceFeatureMask}";
            }
            else
            {
                success = m_ReplacementPrefabMatcher != null &&
                          m_ReplacementPrefabMatcher.TryGetCompatibleTargetOptions(
                              sourcePrefabName,
                              sourceFeatures,
                              string.Empty,
                              targets,
                              MaxCustomRoadAssetDropdownOptions,
                              out sourceOption,
                              out detail);
            }

            if (!success)
            {
                sourceOption = default;
                detail = string.IsNullOrEmpty(detail)
                    ? "matcher-unavailable"
                    : detail;
            }

            Mod.LogDiagnostic($"[CustomRoadAssetMatch] Source select sourcePrefab={sourcePrefabName} sourceFeatures={sourceFeatureMask} success={success} source={FormatRoadAssetOption(sourceOption)} targets={targets.Count} cleaned={cleaned} detail={detail} targetSample={FormatRoadAssetOptionSample(targets)}.");
            return BuildSourceSelectionJson(
                sourcePrefabName,
                success,
                sourceOption,
                targets,
                cleaned,
                detail);
        }

        internal string SearchCustomRoadAssetTargetsJson(string sourcePrefabName, string query, string sourceFeatureMask)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("target-search");
            List<RoadAssetPrefabOption> targets = new List<RoadAssetPrefabOption>();
            RoadAssetPrefabOption sourceOption = default;
            string detail = "matcher-unavailable";
            bool success = false;
            if (!CustomRoadAssetSourceFeatureUtility.TryParse(sourceFeatureMask, out CustomRoadAssetSourceFeatures sourceFeatures))
            {
                detail = $"invalid-source-features {sourceFeatureMask}";
            }
            else
            {
                success = m_ReplacementPrefabMatcher != null &&
                          m_ReplacementPrefabMatcher.TryGetCompatibleTargetOptions(
                              sourcePrefabName,
                              sourceFeatures,
                              query,
                              targets,
                              MaxCustomRoadAssetDropdownOptions,
                              out sourceOption,
                              out detail);
            }

            if (!success)
            {
                sourceOption = default;
                detail = string.IsNullOrEmpty(detail)
                    ? "matcher-unavailable"
                    : detail;
            }

            Mod.LogDiagnostic($"[CustomRoadAssetMatch] Target search sourcePrefab={sourcePrefabName} query=\"{query ?? string.Empty}\" sourceFeatures={sourceFeatureMask} success={success} source={FormatRoadAssetOption(sourceOption)} targets={targets.Count} cleaned={cleaned} detail={detail} targetSample={FormatRoadAssetOptionSample(targets)}.");
            return BuildSourceSelectionJson(
                sourcePrefabName,
                success,
                sourceOption,
                targets,
                cleaned,
                detail);
        }

        internal string SetCustomRoadAssetMatchJson(
            string sourcePrefabName,
            string sourceFeatureMask,
            string targetPrefabName)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("set-before");
            CustomRoadAssetMatchRuleStore store = Mod.CustomRoadAssetMatchRules;
            if (store == null || m_ReplacementPrefabMatcher == null)
            {
                return BuildCustomRoadAssetMatchStateJson("set", cleaned, "rule-store-or-matcher-unavailable");
            }

            if (!CustomRoadAssetSourceFeatureUtility.TryParse(sourceFeatureMask, out CustomRoadAssetSourceFeatures sourceFeatures))
            {
                return BuildCustomRoadAssetMatchStateJson("set", cleaned, $"invalid-source-features {sourceFeatureMask}");
            }

            if (!m_ReplacementPrefabMatcher.IsValidCustomRoadAssetMatch(
                    sourcePrefabName,
                    sourceFeatures,
                    targetPrefabName,
                    out string validationDetail))
            {
                Mod.LogEssential($"[CustomRoadAssetMatch] Set rejected sourcePrefab={sourcePrefabName} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} targetPrefab={targetPrefabName} validation={validationDetail}.");
                return BuildCustomRoadAssetMatchStateJson("set", cleaned, $"invalid-rule {validationDetail}");
            }

            if (!store.SetRule(sourcePrefabName, sourceFeatures, targetPrefabName, out string setDetail))
            {
                return BuildCustomRoadAssetMatchStateJson("set", cleaned, setDetail);
            }

            int cleanedAfter = CleanInvalidCustomRoadAssetMatches("set-after");
            return BuildCustomRoadAssetMatchStateJson("set", cleaned + cleanedAfter, setDetail);
        }

        internal string DeleteCustomRoadAssetMatchJson(string sourcePrefabName, string sourceFeatureMask)
        {
            int cleaned = CleanInvalidCustomRoadAssetMatches("delete-before");
            CustomRoadAssetMatchRuleStore store = Mod.CustomRoadAssetMatchRules;
            string detail = "rule-store-unavailable";
            CustomRoadAssetSourceFeatures sourceFeatures = CustomRoadAssetSourceFeatures.None;
            if (!CustomRoadAssetSourceFeatureUtility.TryParse(sourceFeatureMask, out sourceFeatures))
            {
                detail = $"invalid-source-features {sourceFeatureMask}";
            }
            else if (store != null)
            {
                store.DeleteRule(sourcePrefabName, sourceFeatures, out detail);
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

            int canonicalized = store.CanonicalizeMandatorySourceFeatures(
                m_ReplacementPrefabMatcher.TryGetMandatoryCustomRoadAssetSourceFeatures,
                reason);
            int removed = store.CleanInvalidRules(
                m_ReplacementPrefabMatcher.IsValidCustomRoadAssetMatch,
                reason);
            return canonicalized + removed;
        }

        private string BuildCustomRoadAssetMatchStateJson(
            string action,
            int cleaned,
            string message)
        {
            List<CustomRoadAssetMatchRule> snapshot = Mod.CustomRoadAssetMatchRules?.GetSnapshot() ??
                                                      new List<CustomRoadAssetMatchRule>();
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
            foreach (CustomRoadAssetMatchRule rule in snapshot)
            {
                if (m_ReplacementPrefabMatcher == null ||
                    !m_ReplacementPrefabMatcher.TryGetRoadAssetPrefabOption(rule.SourcePrefabName, out RoadAssetPrefabOption source, out _) ||
                    !m_ReplacementPrefabMatcher.TryGetRoadAssetPrefabOption(rule.TargetPrefabName, out RoadAssetPrefabOption target, out _))
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
                AppendJsonProperty(builder, "sourceFeatureMask", (int)rule.SourceFeatures);
                builder.Append(',');
                AppendJsonProperty(builder, "sourceHasTram", HasSourceFeature(rule.SourceFeatures, CustomRoadAssetSourceFeatures.Tram));
                builder.Append(',');
                AppendJsonProperty(builder, "sourceHasPublicTransport", HasSourceFeature(rule.SourceFeatures, CustomRoadAssetSourceFeatures.PublicTransport));
                builder.Append(',');
                AppendJsonProperty(builder, "sourceIsReversed", HasSourceFeature(rule.SourceFeatures, CustomRoadAssetSourceFeatures.Reverse));
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
            builder.Append(',');
            AppendJsonProperty(builder, "hasTram", option.HasTram);
            builder.Append(',');
            AppendJsonProperty(builder, "hasPublicTransport", option.HasPublicTransport);
            builder.Append(',');
            AppendJsonProperty(builder, "canHaveTram", option.CanHaveTram);
            builder.Append(',');
            AppendJsonProperty(builder, "canHavePublicTransport", option.CanHavePublicTransport);
            builder.Append(',');
            AppendJsonProperty(builder, "hasAsymmetricRoadLanes", option.HasAsymmetricRoadLanes);
            builder.Append(',');
            AppendJsonProperty(builder, "hasReverseSourceSide", option.HasReverseSourceSide);
            builder.Append(',');
            AppendJsonProperty(builder, "hasForwardTargetCandidates", option.HasForwardTargetCandidates);
            builder.Append(',');
            AppendJsonProperty(builder, "hasReverseTargetCandidates", option.HasReverseTargetCandidates);
            builder.Append('}');
        }

        private static bool HasSourceFeature(
            CustomRoadAssetSourceFeatures features,
            CustomRoadAssetSourceFeatures feature)
        {
            return (CustomRoadAssetSourceFeatureUtility.Normalize(features) & feature) != 0;
        }

        private static string FormatRoadAssetOptionSample(List<RoadAssetPrefabOption> options)
        {
            if (options == null || options.Count == 0)
            {
                return "<none>";
            }

            StringBuilder builder = new StringBuilder();
            int appended = 0;
            for (int i = 0; i < options.Count && appended < 8; i++)
            {
                RoadAssetPrefabOption option = options[i];
                if (!option.HasTram &&
                    !option.HasPublicTransport &&
                    !option.CanHaveTram &&
                    !option.CanHavePublicTransport)
                {
                    continue;
                }

                if (appended > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(FormatRoadAssetOption(option));
                appended++;
            }

            if (appended == 0)
            {
                int count = Math.Min(options.Count, 5);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(" | ");
                    }

                    builder.Append(FormatRoadAssetOption(options[i]));
                }
            }

            if (options.Count > appended && appended > 0)
            {
                builder.Append($" | remaining={options.Count - appended}");
            }

            return builder.ToString();
        }

        private static string FormatRoadAssetOption(RoadAssetPrefabOption option)
        {
            if (string.IsNullOrEmpty(option.PrefabName))
            {
                return "<none>";
            }

            return $"{option.PrefabName} hasTram={option.HasTram} hasPT={option.HasPublicTransport} canHaveTram={option.CanHaveTram} canHavePT={option.CanHavePublicTransport} forwardTargets={option.HasForwardTargetCandidates} reverseTargets={option.HasReverseTargetCandidates} summary=({option.Summary})";
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
