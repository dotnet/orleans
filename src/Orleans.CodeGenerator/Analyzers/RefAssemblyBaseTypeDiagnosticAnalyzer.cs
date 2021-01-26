using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Analyzers
{
    public class RefAssemblyBaseTypeDiagnosticAnalyzer
    {
        public const string DiagnosticId = "ORLEANS0100";
        public const string Title = "Types which have a base type belonging to a reference assembly may not be correctly serialized";
        public const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        internal static Diagnostic CreateDiagnostic(TypeDeclarationSyntax syntaxReference) => Diagnostic.Create(Rule, syntaxReference?.GetLocation());
    }
}
