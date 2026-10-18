using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>Resolves the durable task, grain state, and platform symbols which the rules inspect, by metadata name.</summary>
/// <remarks>Matching by name keeps the analyzers independent of the abstractions assembly's version.</remarks>
internal sealed class DurableTaskSymbols
{
    private const string DurableTasksNamespace = "Orleans.DurableTasks";

    private readonly ImmutableArray<INamedTypeSymbol> _grainStateTypes;
    private readonly INamedTypeSymbol? _durableValue;
    private readonly INamedTypeSymbol? _persistentState;

    private DurableTaskSymbols(Compilation compilation, INamedTypeSymbol durableTask)
    {
        DurableTask = durableTask;
        DurableSignal = compilation.GetTypeByMetadataName("Orleans.DurableTasks.DurableSignal`1");
        DurableVersion = compilation.GetTypeByMetadataName("Orleans.DurableTasks.DurableVersion");
        GrainBase = compilation.GetTypeByMetadataName("Orleans.IGrainBase");
        Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        Thread = compilation.GetTypeByMetadataName("System.Threading.Thread");
        ThreadPool = compilation.GetTypeByMetadataName("System.Threading.ThreadPool");
        Parallel = compilation.GetTypeByMetadataName("System.Threading.Tasks.Parallel");
        TaskFactory = compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory");
        CancellationTokenSource = compilation.GetTypeByMetadataName("System.Threading.CancellationTokenSource");
        OperationCanceledException = compilation.GetTypeByMetadataName("System.OperationCanceledException");
        Logger = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ILogger");
        LoggerExtensions = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.LoggerExtensions");
        LoggerMessageAttribute = compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.LoggerMessageAttribute");
        TimerTypes = Resolve(compilation, "System.Threading.Timer", "System.Timers.Timer", "System.Threading.PeriodicTimer");

        var context = compilation.GetTypeByMetadataName("Orleans.DurableTasks.DurableTaskContext");
        IsTaskCancellationRequested = GetProperty(context, "IsTaskCancellationRequested");
        ContextCancellationToken = GetProperty(context, "CancellationToken");

        var stateTypes = Resolve(
            compilation,
            "Orleans.Journaling.IDurableValue`1",
            "Orleans.Journaling.IDurableDictionary`2",
            "Orleans.Journaling.IDurableList`1",
            "Orleans.Journaling.IDurableQueue`1",
            "Orleans.Journaling.IDurableSet`1",
            "Orleans.Journaling.IDurableTaskCompletionSource`1",
            "Orleans.Journaling.IDurableLog`1",
            "Orleans.Runtime.IPersistentState`1");
        _grainStateTypes = stateTypes;
        _durableValue = compilation.GetTypeByMetadataName("Orleans.Journaling.IDurableValue`1");
        _persistentState = compilation.GetTypeByMetadataName("Orleans.Runtime.IPersistentState`1");
        NonDeterministicTypes = Resolve(
            compilation,
            "System.DateTime",
            "System.DateTimeOffset",
            "System.Guid",
            "System.Random",
            "System.Security.Cryptography.RandomNumberGenerator",
            "System.Diagnostics.Stopwatch",
            "System.Environment",
            "System.TimeProvider");
    }

    public INamedTypeSymbol DurableTask { get; }

    public INamedTypeSymbol? DurableSignal { get; }

    public INamedTypeSymbol? DurableVersion { get; }

    public INamedTypeSymbol? GrainBase { get; }

    public INamedTypeSymbol? Task { get; }

    public INamedTypeSymbol? Thread { get; }

    public INamedTypeSymbol? ThreadPool { get; }

    public INamedTypeSymbol? Parallel { get; }

    public INamedTypeSymbol? TaskFactory { get; }

    public INamedTypeSymbol? CancellationTokenSource { get; }

    public INamedTypeSymbol? OperationCanceledException { get; }

    public INamedTypeSymbol? Logger { get; }

    public INamedTypeSymbol? LoggerExtensions { get; }

    public INamedTypeSymbol? LoggerMessageAttribute { get; }

    public ImmutableArray<INamedTypeSymbol> TimerTypes { get; }

    /// <summary>Gets <c>DurableTaskContext.IsTaskCancellationRequested</c>, which reports durable task cancellation.</summary>
    public IPropertySymbol? IsTaskCancellationRequested { get; }

    /// <summary>
    /// Gets <c>DurableTaskContext.CancellationToken</c>, which is canceled by task cancellation and by attempt cancellation.
    /// </summary>
    public IPropertySymbol? ContextCancellationToken { get; }

    public ImmutableArray<INamedTypeSymbol> NonDeterministicTypes { get; }

