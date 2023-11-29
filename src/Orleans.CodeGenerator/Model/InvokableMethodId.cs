using Microsoft.CodeAnalysis;
using System;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Identifies an invokable method.
    /// </summary>
    internal readonly struct InvokableMethodId : IEquatable<InvokableMethodId>
    {
        public InvokableMethodId(InvokableMethodProxyBase proxyBaseInfo, IMethodSymbol method)
        {
            ProxyBase = proxyBaseInfo;
            Method = method;
        }

        /// <summary>
        /// Gets the proxy base information for the method (eg, GrainReference, whether it is an extension).
        /// </summary>
        public InvokableMethodProxyBase ProxyBase { get; }

        /// <summary>
        /// Gets the method symbol.
        /// </summary>
        public IMethodSymbol Method { get; }

        public bool Equals(InvokableMethodId other) =>
            ProxyBase.Equals(other.ProxyBase)
            && SymbolEqualityComparer.Default.Equals(Method, other.Method);

        public override bool Equals(object obj) => obj is InvokableMethodId imd && Equals(imd);
        public override int GetHashCode() => ProxyBase.GetHashCode() * 17 ^ SymbolEqualityComparer.Default.GetHashCode(Method);
        public override string ToString() => $"{ProxyBase}/{Method.ContainingType.Name}/{Method.Name}";
    }
}