using PocketTurnLanes.Tool.Traffic;

namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryGetTrafficApi(out TrafficApi trafficApi, out string error)
        {
            if (m_TrafficApi != null)
            {
                trafficApi = m_TrafficApi;
                error = string.Empty;
                return true;
            }

            if (TrafficApi.TryCreate(out m_TrafficApi, out error))
            {
                trafficApi = m_TrafficApi;
                Mod.UpdateTrafficRuntimeStatus(true, "none", 0);
                Mod.LogEssential("[SplitLaneConnectionFix] Traffic runtime detected; connection repair is enabled.");
                return true;
            }

            trafficApi = null;
            return false;
        }
    }
}
