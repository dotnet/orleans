using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>
/// Reports code in durable task bodies which a replay re-runs with effects or values which differ from the run which
/// recorded the task's operations.
/// </summary>
/// <remarks>
/// <para>
/// A durable task body is an async method, local function, or lambda which returns <c>DurableTask</c>, a delegate which
/// <c>DurableTask.Run</c> receives, or a helper which awaits durable tasks directly or records effects. The analyzer builds
/// each body's control flow graph and divides its code into the replayed region, from which a durable operation is
/// reachable, and the tail, which runs only in the run which completes the body. A replay re-runs the replayed region
/// after later operations committed, so the rules which concern replay apply there.
/// </para>
/// <para>
/// A durable operation is an await of a durable task, a configuration, or an abstractions API; an await of a helper in
/// the same compilation whose source performs durable operations; or a <c>DurableTask.RunInline</c> effect. The analysis is
/// intraprocedural: helpers which the body calls without awaiting, and helpers in other assemblies, aren't inspected.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskReplayAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DurableTaskDiagnostics.GrainStateInReplayedCode,
        DurableTaskDiagnostics.NonDeterministicValueInReplayedCode,
        DurableTaskDiagnostics.ExternalAwaitInReplayedCode,
        DurableTaskDiagnostics.StateChangeBeforeExternalAwait,
        DurableTaskDiagnostics.RunInlineAssignsCapturedVariable,
        DurableTaskDiagnostics.ReservedCharacterInName,
        DurableTaskDiagnostics.DuplicateName,
        DurableTaskDiagnostics.SchedulerEscape,
        DurableTaskDiagnostics.NonDurableCombinator,
        DurableTaskDiagnostics.NonDurableDelay,
        DurableTaskDiagnostics.UnfilteredCancellationHandler,
        DurableTaskDiagnostics.AsyncVoidInBody,
        DurableTaskDiagnostics.RepeatedSignalWait,
        DurableTaskDiagnostics.ReplayedLog);

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

            var helpers = new SourceInspector();
            start.RegisterOperationBlockAction(block =>
            {
                foreach (var operation in block.OperationBlocks)
                {
                    if (operation is IMethodBodyBaseOperation or IBlockOperation { Parent: null or IMethodBodyBaseOperation } && block.OwningSymbol is IMethodSymbol method)
                    {
                        var graph = block.GetControlFlowGraph(operation);
                        new BodyAnalysis(block, symbols, helpers).AnalyzeGraph(graph, ClassifyMethod(symbols, helpers, method, graph));
                    }
                }
            });
        });
    }

    private enum FunctionKind
    {
        /// <summary>Code which isn't part of a durable task body.</summary>
        None,

        /// <summary>
        /// A durable task body, which the replay rules analyze: an <c>async DurableTask</c> method, local function, or lambda,
        /// or a delegate which <c>DurableTask.Run</c> receives.
        /// </summary>
        Body,

        /// <summary>
        /// Code which awaits durable tasks directly or records effects, which the replay rules analyze as a body. It can run
        /// in a durable task, or outside one, where <c>WithId</c> names a root task.
        /// </summary>
        Helper,

        /// <summary>A <c>DurableTask.RunInline</c> effect, which runs once.</summary>
        Effect,

        /// <summary>A lambda which runs as part of the statement of the enclosing body which creates it.</summary>
        Inline,
    }

    private static FunctionKind ClassifyMethod(DurableTaskSymbols symbols, SourceInspector helpers, IMethodSymbol method, ControlFlowGraph graph)
    {
        if (symbols.IsDurableBody(method))
        {
            return FunctionKind.Body;
        }

        return PerformsDirectDurableOperations(symbols, graph) ? FunctionKind.Helper : FunctionKind.None;
    }

    /// <summary>
    /// Returns whether a graph awaits a durable task or a configuration directly or records an effect, which only code
    /// running in a durable task does.
    /// </summary>
    private static bool PerformsDirectDurableOperations(DurableTaskSymbols symbols, ControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            foreach (var root in RootOperations(block))
            {
                foreach (var operation in DurableTaskSymbols.DescendantsAndSelf(root))
                {
                    switch (operation)
                    {
                        case IAwaitOperation await when IsDefinitionAwait(symbols, await):
                        case IInvocationOperation invocation when symbols.IsRunInline(invocation):
                            return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsDefinitionAwait(DurableTaskSymbols symbols, IAwaitOperation await)
    {
        var type = DurableTaskSymbols.Unwrap(await.Operation).Type;
        return symbols.IsDurableTask(type)
            || (DurableTaskSymbols.IsDurableTasksType(type) && type!.Name == "ConfiguredDurableTask");
    }

    private static IEnumerable<IOperation> RootOperations(BasicBlock block)
    {
        foreach (var operation in block.Operations)
        {
            yield return operation;
        }

        if (block.BranchValue is { } branchValue)
        {
            yield return branchValue;
        }
    }

    /// <summary>Inspects the source of helpers which a body awaits, to find helpers which perform durable operations.</summary>
    private sealed class SourceInspector
    {
        private readonly ConcurrentDictionary<IMethodSymbol, bool> _durableHelpers = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<ISymbol, bool> _replaySafeLoggers = new(SymbolEqualityComparer.Default);

        /// <summary>
        /// Returns whether a method in this compilation performs durable operations, judged from its source: it calls
        /// <c>WithId</c>, <c>Post</c>, <c>ScheduleAsync</c>, or durable signal sends, or a static member of
        /// <c>DurableTask</c>.
        /// </summary>
        public bool IsDurableHelper(IMethodSymbol method) => _durableHelpers.GetOrAdd(method.OriginalDefinition, static method =>
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                foreach (var invocation in reference.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    switch (invocation.Expression)
                    {
                        case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "DurableTask" } }:
                            return true;
                        case MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } when IsDurableOperationName(name):
                            return true;
                    }
                }
            }

            return false;
        });

        /// <summary>
        /// Returns whether a field, property, or local holds a replay-safe logger: every assignment in its source, including
        /// its initializer, calls <c>ReplaySafe</c>.
        /// </summary>
        public bool IsReplaySafeLogger(ISymbol member) => _replaySafeLoggers.GetOrAdd(member, static member =>
        {
            var assignments = 0;
            foreach (var value in AssignedValues(member))
            {
                assignments++;
                if (!CallsReplaySafe(value))
                {
                    return false;
                }
            }

            return assignments > 0;
        });

        private static bool IsDurableOperationName(string name) =>
            name is "WithId" or "Post" or "ScheduleAsync" or "SignalDurableTaskAsync" or "TrySignalDurableTaskAsync";

        private static bool CallsReplaySafe(SyntaxNode value) =>
            value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(static invocation => invocation.Expression switch
            {
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ReplaySafe" } => true,
                IdentifierNameSyntax { Identifier.ValueText: "ReplaySafe" } => true,
                _ => false,
            });

        private static IEnumerable<SyntaxNode> AssignedValues(ISymbol member)
        {
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                switch (reference.GetSyntax())
                {
                    case VariableDeclaratorSyntax { Initializer.Value: { } initializer }:
                        yield return initializer;
                        break;
                    case PropertyDeclarationSyntax { Initializer.Value: { } initializer }:
                        yield return initializer;
                        break;
                    case PropertyDeclarationSyntax { ExpressionBody.Expression: { } body }:
                        yield return body;
                        break;
                }
            }

            if (member.Kind is SymbolKind.Local)
            {
                yield break;
            }

            foreach (var typeReference in member.ContainingType.DeclaringSyntaxReferences)
            {
                foreach (var assignment in typeReference.GetSyntax().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    var target = assignment.Left switch
                    {
                        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var name } => name.Identifier.ValueText,
                        _ => null,
                    };
                    if (target == member.Name)
                    {
                        yield return assignment.Right;
                    }
                }
            }
        }
    }

    /// <summary>Analyzes the durable task bodies in one operation block.</summary>
    private sealed class BodyAnalysis
    {
        private readonly OperationBlockAnalysisContext context;
        private readonly DurableTaskSymbols symbols;
        private readonly SourceInspector helpers;
        private readonly HashSet<(string Id, Location Location)> _reported = [];

        public BodyAnalysis(OperationBlockAnalysisContext context, DurableTaskSymbols symbols, SourceInspector helpers)
        {
            this.context = context;
            this.symbols = symbols;
            this.helpers = helpers;
        }

        public void AnalyzeGraph(ControlFlowGraph graph, FunctionKind kind)
        {
            switch (kind)
            {
                case FunctionKind.Body or FunctionKind.Helper:
                    new Region(this, graph, kind).Analyze();
                    AnalyzeCancellationHandlers(graph.OriginalOperation);
                    break;
                case FunctionKind.Effect:
                    AnalyzeEffect(graph);
                    break;
            }

            var analyzed = kind is FunctionKind.Body or FunctionKind.Helper;
            foreach (var localFunction in graph.LocalFunctions)
            {
                var localGraph = graph.GetLocalFunctionControlFlowGraph(localFunction, context.CancellationToken);
                if (analyzed && localFunction.IsAsync && localFunction.ReturnsVoid)
                {
                    Report(DurableTaskDiagnostics.AsyncVoidInBody, localFunction.Locations[0], localFunction.Name);
                }

                AnalyzeGraph(localGraph, ClassifyMethod(symbols, helpers, localFunction, localGraph));
            }

            foreach (var function in AnonymousFunctions(graph))
            {
                var functionGraph = graph.GetAnonymousFunctionControlFlowGraph(function, context.CancellationToken);
                var functionKind = ClassifyAnonymousFunction(function, functionGraph, kind);
                if (analyzed && function.Symbol.IsAsync && function.Symbol.ReturnsVoid)
                {
                    Report(DurableTaskDiagnostics.AsyncVoidInBody, function.Syntax.GetLocation(), "async lambda");
                }

                AnalyzeGraph(functionGraph, functionKind);
            }
        }


        private FunctionKind ClassifyAnonymousFunction(IFlowAnonymousFunctionOperation function, ControlFlowGraph graph, FunctionKind enclosing)
        {
            if (GetReceivingInvocation(function) is { } invocation)
            {
                if (symbols.IsRunInline(invocation))
                {
                    return FunctionKind.Effect;
                }

                if (symbols.IsRun(invocation))
                {
                    return FunctionKind.Body;
                }
            }

            if (symbols.IsDurableBody(function.Symbol))
            {
                return FunctionKind.Body;
            }

            if (enclosing is FunctionKind.Body or FunctionKind.Helper or FunctionKind.Inline)
            {
                return FunctionKind.Inline;
            }

            return PerformsDirectDurableOperations(symbols, graph) ? FunctionKind.Helper : FunctionKind.None;
        }

        /// <summary>Returns the invocation which receives a lambda as an argument, if any.</summary>
        private static IInvocationOperation? GetReceivingInvocation(IOperation function)
        {
            for (var current = function.Parent; current is not null; current = current.Parent)
            {
                switch (current)
                {
                    case IDelegateCreationOperation:
                    case IConversionOperation:
                        continue;
                    case IArgumentOperation { Parent: IInvocationOperation invocation }:
                        return invocation;
                    default:
                        return null;
                }
            }

            return null;
        }

        private static IEnumerable<IFlowAnonymousFunctionOperation> AnonymousFunctions(ControlFlowGraph graph)
        {
            foreach (var block in graph.Blocks)
            {
                foreach (var root in RootOperations(block))
                {
                    foreach (var operation in DurableTaskSymbols.DescendantsAndSelf(root))
                    {
                        if (operation is IFlowAnonymousFunctionOperation function)
                        {
                            yield return function;
                        }
                    }
                }
            }
        }

        /// <summary>Reports assignments to the body's variables and scheduler escapes in a <c>DurableTask.RunInline</c> effect.</summary>
        private void AnalyzeEffect(ControlFlowGraph graph)
        {
            var effect = graph.OriginalOperation switch
            {
                IAnonymousFunctionOperation anonymous => anonymous.Symbol,
                _ => null,
            };

            foreach (var block in graph.Blocks)
            {
                foreach (var root in RootOperations(block))
                {
                    foreach (var operation in DurableTaskSymbols.DescendantsAndSelf(root))
                    {
                        CheckSchedulerEscape(operation);
                        var target = operation switch
                        {
                            ISimpleAssignmentOperation assignment => assignment.Target,
                            ICompoundAssignmentOperation assignment => assignment.Target,
                            IIncrementOrDecrementOperation increment => increment.Target,
                            ICoalesceAssignmentOperation assignment => assignment.Target,
                            _ => null,
                        };
                        ISymbol? variable = target switch
                        {
                            ILocalReferenceOperation local => local.Local,
                            IParameterReferenceOperation parameter => parameter.Parameter,
                            _ => null,
                        };
                        if (variable is not null
                            && effect is not null
                            && !SymbolEqualityComparer.Default.Equals(variable.ContainingSymbol, effect)
                            && !IsDeclaredIn(variable, effect))
                        {
                            Report(DurableTaskDiagnostics.RunInlineAssignsCapturedVariable, operation.Syntax.GetLocation(), variable.Name);
                        }
                    }
                }
            }
        }

        private static bool IsDeclaredIn(ISymbol variable, IMethodSymbol function)
        {
            for (var container = variable.ContainingSymbol; container is not null; container = container.ContainingSymbol)
            {
                if (SymbolEqualityComparer.Default.Equals(container, function))
                {
                    return true;
                }

                if (container is not IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Reports handlers which catch cancellation around durable operations without filtering by
        /// <c>IsTaskCancellationRequested</c> and without rethrowing.
        /// </summary>
        private void AnalyzeCancellationHandlers(IOperation body)
        {
            foreach (var @try in OperationsOutsideNestedFunctions(body).OfType<ITryOperation>())
            {
                if (!OperationsOutsideNestedFunctions(@try.Body).Any(IsDurableOperation))
                {
                    continue;
                }

                foreach (var handler in @try.Catches)
                {
                    if (CatchesCancellation(handler.ExceptionType)
                        && !(handler.Filter is { } filter && ReferencesIsTaskCancellationRequested(filter))
                        && !OperationsOutsideNestedFunctions(handler.Handler).OfType<IThrowOperation>().Any()
                        && handler.Syntax is CatchClauseSyntax clause)
                    {
                        Report(DurableTaskDiagnostics.UnfilteredCancellationHandler, clause.CatchKeyword.GetLocation());
                    }
                }
            }
        }

        private bool CatchesCancellation(ITypeSymbol? type)
        {
            if (type is null || symbols.OperationCanceledException is not { } cancellation)
            {
                return true;
            }

            for (var current = cancellation; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, type))
                {
                    return true;
                }
            }

            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, cancellation))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ReferencesIsTaskCancellationRequested(IOperation filter) =>
            DurableTaskSymbols.DescendantsAndSelf(filter).Any(operation =>
                operation is IPropertyReferenceOperation { Property: var property }
                && SymbolEqualityComparer.Default.Equals(property, symbols.IsTaskCancellationRequested));

        private static IEnumerable<IOperation> OperationsOutsideNestedFunctions(IOperation root)
        {
            var stack = new Stack<IOperation>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var operation = stack.Pop();
                yield return operation;
                foreach (var child in operation.ChildOperations)
                {
                    if (child is not (IAnonymousFunctionOperation or ILocalFunctionOperation))
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        /// <summary>Returns whether an operation, outside a control flow graph, is a durable operation.</summary>
        private bool IsDurableOperation(IOperation operation) => operation switch
        {
            IAwaitOperation await => IsDurableAwait(await),
            IInvocationOperation invocation => symbols.IsRunInline(invocation) || DurableTaskSymbols.IsPost(invocation),
            _ => false,
        };

        /// <summary>Returns whether an await observes a durable operation, directly or through a helper in this compilation.</summary>
        private bool IsDurableAwait(IAwaitOperation await)
        {
            if (symbols.IsDurableAwait(await))
            {
                return true;
            }

            var awaited = DurableTaskSymbols.Unwrap(await.Operation);
            if (awaited is IInvocationOperation invocation)
            {
                var method = invocation.TargetMethod;
                if (HasDurableTypeArgument(method.ContainingType)
                    || (invocation.Instance is { } instance && HasDurableTypeArgument(DurableTaskSymbols.Unwrap(instance).Type)))
                {
                    // An async enumeration of durable tasks, such as DurableTask.WhenEach, including its disposal.
                    return true;
                }

                return !method.DeclaringSyntaxReferences.IsEmpty && helpers.IsDurableHelper(method);
            }

            return false;
        }

        private static bool HasDurableTypeArgument(ITypeSymbol? type) =>
            type is INamedTypeSymbol { TypeArguments: var typeArguments } && typeArguments.Any(DurableTaskSymbols.IsDurableTasksType);

        /// <summary>Returns whether an await waits for work outside the task's recorded operations.</summary>
        private bool IsExternalAwait(IAwaitOperation await)
        {
            if (IsDurableAwait(await))
            {
                return false;
            }

            var awaited = DurableTaskSymbols.Unwrap(await.Operation);
            return awaited switch
            {
                IInvocationOperation { TargetMethod: { Name: "Yield", ContainingType: var type } } when IsTask(type) => false,
                IPropertyReferenceOperation { Property.Name: "CompletedTask" } => false,
                _ => true,
            };
        }

        private bool IsTask(ITypeSymbol? type) => SymbolEqualityComparer.Default.Equals(type, symbols.Task);

        private void CheckSchedulerEscape(IOperation operation)
        {
            if (IsSchedulerEscape(operation))
            {
                var description = operation switch
                {
                    IInvocationOperation invocation => $"{invocation.TargetMethod.ContainingType.Name}.{invocation.TargetMethod.Name}",
                    _ => $"new {operation.Type!.Name}",
                };
                Report(DurableTaskDiagnostics.SchedulerEscape, operation.Syntax.GetLocation(), description);
            }
        }

        /// <summary>Returns whether an operation starts work off the body's scheduler, such as a thread or thread-pool work.</summary>
        private bool IsSchedulerEscape(IOperation operation)
        {
            switch (operation)
            {
                case IInvocationOperation invocation:
                    var method = invocation.TargetMethod;
                    var type = method.ContainingType;
                    return (IsTask(type) && method.Name == "Run")
                        || (SymbolEqualityComparer.Default.Equals(type, symbols.TaskFactory) && method.Name == "StartNew")
                        || SymbolEqualityComparer.Default.Equals(type, symbols.Parallel)
                        || (SymbolEqualityComparer.Default.Equals(type, symbols.ThreadPool) && method.Name.Contains("QueueUserWorkItem"));
                case IObjectCreationOperation creation:
                    return SymbolEqualityComparer.Default.Equals(creation.Type, symbols.Thread)
                        || DurableTaskSymbols.IsOneOf(creation.Type, symbols.TimerTypes);
                default:
                    return false;
            }
        }

        private void Report(DiagnosticDescriptor descriptor, Location location, params object?[] arguments)
        {
            if (_reported.Add((descriptor.Id, location)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));
            }
        }

        /// <summary>Divides one body's control flow graph into its replayed region and its tail, and applies the rules.</summary>
        private sealed class Region
        {
            private readonly BodyAnalysis _analysis;
            private readonly ControlFlowGraph _graph;
            private readonly FunctionKind _kind;
            private readonly List<BasicBlock>[] _successors;
            private readonly bool[] _reachesDurable;
            private readonly bool[] _reachesExternal;
            private readonly List<(string Name, int Block, int Index, Location Location)> _names = [];

            public Region(BodyAnalysis analysis, ControlFlowGraph graph, FunctionKind kind)
            {
                _analysis = analysis;
                _graph = graph;
                _kind = kind;
                _successors = ComputeSuccessors(graph);
                _reachesDurable = ComputeReach(ContainsDurableOperation);
                _reachesExternal = ComputeReach(ContainsExternalAwait);
            }

            private DurableTaskSymbols Symbols => _analysis.symbols;

            public void Analyze()
            {
                foreach (var block in _graph.Blocks)
                {
                    var roots = RootOperations(block).ToArray();
                    for (var index = 0; index < roots.Length; index++)
                    {
                        var replayed = ContainsDurableOperation(roots[index]) || ReachesAfter(block, roots, index, _reachesDurable, ContainsDurableOperation);
                        var position = (block.Ordinal, index);
                        AnalyzeOperation(roots[index], replayed, position, block, roots);
                    }
                }

                ReportDuplicateNames();
            }

            private void AnalyzeOperation(IOperation root, bool replayed, (int Block, int Index) position, BasicBlock block, IOperation[] roots)
            {
                foreach (var operation in DurableTaskSymbols.DescendantsAndSelf(root))
                {
                    AnalyzeSingle(operation, replayed, position, block, roots);
                    if (operation is IFlowAnonymousFunctionOperation function
                        && _analysis.ClassifyAnonymousFunction(function, _graph.GetAnonymousFunctionControlFlowGraph(function, _analysis.context.CancellationToken), FunctionKind.Body) == FunctionKind.Inline)
                    {
                        AnalyzeInlineFunction(_graph.GetAnonymousFunctionControlFlowGraph(function, _analysis.context.CancellationToken), replayed, position, block, roots);
                    }
                }
            }

            private void AnalyzeInlineFunction(ControlFlowGraph graph, bool replayed, (int Block, int Index) position, BasicBlock block, IOperation[] roots)
            {
                foreach (var inner in graph.Blocks)
                {
                    foreach (var root in RootOperations(inner))
                    {
                        foreach (var operation in DurableTaskSymbols.DescendantsAndSelf(root))
                        {
                            AnalyzeSingle(operation, replayed, position, block, roots);
                            if (operation is IFlowAnonymousFunctionOperation function
                                && _analysis.ClassifyAnonymousFunction(function, graph.GetAnonymousFunctionControlFlowGraph(function, _analysis.context.CancellationToken), FunctionKind.Inline) == FunctionKind.Inline)
                            {
                                AnalyzeInlineFunction(graph.GetAnonymousFunctionControlFlowGraph(function, _analysis.context.CancellationToken), replayed, position, block, roots);
                            }
                        }
                    }
                }
            }

            private void AnalyzeSingle(IOperation operation, bool replayed, (int Block, int Index) position, BasicBlock block, IOperation[] roots)
            {
                _analysis.CheckSchedulerEscape(operation);
                switch (operation)
                {
                    case IFieldReferenceOperation or IPropertyReferenceOperation when replayed && Symbols.IsGrainState(operation.Type):
                        _analysis.Report(DurableTaskDiagnostics.GrainStateInReplayedCode, operation.Syntax.GetLocation(), MemberName(operation));
                        break;
                    case IPropertyReferenceOperation property when replayed && DurableTaskSymbols.IsNonDeterministicMember(property.Property) && !IsLogArgument(operation):
                        _analysis.Report(DurableTaskDiagnostics.NonDeterministicValueInReplayedCode, operation.Syntax.GetLocation(), $"{property.Property.ContainingType.Name}.{property.Property.Name}");
                        break;
                    case IObjectCreationOperation { Constructor: { } constructor } when replayed && DurableTaskSymbols.IsNonDeterministicMember(constructor) && !IsLogArgument(operation):
                        _analysis.Report(DurableTaskDiagnostics.NonDeterministicValueInReplayedCode, operation.Syntax.GetLocation(), $"new {constructor.ContainingType.Name}");
                        break;
                    case IAwaitOperation await when replayed && _analysis.IsExternalAwait(await) && !_analysis.IsSchedulerEscape(DurableTaskSymbols.Unwrap(await.Operation)):
                        if (DurableTaskSymbols.Unwrap(await.Operation) is IInvocationOperation { TargetMethod: { Name: "Delay", ContainingType: var delayType } } && _analysis.IsTask(delayType))
                        {
                            _analysis.Report(DurableTaskDiagnostics.NonDurableDelay, await.Syntax.GetLocation(), "Task.Delay");
                        }
                        else
                        {
                            _analysis.Report(DurableTaskDiagnostics.ExternalAwaitInReplayedCode, await.Syntax.GetLocation(), Describe(await.Operation));
                        }

                        break;
                    case IInvocationOperation invocation:
                        AnalyzeInvocation(invocation, replayed, position);
                        break;
                    case ISimpleAssignmentOperation or ICompoundAssignmentOperation or ICoalesceAssignmentOperation or IIncrementOrDecrementOperation when !replayed && IsStateChange(operation, out var changed) && ReachesExternal(position, block, roots):
                        _analysis.Report(DurableTaskDiagnostics.StateChangeBeforeExternalAwait, operation.Syntax.GetLocation(), changed);
                        break;
                }
            }

            private void AnalyzeInvocation(IInvocationOperation invocation, bool replayed, (int Block, int Index) position)
            {
                var method = invocation.TargetMethod;
                if (replayed && DurableTaskSymbols.IsNonDeterministicMember(method) && !IsLogArgument(invocation))
                {
                    _analysis.Report(DurableTaskDiagnostics.NonDeterministicValueInReplayedCode, invocation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}");
                }

                if (replayed && method.Name == "Sleep" && SymbolEqualityComparer.Default.Equals(method.ContainingType, Symbols.Thread))
                {
                    _analysis.Report(DurableTaskDiagnostics.NonDurableDelay, invocation.Syntax.GetLocation(), "Thread.Sleep");
                }

                if (replayed && Symbols.IsLog(invocation, out var logger) && !IsReplaySafeLogger(logger))
                {
                    _analysis.Report(DurableTaskDiagnostics.ReplayedLog, invocation.Syntax.GetLocation());
                }

                if (Symbols.IsGrainState(invocation.Instance?.Type) && IsMutating(method) && !replayed && ReachesExternal(position, null, null))
                {
                    _analysis.Report(DurableTaskDiagnostics.StateChangeBeforeExternalAwait, invocation.Syntax.GetLocation(), method.Name);
                }

                if (_analysis.IsTask(method.ContainingType) && method.Name is "WhenAll" or "WhenAny" or "WaitAll" or "WaitAny" && ArgumentsPerformDurableWork(invocation))
                {
                    _analysis.Report(DurableTaskDiagnostics.NonDurableCombinator, invocation.Syntax.GetLocation(), $"Task.{method.Name}");
                }

                if (Symbols.IsSignalWait(invocation) && IsInLoop(invocation.Syntax) && DurableTaskSymbols.GetExtensionReceiver(invocation) is { } signal && !IsDerivedSignal(signal))
                {
                    _analysis.Report(DurableTaskDiagnostics.RepeatedSignalWait, invocation.Syntax.GetLocation(), signal.Syntax.ToString());
                }

                // A WithId name identifies a child only in a durable task body; elsewhere, such as in a client, it names a
                // root task. A DurableTask.RunInline name identifies an effect only in a durable task, so a helper which
                // records one runs in a body.
                if ((Symbols.IsRunInline(invocation) || (_kind == FunctionKind.Body && DurableTaskSymbols.IsWithId(invocation)))
                    && GetName(invocation) is { } name)
                {
                    if (name.Value.IndexOf('$') >= 0)
                    {
                        _analysis.Report(DurableTaskDiagnostics.ReservedCharacterInName, name.Location, name.Value);
                    }
                    else
                    {
                        _names.Add((name.Value, position.Block, position.Index, name.Location));
                    }
                }
            }

            private bool IsReplaySafeLogger(IOperation? logger)
            {
                switch (logger is null ? null : DurableTaskSymbols.Unwrap(logger))
                {
                    case IInvocationOperation { TargetMethod.Name: "ReplaySafe" }:
                        return true;
                    case IFieldReferenceOperation field:
                        return _analysis.helpers.IsReplaySafeLogger(field.Field);
                    case IPropertyReferenceOperation property:
                        return _analysis.helpers.IsReplaySafeLogger(property.Property);
                    case ILocalReferenceOperation local:
                        return _analysis.helpers.IsReplaySafeLogger(local.Local);
                    default:
                        return false;
                }
            }

            /// <summary>
            /// Returns the constant name which a <c>WithId</c> or <c>DurableTask.RunInline</c> invocation passes, or null. An
            /// unnamed <c>DurableTask.RunInline</c> takes the execution's next generated identifier and has no name.
            /// </summary>
            private static (string Value, Location Location)? GetName(IInvocationOperation invocation)
            {
                foreach (var argument in invocation.Arguments)
                {
                    if (argument.Parameter is { Type.SpecialType: SpecialType.System_String } && argument.Value.ConstantValue is { HasValue: true, Value: string value })
                    {
                        return (value, argument.Value.Syntax.GetLocation());
                    }
                }

                return null;
            }

            private void ReportDuplicateNames()
            {
                foreach (var group in _names.GroupBy(static name => name.Name))
                {
                    var sites = group.OrderBy(static site => site.Location.SourceSpan.Start).ToArray();
                    for (var later = 1; later < sites.Length; later++)
                    {
                        for (var earlier = 0; earlier < later; earlier++)
                        {
                            if (sites[earlier].Location != sites[later].Location && CanRunInOneRun(sites[earlier], sites[later]))
                            {
                                _analysis.Report(DurableTaskDiagnostics.DuplicateName, sites[later].Location, sites[later].Name);
                                break;
                            }
                        }
                    }
                }
            }

            private bool CanRunInOneRun(
                (string Name, int Block, int Index, Location Location) first,
                (string Name, int Block, int Index, Location Location) second)
            {
                if (first.Block == second.Block)
                {
                    return true;
                }

                return Reaches(first.Block, second.Block) || Reaches(second.Block, first.Block);
            }

            private bool Reaches(int from, int to)
            {
                var visited = new bool[_graph.Blocks.Length];
                var pending = new Stack<int>();
                pending.Push(from);
                while (pending.Count > 0)
                {
                    foreach (var successor in _successors[pending.Pop()])
                    {
                        if (successor.Ordinal == to)
                        {
                            return true;
                        }

                        if (!visited[successor.Ordinal])
                        {
                            visited[successor.Ordinal] = true;
                            pending.Push(successor.Ordinal);
                        }
                    }
                }

                return false;
            }

            private bool ArgumentsPerformDurableWork(IInvocationOperation invocation)
            {
                foreach (var argument in invocation.Arguments)
                {
                    foreach (var operation in DurableTaskSymbols.DescendantsAndSelf(argument.Value))
                    {
                        switch (operation)
                        {
                            case IFlowAnonymousFunctionOperation function:
                                var graph = _graph.GetAnonymousFunctionControlFlowGraph(function, _analysis.context.CancellationToken);
                                if (graph.Blocks.SelectMany(RootOperations).SelectMany(DurableTaskSymbols.DescendantsAndSelf).Any(ContainsDurableOperationShallow))
                                {
                                    return true;
                                }

                                break;
                            case IInvocationOperation call when !call.TargetMethod.DeclaringSyntaxReferences.IsEmpty && _analysis.helpers.IsDurableHelper(call.TargetMethod):
                                return true;
                        }
                    }
                }

                return false;
            }

            private bool IsLogArgument(IOperation operation)
            {
                for (var current = operation.Parent; current is not null; current = current.Parent)
                {
                    if (current is IInvocationOperation invocation && Symbols.IsLog(invocation, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool IsStateChange(IOperation assignment, out string changed)
            {
                var target = assignment switch
                {
                    ISimpleAssignmentOperation simple => simple.Target,
                    ICompoundAssignmentOperation compound => compound.Target,
                    ICoalesceAssignmentOperation coalesce => coalesce.Target,
                    IIncrementOrDecrementOperation increment => increment.Target,
                    _ => null,
                };
                if (target is IPropertyReferenceOperation { Instance: { } instance } property && Symbols.IsGrainState(instance.Type))
                {
                    changed = MemberName(instance);
                    return true;
                }

                changed = string.Empty;
                return false;
            }

            private static bool IsMutating(IMethodSymbol method) =>
                method.Name is "Add" or "AddRange" or "Remove" or "Clear" or "Insert" or "RemoveAt" or "Enqueue" or "Dequeue"
                    or "TryDequeue" or "TryAdd" or "UnionWith" or "IntersectWith" or "ExceptWith" or "SymmetricExceptWith"
                    or "Append" or "TrimBefore" or "TrySetResult" or "TrySetException" or "TrySetCanceled" or "WriteStateAsync"
                    or "ClearStateAsync";

            private static string MemberName(IOperation operation) => operation switch
            {
                IFieldReferenceOperation field => field.Field.Name,
                IPropertyReferenceOperation property => property.Property.Name,
                _ => operation.Syntax.ToString(),
            };

            private static string Describe(IOperation awaited)
            {
                var text = DurableTaskSymbols.Unwrap(awaited).Syntax.ToString();
                return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
            }

            private static bool IsInLoop(SyntaxNode node)
            {
                for (var current = node.Parent; current is not null; current = current.Parent)
                {
                    switch (current)
                    {
                        case ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                            return true;
                        case MemberDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax:
                            return false;
                    }
                }

                return false;
            }

            private static bool IsDerivedSignal(IOperation signal) =>
                DurableTaskSymbols.Unwrap(signal) is IInvocationOperation { TargetMethod.Name: "For" };

            private bool ContainsDurableOperation(IOperation root) => DurableTaskSymbols.DescendantsAndSelf(root).Any(ContainsDurableOperationShallow);

            private bool ContainsDurableOperationShallow(IOperation operation) => operation switch
            {
                IAwaitOperation await => _analysis.IsDurableAwait(await),
                IInvocationOperation invocation => Symbols.IsRunInline(invocation) || DurableTaskSymbols.IsPost(invocation),
                _ => false,
            };

            private bool ContainsExternalAwait(IOperation root) =>
                DurableTaskSymbols.DescendantsAndSelf(root).Any(operation => operation is IAwaitOperation await && _analysis.IsExternalAwait(await));

            private bool ReachesAfter(BasicBlock block, IOperation[] roots, int index, bool[] reach, Func<IOperation, bool> contains)
            {
                for (var later = index + 1; later < roots.Length; later++)
                {
                    if (contains(roots[later]))
                    {
                        return true;
                    }
                }

                return _successors[block.Ordinal].Any(successor => reach[successor.Ordinal]);
            }

            private bool ReachesExternal((int Block, int Index) position, BasicBlock? block, IOperation[]? roots)
            {
                block ??= _graph.Blocks[position.Block];
                roots ??= RootOperations(block).ToArray();
                return ReachesAfter(block, roots, position.Index, _reachesExternal, ContainsExternalAwait);
            }

            private bool[] ComputeReach(Func<IOperation, bool> contains)
            {
                var blocks = _graph.Blocks;
                var reach = new bool[blocks.Length];
                foreach (var block in blocks)
                {
                    reach[block.Ordinal] = RootOperations(block).Any(contains);
                }

                for (var changed = true; changed;)
                {
                    changed = false;
                    for (var ordinal = blocks.Length - 1; ordinal >= 0; ordinal--)
                    {
                        if (!reach[ordinal] && _successors[ordinal].Any(successor => reach[successor.Ordinal]))
                        {
                            reach[ordinal] = true;
                            changed = true;
                        }
                    }
                }

                return reach;
            }

            /// <summary>
            /// Computes each block's successors: its branch destinations, the finally regions which its branches run, the
            /// handlers of the try regions which enclose it, and the destinations which follow a finally region.
            /// </summary>
            private static List<BasicBlock>[] ComputeSuccessors(ControlFlowGraph graph)
            {
                var blocks = graph.Blocks;
                var successors = new List<BasicBlock>[blocks.Length];
                var finallyContinuations = new Dictionary<ControlFlowRegion, List<BasicBlock>>();
                foreach (var block in blocks)
                {
                    var list = successors[block.Ordinal] = [];
                    foreach (var branch in new[] { block.FallThroughSuccessor, block.ConditionalSuccessor })
                    {
                        if (branch is null)
                        {
                            continue;
                        }

                        if (branch.Destination is { } destination)
                        {
                            list.Add(destination);
                        }

                        foreach (var region in branch.FinallyRegions)
                        {
                            list.Add(blocks[region.FirstBlockOrdinal]);
                            if (branch.Destination is { } continuation)
                            {
                                if (!finallyContinuations.TryGetValue(region, out var continuations))
                                {
                                    finallyContinuations[region] = continuations = [];
                                }

                                continuations.Add(continuation);
                            }
                        }
                    }

                    for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                    {
                        if (region.Kind == ControlFlowRegionKind.Try && region.EnclosingRegion is { } tryAndHandlers)
                        {
                            foreach (var handler in tryAndHandlers.NestedRegions)
                            {
                                if (handler != region)
                                {
                                    list.Add(blocks[handler.FirstBlockOrdinal]);
                                }
                            }
                        }
                    }
                }

                foreach (var block in blocks)
                {
                    for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                    {
                        if (region.Kind == ControlFlowRegionKind.Finally
                            && region.LastBlockOrdinal == block.Ordinal
                            && finallyContinuations.TryGetValue(region, out var continuations))
                        {
                            successors[block.Ordinal].AddRange(continuations);
                        }
                    }
                }

                return successors;
            }
        }
    }
}
