using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Analyzers
{
    public class InaccessibleGrainClassDiagnostic
    {
        public const string DiagnosticId = "ORLEANS0102";
        public const string Title = "InaccessibleGrain";
        public const string MessageFormat = "Grain must be accessible from generated code";
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        internal static Diagnostic CreateDiagnostic(TypeDeclarationSyntax syntaxReference)
        {
            var location = syntaxReference.Identifier.GetLocation();
            return Diagnostic.Create(Rule, location);
        }
    }
}
