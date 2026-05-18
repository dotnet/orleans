using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class CanNotGenerateImplicitFieldIdsDiagnostic
{
    public const string DiagnosticId = DiagnosticRuleId.CanNotGenerateImplicitFieldIds;
    public const string Title = "Implicit field identifiers could not be generated";
    public const string MessageFormat = "Could not generate implicit field identifiers for the type {0}: {1}";
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(ISymbol symbol, string reason, Location? location = null) => Diagnostic.Create(Rule, location ?? symbol.Locations.First(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), reason);
}
