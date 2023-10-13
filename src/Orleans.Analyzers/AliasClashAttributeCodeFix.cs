using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GenerateAliasAttributesCodeFix)), Shared]
public class AliasClashAttributeCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(AliasClashAttributeAnalyzer.RuleId);
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics.First();
        if (root.FindNode(diagnostic.Location.SourceSpan) is not AttributeSyntax attribute)
        {
            return;
        }

        var aliasName = diagnostic.Properties["AliasName"];
        var aliasSuffix = diagnostic.Properties["AliasSuffix"];

        context.RegisterCodeFix(
            CodeAction.Create(
                Resources.AliasClashDetectedTitle,
                createChangedDocument: _ =>
                {
                    var newAliasName = $"{aliasName}{aliasSuffix}";
                    var newAttribute = attribute.ReplaceNode(
                        attribute.ArgumentList.Arguments[0].Expression,
                        LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(newAliasName)));

                    var newRoot = root.ReplaceNode(attribute, newAttribute);
                    var newDocument = context.Document.WithSyntaxRoot(newRoot);

                    return Task.FromResult(newDocument);
                },
                equivalenceKey: AliasClashAttributeAnalyzer.RuleId),
            diagnostic);
    }
}