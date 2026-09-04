#nullable enable
using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Invocation;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents a durable task request.
/// </summary>
public interface IDurableTaskRequest : IRequest
{
    private const int MaxEquivalentArgumentLength = 256 * 1024;
    private const int MaxEquivalentArgumentsTotalLength = 1024 * 1024;
    private const int MaxEquivalentArgumentCount = 128;

    /// <summary>
    /// Gets the task request context.
    /// </summary>
    DurableTaskRequestContext? Context { get; }

    /// <summary>
    /// Invoke the method on the target.
    /// </summary>
    /// <returns>The result of invocation.</returns>
    //ValueTask<DurableTaskResponse> InvokeImplementation(DurableExecutionContext executionContext);
    DurableTask CreateTask();

    /// <summary>
    /// Returns a string representation of the request.
    /// </summary>
    /// <returns>A string representation of the request.</returns>
    public string ToMethodCallString() => ToMethodCallString(this);

    internal static bool AreRequestsEquivalent(
        IDurableTaskRequest left,
        IDurableTaskRequest right,
        Serializer serializer)
    {
        if (!string.Equals(left.GetInterfaceName(), right.GetInterfaceName(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(left.GetMethodName(), right.GetMethodName(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!HaveEquivalentSignatures(left, right))
        {
            return false;
        }

        if (left.GetArgumentCount() != right.GetArgumentCount())
        {
            return false;
        }

        if (left.GetArgumentCount() > MaxEquivalentArgumentCount)
        {
            throw new InvalidOperationException(
                $"Durable task requests exceed the {MaxEquivalentArgumentCount}-argument equivalence limit.");
        }

        var leftBuffer = ArrayPool<byte>.Shared.Rent(MaxEquivalentArgumentLength);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(MaxEquivalentArgumentLength);
        try
        {
            var totalLength = 0;
            for (var arg = 0; arg < left.GetArgumentCount(); arg++)
            {
                var leftBytes = SerializeArgument(left.GetArgument(arg), serializer, arg, leftBuffer);
                var rightBytes = SerializeArgument(right.GetArgument(arg), serializer, arg, rightBuffer);
                totalLength = checked(totalLength + leftBytes.Length + rightBytes.Length);
                if (totalLength > MaxEquivalentArgumentsTotalLength)
                {
                    throw new InvalidOperationException(
                        $"Durable task request arguments exceed the {MaxEquivalentArgumentsTotalLength}-byte equivalence limit.");
                }

                if (!leftBytes.Span.SequenceEqual(rightBytes.Span))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(rightBuffer, clearArray: true);
        }

        return (left.Context, right.Context) switch
        {
            (null, null) => true,
            ({ } leftContext, { } rightContext) => leftContext.HasEquivalentApplicationValues(rightContext),
            _ => false,
        };

        static bool HaveEquivalentSignatures(IDurableTaskRequest left, IDurableTaskRequest right)
        {
            var leftMethod = left.GetMethod();
            var rightMethod = right.GetMethod();
            if (leftMethod is null || rightMethod is null)
            {
                return leftMethod is null
                    && rightMethod is null
                    && left.GetType() == right.GetType();
            }

            if (leftMethod.ReturnType != rightMethod.ReturnType
                || leftMethod.IsGenericMethod != rightMethod.IsGenericMethod)
            {
                return false;
            }

            if (leftMethod.IsGenericMethod
                && !leftMethod.GetGenericArguments().SequenceEqual(rightMethod.GetGenericArguments()))
            {
                return false;
            }

            return leftMethod.GetParameters().Select(static parameter => parameter.ParameterType)
                .SequenceEqual(rightMethod.GetParameters().Select(static parameter => parameter.ParameterType));
        }

        static ReadOnlyMemory<byte> SerializeArgument(
            object? value,
            Serializer serializer,
            int argumentIndex,
            byte[] buffer)
        {
            Memory<byte> destination = buffer.AsMemory(0, MaxEquivalentArgumentLength);
            try
            {
                serializer.Serialize(value, ref destination);
                return destination;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Durable task request argument {argumentIndex} could not be serialized within the {MaxEquivalentArgumentLength}-byte equivalence limit.",
                    exception);
            }
        }
    }
}

/// <summary>Provides services shared by generated durable task requests.</summary>
/// <param name="grainContextAccessor">The current grain context accessor.</param>
/// <param name="grainFactory">The grain factory.</param>
/// <param name="serializer">The serializer used to preserve scheduling-time request context.</param>
public sealed class DurableTaskRequestShared(
    IGrainContextAccessor grainContextAccessor,
    IGrainFactory grainFactory,
    Serializer serializer)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public IGrainFactory GrainFactory { get; } = grainFactory;
    internal Serializer Serializer { get; } = serializer;
}

[GenerateSerializer]
[ReturnValueProxy(initializerMethodName: nameof(InitializeRequest))]
[Alias("DurableTaskRequest")]
[method: GeneratedActivatorConstructor]
public abstract class DurableTaskRequest(DurableTaskRequestShared shared) : DurableTask, IDurableTaskRequest, ISchedulableTask
{
    bool ISchedulableTask.CommitsDurableState => true;

    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly DurableTaskRequestShared _shared = shared;

    /// <inheritdoc />
    [Id(0)]
    public DurableTaskRequestContext? Context { get; private set; }

    /// <summary>
    /// Gets the invocation options.
    /// </summary>
    [field: NonSerialized]
    public InvokeMethodOptions Options { get; private set; }

    /// <inheritdoc/>
    public virtual int GetArgumentCount() => 0;

    /// <summary>
    /// Incorporates the provided invocation options.
    /// </summary>
    /// <param name="options">
    /// The options.
    /// </param>
    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    /// <inheritdoc/>
    public abstract object GetTarget();

    /// <inheritdoc/>
    public abstract void SetTarget(ITargetHolder holder);

    /// <inheritdoc/>
    public virtual object GetArgument(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "The request has zero arguments.");

    /// <inheritdoc/>
    public virtual void SetArgument(int index, object value) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "The request has zero arguments.");

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <inheritdoc/>
    public abstract string GetMethodName();

    /// <inheritdoc/>
    public abstract string GetInterfaceName();

    /// <inheritdoc/>
    public abstract string GetActivityName();

    /// <inheritdoc/>
    public abstract Type GetInterfaceType();

    /// <inheritdoc/>
    public abstract MethodInfo GetMethod();

    /// <inheritdoc/>
    public override string ToString() => IRequest.ToString(this);

    // Called upon creation in generated code by the creating grain reference by virtue of the [SelfInvokingReturnType(nameof(InitializeRequest))] attribute on this class.
    public DurableTask InitializeRequest(GrainReference targetGrainReference)
    {
        // Capture the request context.
        Context = new()
        {
            // TaskId will be filled in later, before submission, via an extension method at the call site.
            TargetId = targetGrainReference.GrainId,
        };
        return this;
    }

    async ValueTask<DurableTaskResponse> ISchedulableTask.ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        Debug.Assert(Context is not null);
        Context.Values = DurableTaskRequestContext.CaptureRequestContext(_shared.Serializer);

        if (TryGetRuntime(out var runtime))
        {
            return await runtime.ScheduleRemoteAsync(taskId, this, cancellationToken);
        }

        var targetGrain = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        return await targetGrain.ScheduleAsync(taskId, this, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        // Schedule this request with the remote service.
        // If the task has already been submitted then this will submit it again, which is an idempotent operation if:
        // * The task is semantically identical (same implementation and arguments).
        // * The task did not complete already and was subsequently cleaned up.
        // We can be sure that the task was not already cleaned up if we are calling from a grain which has a stable identifier, since
        // the caller must acknowledge completion before the task is eligible for garbage collection.
        // For the first point (identical implementation and arguments), we could store the task locally and verify it against its already-stored copy.
        // This check can also be performed remotely instead, since the remote host must have stored a copy of the request in order to be able to execute it.
        Debug.Assert(Context is not null);
        Context.Values = DurableTaskRequestContext.CaptureRequestContext(_shared.Serializer);
        if (TryGetRuntime(out var runtime))
        {
            using var durableCts = new CancellationTokenSource();
            using var durableDeactivationRegistration = executionContext.RegisterDeactivationCallback(
                static (cts, _) => cts.CancelAsync(),
                durableCts);
            using var durableRegistration = executionContext.RegisterCancellationCallback(
                static async (state, cancellationToken) =>
                {
                    await state.cts.CancelAsync();
                    await state.runtime.CancelRemoteAsync(state.taskId, state.target, cancellationToken);
                },
                state: (runtime, cts: durableCts, taskId: executionContext.TaskId, target: Context.TargetId));
            var durableResponse = await runtime.ScheduleRemoteAsync(executionContext.TaskId, this, durableCts.Token);
            return durableResponse.IsCompleted
                ? durableResponse
                : await runtime.GetScheduledTaskHandle(executionContext.TaskId).WaitAsync(durableCts.Token);
        }

        var callerContext = RuntimeContext.Current;
        if (callerContext is not null)
        {
            Context.CallerId = callerContext.GrainId;
        }

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        using var cts = new CancellationTokenSource();
        using var remoteDeactivationRegistration = executionContext.RegisterDeactivationCallback(
            static (source, _) => source.CancelAsync(),
            cts);
        using var registration = executionContext.RegisterCancellationCallback(
            static async (state, cancellationToken) =>
            {
                await state.cts.CancelAsync();
                await state.remote.CancelAsync(state.executionContext.TaskId, cancellationToken);
            },
            state: (remote, cts, executionContext));
        var response = await remote.ScheduleAsync(executionContext.TaskId, this, cts.Token);
        var options = new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromSeconds(5) };
        while (!response.IsCompleted && !cts.IsCancellationRequested)
        {
            response = await remote.SubscribeOrPollAsync(executionContext.TaskId, options, cts.Token);
        }

        return response;
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke()
        // This could be made to work... maybe pick a random task id, for example.
        => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    //ValueTask<DurableTaskResponse> IDurableTaskRequest.InvokeImplementation(DurableExecutionContext executionContext) => DurableTaskRuntimeHelper.RunAsync(InvokeInner(), executionContext);
    DurableTask IDurableTaskRequest.CreateTask() => InvokeInner();

    // Generated. This invokes the target method directly.
    protected abstract DurableTask InvokeInner();

    internal static bool TryGetRuntime([NotNullWhen(true)] out IDurableTaskGrainRuntime? runtime)
    {
        if (RuntimeContext.Current?.GetComponent<IDurableTaskGrainRuntime>() is not { } localProxy)
        {
            runtime = null;
            return false;
        }

        runtime = localProxy;
        return true;
    }

    /// <inheritdoc/>
    public virtual TimeSpan? GetDefaultResponseTimeout() => null;

    public IScheduledTaskHandle GetHandle(TaskId taskId)
    {
        Debug.Assert(Context is not null);
        return new GrainScheduledTaskHandle(taskId, this, _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId), lastResponse: null);
    }
}

/// <summary>
/// Represents a request to schedule a <see cref="DurableTask{TResult}"/>-returning method.
/// </summary>
[GenerateSerializer]
[ReturnValueProxy(initializerMethodName: nameof(InitializeRequest))]
[Alias("DurableTaskRequest`1")]
[method: GeneratedActivatorConstructor]
public abstract class DurableTaskRequest<TResult>(DurableTaskRequestShared shared) : DurableTask<TResult>, IDurableTaskRequest, ISchedulableTask
{
    bool ISchedulableTask.CommitsDurableState => true;

