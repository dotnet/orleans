using Microsoft.CodeAnalysis;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Orleans.CodeGenerator
{
    public static class PropertyUtility
    {
        public static IPropertySymbol GetMatchingProperty(IFieldSymbol field)
        {
            var propertyName = Regex.Match(field.Name, "^<([^>]+)>.*$");
            if (!propertyName.Success || field.ContainingType is null)
            {
                return null;
            }

            var name = propertyName.Groups[1].Value;
            var candidates = field.ContainingType
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => string.Equals(name, p.Name, StringComparison.Ordinal) && !p.IsAbstract && !p.IsStatic)
                .ToArray();

            if (candidates.Length != 1)
            {
                return null;
            }

            if (!SymbolEqualityComparer.Default.Equals(field.Type, candidates[0].Type))
            {
                return null;
            }

            return candidates[0];
        }
    }
}