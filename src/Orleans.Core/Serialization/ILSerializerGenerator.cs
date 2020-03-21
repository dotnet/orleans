using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal class ILSerializerGenerator
    {
        private static readonly RuntimeTypeHandle IntPtrTypeHandle = typeof(IntPtr).TypeHandle;

        private static readonly RuntimeTypeHandle UIntPtrTypeHandle = typeof(UIntPtr).TypeHandle;

        private static readonly Type DelegateType = typeof(Delegate);
        
        private static readonly Dictionary<RuntimeTypeHandle, SimpleTypeSerializer> DirectSerializers;

        private static readonly ReflectedSerializationMethodInfo SerializationMethodInfos = new ReflectedSerializationMethodInfo();

        private static readonly DeepCopier ImmutableTypeCopier = (obj, context) => obj;

        private static readonly ILFieldBuilder FieldBuilder = new ILFieldBuilder();

        static ILSerializerGenerator()
        {
            DirectSerializers = new Dictionary<RuntimeTypeHandle, SimpleTypeSerializer>
            {
                [typeof(int).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(int)), r => r.ReadInt()),
                [typeof(uint).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(uint)), r => r.ReadUInt()),
                [typeof(short).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(short)), r => r.ReadShort()),
                [typeof(ushort).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(ushort)), r => r.ReadUShort()),
                [typeof(long).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(long)), r => r.ReadLong()),
                [typeof(ulong).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(ulong)), r => r.ReadULong()),
                [typeof(byte).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(byte)), r => r.ReadByte()),
                [typeof(sbyte).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(sbyte)), r => r.ReadSByte()),
                [typeof(float).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(float)), r => r.ReadFloat()),
                [typeof(double).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(double)), r => r.ReadDouble()),
                [typeof(decimal).TypeHandle] =
                    new SimpleTypeSerializer(w => w.Write(default(decimal)), r => r.ReadDecimal()),
                [typeof(string).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(string)), r => r.ReadString()),
                [typeof(char).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(char)), r => r.ReadChar()),
                [typeof(Guid).TypeHandle] = new SimpleTypeSerializer(w => w.Write(default(Guid)), r => r.ReadGuid()),
                [typeof(DateTime).TypeHandle] =
                    new SimpleTypeSerializer(w => w.Write(default(DateTime)), r => r.ReadDateTime()),
                [typeof(TimeSpan).TypeHandle] =
                    new SimpleTypeSerializer(w => w.Write(default(TimeSpan)), r => r.ReadTimeSpan())
            };
        }

        /// <summary>
        /// Returns a value indicating whether the provided <paramref name="type"/> is supported.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A value indicating whether the provided <paramref name="type"/> is supported.</returns>
        public static bool IsSupportedType(Type type)
        {
            return !type.IsAbstract && !type.IsInterface && !type.IsArray && !type.IsEnum && IsSupportedFieldType(type);
        }

        /// <summary>
        /// Generates a serializer for the specified type.
        /// </summary>
        /// <param name="type">The type to generate the serializer for.</param>
        /// <param name="serializationFieldsFilter">
        /// The predicate used in addition to the default logic to select which fields are included in serialization and deserialization.
        /// </param>
        /// <param name="copyFieldsFilter">
        /// The predicate used in addition to the default logic to select which fields are included in copying.
        /// </param>
        /// <param name="fieldComparer">The comparer used to sort fields, or <see langword="null"/> to use the default.</param>
        /// <returns>The generated serializer.</returns>
        public SerializerMethods GenerateSerializer(
            Type type,
            Func<FieldInfo, bool> serializationFieldsFilter = null,
            Func<FieldInfo, bool> copyFieldsFilter = null,
            IComparer<FieldInfo> fieldComparer = null)
        {
            try
            {
                bool SerializationFieldFilter(FieldInfo field) => !field.IsNotSerialized() && (serializationFieldsFilter?.Invoke(field) ?? true);
                var serializationFields = this.GetFields(type, SerializationFieldFilter, fieldComparer);

                var callbacks = GetSerializationCallbacks(type);

                DeepCopier copier;
                if (type.IsOrleansShallowCopyable())
                {
                    copier = ImmutableTypeCopier;
                }
                else
                {
                    var copyFields = this.GetFields(type, copyFieldsFilter, fieldComparer);
                    copier = this.EmitCopier(type, copyFields).CreateDelegate();
                }

                var serializer = this.EmitSerializer(type, serializationFields, callbacks);
                var deserializer = this.EmitDeserializer(type, serializationFields, callbacks);
                return new SerializerMethods(
                    copier,
                    serializer.CreateDelegate(),
                    deserializer.CreateDelegate());
            }
            catch (Exception exception)
            {
                throw new ILGenerationException($"Serializer generation failed for type {type}", exception);
            }
        }

        private ILDelegateBuilder<DeepCopier> EmitCopier(Type type, List<FieldInfo> fields)
        {
            var il = new ILDelegateBuilder<DeepCopier>(
                FieldBuilder,
                type.Name + "DeepCopier",
                SerializationMethodInfos.DeepCopierDelegate);

            // Declare local variables.
            var result = il.DeclareLocal(type);
            var typedInput = il.DeclareLocal(type);

            // Set the typed input variable from the method parameter.
            il.LoadArgument(0);
            il.CastOrUnbox(type);
            il.StoreLocal(typedInput);

            // Construct the result.
            il.CreateInstance(type, result, SerializationMethodInfos.GetUninitializedObject);

            // Record the object.
            il.LoadArgument(1); // Load 'context' parameter.
            il.LoadArgument(0); // Load 'original' parameter.
            il.LoadLocal(result); // Load 'result' local.
            il.BoxIfValueType(type);
            il.Call(SerializationMethodInfos.RecordObjectWhileCopying);

            // Copy each field.
            foreach (var field in fields)
            {
                // Load the field.
                il.LoadLocalAsReference(type, result);
                il.LoadLocal(typedInput);
                il.LoadField(field);

                // Deep-copy the field if needed, otherwise just leave it as-is.
                if (!field.FieldType.IsOrleansShallowCopyable())
                {
                    var copyMethod = SerializationMethodInfos.DeepCopyInner;

                    il.BoxIfValueType(field.FieldType);
                    il.LoadArgument(1);
                    il.Call(copyMethod);
                    il.CastOrUnbox(field.FieldType);
                }

                // Store the copy of the field on the result.
                il.StoreField(field);
            }

            il.LoadLocal(result);
            il.BoxIfValueType(type);
            il.Return();
            return il;
        }

        private ILDelegateBuilder<Serializer> EmitSerializer(Type type, List<FieldInfo> fields,
            SerializationCallbacks callbacks)
        {
            var il = new ILDelegateBuilder<Serializer>(
                FieldBuilder,
                type.Name + "Serializer",
                SerializationMethodInfos.SerializerDelegate);

            // Declare local variables.
            var typedInput = il.DeclareLocal(type);

            var streamingContext = default(ILDelegateBuilder<Serializer>.Local);
            if (callbacks.OnSerializing != null || callbacks.OnSerialized != null)
            {
                streamingContext = il.DeclareLocal(typeof(StreamingContext));
                il.LoadLocalAddress(streamingContext);
                il.LoadConstant((int) StreamingContextStates.All);
                il.LoadArgument(1);
                il.Call(typeof(StreamingContext).GetConstructor(new[] {typeof(StreamingContextStates), typeof(object)}));
            }

            // Set the typed input variable from the method parameter.
            il.LoadArgument(0);
            il.CastOrUnbox(type);
            il.StoreLocal(typedInput);

            if (callbacks.OnSerializing != null)
            {
                il.LoadLocalAsReference(type, typedInput);
                il.LoadLocal(streamingContext);
                il.Call(callbacks.OnSerializing);
            }

            // Serialize each field
            foreach (var field in fields)
            {
                var fieldType = field.FieldType;
                var typeHandle = field.FieldType.TypeHandle;
                if (fieldType.IsEnum)
                {
                    typeHandle = fieldType.GetEnumUnderlyingType().TypeHandle;
                }
                
                if (DirectSerializers.TryGetValue(typeHandle, out var serializer))
                {
                    il.LoadArgument(1);
                    il.Call(SerializationMethodInfos.GetStreamFromSerializationContext);
                    il.LoadLocal(typedInput);
                    il.LoadField(field);

                    il.Call(serializer.WriteMethod);
                }
                else
                {
                    var serializeMethod = SerializationMethodInfos.SerializeInner;

                    // Load the field.
                    il.LoadLocal(typedInput);
                    il.LoadField(field);

                    il.BoxIfValueType(field.FieldType);

                    // Serialize the field.
                    il.LoadArgument(1);
                    il.LoadType(field.FieldType);
                    il.Call(serializeMethod);
                }
            }

            if (callbacks.OnSerialized != null)
            {
                il.LoadLocalAsReference(type, typedInput);
                il.LoadLocal(streamingContext);
                il.Call(callbacks.OnSerialized);
            }

            il.Return();
            return il;
        }

        private ILDelegateBuilder<Deserializer> EmitDeserializer(Type type, List<FieldInfo> fields,
            SerializationCallbacks callbacks)
        {
            var il = new ILDelegateBuilder<Deserializer>(
                FieldBuilder,
                type.Name + "Deserializer",
                SerializationMethodInfos.DeserializerDelegate);

            var streamingContext = default(ILDelegateBuilder<Deserializer>.Local);
            if (callbacks.OnDeserializing != null || callbacks.OnDeserialized != null)
            {
                streamingContext = il.DeclareLocal(typeof(StreamingContext));
                il.LoadLocalAddress(streamingContext);
                il.LoadConstant((int) StreamingContextStates.All);
                il.LoadArgument(1);
                il.Call(typeof(StreamingContext).GetConstructor(new[]
                    {typeof(StreamingContextStates), typeof(object)}));
            }

            // Declare local variables.
            var result = il.DeclareLocal(type);

            // Construct the result.
            il.CreateInstance(type, result, SerializationMethodInfos.GetUninitializedObject);

            // Record the object.
            il.LoadArgument(1); // Load the 'context' parameter.
            il.LoadLocal(result);
            il.BoxIfValueType(type);
            il.Call(SerializationMethodInfos.RecordObjectWhileDeserializing);

            if (callbacks.OnDeserializing != null)
            {
                il.LoadLocalAsReference(type, result);
                il.LoadLocal(streamingContext);
                il.Call(callbacks.OnDeserializing);
            }

            // Deserialize each field.
            foreach (var field in fields)
            {
                // Deserialize the field.
                var fieldType = field.FieldType;
                if (fieldType.IsEnum)
                {
                    var typeHandle = fieldType.GetEnumUnderlyingType().TypeHandle;
                    il.LoadLocalAsReference(type, result);
                    
                    il.LoadArgument(1);
                    il.Call(SerializationMethodInfos.GetStreamFromDeserializationContext);
                    il.Call(DirectSerializers[typeHandle].ReadMethod);
                    il.StoreField(field);
                }
                else if (DirectSerializers.TryGetValue(field.FieldType.TypeHandle, out var serializer))
                {
                    il.LoadLocalAsReference(type, result);
                    il.LoadArgument(1);
                    il.Call(SerializationMethodInfos.GetStreamFromDeserializationContext);
                    il.Call(serializer.ReadMethod);

                    il.StoreField(field);
                }
                else
                {
                    var deserializeMethod = SerializationMethodInfos.DeserializeInner;

                    il.LoadLocalAsReference(type, result);
                    il.LoadType(field.FieldType);
                    il.LoadArgument(1);
                    il.Call(deserializeMethod);

                    // Store the value on the result.
                    il.CastOrUnbox(field.FieldType);
                    il.StoreField(field);
                }
            }

            if (callbacks.OnDeserialized != null)
            {
                il.LoadLocalAsReference(type, result);
                il.LoadLocal(streamingContext);
                il.Call(callbacks.OnDeserialized);
            }

            // If the type implements the IOnDeserialized lifecycle handler, call that method now.
            if (typeof(IOnDeserialized).IsAssignableFrom(type))
            {
                il.LoadLocalAsReference(type, result);
                il.LoadArgument(1);
                var concreteMethod = GetConcreteMethod(
                    type,
                    TypeUtils.Method((IOnDeserialized i) => i.OnDeserialized(default(ISerializerContext))));
                il.Call(concreteMethod);
            }

            // If the type implements the IDeserializationCallback lifecycle handler, call that method now.
            if (typeof(IDeserializationCallback).IsAssignableFrom(type))
            {
                il.LoadLocalAsReference(type, result);
                il.LoadArgument(1);

                var concreteMethod = GetConcreteMethod(
                    type,
                    TypeUtils.Method((IDeserializationCallback i) => i.OnDeserialization(default(object))));
                il.Call(concreteMethod);
            }

            il.LoadLocal(result);
            il.BoxIfValueType(type);
            il.Return();
            return il;
        }

        private static MethodInfo GetConcreteMethod(Type type, MethodInfo interfaceMethod)
        {
            if (interfaceMethod == null) throw new ArgumentNullException(nameof(interfaceMethod));

            var map = type.GetInterfaceMap(interfaceMethod.DeclaringType);
            var concreteMethod = default(MethodInfo);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i] == interfaceMethod)
                {
                    concreteMethod = map.TargetMethods[i];
                    break;
                }
            }

            if (concreteMethod == null)
            {
                throw new InvalidOperationException(
                    $"Unable to find implementation of method {interfaceMethod.DeclaringType}.{interfaceMethod} on type {type} while generating serializer.");
            }

            return concreteMethod;
        }

        private SerializationCallbacks GetSerializationCallbacks(Type type)
        {
            var result = new SerializationCallbacks();
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                if (parameters[0].ParameterType != typeof(StreamingContext)) continue;

                if (method.GetCustomAttribute<OnDeserializingAttribute>() != null)
                {
                    result.OnDeserializing = method;
                }

                if (method.GetCustomAttribute<OnDeserializedAttribute>() != null)
                {
                    result.OnDeserialized = method;
                }

                if (method.GetCustomAttribute<OnSerializingAttribute>() != null)
                {
                    result.OnSerializing = method;
                }

                if (method.GetCustomAttribute<OnSerializedAttribute>() != null)
                {
                    result.OnSerialized = method;
                }
            }

            return result;
        }

        private class SerializationCallbacks
        {
            public MethodInfo OnDeserializing { get; set; }
            public MethodInfo OnDeserialized { get; set; }
            public MethodInfo OnSerializing { get; set; }
            public MethodInfo OnSerialized { get; set; }
        }

        /// <summary>
        /// Returns a sorted list of the fields of the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fieldFilter">The predicate used in addition to the default logic to select which fields are included.</param>
        /// <param name="fieldInfoComparer">The comparer used to sort fields, or <see langword="null"/> to use the default.</param>
        /// <returns>A sorted list of the fields of the provided type.</returns>
        private List<FieldInfo> GetFields(
            Type type,
            Func<FieldInfo, bool> fieldFilter = null,
            IComparer<FieldInfo> fieldInfoComparer = null)
        {
            var result =
                type.GetAllFields()
                    .Where(
                        field =>
                            !field.IsStatic
                            && IsSupportedFieldType(field.FieldType)
                            && (fieldFilter == null || fieldFilter(field)))
                    .ToList();
            result.Sort(fieldInfoComparer ?? FieldInfoComparer.Instance);
            return result;
        }

        /// <summary>
        /// Returns a value indicating whether the provided type is supported as a field by this class.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A value indicating whether the provided type is supported as a field by this class.</returns>
        private static bool IsSupportedFieldType(Type type)
        {
            if (type.IsPointer || type.IsByRef) return false;

            var handle = type.TypeHandle;
            if (handle.Equals(IntPtrTypeHandle)) return false;
            if (handle.Equals(UIntPtrTypeHandle)) return false;
            if (DelegateType.IsAssignableFrom(type)) return false;

            return true;
        }

        /// <summary>
        /// A comparer for <see cref="FieldInfo"/> which compares by name.
        /// </summary>
        private class FieldInfoComparer : IComparer<FieldInfo>
        {
            /// <summary>
            /// Gets the singleton instance of this class.
            /// </summary>
            public static FieldInfoComparer Instance { get; } = new FieldInfoComparer();

            public int Compare(FieldInfo x, FieldInfo y)
            {
                return string.Compare(x.Name, y.Name, StringComparison.Ordinal);
            }
        }

        private class SimpleTypeSerializer
        {
            public SimpleTypeSerializer(
                Expression<Action<IBinaryTokenStreamWriter>> write,
                Expression<Action<IBinaryTokenStreamReader>> read)
            {
                this.WriteMethod = TypeUtils.Method(write);
                this.ReadMethod = TypeUtils.Method(read);
            }

            public MethodInfo WriteMethod { get; }

            public MethodInfo ReadMethod { get; }
        }
    }
}