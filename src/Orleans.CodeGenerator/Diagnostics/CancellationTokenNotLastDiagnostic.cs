using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when an RPC method has a cancellation token parameter which is not last.
/// </summary>
public static class CancellationTokenNotLastDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string DiagnosticId = DiagnosticRuleId.CancellationTokenNotLast;

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "CancellationToken should be the last RPC parameter";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "CancellationToken parameter '{0}' on RPC method '{1}' should be the last parameter";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(IParameterSymbol parameter) =>
        Diagnostic.Create(
            Rule,
            parameter.Locations.FirstOrDefault() ?? Location.None,
            parameter.Name,
            parameter.ContainingSymbol.ToDisplayString());
}
