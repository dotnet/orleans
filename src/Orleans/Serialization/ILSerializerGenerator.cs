namespace Orleans.Serialization
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    using Orleans.Runtime;

    internal class ILSerializerGenerator
    {
        private readonly ReflectedSerializationMethodInfo methods = new ReflectedSerializationMethodInfo();

        private readonly SerializationManager.DeepCopier immutableTypeCopier = obj => obj;

        private static readonly RuntimeTypeHandle IntPtrTypeHandle = typeof(IntPtr).TypeHandle;

        private static readonly RuntimeTypeHandle UintPtrTypeHandle = typeof(UIntPtr).TypeHandle;

        private static readonly TypeInfo DelegateTypeInfo = typeof(Delegate).GetTypeInfo();

        /// <summary>
        /// Returns a value indicating whether the provided <paramref name="type"/> is supported.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A value indicating whether the provided <paramref name="type"/> is supported.</returns>
        public static bool IsSupportedType(TypeInfo type)
        {
            return !type.IsAbstract && !type.IsInterface && !type.IsArray && IsSupportedFieldType(type);
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
        /// <returns>The generated serializer.</returns>
        public SerializationManager.SerializerMethods GenerateSerializer(
            Type type,
            Func<FieldInfo, bool> serializationFieldsFilter = null,
            Func<FieldInfo, bool> copyFieldsFilter = null)
        {
            try
            {
                var serializationFields = this.GetFields(type, serializationFieldsFilter);
                List<FieldInfo> copyFields;
                if (copyFieldsFilter == serializationFieldsFilter)
                {
                    copyFields = serializationFields;
                }
                else
                {
                    copyFields = this.GetFields(type, copyFieldsFilter);
                }

                SerializationManager.DeepCopier copier;
                if (type.IsOrleansShallowCopyable()) copier = this.immutableTypeCopier;
                else copier = this.EmitCopier(type, copyFields).Build();

                var serializer = this.EmitSerializer(type, serializationFields);
                var deserializer = this.EmitDeserializer(type, serializationFields);
                return new SerializationManager.SerializerMethods(copier, serializer.Build(), deserializer.Build());
            }
            catch (Exception exception)
            {
                throw new ILGenerationException($"Serializer generation failed for type {type}", exception);
            }
        }

        private ILDelegateBuilder<SerializationManager.DeepCopier> EmitCopier(Type type, List<FieldInfo> fields)
        {
            var builder = new ILDelegateBuilder<SerializationManager.DeepCopier>(
                type.Name + "DeepCopier",
                this.methods,
                this.methods.DeepCopierDelegate);

            // Declare local variables.
            var result = builder.DeclareLocal(type);
            var typedInput = builder.DeclareLocal(type);

            // Set the typed input variable from the method parameter.
            builder.LoadArgument(0);
            builder.CastOrUnbox(type);
            builder.StoreLocal(typedInput);

            // Construct the result.
            builder.CreateInstance(type, result);

            // Record the object.
            builder.Call(this.methods.GetCurrentSerializationContext);
            builder.LoadArgument(0); // Load 'original' parameter.
            builder.LoadLocal(result); // Load 'result' local.
            builder.BoxIfValueType(type);
            builder.Call(this.methods.RecordObjectWhileCopying);

            // Copy each field.
            foreach (var field in fields)
            {
                // Load the field.
                builder.LoadLocalAsReference(type, result);
                builder.LoadLocal(typedInput);
                builder.LoadField(field);

                // Deep-copy the field if needed, otherwise just leave it as-is.
                if (!field.FieldType.IsOrleansShallowCopyable())
                {
                    builder.BoxIfValueType(field.FieldType);
                    builder.Call(this.methods.DeepCopyInner);
                    builder.CastOrUnbox(field.FieldType);
                }

                // Store the copy of the field on the result.
                builder.StoreField(field);
            }

            builder.LoadLocal(result);
            builder.BoxIfValueType(type);
            builder.Return();
            return builder;
        }

        private ILDelegateBuilder<SerializationManager.Serializer> EmitSerializer(Type type, List<FieldInfo> fields)
        {
            var builder = new ILDelegateBuilder<SerializationManager.Serializer>(
                type.Name + "Serializer",
                this.methods,
                this.methods.SerializerDelegate);

            // Declare local variables.
            var typedInput = builder.DeclareLocal(type);

            // Set the typed input variable from the method parameter.
            builder.LoadArgument(0);
            builder.CastOrUnbox(type);
            builder.StoreLocal(typedInput);

            // Serialize each field
            foreach (var field in fields)
            {
                // Load the field.
                builder.LoadLocal(typedInput);
                builder.LoadField(field);
                builder.BoxIfValueType(field.FieldType);

                // Serialize the field.
                builder.LoadArgument(1);
                builder.LoadType(field.FieldType);
                builder.Call(this.methods.SerializeInner);
            }

            builder.Return();
            return builder;
        }

        private ILDelegateBuilder<SerializationManager.Deserializer> EmitDeserializer(Type type, List<FieldInfo> fields)
        {
            var builder = new ILDelegateBuilder<SerializationManager.Deserializer>(
                type.Name + "Deserializer",
                this.methods,
                this.methods.DeserializerDelegate);

            // Declare local variables.
            var result = builder.DeclareLocal(type);

            // Construct the result.
            builder.CreateInstance(type, result);

            // Record the object.
            builder.Call(this.methods.GetCurrentDeserializationContext);
            builder.LoadLocal(result);
            builder.BoxIfValueType(type);
            builder.Call(this.methods.RecordObjectWhileDeserializing);

            // Deserialize each field.
            foreach (var field in fields)
            {
                // Deserialize the field.
                builder.LoadLocalAsReference(type, result);
                builder.LoadType(field.FieldType);
                builder.LoadArgument(1);
                builder.Call(this.methods.DeserializeInner);

                // Store the value on the result.
                builder.CastOrUnbox(field.FieldType);
                builder.StoreField(field);
            }

            builder.LoadLocal(result);
            builder.BoxIfValueType(type);
            builder.Return();
            return builder;
        }

        /// <summary>
        /// Returns a sorted list of the fields of the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fieldFilter">The predicate used in addition to the default logic to select which fields are included.</param>
        /// <returns>A sorted list of the fields of the provided type.</returns>
        private List<FieldInfo> GetFields(Type type, Func<FieldInfo, bool> fieldFilter = null)
        {
            var result = type.GetAllFields().Where(
                field =>
                field.GetCustomAttribute<NonSerializedAttribute>() == null
                && !field.IsStatic && IsSupportedFieldType(field.FieldType.GetTypeInfo())
                && (fieldFilter == null || fieldFilter(field))).ToList();
            result.Sort(FieldInfoComparer.Instance);
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
            if (handle.Equals(UintPtrTypeHandle)) return false;
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
    }
}