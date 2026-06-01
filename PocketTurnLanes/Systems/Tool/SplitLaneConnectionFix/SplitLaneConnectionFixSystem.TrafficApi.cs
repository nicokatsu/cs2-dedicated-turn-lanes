using System;
using System.Linq;
using System.Reflection;
using Game.Pathfind;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
namespace PocketTurnLanes.Systems.Tool.SplitLaneConnectionFix
{
    public partial class SplitLaneConnectionFixSystem
    {
        private bool TryGetTrafficApi(out TrafficApi trafficApi, out string error)
        {
            if (m_TrafficApi != null)
            {
                trafficApi = m_TrafficApi;
                error = string.Empty;
                return true;
            }

            if (TrafficApi.TryCreate(out m_TrafficApi, out error))
            {
                trafficApi = m_TrafficApi;
                Mod.UpdateTrafficRuntimeStatus(true, "none", 0);
                Mod.LogEssential("[SplitLaneConnectionFix] Traffic runtime detected; connection repair is enabled.");
                return true;
            }

            trafficApi = null;
            return false;
        }

        private sealed class TrafficApi
        {
            private readonly Type m_ModifiedConnectionsType;
            private readonly Type m_DataOwnerType;
            private readonly Type m_ModifiedLaneConnectionsType;
            private readonly Type m_GeneratedConnectionType;
            private readonly Entity m_FakePrefabRef;
            private readonly MethodInfo m_AddModifiedLaneConnectionsBuffer;
            private readonly MethodInfo m_GetModifiedLaneConnectionsBuffer;
            private readonly MethodInfo m_HasModifiedLaneConnectionsBuffer;
            private readonly MethodInfo m_AddGeneratedConnectionBuffer;
            private readonly MethodInfo m_GetGeneratedConnectionBuffer;
            private readonly MethodInfo m_HasGeneratedConnectionBuffer;
            private readonly MethodInfo m_SetDataOwner;
            private readonly FieldInfo m_DataOwnerEntityField;
            private readonly FieldInfo m_ModifiedLaneIndexField;
            private readonly FieldInfo m_ModifiedCarriagewayAndGroupField;
            private readonly FieldInfo m_ModifiedLanePositionField;
            private readonly FieldInfo m_ModifiedEdgeField;
            private readonly FieldInfo m_ModifiedConnectionsField;
            private readonly FieldInfo m_GeneratedSourceField;
            private readonly FieldInfo m_GeneratedTargetField;
            private readonly FieldInfo m_GeneratedLaneIndexMapField;
            private readonly FieldInfo m_GeneratedCarriagewayAndGroupIndexMapField;
            private readonly FieldInfo m_GeneratedLanePositionMapField;
            private readonly FieldInfo m_GeneratedMethodField;
            private readonly FieldInfo m_GeneratedUnsafeField;

