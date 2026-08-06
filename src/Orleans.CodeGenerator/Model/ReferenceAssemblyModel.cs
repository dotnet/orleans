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
internal readonly record struct CompoundTypeAliasModel(EquatableArray<CompoundAliasComponentModel> Components, TypeRef TargetType);

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
internal sealed record class ReferenceAssemblyModel(
    string AssemblyName,
    EquatableArray<string> ApplicationParts,
    EquatableArray<WellKnownTypeIdModel> WellKnownTypeIds,
    EquatableArray<TypeAliasModel> TypeAliases,
    EquatableArray<CompoundTypeAliasModel> CompoundTypeAliases,
    EquatableArray<SerializableTypeModel> ReferencedSerializableTypes,
    EquatableArray<ProxyInterfaceModel> ReferencedProxyInterfaces,
    EquatableArray<RegisteredCodecModel> RegisteredCodecs,
    EquatableArray<InterfaceImplementationModel> InterfaceImplementations);