    // Note: we could save a field here by using RuntimeContext, but that will require making internals visible to this assembly.
    // For now, we're not doing that, just to make sure that we can get far without needing it, demonstrating the extensibility of Orleans.
    // It might be worthwhile making RuntimeContext public at some point, even if it is not the recommended approach.
    [NonSerialized]
    private readonly DurableTaskRequestShared _shared = shared;

    /// <inheritdoc/>
    [Id(0)]
    public DurableTaskRequestContext? Context { get; private set; }

    /// <summary>
    /// Gets the invocation options.
    /// </summary>
    [field: NonSerialized]
    public InvokeMethodOptions Options { get; private set; }

    /// <inheritdoc/>
    public virtual int GetArgumentCount() => 0;

    /// <summary>
    /// Incorporates the provided invocation options.
    /// </summary>
    /// <param name="options">
    /// The options.
    /// </param>
    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    /// <inheritdoc/>
    public abstract object GetTarget();

    /// <inheritdoc/>
    public abstract void SetTarget(ITargetHolder holder);

    /// <inheritdoc/>
    public virtual object GetArgument(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "The request has zero arguments.");

    /// <inheritdoc/>
    public virtual void SetArgument(int index, object value) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "The request has zero arguments.");

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <inheritdoc/>
    public abstract string GetMethodName();