            private TrafficApi(
                Type modifiedConnectionsType,
                Type dataOwnerType,
                Type modifiedLaneConnectionsType,
                Type generatedConnectionType,
                Entity fakePrefabRef)
            {
                m_ModifiedConnectionsType = modifiedConnectionsType;
                m_DataOwnerType = dataOwnerType;
                m_ModifiedLaneConnectionsType = modifiedLaneConnectionsType;
                m_GeneratedConnectionType = generatedConnectionType;
                m_FakePrefabRef = fakePrefabRef;

                m_AddModifiedLaneConnectionsBuffer = MakeEntityManagerGeneric(nameof(EntityManager.AddBuffer), modifiedLaneConnectionsType, typeof(Entity));
                m_GetModifiedLaneConnectionsBuffer = MakeEntityManagerGeneric(nameof(EntityManager.GetBuffer), modifiedLaneConnectionsType, typeof(Entity), typeof(bool));
                m_HasModifiedLaneConnectionsBuffer = MakeEntityManagerGeneric(nameof(EntityManager.HasBuffer), modifiedLaneConnectionsType, typeof(Entity));
                m_AddGeneratedConnectionBuffer = MakeEntityManagerGeneric(nameof(EntityManager.AddBuffer), generatedConnectionType, typeof(Entity));
                m_GetGeneratedConnectionBuffer = MakeEntityManagerGeneric(nameof(EntityManager.GetBuffer), generatedConnectionType, typeof(Entity), typeof(bool));
                m_HasGeneratedConnectionBuffer = MakeEntityManagerGeneric(nameof(EntityManager.HasBuffer), generatedConnectionType, typeof(Entity));
                m_SetDataOwner = MakeEntityManagerGeneric(nameof(EntityManager.SetComponentData), dataOwnerType, typeof(Entity), dataOwnerType);

                m_DataOwnerEntityField = RequireField(dataOwnerType, "entity");
                m_ModifiedLaneIndexField = RequireField(modifiedLaneConnectionsType, "laneIndex");
                m_ModifiedCarriagewayAndGroupField = RequireField(modifiedLaneConnectionsType, "carriagewayAndGroup");
                m_ModifiedLanePositionField = RequireField(modifiedLaneConnectionsType, "lanePosition");
                m_ModifiedEdgeField = RequireField(modifiedLaneConnectionsType, "edgeEntity");
                m_ModifiedConnectionsField = RequireField(modifiedLaneConnectionsType, "modifiedConnections");
                m_GeneratedSourceField = RequireField(generatedConnectionType, "sourceEntity");
                m_GeneratedTargetField = RequireField(generatedConnectionType, "targetEntity");
                m_GeneratedLaneIndexMapField = RequireField(generatedConnectionType, "laneIndexMap");
                m_GeneratedCarriagewayAndGroupIndexMapField = RequireField(generatedConnectionType, "carriagewayAndGroupIndexMap");
                m_GeneratedLanePositionMapField = RequireField(generatedConnectionType, "lanePositionMap");
                m_GeneratedMethodField = RequireField(generatedConnectionType, "method");
                m_GeneratedUnsafeField = RequireField(generatedConnectionType, "isUnsafe");
            }

