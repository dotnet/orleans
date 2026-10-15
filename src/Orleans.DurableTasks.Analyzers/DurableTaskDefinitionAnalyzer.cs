using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Reports durable task definitions which never run, which run more than once, or which are posted from code without a
/// durable outbox.
/// </summary>
/// <remarks>
/// A method which returns <c>DurableTask</c> creates a definition, which runs when it is awaited, scheduled, or posted.
/// An <c>async DurableTask</c> method's definition is single-use: it runs its state machine once. <c>Post</c> stages a
/// start in the durable outbox of the grain which runs the calling code.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskDefinitionAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DurableTaskDiagnostics.UnobservedDefinition,
        DurableTaskDiagnostics.ReusedDefinition,
        DurableTaskDiagnostics.PostWithoutOutbox);

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

        if (DurableTaskSymbols.Unwrap(value) is IInvocationOperation invocation && symbols.ReturnsDefinition(invocation.TargetMethod))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DurableTaskDiagnostics.UnobservedDefinition,
                context.Operation.Syntax.GetLocation(),
                invocation.TargetMethod.Name));
        }
    }

    /// <summary>Reports a <c>Post</c> outside grains and durable task bodies, where no durable outbox can stage it.</summary>
    private static void AnalyzePost(OperationAnalysisContext context, DurableTaskSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!DurableTaskSymbols.IsPost(invocation)
            || symbols.IsGrain(context.ContainingSymbol.ContainingType)
            || RunsInDurableBody(invocation, context.ContainingSymbol, symbols))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DurableTaskDiagnostics.PostWithoutOutbox,
            invocation.Syntax.GetLocation(),
            GetDefinitionName(invocation)));
    }

    /// <summary>
    /// Returns whether an operation runs in a durable task body: an <c>async DurableTask</c> method, or a delegate which a
    /// durable task factory, such as <c>DurableTask.Run</c>, runs, including code which such a body contains.
    /// </summary>
    private static bool RunsInDurableBody(IOperation operation, ISymbol containingSymbol, DurableTaskSymbols symbols)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IAnonymousFunctionOperation function
                    when symbols.IsDurableBody(function.Symbol) || IsDurableTaskFactoryArgument(function):
                    return true;
                case ILocalFunctionOperation local when symbols.IsDurableBody(local.Symbol):
                    return true;
            }
        }

        return containingSymbol is IMethodSymbol method && symbols.IsDurableBody(method);
    }

    private static bool IsDurableTaskFactoryArgument(IAnonymousFunctionOperation function) =>
        function.Parent is IDelegateCreationOperation { Parent: IArgumentOperation { Parent: IInvocationOperation factory } }
        && DurableTaskSymbols.IsDurableTasksType(factory.TargetMethod.ContainingType);

    /// <summary>Returns the name of the method which defines the posted task, through any configuration calls.</summary>
    private static string GetDefinitionName(IInvocationOperation post)
    {
        var current = DurableTaskSymbols.GetExtensionReceiver(post);
        while (current is not null && DurableTaskSymbols.Unwrap(current) is IInvocationOperation invocation)
        {
            if (!DurableTaskSymbols.IsConfiguration(invocation))
            {
                return invocation.TargetMethod.Name;
            }

            current = DurableTaskSymbols.GetExtensionReceiver(invocation);
        }

        return post.TargetMethod.Name;
    }

    /// <summary>
    /// Reports the second run of a local which holds a single-use definition: a second reference, or a reference in a loop
    /// which doesn't create the definition.
    /// </summary>
    private static void AnalyzeLocals(OperationBlockAnalysisContext context, DurableTaskSymbols symbols)
    {
        var singleUse = new Dictionary<ILocalSymbol, SyntaxNode>(SymbolEqualityComparer.Default);
        var reassigned = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        var references = new Dictionary<ILocalSymbol, List<ILocalReferenceOperation>>(SymbolEqualityComparer.Default);
        foreach (var block in context.OperationBlocks)
        {
            foreach (var operation in block.Descendants())
            {
                switch (operation)
                {
                    case IVariableDeclaratorOperation { Initializer.Value: { } initializer } declarator
                        when IsSingleUseDefinition(initializer, symbols):
                        singleUse[declarator.Symbol] = declarator.Syntax;
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
    }

    /// <summary>
    /// Returns whether a value is a single-use definition: a call to an async method in this compilation which returns
    /// <c>DurableTask</c>, optionally configured with <c>WithId</c>, <c>Detached</c>, or <c>Shielded</c>.
    /// </summary>
    private static bool IsSingleUseDefinition(IOperation value, DurableTaskSymbols symbols)
    {
        var current = DurableTaskSymbols.Unwrap(value);
        while (current is IInvocationOperation invocation)
        {
            if (symbols.IsDurableBody(invocation.TargetMethod))
            {
                return true;
            }

            if (!DurableTaskSymbols.IsConfiguration(invocation) || DurableTaskSymbols.GetExtensionReceiver(invocation) is not { } receiver)
            {
                return false;
            }

            current = DurableTaskSymbols.Unwrap(receiver);
        }

        return false;
    }

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
