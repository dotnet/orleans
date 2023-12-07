using Microsoft.CodeAnalysis;
using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Identifies a proxy base, including whether the proxy is a grain reference or extension.
    /// </summary>
    internal readonly struct InvokableMethodProxyBaseId : IEquatable<InvokableMethodProxyBaseId>
    {
        public InvokableMethodProxyBaseId(INamedTypeSymbol type, bool isExtension)
        {
            if (!SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition))
            {
                throw new ArgumentException("Type must be an original definition. This is a code generator bug.");
            }

            ProxyBaseType = type;
            IsExtension = isExtension;

            if (IsExtension)
            {
                CompositeAliasComponents = ImmutableArray.Create(new CompoundTypeAliasComponent[] { new(ProxyBaseType), new("Ext") });
                GeneratedClassNameComponent = $"{ProxyBaseType.Name}_Ext";
            }
            else
            {
                CompositeAliasComponents = ImmutableArray.Create(new CompoundTypeAliasComponent[] { new(ProxyBaseType) });
                GeneratedClassNameComponent = ProxyBaseType.Name;
            }
        }

        /// <summary>
        /// Gets the proxy base type, eg <c>GrainReference</c>.
        /// </summary>
        public INamedTypeSymbol ProxyBaseType { get; }

        /// <summary>
        /// Gets a value indicating whether this descriptor represents an extension.
        /// </summary>
        public bool IsExtension { get; }

        /// <summary>
        /// Gets the components of the compound type alias used to refer to this proxy base.
        /// </summary>
        public ImmutableArray<CompoundTypeAliasComponent> CompositeAliasComponents { get; }

        /// <summary>
        /// Gets a string used to distinguish this proxy base from others in generated class names.
        /// </summary>
        public string GeneratedClassNameComponent { get; }

        public bool Equals(InvokableMethodProxyBaseId other) => SymbolEqualityComparer.Default.Equals(ProxyBaseType, other.ProxyBaseType) && IsExtension == other.IsExtension;
        public override bool Equals(object obj) => obj is InvokableMethodProxyBaseId other && Equals(other);
        public override int GetHashCode() => IsExtension.GetHashCode() * 17 ^ SymbolEqualityComparer.Default.GetHashCode(ProxyBaseType);
        public override string ToString() => GeneratedClassNameComponent;
    }
}