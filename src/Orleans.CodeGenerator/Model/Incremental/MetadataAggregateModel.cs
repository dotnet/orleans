using System;

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
            EquatableArray<SerializableTypeModel> serializableTypes,
            EquatableArray<ProxyInterfaceModel> proxyInterfaces,
            EquatableArray<RegisteredCodecModel> registeredCodecs,
            ReferenceAssemblyModel referenceAssemblyData,
            EquatableArray<TypeRef> activatableTypes,
            EquatableArray<TypeRef> generatedProxyTypes,
            EquatableArray<TypeRef> invokableInterfaces,
            EquatableArray<InterfaceImplementationModel> interfaceImplementations,
            EquatableArray<DefaultCopierModel> defaultCopiers)
        {
            AssemblyName = assemblyName;
            SerializableTypes = serializableTypes;
            ProxyInterfaces = proxyInterfaces;
            RegisteredCodecs = registeredCodecs;
            ReferenceAssemblyData = referenceAssemblyData;
            ActivatableTypes = activatableTypes;
            GeneratedProxyTypes = generatedProxyTypes;
            InvokableInterfaces = invokableInterfaces;
            InterfaceImplementations = interfaceImplementations;
            DefaultCopiers = defaultCopiers;
        }

        public string AssemblyName { get; }
        public EquatableArray<SerializableTypeModel> SerializableTypes { get; }
        public EquatableArray<ProxyInterfaceModel> ProxyInterfaces { get; }
        public EquatableArray<RegisteredCodecModel> RegisteredCodecs { get; }
        public ReferenceAssemblyModel ReferenceAssemblyData { get; }
        public EquatableArray<TypeRef> ActivatableTypes { get; }
        public EquatableArray<TypeRef> GeneratedProxyTypes { get; }
        public EquatableArray<TypeRef> InvokableInterfaces { get; }
        public EquatableArray<InterfaceImplementationModel> InterfaceImplementations { get; }
        public EquatableArray<DefaultCopierModel> DefaultCopiers { get; }

        public bool Equals(MetadataAggregateModel other)
        {
            if (other is null)
            {
                return false;
            }

            return string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal)
                && SerializableTypes.Equals(other.SerializableTypes)
                && ProxyInterfaces.Equals(other.ProxyInterfaces)
                && RegisteredCodecs.Equals(other.RegisteredCodecs)
                && ReferenceAssemblyData.Equals(other.ReferenceAssemblyData)
                && ActivatableTypes.Equals(other.ActivatableTypes)
                && GeneratedProxyTypes.Equals(other.GeneratedProxyTypes)
                && InvokableInterfaces.Equals(other.InvokableInterfaces)
                && InterfaceImplementations.Equals(other.InterfaceImplementations)
                && DefaultCopiers.Equals(other.DefaultCopiers);
        }

        public override bool Equals(object obj) => obj is MetadataAggregateModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
                hash = hash * 31 + SerializableTypes.GetHashCode();
                hash = hash * 31 + ProxyInterfaces.GetHashCode();
                hash = hash * 31 + RegisteredCodecs.GetHashCode();
                hash = hash * 31 + ReferenceAssemblyData.GetHashCode();
                hash = hash * 31 + ActivatableTypes.GetHashCode();
                hash = hash * 31 + GeneratedProxyTypes.GetHashCode();
                hash = hash * 31 + InvokableInterfaces.GetHashCode();
                hash = hash * 31 + InterfaceImplementations.GetHashCode();
                hash = hash * 31 + DefaultCopiers.GetHashCode();
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
