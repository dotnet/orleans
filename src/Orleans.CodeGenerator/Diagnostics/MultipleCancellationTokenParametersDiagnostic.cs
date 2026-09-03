using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when an RPC method declares multiple cancellation token parameters.
/// </summary>
public static class MultipleCancellationTokenParametersDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string DiagnosticId = DiagnosticRuleId.MultipleCancellationTokenParameters;

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "Grain method has multiple parameters of type CancellationToken";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "The type {0} contains method {1} which has multiple CancellationToken parameters. Only a single CancellationToken parameter is supported.";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(IMethodSymbol symbol) => Diagnostic.Create(Rule, symbol.Locations.First(), symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), symbol.Name);
}
