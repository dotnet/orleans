using System.Linq;
using Microsoft.Extensions.Logging;

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
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger log;

        /// <summary>
        /// The types to process.
        /// </summary>
        private readonly HashSet<Type> typesToProcess;

        /// <summary>
        /// The processed types.
        /// </summary>
        private readonly HashSet<Type> processedTypes;

        /// <summary>
        /// The set of types which are known to have existing serializers.
        /// </summary>
        private readonly HashSet<Type> typesToIgnore;

        /// <summary>
        /// Initializes members of the <see cref="SerializerGenerationManager"/> class.
        /// </summary>
        internal SerializerGenerationManager(IEnumerable<Type> typesToIgnore, ILoggerFactory loggerFactory)
        {
            this.typesToIgnore = new HashSet<Type>(typesToIgnore);
            typesToProcess = new HashSet<Type>();
            processedTypes = new HashSet<Type>();

            log = loggerFactory.CreateLogger<SerializerGenerationManager>();
        }

        /// <summary>
        /// Returns true if <paramref name="type"/> is serializable, false otherwise.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>true if <paramref name="type"/> is serializable, false otherwise.</returns>
        private bool HasSerializer(Type type)
        {
            if (this.typesToIgnore.Contains(type)) return true;
            var typeInfo = type.GetTypeInfo();
            if (typeInfo.IsOrleansPrimitive()) return true;
            if (!typeInfo.IsGenericType) return false;
            var genericTypeDefinition = typeInfo.GetGenericTypeDefinition();
            return this.typesToIgnore.Contains(genericTypeDefinition) &&
                   typeInfo.GetGenericArguments().All(arg => HasSerializer(arg));
        }

        internal bool IsTypeRecorded(Type type)
        {
            return this.typesToProcess.Contains(type) || this.processedTypes.Contains(type);
        }

        internal bool IsTypeIgnored(Type type)
        {
            return this.typesToIgnore.Contains(type);
        }

        internal bool RecordType(Type t, Assembly targetAssembly, string logContext)
        {
            if (!TypeUtilities.IsAccessibleFromAssembly(t, targetAssembly))
            {
                return false;
            }

            if (t.IsGenericParameter || processedTypes.Contains(t) || typesToProcess.Contains(t)
                || typesToIgnore.Contains(t)
                || typeof (Exception).GetTypeInfo().IsAssignableFrom(t)
                || typeof (Delegate).GetTypeInfo().IsAssignableFrom(t)
                || typeof (Task<>).GetTypeInfo().IsAssignableFrom(t)) return false;

            if (t.IsArray)
            {
                RecordType(t.GetElementType(), targetAssembly, logContext);
                return false;
            }

            if (t.IsNestedFamily || t.IsNestedPrivate)
            {
                log.Warn(
                    ErrorCode.CodeGenIgnoringTypes,
                    "Skipping serializer generation for nested type {0}. If this type is used frequently, you may wish to consider making it non-nested.",
                    t.Name);
                return false;
            }

            if (t.IsConstructedGenericType)
            {
                var args = t.GetGenericArguments();
                foreach (var arg in args)
                {
                    if (log.IsEnabled(LogLevel.Trace)) logContext = "generic argument of type " + t.GetLogFormat();
                    RecordType(arg, targetAssembly, logContext);
                }
            }

            if (t.IsInterface || t.IsAbstract || t == typeof (object) || t == typeof (void)
                || GrainInterfaceUtils.IsTaskType(t)) return false;

            if (t.IsConstructedGenericType)
            {
                return RecordType(t.GetGenericTypeDefinition(), targetAssembly, logContext);
            }

            if (t.IsOrleansPrimitive() || this.HasSerializer(t) ||
                typeof(IAddressable).GetTypeInfo().IsAssignableFrom(t)) return false;

            if (t.Namespace != null && (t.Namespace.Equals("System") || t.Namespace.StartsWith("System.")))
            {
                if (log.IsEnabled(LogLevel.Debug))
                {
                    var message = $"System type {t.Name} may require a custom serializer for optimal performance. " +
                                  "If you use arguments of this type a lot, consider submitting a pull request to https://github.com/dotnet/orleans/ to add a custom serializer for it.";
                    log.Debug(ErrorCode.CodeGenSystemTypeRequiresSerializer, message);
                }
                return false;
            }

            if (TypeUtils.HasAllSerializationMethods(t)) return false;

            // For every field which is not marked as [NonSerialized], check that it is accessible from code.
            // If any of those fields are not accessible, then a serializer cannot be generated for this type.
            var skipSerializerGeneration =
                t.GetAllFields().Where(field => !field.IsNotSerialized())
                    .Any(field => !TypeUtilities.IsAccessibleFromAssembly(field.FieldType, targetAssembly));
            if (skipSerializerGeneration)
            {
                return false;
            }

            // Do not generate serializers for classes which require the use of serialization hooks.
            // Instead, a fallback serializer which supports those hooks can be used.
            if (DotNetSerializableUtilities.HasSerializationHookAttributes(t)) return false;

            if (!typesToProcess.Add(t)) return true;

            if (log.IsEnabled(LogLevel.Trace)) log.LogTrace($"Will generate serializer for type {t.GetLogFormat()} encountered from {logContext}");

            var interfaces = t.GetInterfaces().Where(x => x.IsConstructedGenericType);
            if (log.IsEnabled(LogLevel.Trace)) logContext = "generic argument of implemented interface on type " + t.GetLogFormat();
            foreach (var arg in interfaces.SelectMany(v => v.GetGenericArguments()))
            {
                RecordType(arg, targetAssembly, logContext);
            }

            return true;
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
