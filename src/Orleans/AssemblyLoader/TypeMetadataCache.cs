using System;
using System.Collections.Concurrent;
using System.Reflection;

using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Cache of type metadata.
    /// </summary>
    internal class TypeMetadataCache
    {
        /// <summary>
        /// The mapping between grain types and the corresponding type for the <see cref="IGrainMethodInvoker"/> implementation.
        /// </summary>
        private readonly ConcurrentDictionary<Type, Type> grainToInvokerMapping = new ConcurrentDictionary<Type, Type>();

        /// <summary>
        /// The mapping between grain types and the corresponding type for the <see cref="GrainReference"/> implementation.
        /// </summary>
        private readonly ConcurrentDictionary<Type, Type> grainToReferenceMapping = new ConcurrentDictionary<Type, Type>();

        public void FindSupportClasses(Type type)
        {
            var typeInfo = type.GetTypeInfo();
            var invokerAttr = typeInfo.GetCustomAttribute<MethodInvokerAttribute>(false);
            if (invokerAttr != null)
            {
                this.grainToInvokerMapping.TryAdd(invokerAttr.TargetType, type);
            }

            var grainReferenceAttr = typeInfo.GetCustomAttribute<GrainReferenceAttribute>(false);
            if (grainReferenceAttr != null)
            {
                this.grainToReferenceMapping.TryAdd(grainReferenceAttr.TargetType, type);
            }
        }

        public Type GetGrainReferenceType(Type interfaceType)
        {
            var typeInfo = interfaceType.GetTypeInfo();
            CodeGeneratorManager.GenerateAndCacheCodeForAssembly(typeInfo.Assembly);
            var genericInterfaceType = interfaceType.IsConstructedGenericType
                                           ? typeInfo.GetGenericTypeDefinition()
                                           : interfaceType;

            if (!typeof(IAddressable).IsAssignableFrom(interfaceType))
            {
                throw new InvalidCastException(
                    $"Target interface must be derived from {typeof(IAddressable).FullName} - cannot handle {interfaceType}");
            }

            // Try to find the correct GrainReference type for this interface.
            Type grainReferenceType;
            if (!this.grainToReferenceMapping.TryGetValue(genericInterfaceType, out grainReferenceType))
            {
                throw new InvalidOperationException(
                    $"Cannot find generated {nameof(GrainReference)} class for interface '{interfaceType}'");
            }

            if (interfaceType.IsConstructedGenericType)
            {
                grainReferenceType = grainReferenceType.MakeGenericType(typeInfo.GenericTypeArguments);
            }

            if (!typeof(IAddressable).IsAssignableFrom(grainReferenceType))
            {
                // This represents an internal programming error.
                throw new InvalidCastException(
                    $"Target reference type must be derived from {typeof(IAddressable).FullName}- cannot handle {grainReferenceType}");
            }

            return grainReferenceType;
        }

        public Type GetGrainMethodInvokerType(Type interfaceType)
        {
            var typeInfo = interfaceType.GetTypeInfo();
            CodeGeneratorManager.GenerateAndCacheCodeForAssembly(typeInfo.Assembly);
            var genericInterfaceType = interfaceType.IsConstructedGenericType
                                           ? typeInfo.GetGenericTypeDefinition()
                                           : interfaceType;

            // Try to find the correct IGrainMethodInvoker type for this interface.
            Type invokerType;
            if (!this.grainToInvokerMapping.TryGetValue(genericInterfaceType, out invokerType))
            {
                throw new InvalidOperationException(
                    $"Cannot find generated {nameof(IGrainMethodInvoker)} implementation for interface '{interfaceType}'");
            }

            if (interfaceType.IsConstructedGenericType)
            {
                invokerType = invokerType.MakeGenericType(typeInfo.GenericTypeArguments);
            }

            return invokerType;
        }
    }
}