            public static bool TryCreate(out TrafficApi api, out string error)
            {
                api = null;
                error = string.Empty;

                Type modifiedConnectionsType = FindType("Traffic.Components.ModifiedConnections");
                Type dataOwnerType = FindType("Traffic.Components.DataOwner");
                Type modifiedLaneConnectionsType = FindType("Traffic.Components.LaneConnections.ModifiedLaneConnections");
                Type generatedConnectionType = FindType("Traffic.Components.LaneConnections.GeneratedConnection");
                Type modDefaultsSystemType = FindType("Traffic.Systems.ModDefaultsSystem");

                if (modifiedConnectionsType == null ||
                    dataOwnerType == null ||
                    modifiedLaneConnectionsType == null ||
                    generatedConnectionType == null ||
                    modDefaultsSystemType == null)
                {
                    error = $"missingTypes modifiedConnections={modifiedConnectionsType != null} dataOwner={dataOwnerType != null} modifiedLaneConnections={modifiedLaneConnectionsType != null} generatedConnection={generatedConnectionType != null} modDefaults={modDefaultsSystemType != null}";
                    return false;
                }

                FieldInfo fakePrefabRefField = modDefaultsSystemType.GetField("FakePrefabRef", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (fakePrefabRefField == null)
                {
                    error = "missing Traffic.Systems.ModDefaultsSystem.FakePrefabRef";
                    return false;
                }

                Entity fakePrefabRef = (Entity)fakePrefabRefField.GetValue(null);
                if (fakePrefabRef == Entity.Null)
                {
                    error = "Traffic FakePrefabRef is Entity.Null; Traffic defaults have not initialized yet";
                    return false;
                }

                try
                {
                    api = new TrafficApi(
                        modifiedConnectionsType,
                        dataOwnerType,
                        modifiedLaneConnectionsType,
                        generatedConnectionType,
                        fakePrefabRef);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public object GetOrAddModifiedLaneConnectionsBuffer(EntityManager entityManager, Entity node)
            {
                bool hasBuffer = (bool)m_HasModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node });
                return hasBuffer
                    ? m_GetModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node, false })
                    : m_AddModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node });
            }

            public bool HasModifiedLaneConnectionsBuffer(EntityManager entityManager, Entity node)
            {
                return (bool)m_HasModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node });
            }

            public object GetModifiedLaneConnectionsBuffer(EntityManager entityManager, Entity node, bool readOnly)
            {
                return m_GetModifiedLaneConnectionsBuffer.Invoke(entityManager, new object[] { node, readOnly });
            }

            public object AddGeneratedConnectionBuffer(EntityManager entityManager, Entity entity)
            {
                return m_AddGeneratedConnectionBuffer.Invoke(entityManager, new object[] { entity });
            }

            public bool HasGeneratedConnectionBuffer(EntityManager entityManager, Entity entity)
            {
                return (bool)m_HasGeneratedConnectionBuffer.Invoke(entityManager, new object[] { entity });
            }

            public object GetGeneratedConnectionBuffer(EntityManager entityManager, Entity entity, bool readOnly)
            {
                return m_GetGeneratedConnectionBuffer.Invoke(entityManager, new object[] { entity, readOnly });
            }

            public int GetBufferLength(object buffer)
            {
                return (int)buffer.GetType().GetProperty("Length").GetValue(buffer);
            }

            public object GetBufferItem(object buffer, int index)
            {
                return buffer.GetType().GetProperty("Item").GetValue(buffer, new object[] { index });
            }

            public void ClearBuffer(object buffer)
            {
                buffer.GetType().GetMethod("Clear").Invoke(buffer, null);
            }

            public void AddBufferElement(object buffer, object element)
            {
                buffer.GetType().GetMethod("Add").Invoke(buffer, new[] { element });
            }

            public Entity GetModifiedConnectionEdge(object element)
            {
                return (Entity)m_ModifiedEdgeField.GetValue(element);
            }

            public int GetModifiedConnectionLaneIndex(object element)
            {
                return (int)m_ModifiedLaneIndexField.GetValue(element);
            }

            public int2 GetModifiedConnectionCarriagewayAndGroup(object element)
            {
                return (int2)m_ModifiedCarriagewayAndGroupField.GetValue(element);
            }

            public float3 GetModifiedConnectionLanePosition(object element)
            {
                return (float3)m_ModifiedLanePositionField.GetValue(element);
            }

            public Entity GetModifiedConnectionEntity(object element)
            {
                return (Entity)m_ModifiedConnectionsField.GetValue(element);
            }

            public Entity GetGeneratedConnectionSource(object element)
            {
                return (Entity)m_GeneratedSourceField.GetValue(element);
            }

            public Entity GetGeneratedConnectionTarget(object element)
            {
                return (Entity)m_GeneratedTargetField.GetValue(element);
            }

            public int2 GetGeneratedConnectionLaneIndexMap(object element)
            {
                return (int2)m_GeneratedLaneIndexMapField.GetValue(element);
            }

            public int4 GetGeneratedConnectionCarriagewayAndGroupIndexMap(object element)
            {
                return (int4)m_GeneratedCarriagewayAndGroupIndexMapField.GetValue(element);
            }

            public float3x2 GetGeneratedConnectionLanePositionMap(object element)
            {
                return (float3x2)m_GeneratedLanePositionMapField.GetValue(element);
            }

            public PathMethod GetGeneratedConnectionMethod(object element)
            {
                return (PathMethod)m_GeneratedMethodField.GetValue(element);
            }

            public bool GetGeneratedConnectionUnsafe(object element)
            {
                return (bool)m_GeneratedUnsafeField.GetValue(element);
            }

            public object CreateModifiedLaneConnection(
                int laneIndex,
                int2 carriagewayAndGroup,
                float3 lanePosition,
                Entity edgeEntity,
                Entity modifiedConnections)
            {
                object value = Activator.CreateInstance(m_ModifiedLaneConnectionsType);
                m_ModifiedLaneIndexField.SetValue(value, laneIndex);
                m_ModifiedCarriagewayAndGroupField.SetValue(value, carriagewayAndGroup);
                m_ModifiedLanePositionField.SetValue(value, lanePosition);
                m_ModifiedEdgeField.SetValue(value, edgeEntity);
                m_ModifiedConnectionsField.SetValue(value, modifiedConnections);
                return value;
            }

            public object CreateGeneratedConnection(
                Entity sourceEntity,
                Entity targetEntity,
                int sourceLaneIndex,
                int targetLaneIndex,
                float3x2 lanePositionMap,
                int4 carriagewayAndGroupIndexMap,
                PathMethod method,
                bool isUnsafe)
            {
                object value = Activator.CreateInstance(m_GeneratedConnectionType);
                m_GeneratedSourceField.SetValue(value, sourceEntity);
                m_GeneratedTargetField.SetValue(value, targetEntity);
                m_GeneratedLaneIndexMapField.SetValue(value, new int2(sourceLaneIndex & 0xff, targetLaneIndex & 0xff));
                m_GeneratedCarriagewayAndGroupIndexMapField.SetValue(value, carriagewayAndGroupIndexMap);
                m_GeneratedLanePositionMapField.SetValue(value, lanePositionMap);
                m_GeneratedMethodField.SetValue(value, method);
                m_GeneratedUnsafeField.SetValue(value, isUnsafe);
                return value;
            }

            public void AddDataOwner(EntityManager entityManager, Entity entity, Entity owner)
            {
                ComponentType componentType = ComponentType.ReadWrite(m_DataOwnerType);
                if (!entityManager.HasComponent(entity, componentType))
                {
                    entityManager.AddComponent(entity, componentType);
                }

                object dataOwner = Activator.CreateInstance(m_DataOwnerType);
                m_DataOwnerEntityField.SetValue(dataOwner, owner);
                m_SetDataOwner.Invoke(entityManager, new[] { entity, dataOwner });
            }

            public void AddFakePrefabRef(EntityManager entityManager, Entity entity)
            {
                if (entityManager.HasComponent<PrefabRef>(entity))
                {
                    entityManager.SetComponentData(entity, new PrefabRef(m_FakePrefabRef));
                }
                else
                {
                    entityManager.AddComponentData(entity, new PrefabRef(m_FakePrefabRef));
                }
            }

            public void EnsureModifiedConnectionsTag(EntityManager entityManager, Entity node)
            {
                ComponentType componentType = ComponentType.ReadWrite(m_ModifiedConnectionsType);
                if (!entityManager.HasComponent(node, componentType))
                {
                    entityManager.AddComponent(node, componentType);
                }
            }

            private static Type FindType(string fullName)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }

                return null;
            }

            private static FieldInfo RequireField(Type type, string name)
            {
                return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? throw new MissingFieldException(type.FullName, name);
            }

            private static MethodInfo MakeEntityManagerGeneric(string name, Type genericType, params Type[] parameterTypes)
            {
                MethodInfo method = typeof(EntityManager)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(candidate =>
                        candidate.Name == name &&
                        candidate.IsGenericMethodDefinition &&
                        ParametersMatch(candidate, genericType, parameterTypes));

                if (method == null)
                {
                    throw new MissingMethodException(typeof(EntityManager).FullName, name);
                }

                return method.MakeGenericMethod(genericType);
            }

            private static bool ParametersMatch(MethodInfo method, Type genericType, Type[] parameterTypes)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                {
                    return false;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    Type parameterType = parameters[i].ParameterType;
                    if (parameterType.IsGenericParameter)
                    {
                        if (parameterTypes[i] != genericType)
                        {
                            return false;
                        }

                        continue;
                    }

                    if (parameterType != parameterTypes[i])
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
