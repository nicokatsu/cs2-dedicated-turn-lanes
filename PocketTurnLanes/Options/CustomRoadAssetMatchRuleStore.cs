using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using Colossal.Json;
using PocketTurnLanes.Diagnostics;

namespace PocketTurnLanes.Options
{
    internal delegate bool CustomRoadAssetMatchRuleValidator(
        string sourcePrefabName,
        CustomRoadAssetSourceFeatures sourceFeatures,
        string targetPrefabName,
        out string detail);

    internal delegate bool CustomRoadAssetMandatorySourceFeatureProvider(
        string sourcePrefabName,
        out CustomRoadAssetSourceFeatures mandatoryFeatures,
        out string detail);

    internal sealed class CustomRoadAssetMatchRuleStore
    {
        private const string VersionedKeyPrefix = "v2|";

        private readonly DedicatedTurnLaneSettings m_Settings;
        private readonly Dictionary<RuleKey, string> m_Rules =
            new Dictionary<RuleKey, string>();

        internal CustomRoadAssetMatchRuleStore(DedicatedTurnLaneSettings settings)
        {
            m_Settings = settings;
            Reload();
        }

        internal int Count => m_Rules.Count;

        internal List<CustomRoadAssetMatchRule> GetSnapshot()
        {
            List<CustomRoadAssetMatchRule> snapshot = new List<CustomRoadAssetMatchRule>(m_Rules.Count);
            foreach (KeyValuePair<RuleKey, string> rule in m_Rules)
            {
                snapshot.Add(new CustomRoadAssetMatchRule(
                    rule.Key.SourcePrefabName,
                    rule.Key.SourceFeatures,
                    rule.Value));
            }

            return snapshot;
        }

        internal bool TryGetTargetPrefabName(string sourcePrefabName, out string targetPrefabName)
        {
            return TryGetTargetPrefabName(
                sourcePrefabName,
                CustomRoadAssetSourceFeatures.None,
                out targetPrefabName);
        }

        internal bool TryGetTargetPrefabName(
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures,
            out string targetPrefabName)
        {
            targetPrefabName = null;
            sourcePrefabName = NormalizePrefabName(sourcePrefabName);
            if (sourcePrefabName == null)
            {
                return false;
            }

            RuleKey key = new RuleKey(
                sourcePrefabName,
                CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures));
            return m_Rules.TryGetValue(key, out targetPrefabName) &&
                   !string.IsNullOrWhiteSpace(targetPrefabName);
        }

        internal string GetTargetPrefabName(string sourcePrefabName)
        {
            return TryGetTargetPrefabName(sourcePrefabName, out string targetPrefabName)
                ? targetPrefabName
                : null;
        }

        internal List<CustomRoadAssetMatchRule> GetCandidateRules(
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures)
        {
            List<CustomRoadAssetMatchRule> rules = new List<CustomRoadAssetMatchRule>(8);
            sourcePrefabName = NormalizePrefabName(sourcePrefabName);
            if (sourcePrefabName == null)
            {
                return rules;
            }

            sourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            bool hasReverse = (sourceFeatures & CustomRoadAssetSourceFeatures.Reverse) != 0;
            bool hasTram = (sourceFeatures & CustomRoadAssetSourceFeatures.Tram) != 0;
            bool hasPublicTransport = (sourceFeatures & CustomRoadAssetSourceFeatures.PublicTransport) != 0;

            TryAddCandidateRule(rules, sourcePrefabName, sourceFeatures);
            if (hasReverse)
            {
                if (hasTram && hasPublicTransport)
                {
                    TryAddCandidateRule(
                        rules,
                        sourcePrefabName,
                        CustomRoadAssetSourceFeatures.Reverse |
                        CustomRoadAssetSourceFeatures.Tram |
                        CustomRoadAssetSourceFeatures.PublicTransport);
                }

                if (hasTram)
                {
                    TryAddCandidateRule(
                        rules,
                        sourcePrefabName,
                        CustomRoadAssetSourceFeatures.Reverse |
                        CustomRoadAssetSourceFeatures.Tram);
                }

                if (hasPublicTransport)
                {
                    TryAddCandidateRule(
                        rules,
                        sourcePrefabName,
                        CustomRoadAssetSourceFeatures.Reverse |
                        CustomRoadAssetSourceFeatures.PublicTransport);
                }

                TryAddCandidateRule(rules, sourcePrefabName, CustomRoadAssetSourceFeatures.Reverse);
                return rules;
            }

            if (hasTram && hasPublicTransport)
            {
                TryAddCandidateRule(
                    rules,
                    sourcePrefabName,
                    CustomRoadAssetSourceFeatures.Tram |
                    CustomRoadAssetSourceFeatures.PublicTransport);
            }

            if (hasTram)
            {
                TryAddCandidateRule(rules, sourcePrefabName, CustomRoadAssetSourceFeatures.Tram);
            }

            if (hasPublicTransport)
            {
                TryAddCandidateRule(rules, sourcePrefabName, CustomRoadAssetSourceFeatures.PublicTransport);
            }

            if (sourceFeatures != CustomRoadAssetSourceFeatures.None)
            {
                TryAddCandidateRule(rules, sourcePrefabName, CustomRoadAssetSourceFeatures.None);
            }

            return rules;
        }

