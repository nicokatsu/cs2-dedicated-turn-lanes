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

        private struct PendingToolCommandState
        {
            public PendingToolCommand Command;
            public string Reason;
            public bool SwitchToDefaultTool;
            public bool DeferredFromToolChanged;
            public string QueuedActiveTool;

            public bool IsEmpty => Command == PendingToolCommand.None;

            public string ProcessingReason => string.IsNullOrEmpty(Reason)
                ? "tool disabled"
                : Reason;

            public string QueuedActiveToolOrDefault => string.IsNullOrEmpty(QueuedActiveTool)
                ? "<null>"
                : QueuedActiveTool;

            public static PendingToolCommandState Create(
                PendingToolCommand command,
                string reason,
                bool switchToDefaultTool,
                bool deferredFromToolChanged,
                string queuedActiveTool)
            {
                return new PendingToolCommandState
                {
                    Command = command,
                    Reason = reason,
                    SwitchToDefaultTool = switchToDefaultTool,
                    DeferredFromToolChanged = deferredFromToolChanged,
                    QueuedActiveTool = queuedActiveTool
                };
            }
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

            m_PendingToolCommandState = PendingToolCommandState.Create(
                command,
                reason,
                switchToDefaultTool,
                deferredFromToolChanged,
                m_ToolSystem?.activeTool?.toolID ?? "<null>");
            Mod.LogDiagnostic($"[IntersectionTool] Queued tool command command={command} reason={reason} switchToDefaultTool={switchToDefaultTool} deferredFromToolChanged={deferredFromToolChanged} isEnabled={IsToolEnabled} activeTool={m_PendingToolCommandState.QueuedActiveToolOrDefault} {GetToolExitSnapshot()}.");
        }

        private bool ProcessPendingToolCommand(ref JobHandle result)
        {
            PendingToolCommandState pendingCommand = m_PendingToolCommandState;
            if (pendingCommand.IsEmpty)
            {
                return false;
            }

            m_PendingToolCommandState = default;
            Mod.LogDiagnostic($"[IntersectionTool] Processing queued tool command command={pendingCommand.Command} reason={pendingCommand.ProcessingReason} switchToDefaultTool={pendingCommand.SwitchToDefaultTool} deferredFromToolChanged={pendingCommand.DeferredFromToolChanged} queuedActiveTool={pendingCommand.QueuedActiveToolOrDefault} currentActiveTool={m_ToolSystem?.activeTool?.toolID ?? "<null>"} isEnabled={IsToolEnabled} {GetToolExitSnapshot()}.");

            if (pendingCommand.Command == PendingToolCommand.Enable)
            {
                EnableTool();
                return false;
            }

            result = DisableTool(
                result,
                pendingCommand.ProcessingReason,
                pendingCommand.SwitchToDefaultTool,
                pendingCommand.DeferredFromToolChanged);
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
