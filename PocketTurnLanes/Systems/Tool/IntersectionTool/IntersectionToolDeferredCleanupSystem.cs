using System;
using Game;

namespace PocketTurnLanes.Systems.Tool.IntersectionTool
{
    public partial class IntersectionToolDeferredCleanupSystem : GameSystemBase
    {
        private IntersectionToolSystem m_IntersectionToolSystem;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_IntersectionToolSystem = World.GetOrCreateSystemManaged<IntersectionToolSystem>();
            Mod.LogDiagnostic("[IntersectionTool] Deferred active-tool cleanup runner created. It runs in ToolUpdate so ToolOutputBarrier command buffers are valid.");
        }

        protected override void OnUpdate()
        {
            try
            {
                m_IntersectionToolSystem?.ProcessDeferredToolChangedCleanupFromToolUpdate();
            }
            catch (Exception ex)
            {
                Mod.LogException(ex, "[IntersectionTool] Deferred active-tool cleanup runner failed before the intersection tool handled the cleanup.");
            }
        }
    }
}
