using System;
using System.Linq;
using System.Reflection;
using Game.Pathfind;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace PocketTurnLanes.Tool.Traffic
{
    internal sealed class TrafficApi
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
                error = $"compatibilityCheckFailed error={ex.GetType().Name}:{ex.Message} modifiedConnections={FormatType(modifiedConnectionsType)} dataOwner={FormatType(dataOwnerType)} modifiedLaneConnections={FormatType(modifiedLaneConnectionsType)} generatedConnection={FormatType(generatedConnectionType)} modDefaults={FormatType(modDefaultsSystemType)}";
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

        public void RemoveModifiedLaneConnectionsBuffer(EntityManager entityManager, Entity node)
        {
            ComponentType componentType = ComponentType.ReadWrite(m_ModifiedLaneConnectionsType);
            if (entityManager.HasComponent(node, componentType))
            {
                entityManager.RemoveComponent(node, componentType);
            }
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
            PropertyInfo property = RequireBufferProperty(buffer, "Length", "read buffer length");
            object value = GetPropertyValue(property, buffer, null, "read buffer length");
            if (value is int length)
            {
                return length;
            }

            throw CreateCompatibilityException($"read buffer length returned non-int valueType={value?.GetType().FullName ?? "<null>"} member={FormatMember(property)} bufferType={FormatType(buffer?.GetType())}");
        }

        public object GetBufferItem(object buffer, int index)
        {
            PropertyInfo property = RequireBufferProperty(buffer, "Item", "read buffer item");
            return GetPropertyValue(property, buffer, new object[] { index }, $"read buffer item index={index}");
        }

        public void ClearBuffer(object buffer)
        {
            MethodInfo method = RequireBufferMethod(buffer, "Clear", 0, "clear buffer");
            InvokeMethod(method, buffer, null, "clear buffer");
        }

        public void AddBufferElement(object buffer, object element)
        {
            MethodInfo method = RequireBufferMethod(buffer, "Add", 1, "add buffer element");
            InvokeMethod(method, buffer, new[] { element }, $"add buffer element elementType={FormatType(element?.GetType())}");
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

        public void RemoveModifiedConnectionsTag(EntityManager entityManager, Entity node)
        {
            ComponentType componentType = ComponentType.ReadWrite(m_ModifiedConnectionsType);
            if (entityManager.HasComponent(node, componentType))
            {
                entityManager.RemoveComponent(node, componentType);
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
                   ?? throw CreateCompatibilityException($"missing field type={FormatType(type)} field={name}");
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
                throw CreateCompatibilityException($"missing EntityManager generic method operation={name} genericType={FormatType(genericType)} parameters={FormatParameterTypes(parameterTypes)}");
            }

            return method.MakeGenericMethod(genericType);
        }

        private static PropertyInfo RequireBufferProperty(object buffer, string name, string operation)
        {
            Type bufferType = buffer?.GetType();
            if (bufferType == null)
            {
                throw CreateCompatibilityException($"{operation} failed because buffer is <null> member={name}");
            }

            PropertyInfo property = bufferType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                throw CreateCompatibilityException($"{operation} missing property bufferType={FormatType(bufferType)} property={name}");
            }

            return property;
        }

        private static MethodInfo RequireBufferMethod(object buffer, string name, int parameterCount, string operation)
        {
            Type bufferType = buffer?.GetType();
            if (bufferType == null)
            {
                throw CreateCompatibilityException($"{operation} failed because buffer is <null> method={name}");
            }

            MethodInfo method = bufferType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(candidate =>
                    candidate.Name == name &&
                    candidate.GetParameters().Length == parameterCount);
            if (method == null)
            {
                throw CreateCompatibilityException($"{operation} missing method bufferType={FormatType(bufferType)} method={name} parameterCount={parameterCount}");
            }

            return method;
        }

        private static object GetPropertyValue(PropertyInfo property, object instance, object[] index, string operation)
        {
            try
            {
                return property.GetValue(instance, index);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                throw CreateCompatibilityException($"{operation} property getter failed member={FormatMember(property)} error={inner.GetType().Name}:{inner.Message}", inner);
            }
            catch (Exception ex)
            {
                throw CreateCompatibilityException($"{operation} property access failed member={FormatMember(property)} error={ex.GetType().Name}:{ex.Message}", ex);
            }
        }

        private static object InvokeMethod(MethodInfo method, object instance, object[] parameters, string operation)
        {
            try
            {
                return method.Invoke(instance, parameters);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                throw CreateCompatibilityException($"{operation} method invocation failed member={FormatMember(method)} error={inner.GetType().Name}:{inner.Message}", inner);
            }
            catch (Exception ex)
            {
                throw CreateCompatibilityException($"{operation} method access failed member={FormatMember(method)} error={ex.GetType().Name}:{ex.Message}", ex);
            }
        }

        private static InvalidOperationException CreateCompatibilityException(string message, Exception inner = null)
        {
            return inner == null
                ? new InvalidOperationException($"Traffic API compatibility check failed: {message}")
                : new InvalidOperationException($"Traffic API compatibility check failed: {message}", inner);
        }

        private static string FormatType(Type type)
        {
            if (type == null)
            {
                return "<null>";
            }

            AssemblyName assemblyName = type.Assembly.GetName();
            return $"{type.FullName}, assembly={assemblyName.Name}, version={assemblyName.Version}";
        }

        private static string FormatMember(MemberInfo member)
        {
            return member == null
                ? "<null>"
                : $"{FormatType(member.DeclaringType)}.{member.Name}";
        }

        private static string FormatParameterTypes(Type[] parameterTypes)
        {
            return parameterTypes == null || parameterTypes.Length == 0
                ? "<none>"
                : string.Join(",", parameterTypes.Select(type => type == null ? "<null>" : type.FullName));
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
