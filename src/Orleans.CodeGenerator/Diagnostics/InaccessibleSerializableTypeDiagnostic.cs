using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when a serializable type is inaccessible to generated code.
/// </summary>
public static class InaccessibleSerializableTypeDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string RuleId = DiagnosticRuleId.InaccessibleSerializableType; 

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "Serializable type must be accessible from generated code";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "The type {0} is marked as being serializable but it is inaccessible from generated code";

    /// <summary>
    /// The diagnostic description.
    /// </summary>
    public const string Description = "Source generation requires that all types marked as serializable are accessible from generated code. Either make the type public or make it internal and ensure that internals are visible to the generated code.";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    internal static Diagnostic CreateDiagnostic(ISymbol symbol, Location? location = null) => Diagnostic.Create(Rule, location ?? symbol.Locations.First(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
}
