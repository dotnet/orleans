using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class NoRefParamsDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        private const string DiagnosticId = "ORLEANS0002";
        private const string Title = "Reference parameter modifiers are not allowed";
        private const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeSyntax, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeSyntax(SyntaxNodeAnalysisContext context)
        {
            var syntax = (MethodDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(syntax);

            if (symbol.ContainingType.TypeKind == TypeKind.Interface)
            {
                // TODO: Check that interface inherits from IGrain
                return;
            }

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
