using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace PocketTurnLanes.Systems.Overlay
{
    public partial class IntersectionOverlaySystem : GameSystemBase
    {
        private static readonly Color HoverColor = new Color(0.1f, 0.75f, 1f, 0.35f);
        private readonly List<Vector3> m_Vertices = new List<Vector3>(64);
        private readonly List<Color> m_Colors = new List<Color>(64);
        private readonly List<int> m_Indices = new List<int>(192);

        private MaterialPropertyBlock m_Block;
        private Material m_Material;
        private Mesh m_Mesh;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Block = new MaterialPropertyBlock();
            m_Mesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = IndexFormat.UInt32
            };
            m_Mesh.MarkDynamic();

            Shader shader = Shader.Find("Sprites/Default") ??
                            Shader.Find("UI/Default") ??
                            Shader.Find("Hidden/InternalColored");
            if (shader == null)
            {
                Mod.log.Warn("[IntersectionOverlaySystem] No shader found; hover overlay will not render.");
            }

            m_Material = shader != null ? new Material(shader) : null;
            RenderPipelineManager.beginContextRendering += Render;
        }

        protected override void OnDestroy()
        {
            RenderPipelineManager.beginContextRendering -= Render;

            if (m_Mesh != null)
            {
                Object.Destroy(m_Mesh);
            }

            if (m_Material != null)
            {
                Object.Destroy(m_Material);
            }

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
        }

        public void ShowBounds(Bounds3 bounds)
        {
            Clear();
            AddBounds(bounds, HoverColor, 0.75f);
            Build();
        }

        public void Clear()
        {
            m_Mesh?.Clear();
            m_Vertices.Clear();
            m_Colors.Clear();
            m_Indices.Clear();
        }

        private void AddBounds(Bounds3 bounds, Color color, float scale)
        {
            Vector3 center = (bounds.min + bounds.max) * 0.5f;
            Vector3 radius = ((float3)center - bounds.min) * scale;
            radius.y = 0f;

            if (radius.sqrMagnitude < 4f)
            {
                radius = new Vector3(4f, 0f, 4f);
            }

            Quaternion step = Quaternion.AngleAxis(10f, Vector3.up);
            int startIndex = m_Vertices.Count;

            m_Vertices.Add(center + Vector3.up * 0.15f);
            m_Colors.Add(color);

            for (int i = 0; i < 36; i++)
            {
                radius = step * radius;
                m_Vertices.Add(center + radius + Vector3.up * 0.15f);
                m_Colors.Add(color);

                m_Indices.Add(startIndex);
                m_Indices.Add(startIndex + i + 1);
                m_Indices.Add(startIndex + (i == 35 ? 1 : i + 2));
            }
        }

        private void AddVertex(float3 position, Color color)
        {
            m_Vertices.Add(new Vector3(position.x, position.y, position.z));
            m_Colors.Add(color);
        }

        private void Build()
        {
            if (m_Mesh == null || m_Vertices.Count == 0)
            {
                return;
            }

            m_Mesh.Clear();
            m_Mesh.SetVertices(m_Vertices);
            m_Mesh.SetColors(m_Colors);
            m_Mesh.SetIndices(m_Indices, MeshTopology.Triangles, 0);
        }

        private void Render(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (m_Mesh == null || m_Material == null || m_Mesh.subMeshCount == 0)
            {
                return;
            }

            foreach (Camera camera in cameras)
            {
                if (camera.cameraType == CameraType.Game)
                {
                    Graphics.DrawMesh(m_Mesh, Matrix4x4.identity, m_Material, 0, camera, 0, m_Block, false, false);
                }
            }
        }
    }
}
