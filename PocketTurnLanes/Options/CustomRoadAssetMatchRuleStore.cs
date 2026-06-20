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
        string targetPrefabName,
        out string detail);

    internal sealed class CustomRoadAssetMatchRuleStore
    {
        private readonly DedicatedTurnLaneSettings m_Settings;
        private readonly Dictionary<string, string> m_Rules =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal CustomRoadAssetMatchRuleStore(DedicatedTurnLaneSettings settings)
        {
            m_Settings = settings;
            Reload();
        }

        internal int Count => m_Rules.Count;

        internal Dictionary<string, string> GetSnapshot()
        {
            return new Dictionary<string, string>(m_Rules, StringComparer.Ordinal);
        }

        internal bool TryGetTargetPrefabName(string sourcePrefabName, out string targetPrefabName)
        {
            targetPrefabName = null;
            if (string.IsNullOrWhiteSpace(sourcePrefabName))
            {
                return false;
            }

            return m_Rules.TryGetValue(sourcePrefabName.Trim(), out targetPrefabName) &&
                   !string.IsNullOrWhiteSpace(targetPrefabName);
        }

        internal string GetTargetPrefabName(string sourcePrefabName)
        {
            return TryGetTargetPrefabName(sourcePrefabName, out string targetPrefabName)
                ? targetPrefabName
                : null;
        }

        internal bool SetRule(string sourcePrefabName, string targetPrefabName, out string detail)
        {
            detail = string.Empty;
            sourcePrefabName = NormalizePrefabName(sourcePrefabName);
            targetPrefabName = NormalizePrefabName(targetPrefabName);
            if (sourcePrefabName == null || targetPrefabName == null)
            {
                detail = "sourcePrefabName or targetPrefabName is empty";
                return false;
            }

            bool replaced = m_Rules.ContainsKey(sourcePrefabName);
            m_Rules[sourcePrefabName] = targetPrefabName;
            Save($"set sourcePrefab={sourcePrefabName} targetPrefab={targetPrefabName} replaced={replaced}");
            detail = replaced ? "replaced" : "added";
            return true;
        }

        internal bool DeleteRule(string sourcePrefabName, out string detail)
        {
            detail = string.Empty;
            sourcePrefabName = NormalizePrefabName(sourcePrefabName);
            if (sourcePrefabName == null)
            {
                detail = "sourcePrefabName is empty";
                return false;
            }

            if (!m_Rules.Remove(sourcePrefabName))
            {
                detail = "not-found";
                return false;
            }

            Save($"delete sourcePrefab={sourcePrefabName}");
            detail = "deleted";
            return true;
        }

        internal int CleanInvalidRules(CustomRoadAssetMatchRuleValidator validator, string reason)
        {
            if (validator == null || m_Rules.Count == 0)
            {
                return 0;
            }

            List<string> invalidSources = null;
            foreach (KeyValuePair<string, string> rule in m_Rules)
            {
                if (validator(rule.Key, rule.Value, out string detail))
                {
                    continue;
                }

                invalidSources ??= new List<string>();
                invalidSources.Add(rule.Key);
                ModLogger.LogEssential($"[CustomRoadAssetMatch] Removing invalid rule sourcePrefab={rule.Key} targetPrefab={rule.Value} reason={reason} validation={detail}.");
            }

            if (invalidSources == null)
            {
                return 0;
            }

            for (int i = 0; i < invalidSources.Count; i++)
            {
                m_Rules.Remove(invalidSources[i]);
            }

            Save($"clean-invalid reason={reason} removed={invalidSources.Count}");
            return invalidSources.Count;
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
                    string source = NormalizePrefabName(rule.Key);
                    string target = NormalizePrefabName(rule.Value);
                    if (source == null || target == null)
                    {
                        continue;
                    }

                    m_Rules[source] = target;
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
            foreach (KeyValuePair<string, string> rule in m_Rules)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                AppendJsonString(builder, rule.Key);
                builder.Append(':');
                AppendJsonString(builder, rule.Value);
            }

            builder.Append('}');
            return builder.ToString();
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
    }
}
