using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers;

/// <summary>
/// A code fix provider that converts ConfigureAwait(false) to ConfigureAwait(true) and
/// adds ContinueOnCapturedContext to ConfigureAwait(ConfigureAwaitOptions) calls.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ConfigureAwaitCodeFix)), Shared]
public class ConfigureAwaitCodeFix : CodeFixProvider
{
    public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(ConfigureAwaitAnalyzer.RuleId);
    public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics.First();
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        // Find the invocation expression identified by the diagnostic
        var node = root.FindNode(diagnosticSpan);
        var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();

        if (invocation is null)
        {
            return;
        }

        // Get semantic model to determine which fix to apply
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var symbolInfo = semanticModel.GetSymbolInfo(invocation, context.CancellationToken);

        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
        {
            return;
        }

        // Check the parameter type to determine which fix to apply
        if (methodSymbol.Parameters.Length == 1)
        {
            var parameterType = methodSymbol.Parameters[0].Type;

            if (parameterType.SpecialType == SpecialType.System_Boolean)
            {
                // Fix for ConfigureAwait(bool) - change false to true
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: Resources.ConfigureAwaitCodeFixTitle,
                        createChangedDocument: ct => FixConfigureAwaitBoolAsync(context.Document, invocation, ct),
                        equivalenceKey: ConfigureAwaitAnalyzer.RuleId + "_Bool"),
                    diagnostic);
            }
            else if (string.Equals(parameterType.ToDisplayString(), "System.Threading.Tasks.ConfigureAwaitOptions", StringComparison.Ordinal))
            {
                // Fix for ConfigureAwait(ConfigureAwaitOptions) - add ContinueOnCapturedContext flag
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: Resources.ConfigureAwaitCodeFixTitle,
                        createChangedDocument: ct => FixConfigureAwaitOptionsAsync(context.Document, invocation, semanticModel, ct),
                        equivalenceKey: ConfigureAwaitAnalyzer.RuleId + "_Options"),
                    diagnostic);
            }
        }
    }

    private static async Task<Document> FixConfigureAwaitBoolAsync(
        Document document,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        // Create new argument with 'true' instead of 'false'
        var newArgument = Argument(LiteralExpression(SyntaxKind.TrueLiteralExpression));
        var newArgumentList = ArgumentList(SingletonSeparatedList(newArgument));

        // Replace the argument list
        var newInvocation = invocation.WithArgumentList(newArgumentList);
        var newRoot = root.ReplaceNode(invocation, newInvocation);

        return document.WithSyntaxRoot(newRoot);
    }

    private static async Task<Document> FixConfigureAwaitOptionsAsync(
        Document document,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        var arguments = invocation.ArgumentList?.Arguments;
        if (arguments is null || arguments.Value.Count == 0)
        {
            return document;
        }

        var existingArgument = arguments.Value[0].Expression;

        // Check if the existing argument is ConfigureAwaitOptions.None
        var constantValue = semanticModel.GetConstantValue(existingArgument, cancellationToken);
        ExpressionSyntax newExpression;

        if (constantValue.HasValue && constantValue.Value is int intValue && intValue == 0)
        {
            // If it's None (0), just replace with ContinueOnCapturedContext
            newExpression = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("ConfigureAwaitOptions"),
                IdentifierName("ContinueOnCapturedContext"));
        }
        else
        {
            // Otherwise, add ContinueOnCapturedContext using bitwise OR
            var continueOnCapturedContext = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("ConfigureAwaitOptions"),
                IdentifierName("ContinueOnCapturedContext"));

            newExpression = BinaryExpression(
                SyntaxKind.BitwiseOrExpression,
                existingArgument.WithoutTrivia(),
                continueOnCapturedContext);
        }

        var newArgument = Argument(newExpression);
        var newArgumentList = ArgumentList(SingletonSeparatedList(newArgument));

        var newInvocation = invocation.WithArgumentList(newArgumentList);
        var newRoot = root.ReplaceNode(invocation, newInvocation);

        return document.WithSyntaxRoot(newRoot);
    }
}
