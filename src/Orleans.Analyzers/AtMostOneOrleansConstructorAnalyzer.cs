using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AtMostOneOrleansConstructorAnalyzer : DiagnosticAnalyzer
    {
        public const string RuleId = "ORLEANS0007";
        private const string Category = "Usage";
        private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AtMostOneOrleansConstructorTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AtMostOneOrleansConstructorMessageFormat), Resources.ResourceManager, typeof(Resources));

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(context =>
            {
                var generateSerializerAttributeSymbol = context.Compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute");
                if (generateSerializerAttributeSymbol is not null)
                {
                    context.RegisterSymbolAction(context => AnalyzeNamedType(context, generateSerializerAttributeSymbol), SymbolKind.NamedType);
                }
            });
        }

        private void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol generateSerializerAttributeSymbol)
        {
            var symbol = (INamedTypeSymbol)context.Symbol;
            if (SerializationAttributesHelper.ShouldGenerateSerializer(symbol, generateSerializerAttributeSymbol))
            {
                var foundAttribute = false;
                foreach (var constructor in symbol.Constructors)
                {
                    if (constructor.HasAttribute(generateSerializerAttributeSymbol))
                    {
                        if (foundAttribute)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Locations[0]));
                            return;
                        }

                        foundAttribute = true;
                    }
                }
            }
        }
    }
}
