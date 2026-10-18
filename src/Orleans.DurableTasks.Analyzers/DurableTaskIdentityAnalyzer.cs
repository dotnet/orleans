using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>Reports unnamed durable operations whose generated identifiers can shift between runs and deployments.</summary>
/// <remarks>
/// An unnamed operation of a durable task body takes the execution's next generated identifier: a child, an effect, or a
/// detached start takes <c>$child-n</c>, and an awaited combinator takes an operation identifier such as
/// <c>$when-all-n</c>. Each counts the unnamed operations of its kind which ran before it, so an operation in a loop or a
/// condition, or after another unnamed operation of its kind, can receive another identifier.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskIdentityAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic property which marks an operation which <c>WithId</c> can name.</summary>
    internal const string WithIdProperty = "WithId";

    private static readonly ImmutableDictionary<string, string?> WithIdProperties =
        ImmutableDictionary<string, string?>.Empty.Add(WithIdProperty, null);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DurableTaskDiagnostics.UnnamedOperation);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            if (DurableTaskSymbols.Create(start.Compilation) is { } symbols)
            {
                start.RegisterOperationBlockAction(block => Analyze(block, symbols));
            }
        });
    }

    private enum Counter
    {
        /// <summary>Children, effects, and detached starts, which take <c>$child-n</c>.</summary>
        Child,

        /// <summary>Awaited combinators, which take operation identifiers such as <c>$when-all-n</c>.</summary>
        Operation,
    }

    private static void Analyze(OperationBlockAnalysisContext context, DurableTaskSymbols symbols)
    {
        var sites = new List<(SyntaxNode Scope, Counter Counter, SyntaxNode Node, bool Nameable, bool Repeated)>();
        foreach (var block in context.OperationBlocks)
        {
            foreach (var operation in block.DescendantsAndSelf())
            {
                if (GetUnnamedOperation(operation, symbols) is { } site && GetScope(operation, context.OwningSymbol, symbols) is { } scope)
                {
                    sites.Add((scope.Node, site.Counter, site.Node, site.Nameable, scope.Repeated));
                }
            }
        }

        foreach (var group in sites.GroupBy(static site => (site.Scope, site.Counter)))
        {
            var first = true;
            foreach (var site in group.OrderBy(static site => site.Node.Span.End))
            {
                if (!first || site.Repeated || IsConditionalOrRepeated(site.Node, site.Scope))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DurableTaskDiagnostics.UnnamedOperation,
                        site.Node.GetLocation(),
                        site.Nameable ? WithIdProperties : null));
                }

                first = false;
            }
        }
    }

    /// <summary>
    /// Returns the unnamed operation which an operation starts: an awaited, scheduled, or posted definition without
    /// <c>WithId</c>, which the diagnostic marks, or an unnamed <c>DurableTask.RunInline</c> effect.
    /// </summary>
    private static (Counter Counter, SyntaxNode Node, bool Nameable)? GetUnnamedOperation(IOperation operation, DurableTaskSymbols symbols)
    {
        IOperation started;
        var awaited = false;
        switch (operation)
        {
            case IAwaitOperation await when symbols.IsDefinitionType(DurableTaskSymbols.Unwrap(await.Operation).Type):
                started = await.Operation;
                awaited = true;
                break;
            case IInvocationOperation { TargetMethod.Name: "ScheduleAsync" or "Post" } start
                when DurableTaskSymbols.IsDurableTasksType(start.TargetMethod.ContainingType)
                && DurableTaskSymbols.GetExtensionReceiver(start) is { } receiver
                && symbols.IsDefinitionType(DurableTaskSymbols.Unwrap(receiver).Type):
                started = receiver;
                break;
            case IInvocationOperation effect when symbols.IsRunInline(effect)
                && !effect.TargetMethod.Parameters.Any(static parameter => parameter.Type.SpecialType == SpecialType.System_String):
                return (Counter.Child, effect.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } ? memberAccess.Name : effect.Syntax, false);
            default:
                return null;
        }

        if (DurableTaskSymbols.GetDefinition(started, "WithId", out var named) is not IInvocationOperation definition
            || named
            || (awaited && symbols.IsSignalWait(definition)))
        {
            return null;
        }

        var counter = awaited && symbols.IsCombinator(definition) ? Counter.Operation : Counter.Child;
        return (counter, definition.Syntax, true);
    }

    /// <summary>
    /// Returns the durable task body which assigns an operation's identifier, and whether code between them, such as a
    /// lambda which a query runs or a local function, can run the operation more than once.
    /// </summary>
    private static (SyntaxNode Node, bool Repeated)? GetScope(IOperation operation, ISymbol owningSymbol, DurableTaskSymbols symbols)
    {
        var repeated = false;
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IAnonymousFunctionOperation function:
                    if (symbols.IsDurableBody(function.Symbol)
                        || (DurableTaskSymbols.GetDelegateReceiver(function) is { } receiver && symbols.IsRun(receiver)))
                    {
                        return (function.Syntax, repeated);
                    }

                    if (DurableTaskSymbols.GetDelegateReceiver(function) is { } effect && symbols.IsRunInline(effect))
                    {
                        return null;
                    }

                    repeated = true;
                    break;
                case ILocalFunctionOperation local:
                    if (symbols.IsDurableBody(local.Symbol))
                    {
                        return (local.Syntax, repeated);
                    }

                    repeated = true;
                    break;
                case { Parent: null } root when owningSymbol is IMethodSymbol method && symbols.IsDurableBody(method):
                    return (root.Syntax, repeated);
            }
        }

        return null;
    }

    /// <summary>Returns whether a node runs in a loop or a condition within its body.</summary>
    private static bool IsConditionalOrRepeated(SyntaxNode node, SyntaxNode scope)
    {
        for (var child = node; child.Parent is { } parent && child != scope; child = parent)
        {
            var conditional = parent switch
            {
                IfStatementSyntax @if => child != @if.Condition,
                SwitchSectionSyntax or CatchClauseSyntax => true,
                SwitchExpressionArmSyntax arm => child != arm.Pattern,
                ConditionalExpressionSyntax conditionalExpression => child != conditionalExpression.Condition,
                BinaryExpressionSyntax binary => binary.Right == child
                    && binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression,
                AssignmentExpressionSyntax assignment => assignment.Right == child && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression),
                ConditionalAccessExpressionSyntax access => child == access.WhenNotNull,
                ForStatementSyntax @for => child != @for.Declaration && !(child is ExpressionSyntax expression && @for.Initializers.Contains(expression)),
                CommonForEachStatementSyntax forEach => child == forEach.Statement,
                WhileStatementSyntax or DoStatementSyntax => true,
                _ => false,
            };
            if (conditional)
            {
                return true;
            }
        }

        return false;
    }
}
