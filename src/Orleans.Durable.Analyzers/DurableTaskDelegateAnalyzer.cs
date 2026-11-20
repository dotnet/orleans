using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Reports delegates which <c>DurableTask.Run</c> and <c>DurableTask.RunInline</c> receive and which the call can't run as
/// intended: an async void delegate, whose completion precedes its work, and a synchronous step, which runs inline as an
/// effect.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskDelegateAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DurableTaskDiagnostics.SynchronousStep,
        DurableTaskDiagnostics.AsyncVoidDelegate);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            if (DurableTaskSymbols.Create(start.Compilation) is { } symbols)
            {
                start.RegisterOperationAction(operation => Analyze(operation, symbols), OperationKind.Invocation);
            }
        });
    }

    private static void Analyze(OperationAnalysisContext context, DurableTaskSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var run = symbols.IsRun(invocation);
        if (!run && !symbols.IsRunInline(invocation))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Type is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
            {
                continue;
            }

            var target = GetTarget(argument.Value);
            if (invoke.ReturnsVoid && GetAsyncVoidName(target, context.Operation.SemanticModel) is { } name)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DurableTaskDiagnostics.AsyncVoidDelegate,
                    argument.Value.Syntax.GetLocation(),
                    name,
                    $"DurableTask.{invocation.TargetMethod.Name}"));
            }
            else if (run
                && target is IAnonymousFunctionOperation function
                && IsSynchronous(function)
                && IsAwaitedStep(invocation)
                && symbols.IsInDurableBody(invocation, context.ContainingSymbol))
            {
                var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }
                    ? memberAccess.Name.GetLocation()
                    : invocation.Syntax.GetLocation();
                context.ReportDiagnostic(Diagnostic.Create(DurableTaskDiagnostics.SynchronousStep, location));
            }
        }
    }

    /// <summary>Returns the function or method which a delegate argument binds, through conversions and casts.</summary>
    private static IOperation GetTarget(IOperation value)
    {
        while (true)
        {
            switch (value)
            {
                case IConversionOperation conversion:
                    value = conversion.Operand;
                    break;
                case IDelegateCreationOperation creation:
                    value = creation.Target;
                    break;
                default:
                    return value;
            }
        }
    }

    /// <summary>
    /// Returns a description of an async void function which a delegate binds: an async lambda, an async void method, or a
    /// local which a declaration initializes with one and which no code assigns again.
    /// </summary>
    private static string? GetAsyncVoidName(IOperation target, SemanticModel? semanticModel)
    {
        switch (target)
        {
            case IAnonymousFunctionOperation { Symbol.IsAsync: true }:
                return "async lambda";
            case IMethodReferenceOperation { Method: { IsAsync: true, ReturnsVoid: true } method }:
                return method.Name;
            case ILocalReferenceOperation { Local: var local } when semanticModel is not null
                && DurableTaskSymbols.GetOnlyValue(local, semanticModel) is { } value
                && GetAsyncVoidName(GetTarget(value), semanticModel: null) is not null:
                return local.Name;
            default:
                return null;
        }
    }

    /// <summary>
    /// Returns whether a step's delegate does synchronous work only: it awaits nothing, returns no task, and doesn't read
    /// the <c>CancellationToken</c> which the step receives.
    /// </summary>
    private static bool IsSynchronous(IAnonymousFunctionOperation function)
    {
        var symbol = function.Symbol;
        if (!symbol.IsAsync && !symbol.ReturnsVoid && !symbol.ReturnType.GetMembers("GetAwaiter").IsEmpty)
        {
            return false;
        }

        foreach (var operation in function.Body.DescendantsAndSelf())
        {
            switch (operation)
            {
                case IAwaitOperation:
                case IForEachLoopOperation { IsAsynchronous: true }:
                case IUsingOperation { IsAsynchronous: true }:
                case IUsingDeclarationOperation { IsAsynchronous: true }:
                case IParameterReferenceOperation { Parameter.Type.Name: "CancellationToken" } reference
                    when SymbolEqualityComparer.Default.Equals(reference.Parameter.ContainingSymbol, symbol):
                    return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether a step is awaited where it is defined, optionally named or shielded, and not detached.</summary>
    private static bool IsAwaitedStep(IInvocationOperation run) =>
        DurableTaskSymbols.GetConsumer(run, "Detached", out var detached) is IAwaitOperation && !detached;
}
