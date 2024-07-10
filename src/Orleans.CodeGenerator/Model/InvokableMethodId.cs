using Microsoft.CodeAnalysis;
using System;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Identifies an invokable method.
    /// </summary>
    internal readonly struct InvokableMethodId(InvokableMethodProxyBase proxyBaseInfo, INamedTypeSymbol interfaceType, IMethodSymbol method) : IEquatable<InvokableMethodId>
    {
        /// <summary>
        /// Gets the proxy base information for the method (eg, GrainReference, whether it is an extension).
        /// </summary>
        public InvokableMethodProxyBase ProxyBase { get; } = proxyBaseInfo;

        /// <summary>
        /// Gets the method symbol.
        /// </summary>
        public IMethodSymbol Method { get; } = method;

        /// <summary>
        /// Gets the containing interface symbol.
        /// </summary>
        public INamedTypeSymbol InterfaceType { get; } = interfaceType;

        public bool Equals(InvokableMethodId other) =>
            ProxyBase.Equals(other.ProxyBase)
            && SymbolEqualityComparer.Default.Equals(Method, other.Method)
            && SymbolEqualityComparer.Default.Equals(InterfaceType, other.InterfaceType);

        public override bool Equals(object obj) => obj is InvokableMethodId imd && Equals(imd);
        public override int GetHashCode()
        {
            unchecked
            {
                return ProxyBase.GetHashCode()
                    * 17 ^ SymbolEqualityComparer.Default.GetHashCode(Method)
                    * 17 ^ SymbolEqualityComparer.Default.GetHashCode(InterfaceType);
            }
        }

        public override string ToString() => $"{ProxyBase}/{InterfaceType.Name}/{Method.Name}";
    }
}