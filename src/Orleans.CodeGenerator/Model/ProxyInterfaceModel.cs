namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a mapping from a return type to an invokable base type (e.g., <c>ValueTask → ValueTaskRequest</c>).
/// </summary>
internal readonly record struct InvokableBaseTypeMapping(TypeRef ReturnType, TypeRef InvokableBaseType);

/// <summary>
/// Describes a proxy base type used for RPC proxy generation.
/// </summary>
internal sealed record class ProxyBaseModel(
    TypeRef ProxyBaseType,
    bool IsExtension,
    string GeneratedClassNameComponent,
    EquatableArray<InvokableBaseTypeMapping> InvokableBaseTypes);

/// <summary>
/// Describes a <c>[GenerateMethodSerializers]</c>-annotated interface for incremental proxy/invokable generation.
/// </summary>
internal sealed record class ProxyInterfaceModel(
    TypeRef InterfaceType,
    string Name,
    string GeneratedNamespace,
    EquatableArray<TypeParameterModel> TypeParameters,
    ProxyBaseModel ProxyBase,
    EquatableArray<MethodModel> Methods,
    SourceLocationModel SourceLocation = default,
    TypeMetadataIdentity MetadataIdentity = default);
