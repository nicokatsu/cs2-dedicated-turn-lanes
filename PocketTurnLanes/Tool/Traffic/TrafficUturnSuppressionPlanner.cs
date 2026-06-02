using System.Collections.Generic;

namespace PocketTurnLanes.Tool.Traffic
{
    internal static class TrafficUturnSuppressionPlanner
    {
        public static HashSet<SourceLaneKey> BuildStaleSourceKeys(IReadOnlyList<ConnectorLane> staleUturns)
        {
            HashSet<SourceLaneKey> sourceKeys = new HashSet<SourceLaneKey>();
            if (staleUturns == null)
            {
                return sourceKeys;
            }

            for (int i = 0; i < staleUturns.Count; i++)
            {
                ConnectorLane connector = staleUturns[i];
                sourceKeys.Add(new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex));
            }

            return sourceKeys;
        }

        public static int CountRuntimeNonUturnConnections(
            SourceLaneKey sourceKey,
            IReadOnlyList<ConnectorLane> connectors)
        {
            int count = 0;
            if (connectors == null)
            {
                return count;
            }

            for (int i = 0; i < connectors.Count; i++)
            {
                ConnectorLane connector = connectors[i];
                if (connector.SourceEdge == sourceKey.Edge &&
                    connector.SourceLaneIndex == sourceKey.LaneIndex &&
                    connector.TargetEdge != sourceKey.Edge)
                {
                    count++;
                }
            }

            return count;
        }

        public static int CountRuntimeNonUturnConnections(
            HashSet<SourceLaneKey> sourceKeys,
            IReadOnlyList<ConnectorLane> connectors)
        {
            int count = 0;
            if (sourceKeys == null ||
                sourceKeys.Count == 0 ||
                connectors == null)
            {
                return count;
            }

            for (int i = 0; i < connectors.Count; i++)
            {
                ConnectorLane connector = connectors[i];
                SourceLaneKey sourceKey = new SourceLaneKey(connector.SourceEdge, connector.SourceLaneIndex);
                if (sourceKeys.Contains(sourceKey) &&
                    connector.TargetEdge != sourceKey.Edge)
                {
                    count++;
                }
            }

            return count;
        }

        public static void EnsureTrafficPlanSource(
            Dictionary<SourceLaneKey, Dictionary<TargetLaneKey, LaneMapping>> bySource,
            SourceLaneKey sourceKey)
        {
            if (!bySource.ContainsKey(sourceKey))
            {
                bySource.Add(sourceKey, new Dictionary<TargetLaneKey, LaneMapping>());
            }
        }
    }
}
