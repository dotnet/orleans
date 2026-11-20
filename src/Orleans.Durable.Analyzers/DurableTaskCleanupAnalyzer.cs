using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Reports code which runs only after task cancellation and passes <c>DurableTaskContext.CancellationToken</c>, which
/// task cancellation has already canceled, to a call.
/// </summary>
/// <remarks>
/// Code runs only after task cancellation when it is the handler of a <see langword="catch"/> clause, or the true branch of
/// an <see langword="if"/> statement or a conditional expression, whose filter or condition is
/// <c>DurableTaskContext.IsTaskCancellationRequested</c> or a conjunction with it as an operand. Task cancellation is never
/// withdrawn, so the region stays canceled. Nested lambdas and local functions can run in another task's context, so they
/// aren't inspected.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskCleanupAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DurableTaskDiagnostics.CanceledTokenInCleanup);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            if (DurableTaskSymbols.Create(start.Compilation) is { IsTaskCancellationRequested: { } requested, ContextCancellationToken: { } token })
            {
                start.RegisterOperationAction(operation => AnalyzeArgument(operation, requested, token), OperationKind.Argument);
            }
        });
    }

    private static void AnalyzeArgument(OperationAnalysisContext context, IPropertySymbol requested, IPropertySymbol token)
    {
        if (DurableTaskSymbols.Unwrap(((IArgumentOperation)context.Operation).Value) is IPropertyReferenceOperation reference
            && SymbolEqualityComparer.Default.Equals(reference.Property, token)
            && RunsOnlyAfterTaskCancellation(reference, requested))
        {
            context.ReportDiagnostic(Diagnostic.Create(DurableTaskDiagnostics.CanceledTokenInCleanup, reference.Syntax.GetLocation()));
        }
    }

    private static bool RunsOnlyAfterTaskCancellation(IOperation operation, IPropertySymbol requested)
    {
        for (var current = operation; current.Parent is { } parent; current = parent)
        {
            switch (parent)
            {
                case IAnonymousFunctionOperation or ILocalFunctionOperation:
                    return false;
                case ICatchClauseOperation clause when clause.Handler == current && clause.Filter is { } filter && Requires(filter, requested):
                case IConditionalOperation conditional when conditional.WhenTrue == current && Requires(conditional.Condition, requested):
                    return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a condition is true only when <paramref name="requested"/> is.</summary>
    private static bool Requires(IOperation condition, IPropertySymbol requested) => DurableTaskSymbols.Unwrap(condition) switch
    {
        IPropertyReferenceOperation reference => SymbolEqualityComparer.Default.Equals(reference.Property, requested),
        IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } conjunction =>
            Requires(conjunction.LeftOperand, requested) || Requires(conjunction.RightOperand, requested),
        _ => false,
    };
}
