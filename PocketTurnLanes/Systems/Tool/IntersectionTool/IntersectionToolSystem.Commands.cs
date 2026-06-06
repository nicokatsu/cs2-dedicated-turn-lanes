using Unity.Jobs;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolSystem
    {
        private enum PendingToolCommand
        {
            None,
            Enable,
            Disable
        }

        internal void RequestToggleTool()
        {
            PendingToolCommand command = IsToolEnabled
                ? PendingToolCommand.Disable
                : PendingToolCommand.Enable;
            QueuePendingToolCommand(command, "ui toggle");
        }

        private void EnableTool()
        {
            m_ToolSystem.activeTool = this;
            if (SetToolEnabled(true))
            {
                SetVanillaMutationSystemsEnabled(false);
                Mod.LogEssential("[IntersectionTool] Enabled.");
            }
        }

        private void DisableTool()
        {
            DisableTool(m_LastToolUpdateJobHandle, "tool disabled");
        }

        private JobHandle DisableTool(JobHandle inputDeps, string reason)
        {
            bool wasEnabled = IsToolEnabled;
            JobHandle result = ClearDefinitionsAndResetForToolExit(inputDeps, reason, true, out string cleanupDetail);
            if (wasEnabled)
            {
                Mod.LogEssential($"[IntersectionTool] Disabled. {cleanupDetail}");
            }

            return result;
        }

        private void QueuePendingToolCommand(PendingToolCommand command, string reason)
        {
            if (command == PendingToolCommand.None)
            {
                return;
            }

            m_PendingToolCommand = command;
            Mod.LogDiagnostic($"[IntersectionTool] Queued tool command command={command} reason={reason} isEnabled={IsToolEnabled} activeTool={m_ToolSystem?.activeTool?.toolID ?? "<null>"}.");
        }

        private bool ProcessPendingToolCommand(ref JobHandle result)
        {
            PendingToolCommand command = m_PendingToolCommand;
            if (command == PendingToolCommand.None)
            {
                return false;
            }

            m_PendingToolCommand = PendingToolCommand.None;
            Mod.LogDiagnostic($"[IntersectionTool] Processing queued tool command command={command} isEnabled={IsToolEnabled} activeTool={m_ToolSystem?.activeTool?.toolID ?? "<null>"}.");

            if (command == PendingToolCommand.Enable)
            {
                EnableTool();
                return false;
            }

            result = DisableTool(result, "tool disabled");
            return true;
        }
    }
}