    /// <inheritdoc/>
    public abstract string GetInterfaceName();

    /// <inheritdoc/>
    public abstract string GetActivityName();

    /// <inheritdoc/>
    public abstract Type GetInterfaceType();

    /// <inheritdoc/>
    public abstract MethodInfo GetMethod();

    /// <inheritdoc/>
    public override string ToString() => IRequest.ToString(this);

    // Called upon creation in generated code by the creating grain reference by virtue of the [SelfInvokingReturnType(nameof(InitializeRequest))] attribute on this class.
    public DurableTask<TResult> InitializeRequest(GrainReference targetGrainReference)
    {
        // Capture the request context.
        Context = new()
        {
            // TaskId will be filled in later, before submission, via an extension method at the call site.
            TargetId = targetGrainReference.GrainId,
        };
        return this;
    }

    /// <inheritdoc/>
    public async ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken = default)
    {
        Debug.Assert(Context is not null);
        Context.Values = DurableTaskRequestContext.CaptureRequestContext(_shared.Serializer);

        if (DurableTaskRequest.TryGetRuntime(out var runtime))
        {
            return await runtime.ScheduleRemoteAsync(taskId, this, cancellationToken);
        }

        var targetGrain = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        return await targetGrain.ScheduleAsync(taskId, this, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        // Schedule this request with the remote service.
        // If the task has already been submitted then this will submit it again, which is an idempotent operation if:
        // * The task is semantically identical (same implementation and arguments).
        // * The task did not complete already and was subsequently cleaned up.
        // We can be sure that the task was not already cleaned up if we are calling from a grain which has a stable identifier, since
        // the caller must acknowledge completion before the task is eligible for garbage collection.
        // For the first point (identical implementation and arguments), we could store the task locally and verify it against its already-stored copy.
        // This check can also be performed remotely instead, since the remote host must have stored a copy of the request in order to be able to execute it.
        Debug.Assert(Context is not null);
        Context.Values = DurableTaskRequestContext.CaptureRequestContext(_shared.Serializer);
        if (DurableTaskRequest.TryGetRuntime(out var runtime))
        {
            using var durableCts = new CancellationTokenSource();
            using var durableDeactivationRegistration = executionContext.RegisterDeactivationCallback(
                static (cts, _) => cts.CancelAsync(),
                durableCts);
            using var durableRegistration = executionContext.RegisterCancellationCallback(
                static async (state, cancellationToken) =>
                {
                    await state.cts.CancelAsync();
                    await state.runtime.CancelRemoteAsync(state.taskId, state.target, cancellationToken);
                },
                state: (runtime, cts: durableCts, taskId: executionContext.TaskId, target: Context.TargetId));
            var durableResponse = await runtime.ScheduleRemoteAsync(executionContext.TaskId, this, durableCts.Token);
            return durableResponse.IsCompleted
                ? durableResponse
                : await runtime.GetScheduledTaskHandle(executionContext.TaskId).WaitAsync(durableCts.Token);
        }

        var callerContext = RuntimeContext.Current;
        if (callerContext is not null)
        {
            Context.CallerId = callerContext.GrainId;
        }

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        using var cts = new CancellationTokenSource();
        using var remoteDeactivationRegistration = executionContext.RegisterDeactivationCallback(
            static (source, _) => source.CancelAsync(),
            cts);
        using var registration = executionContext.RegisterCancellationCallback(
            static async (state, cancellationToken) =>
            {
                await state.cts.CancelAsync();
                await state.remote.CancelAsync(state.executionContext.TaskId, cancellationToken);
            },
            state: (remote, cts, executionContext));
        var response = await remote.ScheduleAsync(executionContext.TaskId, this, cts.Token);
        var options = new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromSeconds(5) };
        while (!response.IsCompleted && !cts.IsCancellationRequested)
        {
            response = await remote.SubscribeOrPollAsync(executionContext.TaskId, options, cts.Token);
        }

        return response;
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke() => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    //ValueTask<DurableTaskResponse> IDurableTaskRequest.InvokeImplementation(DurableExecutionContext executionContext) => DurableTaskRuntimeHelper.RunAsync(InvokeInner(), executionContext);
    DurableTask IDurableTaskRequest.CreateTask() => InvokeInner();

    // Generated. This invokes the target method directly.
    protected abstract DurableTask<TResult> InvokeInner();

    /// <inheritdoc/>
    public virtual TimeSpan? GetDefaultResponseTimeout() => null;

    public IScheduledTaskHandle GetHandle(TaskId taskId)
    {
        Debug.Assert(Context is not null);
        return new GrainScheduledTaskHandle(taskId, this, _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId), lastResponse: null);
    }
}

