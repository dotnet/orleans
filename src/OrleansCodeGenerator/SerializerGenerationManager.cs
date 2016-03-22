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
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly TraceLogger Log;

        /// <summary>
        /// The types to process.
        /// </summary>
        private readonly HashSet<Type> TypesToProcess;

        /// <summary>
        /// The processed types.
        /// </summary>
        private readonly HashSet<Type> ProcessedTypes;
        
        /// <summary>
        /// The generic interface types whose type arguments needs serializators generation
        /// </summary>
        internal readonly HashSet<Type> KnownGenericIntefaceTypes;

        /// <summary>
        /// The generic base types whose type arguments needs serializators generation
        /// </summary>
        internal readonly HashSet<Type> KnownGenericBaseTypes;

        /// <summary>
        /// Initializes members of the <see cref="SerializerGenerationManager"/> class.
        /// </summary>
        internal SerializerGenerationManager()
        {
            TypesToProcess = new HashSet<Type>();
            ProcessedTypes = new HashSet<Type>();
            KnownGenericIntefaceTypes = new HashSet<Type>
            {
                typeof(Streams.IAsyncObserver<>),
                typeof(Streams.IAsyncStream<>),
                typeof(Streams.IAsyncObservable<>)
            };

            KnownGenericBaseTypes = new HashSet<Type>
            {
                typeof(Grain<>),
                typeof(Streams.StreamSubscriptionHandleImpl<>),
                typeof(Streams.StreamSubscriptionHandle<>)
            };

            Log = TraceLogger.GetLogger(typeof(SerializerGenerationManager).Name);
        }
        
        internal bool RecordTypeToGenerate(Type t, Module module, Assembly targetAssembly)
        {
            if (TypeUtilities.IsTypeIsInaccessibleForSerialization(t, module, targetAssembly))
            {
                return false;
            }

            var typeInfo = t.GetTypeInfo();

            if (typeInfo.IsGenericParameter || ProcessedTypes.Contains(t) || TypesToProcess.Contains(t)
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
                Log.Warn(
                    ErrorCode.CodeGenIgnoringTypes,
                    "Skipping serializer generation for nested type {0}. If this type is used frequently, you may wish to consider making it non-nested.",
                    t.Name);
            }

            if (typeInfo.IsConstructedGenericType)
            {
                var args = t.GetGenericArguments();
                foreach (var arg in args)
                {
                    RecordTypeToGenerate(arg, module, targetAssembly);
                }
            }

            if (typeInfo.IsInterface || typeInfo.IsAbstract || t == typeof (object) || t == typeof (void)
                || GrainInterfaceUtils.IsTaskType(t)) return false;

            if (typeInfo.IsConstructedGenericType)
            {
                return RecordTypeToGenerate(typeInfo.GetGenericTypeDefinition(), module, targetAssembly);
            }

            if (typeInfo.IsOrleansPrimitive() || (SerializationManager.GetSerializer(t) != null) ||
                typeof(IAddressable).GetTypeInfo().IsAssignableFrom(t)) return false;

            if (typeInfo.Namespace != null && (typeInfo.Namespace.Equals("System") || typeInfo.Namespace.StartsWith("System.")))
            {
                var message = "System type " + t.Name + " may require a custom serializer for optimal performance. "
                              + "If you use arguments of this type a lot, consider submitting a pull request to https://github.com/dotnet/orleans/ to add a custom serializer for it.";
                Log.Warn(ErrorCode.CodeGenSystemTypeRequiresSerializer, message);
                return false;
            }

            if (TypeUtils.HasAllSerializationMethods(t)) return false;

            // This check is here and not within TypeUtilities.IsTypeIsInaccessibleForSerialization() to prevent potential infinite recursions 
            var skipSerialzerGeneration = t.GetAllFields()
                .Any(
                    field => !field.IsNotSerialized &&
                        TypeUtilities.IsTypeIsInaccessibleForSerialization(
                            field.FieldType,
                            module,
                            targetAssembly));
            if (skipSerialzerGeneration)
            {
                return false;
            }

            TypesToProcess.Add(t);
            return true;
        }

        internal bool GetNextTypeToProcess(out Type next)
        {
            next = null;
            if (TypesToProcess.Count == 0) return false;

            var enumerator = TypesToProcess.GetEnumerator();
            enumerator.MoveNext();
            next = enumerator.Current;

            TypesToProcess.Remove(next);
            ProcessedTypes.Add(next);

            return true;
        }
    }
}
