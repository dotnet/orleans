using Microsoft.CodeAnalysis;

namespace Orleans.Analyzers
{
    internal static class SymbolHelpers
    {
        public static bool HasAttribute(this ISymbol symbol, INamedTypeSymbol attributeSymbol)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (attributeSymbol.Equals(attribute.AttributeClass, SymbolEqualityComparer.Default))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
