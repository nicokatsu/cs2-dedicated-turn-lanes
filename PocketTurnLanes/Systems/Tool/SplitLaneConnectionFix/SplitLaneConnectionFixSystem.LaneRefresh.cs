using Colossal.Entities;
using Game.Common;
using Game.Net;
using Unity.Entities;
using NetEdge = Game.Net.Edge;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private void MarkForLaneRebuild(Request request)
        {
            int updatedNodes = MarkUpdatedIfExists(request.SplitNode, out bool splitAlreadyUpdated) ? 1 : 0;
            int alreadyUpdatedNodes = splitAlreadyUpdated ? 1 : 0;
            int updatedEdges = 0;
            int alreadyUpdatedEdges = 0;
            int scannedEdges = 0;

            if (EntityManager.TryGetBuffer(request.SplitNode, true, out DynamicBuffer<ConnectedEdge> connectedEdges))
            {
                scannedEdges = connectedEdges.Length;
                for (int i = 0; i < connectedEdges.Length; i++)
                {
                    Entity edgeEntity = connectedEdges[i].m_Edge;
                    if (edgeEntity == Entity.Null ||
                        !EntityManager.Exists(edgeEntity) ||
                        EntityManager.HasComponent<Deleted>(edgeEntity))
                    {
                        continue;
                    }

                    if (MarkUpdatedIfExists(edgeEntity, out bool edgeAlreadyUpdated))
                    {
                        updatedEdges++;
                    }
                    else if (edgeAlreadyUpdated)
                    {
                        alreadyUpdatedEdges++;
                    }

                    if (EntityManager.TryGetComponent(edgeEntity, out NetEdge edge))
                    {
                        Entity otherNode = edge.m_Start == request.SplitNode
                            ? edge.m_End
                            : edge.m_End == request.SplitNode
                                ? edge.m_Start
                                : Entity.Null;
                        if (MarkUpdatedIfExists(otherNode, out bool otherNodeAlreadyUpdated))
                        {
                            updatedNodes++;
                        }
                        else if (otherNodeAlreadyUpdated)
                        {
                            alreadyUpdatedNodes++;
                        }
                    }
                }
            }

            if (MarkUpdatedIfExists(request.PocketEdge, out bool pocketAlreadyUpdated))
            {
                updatedEdges++;
            }
            else if (pocketAlreadyUpdated)
            {
                alreadyUpdatedEdges++;
            }

            if (MarkUpdatedIfExists(request.OuterEdge, out bool outerAlreadyUpdated))
            {
                updatedEdges++;
            }
            else if (outerAlreadyUpdated)
            {
                alreadyUpdatedEdges++;
            }

            if (MarkUpdatedIfExists(request.OriginalEdge, out bool originalAlreadyUpdated))
            {
                updatedEdges++;
            }
            else if (originalAlreadyUpdated)
            {
                alreadyUpdatedEdges++;
            }

            Mod.LogDiagnostic($"[SplitLaneConnectionFix] Marked lane rebuild neighborhood splitNode={FormatEntity(request.SplitNode)} scannedConnectedEdges={scannedEdges} addedUpdatedNodes={updatedNodes} alreadyUpdatedNodes={alreadyUpdatedNodes} addedUpdatedEdges={updatedEdges} alreadyUpdatedEdges={alreadyUpdatedEdges} pocketEdge={FormatEntity(request.PocketEdge)} outerEdge={FormatEntity(request.OuterEdge)} originalEdge={FormatEntity(request.OriginalEdge)} laneRefreshOwners={m_LaneRefreshOwnerQuery.CalculateEntityCount()}.");
        }

        private bool MarkUpdatedIfExists(Entity entity)
        {
            return MarkUpdatedIfExists(entity, out _);
        }

        private bool MarkUpdatedIfExists(Entity entity, out bool alreadyUpdated)
        {
            alreadyUpdated = false;
            if (entity != Entity.Null &&
                EntityManager.Exists(entity))
            {
                alreadyUpdated = EntityManager.HasComponent<Updated>(entity);
                if (!alreadyUpdated)
                {
                    EntityManager.AddComponent<Updated>(entity);
                    return true;
                }
            }

            return false;
        }
    }
}
