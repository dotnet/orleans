using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator.Analyzers
{
    public class UnawaitableGrainMethodReturnTypeDiagostic
    {
        public const string DiagnosticId = "ORLEANS0103";
        public const string Title = "Grain method return types must be awaitable types such as Task, Task<T>, ValueTask, ValueTask<T>";
        public const string MessageFormat = Title;
        public const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        internal static Diagnostic CreateDiagnostic(MethodDeclarationSyntax syntaxReference) => Diagnostic.Create(Rule, syntaxReference?.GetLocation());
    }
}