    /// <summary>Returns the symbols for a compilation which references the durable task abstractions, or null.</summary>
    public static DurableTaskSymbols? Create(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Orleans.DurableTasks.DurableTask") is { } durableTask
            ? new DurableTaskSymbols(compilation, durableTask)
            : null;

    /// <summary>Returns whether a type is <c>DurableTask</c> or derives from it.</summary>
    public bool IsDurableTask(ITypeSymbol? type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, DurableTask))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a type is declared in the durable task abstractions' namespace.</summary>
    public static bool IsDurableTasksType(ITypeSymbol? type) =>
        type?.ContainingNamespace?.ToDisplayString() is { } name
        && (name == DurableTasksNamespace || name.StartsWith(DurableTasksNamespace + ".", StringComparison.Ordinal));

    /// <summary>Returns whether a method returns a durable task definition or a configuration of one.</summary>
    public bool ReturnsDefinition(IMethodSymbol method) => IsDefinitionType(method.ReturnType);

    /// <summary>Returns whether a type is a durable task definition or a configuration of one.</summary>
    public bool IsDefinitionType(ITypeSymbol? type) =>
        IsDurableTask(type) || (IsDurableTasksType(type) && type!.Name == "ConfiguredDurableTask");

    /// <summary>Returns whether a type, or a type which contains it, is a grain: it implements <c>IGrainBase</c>.</summary>
    public bool IsGrain(INamedTypeSymbol? type)
    {
        if (GrainBase is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.ContainingType)
        {
            foreach (var @interface in current.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(@interface, GrainBase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns whether a method is a durable task body: an async method which returns <c>DurableTask</c>.</summary>
    public bool IsDurableBody(IMethodSymbol method) => method.IsAsync && IsDurableTask(method.ReturnType);

    /// <summary>
    /// Returns whether an operation runs in a durable task body: an <c>async DurableTask</c> method, or a delegate which a
    /// durable task factory, such as <c>DurableTask.Run</c>, runs, including code which such a body contains.
    /// </summary>
    public bool IsInDurableBody(IOperation operation, ISymbol containingSymbol)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IAnonymousFunctionOperation function
                    when IsDurableBody(function.Symbol) || GetDelegateReceiver(function) is { } factory && IsDurableTasksType(factory.TargetMethod.ContainingType):
                    return true;
                case ILocalFunctionOperation local when IsDurableBody(local.Symbol):
                    return true;
            }
        }

        return containingSymbol is IMethodSymbol method && IsDurableBody(method);
    }

    /// <summary>Returns the invocation which receives a delegate as an argument, or null.</summary>
    public static IInvocationOperation? GetDelegateReceiver(IOperation function)
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

    /// <summary>
    /// Returns the operation which consumes a definition, through the configuration calls which wrap it, and whether one of
    /// those calls is <paramref name="configuration"/>.
    /// </summary>
    public static IOperation? GetConsumer(IOperation definition, string configuration, out bool configured)
    {
        configured = false;
        var current = definition;
        while (current.Parent is { } parent)
        {
            if (parent is IConversionOperation)
            {
                current = parent;
                continue;
            }

            var configurationCall = parent switch
            {
                IInvocationOperation invocation when invocation.Instance == current => invocation,
                IArgumentOperation { Parent: IInvocationOperation invocation } argument
                    when invocation.Instance is null && invocation.Arguments[0] == argument => invocation,
                _ => null,
            };
            if (configurationCall is null || !IsConfiguration(configurationCall))
            {
                return parent;
            }

            configured |= configurationCall.TargetMethod.Name == configuration;
            current = configurationCall;
        }

        return null;
    }

    /// <summary>
    /// Returns the operation which creates a definition, through the configuration calls which wrap it, and whether one of
    /// those calls is <paramref name="configuration"/>.
    /// </summary>
    public static IOperation GetDefinition(IOperation value, string configuration, out bool configured)
    {
        configured = false;
        var current = Unwrap(value);
        while (current is IInvocationOperation invocation && IsConfiguration(invocation) && GetExtensionReceiver(invocation) is { } receiver)
        {
            configured |= invocation.TargetMethod.Name == configuration;
            current = Unwrap(receiver);
        }

        return current;
    }

    /// <summary>
    /// Returns whether an invocation creates a combinator, which an awaiting execution runs as part of itself under a
    /// generated operation identifier: <c>WhenAll</c>, <c>WhenAny</c>, <c>Race</c>, <c>ScheduleAll</c>,
    /// <c>WhenAllCompleted</c>, a definition's <c>WaitAsync</c> or <c>WaitUntilAsync</c>, or a signal's <c>TryWaitAsync</c>.
    /// </summary>
    public bool IsCombinator(IInvocationOperation invocation) =>
        IsDurableTask(invocation.TargetMethod.ReturnType)
        && IsDurableTasksType(invocation.TargetMethod.ContainingType)
        && invocation.TargetMethod.Name is "WhenAll" or "WhenAny" or "Race" or "ScheduleAll" or "WhenAllCompleted" or "WaitAsync"
            or "WaitUntilAsync" or "TryWaitAsync"
        && !(invocation.TargetMethod.Name == "WaitAsync" && IsSignalWait(invocation));

    /// <summary>
    /// Returns whether an operation reads a value which grain state holds: <c>IDurableValue&lt;T&gt;.Value</c>,
    /// <c>IPersistentState&lt;T&gt;.State</c>, or an element of a journaled collection through its indexer.
    /// </summary>
    public bool IsStateValue(IOperation operation) =>
        operation is IPropertyReferenceOperation { Instance.Type: { } stateType, Property: var property }
        && (property.IsIndexer
            ? IsGrainState(stateType)
            : (property.Name == "Value" && Implements(stateType, _durableValue))
                || (property.Name == "State" && Implements(stateType, _persistentState)));

    /// <summary>
    /// Returns whether a method changes the collection which receives it in place, as <c>List&lt;T&gt;.Add</c> does,
    /// rather than returning a changed copy, as immutable collections do.
    /// </summary>
    public static bool IsCollectionMutation(IMethodSymbol method) =>
        !method.IsStatic
        && method.Name is "Add" or "AddRange" or "Remove" or "RemoveAt" or "RemoveAll" or "RemoveRange" or "RemoveWhere" or "Clear"
            or "Insert" or "InsertRange" or "Enqueue" or "Dequeue" or "TryDequeue" or "Push" or "Pop" or "TryPop" or "TryAdd"
            or "TryRemove" or "UnionWith" or "IntersectWith" or "ExceptWith" or "SymmetricExceptWith" or "Sort" or "Reverse"
        && !SymbolEqualityComparer.Default.Equals(method.ReturnType.OriginalDefinition, method.ContainingType.OriginalDefinition)
        && method.ContainingNamespace?.ToDisplayString() != "System.Collections.Immutable";

    /// <summary>Returns whether a type is journaled or persistent grain state, or implements one of those types.</summary>
    public bool IsGrainState(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || _grainStateTypes.IsEmpty)
        {
            return false;
        }

        if (IsGrainStateDefinition(named))
        {
            return true;
        }

        foreach (var @interface in named.AllInterfaces)
        {
            if (IsGrainStateDefinition(@interface))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether an await observes a durable operation of the running task: the awaited value or its awaiter is a
    /// durable task type, or an abstractions API produced it.
    /// </summary>
    public bool IsDurableAwait(IAwaitOperation await)
    {
        var awaited = Unwrap(await.Operation);
        if (IsDurableTasksType(awaited.Type))
        {
            return true;
        }

        return awaited is IInvocationOperation invocation && IsDurableTasksType(invocation.TargetMethod.ContainingType);
    }

    /// <summary>Returns whether an invocation records an effect: <c>DurableTask.RunInline</c>, named or unnamed.</summary>
    public bool IsRunInline(IInvocationOperation invocation) =>
        invocation.TargetMethod.Name == "RunInline"
        && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, DurableTask);

    /// <summary>Returns whether an invocation creates a delegate definition: <c>DurableTask.Run</c>.</summary>
    public bool IsRun(IInvocationOperation invocation) =>
        invocation.TargetMethod.Name == "Run"
        && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, DurableTask);

