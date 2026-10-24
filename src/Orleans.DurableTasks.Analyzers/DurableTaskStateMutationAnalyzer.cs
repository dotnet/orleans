using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Reports code in durable task bodies which changes an object that grain state holds in place, such as
/// <c>_order.Value.Lines.Add(line)</c> or <c>_state.State.Count++</c>, instead of assigning a new value through the
/// state's API.
/// </summary>
/// <remarks>
/// The analysis follows the instance chain of the changed member or the receiver of a collection mutation to a value which
/// grain state holds, directly or through a local which a declaration initializes with one and which no code assigns
/// again. Value types and immutable records can't be changed through that chain, and immutable collections return
/// changed copies, so neither is reported.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskStateMutationAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DurableTaskDiagnostics.StateChangedInPlace);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            if (DurableTaskSymbols.Create(start.Compilation) is { } symbols)
            {
                start.RegisterOperationAction(
                    operation => Analyze(operation, symbols),
                    OperationKind.SimpleAssignment,
                    OperationKind.CompoundAssignment,
                    OperationKind.CoalesceAssignment,
                    OperationKind.Increment,
                    OperationKind.Decrement,
                    OperationKind.Invocation);
            }
        });
    }

    private static void Analyze(OperationAnalysisContext context, DurableTaskSymbols symbols)
    {
        var holder = context.Operation switch
        {
            IInvocationOperation invocation when DurableTaskSymbols.IsCollectionMutation(invocation.TargetMethod) => invocation.Instance,
            IAssignmentOperation assignment => GetInstance(assignment.Target),
            IIncrementOrDecrementOperation increment => GetInstance(increment.Target),
            _ => null,
        };
        if (holder is not null
            && GetStateValue(holder, symbols) is { } value
            && symbols.IsInDurableBody(context.Operation, context.ContainingSymbol))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DurableTaskDiagnostics.StateChangedInPlace,
                context.Operation.Syntax.GetLocation(),
                value.Syntax.ToString()));
        }
    }

    /// <summary>Returns the object whose member or element an assignment target changes.</summary>
    private static IOperation? GetInstance(IOperation target) => target switch
    {
        IMemberReferenceOperation member => member.Instance,
        IArrayElementReferenceOperation element => element.ArrayReference,
        _ => null,
    };

    /// <summary>Returns the grain state value which holds an object, following members, elements, and aliasing locals.</summary>
    private static IOperation? GetStateValue(IOperation holder, DurableTaskSymbols symbols)
    {
        for (var current = DurableTaskSymbols.Unwrap(holder); ; current = DurableTaskSymbols.Unwrap(current))
        {
            if (symbols.IsStateValue(current))
            {
                return current;
            }

            switch (current)
            {
                case IMemberReferenceOperation { Instance: { } instance }:
                    current = instance;
                    break;
                case IArrayElementReferenceOperation element:
                    current = element.ArrayReference;
                    break;
                case ILocalReferenceOperation local when GetAliasedStateValue(local, symbols) is { } aliased:
                    return aliased;
                default:
                    return null;
            }
        }
    }

    /// <summary>Returns the grain state value which a local holds when its declaration is its only assignment.</summary>
    private static IOperation? GetAliasedStateValue(ILocalReferenceOperation reference, DurableTaskSymbols symbols) =>
        reference.SemanticModel is { } semanticModel
        && DurableTaskSymbols.GetOnlyValue(reference.Local, semanticModel) is { } value
        && symbols.IsStateValue(DurableTaskSymbols.Unwrap(value))
            ? DurableTaskSymbols.Unwrap(value)
            : null;
}
