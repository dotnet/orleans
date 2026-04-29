using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a <c>[GenerateSerializer]</c>-annotated type for incremental pipeline caching and generation.
/// Contains all data needed to generate a serializer, copier, and activator without holding <c>ISymbol</c> references.
/// </summary>
internal sealed class SerializableTypeModel(
    Accessibility accessibility,
    TypeRef typeSyntax,
    bool hasComplexBaseType,
    bool includePrimaryConstructorParameters,
    TypeRef baseTypeSyntax,
    string ns,
    string generatedNamespace,
    string name,
    bool isValueType,
    bool isSealedType,
    bool isAbstractType,
    bool isEnumType,
    bool isGenericType,
    ImmutableArray<TypeParameterModel> typeParameters,
    ImmutableArray<MemberModel> members,
    bool useActivator,
    bool isEmptyConstructable,
    bool hasActivatorConstructor,
    bool trackReferences,
    bool omitDefaultMemberValues,
    ImmutableArray<TypeRef> serializationHooks,
    bool isShallowCopyable,
    bool isUnsealedImmutable,
    bool isImmutable,
    bool isExceptionType,
    ImmutableArray<TypeRef> activatorConstructorParameters,
    ObjectCreationStrategy creationStrategy,
    SourceLocationModel sourceLocation = default,
    TypeMetadataIdentity metadataIdentity = default) : IEquatable<SerializableTypeModel>
{
    public Accessibility Accessibility { get; } = accessibility;
    public TypeRef TypeSyntax { get; } = typeSyntax;
    public TypeMetadataIdentity MetadataIdentity { get; } = metadataIdentity;
    public bool HasComplexBaseType { get; } = hasComplexBaseType;
    public bool IncludePrimaryConstructorParameters { get; } = includePrimaryConstructorParameters;
    public TypeRef BaseTypeSyntax { get; } = baseTypeSyntax;
    public string Namespace { get; } = ns;
    public string GeneratedNamespace { get; } = generatedNamespace;
    public string Name { get; } = name;
    public bool IsValueType { get; } = isValueType;
    public bool IsSealedType { get; } = isSealedType;
    public bool IsAbstractType { get; } = isAbstractType;
    public bool IsEnumType { get; } = isEnumType;
    public bool IsGenericType { get; } = isGenericType;
    public ImmutableArray<TypeParameterModel> TypeParameters { get; } = StructuralEquality.Normalize(typeParameters);
    public ImmutableArray<MemberModel> Members { get; } = StructuralEquality.Normalize(members);
    public bool UseActivator { get; } = useActivator;
    public bool IsEmptyConstructable { get; } = isEmptyConstructable;
    public bool HasActivatorConstructor { get; } = hasActivatorConstructor;
    public bool TrackReferences { get; } = trackReferences;
    public bool OmitDefaultMemberValues { get; } = omitDefaultMemberValues;
    public ImmutableArray<TypeRef> SerializationHooks { get; } = StructuralEquality.Normalize(serializationHooks);
    public bool IsShallowCopyable { get; } = isShallowCopyable;
    public bool IsUnsealedImmutable { get; } = isUnsealedImmutable;
    public bool IsImmutable { get; } = isImmutable;
    public bool IsExceptionType { get; } = isExceptionType;
    public ImmutableArray<TypeRef> ActivatorConstructorParameters { get; } = StructuralEquality.Normalize(activatorConstructorParameters);
    public ObjectCreationStrategy CreationStrategy { get; } = creationStrategy;
    public SourceLocationModel SourceLocation { get; } = sourceLocation;

    public bool Equals(SerializableTypeModel other)
    {
        if (other is null)
        {
            return false;
        }

        return Accessibility == other.Accessibility
            && TypeSyntax.Equals(other.TypeSyntax)
            && MetadataIdentity.Equals(other.MetadataIdentity)
            && HasComplexBaseType == other.HasComplexBaseType
            && IncludePrimaryConstructorParameters == other.IncludePrimaryConstructorParameters
            && BaseTypeSyntax.Equals(other.BaseTypeSyntax)
            && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
            && string.Equals(GeneratedNamespace, other.GeneratedNamespace, StringComparison.Ordinal)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && IsValueType == other.IsValueType
            && IsSealedType == other.IsSealedType
            && IsAbstractType == other.IsAbstractType
            && IsEnumType == other.IsEnumType
            && IsGenericType == other.IsGenericType
            && StructuralEquality.SequenceEqual(TypeParameters, other.TypeParameters)
            && StructuralEquality.SequenceEqual(Members, other.Members)
            && UseActivator == other.UseActivator
            && IsEmptyConstructable == other.IsEmptyConstructable
            && HasActivatorConstructor == other.HasActivatorConstructor
            && TrackReferences == other.TrackReferences
            && OmitDefaultMemberValues == other.OmitDefaultMemberValues
            && StructuralEquality.SequenceEqual(SerializationHooks, other.SerializationHooks)
            && IsShallowCopyable == other.IsShallowCopyable
            && IsUnsealedImmutable == other.IsUnsealedImmutable
            && IsImmutable == other.IsImmutable
            && IsExceptionType == other.IsExceptionType
            && StructuralEquality.SequenceEqual(ActivatorConstructorParameters, other.ActivatorConstructorParameters)
            && CreationStrategy == other.CreationStrategy
            && SourceLocation.Equals(other.SourceLocation);
    }

    public override bool Equals(object obj) => obj is SerializableTypeModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = TypeSyntax.GetHashCode();
            hash = hash * 31 + MetadataIdentity.GetHashCode();
            hash = hash * 31 + (int)Accessibility;
            hash = hash * 31 + (HasComplexBaseType ? 1 : 0);
            hash = hash * 31 + (IncludePrimaryConstructorParameters ? 1 : 0);
            hash = hash * 31 + BaseTypeSyntax.GetHashCode();
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Namespace ?? string.Empty);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(GeneratedNamespace ?? string.Empty);
            hash = hash * 31 + (IsValueType ? 1 : 0);
            hash = hash * 31 + (IsSealedType ? 1 : 0);
            hash = hash * 31 + (IsAbstractType ? 1 : 0);
            hash = hash * 31 + (IsEnumType ? 1 : 0);
            hash = hash * 31 + (IsGenericType ? 1 : 0);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(TypeParameters);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(Members);
            hash = hash * 31 + (UseActivator ? 1 : 0);
            hash = hash * 31 + (IsEmptyConstructable ? 1 : 0);
            hash = hash * 31 + (HasActivatorConstructor ? 1 : 0);
            hash = hash * 31 + (TrackReferences ? 1 : 0);
            hash = hash * 31 + (OmitDefaultMemberValues ? 1 : 0);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(SerializationHooks);
            hash = hash * 31 + (IsShallowCopyable ? 1 : 0);
            hash = hash * 31 + (IsUnsealedImmutable ? 1 : 0);
            hash = hash * 31 + (IsImmutable ? 1 : 0);
            hash = hash * 31 + (IsExceptionType ? 1 : 0);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ActivatorConstructorParameters);
            hash = hash * 31 + (int)CreationStrategy;
            hash = hash * 31 + SourceLocation.GetHashCode();
            return hash;
        }
    }
}
