using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a well-known type ID mapping.
/// </summary>
internal readonly record struct WellKnownTypeIdModel(TypeRef Type, uint Id);

/// <summary>
/// Describes a type alias mapping.
/// </summary>
internal readonly record struct TypeAliasModel(TypeRef Type, string Alias);

/// <summary>
/// A single component in a compound type alias path.
/// </summary>
internal readonly record struct CompoundAliasComponentModel
{
    public CompoundAliasComponentModel(string stringValue)
    {
        StringValue = stringValue;
        TypeValue = TypeRef.Empty;
        IsType = false;
    }

    public CompoundAliasComponentModel(TypeRef typeValue)
    {
        StringValue = null;
        TypeValue = typeValue;
        IsType = true;
    }

    public bool IsString => !IsType && StringValue is not null;
    public bool IsType { get; }
    public string? StringValue { get; }
    public TypeRef TypeValue { get; }

}

/// <summary>
/// Describes a compound type alias entry (a path of components mapping to a type).
/// </summary>
internal readonly struct CompoundTypeAliasModel(ImmutableArray<CompoundAliasComponentModel> components, TypeRef targetType) : IEquatable<CompoundTypeAliasModel>
{
    public ImmutableArray<CompoundAliasComponentModel> Components { get; } = StructuralEquality.Normalize(components);
    public TypeRef TargetType { get; } = targetType;

    public bool Equals(CompoundTypeAliasModel other) =>
        StructuralEquality.SequenceEqual(Components, other.Components) && TargetType.Equals(other.TargetType);

    public override bool Equals(object obj) => obj is CompoundTypeAliasModel other && Equals(other);
    public override int GetHashCode() { unchecked { return StructuralEquality.GetSequenceHashCode(Components) * 31 + TargetType.GetHashCode(); } }

    public static bool operator ==(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => left.Equals(right);
    public static bool operator !=(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => !left.Equals(right);
}

/// <summary>
/// Describes an interface implementation (a concrete type implementing an invokable interface).
/// </summary>
internal readonly record struct InterfaceImplementationModel
{
    public InterfaceImplementationModel(TypeRef implementationType, SourceLocationModel sourceLocation = default)
    {
        ImplementationType = implementationType;
        SourceLocation = sourceLocation;
    }

    public TypeRef ImplementationType { get; }
    public SourceLocationModel SourceLocation { get; }
}

/// <summary>
/// Aggregated data extracted from referenced assemblies via <c>[GenerateCodeForDeclaringAssembly]</c>
/// and <c>[ApplicationPart]</c> attributes. This model is produced by a <c>CompilationProvider</c>-based
/// pipeline and cached via structural equality.
/// </summary>
internal sealed class ReferenceAssemblyModel(
    string assemblyName,
    ImmutableArray<string> applicationParts,
    ImmutableArray<WellKnownTypeIdModel> wellKnownTypeIds,
    ImmutableArray<TypeAliasModel> typeAliases,
    ImmutableArray<CompoundTypeAliasModel> compoundTypeAliases,
    ImmutableArray<SerializableTypeModel> referencedSerializableTypes,
    ImmutableArray<ProxyInterfaceModel> referencedProxyInterfaces,
    ImmutableArray<RegisteredCodecModel> registeredCodecs,
    ImmutableArray<InterfaceImplementationModel> interfaceImplementations) : IEquatable<ReferenceAssemblyModel>
{
    public string AssemblyName { get; } = assemblyName;
    public ImmutableArray<string> ApplicationParts { get; } = StructuralEquality.Normalize(applicationParts);
    public ImmutableArray<WellKnownTypeIdModel> WellKnownTypeIds { get; } = StructuralEquality.Normalize(wellKnownTypeIds);
    public ImmutableArray<TypeAliasModel> TypeAliases { get; } = StructuralEquality.Normalize(typeAliases);
    public ImmutableArray<CompoundTypeAliasModel> CompoundTypeAliases { get; } = StructuralEquality.Normalize(compoundTypeAliases);
    public ImmutableArray<SerializableTypeModel> ReferencedSerializableTypes { get; } = StructuralEquality.Normalize(referencedSerializableTypes);
    public ImmutableArray<ProxyInterfaceModel> ReferencedProxyInterfaces { get; } = StructuralEquality.Normalize(referencedProxyInterfaces);
    public ImmutableArray<RegisteredCodecModel> RegisteredCodecs { get; } = StructuralEquality.Normalize(registeredCodecs);
    public ImmutableArray<InterfaceImplementationModel> InterfaceImplementations { get; } = StructuralEquality.Normalize(interfaceImplementations);

    public bool Equals(ReferenceAssemblyModel other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal)
            && StructuralEquality.SequenceEqual(ApplicationParts, other.ApplicationParts)
            && StructuralEquality.SequenceEqual(WellKnownTypeIds, other.WellKnownTypeIds)
            && StructuralEquality.SequenceEqual(TypeAliases, other.TypeAliases)
            && StructuralEquality.SequenceEqual(CompoundTypeAliases, other.CompoundTypeAliases)
            && StructuralEquality.SequenceEqual(ReferencedSerializableTypes, other.ReferencedSerializableTypes)
            && StructuralEquality.SequenceEqual(ReferencedProxyInterfaces, other.ReferencedProxyInterfaces)
            && StructuralEquality.SequenceEqual(RegisteredCodecs, other.RegisteredCodecs)
            && StructuralEquality.SequenceEqual(InterfaceImplementations, other.InterfaceImplementations);
    }

    public override bool Equals(object obj) => obj is ReferenceAssemblyModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ApplicationParts);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(WellKnownTypeIds);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(TypeAliases);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(CompoundTypeAliases);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ReferencedSerializableTypes);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ReferencedProxyInterfaces);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(RegisteredCodecs);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(InterfaceImplementations);
            return hash;
        }
    }
}