        internal bool SetRule(
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures,
            string targetPrefabName,
            out string detail)
        {
            detail = string.Empty;
            sourcePrefabName = NormalizePrefabName(sourcePrefabName);
            targetPrefabName = NormalizePrefabName(targetPrefabName);
            sourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            if (sourcePrefabName == null || targetPrefabName == null)
            {
                detail = "sourcePrefabName or targetPrefabName is empty";
                return false;
            }

            RuleKey key = new RuleKey(sourcePrefabName, sourceFeatures);
            bool replaced = m_Rules.ContainsKey(key);
            m_Rules[key] = targetPrefabName;
            Save($"set sourcePrefab={sourcePrefabName} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)} targetPrefab={targetPrefabName} replaced={replaced}");
            detail = replaced ? "replaced" : "added";
            return true;
        }

        internal bool DeleteRule(
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures,
            out string detail)
        {
            detail = string.Empty;
            sourcePrefabName = NormalizePrefabName(sourcePrefabName);
            sourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            if (sourcePrefabName == null)
            {
                detail = "sourcePrefabName is empty";
                return false;
            }

            RuleKey key = new RuleKey(sourcePrefabName, sourceFeatures);
            if (!m_Rules.Remove(key))
            {
                detail = "not-found";
                return false;
            }

            Save($"delete sourcePrefab={sourcePrefabName} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(sourceFeatures)}");
            detail = "deleted";
            return true;
        }

        internal int CleanInvalidRules(CustomRoadAssetMatchRuleValidator validator, string reason)
        {
            if (validator == null || m_Rules.Count == 0)
            {
                return 0;
            }

            List<RuleKey> invalidRules = null;
            foreach (KeyValuePair<RuleKey, string> rule in m_Rules)
            {
                if (validator(
                        rule.Key.SourcePrefabName,
                        rule.Key.SourceFeatures,
                        rule.Value,
                        out string detail))
                {
                    continue;
                }

                invalidRules ??= new List<RuleKey>();
                invalidRules.Add(rule.Key);
                ModLogger.LogEssential($"[CustomRoadAssetMatch] Removing invalid rule sourcePrefab={rule.Key.SourcePrefabName} sourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(rule.Key.SourceFeatures)} targetPrefab={rule.Value} reason={reason} validation={detail}.");
            }

            if (invalidRules == null)
            {
                return 0;
            }

            for (int i = 0; i < invalidRules.Count; i++)
            {
                m_Rules.Remove(invalidRules[i]);
            }

            Save($"clean-invalid reason={reason} removed={invalidRules.Count}");
            return invalidRules.Count;
        }

