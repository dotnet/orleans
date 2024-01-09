using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Composition;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace Orleans.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(IncorrectAttributeUseCodeFix)), Shared]
public class IncorrectAttributeUseCodeFix : CodeFixProvider
{
    public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(IncorrectAttributeUseAnalyzer.RuleId);
    public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics.First();

        if (root.FindNode(diagnostic.Location.SourceSpan) is not AttributeSyntax node)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Resources.IncorrectAttributeUseTitle,
                createChangedDocument: token =>
                {
                    var newRoot = root.RemoveNode(node.Parent, SyntaxRemoveOptions.KeepEndOfLine);
                    return Task.FromResult(context.Document.WithSyntaxRoot(newRoot));

                },
                equivalenceKey: IncorrectAttributeUseAnalyzer.RuleId),
            diagnostic);
    }

}