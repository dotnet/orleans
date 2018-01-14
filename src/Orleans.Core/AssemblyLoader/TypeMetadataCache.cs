using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.ApplicationParts;
using Orleans.CodeGeneration;
using Orleans.Hosting;
using Orleans.Metadata;

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
        private readonly Dictionary<Type, Type> grainToInvokerMapping = new Dictionary<Type, Type>();

        /// <summary>
        /// The mapping between grain types and the corresponding type for the <see cref="GrainReference"/> implementation.
        /// </summary>
        private readonly Dictionary<Type, Type> grainToReferenceMapping = new Dictionary<Type, Type>();

        public TypeMetadataCache(IApplicationPartManager applicationPartManager)
        {
            var grainInterfaceFeature = applicationPartManager.CreateAndPopulateFeature<GrainInterfaceFeature>();
            foreach (var grain in grainInterfaceFeature.Interfaces)
            {
                this.grainToInvokerMapping[grain.InterfaceType] = grain.InvokerType;
                this.grainToReferenceMapping[grain.InterfaceType] = grain.ReferenceType;
            }
        }

        public Type GetGrainReferenceType(Type interfaceType)
        {
            var typeInfo = interfaceType.GetTypeInfo();
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
