using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a <c>[GenerateSerializer]</c>-annotated type for incremental pipeline caching and generation.
/// Contains all data needed to generate a serializer, copier, and activator without holding <c>ISymbol</c> references.
/// </summary>
internal sealed record class SerializableTypeModel(
    Accessibility Accessibility,
    TypeRef TypeSyntax,
    bool HasComplexBaseType,
    bool IncludePrimaryConstructorParameters,
    TypeRef BaseTypeSyntax,
    string Namespace,
    string GeneratedNamespace,
    string Name,
    bool IsValueType,
    bool IsSealedType,
    bool IsAbstractType,
    bool IsEnumType,
    bool IsGenericType,
    EquatableArray<TypeParameterModel> TypeParameters,
    EquatableArray<MemberModel> Members,
    bool UseActivator,
    bool IsEmptyConstructable,
    bool HasActivatorConstructor,
    bool TrackReferences,
    bool OmitDefaultMemberValues,
    EquatableArray<TypeRef> SerializationHooks,
    bool IsShallowCopyable,
    bool IsUnsealedImmutable,
    bool IsImmutable,
    bool IsExceptionType,
    EquatableArray<TypeRef> ActivatorConstructorParameters,
    ObjectCreationStrategy CreationStrategy,
    SourceLocationModel SourceLocation = default,
    TypeMetadataIdentity MetadataIdentity = default);
