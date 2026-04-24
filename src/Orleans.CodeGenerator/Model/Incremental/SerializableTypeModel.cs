using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Describes a <c>[GenerateSerializer]</c>-annotated type for incremental pipeline caching and generation.
    /// Contains all data needed to generate a serializer, copier, and activator without holding <c>ISymbol</c> references.
    /// </summary>
    internal sealed class SerializableTypeModel : IEquatable<SerializableTypeModel>
    {
        public SerializableTypeModel(
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
            ObjectCreationStrategy creationStrategy)
        {
            Accessibility = accessibility;
            TypeSyntax = typeSyntax;
            HasComplexBaseType = hasComplexBaseType;
            IncludePrimaryConstructorParameters = includePrimaryConstructorParameters;
            BaseTypeSyntax = baseTypeSyntax;
            Namespace = ns;
            GeneratedNamespace = generatedNamespace;
            Name = name;
            IsValueType = isValueType;
            IsSealedType = isSealedType;
            IsAbstractType = isAbstractType;
            IsEnumType = isEnumType;
            IsGenericType = isGenericType;
            TypeParameters = ImmutableArrayValueComparer.Normalize(typeParameters);
            Members = ImmutableArrayValueComparer.Normalize(members);
            UseActivator = useActivator;
            IsEmptyConstructable = isEmptyConstructable;
            HasActivatorConstructor = hasActivatorConstructor;
            TrackReferences = trackReferences;
            OmitDefaultMemberValues = omitDefaultMemberValues;
            SerializationHooks = ImmutableArrayValueComparer.Normalize(serializationHooks);
            IsShallowCopyable = isShallowCopyable;
            IsUnsealedImmutable = isUnsealedImmutable;
            IsImmutable = isImmutable;
            IsExceptionType = isExceptionType;
            ActivatorConstructorParameters = ImmutableArrayValueComparer.Normalize(activatorConstructorParameters);
            CreationStrategy = creationStrategy;
        }

        public Accessibility Accessibility { get; }
        public TypeRef TypeSyntax { get; }
        public bool HasComplexBaseType { get; }
        public bool IncludePrimaryConstructorParameters { get; }
        public TypeRef BaseTypeSyntax { get; }
        public string Namespace { get; }
        public string GeneratedNamespace { get; }
        public string Name { get; }
        public bool IsValueType { get; }
        public bool IsSealedType { get; }
        public bool IsAbstractType { get; }
        public bool IsEnumType { get; }
        public bool IsGenericType { get; }
        public ImmutableArray<TypeParameterModel> TypeParameters { get; }
        public ImmutableArray<MemberModel> Members { get; }
        public bool UseActivator { get; }
        public bool IsEmptyConstructable { get; }
        public bool HasActivatorConstructor { get; }
        public bool TrackReferences { get; }
        public bool OmitDefaultMemberValues { get; }
        public ImmutableArray<TypeRef> SerializationHooks { get; }
        public bool IsShallowCopyable { get; }
        public bool IsUnsealedImmutable { get; }
        public bool IsImmutable { get; }
        public bool IsExceptionType { get; }
        public ImmutableArray<TypeRef> ActivatorConstructorParameters { get; }
        public ObjectCreationStrategy CreationStrategy { get; }

        public bool Equals(SerializableTypeModel other)
        {
            if (other is null)
            {
                return false;
            }

            return Accessibility == other.Accessibility
                && TypeSyntax.Equals(other.TypeSyntax)
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
                && ImmutableArrayValueComparer.Equals(TypeParameters, other.TypeParameters)
                && ImmutableArrayValueComparer.Equals(Members, other.Members)
                && UseActivator == other.UseActivator
                && IsEmptyConstructable == other.IsEmptyConstructable
                && HasActivatorConstructor == other.HasActivatorConstructor
                && TrackReferences == other.TrackReferences
                && OmitDefaultMemberValues == other.OmitDefaultMemberValues
                && ImmutableArrayValueComparer.Equals(SerializationHooks, other.SerializationHooks)
                && IsShallowCopyable == other.IsShallowCopyable
                && IsUnsealedImmutable == other.IsUnsealedImmutable
                && IsImmutable == other.IsImmutable
                && IsExceptionType == other.IsExceptionType
                && ImmutableArrayValueComparer.Equals(ActivatorConstructorParameters, other.ActivatorConstructorParameters)
                && CreationStrategy == other.CreationStrategy;
        }

        public override bool Equals(object obj) => obj is SerializableTypeModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = TypeSyntax.GetHashCode();
                hash = hash * 31 + (int)Accessibility;
                hash = hash * 31 + BaseTypeSyntax.GetHashCode();
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Namespace ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(GeneratedNamespace ?? string.Empty);
                hash = hash * 31 + (IsValueType ? 1 : 0);
                hash = hash * 31 + (IsSealedType ? 1 : 0);
                hash = hash * 31 + (IsAbstractType ? 1 : 0);
                hash = hash * 31 + (IsEnumType ? 1 : 0);
                hash = hash * 31 + (IsGenericType ? 1 : 0);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(TypeParameters);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(Members);
                hash = hash * 31 + (UseActivator ? 1 : 0);
                hash = hash * 31 + (TrackReferences ? 1 : 0);
                hash = hash * 31 + (OmitDefaultMemberValues ? 1 : 0);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(SerializationHooks);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(ActivatorConstructorParameters);
                hash = hash * 31 + (int)CreationStrategy;
                return hash;
            }
        }
    }
}
