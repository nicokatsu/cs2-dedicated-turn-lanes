using Game.Prefabs;

namespace PocketTurnLanes.Systems.UI
{
    public sealed class DedicatedTurnLanesToolEntryPrefab : PrefabBase
    {
        internal const string RoadsServicesCategoryName = "RoadsServices";
        internal const string DefaultIconUri = "coui://ui-mods/images/dedicated-turn-lanes-icon-default.svg";
        internal const int RoadsServicesPriority = 999999;

        internal static string PrefabName => $"{Mod.ModId}ToolEntry";
        internal static PrefabID PrefabId => new PrefabID(nameof(DedicatedTurnLanesToolEntryPrefab), PrefabName);
        internal static PrefabID RoadsServicesCategoryId => new PrefabID(nameof(UIAssetCategoryPrefab), RoadsServicesCategoryName);
    }
}
