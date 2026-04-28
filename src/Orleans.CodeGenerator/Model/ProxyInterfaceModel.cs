using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model
{
    /// <summary>
    /// Describes a mapping from a return type to an invokable base type (e.g., <c>ValueTask → ValueTaskRequest</c>).
    /// </summary>
    internal readonly record struct InvokableBaseTypeMapping(TypeRef ReturnType, TypeRef InvokableBaseType);

    /// <summary>
    /// Describes a proxy base type used for RPC proxy generation.
    /// </summary>
    internal sealed class ProxyBaseModel : IEquatable<ProxyBaseModel>
    {
        public ProxyBaseModel(
            TypeRef proxyBaseType,
            bool isExtension,
            string generatedClassNameComponent,
            ImmutableArray<InvokableBaseTypeMapping> invokableBaseTypes)
        {
            ProxyBaseType = proxyBaseType;
            IsExtension = isExtension;
            GeneratedClassNameComponent = generatedClassNameComponent;
            InvokableBaseTypes = StructuralEquality.Normalize(invokableBaseTypes);
        }

        public TypeRef ProxyBaseType { get; }
        public bool IsExtension { get; }
        public string GeneratedClassNameComponent { get; }
        public ImmutableArray<InvokableBaseTypeMapping> InvokableBaseTypes { get; }

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
    internal sealed class ProxyInterfaceModel : IEquatable<ProxyInterfaceModel>
    {
        public ProxyInterfaceModel(
            TypeRef interfaceType,
            string name,
            string generatedNamespace,
            ImmutableArray<TypeParameterModel> typeParameters,
            ProxyBaseModel proxyBase,
            ImmutableArray<MethodModel> methods,
            SourceLocationModel sourceLocation = default,
            TypeMetadataIdentity metadataIdentity = default)
        {
            InterfaceType = interfaceType;
            MetadataIdentity = metadataIdentity;
            Name = name;
            GeneratedNamespace = generatedNamespace;
            TypeParameters = StructuralEquality.Normalize(typeParameters);
            ProxyBase = proxyBase;
            Methods = StructuralEquality.Normalize(methods);
            SourceLocation = sourceLocation;
        }

        public TypeRef InterfaceType { get; }
        public TypeMetadataIdentity MetadataIdentity { get; }
        public string Name { get; }
        public string GeneratedNamespace { get; }
        public ImmutableArray<TypeParameterModel> TypeParameters { get; }
        public ProxyBaseModel ProxyBase { get; }
        public ImmutableArray<MethodModel> Methods { get; }
        public SourceLocationModel SourceLocation { get; }

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
}