        internal int CanonicalizeMandatorySourceFeatures(
            CustomRoadAssetMandatorySourceFeatureProvider mandatoryFeatureProvider,
            string reason)
        {
            if (mandatoryFeatureProvider == null || m_Rules.Count == 0)
            {
                return 0;
            }

            Dictionary<RuleKey, CanonicalRulePlan> canonicalRules =
                new Dictionary<RuleKey, CanonicalRulePlan>(m_Rules.Count);
            int changed = 0;
            foreach (KeyValuePair<RuleKey, string> rule in m_Rules)
            {
                RuleKey canonicalKey = rule.Key;
                CustomRoadAssetSourceFeatures mandatoryFeatures = CustomRoadAssetSourceFeatures.None;
                string mandatoryDetail = string.Empty;
                if (mandatoryFeatureProvider(
                        rule.Key.SourcePrefabName,
                        out mandatoryFeatures,
                        out mandatoryDetail))
                {
                    mandatoryFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(mandatoryFeatures) &
                                        (CustomRoadAssetSourceFeatures.Tram |
                                         CustomRoadAssetSourceFeatures.PublicTransport);
                    canonicalKey = new RuleKey(
                        rule.Key.SourcePrefabName,
                        rule.Key.SourceFeatures | mandatoryFeatures);
                }

                bool isChanged = !rule.Key.Equals(canonicalKey);
                int preferenceScore = GetCanonicalRulePreferenceScore(rule.Key.SourceFeatures, isChanged);
                CanonicalRulePlan plan = new CanonicalRulePlan(
                    rule.Key,
                    rule.Value,
                    preferenceScore);

                if (!canonicalRules.TryGetValue(canonicalKey, out CanonicalRulePlan existingPlan))
                {
                    canonicalRules.Add(canonicalKey, plan);
                    if (isChanged)
                    {
                        changed++;
                        ModLogger.LogEssential($"[CustomRoadAssetMatch] Canonicalizing rule sourcePrefab={rule.Key.SourcePrefabName} oldSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(rule.Key.SourceFeatures)} newSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(canonicalKey.SourceFeatures)} mandatorySourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(mandatoryFeatures)} targetPrefab={rule.Value} reason={reason} detail={mandatoryDetail}.");
                    }

                    continue;
                }

                if (preferenceScore > existingPlan.PreferenceScore)
                {
                    canonicalRules[canonicalKey] = plan;
                    changed++;
                    ModLogger.LogEssential($"[CustomRoadAssetMatch] Removing duplicate canonical rule sourcePrefab={existingPlan.OriginalKey.SourcePrefabName} oldSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(existingPlan.OriginalKey.SourceFeatures)} canonicalSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(canonicalKey.SourceFeatures)} removedTargetPrefab={existingPlan.TargetPrefabName} keptTargetPrefab={rule.Value} reason={reason} keptRuleWasMoreSpecific=true.");
                    if (isChanged)
                    {
                        ModLogger.LogEssential($"[CustomRoadAssetMatch] Canonicalizing rule sourcePrefab={rule.Key.SourcePrefabName} oldSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(rule.Key.SourceFeatures)} newSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(canonicalKey.SourceFeatures)} mandatorySourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(mandatoryFeatures)} targetPrefab={rule.Value} reason={reason} detail={mandatoryDetail}.");
                    }
                }
                else
                {
                    changed++;
                    ModLogger.LogEssential($"[CustomRoadAssetMatch] Removing duplicate canonical rule sourcePrefab={rule.Key.SourcePrefabName} oldSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(rule.Key.SourceFeatures)} canonicalSourceFeatures={CustomRoadAssetSourceFeatureUtility.Format(canonicalKey.SourceFeatures)} removedTargetPrefab={rule.Value} keptTargetPrefab={existingPlan.TargetPrefabName} reason={reason} keptRuleWasMoreSpecific={existingPlan.PreferenceScore > preferenceScore}.");
                }
            }

            if (changed == 0)
            {
                return 0;
            }

            m_Rules.Clear();
            foreach (KeyValuePair<RuleKey, CanonicalRulePlan> rule in canonicalRules)
            {
                m_Rules.Add(rule.Key, rule.Value.TargetPrefabName);
            }

            Save($"canonicalize-mandatory-source-features reason={reason} changed={changed}");
            return changed;
        }

