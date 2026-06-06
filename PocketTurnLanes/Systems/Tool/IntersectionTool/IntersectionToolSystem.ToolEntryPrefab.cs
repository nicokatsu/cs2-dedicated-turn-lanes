using Game.Prefabs;
using PocketTurnLanes.Systems.UI;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private DedicatedTurnLanesToolEntryPrefabSystem m_ToolEntryPrefabSystem;

        private void InitializeToolEntryPrefabSystem()
        {
            m_ToolEntryPrefabSystem = World.GetOrCreateSystemManaged<DedicatedTurnLanesToolEntryPrefabSystem>();
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            if (m_ToolEntryPrefabSystem == null ||
                !m_ToolEntryPrefabSystem.IsToolEntryPrefab(prefab))
            {
                return false;
            }

            QueuePendingToolCommand(PendingToolCommand.Enable, "tool entry prefab selected");
            Mod.LogEssential($"[ToolEntryPrefab] Selected tool entry prefab={prefab?.GetPrefabID()} isEnabled={IsToolEnabled} activeTool={m_ToolSystem?.activeTool?.toolID ?? "<null>"}.");
            return true;
        }

        public override PrefabBase GetPrefab()
        {
            if (!IsToolEntrySelectionActive())
            {
                return null;
            }

            return m_ToolEntryPrefabSystem?.GetToolEntryPrefab();
        }

        private bool IsToolEntrySelectionActive()
        {
            return IsToolEnabled ||
                   m_PendingToolCommand == PendingToolCommand.Enable;
        }
    }
}
