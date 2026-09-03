using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when a type referenced by <c>GenerateCodeForDeclaringAssemblyAttribute</c> has no declaring assembly.
/// </summary>
public static class GenerateCodeForDeclaringAssemblyAttribute_NoDeclaringAssembly_Diagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string DiagnosticId = DiagnosticRuleId.GenerateCodeForDeclaringAssemblyAttribute_NoDeclaringAssembly;

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "Types passed to GenerateCodeForDeclaringAssemblyAttribute must have a declaring assembly";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "The type {0} provided as an argument to {1} does not have a declaring assembly";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static Diagnostic CreateDiagnostic(AttributeData attribute, ITypeSymbol type)
    {
        var location = attribute.ApplicationSyntaxReference is { } syntaxReference
            ? syntaxReference.SyntaxTree.GetLocation(syntaxReference.Span)
            : Location.None;
        return Diagnostic.Create(Rule, location, type.ToDisplayString(), attribute.ToString());
    }
}
