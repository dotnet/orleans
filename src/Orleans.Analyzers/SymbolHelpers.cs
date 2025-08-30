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

        public static bool DerivesFrom(this ITypeSymbol symbol, ITypeSymbol candidateBaseType)
        {
            var baseType = symbol.BaseType;
            while (baseType is not null)
            {
                if (baseType.Equals(candidateBaseType, SymbolEqualityComparer.Default))
                {
                    return true;
                }

                baseType = baseType.BaseType;
            }

            return false;
        }
    }
}
