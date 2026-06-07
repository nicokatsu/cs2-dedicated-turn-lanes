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
            return DisableTool(inputDeps, reason, true, false);
        }

        private JobHandle DisableTool(
            JobHandle inputDeps,
            string reason,
            bool switchToDefaultTool,
            bool deferredFromToolChanged)
        {
            bool wasEnabled = IsToolEnabled;
            JobHandle result = ClearDefinitionsAndResetForToolExit(inputDeps, reason, switchToDefaultTool, out string cleanupDetail);
            if (wasEnabled)
            {
                string prefix = deferredFromToolChanged
                    ? "Disabled after deferred active-tool cleanup"
                    : "Disabled";
                Mod.LogEssential($"[IntersectionTool] {prefix}. deferredFromToolChanged={deferredFromToolChanged} {cleanupDetail}");
            }

            return result;
        }

        private void QueuePendingToolCommand(PendingToolCommand command, string reason)
        {
            QueuePendingToolCommand(command, reason, command == PendingToolCommand.Disable, false);
        }

        private void QueuePendingToolCommand(
            PendingToolCommand command,
            string reason,
            bool switchToDefaultTool,
            bool deferredFromToolChanged)
        {
            if (command == PendingToolCommand.None)
            {
                return;
            }

            m_PendingToolCommand = command;
            m_PendingToolCommandReason = reason;
            m_PendingToolCommandSwitchToDefaultTool = switchToDefaultTool;
            m_PendingToolCommandDeferredFromToolChanged = deferredFromToolChanged;
            m_PendingToolCommandQueuedActiveTool = m_ToolSystem?.activeTool?.toolID ?? "<null>";
            Mod.LogDiagnostic($"[IntersectionTool] Queued tool command command={command} reason={reason} switchToDefaultTool={switchToDefaultTool} deferredFromToolChanged={deferredFromToolChanged} isEnabled={IsToolEnabled} activeTool={m_PendingToolCommandQueuedActiveTool} {GetToolExitSnapshot()}.");
        }

        private bool ProcessPendingToolCommand(ref JobHandle result)
        {
            PendingToolCommand command = m_PendingToolCommand;
            if (command == PendingToolCommand.None)
            {
                return false;
            }

            string reason = string.IsNullOrEmpty(m_PendingToolCommandReason)
                ? "tool disabled"
                : m_PendingToolCommandReason;
            bool switchToDefaultTool = m_PendingToolCommandSwitchToDefaultTool;
            bool deferredFromToolChanged = m_PendingToolCommandDeferredFromToolChanged;
            string queuedActiveTool = string.IsNullOrEmpty(m_PendingToolCommandQueuedActiveTool)
                ? "<null>"
                : m_PendingToolCommandQueuedActiveTool;

            m_PendingToolCommand = PendingToolCommand.None;
            m_PendingToolCommandReason = null;
            m_PendingToolCommandSwitchToDefaultTool = false;
            m_PendingToolCommandDeferredFromToolChanged = false;
            m_PendingToolCommandQueuedActiveTool = null;
            Mod.LogDiagnostic($"[IntersectionTool] Processing queued tool command command={command} reason={reason} switchToDefaultTool={switchToDefaultTool} deferredFromToolChanged={deferredFromToolChanged} queuedActiveTool={queuedActiveTool} currentActiveTool={m_ToolSystem?.activeTool?.toolID ?? "<null>"} isEnabled={IsToolEnabled} {GetToolExitSnapshot()}.");

            if (command == PendingToolCommand.Enable)
            {
                EnableTool();
                return false;
            }

            result = DisableTool(result, reason, switchToDefaultTool, deferredFromToolChanged);
            return true;
        }

        private string GetToolExitSnapshot()
        {
            int definitionCount = CalculateEntityCountSafe(m_DefinitionQuery);
            int replacementDefinitionCount = CalculateEntityCountSafe(m_ReplacementPreviewDefinitionQuery);
            return $"hadPreviewState={HasPreviewState()} definitions={definitionCount} replacementPreviewDefinitions={replacementDefinitionCount} hovered={FormatEntity(m_HoveredIntersection)} previewNode={FormatEntity(m_PreviewIntersection)} previewEdge={FormatEntity(m_PreviewEdge)} previewEdges={m_PreviewEdgeCount}";
        }
    }
}
