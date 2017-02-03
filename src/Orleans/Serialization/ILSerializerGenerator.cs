namespace Orleans.Serialization
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;

    using Orleans.Runtime;

    internal class ILSerializerGenerator
    {
        private static readonly RuntimeTypeHandle IntPtrTypeHandle = typeof(IntPtr).TypeHandle;

        private static readonly RuntimeTypeHandle UIntPtrTypeHandle = typeof(UIntPtr).TypeHandle;

        private static readonly TypeInfo DelegateTypeInfo = typeof(Delegate).GetTypeInfo();
        
        private static readonly Dictionary<RuntimeTypeHandle, SimpleTypeSerializer> DirectSerializers;

        private static readonly ReflectedSerializationMethodInfo SerializationMethodInfos = new ReflectedSerializationMethodInfo();

        private static readonly SerializationManager.DeepCopier ImmutableTypeCopier = (obj, context) => obj;

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
        public static bool IsSupportedType(TypeInfo type)
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
        public SerializationManager.SerializerMethods GenerateSerializer(
            Type type,
            Func<FieldInfo, bool> serializationFieldsFilter = null,
            Func<FieldInfo, bool> copyFieldsFilter = null,
            IComparer<FieldInfo> fieldComparer = null)
        {
            try
            {
                var serializationFields = this.GetFields(type, serializationFieldsFilter, fieldComparer);
                List<FieldInfo> copyFields;
                if (copyFieldsFilter == serializationFieldsFilter)
                {
                    copyFields = serializationFields;
                }
                else
                {
                    copyFields = this.GetFields(type, copyFieldsFilter, fieldComparer);
                }
                
                SerializationManager.DeepCopier copier;
                if (type.IsOrleansShallowCopyable()) copier = ImmutableTypeCopier;
                else copier = this.EmitCopier(type, copyFields).CreateDelegate();

                var serializer = this.EmitSerializer(type, serializationFields);
                var deserializer = this.EmitDeserializer(type, serializationFields);
                return new SerializationManager.SerializerMethods(
                    copier,
                    serializer.CreateDelegate(),
                    deserializer.CreateDelegate());
            }
            catch (Exception exception)
            {
                throw new ILGenerationException($"Serializer generation failed for type {type}", exception);
            }
        }

        private ILDelegateBuilder<SerializationManager.DeepCopier> EmitCopier(Type type, List<FieldInfo> fields)
        {
            var il = new ILDelegateBuilder<SerializationManager.DeepCopier>(
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

        private ILDelegateBuilder<SerializationManager.Serializer> EmitSerializer(Type type, List<FieldInfo> fields)
        {
            var il = new ILDelegateBuilder<SerializationManager.Serializer>(
                FieldBuilder,
                type.Name + "Serializer",
                SerializationMethodInfos.SerializerDelegate);

            // Declare local variables.
            var typedInput = il.DeclareLocal(type);

            // Set the typed input variable from the method parameter.
            il.LoadArgument(0);
            il.CastOrUnbox(type);
            il.StoreLocal(typedInput);

            // Serialize each field
            foreach (var field in fields)
            {
                SimpleTypeSerializer serializer;
                var fieldType = field.FieldType.GetTypeInfo();
                var typeHandle = field.FieldType.TypeHandle;
                if (fieldType.IsEnum)
                {
                    typeHandle = fieldType.GetEnumUnderlyingType().TypeHandle;
                }
                
                if (DirectSerializers.TryGetValue(typeHandle, out serializer))
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

            il.Return();
            return il;
        }

        private ILDelegateBuilder<SerializationManager.Deserializer> EmitDeserializer(Type type, List<FieldInfo> fields)
        {
            var il = new ILDelegateBuilder<SerializationManager.Deserializer>(
                FieldBuilder,
                type.Name + "Deserializer",
                SerializationMethodInfos.DeserializerDelegate);

            // Declare local variables.
            var result = il.DeclareLocal(type);

            // Construct the result.
            il.CreateInstance(type, result, SerializationMethodInfos.GetUninitializedObject);

            // Record the object.
            il.LoadArgument(1); // Load the 'context' parameter.
            il.LoadLocal(result);
            il.BoxIfValueType(type);
            il.Call(SerializationMethodInfos.RecordObjectWhileDeserializing);

            // Deserialize each field.
            foreach (var field in fields)
            {
                // Deserialize the field.
                SimpleTypeSerializer serializer;
                var fieldType = field.FieldType.GetTypeInfo();
                if (fieldType.IsEnum)
                {
                    var typeHandle = fieldType.GetEnumUnderlyingType().TypeHandle;
                    il.LoadLocalAsReference(type, result);
                    
                    il.LoadArgument(1);
                    il.Call(SerializationMethodInfos.GetStreamFromDeserializationContext);
                    il.Call(DirectSerializers[typeHandle].ReadMethod);
                    il.StoreField(field);
                }
                else if (DirectSerializers.TryGetValue(field.FieldType.TypeHandle, out serializer))
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

            il.LoadLocal(result);
            il.BoxIfValueType(type);
            il.Return();
            return il;
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
                            field.GetCustomAttribute<NonSerializedAttribute>() == null && !field.IsStatic
                            && IsSupportedFieldType(field.FieldType.GetTypeInfo())
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
        private static bool IsSupportedFieldType(TypeInfo type)
        {
            if (type.IsPointer || type.IsByRef) return false;

            var handle = type.AsType().TypeHandle;
            if (handle.Equals(IntPtrTypeHandle)) return false;
            if (handle.Equals(UIntPtrTypeHandle)) return false;
            if (DelegateTypeInfo.IsAssignableFrom(type)) return false;

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
                Expression<Action<BinaryTokenStreamWriter>> write,
                Expression<Action<BinaryTokenStreamReader>> read)
            {
                this.WriteMethod = TypeUtils.Method(write);
                this.ReadMethod = TypeUtils.Method(read);
            }

            public MethodInfo WriteMethod { get; }

            public MethodInfo ReadMethod { get; }
        }
    }
}