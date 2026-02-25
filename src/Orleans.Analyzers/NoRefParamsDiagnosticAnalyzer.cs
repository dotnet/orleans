using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    #nullable disable
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class NoRefParamsDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ORLEANS0002";
        public const string Title = "Reference parameter modifiers are not allowed";
        public const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(context =>
            {
                var baseInterface = context.Compilation.GetTypeByMetadataName("Orleans.Runtime.IAddressable");
                if (baseInterface is not null)
                {
                    context.RegisterSymbolAction(context => AnalyzeMethodSymbol(context, baseInterface), SymbolKind.Method);
                }
            });
        }

        private static void AnalyzeMethodSymbol(SymbolAnalysisContext context, INamedTypeSymbol baseInterface)
        {
            var symbol = (IMethodSymbol)context.Symbol;

            if (symbol.ContainingType.TypeKind != TypeKind.Interface) return;

            // ignore static members
            if (symbol.IsStatic) return;

            var implementedInterfaces = symbol.ContainingType
                                              .AllInterfaces
                                              .Select(interfaceDef => interfaceDef.Name);
            if (!symbol.ContainingType.AllInterfaces.Contains(baseInterface)) return;

            foreach(var param in symbol.Parameters)
            {
                if (param.RefKind == RefKind.None) continue;

                var syntaxReference = param.DeclaringSyntaxReferences;
                context.ReportDiagnostic(
                    Diagnostic.Create(Rule, Location.Create(syntaxReference[0].SyntaxTree, syntaxReference[0].Span)));
            }
        }
    }
}
