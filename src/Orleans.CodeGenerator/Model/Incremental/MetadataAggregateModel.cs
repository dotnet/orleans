using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model.Incremental
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
            SerializableTypes = ImmutableArrayValueComparer.Normalize(serializableTypes);
            ProxyInterfaces = ImmutableArrayValueComparer.Normalize(proxyInterfaces);
            RegisteredCodecs = ImmutableArrayValueComparer.Normalize(registeredCodecs);
            ReferenceAssemblyData = referenceAssemblyData;
            ActivatableTypes = ImmutableArrayValueComparer.Normalize(activatableTypes);
            GeneratedProxyTypes = ImmutableArrayValueComparer.Normalize(generatedProxyTypes);
            InvokableInterfaces = ImmutableArrayValueComparer.Normalize(invokableInterfaces);
            InterfaceImplementations = ImmutableArrayValueComparer.Normalize(interfaceImplementations);
            DefaultCopiers = ImmutableArrayValueComparer.Normalize(defaultCopiers);
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
                && ImmutableArrayValueComparer.Equals(SerializableTypes, other.SerializableTypes)
                && ImmutableArrayValueComparer.Equals(ProxyInterfaces, other.ProxyInterfaces)
                && ImmutableArrayValueComparer.Equals(RegisteredCodecs, other.RegisteredCodecs)
                && ReferenceAssemblyData.Equals(other.ReferenceAssemblyData)
                && ImmutableArrayValueComparer.Equals(ActivatableTypes, other.ActivatableTypes)
                && ImmutableArrayValueComparer.Equals(GeneratedProxyTypes, other.GeneratedProxyTypes)
                && ImmutableArrayValueComparer.Equals(InvokableInterfaces, other.InvokableInterfaces)
                && ImmutableArrayValueComparer.Equals(InterfaceImplementations, other.InterfaceImplementations)
                && ImmutableArrayValueComparer.Equals(DefaultCopiers, other.DefaultCopiers);
        }

        public override bool Equals(object obj) => obj is MetadataAggregateModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(SerializableTypes);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(ProxyInterfaces);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(RegisteredCodecs);
                hash = hash * 31 + ReferenceAssemblyData.GetHashCode();
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(ActivatableTypes);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(GeneratedProxyTypes);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(InvokableInterfaces);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(InterfaceImplementations);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(DefaultCopiers);
                return hash;
            }
        }
    }

    /// <summary>
    /// Describes a default copier mapping (type → copier type) for shallow-copyable types.
    /// </summary>
    internal readonly struct DefaultCopierModel : IEquatable<DefaultCopierModel>
    {
        public DefaultCopierModel(TypeRef originalType, TypeRef copierType)
        {
            OriginalType = originalType;
            CopierType = copierType;
        }

        public TypeRef OriginalType { get; }
        public TypeRef CopierType { get; }

        public bool Equals(DefaultCopierModel other) =>
            OriginalType.Equals(other.OriginalType) && CopierType.Equals(other.CopierType);

        public override bool Equals(object obj) => obj is DefaultCopierModel other && Equals(other);
        public override int GetHashCode() { unchecked { return OriginalType.GetHashCode() * 31 + CopierType.GetHashCode(); } }

        public static bool operator ==(DefaultCopierModel left, DefaultCopierModel right) => left.Equals(right);
        public static bool operator !=(DefaultCopierModel left, DefaultCopierModel right) => !left.Equals(right);
    }
}
