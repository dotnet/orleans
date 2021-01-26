using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Analyzers
{
    public class GenericMethodRequireOrleansCoreDiagnostic
    {
        public const string DiagnosticId = "ORLEANS0101";
        public const string Title = "Support for generic grain methods requires the project to reference Microsoft.Orleans.Core";
        public const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        internal static Diagnostic CreateDiagnostic(MethodDeclarationSyntax syntaxReference) => Diagnostic.Create(Rule, syntaxReference?.GetLocation());
    }
}
