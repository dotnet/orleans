using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    #nullable disable
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AlwaysInterleaveDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        private const string AlwaysInterleaveAttributeName = "Orleans.Concurrency.AlwaysInterleaveAttribute";

        public const string DiagnosticId = "ORLEANS0001";
        public const string Title = "[AlwaysInterleave] must only be used on the grain interface method and not the grain class method";
        public const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(context =>
            {
                var alwaysInterleaveAttributeSymbol = context.Compilation.GetTypeByMetadataName(AlwaysInterleaveAttributeName);
                if (alwaysInterleaveAttributeSymbol is not null)
                {
                    context.RegisterSymbolAction(context => AnalyzeMethod(context, alwaysInterleaveAttributeSymbol), SymbolKind.Method);
                }
            });
        }

        private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol alwaysInterleaveAttribute)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;

            if (methodSymbol.ContainingType.TypeKind == TypeKind.Interface)
            {
                // TODO: Check that interface inherits from IGrain
                return;
            }

            foreach (var attribute in methodSymbol.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, alwaysInterleaveAttribute))
                {
                    return;
                }

                var syntaxReference = attribute.ApplicationSyntaxReference;

                context.ReportDiagnostic(
                    Diagnostic.Create(Rule, Location.Create(syntaxReference.SyntaxTree, syntaxReference.Span)));
            }
        }
    }
}
