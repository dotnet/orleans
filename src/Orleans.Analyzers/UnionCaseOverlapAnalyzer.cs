using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.CodeGenerator;

namespace Orleans.Analyzers;

/// <summary>
/// Reports serializable union types whose cases are constructed from the same generic type and therefore have the same type for some type arguments.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnionCaseOverlapAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0028";

    private static readonly DiagnosticDescriptor Rule = new(
        id: RuleId,
        title: "Union cases can have the same type",
        messageFormat: "Cases '{0}' and '{1}' of union '{2}' can be the same type for some type arguments. A value of that type matches, and is serialized as, the first of these cases.",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A union value stores only its case value, so cases which have the same type cannot be distinguished. Use distinct case types to preserve which case was created.",
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(context =>
        {
            if (context.Compilation.GetTypeByMetadataName(Constants.GenerateSerializerAttributeFullyQualifiedName) is { } generateSerializerAttribute)
            {
                context.RegisterSymbolAction(context => AnalyzeNamedType(context, generateSerializerAttribute), SymbolKind.NamedType);
            }
        });
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol generateSerializerAttribute)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.IsGenericType || !LanguageFeatureSymbols.IsUnion(type) || !type.HasAttribute(generateSerializerAttribute))
        {
            return;
        }

        var cases = LanguageFeatureSymbols.GetUnionCaseTypes(type) ?? [];
        for (var i = 0; i < cases.Length; i++)
        {
            for (var j = i + 1; j < cases.Length; j++)
            {
                if (cases[i] is INamedTypeSymbol { IsGenericType: true } first
                    && cases[j] is INamedTypeSymbol { IsGenericType: true } second
                    && SymbolEqualityComparer.Default.Equals(first.OriginalDefinition, second.OriginalDefinition)
                    && TypeUnifier.CanUnify(context.Compilation, first, second))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule,
                        type.Locations.FirstOrDefault() ?? Location.None,
                        first.ToDisplayString(),
                        second.ToDisplayString(),
                        type.ToDisplayString()));
                }
            }
        }
    }
}
