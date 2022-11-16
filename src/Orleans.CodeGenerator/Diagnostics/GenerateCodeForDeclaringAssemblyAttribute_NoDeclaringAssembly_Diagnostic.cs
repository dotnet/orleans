using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class GenerateCodeForDeclaringAssemblyAttribute_NoDeclaringAssembly_Diagnostic
{
    public const string DiagnosticId = "ORLEANS0108";
    public const string Title = "Types passed to GenerateCodeForDeclaringAssemblyAttribute must have a declaring assembly";
    public const string MessageFormat = "The type {0} provided as an argument to {1} does not have a declaring assembly";
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(AttributeData attribute, ITypeSymbol type) => Diagnostic.Create(Rule, attribute.ApplicationSyntaxReference.SyntaxTree.GetLocation(attribute.ApplicationSyntaxReference.Span), type.ToDisplayString(), attribute.ToString());
}
