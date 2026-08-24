using System.Collections.Immutable;
using Orleans.Concurrency;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class ReadOnlySchedulingGrain : Grain, IReadOnlySchedulingGrain
{
    private readonly Dictionary<string, OperationGate> _operations = [];
    private readonly List<string> _events = [];

    public Task<string> ReadOnlyEcho(string value) => Task.FromResult($"read:{value}");

    public Task<string> WritableEcho(string value) => Task.FromResult($"write:{value}");

    public Task<string> AlwaysInterleaveEcho(string value) => Task.FromResult($"interleave:{value}");

    public Task<string> ReadOnlyAlwaysInterleaveEcho(string value) => Task.FromResult($"read-interleave:{value}");

    public Task BlockReadOnly(string operationId) => Block(operationId);

    public Task BlockWritable(string operationId) => Block(operationId);

    public Task BlockReadOnlyAlwaysInterleave(string operationId) => Block(operationId);

    public Task WaitForEntry(string operationId) => GetOrCreate(operationId).Entered.Task;

    public Task Release(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation) || !operation.Started)
        {
            throw new InvalidOperationException($"Operation '{operationId}' has not entered.");
        }

        if (!operation.Release.TrySetResult())
        {
            throw new InvalidOperationException($"Operation '{operationId}' has already been released.");
        }

        return Task.CompletedTask;
    }

    public Task<ImmutableArray<string>> GetEvents() => Task.FromResult(_events.ToImmutableArray());

    public Task Reset()
    {
        var running = _operations.Where(static pair => pair.Value.Started && !pair.Value.Exited).Select(static pair => pair.Key).ToArray();
        if (running.Length > 0)
        {
            throw new InvalidOperationException($"Cannot reset while operations are running: {string.Join(", ", running)}.");
        }

        _operations.Clear();
        _events.Clear();
        return Task.CompletedTask;
    }

    public Task Checkpoint(string checkpointId)
    {
        _events.Add($"checkpoint:{checkpointId}");
        return Task.CompletedTask;
    }

    private async Task Block(string operationId)
    {
        var operation = GetOrCreate(operationId);
        if (operation.Started)
        {
            throw new InvalidOperationException($"Operation '{operationId}' has already started.");
        }

        operation.Started = true;
        _events.Add($"enter:{operationId}");
        operation.Entered.TrySetResult();

        await operation.Release.Task;

        _events.Add($"exit:{operationId}");
        operation.Exited = true;
    }

    private OperationGate GetOrCreate(string operationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);

        if (!_operations.TryGetValue(operationId, out var operation))
        {
            operation = new OperationGate();
            _operations.Add(operationId, operation);
        }

        return operation;
    }

    private sealed class OperationGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started { get; set; }

        public bool Exited { get; set; }
    }
}

public class ReadOnlyInheritedInterfaceGrain : Grain, IReadOnlyInheritedInterfaceDerivedGrain
{
    public Task<string> InheritedReadOnly() => Task.FromResult("inherited-interface");
}

public class ReadOnlyRedeclaredBaseReadOnlyGrain : Grain, IReadOnlyRedeclaredDerivedWritableGrain
{
    public Task<string> Redeclared() => Task.FromResult("base-readonly-derived-writable");
}

public class ReadOnlyRedeclaredBaseWritableGrain : Grain, IReadOnlyRedeclaredDerivedReadOnlyGrain
{
    public Task<string> Redeclared() => Task.FromResult("base-writable-derived-readonly");
}

public abstract class ReadOnlySchedulingBaseImplementationGrain : Grain
{
    public Task<string> InheritedImplementation() => Task.FromResult("inherited-implementation");
}

public class ReadOnlySchedulingDerivedImplementationGrain
    : ReadOnlySchedulingBaseImplementationGrain, IReadOnlyInheritedImplementationGrain
{
}

public abstract class ImplementationOnlyReadOnlySchedulingBaseGrain : Grain
{
    [ReadOnly]
    public Task<string> ImplementationOnly() => Task.FromResult("implementation-only");
}

