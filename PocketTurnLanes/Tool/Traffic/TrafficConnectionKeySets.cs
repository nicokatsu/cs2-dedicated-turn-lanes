using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficConnectionKeySets
    {
        public static HashSet<ConnectionKey> FromMappings(IReadOnlyList<LaneMapping> mappings)
        {
            HashSet<ConnectionKey> result = new HashSet<ConnectionKey>();
            if (mappings == null)
            {
                return result;
            }

            for (int i = 0; i < mappings.Count; i++)
            {
                result.Add(new ConnectionKey(mappings[i].SourceLaneIndex, mappings[i].TargetLaneIndex));
            }

            return result;
        }

        public static HashSet<ConnectionKey> FromConnectors(IReadOnlyList<ConnectorLane> connectors)
        {
            HashSet<ConnectionKey> result = new HashSet<ConnectionKey>();
            if (connectors == null)
            {
                return result;
            }

            for (int i = 0; i < connectors.Count; i++)
            {
                ConnectorLane connector = connectors[i];
                result.Add(new ConnectionKey(connector.SourceLaneIndex, connector.TargetLaneIndex));
            }

            return result;
        }
    }
}
