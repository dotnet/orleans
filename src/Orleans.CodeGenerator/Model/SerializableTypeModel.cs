using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model;

/// <summary>
/// Describes a <c>[GenerateSerializer]</c>-annotated type for incremental discovery and metadata generation.
/// Serializer, copier, and activator emission resolves additional semantic dependencies against the current compilation.
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
