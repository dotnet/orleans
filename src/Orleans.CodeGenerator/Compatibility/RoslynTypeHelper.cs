using System;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Compatibility
{
    internal static class RoslynTypeHelper
    {
        public static bool IsSystemNamespace(INamespaceSymbol ns)
        {
            if (ns is null || ns.IsGlobalNamespace) return false;
            if (ns.ContainingNamespace is INamespaceSymbol parent && !parent.IsGlobalNamespace) return IsSystemNamespace(parent);
            return string.Equals(ns.Name, "System", StringComparison.Ordinal) || ns.Name.StartsWith("System.", StringComparison.Ordinal);
        }
    }
}
