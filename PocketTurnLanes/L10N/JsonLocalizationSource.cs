using System.Collections.Generic;
using Colossal;

namespace PocketTurnLanes.L10N
{
    internal sealed class JsonLocalizationSource : IDictionarySource
    {
        private readonly IReadOnlyDictionary<string, string> m_Localization;

        public JsonLocalizationSource(IReadOnlyDictionary<string, string> localization)
        {
            m_Localization = localization;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return m_Localization;
        }

        public void Unload()
        {
        }
    }
}
