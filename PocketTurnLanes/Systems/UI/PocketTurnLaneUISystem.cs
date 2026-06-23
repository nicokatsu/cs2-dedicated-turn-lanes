using Colossal.UI.Binding;
using Game.UI;
using PocketTurnLanes.Systems.Tool.IntersectionTool;

namespace PocketTurnLanes.Systems.UI
{
    public partial class PocketTurnLaneUISystem : UISystemBase
    {
        private const string BindingGroup = Mod.BindingGroup;

        private IntersectionToolSystem m_IntersectionToolSystem;
        private ValueBinding<bool> m_ToolEnabledBinding;
        private ValueBinding<string> m_CustomRoadAssetMatchStateBinding;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_IntersectionToolSystem = World.GetOrCreateSystemManaged<IntersectionToolSystem>();
            m_IntersectionToolSystem.ToolEnabledChanged += OnToolEnabledChanged;

            AddBinding(m_ToolEnabledBinding = new ValueBinding<bool>(BindingGroup, "ToolEnabled", false));
            AddBinding(m_CustomRoadAssetMatchStateBinding = new ValueBinding<string>(BindingGroup, "CustomRoadAssetMatchState", "{}"));
            AddBinding(new TriggerBinding(BindingGroup, "ToggleTool", ToggleTool));
            AddBinding(new CallBinding<string, string>(BindingGroup, "SearchCustomRoadAssetSources", SearchCustomRoadAssetSources, new StringReader()));
            AddBinding(new CallBinding<string, string, string>(BindingGroup, "SelectCustomRoadAssetSource", SelectCustomRoadAssetSource, new StringReader(), new StringReader()));
            AddBinding(new CallBinding<string, string, string, string>(BindingGroup, "SearchCustomRoadAssetTargets", SearchCustomRoadAssetTargets, new StringReader(), new StringReader(), new StringReader()));
            AddBinding(new CallBinding<string, string, string, string>(BindingGroup, "SetCustomRoadAssetMatch", SetCustomRoadAssetMatch, new StringReader(), new StringReader(), new StringReader()));
            AddBinding(new CallBinding<string, string, string>(BindingGroup, "DeleteCustomRoadAssetMatch", DeleteCustomRoadAssetMatch, new StringReader(), new StringReader()));
            RefreshCustomRoadAssetMatchStateBinding();
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
            SyncToolEnabledBinding();
        }

        private void ToggleTool()
        {
            m_IntersectionToolSystem.RequestToggleTool();
        }

        private string SearchCustomRoadAssetSources(string query)
        {
            string result = m_IntersectionToolSystem.SearchCustomRoadAssetSourcesJson(query);
            RefreshCustomRoadAssetMatchStateBinding();
            return result;
        }

        private string SelectCustomRoadAssetSource(string sourcePrefabName, string sourceFeatureMask)
        {
            string result = m_IntersectionToolSystem.SelectCustomRoadAssetSourceJson(sourcePrefabName, sourceFeatureMask);
            RefreshCustomRoadAssetMatchStateBinding();
            return result;
        }

        private string SearchCustomRoadAssetTargets(string sourcePrefabName, string query, string sourceFeatureMask)
        {
            string result = m_IntersectionToolSystem.SearchCustomRoadAssetTargetsJson(sourcePrefabName, query, sourceFeatureMask);
            RefreshCustomRoadAssetMatchStateBinding();
            return result;
        }

        private string SetCustomRoadAssetMatch(string sourcePrefabName, string targetPrefabName, string sourceFeatureMask)
        {
            string result = m_IntersectionToolSystem.SetCustomRoadAssetMatchJson(sourcePrefabName, sourceFeatureMask, targetPrefabName);
            UpdateCustomRoadAssetMatchStateBinding(result);
            return result;
        }

        private string DeleteCustomRoadAssetMatch(string sourcePrefabName, string sourceFeatureMask)
        {
            string result = m_IntersectionToolSystem.DeleteCustomRoadAssetMatchJson(sourcePrefabName, sourceFeatureMask);
            UpdateCustomRoadAssetMatchStateBinding(result);
            return result;
        }

        private void SyncToolEnabledBinding()
        {
            if (m_IntersectionToolSystem == null)
            {
                return;
            }

            UpdateToolEnabledBinding(m_IntersectionToolSystem.IsToolEnabled);
        }

        private void UpdateToolEnabledBinding(bool enabled)
        {
            if (m_ToolEnabledBinding == null ||
                m_ToolEnabledBinding.value == enabled)
            {
                return;
            }

            m_ToolEnabledBinding.Update(enabled);
        }

        private void OnToolEnabledChanged(bool enabled)
        {
            UpdateToolEnabledBinding(enabled);
            if (enabled)
            {
                RefreshCustomRoadAssetMatchStateBinding();
            }
        }

        private void RefreshCustomRoadAssetMatchStateBinding()
        {
            if (m_IntersectionToolSystem == null)
            {
                return;
            }

            UpdateCustomRoadAssetMatchStateBinding(m_IntersectionToolSystem.GetCustomRoadAssetMatchStateJson());
        }

        private void UpdateCustomRoadAssetMatchStateBinding(string stateJson)
        {
            if (m_CustomRoadAssetMatchStateBinding == null ||
                m_CustomRoadAssetMatchStateBinding.value == stateJson)
            {
                return;
            }

            m_CustomRoadAssetMatchStateBinding.Update(stateJson);
        }
    }
}
