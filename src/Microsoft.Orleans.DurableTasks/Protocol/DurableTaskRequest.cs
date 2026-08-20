#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.DurableTasks;
using Orleans.Invocation;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.TypeSystem;

namespace Orleans.DurableTasks.Protocol;

/// <summary>
/// Represents a durable task request.
/// </summary>
public interface IDurableTaskRequest : IRequest
{
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

    internal static string GetFingerprint(IDurableTaskRequest request, Serializer serializer)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(Encoding.UTF8.GetBytes(RuntimeTypeNameFormatter.Format(request.GetType())));

        var arguments = new object?[request.GetArgumentCount()];
        for (var index = 0; index < request.GetArgumentCount(); index++)
        {
            arguments[index] = request.GetArgument(index);
        }

        using var stream = new MemoryStream();
        using var session = serializer.SessionPool.GetSession();
        serializer.Serialize(arguments, stream, session);
        Append(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            hash.AppendData(length);
            hash.AppendData(value);
        }
    }
}

public sealed class DurableTaskRequestShared(IGrainContextAccessor grainContextAccessor, IGrainFactory grainFactory)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public IGrainFactory GrainFactory { get; } = grainFactory;
}

[GenerateSerializer]
[ReturnValueProxy(initializerMethodName: nameof(InitializeRequest))]
[Alias("DurableTaskRequest")]
[method: GeneratedActivatorConstructor]
public abstract class DurableTaskRequest(DurableTaskRequestShared shared) : DurableTask, IDurableTaskRequest, ISchedulableTask
{
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
    public virtual object GetArgument(int index) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

    /// <inheritdoc/>
    public virtual void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

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

        if (TryGetRuntime(_shared, out var runtime))
        {
            return await runtime.ScheduleRemoteAsync(taskId, this, cancellationToken);
        }

        Context.SupportsDurableCompletion = false;
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
        if (TryGetRuntime(_shared, out var runtime))
        {
            using var durableCts = new CancellationTokenSource();
            await using var durableRegistration = await executionContext.RegisterCancellationCallbackAsync(
                async cancellationToken =>
                {
                    await durableCts.CancelAsync();
                    await runtime.CancelRemoteAsync(executionContext.TaskId, Context.TargetId, cancellationToken);
                });
            var durableResponse = await runtime.ScheduleRemoteAsync(executionContext.TaskId, this, durableCts.Token);
            return durableResponse.IsCompleted
                ? durableResponse
                : await runtime.GetScheduledTaskHandle(executionContext.TaskId).WaitAsync(durableCts.Token);
        }

        Context.CallerId = default;
        Context.SupportsDurableCompletion = false;

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        using var cts = new CancellationTokenSource();
        await using var registration = await executionContext.RegisterCancellationCallbackAsync(
            async cancellationToken =>
            {
                await cts.CancelAsync();
                await remote.CancelAsync(executionContext.TaskId, cancellationToken);
            });
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

    internal static bool TryGetRuntime(
        DurableTaskRequestShared shared,
        [NotNullWhen(true)] out IDurableTaskGrainRuntime? runtime)
    {
        if (shared.GrainContextAccessor.GrainContext.GetComponent<IDurableTaskGrainRuntime>() is not { } localProxy)
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
    public virtual object GetArgument(int index) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

    /// <inheritdoc/>
    public virtual void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);

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

        if (DurableTaskRequest.TryGetRuntime(_shared, out var runtime))
        {
            return await runtime.ScheduleRemoteAsync(taskId, this, cancellationToken);
        }

        Context.SupportsDurableCompletion = false;
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
        if (DurableTaskRequest.TryGetRuntime(_shared, out var runtime))
        {
            using var durableCts = new CancellationTokenSource();
            await using var durableRegistration = await executionContext.RegisterCancellationCallbackAsync(
                async cancellationToken =>
                {
                    await durableCts.CancelAsync();
                    await runtime.CancelRemoteAsync(executionContext.TaskId, Context.TargetId, cancellationToken);
                });
            var durableResponse = await runtime.ScheduleRemoteAsync(executionContext.TaskId, this, durableCts.Token);
            return durableResponse.IsCompleted
                ? durableResponse
                : await runtime.GetScheduledTaskHandle(executionContext.TaskId).WaitAsync(durableCts.Token);
        }

        Context.CallerId = default;
        Context.SupportsDurableCompletion = false;

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        using var cts = new CancellationTokenSource();
        await using var registration = await executionContext.RegisterCancellationCallbackAsync(
            async cancellationToken =>
            {
                await cts.CancelAsync();
                await remote.CancelAsync(executionContext.TaskId, cancellationToken);
            });
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
