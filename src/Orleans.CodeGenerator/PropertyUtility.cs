#nullable enable
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Orleans.CodeGenerator
{
    public static class PropertyUtility
    {
        public static IPropertySymbol? GetMatchingProperty(IFieldSymbol field)
        {
            if (field.ContainingType is null)
                return null;
            return GetMatchingProperty(field, field.ContainingType.GetMembers());
        }

        public static IPropertySymbol? GetMatchingProperty(IFieldSymbol field, IEnumerable<ISymbol> memberSymbols)
        {
            var propertyName = Regex.Match(field.Name, "^<([^>]+)>.*$");
            if (!propertyName.Success)
            {
                return null;
            }

            var name = propertyName.Groups[1].Value;
            var candidates = memberSymbols.OfType<IPropertySymbol>()
                .Where(property => string.Equals(name, property.Name, StringComparison.Ordinal)
                                   && SymbolEqualityComparer.Default.Equals(field.Type, property.Type)).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        public static IFieldSymbol? GetMatchingField(IPropertySymbol property)
        {
            if (property.ContainingType is null)
                return null;
            return GetMatchingField(property, property.ContainingType.GetMembers());
        }

        public static IFieldSymbol? GetMatchingField(IPropertySymbol property, IEnumerable<ISymbol> memberSymbols)
        {
            var backingFieldName = $"<{property.Name}>k__BackingField";
            var candidates = (from field in memberSymbols.OfType<IFieldSymbol>()
                where SymbolEqualityComparer.Default.Equals(field.Type, property.Type)
                where field.Name == backingFieldName || GetCanonicalName(field.Name) == GetCanonicalName(property.Name)
                select field).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        public static string GetCanonicalName(string name)
        {
            name = name.TrimStart('_');
            if (name.Length > 0 && char.IsUpper(name[0]))
                name = $"{char.ToLowerInvariant(name[0])}{name.Substring(1)}";
            return name;
        }
    }
}