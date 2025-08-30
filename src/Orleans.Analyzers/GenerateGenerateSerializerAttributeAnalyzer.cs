using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class GenerateGenerateSerializerAttributeAnalyzer : DiagnosticAnalyzer
    {
        public const string RuleId = "ORLEANS0005";
        private const string Category = "Usage";
        private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AddGenerateSerializerAttributesTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AddGenerateSerializerAttributeMessageFormat), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AddGenerateSerializerAttributeDescription), Resources.ResourceManager, typeof(Resources));

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(context =>
            {
                var serializableAttributeSymbol = context.Compilation.GetTypeByMetadataName("System.SerializableAttribute");
                var generateSerializerAttributeSymbol = context.Compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute");
                if (serializableAttributeSymbol is not null && generateSerializerAttributeSymbol is not null)
                {
                    context.RegisterSymbolAction(context => AnalyzeNamedType(context, serializableAttributeSymbol, generateSerializerAttributeSymbol), SymbolKind.NamedType);
                }
            });
        }

        private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol serializableAttributeSymbol, INamedTypeSymbol generateSerializerAttributeSymbol)
        {
            var symbol = (INamedTypeSymbol)context.Symbol;
            if (!symbol.IsStatic)
            {
                if (symbol.HasAttribute(serializableAttributeSymbol) && !symbol.HasAttribute(generateSerializerAttributeSymbol))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Locations[0], symbol.Name));
                }
            }
        }
    }
}