internal sealed class GrainScheduledTaskHandle(TaskId taskId, IDurableTaskRequest request, IDurableTaskServer grain, DurableTaskResponse? lastResponse) : IScheduledTaskHandle
{
    public TaskId TaskId { get; } = taskId;
    public DurableTaskResponse? LastResponse { get; private set; } = lastResponse;

    public async ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        // TODO: Add resilience via Polly
        await grain.CancelAsync(TaskId, cancellationToken);
    }

    public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
    {
        if (LastResponse is { IsCompleted: true } response)
        {
            return response;
        }

        // TODO: Add resilience via Polly
        var pollOptions = new SubscribeOrPollOptions { PollTimeout = options.PollTimeout };
        return LastResponse = await grain.SubscribeOrPollAsync(TaskId, pollOptions, cancellationToken);
    }

    public async ValueTask<DurableTaskResponse> ScheduleAsync(CancellationToken cancellationToken)
    {
        return await grain.ScheduleAsync(TaskId, request, cancellationToken);
    }

    public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken)
    {
        if (LastResponse is { IsCompleted: true } response)
        {
            return response;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // TODO: Add resilience via Polly

            var options = new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromSeconds(5) };
            response = LastResponse = await grain.SubscribeOrPollAsync(TaskId, options, cancellationToken);
            if (response.IsCompleted)
            {
                return response;
            }

            // TODO: Add exponential backoff via Polly/etc?
        }
    }
}
