namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;

    using Orleans;
    using Orleans.CodeGeneration;
    using Orleans.Runtime;
    using Orleans.Serialization;

    /// <summary>
    /// Collection of types which need serializers to be generated.
    /// </summary>
    internal class SerializableTypeCollector
    {
        /// <summary>
        /// Types which have not yet been considered.
        /// </summary>
        private readonly Queue<Type> pending = new Queue<Type>();

        /// <summary>
        /// The types which have been accepted but not yet consumed.
        /// </summary>
        private readonly HashSet<Type> accepted = new HashSet<Type>();

        /// <summary>
        /// The types which have been rejected.
        /// </summary>
        private readonly HashSet<Type> rejected = new HashSet<Type>();

        /// <summary>
        /// The types which have been accepted and consumed.
        /// </summary>
        private readonly HashSet<Type> processed = new HashSet<Type>();

        /// <summary>
        /// The types which have been or are being considered for inclusion.
        /// </summary>
        private readonly HashSet<Type> considered = new HashSet<Type>();

        /// <summary>
        /// Whether or not to include types which may not be accessible.
        /// </summary>
        private readonly bool includeNonPublic;

        /// <summary>
        /// The logger.
        /// </summary>
        private readonly TraceLogger log = TraceLogger.GetLogger("SerializerGenerator");

        /// <summary>
        /// The number of assemblies which have been seen by this instance.
        /// </summary>
        private int numAssemblies;

        public SerializableTypeCollector(bool includeNonPublic = false)
        {
            this.includeNonPublic = includeNonPublic;
        }

        /// <summary>
        /// Considers the specified type, recording whether or not it requires generation of a serializer, recursing
        /// into referenced types.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="module">
        /// The module which the serialzier will reside in.
        /// </param>
        /// <param name="serializerAssembly">
        /// The assembly which the serializalizers will reside in.
        /// </param>
        public void Consider(Type type, Assembly serializerAssembly = null, Module module = null)
        {
            // Reject all types which have already have generated serializers.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(_ => !_.IsDynamic).ToList();
            if (assemblies.Count != this.numAssemblies)
            {
                this.rejected.UnionWith(CodeGeneratorCommon.GetTypesWithImplementations<MethodInvokerAttribute>());
                this.numAssemblies = assemblies.Count;
            }

            // Consider this type.
            if (!this.considered.Contains(type))
            {
                this.pending.Enqueue(type);
                this.Consider(serializerAssembly, module);
            }
        }

        /// <summary>
        /// Considers the specified type, recording whether or not it requires generation of a serializer, recursing
        /// into referenced types.
        /// </summary>
        /// <param name="serializerAssembly">
        /// The assembly which the serializalizers will reside in.
        /// </param>
        /// <param name="module">
        /// The module which the serialzier will reside in.
        /// </param>
        private void Consider(Assembly serializerAssembly, Module module = null)
        {
            while (this.pending.Count > 0)
            {
                var type = this.pending.Dequeue();
                if (!this.considered.Add(type) || type.GetCustomAttribute<NonSerializableAttribute>() != null || type.Name.StartsWith("<"))
                {
                    continue;
                }

                // Skip internal and private types unless that option is set.
                if ((!type.IsPublic || type.IsNotPublic || !type.IsVisible) && !this.includeNonPublic)
                {
                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "Skipping serializer generation for {0} because it is not public.",
                        type);
                    continue;
                }

                // Skip types in the System namespace.
                if (TypeUtils.IsInSystemNamespace(type))
                {
                    Debug.Assert(type.Namespace != null, "type.Namespace != null");
                    if (type.Namespace.StartsWith("System.Threading.Tasks"))
                    {
                        continue;
                    }

                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "System type {0} may require a custom serializer for optimal performance.\n"
                        + "If you use arguments of this type a lot, consider asking the Orleans team to build a custom serializer for it.",
                        type);
                    continue;
                }

                // Consider all type parameters of constructed generic types.
                if (type.IsConstructedGenericType)
                {
                    foreach (var typeParameter in
                        type.GetGenericArguments().Where(typeParameter => !this.considered.Contains(typeParameter)))
                    {
                        this.pending.Enqueue(typeParameter);
                    }
                }

                // For addressable types, consider all method return types and parameter types.
                if (type.IsInterface && typeof(IAddressable).IsAssignableFrom(type))
                {
                    var innerTypes = type.GetTypes(includeMethods: true);
                    if (!this.includeNonPublic
                        && !innerTypes.Any(_ => !type.IsPublic || type.IsNotPublic || !type.IsVisible))
                    {
                        this.log.Verbose3(
                            ErrorCode.CodeGenSerializerGenerator,
                            "Skipping serializer generation for {0} because it contains non-public types.",
                            type);
                        continue;
                    }

                    foreach (
                        var innerType in innerTypes.Where(typeParameter => !this.considered.Contains(typeParameter)))
                    {
                        this.pending.Enqueue(innerType);
                    }

                    continue;
                }

                // Skip types which cannot be serialized or are already accounted for.
                if (type.IsGenericParameter || typeof(Exception).IsAssignableFrom(type) || type.Name.StartsWith("<"))
                {
                    if (type.IsGenericParameter)
                    {
                        this.log.Verbose3(
                            ErrorCode.CodeGenSerializerGenerator,
                            "Skipping serializer generation for {0} because it is already serializable.",
                            type);
                    }

                    continue;
                }

                // Skip types which already have serializers.
                if (type.IsOrleansPrimitive() || SerializationManager.GetSerializer(type) != null)
                {
                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "Skipping serializer generation for {0} because it is already serializable.",
                        type);
                    continue;
                }

                if (type.IsArray)
                {
                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "Serializer generation is traversing into {0} because it is an array.",
                        type);
                    this.pending.Enqueue(type.GetElementType());
                    continue;
                }

                if (type.IsByRef)
                {
                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "Serializer generation is traversing into {0} because it is a by-ref type.",
                        type);
                    this.pending.Enqueue(type.GetElementType());
                    continue;
                }

                if (typeof(MarshalByRefObject).IsAssignableFrom(type))
                {
                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "Skipping serializer generation for {0} because it is a MarshalByRefObject, which is not supported.",
                        type);
                    continue;
                }

                // Skip nested types.
                if (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedPrivate)
                {
                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "Skipping serializer generation for {0} because it is nested. If this type is used frequently, you may wish to consider making it non-nested.",
                        type);
                    continue;
                }

                // Skip non-concrete types and enums.
                if (type.IsInterface || type.IsAbstract || type.IsEnum || type == typeof(object))
                {
                    if (type.IsInterface)
                    {
                        this.log.Verbose3(
                            ErrorCode.CodeGenSerializerGenerator,
                            "Skipping serializer generation for {0} because it is an interface",
                            type);
                    }
                    else if (type.IsAbstract)
                    {
                        this.log.Verbose3(
                            ErrorCode.CodeGenSerializerGenerator,
                            "Skipping serializer generation for {0} because it is abstract",
                            type);
                    }
                    else if (type.IsEnum)
                    {
                        this.log.Verbose3(
                            ErrorCode.CodeGenSerializerGenerator,
                            "Skipping serializer generation for {0} because it is an enum",
                            type);
                    }

                    continue;
                }

                // If a type has all required methods, skip generation.
                if (TypeUtils.HasAllSerializationMethods(type))
                {
                    if (type.IsInterface)
                    {
                        this.log.Verbose3(
                            ErrorCode.CodeGenSerializerGenerator,
                            "Skipping serializer generation for {0} because it has all serialization methods defined.",
                            type);
                    }

                    continue;
                }

                if (this.HasNonAccessibleFieldTypes(type, serializerAssembly, module))
                {
                    this.log.Verbose3(
                        ErrorCode.CodeGenSerializerGenerator,
                        "Skipping serializer generation for {0} because it is contains fields with non-accessible types.",
                        type);

                    continue;
                }

                if (type.IsGenericType)
                {
                    // For generic types, only add types which don't already have a serializer registered.
                    var generic = type.GetGenericTypeDefinition();
                    if (SerializationManager.GetSerializer(generic) != null)
                    {
                        if (type.IsInterface)
                        {
                            this.log.Verbose3(
                                ErrorCode.CodeGenSerializerGenerator,
                                "Skipping serializer generation for {0} because it is already serializable.",
                                type);
                        }

                        continue;
                    }

                    // Add the generic type definition.
                    this.accepted.Add(generic);
                }
                else
                {
                    // Add the concrete type.
                    this.accepted.Add(type);
                }
            }
        }

        /// <summary>
        /// Returns true if the provided type has non-accessible fields, false if all fields have accessible types.
        /// </summary>
        /// <param name="type">The type to check.</param>
        /// <param name="serializerAssembly">The assembly which the type must be accessible from.</param>
        /// <param name="module">
        /// The module which would be accessing the type.
        /// </param>
        /// <param name="types">
        /// The types which have already been considered or are being considered.
        /// </param>
        /// <returns>true if the provided type has non-accessible fields, false if all fields have accessible types.</returns>
        private bool HasNonAccessibleFieldTypes(Type type, Assembly serializerAssembly, Module module = null, HashSet<Type> types = null)
        {
            if (this.rejected.Contains(type))
            {
                return true;
            }

            if (this.accepted.Contains(type))
            {
                return false;
            }

            types = types ?? new HashSet<Type>();
            if (!types.Add(type))
            {
                return false;
            }

            if (TypeUtilities.IsTypeIsInaccessibleForSerialization(type, module, serializerAssembly))
            {
                this.rejected.Add(type);
                return true;
            }

            foreach (var fieldType in type.GetAllFields().Select(field => field.FieldType))
            {
                if (TypeUtilities.IsTypeIsInaccessibleForSerialization(fieldType, module, serializerAssembly))
                {
                    this.rejected.Add(type);
                    return true;
                }

                if (!fieldType.IsConstructedGenericType)
                {
                    continue;
                }

                foreach (var genericArgument in fieldType.GetGenericArguments().Where(_ => !types.Contains(_)))
                {
                    if (TypeUtilities.IsTypeIsInaccessibleForSerialization(fieldType, module, serializerAssembly))
                    {
                        this.rejected.Add(type);
                        return true;
                    }

                    if (this.HasNonAccessibleFieldTypes(genericArgument, serializerAssembly, module, types))
                    {
                        this.rejected.Add(type);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Takes an element from the collection and returns it, returning <see langword="null"/> if no elements remain.
        /// </summary>
        /// <returns>The element, or <see langword="null"/> if no elements remain.</returns>
        public Type Take()
        {
            if (this.accepted.Count == 0)
            {
                return null;
            }

            var type = this.accepted.First();

            this.accepted.Remove(type);
            this.processed.Add(type);

            return type;
        }

        /// <summary>
        /// Takes all elements from the collection and returns them.
        /// </summary>
        /// <returns>All elements from the collection.</returns>
        public IList<Type> TakeAll()
        {
            var result = new List<Type>();
            Type type;
            while ((type = this.Take()) != null)
            {
                result.Add(type);
            }

            return result;
        }

        /// <summary>
        /// Returns true if this instance has types which require serializers, false otherwise.
        /// </summary>
        /// <returns>true if this instance has types which require serializers, false otherwise.</returns>
        public bool HasMore()
        {
            return this.accepted.Count > 0;
        }

        /// <summary>
        /// Returns a string representation of this instance.
        /// </summary>
        /// <returns>A string representation of this instance.</returns>
        public override string ToString()
        {
            return string.Join("\n", this.accepted.Select(_ => _.GetParseableName()));
        }
    }
}