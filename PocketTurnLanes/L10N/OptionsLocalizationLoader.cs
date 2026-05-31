using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Colossal.Json;
using Game.SceneFlow;
using PocketTurnLanes.Diagnostics;

namespace PocketTurnLanes.L10N
{
    internal static class OptionsLocalizationLoader
    {
        private const string ResourceMarker = ".L10N.lang.";
        private const string ResourceSuffix = ".json";

        public static int LoadFromAssembly(Assembly assembly)
        {
            int loadedCount = 0;
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                string localeId = GetLocalizationLocaleId(resourceName);
                if (localeId == null)
                {
                    continue;
                }

                if (TryLoadResource(assembly, resourceName, localeId))
                {
                    loadedCount++;
                }
            }

            if (loadedCount == 0)
            {
                ModLogger.LogEssential("[Settings] No embedded localization JSON files found under L10N/lang; Options UI may show localization keys.");
            }

            return loadedCount;
        }

        private static bool TryLoadResource(Assembly assembly, string resourceName, string localeId)
        {
            try
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        ModLogger.LogEssential($"[Settings] Localization resource stream missing resource={resourceName}.");
                        return false;
                    }

                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string json = reader.ReadToEnd();
                        Dictionary<string, string> translations = JSON.Load(json).Make<Dictionary<string, string>>();
                        if (translations == null)
                        {
                            throw new InvalidDataException("Localization JSON did not parse to a string dictionary.");
                        }

                        GameManager.instance.localizationManager.AddSource(localeId, new JsonLocalizationSource(translations));
                        ModLogger.LogDiagnostic($"[Settings] Loaded localization locale={localeId} resource={resourceName} entries={translations.Count}.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogException(ex, $"[Settings] Failed to load localization resource={resourceName}.");
                return false;
            }
        }

        private static string GetLocalizationLocaleId(string resourceName)
        {
            int markerIndex = resourceName.IndexOf(ResourceMarker, StringComparison.Ordinal);
            if (markerIndex < 0 || !resourceName.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int start = markerIndex + ResourceMarker.Length;
            int length = resourceName.Length - start - ResourceSuffix.Length;
            return length > 0 ? resourceName.Substring(start, length) : null;
        }
    }
}
