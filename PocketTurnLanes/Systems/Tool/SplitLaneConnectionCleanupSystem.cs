using Game;

namespace PocketTurnLanes.Systems.Tool
{
    public partial class SplitLaneConnectionCleanupSystem : GameSystemBase
    {
        private SplitLaneConnectionFixSystem m_FixSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_FixSystem = World.GetOrCreateSystemManaged<SplitLaneConnectionFixSystem>();
            Mod.log.Info("[SplitLaneConnectionFix] Post-lane cleanup system created. It verifies and directly cleans split-node connector lanes after Traffic lane generation.");
        }

        protected override void OnUpdate()
        {
            m_FixSystem?.ProcessPostLaneGenerationCleanup();
        }
    }
}
