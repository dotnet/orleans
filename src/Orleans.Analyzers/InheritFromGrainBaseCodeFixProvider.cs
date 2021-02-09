using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Analyzers
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(InheritFromGrainBaseCodeFixProvider)), Shared]
    public class InheritFromGrainBaseCodeFixProvider : CodeFixProvider
    {
        public const string CodeFixTitle = "Inherit from Orleans.Grain";
        public const string AbstractCodeFixTitle = "Mark as Abstract";

        public sealed override ImmutableArray<string> FixableDiagnosticIds
        {
            get { return ImmutableArray.Create(InheritFromGrainBaseAnalyzer.DiagnosticId); }
        }

        public sealed override FixAllProvider GetFixAllProvider()
        {
            // See https://github.com/dotnet/roslyn/blob/master/docs/analyzers/FixAllProvider.md for more information on Fix All Providers
            return WellKnownFixAllProviders.BatchFixer;
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            // Find the type declaration identified by the diagnostic.
            var declaration = root.FindToken(diagnosticSpan.Start).Parent.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().First();

            // Register a code action to invoke the fix
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: CodeFixTitle,
                    createChangedDocument: c => ApplyBaseClassAsync(context.Document, declaration, c),
                    equivalenceKey: CodeFixTitle),
                diagnostic);
        }

        private async Task<Document> ApplyBaseClassAsync(Document document, TypeDeclarationSyntax typeDeclaration, CancellationToken cancellationToken)
        {
            var oldRoot = await document.GetSyntaxRootAsync(cancellationToken);

            // Add Grain as base case
            var generator = SyntaxGenerator.GetGenerator(document);
            var newNode = generator.AddBaseType(typeDeclaration, SyntaxFactory.ParseName("Orleans.Grain"));

            // Format node
            var formattedNewNode = newNode.WithAdditionalAnnotations(Formatter.Annotation);

            // Replace node
            var newRoot = oldRoot.ReplaceNode(typeDeclaration, formattedNewNode);

            // Update Document
            return document.WithSyntaxRoot(newRoot);
        }
    }
}