        private void TryAddCandidateRule(
            List<CustomRoadAssetMatchRule> rules,
            string sourcePrefabName,
            CustomRoadAssetSourceFeatures sourceFeatures)
        {
            sourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i].SourceFeatures == sourceFeatures)
                {
                    return;
                }
            }

            if (!TryGetTargetPrefabName(sourcePrefabName, sourceFeatures, out string targetPrefabName))
            {
                return;
            }

            rules.Add(new CustomRoadAssetMatchRule(sourcePrefabName, sourceFeatures, targetPrefabName));
        }

        private void Reload()
        {
            m_Rules.Clear();
            string raw = m_Settings?.CustomRoadAssetMatchesJson;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            try
            {
                Dictionary<string, string> loaded = JSON.Load(raw).Make<Dictionary<string, string>>();
                if (loaded == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, string> rule in loaded)
                {
                    if (!TryDecodeRuleKey(rule.Key, out RuleKey key))
                    {
                        continue;
                    }

                    string target = NormalizePrefabName(rule.Value);
                    if (target == null)
                    {
                        continue;
                    }

                    m_Rules[key] = target;
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogException(ex, $"[CustomRoadAssetMatch] Failed to parse stored rule JSON; resetting rules. rawLength={raw.Length}.");
                Save("reset-invalid-json");
            }
        }

        private void Save(string reason)
        {
            if (m_Settings == null)
            {
                return;
            }

            m_Settings.CustomRoadAssetMatchesJson = SerializeRules();
            ModLogger.LogEssential($"[CustomRoadAssetMatch] Rules saved reason={reason} count={m_Rules.Count} settingsAssetName=\"{DedicatedTurnLaneSettings.SettingsAssetName}\".");
            AssetDatabase.global.SaveSpecificSetting(DedicatedTurnLaneSettings.SettingsAssetName).ContinueWith(
                task => ModLogger.LogException(task.Exception, "[CustomRoadAssetMatch] Failed to save custom road asset match rules."),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private string SerializeRules()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<RuleKey, string> rule in m_Rules)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                AppendJsonString(builder, EncodeRuleKey(rule.Key));
                builder.Append(':');
                AppendJsonString(builder, rule.Value);
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static bool TryDecodeRuleKey(string rawKey, out RuleKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                return false;
            }

            if (!rawKey.StartsWith(VersionedKeyPrefix, StringComparison.Ordinal))
            {
                string legacySource = NormalizePrefabName(rawKey);
                if (legacySource == null)
                {
                    return false;
                }

                key = new RuleKey(legacySource, CustomRoadAssetSourceFeatures.None);
                return true;
            }

            int maskStart = VersionedKeyPrefix.Length;
            int maskEnd = rawKey.IndexOf('|', maskStart);
            if (maskEnd < 0 ||
                !int.TryParse(rawKey.Substring(maskStart, maskEnd - maskStart), out int mask))
            {
                return false;
            }

            string source = NormalizePrefabName(rawKey.Substring(maskEnd + 1));
            if (source == null)
            {
                return false;
            }

            key = new RuleKey(
                source,
                CustomRoadAssetSourceFeatureUtility.Normalize((CustomRoadAssetSourceFeatures)mask));
            return true;
        }

        private static string EncodeRuleKey(RuleKey key)
        {
            return $"{VersionedKeyPrefix}{(int)key.SourceFeatures}|{key.SourcePrefabName}";
        }

        private static string NormalizePrefabName(string prefabName)
        {
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                return null;
            }

            return prefabName.Trim();
        }

        private static void AppendJsonString(StringBuilder builder, string value)
        {
            builder.Append('"');
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

            builder.Append('"');
        }

        private static int GetCanonicalRulePreferenceScore(
            CustomRoadAssetSourceFeatures sourceFeatures,
            bool isChanged)
        {
            int score = isChanged ? 0 : 1000;
            if ((sourceFeatures & CustomRoadAssetSourceFeatures.Tram) != 0)
            {
                score += 4;
            }

            if ((sourceFeatures & CustomRoadAssetSourceFeatures.PublicTransport) != 0)
            {
                score += 2;
            }

            if ((sourceFeatures & CustomRoadAssetSourceFeatures.Reverse) != 0)
            {
                score += 1;
            }

            return score;
        }

        private readonly struct CanonicalRulePlan
        {
            public CanonicalRulePlan(
                RuleKey originalKey,
                string targetPrefabName,
                int preferenceScore)
            {
                OriginalKey = originalKey;
                TargetPrefabName = targetPrefabName;
                PreferenceScore = preferenceScore;
            }

            public RuleKey OriginalKey { get; }

            public string TargetPrefabName { get; }

            public int PreferenceScore { get; }
        }

        private readonly struct RuleKey : IEquatable<RuleKey>
        {
            public RuleKey(string sourcePrefabName, CustomRoadAssetSourceFeatures sourceFeatures)
            {
                SourcePrefabName = sourcePrefabName;
                SourceFeatures = CustomRoadAssetSourceFeatureUtility.Normalize(sourceFeatures);
            }

            public string SourcePrefabName { get; }

            public CustomRoadAssetSourceFeatures SourceFeatures { get; }

            public bool Equals(RuleKey other)
            {
                return string.Equals(SourcePrefabName, other.SourcePrefabName, StringComparison.Ordinal) &&
                       SourceFeatures == other.SourceFeatures;
            }

            public override bool Equals(object obj)
            {
                return obj is RuleKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((SourcePrefabName != null ? SourcePrefabName.GetHashCode() : 0) * 397) ^
                           (int)SourceFeatures;
                }
            }
        }
    }
}
