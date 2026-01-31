using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.Analyzers
{
    /// <summary>
    /// Code fix provider for <see cref="OnActivateAsyncCancellationAnalyzer"/> diagnostics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This code fix provider offers the following fixes:
    /// <list type="bullet">
    /// <item>
    /// <term>ORLEANS0014</term>
    /// <description>Pass the cancellation token to the method overload that accepts it.</description>
    /// </item>
    /// <item>
    /// <term>ORLEANS0015</term>
    /// <description>Append <c>.WaitAsync(cancellationToken)</c> to the awaited expression.</description>
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(OnActivateAsyncCancellationCodeFix)), Shared]
    public class OnActivateAsyncCancellationCodeFix : CodeFixProvider
    {
        private const string PassCancellationTokenTitle = "Pass cancellation token to method";
        private const string AddWaitAsyncTitle = "Add .WaitAsync(cancellationToken)";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(
                OnActivateAsyncCancellationAnalyzer.MissingCancellationTokenOverloadDiagnosticId,
                OnActivateAsyncCancellationAnalyzer.MissingWaitAsyncDiagnosticId);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;
            
            // Use getInnermostNodeForTie to get the most specific node when spans match
            var node = root.FindNode(diagnosticSpan, getInnermostNodeForTie: true);

            // Find the containing method to get the cancellation token parameter name
            var containingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (containingMethod == null)
            {
                // Also check if node itself is part of a method
                containingMethod = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
            }
            
            if (containingMethod == null)
            {
                return;
            }

            var cancellationTokenParameterName = GetCancellationTokenParameterName(containingMethod);
            if (cancellationTokenParameterName == null)
            {
                return;
            }

            switch (diagnostic.Id)
            {
                case OnActivateAsyncCancellationAnalyzer.MissingCancellationTokenOverloadDiagnosticId:
                    RegisterPassCancellationTokenFix(context, diagnostic, node, cancellationTokenParameterName);
                    break;

                case OnActivateAsyncCancellationAnalyzer.MissingWaitAsyncDiagnosticId:
                    RegisterAddWaitAsyncFix(context, diagnostic, node, cancellationTokenParameterName);
                    break;
            }
        }

        private static string GetCancellationTokenParameterName(MethodDeclarationSyntax method)
        {
            // Find the CancellationToken parameter
            foreach (var parameter in method.ParameterList.Parameters)
            {
                var identifierName = parameter.Type as IdentifierNameSyntax;
                if (identifierName != null && identifierName.Identifier.Text == "CancellationToken")
                {
                    return parameter.Identifier.Text;
                }

                var qualifiedName = parameter.Type as QualifiedNameSyntax;
                if (qualifiedName != null && qualifiedName.Right.Identifier.Text == "CancellationToken")
                {
                    return parameter.Identifier.Text;
                }
            }

            return null;
        }

        private static void RegisterPassCancellationTokenFix(
            CodeFixContext context,
            Diagnostic diagnostic,
            SyntaxNode node,
            string cancellationTokenParameterName)
        {
            // Find the invocation expression
            var invocation = node as InvocationExpressionSyntax;
            if (invocation == null)
            {
                invocation = node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            }

            if (invocation == null)
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: PassCancellationTokenTitle,
                    createChangedDocument: ct => PassCancellationTokenAsync(
                        context.Document,
                        invocation,
                        cancellationTokenParameterName,
                        ct),
                    equivalenceKey: OnActivateAsyncCancellationAnalyzer.MissingCancellationTokenOverloadDiagnosticId),
                diagnostic);
        }

        private static void RegisterAddWaitAsyncFix(
            CodeFixContext context,
            Diagnostic diagnostic,
            SyntaxNode node,
            string cancellationTokenParameterName)
        {
            // Find the expression to wrap - may need to search ancestors if FindNode returns a child node
            var expression = node as ExpressionSyntax;
            if (expression == null)
            {
                // Try to find the invocation expression in ancestors
                expression = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
            }

            if (expression == null)
            {
                // Try descendants as well
                expression = node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            }

            if (expression == null)
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: AddWaitAsyncTitle,
                    createChangedDocument: ct => AddWaitAsyncAsync(
                        context.Document,
                        expression,
                        cancellationTokenParameterName,
                        ct),
                    equivalenceKey: OnActivateAsyncCancellationAnalyzer.MissingWaitAsyncDiagnosticId),
                diagnostic);
        }

        private static async Task<Document> PassCancellationTokenAsync(
            Document document,
            InvocationExpressionSyntax invocation,
            string cancellationTokenParameterName,
            CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            // Create the cancellation token argument
            var cancellationTokenArgument = Argument(IdentifierName(cancellationTokenParameterName));

            // Add the cancellation token to the argument list
            var newArgumentList = invocation.ArgumentList.AddArguments(cancellationTokenArgument);
            var newInvocation = invocation.WithArgumentList(newArgumentList);

            editor.ReplaceNode(invocation, newInvocation);

            return editor.GetChangedDocument();
        }

        private static async Task<Document> AddWaitAsyncAsync(
            Document document,
            ExpressionSyntax expression,
            string cancellationTokenParameterName,
            CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);

            // Create the .WaitAsync(cancellationToken) invocation
            var waitAsyncInvocation = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    expression.WithoutTrivia(),
                    IdentifierName("WaitAsync")))
                .WithArgumentList(
                    ArgumentList(
                        SingletonSeparatedList(
                            Argument(IdentifierName(cancellationTokenParameterName)))))
                .WithLeadingTrivia(expression.GetLeadingTrivia())
                .WithTrailingTrivia(expression.GetTrailingTrivia());

            editor.ReplaceNode(expression, waitAsyncInvocation);

            return editor.GetChangedDocument();
        }
    }
}
