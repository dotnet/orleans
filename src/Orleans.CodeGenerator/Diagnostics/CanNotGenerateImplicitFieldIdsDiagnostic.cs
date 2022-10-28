using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class CanNotGenerateImplicitFieldIdsDiagnostic
{
    public const string DiagnosticId = "ORLEANS0106";
    public const string Title = "Implicit field identifiers could not be generated";
    public const string MessageFormat = "Could not generate implicit field identifiers for the type {0}: {reason}";
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(ISymbol symbol, string reason) => Diagnostic.Create(Rule, symbol.Locations.First(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), reason);
}
