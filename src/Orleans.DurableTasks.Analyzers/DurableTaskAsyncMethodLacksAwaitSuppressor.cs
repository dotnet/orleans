using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Suppresses CS1998 for async methods, local functions, and anonymous functions which return
/// <c>Orleans.DurableTasks.DurableTask</c> or <c>Orleans.DurableTasks.DurableTask&lt;TResult&gt;</c>.
/// </summary>
/// <remarks>
/// The durable task method builder defers the body, which the host runs under replay control, so an
/// async durable task body which completes without awaiting is a normal durable step.
/// The types are matched by metadata name, so the analyzer has no dependency on the abstractions assembly.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskAsyncMethodLacksAwaitSuppressor : DiagnosticSuppressor
{
    public const string SuppressionId = "ORLEANS0028";
    public const string SuppressedDiagnosticId = "CS1998";

    private static readonly SuppressionDescriptor Rule = new(
        id: SuppressionId,
        suppressedDiagnosticId: SuppressedDiagnosticId,
        justification: "An async DurableTask method defines a deferred durable step which the host runs under replay control, so a body which completes without awaiting is a normal step.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = ImmutableArray.Create(Rule);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        var durableTask = context.Compilation.GetTypeByMetadataName("Orleans.DurableTasks.DurableTask");
        var genericDurableTask = context.Compilation.GetTypeByMetadataName("Orleans.DurableTasks.DurableTask`1");
        if (durableTask is null && genericDurableTask is null)
        {
            return;
        }

        var cancellationToken = context.CancellationToken;
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var location = diagnostic.Location;
            if (location.SourceTree is not { } syntaxTree)
            {
                continue;
            }

            // CS1998 is reported on the declaration of the async function, so the innermost enclosing function is the one which lacks awaits.
            var node = syntaxTree.GetRoot(cancellationToken).FindNode(location.SourceSpan, getInnermostNodeForTie: true);
            var function = node.FirstAncestorOrSelf<SyntaxNode>(static n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
            if (function is null)
            {
                continue;
            }

            var semanticModel = context.GetSemanticModel(syntaxTree);
            var symbol = function is AnonymousFunctionExpressionSyntax
                ? semanticModel.GetSymbolInfo(function, cancellationToken).Symbol
                : semanticModel.GetDeclaredSymbol(function, cancellationToken);
            if (symbol is IMethodSymbol { ReturnType: INamedTypeSymbol returnType }
                && (SymbolEqualityComparer.Default.Equals(returnType.OriginalDefinition, durableTask)
                    || SymbolEqualityComparer.Default.Equals(returnType.OriginalDefinition, genericDurableTask)))
            {
                context.ReportSuppression(Suppression.Create(Rule, diagnostic));
            }
        }
    }
}
