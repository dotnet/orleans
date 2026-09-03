using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when an RPC interface declares a property.
/// </summary>
public static class RpcInterfacePropertyDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string DiagnosticId = DiagnosticRuleId.RpcInterfaceProperty;

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "RPC interfaces must not contain properties";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "The interface {0} contains a property {1}. RPC interfaces must not contain properties.";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(INamedTypeSymbol interfaceSymbol, IPropertySymbol property) => Diagnostic.Create(Rule, property.Locations.First(), interfaceSymbol.ToDisplayString(), property.ToDisplayString());
}
