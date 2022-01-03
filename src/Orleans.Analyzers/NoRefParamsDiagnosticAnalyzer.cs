using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class NoRefParamsDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        private const string BaseInterfaceName = "IAddressable";
        public const string DiagnosticId = "ORLEANS0002";
        public const string Title = "Reference parameter modifiers are not allowed";
        public const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeSyntax, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeSyntax(SyntaxNodeAnalysisContext context)
        {
            if (!(context.Node is MethodDeclarationSyntax syntax)) return;

            var symbol = context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken);

            if (symbol.ContainingType.TypeKind != TypeKind.Interface) return;

            var implementedInterfaces = symbol.ContainingType
                                              .AllInterfaces
                                              .Select(interfaceDef => interfaceDef.Name);
            if (!implementedInterfaces.Contains(BaseInterfaceName)) return;

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
