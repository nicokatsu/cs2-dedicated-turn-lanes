using System;
using System.Collections.Generic;
using System.Text;
using PocketTurnLanes.Tool.Json;
using PocketTurnLanes.Tool.PrefabMatching;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    internal static class CustomRoadAssetMatchJson
    {
        public static string BuildState(
            ReplacementPrefabMatcher matcher,
            List<CustomRoadAssetMatchRule> snapshot,
            string action,
            int cleaned,
            string message)
        {
            snapshot ??= new List<CustomRoadAssetMatchRule>();
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            JsonStringBuilder.AppendProperty(builder, "schemaVersion", 1);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "action", action);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "cleanedInvalidRules", cleaned);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "message", message ?? string.Empty);
            builder.Append(',');
            builder.Append("\"rules\":[");

            bool first = true;
            int appended = 0;
            foreach (CustomRoadAssetMatchRule rule in snapshot)
            {
                if (matcher == null ||
                    !matcher.TryGetRoadAssetPrefabOption(rule.SourcePrefabName, out RoadAssetPrefabOption source, out _) ||
                    !matcher.TryGetRoadAssetPrefabOption(rule.TargetPrefabName, out RoadAssetPrefabOption target, out _))
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
                JsonStringBuilder.AppendProperty(builder, "sourceFeatureMask", (int)rule.SourceFeatures);
                builder.Append(',');
                JsonStringBuilder.AppendProperty(builder, "sourceHasTram", HasSourceFeature(rule.SourceFeatures, CustomRoadAssetSourceFeatures.Tram));
                builder.Append(',');
                JsonStringBuilder.AppendProperty(builder, "sourceHasPublicTransport", HasSourceFeature(rule.SourceFeatures, CustomRoadAssetSourceFeatures.PublicTransport));
                builder.Append(',');
                JsonStringBuilder.AppendProperty(builder, "sourceIsReversed", HasSourceFeature(rule.SourceFeatures, CustomRoadAssetSourceFeatures.Reverse));
                builder.Append(',');
                builder.Append("\"target\":");
                AppendOption(builder, target);
                builder.Append('}');
            }

            builder.Append(']');
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "ruleCount", appended);
            builder.Append('}');
            return builder.ToString();
        }

        public static string BuildOptionList(
            string action,
            string query,
            List<RoadAssetPrefabOption> options,
            int cleaned,
            string message)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            JsonStringBuilder.AppendProperty(builder, "schemaVersion", 1);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "action", action);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "query", query ?? string.Empty);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "cleanedInvalidRules", cleaned);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "message", message ?? string.Empty);
            builder.Append(',');
            builder.Append("\"items\":");
            AppendOptions(builder, options);
            builder.Append('}');
            return builder.ToString();
        }

        public static string BuildSourceSelection(
            string sourcePrefabName,
            bool success,
            RoadAssetPrefabOption source,
            List<RoadAssetPrefabOption> targets,
            int cleaned,
            string detail)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            JsonStringBuilder.AppendProperty(builder, "schemaVersion", 1);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "action", "source-select");
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "success", success);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "sourcePrefabName", sourcePrefabName ?? string.Empty);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "cleanedInvalidRules", cleaned);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "message", detail ?? string.Empty);
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

        public static string FormatRoadAssetOptionSample(List<RoadAssetPrefabOption> options)
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

        public static string FormatRoadAssetOption(RoadAssetPrefabOption option)
        {
            if (string.IsNullOrEmpty(option.PrefabName))
            {
                return "<none>";
            }

            return $"{option.PrefabName} lanes={option.ForwardRoadLanes}/{option.BackwardRoadLanes} totalLanes={option.TotalRoadLanes} hasTram={option.HasTram} hasPT={option.HasPublicTransport} canHaveTram={option.CanHaveTram} canHavePT={option.CanHavePublicTransport} forwardTargets={option.HasForwardTargetCandidates} reverseTargets={option.HasReverseTargetCandidates} summary=({option.Summary})";
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
            JsonStringBuilder.AppendProperty(builder, "prefabName", option.PrefabName ?? string.Empty);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "displayName", option.DisplayName ?? string.Empty);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "icon", option.Icon ?? string.Empty);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "summary", option.Summary ?? string.Empty);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "forwardRoadLanes", option.ForwardRoadLanes);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "backwardRoadLanes", option.BackwardRoadLanes);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "totalRoadLanes", option.TotalRoadLanes);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "isDlc", option.IsDlc);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "contentDetail", option.ContentDetail ?? string.Empty);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "hasTram", option.HasTram);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "hasPublicTransport", option.HasPublicTransport);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "canHaveTram", option.CanHaveTram);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "canHavePublicTransport", option.CanHavePublicTransport);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "hasAsymmetricRoadLanes", option.HasAsymmetricRoadLanes);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "hasReverseSourceSide", option.HasReverseSourceSide);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "hasForwardTargetCandidates", option.HasForwardTargetCandidates);
            builder.Append(',');
            JsonStringBuilder.AppendProperty(builder, "hasReverseTargetCandidates", option.HasReverseTargetCandidates);
            builder.Append('}');
        }

        private static bool HasSourceFeature(
            CustomRoadAssetSourceFeatures features,
            CustomRoadAssetSourceFeatures feature)
        {
            return (CustomRoadAssetSourceFeatureUtility.Normalize(features) & feature) != 0;
        }
    }

}
