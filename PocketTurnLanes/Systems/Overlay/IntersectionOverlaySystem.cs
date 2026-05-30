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
        private static readonly Color PreviewSegmentColor = new Color(1f, 0.78f, 0.08f, 0.45f);

        public struct PreviewSegment
        {
            public float3 Start;
            public float3 End;
            public float Width;

            public PreviewSegment(float3 start, float3 end, float width)
            {
                Start = start;
                End = end;
                Width = width;
            }
        }

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

        public void ShowBoundsAndSegments(Bounds3 bounds, IReadOnlyList<PreviewSegment> segments)
        {
            Clear();
            AddBounds(bounds, HoverColor, 0.75f);
            for (int i = 0; i < segments.Count; i++)
            {
                AddSegment(segments[i], PreviewSegmentColor);
            }

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

        private void AddSegment(PreviewSegment segment, Color color)
        {
            float3 delta = segment.End - segment.Start;
            float2 direction = delta.xz;
            float lengthSq = math.lengthsq(direction);
            if (lengthSq <= 0.01f)
            {
                return;
            }

            direction *= math.rsqrt(lengthSq);
            float halfWidth = math.max(1f, segment.Width * 0.5f);
            float2 side = new float2(-direction.y, direction.x) * halfWidth;
            float3 up = new float3(0f, 0.2f, 0f);

            int startIndex = m_Vertices.Count;
            AddVertex(segment.Start + new float3(side.x, 0f, side.y) + up, color);
            AddVertex(segment.Start - new float3(side.x, 0f, side.y) + up, color);
            AddVertex(segment.End - new float3(side.x, 0f, side.y) + up, color);
            AddVertex(segment.End + new float3(side.x, 0f, side.y) + up, color);

            m_Indices.Add(startIndex);
            m_Indices.Add(startIndex + 1);
            m_Indices.Add(startIndex + 2);
            m_Indices.Add(startIndex);
            m_Indices.Add(startIndex + 2);
            m_Indices.Add(startIndex + 3);
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
