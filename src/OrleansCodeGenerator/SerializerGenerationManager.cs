using System.Linq;

namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading.Tasks;
    using Orleans.Runtime;
    using Orleans.Serialization;
    using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;

    /// <summary>
    /// The serializer generation manager.
    /// </summary>
    internal class SerializerGenerationManager
    {
        private readonly SerializationManager serializationManager;

        /// <summary>
        /// The logger.
        /// </summary>
        private readonly Logger log;

        /// <summary>
        /// The types to process.
        /// </summary>
        private readonly HashSet<Type> typesToProcess;

        /// <summary>
        /// The processed types.
        /// </summary>
        private readonly HashSet<Type> processedTypes;

        /// <summary>
        /// Initializes members of the <see cref="SerializerGenerationManager"/> class.
        /// </summary>
        internal SerializerGenerationManager(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
            typesToProcess = new HashSet<Type>();
            processedTypes = new HashSet<Type>();

            log = LogManager.GetLogger(typeof(SerializerGenerationManager).Name);
        }

        internal bool IsTypeRecorded(Type type)
        {
            return this.typesToProcess.Contains(type) || this.processedTypes.Contains(type);
        }

        internal bool RecordTypeToGenerate(Type t, Module module, Assembly targetAssembly)
        {
            if (TypeUtilities.IsTypeIsInaccessibleForSerialization(t, module, targetAssembly))
            {
                return false;
            }

            var typeInfo = t.GetTypeInfo();

            if (typeInfo.IsGenericParameter || processedTypes.Contains(t) || typesToProcess.Contains(t)
                || typeof (Exception).GetTypeInfo().IsAssignableFrom(t)
                || typeof (Delegate).GetTypeInfo().IsAssignableFrom(t)
                || typeof (Task<>).GetTypeInfo().IsAssignableFrom(t)) return false;

            if (typeInfo.IsArray)
            {
                RecordTypeToGenerate(typeInfo.GetElementType(), module, targetAssembly);
                return false;
            }

            if (typeInfo.IsNestedFamily || typeInfo.IsNestedPrivate)
            {
                log.Warn(
                    ErrorCode.CodeGenIgnoringTypes,
                    "Skipping serializer generation for nested type {0}. If this type is used frequently, you may wish to consider making it non-nested.",
                    t.Name);
                return false;
            }

            if (t.IsConstructedGenericType)
            {
                var args = typeInfo.GetGenericArguments();
                foreach (var arg in args)
                {
                    RecordTypeToGenerate(arg, module, targetAssembly);
                }
            }

            if (typeInfo.IsInterface || typeInfo.IsAbstract || t == typeof (object) || t == typeof (void)
                || GrainInterfaceUtils.IsTaskType(t)) return false;

            if (t.IsConstructedGenericType)
            {
                return RecordTypeToGenerate(typeInfo.GetGenericTypeDefinition(), module, targetAssembly);
            }

            if (typeInfo.IsOrleansPrimitive() || this.serializationManager.HasSerializer(t) ||
                typeof(IAddressable).GetTypeInfo().IsAssignableFrom(t)) return false;

            if (typeInfo.Namespace != null && (typeInfo.Namespace.Equals("System") || typeInfo.Namespace.StartsWith("System.")))
            {
                var message = "System type " + t.Name + " may require a custom serializer for optimal performance. "
                              + "If you use arguments of this type a lot, consider submitting a pull request to https://github.com/dotnet/orleans/ to add a custom serializer for it.";
                log.Warn(ErrorCode.CodeGenSystemTypeRequiresSerializer, message);
                return false;
            }

            if (TypeUtils.HasAllSerializationMethods(t)) return false;

            // This check is here and not within TypeUtilities.IsTypeIsInaccessibleForSerialization() to prevent potential infinite recursions 
            var skipSerializerGeneration =
                t.GetAllFields().Any(field => this.IsFieldInaccessibleForSerialization(module, targetAssembly, field));
            if (skipSerializerGeneration)
            {
                return false;
            }

            typesToProcess.Add(t);
            return true;
        }

        private bool IsFieldInaccessibleForSerialization(Module module, Assembly targetAssembly, FieldInfo field)
        {
            return field.GetCustomAttributes().All(attr => attr.GetType().Name != "NonSerializedAttribute")
                   && !this.serializationManager.HasSerializer(field.FieldType)
                   && TypeUtilities.IsTypeIsInaccessibleForSerialization(field.FieldType, module, targetAssembly);
        }

        internal bool GetNextTypeToProcess(out Type next)
        {
            next = null;
            if (typesToProcess.Count == 0) return false;

            var enumerator = typesToProcess.GetEnumerator();
            enumerator.MoveNext();
            next = enumerator.Current;

            typesToProcess.Remove(next);
            processedTypes.Add(next);

            return true;
        }
    }
}
