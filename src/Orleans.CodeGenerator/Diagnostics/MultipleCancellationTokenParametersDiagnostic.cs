using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class MultipleCancellationTokenParametersDiagnostic
{
    public const string DiagnosticId = "ORLEANS0109";
    public const string Title = "Grain method has multiple parameters of type CancellationToken";
    public const string MessageFormat = "The type {0} contains method {1} which has multiple CancellationToken parameters. Only a single CancellationToken parameter is supported.";
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(IMethodSymbol symbol) => Diagnostic.Create(Rule, symbol.Locations.First(), symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), symbol.Name);
}