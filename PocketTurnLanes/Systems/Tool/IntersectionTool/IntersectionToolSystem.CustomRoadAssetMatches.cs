using System.Collections.Generic;
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
            Mod.LogDiagnostic($"[CustomRoadAssetMatch] Source search query=\"{query ?? string.Empty}\" options={options.Count} cleaned={cleaned} sample={CustomRoadAssetMatchJson.FormatRoadAssetOptionSample(options)}.");
            return CustomRoadAssetMatchJson.BuildOptionList("source-search", query, options, cleaned, string.Empty);
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

            Mod.LogDiagnostic($"[CustomRoadAssetMatch] Source select sourcePrefab={sourcePrefabName} sourceFeatures={sourceFeatureMask} success={success} source={CustomRoadAssetMatchJson.FormatRoadAssetOption(sourceOption)} targets={targets.Count} cleaned={cleaned} detail={detail} targetSample={CustomRoadAssetMatchJson.FormatRoadAssetOptionSample(targets)}.");
            return CustomRoadAssetMatchJson.BuildSourceSelection(
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

            Mod.LogDiagnostic($"[CustomRoadAssetMatch] Target search sourcePrefab={sourcePrefabName} query=\"{query ?? string.Empty}\" sourceFeatures={sourceFeatureMask} success={success} source={CustomRoadAssetMatchJson.FormatRoadAssetOption(sourceOption)} targets={targets.Count} cleaned={cleaned} detail={detail} targetSample={CustomRoadAssetMatchJson.FormatRoadAssetOptionSample(targets)}.");
            return CustomRoadAssetMatchJson.BuildSourceSelection(
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
            return CustomRoadAssetMatchJson.BuildState(
                m_ReplacementPrefabMatcher,
                Mod.CustomRoadAssetMatchRules?.GetSnapshot(),
                action,
                cleaned,
                message);
        }

    }
}
