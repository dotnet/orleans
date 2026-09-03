using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when implicit field identifiers cannot be generated for a serializable type.
/// </summary>
public static class CanNotGenerateImplicitFieldIdsDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string DiagnosticId = DiagnosticRuleId.CanNotGenerateImplicitFieldIds;

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "Implicit field identifiers could not be generated";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "Could not generate implicit field identifiers for the type {0}: {1}";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(ISymbol symbol, string reason, Location? location = null) => Diagnostic.Create(Rule, location ?? symbol.Locations.First(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), reason);
}
