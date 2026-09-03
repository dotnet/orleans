using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

/// <summary>
/// Defines the diagnostic reported when <c>GenerateSerializerAttribute</c> is applied to a type in a reference assembly.
/// </summary>
public static class ReferenceAssemblyWithGenerateSerializerDiagnostic
{
    /// <summary>
    /// The diagnostic identifier.
    /// </summary>
    public const string DiagnosticId = DiagnosticRuleId.ReferenceAssemblyWithGenerateSerializer;

    /// <summary>
    /// The diagnostic title.
    /// </summary>
    public const string Title = "[GenerateSerializer] used in a reference assembly";

    /// <summary>
    /// The diagnostic message format.
    /// </summary>
    public const string MessageFormat = "The type {0} is marked with [GenerateSerializer] in a reference assembly";

    /// <summary>
    /// The diagnostic description.
    /// </summary>
    public const string Description = "The type {0} is marked with [GenerateSerializer] in a reference assembly. Serialization is likely to fail. Options: (1) Enable code generation on the target project directly; (2) Disable reference assemblies using <ProduceReferenceAssembly>false</ProduceReferenceAssembly> in the codegen project; (3) Use a different serializer or create surrogates. See https://aka.ms/orleans-serialization for details.";

    /// <summary>
    /// The diagnostic category.
    /// </summary>
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    internal static Diagnostic CreateDiagnostic(ISymbol symbol, Location? location = null) => Diagnostic.Create(Rule, location ?? symbol.Locations.First(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
}
