using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a mapping from a return type to an invokable base type (e.g., <c>ValueTask → ValueTaskRequest</c>).
/// </summary>
internal readonly record struct InvokableBaseTypeMapping(TypeRef ReturnType, TypeRef InvokableBaseType);

/// <summary>
/// Describes a proxy base type used for RPC proxy generation.
/// </summary>
internal sealed class ProxyBaseModel(
    TypeRef proxyBaseType,
    bool isExtension,
    string generatedClassNameComponent,
    ImmutableArray<InvokableBaseTypeMapping> invokableBaseTypes) : IEquatable<ProxyBaseModel>
{
    public TypeRef ProxyBaseType { get; } = proxyBaseType;
    public bool IsExtension { get; } = isExtension;
    public string GeneratedClassNameComponent { get; } = generatedClassNameComponent;
    public ImmutableArray<InvokableBaseTypeMapping> InvokableBaseTypes { get; } = StructuralEquality.Normalize(invokableBaseTypes);

    public bool Equals(ProxyBaseModel other)
    {
        if (other is null)
        {
            return false;
        }

        return ProxyBaseType.Equals(other.ProxyBaseType)
            && IsExtension == other.IsExtension
            && string.Equals(GeneratedClassNameComponent, other.GeneratedClassNameComponent, StringComparison.Ordinal)
            && StructuralEquality.SequenceEqual(InvokableBaseTypes, other.InvokableBaseTypes);
    }

    public override bool Equals(object obj) => obj is ProxyBaseModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = ProxyBaseType.GetHashCode();
            hash = hash * 31 + (IsExtension ? 1 : 0);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(GeneratedClassNameComponent ?? string.Empty);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(InvokableBaseTypes);
            return hash;
        }
    }
}

/// <summary>
/// Describes a <c>[GenerateMethodSerializers]</c>-annotated interface for incremental proxy/invokable generation.
/// </summary>
internal sealed class ProxyInterfaceModel(
    TypeRef interfaceType,
    string name,
    string generatedNamespace,
    ImmutableArray<TypeParameterModel> typeParameters,
    ProxyBaseModel proxyBase,
    ImmutableArray<MethodModel> methods,
    SourceLocationModel sourceLocation = default,
    TypeMetadataIdentity metadataIdentity = default) : IEquatable<ProxyInterfaceModel>
{
    public TypeRef InterfaceType { get; } = interfaceType;
    public TypeMetadataIdentity MetadataIdentity { get; } = metadataIdentity;
    public string Name { get; } = name;
    public string GeneratedNamespace { get; } = generatedNamespace;
    public ImmutableArray<TypeParameterModel> TypeParameters { get; } = StructuralEquality.Normalize(typeParameters);
    public ProxyBaseModel ProxyBase { get; } = proxyBase;
    public ImmutableArray<MethodModel> Methods { get; } = StructuralEquality.Normalize(methods);
    public SourceLocationModel SourceLocation { get; } = sourceLocation;

    public bool Equals(ProxyInterfaceModel other)
    {
        if (other is null)
        {
            return false;
        }

        return InterfaceType.Equals(other.InterfaceType)
            && MetadataIdentity.Equals(other.MetadataIdentity)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(GeneratedNamespace, other.GeneratedNamespace, StringComparison.Ordinal)
            && StructuralEquality.SequenceEqual(TypeParameters, other.TypeParameters)
            && ProxyBase.Equals(other.ProxyBase)
            && StructuralEquality.SequenceEqual(Methods, other.Methods)
            && SourceLocation.Equals(other.SourceLocation);
    }

    public override bool Equals(object obj) => obj is ProxyInterfaceModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = InterfaceType.GetHashCode();
            hash = hash * 31 + MetadataIdentity.GetHashCode();
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(GeneratedNamespace ?? string.Empty);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(TypeParameters);
            hash = hash * 31 + (ProxyBase?.GetHashCode() ?? 0);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(Methods);
            hash = hash * 31 + SourceLocation.GetHashCode();
            return hash;
        }
    }
}