    /// <summary>Returns whether an invocation names a durable operation: <c>WithId</c> on a definition or configuration.</summary>
    public static bool IsWithId(IInvocationOperation invocation) =>
        invocation.TargetMethod.Name == "WithId" && IsDurableTasksType(invocation.TargetMethod.ContainingType);

    /// <summary>
    /// Returns whether an invocation configures a definition or configuration: <c>WithId</c>, <c>Detached</c>, or
    /// <c>Shielded</c>.
    /// </summary>
    public static bool IsConfiguration(IInvocationOperation invocation) =>
        invocation.TargetMethod.Name is "WithId" or "Detached" or "Shielded"
        && IsDurableTasksType(invocation.TargetMethod.ContainingType);

    /// <summary>Returns whether an invocation posts a definition or configuration.</summary>
    public static bool IsPost(IInvocationOperation invocation) =>
        invocation.TargetMethod.Name == "Post" && IsDurableTasksType(invocation.TargetMethod.ContainingType);

    /// <summary>Returns whether an invocation waits for a durable signal.</summary>
    public bool IsSignalWait(IInvocationOperation invocation) =>
        invocation.TargetMethod.Name is "WaitAsync" or "TryWaitAsync"
        && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType?.OriginalDefinition, DurableSignal);

    /// <summary>Returns whether an invocation logs through a logger.</summary>
    public bool IsLog(IInvocationOperation invocation, out IOperation? logger)
    {
        logger = null;
        var method = invocation.TargetMethod;
        if (Logger is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, LoggerExtensions)
            || (method.Name == "Log" && SymbolEqualityComparer.Default.Equals(method.ContainingType, Logger)))
        {
            logger = method.IsExtensionMethod || method.MethodKind == MethodKind.ReducedExtension
                ? GetExtensionReceiver(invocation)
                : invocation.Instance;
            return true;
        }

        if (LoggerMessageAttribute is not null && method.IsStatic && HasAttribute(method, LoggerMessageAttribute))
        {
            foreach (var argument in invocation.Arguments)
            {
                if (SymbolEqualityComparer.Default.Equals(argument.Parameter?.Type, Logger))
                {
                    logger = argument.Value;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns the receiver of an extension method invocation, or the instance of an instance invocation.</summary>
    public static IOperation? GetExtensionReceiver(IInvocationOperation invocation) =>
        invocation.Instance ?? (invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null);

    /// <summary>
    /// Returns the value which a local's declaration assigns to it, when no code in the declaring block assigns the local
    /// again, or null.
    /// </summary>
    public static IOperation? GetOnlyValue(ILocalSymbol local, SemanticModel semanticModel)
    {
        if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax { Initializer: not null } declarator
            || semanticModel.GetOperation(declarator) is not IVariableDeclaratorOperation { Initializer.Value: { } value }
            || (declarator.FirstAncestorOrSelf<BlockSyntax>() is { } block
                && block.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                    assignment.Left is IdentifierNameSyntax { Identifier.ValueText: var name }
                    && name == local.Name
                    && SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(assignment.Left).Symbol, local))))
        {
            return null;
        }

        return value;
    }

    /// <summary>Strips conversions and parentheses from an operation.</summary>
    public static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation { IsImplicit: true } conversion:
                    operation = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return operation;
            }
        }
    }

    private bool IsGrainStateDefinition(INamedTypeSymbol type)
    {
        foreach (var stateType in _grainStateTypes)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, stateType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol? definition)
    {
        if (definition is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, definition))
        {
            return true;
        }

        foreach (var @interface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, definition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
            {
                return true;
            }
        }

        return false;
    }

    private static IPropertySymbol? GetProperty(INamedTypeSymbol? type, string name) =>
        type?.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();

    private static ImmutableArray<INamedTypeSymbol> Resolve(Compilation compilation, params string[] names)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>(names.Length);
        foreach (var name in names)
        {
            if (compilation.GetTypeByMetadataName(name) is { } type)
            {
                builder.Add(type);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>Returns whether a symbol is one of <paramref name="types"/>.</summary>
    public static bool IsOneOf(ITypeSymbol? type, ImmutableArray<INamedTypeSymbol> types)
    {
        foreach (var candidate in types)
        {
            if (SymbolEqualityComparer.Default.Equals(type?.OriginalDefinition, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the members of a type which produce values which differ between runs.</summary>
    public static bool IsNonDeterministicMember(ISymbol member)
    {
        var type = member.ContainingType?.ToDisplayString();
        return (type, member.Name) switch
        {
            ("System.DateTime", "Now" or "UtcNow" or "Today") => true,
            ("System.DateTimeOffset", "Now" or "UtcNow") => true,
            ("System.Guid", "NewGuid" or "CreateVersion7") => true,
            ("System.Random", "Shared" or ".ctor") => true,
            ("System.Security.Cryptography.RandomNumberGenerator", _) => member.IsStatic,
            ("System.Diagnostics.Stopwatch", "StartNew" or "GetTimestamp" or ".ctor") => true,
            ("System.Environment", "TickCount" or "TickCount64" or "MachineName" or "ProcessId" or "CurrentManagedThreadId"
                or "GetEnvironmentVariable" or "GetEnvironmentVariables" or "ProcessorCount") => true,
            ("System.TimeProvider", "GetUtcNow" or "GetLocalNow" or "GetTimestamp") => true,
            _ => false,
        };
    }

    /// <summary>Returns the method names which identify durable operations in a callee's source.</summary>
    public static readonly ImmutableHashSet<string> DurableOperationNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "WithId",
        "RunInline",
        "Post",
        "ScheduleAsync",
        "SignalDurableTaskAsync",
        "TrySignalDurableTaskAsync",
        "WhenAll",
        "WhenAny",
        "WhenAllCompleted",
        "ScheduleAll",
        "WhenEach",
        "WaitAsync",
        "TryWaitAsync",
        "Delay",
        "DelayUntil");

    internal static IEnumerable<IOperation> DescendantsAndSelf(IOperation operation)
    {
        yield return operation;
        foreach (var descendant in operation.Descendants())
        {
            yield return descendant;
        }
    }
}
