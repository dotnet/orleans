using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Reports durable task definitions which never run, which run more than once, or which are posted from code without a
/// durable outbox, and handles of scheduled children which nothing observes.
/// </summary>
/// <remarks>
/// A method which returns <c>DurableTask</c> creates a definition, which runs when it is awaited, scheduled, or posted.
/// An <c>async DurableTask</c> method's definition is single-use: it runs its state machine once. <c>Post</c> stages a
/// start in the durable outbox of the grain which runs the calling code. In a durable task body, <c>ScheduleAsync</c>
/// starts an attached child, which the execution owns. The analysis of locals reports only definite cases: a local which
/// any reference passes on, or which code assigns again, counts as observed.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskDefinitionAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DurableTaskDiagnostics.UnobservedDefinition,
        DurableTaskDiagnostics.ReusedDefinition,
        DurableTaskDiagnostics.PostWithoutOutbox,
        DurableTaskDiagnostics.UnobservedHandle);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            if (DurableTaskSymbols.Create(start.Compilation) is not { } symbols)
            {
                return;
            }

            start.RegisterOperationAction(operation => AnalyzeStatement(operation, symbols), OperationKind.ExpressionStatement);
            start.RegisterOperationAction(operation => AnalyzePost(operation, symbols), OperationKind.Invocation);
            start.RegisterOperationBlockAction(block => AnalyzeLocals(block, symbols));
        });
    }

    private static void AnalyzeStatement(OperationAnalysisContext context, DurableTaskSymbols symbols)
    {
        var value = ((IExpressionStatementOperation)context.Operation).Operation;
        if (value is ISimpleAssignmentOperation { Target: IDiscardOperation } discard)
        {
            value = discard.Value;
        }

        if (IsDefinition(value, symbols))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DurableTaskDiagnostics.UnobservedDefinition,
                context.Operation.Syntax.GetLocation(),
                GetDefinitionName(value)));
        }
        else if (GetOwnedSchedule(value, context.ContainingSymbol, symbols) is { } schedule)
        {
            ReportUnobservedHandle(context.ReportDiagnostic, schedule);
        }
    }

    /// <summary>Reports a <c>Post</c> outside grains and durable task bodies, where no durable outbox can stage it.</summary>
    private static void AnalyzePost(OperationAnalysisContext context, DurableTaskSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!DurableTaskSymbols.IsPost(invocation)
            || symbols.IsGrain(context.ContainingSymbol.ContainingType)
            || symbols.IsInDurableBody(invocation, context.ContainingSymbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DurableTaskDiagnostics.PostWithoutOutbox,
            invocation.Syntax.GetLocation(),
            DurableTaskSymbols.GetExtensionReceiver(invocation) is { } receiver ? GetDefinitionName(receiver) : invocation.TargetMethod.Name));
    }

    /// <summary>Returns whether a value creates a definition or configures one.</summary>
    private static bool IsDefinition(IOperation value, DurableTaskSymbols symbols) =>
        DurableTaskSymbols.Unwrap(value) is IInvocationOperation invocation && symbols.ReturnsDefinition(invocation.TargetMethod);

    /// <summary>Returns the name of the method which defines a task, through any configuration calls.</summary>
    private static string GetDefinitionName(IOperation value) =>
        DurableTaskSymbols.GetDefinition(value, "WithId", out _) switch
        {
            IInvocationOperation invocation => invocation.TargetMethod.Name,
            var definition => definition.Syntax.ToString(),
        };

    /// <summary>
    /// Returns the <c>ScheduleAsync</c> call which a value awaits when it starts a child which a durable task body owns: a
    /// definition which isn't detached, scheduled in a durable task body.
    /// </summary>
    private static IInvocationOperation? GetOwnedSchedule(IOperation value, ISymbol containingSymbol, DurableTaskSymbols symbols)
    {
        if (DurableTaskSymbols.Unwrap(value) is not IAwaitOperation await
            || DurableTaskSymbols.Unwrap(await.Operation) is not IInvocationOperation { TargetMethod.Name: "ScheduleAsync" } schedule
            || !DurableTaskSymbols.IsDurableTasksType(schedule.TargetMethod.ContainingType)
            || !(DurableTaskSymbols.IsDurableTasksType(await.Type) && await.Type!.Name == "ScheduledTask")
            || DurableTaskSymbols.GetExtensionReceiver(schedule) is not { } receiver)
        {
            return null;
        }

        DurableTaskSymbols.GetDefinition(receiver, "Detached", out var detached);
        return detached || !symbols.IsInDurableBody(await, containingSymbol) ? null : schedule;
    }

    private static void ReportUnobservedHandle(Action<Diagnostic> report, IInvocationOperation schedule) =>
        report(Diagnostic.Create(
            DurableTaskDiagnostics.UnobservedHandle,
            schedule.Syntax.GetLocation(),
            GetDefinitionName(DurableTaskSymbols.GetExtensionReceiver(schedule)!)));

    /// <summary>
    /// Reports locals which hold a definition that never runs or the handle of an owned child which nothing observes, and
    /// the second run of a local which holds a single-use definition: a second reference, or a reference in a loop which
    /// doesn't create the definition.
    /// </summary>
    private static void AnalyzeLocals(OperationBlockAnalysisContext context, DurableTaskSymbols symbols)
    {
        var definitions = new Dictionary<ILocalSymbol, IVariableDeclaratorOperation>(SymbolEqualityComparer.Default);
        var singleUse = new Dictionary<ILocalSymbol, SyntaxNode>(SymbolEqualityComparer.Default);
        var handles = new Dictionary<ILocalSymbol, IInvocationOperation>(SymbolEqualityComparer.Default);
        var aliases = new Dictionary<IOperation, ILocalSymbol>();
        var reassigned = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        var references = new Dictionary<ILocalSymbol, List<ILocalReferenceOperation>>(SymbolEqualityComparer.Default);
        foreach (var block in context.OperationBlocks)
        {
            foreach (var operation in block.Descendants())
            {
                switch (operation)
                {
                    case IVariableDeclaratorOperation { Initializer.Value: { } initializer } declarator:
                        if (DurableTaskSymbols.GetDefinition(initializer, "WithId", out _) is ILocalReferenceOperation source
                            && symbols.IsDefinitionType(source.Type))
                        {
                            aliases[source] = declarator.Symbol;
                        }
                        else if (IsDefinition(initializer, symbols))
                        {
                            definitions[declarator.Symbol] = declarator;
                        }

                        if (IsSingleUseDefinition(initializer, symbols))
                        {
                            singleUse[declarator.Symbol] = declarator.Syntax;
                        }


                        if (GetOwnedSchedule(initializer, context.OwningSymbol, symbols) is { } schedule)
                        {
                            handles[declarator.Symbol] = schedule;
                        }

                        break;
                    case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target }:
                        reassigned.Add(target.Local);
                        break;
                    case ILocalReferenceOperation reference when !reference.IsDeclaration && !IsAssignmentTarget(reference):
                        if (!references.TryGetValue(reference.Local, out var list))
                        {
                            references[reference.Local] = list = [];
                        }

                        list.Add(reference);
                        break;
                }
            }
        }

        foreach (var pair in definitions)
        {
            if (IsUnobserved(pair.Key, []))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DurableTaskDiagnostics.UnobservedDefinition,
                    pair.Value.Syntax.GetLocation(),
                    GetDefinitionName(pair.Value.Initializer!.Value)));
            }
        }

        foreach (var pair in handles)
        {
            if (!reassigned.Contains(pair.Key) && (!references.TryGetValue(pair.Key, out var uses) || uses.TrueForAll(IsPropertyRead)))
            {
                ReportUnobservedHandle(context.ReportDiagnostic, pair.Value);
            }
        }

        foreach (var pair in singleUse)
        {
            if (reassigned.Contains(pair.Key) || !references.TryGetValue(pair.Key, out var uses))
            {
                continue;
            }

            for (var index = 0; index < uses.Count; index++)
            {
                if (index > 0 || IsInLoopWithout(uses[index].Syntax, pair.Value))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DurableTaskDiagnostics.ReusedDefinition,
                        uses[index].Syntax.GetLocation(),
                        pair.Key.Name));
                }
            }
        }

        // A local's definition never runs when the local is never assigned again and its only references initialize
        // aliases whose definitions never run either.
        bool IsUnobserved(ILocalSymbol local, HashSet<ILocalSymbol> visited)
        {
            if (!visited.Add(local) || reassigned.Contains(local))
            {
                return false;
            }

            if (references.TryGetValue(local, out var uses))
            {
                foreach (var use in uses)
                {
                    if (!aliases.TryGetValue(use, out var alias) || !IsUnobserved(alias, visited))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    private static bool IsPropertyRead(ILocalReferenceOperation reference) =>
        reference.Parent is IPropertyReferenceOperation property && property.Instance == reference && !IsAssignmentTarget(property);

    /// <summary>
    /// Returns whether a value is a single-use definition: a call to an async method in this compilation which returns
    /// <c>DurableTask</c>, optionally configured with <c>WithId</c>, <c>Detached</c>, or <c>Shielded</c>.
    /// </summary>
    private static bool IsSingleUseDefinition(IOperation value, DurableTaskSymbols symbols) =>
        DurableTaskSymbols.GetDefinition(value, "WithId", out _) is IInvocationOperation invocation && symbols.IsDurableBody(invocation.TargetMethod);

    private static bool IsAssignmentTarget(IOperation reference) =>
        reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference;

    private static bool IsInLoopWithout(SyntaxNode use, SyntaxNode declaration)
    {
        for (var current = use.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                    return !current.Span.Contains(declaration.Span);
                case MemberDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax:
                    return false;
            }
        }

        return false;
    }
}
