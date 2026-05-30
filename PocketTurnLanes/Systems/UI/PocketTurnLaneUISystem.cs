using Colossal.UI.Binding;
using Game.UI;
using PocketTurnLanes.Systems.Tool;

namespace PocketTurnLanes.Systems.UI
{
    public partial class PocketTurnLaneUISystem : UISystemBase
    {
        private const string BindingGroup = Mod.BindingGroup;

        private IntersectionToolSystem m_IntersectionToolSystem;
        private ValueBinding<bool> m_ToolEnabledBinding;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_IntersectionToolSystem = World.GetOrCreateSystemManaged<IntersectionToolSystem>();
            m_IntersectionToolSystem.ToolEnabledChanged += OnToolEnabledChanged;

            AddBinding(m_ToolEnabledBinding = new ValueBinding<bool>(BindingGroup, "ToolEnabled", false));
            AddBinding(new TriggerBinding(BindingGroup, "ToggleTool", ToggleTool));
        }

        protected override void OnDestroy()
        {
            if (m_IntersectionToolSystem != null)
            {
                m_IntersectionToolSystem.ToolEnabledChanged -= OnToolEnabledChanged;
            }

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (m_ToolEnabledBinding.value != m_IntersectionToolSystem.IsToolEnabled)
            {
                m_ToolEnabledBinding.Update(m_IntersectionToolSystem.IsToolEnabled);
            }
        }

        private void ToggleTool()
        {
            if (m_IntersectionToolSystem.IsToolEnabled)
            {
                m_IntersectionToolSystem.DisableTool();
            }
            else
            {
                m_IntersectionToolSystem.EnableTool();
            }

            m_ToolEnabledBinding.Update(m_IntersectionToolSystem.IsToolEnabled);
        }

        private void OnToolEnabledChanged(bool enabled)
        {
            m_ToolEnabledBinding?.Update(enabled);
        }
    }
}
