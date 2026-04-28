using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model
{
    /// <summary>
    /// Combined model aggregating all pipeline outputs for metadata class generation.
    /// This is the input to the final <c>RegisterSourceOutput</c> that produces the
    /// <c>Metadata_{AssemblyName}</c> class and assembly-level attributes.
    /// </summary>
    internal sealed class MetadataAggregateModel : IEquatable<MetadataAggregateModel>
    {
        public MetadataAggregateModel(
            string assemblyName,
            ImmutableArray<SerializableTypeModel> serializableTypes,
            ImmutableArray<ProxyInterfaceModel> proxyInterfaces,
            ImmutableArray<RegisteredCodecModel> registeredCodecs,
            ReferenceAssemblyModel referenceAssemblyData,
            ImmutableArray<TypeRef> activatableTypes,
            ImmutableArray<TypeRef> generatedProxyTypes,
            ImmutableArray<TypeRef> invokableInterfaces,
            ImmutableArray<InterfaceImplementationModel> interfaceImplementations,
            ImmutableArray<DefaultCopierModel> defaultCopiers)
        {
            AssemblyName = assemblyName;
            SerializableTypes = StructuralEquality.Normalize(serializableTypes);
            ProxyInterfaces = StructuralEquality.Normalize(proxyInterfaces);
            RegisteredCodecs = StructuralEquality.Normalize(registeredCodecs);
            ReferenceAssemblyData = referenceAssemblyData;
            ActivatableTypes = StructuralEquality.Normalize(activatableTypes);
            GeneratedProxyTypes = StructuralEquality.Normalize(generatedProxyTypes);
            InvokableInterfaces = StructuralEquality.Normalize(invokableInterfaces);
            InterfaceImplementations = StructuralEquality.Normalize(interfaceImplementations);
            DefaultCopiers = StructuralEquality.Normalize(defaultCopiers);
        }

        public string AssemblyName { get; }
        public ImmutableArray<SerializableTypeModel> SerializableTypes { get; }
        public ImmutableArray<ProxyInterfaceModel> ProxyInterfaces { get; }
        public ImmutableArray<RegisteredCodecModel> RegisteredCodecs { get; }
        public ReferenceAssemblyModel ReferenceAssemblyData { get; }
        public ImmutableArray<TypeRef> ActivatableTypes { get; }
        public ImmutableArray<TypeRef> GeneratedProxyTypes { get; }
        public ImmutableArray<TypeRef> InvokableInterfaces { get; }
        public ImmutableArray<InterfaceImplementationModel> InterfaceImplementations { get; }
        public ImmutableArray<DefaultCopierModel> DefaultCopiers { get; }

        public bool Equals(MetadataAggregateModel other)
        {
            if (other is null)
            {
                return false;
            }

            return string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal)
                && StructuralEquality.SequenceEqual(SerializableTypes, other.SerializableTypes)
                && StructuralEquality.SequenceEqual(ProxyInterfaces, other.ProxyInterfaces)
                && StructuralEquality.SequenceEqual(RegisteredCodecs, other.RegisteredCodecs)
                && ReferenceAssemblyData.Equals(other.ReferenceAssemblyData)
                && StructuralEquality.SequenceEqual(ActivatableTypes, other.ActivatableTypes)
                && StructuralEquality.SequenceEqual(GeneratedProxyTypes, other.GeneratedProxyTypes)
                && StructuralEquality.SequenceEqual(InvokableInterfaces, other.InvokableInterfaces)
                && StructuralEquality.SequenceEqual(InterfaceImplementations, other.InterfaceImplementations)
                && StructuralEquality.SequenceEqual(DefaultCopiers, other.DefaultCopiers);
        }

        public override bool Equals(object obj) => obj is MetadataAggregateModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(SerializableTypes);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ProxyInterfaces);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(RegisteredCodecs);
                hash = hash * 31 + ReferenceAssemblyData.GetHashCode();
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ActivatableTypes);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(GeneratedProxyTypes);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(InvokableInterfaces);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(InterfaceImplementations);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(DefaultCopiers);
                return hash;
            }
        }
    }

    /// <summary>
    /// Describes a default copier mapping (type → copier type) for shallow-copyable types.
    /// </summary>
    internal readonly record struct DefaultCopierModel(TypeRef OriginalType, TypeRef CopierType);
}
