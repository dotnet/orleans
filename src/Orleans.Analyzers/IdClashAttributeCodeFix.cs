using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(IdClashAttributeCodeFix)), Shared]
public class IdClashAttributeCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(IdClashAttributeAnalyzer.RuleId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics.First();
        if (root.FindNode(diagnostic.Location.SourceSpan) is not AttributeSyntax attribute)
        {
            return;
        }

        var idValue = diagnostic.Properties["IdValue"];

        context.RegisterCodeFix(
            CodeAction.Create(
                Resources.IdClashDetectedTitle,
                createChangedDocument: _ =>
                {
                    var newIdValue = root
                        .DescendantNodes()
                        .OfType<AttributeSyntax>()
                        .Where(a => a.IsAttribute(Constants.IdAttributeName))
                        .Select(a => int.Parse(a.ArgumentList.Arguments.Single().ToString()))
                        .ToList()
                        .Max() + 1;

                    var newAttribute = attribute.ReplaceNode(
                        attribute.ArgumentList.Arguments[0].Expression,
                        LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(newIdValue)));

                    var newRoot = root.ReplaceNode(attribute, newAttribute);
                    var newDocument = context.Document.WithSyntaxRoot(newRoot);

                    return Task.FromResult(newDocument);
                },
                equivalenceKey: IdClashAttributeAnalyzer.RuleId),
            diagnostic);
    }
}