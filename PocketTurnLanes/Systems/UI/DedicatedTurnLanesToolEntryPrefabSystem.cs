using System;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;

namespace PocketTurnLanes.Systems.UI
{
    public partial class DedicatedTurnLanesToolEntryPrefabSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private DedicatedTurnLanesToolEntryPrefab m_ToolEntryPrefab;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            Mod.LogDiagnostic($"[ToolEntryPrefab] System created prefabName={DedicatedTurnLanesToolEntryPrefab.PrefabName} category={DedicatedTurnLanesToolEntryPrefab.RoadsServicesCategoryId} icon={DedicatedTurnLanesToolEntryPrefab.DefaultIconUri} priority={DedicatedTurnLanesToolEntryPrefab.RoadsServicesPriority}.");
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            EnsureToolEntryPrefabRegistered($"gamePreload purpose={purpose} mode={mode}");
        }

        protected override void OnUpdate()
        {
        }

        internal PrefabBase GetToolEntryPrefab()
        {
            if (m_ToolEntryPrefab != null)
            {
                return m_ToolEntryPrefab;
            }

            if (m_PrefabSystem != null &&
                m_PrefabSystem.TryGetPrefab(DedicatedTurnLanesToolEntryPrefab.PrefabId, out PrefabBase prefab) &&
                IsToolEntryPrefab(prefab))
            {
                m_ToolEntryPrefab = (DedicatedTurnLanesToolEntryPrefab)prefab;
            }

            return m_ToolEntryPrefab;
        }

        internal bool IsToolEntryPrefab(PrefabBase prefab)
        {
            return prefab is DedicatedTurnLanesToolEntryPrefab &&
                   string.Equals(prefab.name, DedicatedTurnLanesToolEntryPrefab.PrefabName, StringComparison.Ordinal);
        }

        private void EnsureToolEntryPrefabRegistered(string reason)
        {
            if (GetToolEntryPrefab() != null)
            {
                Mod.LogDiagnostic($"[ToolEntryPrefab] Already registered prefab={DedicatedTurnLanesToolEntryPrefab.PrefabId} reason={reason}.");
                return;
            }

            if (m_PrefabSystem.TryGetPrefab(DedicatedTurnLanesToolEntryPrefab.PrefabId, out PrefabBase duplicatePrefab))
            {
                Mod.LogEssential($"[ToolEntryPrefab] Cannot register tool entry because prefab id already exists id={DedicatedTurnLanesToolEntryPrefab.PrefabId} existingType={duplicatePrefab.GetType().FullName} existingName={duplicatePrefab.name} reason={reason}.");
                return;
            }

            if (!m_PrefabSystem.TryGetPrefab(DedicatedTurnLanesToolEntryPrefab.RoadsServicesCategoryId, out PrefabBase categoryBase))
            {
                Mod.LogEssential($"[ToolEntryPrefab] Cannot register tool entry because target category was not found category={DedicatedTurnLanesToolEntryPrefab.RoadsServicesCategoryId} reason={reason}.");
                return;
            }

            UIAssetCategoryPrefab categoryPrefab = categoryBase as UIAssetCategoryPrefab;
            if (categoryPrefab == null)
            {
                Mod.LogEssential($"[ToolEntryPrefab] Cannot register tool entry because target category has unexpected type category={DedicatedTurnLanesToolEntryPrefab.RoadsServicesCategoryId} actualType={categoryBase.GetType().FullName} actualName={categoryBase.name} reason={reason}.");
                return;
            }

            DedicatedTurnLanesToolEntryPrefab entryPrefab =
                PrefabBase.Create<DedicatedTurnLanesToolEntryPrefab>(DedicatedTurnLanesToolEntryPrefab.PrefabName);
            entryPrefab.active = true;
            entryPrefab.prefab = entryPrefab;

            UIObject uiObject = entryPrefab.AddComponent<UIObject>();
            uiObject.active = true;
            uiObject.name = $"{DedicatedTurnLanesToolEntryPrefab.PrefabName} UIObject";
            uiObject.m_Group = categoryPrefab;
            uiObject.m_Priority = DedicatedTurnLanesToolEntryPrefab.RoadsServicesPriority;
            uiObject.m_Icon = DedicatedTurnLanesToolEntryPrefab.DefaultIconUri;
            uiObject.m_IsDebugObject = false;

            if (!m_PrefabSystem.AddPrefab(entryPrefab))
            {
                Mod.LogEssential($"[ToolEntryPrefab] PrefabSystem.AddPrefab failed prefab={DedicatedTurnLanesToolEntryPrefab.PrefabId} category={DedicatedTurnLanesToolEntryPrefab.RoadsServicesCategoryId} icon={DedicatedTurnLanesToolEntryPrefab.DefaultIconUri} reason={reason}.");
                return;
            }

            m_ToolEntryPrefab = entryPrefab;
            Mod.LogEssential($"[ToolEntryPrefab] Registered tool entry prefab={DedicatedTurnLanesToolEntryPrefab.PrefabId} category={DedicatedTurnLanesToolEntryPrefab.RoadsServicesCategoryId} icon={DedicatedTurnLanesToolEntryPrefab.DefaultIconUri} priority={DedicatedTurnLanesToolEntryPrefab.RoadsServicesPriority} reason={reason}.");
        }
    }
}
