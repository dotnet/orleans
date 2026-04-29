using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Combined model aggregating all pipeline outputs for metadata class generation.
/// This is the input to the final <c>RegisterSourceOutput</c> that produces the
/// <c>Metadata_{AssemblyName}</c> class and assembly-level attributes.
/// </summary>
internal sealed class MetadataAggregateModel(
    string assemblyName,
    ImmutableArray<SerializableTypeModel> serializableTypes,
    ImmutableArray<ProxyInterfaceModel> proxyInterfaces,
    ImmutableArray<RegisteredCodecModel> registeredCodecs,
    ReferenceAssemblyModel referenceAssemblyData,
    ImmutableArray<TypeRef> activatableTypes,
    ImmutableArray<TypeRef> generatedProxyTypes,
    ImmutableArray<TypeRef> invokableInterfaces,
    ImmutableArray<string> generatedInvokableActivatorMetadataNames,
    ImmutableArray<InterfaceImplementationModel> interfaceImplementations,
    ImmutableArray<DefaultCopierModel> defaultCopiers) : IEquatable<MetadataAggregateModel>
{
    public string AssemblyName { get; } = assemblyName;
    public ImmutableArray<SerializableTypeModel> SerializableTypes { get; } = StructuralEquality.Normalize(serializableTypes);
    public ImmutableArray<ProxyInterfaceModel> ProxyInterfaces { get; } = StructuralEquality.Normalize(proxyInterfaces);
    public ImmutableArray<RegisteredCodecModel> RegisteredCodecs { get; } = StructuralEquality.Normalize(registeredCodecs);
    public ReferenceAssemblyModel ReferenceAssemblyData { get; } = referenceAssemblyData;
    public ImmutableArray<TypeRef> ActivatableTypes { get; } = StructuralEquality.Normalize(activatableTypes);
    public ImmutableArray<TypeRef> GeneratedProxyTypes { get; } = StructuralEquality.Normalize(generatedProxyTypes);
    public ImmutableArray<TypeRef> InvokableInterfaces { get; } = StructuralEquality.Normalize(invokableInterfaces);
    public ImmutableArray<string> GeneratedInvokableActivatorMetadataNames { get; } = StructuralEquality.Normalize(generatedInvokableActivatorMetadataNames);
    public ImmutableArray<InterfaceImplementationModel> InterfaceImplementations { get; } = StructuralEquality.Normalize(interfaceImplementations);
    public ImmutableArray<DefaultCopierModel> DefaultCopiers { get; } = StructuralEquality.Normalize(defaultCopiers);

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
            && StructuralEquality.SequenceEqual(GeneratedInvokableActivatorMetadataNames, other.GeneratedInvokableActivatorMetadataNames)
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
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(GeneratedInvokableActivatorMetadataNames);
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
