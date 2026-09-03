using Microsoft.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Orleans.CodeGenerator;

/// <summary>
/// Provides utilities for relating Roslyn property, field, and constructor parameter symbols.
/// </summary>
public static class PropertyUtility
{
    private static readonly Regex PropertyMatchRegex = new("^<([^>]+)>.*$", RegexOptions.Compiled);

    /// <summary>
    /// Gets the property represented by a compiler-generated backing field.
    /// </summary>
    /// <param name="field">The field to inspect.</param>
    /// <returns>The matching property when exactly one match exists; otherwise, <see langword="null"/>.</returns>
    public static IPropertySymbol? GetMatchingProperty(IFieldSymbol field)
    {
        if (field.ContainingType is null)
            return null;
        return GetMatchingProperty(field, field.ContainingType.GetMembers());
    }

    /// <summary>
    /// Determines whether a symbol has a compiler-generated attribute.
    /// </summary>
    /// <param name="symbol">The symbol to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="symbol"/> has a compiler-generated attribute; otherwise, <see langword="false"/>.</returns>
    public static bool IsCompilerGenerated(this ISymbol? symbol)
        => symbol?.GetAttributes().Any(a => a.AttributeClass?.Name == "CompilerGeneratedAttribute") == true;

    /// <summary>
    /// Determines whether a property's accessors are compiler-generated.
    /// </summary>
    /// <param name="property">The property to inspect.</param>
    /// <returns><see langword="true"/> when both accessors are compiler-generated; otherwise, <see langword="false"/>.</returns>
    public static bool IsCompilerGenerated(this IPropertySymbol? property)
        => property?.GetMethod.IsCompilerGenerated() == true && property.SetMethod.IsCompilerGenerated();

    /// <summary>
    /// Gets the constructor parameter which represents a property, matching by canonical name and type.
    /// </summary>
    /// <param name="property">The property to match.</param>
    /// <param name="constructorParameters">The constructor parameters to search.</param>
    /// <returns>The first matching parameter; otherwise, <see langword="null"/>.</returns>
    public static IParameterSymbol? GetMatchingPrimaryConstructorParameter(IPropertySymbol property, IEnumerable<IParameterSymbol> constructorParameters)
    {
        return constructorParameters.FirstOrDefault(p =>
            string.Equals(GetCanonicalName(p.Name), GetCanonicalName(property.Name), StringComparison.Ordinal) &&
            SymbolEqualityComparer.Default.Equals(p.Type, property.Type));
    }

    /// <summary>
    /// Gets the property represented by a compiler-generated backing field from a set of member symbols.
    /// </summary>
    /// <param name="field">The field to inspect.</param>
    /// <param name="memberSymbols">The member symbols to search.</param>
    /// <returns>The matching property when exactly one match exists; otherwise, <see langword="null"/>.</returns>
    public static IPropertySymbol? GetMatchingProperty(IFieldSymbol field, IEnumerable<ISymbol> memberSymbols)
    {
        var propertyName = PropertyMatchRegex.Match(field.Name);
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

    /// <summary>
    /// Gets the field which stores the value of a property.
    /// </summary>
    /// <param name="property">The property to inspect.</param>
    /// <returns>The matching field when exactly one match exists; otherwise, <see langword="null"/>.</returns>
    public static IFieldSymbol? GetMatchingField(IPropertySymbol property)
    {
        if (property.ContainingType is null)
            return null;
        return GetMatchingField(property, property.ContainingType.GetMembers());
    }

    /// <summary>
    /// Gets the field which stores the value of a property from a set of member symbols.
    /// </summary>
    /// <param name="property">The property to inspect.</param>
    /// <param name="memberSymbols">The member symbols to search.</param>
    /// <returns>The matching field when exactly one match exists; otherwise, <see langword="null"/>.</returns>
    public static IFieldSymbol? GetMatchingField(IPropertySymbol property, IEnumerable<ISymbol> memberSymbols)
    {
        var backingFieldName = $"<{property.Name}>k__BackingField";
        var candidates = (from field in memberSymbols.OfType<IFieldSymbol>()
                          where SymbolEqualityComparer.Default.Equals(field.Type, property.Type)
                          where field.Name == backingFieldName || GetCanonicalName(field.Name) == GetCanonicalName(property.Name)
                          select field).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// Gets the canonical form of a member or parameter name by removing leading underscores and lowercasing its first character.
    /// </summary>
    /// <param name="name">The name to canonicalize.</param>
    /// <returns>The canonical name.</returns>
    public static string GetCanonicalName(string name)
    {
        name = name.TrimStart('_');
        if (name.Length > 0 && char.IsUpper(name[0]))
            name = $"{char.ToLowerInvariant(name[0])}{name.Substring(1)}";
        return name;
    }
}
