using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when a compound type alias refers to a type which is inaccessible to generated code.
/// </summary>
public static class InaccessibleCompoundTypeAliasDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string RuleId = DiagnosticRuleId.InaccessibleCompoundTypeAlias;

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "Compound type alias must refer to accessible types";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "The compound type alias on {0} cannot be registered because the type {1} is inaccessible from generated code";

    /// <summary>
    /// The diagnostic description.
    /// </summary>
    public const string Description = "Compound type aliases are registered by generated code, which must be able to refer to the aliased type and to every type used in the alias. Either make those types public or make them internal and ensure that internals are visible to the generated code, or remove the alias.";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    internal static Diagnostic CreateDiagnostic(INamedTypeSymbol target, AttributeData attribute, ITypeSymbol inaccessibleType)
    {
        var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
            ?? target.Locations.FirstOrDefault()
            ?? Location.None;

        return Diagnostic.Create(
            Rule,
            location,
            target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            inaccessibleType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }
}
