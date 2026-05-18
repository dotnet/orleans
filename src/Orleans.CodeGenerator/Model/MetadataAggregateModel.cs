namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Combined model aggregating all pipeline outputs for metadata class generation.
/// This is the input to the final <c>RegisterSourceOutput</c> that produces the
/// <c>Metadata_{AssemblyName}</c> class and assembly-level attributes.
/// </summary>
internal sealed record class MetadataAggregateModel(
    string AssemblyName,
    EquatableArray<SerializableTypeModel> SerializableTypes,
    EquatableArray<ProxyInterfaceModel> ProxyInterfaces,
    EquatableArray<RegisteredCodecModel> RegisteredCodecs,
    ReferenceAssemblyModel ReferenceAssemblyData,
    EquatableArray<TypeRef> ActivatableTypes,
    EquatableArray<TypeRef> GeneratedProxyTypes,
    EquatableArray<TypeRef> InvokableInterfaces,
    EquatableArray<string> GeneratedInvokableActivatorMetadataNames,
    EquatableArray<InterfaceImplementationModel> InterfaceImplementations,
    EquatableArray<DefaultCopierModel> DefaultCopiers);

/// <summary>
/// Describes a default copier mapping (type → copier type) for shallow-copyable types.
/// </summary>
internal readonly record struct DefaultCopierModel(TypeRef OriginalType, TypeRef CopierType);
