using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class InaccessibleSerializableTypeDiagnostic
{
    public const string RuleId = "ORLEANS0107"; 
    public const string Title = "Serializable type must be accessible from generated code";
    public const string MessageFormat = "The type {0} is marked as being serializable but it is inaccessible from generated code";
    public const string Descsription = "Source generation requires that all types marked as serializable are accessible from generated code. Either make the type public or make it internal and ensure that internals are visible to the generated code.";
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Descsription);

    internal static Diagnostic CreateDiagnostic(ISymbol symbol) => Diagnostic.Create(Rule, symbol.Locations.First(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
}
