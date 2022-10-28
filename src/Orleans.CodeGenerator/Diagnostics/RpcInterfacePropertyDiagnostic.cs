using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class RpcInterfacePropertyDiagnostic
{
    public const string DiagnosticId = "ORLEANS0105";
    public const string Title = "RPC interfaces must not contain properties";
    public const string MessageFormat = "The interface {0} contains a property {1}. RPC interfaces must not contain properties.";
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(INamedTypeSymbol interfaceSymbol, IPropertySymbol property) => Diagnostic.Create(Rule, property.Locations.First(), interfaceSymbol.ToDisplayString(), property.ToDisplayString());
}
