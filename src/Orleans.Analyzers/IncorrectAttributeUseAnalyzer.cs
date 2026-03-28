using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace Orleans.Analyzers;

#nullable disable
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class IncorrectAttributeUseAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0013";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
       id: RuleId,
       category: "Usage",
       defaultSeverity: DiagnosticSeverity.Error,
       isEnabledByDefault: true,
       title: new LocalizableResourceString(nameof(Resources.IncorrectAttributeUseTitle), Resources.ResourceManager, typeof(Resources)),
       messageFormat: new LocalizableResourceString(nameof(Resources.IncorrectAttributeUseMessageFormat), Resources.ResourceManager, typeof(Resources)),
       description: new LocalizableResourceString(nameof(Resources.IncorrectAttributeUseTitleDescription), Resources.ResourceManager, typeof(Resources)));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(context =>
        {
            var aliasAttributeSymbol = context.Compilation.GetTypeByMetadataName("Orleans.AliasAttribute");
            var grainSymbol = context.Compilation.GetTypeByMetadataName("Orleans.Grain");
            var generateSerializerAttributeSymbol = context.Compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute");
            if (aliasAttributeSymbol is not null && grainSymbol is not null)
            {
                context.RegisterSymbolAction(context => AnalyzeNamedType(context, aliasAttributeSymbol, grainSymbol, generateSerializerAttributeSymbol), SymbolKind.NamedType);
            }
        });
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol aliasAttributeSymbol, INamedTypeSymbol grainSymbol, INamedTypeSymbol generateSerializerAttributeSymbol)
    {
        var symbol = (INamedTypeSymbol)context.Symbol;
        if (!symbol.DerivesFrom(grainSymbol))
        {
            return;
        }

        TryReportFor(aliasAttributeSymbol, context, symbol);
        TryReportFor(generateSerializerAttributeSymbol, context, symbol);
    }

    private static void TryReportFor(INamedTypeSymbol attributeSymbol, SymbolAnalysisContext context, INamedTypeSymbol symbol)
    {
        if (symbol.HasAttribute(attributeSymbol, out var location))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: Rule,
                location: location,
                messageArgs: new object[] { attributeSymbol.Name }));
        }
    }
}
