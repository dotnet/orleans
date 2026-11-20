using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Names an unnamed durable operation with <c>WithId</c>, deriving the name from the method which defines the operation,
/// such as <c>charge</c> for <c>ChargeAsync</c>, and keeping it distinct from the other names of its body.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DurableTaskIdentityCodeFix)), Shared]
public sealed class DurableTaskIdentityCodeFix : CodeFixProvider
{
    private const string Title = "Name the operation with WithId";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DurableTaskDiagnostics.UnnamedOperation.Id);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create(static async (context, document, diagnostics) =>
            await NameAsync(document, diagnostics, context.CancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Properties.ContainsKey(DurableTaskIdentityAnalyzer.WithIdProperty))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        Title,
                        cancellationToken => NameAsync(context.Document, ImmutableArray.Create(diagnostic), cancellationToken),
                        equivalenceKey: Title),
                    diagnostic);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Names each marked definition, in source order, with a name which its body doesn't use yet.</summary>
    private static async Task<Document> NameAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        // C# documents always have syntax roots.
        var root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var usedNames = new Dictionary<SyntaxNode, HashSet<string>>();
        var names = new Dictionary<SyntaxNode, string>();
        foreach (var diagnostic in diagnostics.OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start))
        {
            if (!diagnostic.Properties.ContainsKey(DurableTaskIdentityAnalyzer.WithIdProperty)
                || root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not InvocationExpressionSyntax definition)
            {
                continue;
            }

            var body = definition.FirstAncestorOrSelf<SyntaxNode>(static node =>
                node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax)!;
            if (!usedNames.TryGetValue(body, out var used))
            {
                usedNames[body] = used = GetNames(body);
            }

            var baseName = GetBaseName(definition);
            var name = baseName;
            for (var suffix = 2; !used.Add(name); suffix++)
            {
                name = $"{baseName}-{suffix}";
            }

            names[definition] = name;
        }

        return document.WithSyntaxRoot(root.ReplaceNodes(
            names.Keys,
            (original, rewritten) => SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        ((ExpressionSyntax)rewritten).WithoutTrivia(),
                        SyntaxFactory.IdentifierName("WithId")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(names[original]))))))
                .WithTriviaFrom(rewritten)));
    }

    /// <summary>Returns the constant names which a body passes to <c>WithId</c> and <c>DurableTask.RunInline</c>.</summary>
    private static HashSet<string> GetNames(SyntaxNode body)
    {
        var names = new HashSet<string>();
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WithId" or "RunInline" }
                && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                names.Add(literal.Token.ValueText);
            }
        }

        return names;
    }

    /// <summary>Returns the kebab-case name of the method which a definition calls, without its <c>Async</c> suffix.</summary>
    private static string GetBaseName(InvocationExpressionSyntax definition)
    {
        var method = definition.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => string.Empty,
        };
        if (method.Length > "Async".Length && method.EndsWith("Async", System.StringComparison.Ordinal))
        {
            method = method.Substring(0, method.Length - "Async".Length);
        }

        var builder = new StringBuilder(method.Length + 4);
        foreach (var character in method)
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('-');
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.Length > 0 ? builder.ToString() : "operation";
    }
}