public class ImplementationOnlyReadOnlySchedulingGrain
    : ImplementationOnlyReadOnlySchedulingBaseGrain, IImplementationOnlyReadOnlySchedulingGrain
{
}

public class ReadOnlyOverloadSchedulingGrain : Grain, IReadOnlyOverloadSchedulingGrain
{
    public Task<string> Overloaded(int value) => Task.FromResult($"readonly-int:{value}");

    public Task<string> Overloaded(string value) => Task.FromResult($"writable-string:{value}");
}

public class ReadOnlyGenericMethodSchedulingGrain : Grain, IReadOnlyGenericMethodSchedulingGrain
{
    public Task<T> GenericReadOnly<T>(T value) => Task.FromResult(value);
}

public class ReadOnlyGenericSchedulingGrain<T> : Grain, IReadOnlyGenericSchedulingGrain<T>
{
    public Task<T> GenericReadOnly(T value) => Task.FromResult(value);
}

public abstract class ReadOnlyPolicySchedulingGrainBase : Grain
{
    private readonly Dictionary<string, OperationGate> _operations = [];
    private readonly List<string> _events = [];

    public Task BlockReadOnly(string operationId) => Block(operationId);

    public Task BlockWritable(string operationId) => Block(operationId);

    public Task WaitForEntry(string operationId) => GetOrCreate(operationId).Entered.Task;

    public Task Release(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation) || !operation.Started)
        {
            throw new InvalidOperationException($"Operation '{operationId}' has not entered.");
        }

        if (!operation.Release.TrySetResult())
        {
            throw new InvalidOperationException($"Operation '{operationId}' has already been released.");
        }

        return Task.CompletedTask;
    }

    public Task<ImmutableArray<string>> GetEvents() => Task.FromResult(_events.ToImmutableArray());

    public Task Checkpoint(string checkpointId)
    {
        _events.Add($"checkpoint:{checkpointId}");
        return Task.CompletedTask;
    }

    private async Task Block(string operationId)
    {
        var operation = GetOrCreate(operationId);
        if (operation.Started)
        {
            throw new InvalidOperationException($"Operation '{operationId}' has already started.");
        }

        operation.Started = true;
        _events.Add($"enter:{operationId}");
        operation.Entered.TrySetResult();

        await operation.Release.Task;

        _events.Add($"exit:{operationId}");
        operation.Exited = true;
    }

    private OperationGate GetOrCreate(string operationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);

        if (!_operations.TryGetValue(operationId, out var operation))
        {
            operation = new OperationGate();
            _operations.Add(operationId, operation);
        }

        return operation;
    }

    private sealed class OperationGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started { get; set; }

        public bool Exited { get; set; }
    }
}

[Reentrant]
public class ReadOnlyReentrantSchedulingGrain
    : ReadOnlyPolicySchedulingGrainBase, IReadOnlyReentrantSchedulingGrain
{
}

[MayInterleave(nameof(MayInterleave))]
public class ReadOnlyMayInterleaveTrueSchedulingGrain
    : ReadOnlyPolicySchedulingGrainBase, IReadOnlyMayInterleaveTrueSchedulingGrain
{
    public static bool MayInterleave(Orleans.Serialization.Invocation.IInvokable request) =>
        request.GetMethodName() == nameof(IReadOnlyPolicySchedulingGrain.BlockWritable);
}

[MayInterleave(nameof(MayInterleave))]
public class ReadOnlyMayInterleaveFalseSchedulingGrain
    : ReadOnlyPolicySchedulingGrainBase, IReadOnlyMayInterleaveFalseSchedulingGrain
{
    public static bool MayInterleave(Orleans.Serialization.Invocation.IInvokable request) => false;
}

[MayInterleave(nameof(MayInterleave))]
public class ReadOnlyMayInterleaveReadSchedulingGrain
    : ReadOnlyPolicySchedulingGrainBase, IReadOnlyMayInterleaveReadSchedulingGrain
{
    public static bool MayInterleave(Orleans.Serialization.Invocation.IInvokable request) =>
        request.GetMethodName() == nameof(IReadOnlyPolicySchedulingGrain.BlockReadOnly);
}
