using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class GrainInterfacePropertyDiagnosticAnalyzer : DiagnosticAnalyzer
    {
        private const string BaseInterfaceName = "Orleans.Runtime.IAddressable";
        public const string DiagnosticId = "ORLEANS0008";
        public const string Title = "Grain interfaces must not contain properties";
        public const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeSyntax, SyntaxKind.PropertyDeclaration);
        }

        private static void AnalyzeSyntax(SyntaxNodeAnalysisContext context)
        {
            if (context.Node is not PropertyDeclarationSyntax syntax) return;

            var symbol = context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken);

            if (symbol.ContainingType.TypeKind != TypeKind.Interface) return;

            var isIAddressableInterface = false;
            foreach (var implementedInterface in symbol.ContainingType.AllInterfaces)
            {
                if (BaseInterfaceName.Equals(implementedInterface.ToDisplayString(NullableFlowState.None), System.StringComparison.Ordinal))
                {
                    isIAddressableInterface = true;
                    break;
                }
            }

            if (!isIAddressableInterface) return;

            var syntaxReference = symbol.DeclaringSyntaxReferences;
            context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(syntaxReference[0].SyntaxTree, syntaxReference[0].Span)));
        }
    }
}
