using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Describes a mapping from a return type to an invokable base type (e.g., <c>ValueTask → ValueTaskRequest</c>).
    /// </summary>
    internal readonly struct InvokableBaseTypeMapping : IEquatable<InvokableBaseTypeMapping>
    {
        public InvokableBaseTypeMapping(TypeRef returnType, TypeRef invokableBaseType)
        {
            ReturnType = returnType;
            InvokableBaseType = invokableBaseType;
        }

        public TypeRef ReturnType { get; }
        public TypeRef InvokableBaseType { get; }

        public bool Equals(InvokableBaseTypeMapping other) =>
            ReturnType.Equals(other.ReturnType) && InvokableBaseType.Equals(other.InvokableBaseType);

        public override bool Equals(object obj) => obj is InvokableBaseTypeMapping other && Equals(other);

        public override int GetHashCode()
        {
            unchecked { return ReturnType.GetHashCode() * 31 + InvokableBaseType.GetHashCode(); }
        }

        public static bool operator ==(InvokableBaseTypeMapping left, InvokableBaseTypeMapping right) => left.Equals(right);
        public static bool operator !=(InvokableBaseTypeMapping left, InvokableBaseTypeMapping right) => !left.Equals(right);
    }

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
            InvokableBaseTypes = ImmutableArrayValueComparer.Normalize(invokableBaseTypes);
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
                && ImmutableArrayValueComparer.Equals(InvokableBaseTypes, other.InvokableBaseTypes);
        }

        public override bool Equals(object obj) => obj is ProxyBaseModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = ProxyBaseType.GetHashCode();
                hash = hash * 31 + (IsExtension ? 1 : 0);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(GeneratedClassNameComponent ?? string.Empty);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(InvokableBaseTypes);
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
            TypeParameters = ImmutableArrayValueComparer.Normalize(typeParameters);
            ProxyBase = proxyBase;
            Methods = ImmutableArrayValueComparer.Normalize(methods);
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
                && ImmutableArrayValueComparer.Equals(TypeParameters, other.TypeParameters)
                && ProxyBase.Equals(other.ProxyBase)
                && ImmutableArrayValueComparer.Equals(Methods, other.Methods)
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
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(TypeParameters);
                hash = hash * 31 + (ProxyBase?.GetHashCode() ?? 0);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(Methods);
                hash = hash * 31 + SourceLocation.GetHashCode();
                return hash;
            }
        }
    }
}
