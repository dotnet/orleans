using System.Linq;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator.Diagnostics;

public static class ReferenceAssemblyWithGenerateSerializerDiagnostic
{
    public const string DiagnosticId = DiagnosticRuleId.ReferenceAssemblyWithGenerateSerializer;
    public const string Title = "[GenerateSerializer] used in a reference assembly";
    public const string MessageFormat = "The type {0} is marked with [GenerateSerializer] in a reference assembly";
    public const string Description = "The type {0} is marked with [GenerateSerializer] in a reference assembly. Serialization is likely to fail. Options: (1) Enable code generation on the target project directly; (2) Disable reference assemblies using <ProduceReferenceAssembly>false</ProduceReferenceAssembly> in the codegen project; (3) Use a different serializer or create surrogates. See https://aka.ms/orleans-serialization for details.";
    public const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

    internal static Diagnostic CreateDiagnostic(ISymbol symbol) => Diagnostic.Create(Rule, symbol.Locations.First(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
}
