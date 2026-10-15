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
using Orleans.DurableTasks.Runtime;
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
    //ValueTask<DurableTaskResponse> InvokeImplementation(DurableTaskContext executionContext);
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

    ValueTask<DurableTaskResponse> ISchedulableTask.ScheduleAsync(TaskId taskId, CancellationToken cancellationToken) =>
        ScheduleRootAsync(_shared, this, taskId, cancellationToken);

    IScheduledTaskHandle ISchedulableTask.Post(TaskId taskId) => PostRoot(_shared, this, taskId);

    /// <summary>
    /// Schedules a root task from code which runs outside a durable execution, and completes once the target grain has
    /// accepted and durably stored the task.
    /// </summary>
    /// <remarks>
    /// The call goes directly to the target, like a client's. Code which runs on the target grain schedules through the
    /// grain's own runtime, so a non-reentrant grain doesn't call itself.
    /// </remarks>
    internal static ValueTask<DurableTaskResponse> ScheduleRootAsync(
        DurableTaskRequestShared shared,
        IDurableTaskRequest request,
        TaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        var context = request.Context!;
        PrepareClientContext(context);
        return GetServer(shared, context.TargetId).ScheduleAsync(taskId, request, cancellationToken);
    }

    /// <summary>
    /// Stages the start of a root task in the durable outbox of the grain which runs the calling code, and returns the
    /// task's handle.
    /// </summary>
    /// <remarks>The start commits with the grain's next journal write.</remarks>
    /// <exception cref="InvalidOperationException">The calling code doesn't run in a grain with durable tasks.</exception>
    internal static IScheduledTaskHandle PostRoot(DurableTaskRequestShared shared, IDurableTaskRequest request, TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        if (!TryGetRuntime(shared, out var runtime))
        {
            throw new InvalidOperationException(
                "Post stages a start in the durable outbox of the calling grain, and this caller has none. Use ScheduleAsync, which completes once the task's host has accepted the task.");
        }

        runtime.PostRoot(taskId, request);
        return GetHandle(shared, request, taskId);
    }

    /// <summary>Gets a handle for the root task <paramref name="taskId"/> of the request's target.</summary>
    internal static IScheduledTaskHandle GetHandle(DurableTaskRequestShared shared, IDurableTaskRequest request, TaskId taskId) =>
        new GrainScheduledTaskHandle(
            taskId,
            request.Context!.TargetId,
            request,
            GetServer(shared, request.Context.TargetId),
            lastResponse: null);

    /// <summary>
    /// Gets the server of <paramref name="target"/>'s tasks: the runtime of the grain which runs the calling code, when it's
    /// the target, or the target's grain extension.
    /// </summary>
    private static IDurableTaskServer GetServer(DurableTaskRequestShared shared, GrainId target) =>
        TryGetRuntime(shared, out var runtime)
        && runtime is IDurableTaskServer local
        && shared.GrainContextAccessor.GrainContext.GrainId == target
            ? local
            : shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(target);

    /// <inheritdoc/>
    protected override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext executionContext)
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
                async _ =>
                {
                    await durableCts.CancelAsync();
                    await runtime.CancelRemoteAsync(executionContext.Id, Context.TargetId, executionContext.CancellationReason, CancellationToken.None);
                });
            var durableResponse = await runtime.ScheduleRemoteAsync(executionContext.Id, this, durableCts.Token);
            return durableResponse.IsCompleted
                ? durableResponse
                : await runtime.GetScheduledTaskHandle(executionContext.Id).WaitAsync(durableCts.Token);
        }

        PrepareClientContext(Context);

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        using var cts = new CancellationTokenSource();
        await using var registration = await executionContext.RegisterCancellationCallbackAsync(
            async _ =>
            {
                await cts.CancelAsync();
                await GrainScheduledTaskHandle.RequestCancellationAsync(remote, executionContext.Id, executionContext.CancellationReason);
            });
        var response = await remote.ScheduleAsync(executionContext.Id, this, cts.Token);
        var options = new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromSeconds(5) };
        while (!response.IsCompleted && !cts.IsCancellationRequested)
        {
            response = await DurableTaskPolling.PollAsync(remote, executionContext.Id, options, cts.Token);
        }

        return response;
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke()
        // This could be made to work... maybe pick a random task id, for example.
        => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    //ValueTask<DurableTaskResponse> IDurableTaskRequest.InvokeImplementation(DurableTaskContext executionContext) => DurableTaskRuntimeHelper.RunAsync(InvokeInner(), executionContext);
    DurableTask IDurableTaskRequest.CreateTask() => InvokeInner();

    // Generated. This invokes the target method directly.
    protected abstract DurableTask InvokeInner();

    internal static bool TryGetRuntime(
        DurableTaskRequestShared shared,
        [NotNullWhen(true)] out IDurableTaskGrainRuntime? runtime)
    {
        // Outside a grain, a silo resolves the hosted client, whose components come from the silo's root services and
        // can't construct the activation-scoped runtime; that caller schedules like an external client.
        var context = shared.GrainContextAccessor.GrainContext;
        if (context.GrainId.IsClient() || context.GetComponent<IDurableTaskGrainRuntime>() is not { } localProxy)
        {
            runtime = null;
            return false;
        }

        runtime = localProxy;
        return true;
    }

    internal static void PrepareClientContext(DurableTaskRequestContext context)
    {
        context.CallerId = default;
        context.SupportsDurableCompletion = false;
    }

    /// <inheritdoc/>
    public virtual TimeSpan? GetDefaultResponseTimeout() => null;

    public IScheduledTaskHandle GetHandle(TaskId taskId) => DurableTaskRequest.GetHandle(_shared, this, taskId);
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
    public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken = default) =>
        DurableTaskRequest.ScheduleRootAsync(_shared, this, taskId, cancellationToken);

    IScheduledTaskHandle ISchedulableTask.Post(TaskId taskId) => DurableTaskRequest.PostRoot(_shared, this, taskId);

    /// <inheritdoc/>
    protected override async ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext executionContext)
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
                async _ =>
                {
                    await durableCts.CancelAsync();
                    await runtime.CancelRemoteAsync(executionContext.Id, Context.TargetId, executionContext.CancellationReason, CancellationToken.None);
                });
            var durableResponse = await runtime.ScheduleRemoteAsync(executionContext.Id, this, durableCts.Token);
            return durableResponse.IsCompleted
                ? durableResponse
                : await runtime.GetScheduledTaskHandle(executionContext.Id).WaitAsync(durableCts.Token);
        }

        DurableTaskRequest.PrepareClientContext(Context);

        var remote = _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(Context.TargetId);
        using var cts = new CancellationTokenSource();
        await using var registration = await executionContext.RegisterCancellationCallbackAsync(
            async _ =>
            {
                await cts.CancelAsync();
                await GrainScheduledTaskHandle.RequestCancellationAsync(remote, executionContext.Id, executionContext.CancellationReason);
            });
        var response = await remote.ScheduleAsync(executionContext.Id, this, cts.Token);
        var options = new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromSeconds(5) };
        while (!response.IsCompleted && !cts.IsCancellationRequested)
        {
            response = await DurableTaskPolling.PollAsync(remote, executionContext.Id, options, cts.Token);
        }

        return response;
    }

    /// <inheritdoc/>
    ValueTask<Response> IInvokable.Invoke() => throw new NotImplementedException("Durable task requests can not be invoked directly");

    /// <inheritdoc/>
    //ValueTask<DurableTaskResponse> IDurableTaskRequest.InvokeImplementation(DurableTaskContext executionContext) => DurableTaskRuntimeHelper.RunAsync(InvokeInner(), executionContext);
    DurableTask IDurableTaskRequest.CreateTask() => InvokeInner();

    // Generated. This invokes the target method directly.
    protected abstract DurableTask<TResult> InvokeInner();

    /// <inheritdoc/>
    public virtual TimeSpan? GetDefaultResponseTimeout() => null;

    public IScheduledTaskHandle GetHandle(TaskId taskId) => DurableTaskRequest.GetHandle(_shared, this, taskId);
}

/// <summary>The handle of a root task of <paramref name="target"/>, which code outside a durable execution observes.</summary>
internal sealed class GrainScheduledTaskHandle(
    TaskId taskId,
    GrainId target,
    IDurableTaskRequest? request,
    IDurableTaskServer grain,
    DurableTaskResponse? lastResponse) : IScheduledTaskHandle
{
    public TaskId TaskId { get; } = taskId;

    /// <summary>Returns the task's <see cref="DurableTaskAddress"/>, as <see cref="IDurableTaskReference"/>.</summary>
    public TFeature? GetFeature<TFeature>()
        where TFeature : class => new DurableTaskAddress(target, TaskId) as TFeature;
    public DurableTaskResponse? LastResponse { get; private set; } = lastResponse;

    public ValueTask CancelAsync(CancellationToken cancellationToken) => CancelAsync(reason: null, cancellationToken);

    public async ValueTask CancelAsync(string? reason, CancellationToken cancellationToken)
    {
        // TODO: Add resilience via Polly
        await RequestCancellationAsync(grain, TaskId, reason).AsTask().WaitAsync(cancellationToken);
    }

    /// <summary>Requests cancellation of <paramref name="taskId"/> on <paramref name="grain"/>, carrying <paramref name="reason"/>.</summary>
    /// <remarks>A request without a reason uses the original operation, which every version of the grain serves.</remarks>
    internal static ValueTask RequestCancellationAsync(IDurableTaskServer grain, TaskId taskId, string? reason) =>
        reason is null
            ? grain.CancelAsync(taskId, CancellationToken.None)
            : grain.CancelAsync(taskId, reason, CancellationToken.None);

    public async ValueTask AbortAsync(string reason, CancellationToken cancellationToken) =>
        await grain.AbortAsync(TaskId, reason, CancellationToken.None).AsTask().WaitAsync(cancellationToken);

    public async ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
    {
        if (LastResponse is { IsCompleted: true } response)
        {
            return response;
        }

        var pollOptions = new SubscribeOrPollOptions { PollTimeout = options.PollTimeout };
        return DecodeResponse(await DurableTaskPolling.PollAsync(grain, TaskId, pollOptions, cancellationToken));
    }

    public async ValueTask<DurableTaskResponse> ScheduleAsync(CancellationToken cancellationToken)
    {
        return await grain.ScheduleAsync(
            TaskId,
            request ?? throw new InvalidOperationException("An attached durable task cannot be scheduled."),
            cancellationToken);
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
            var options = new SubscribeOrPollOptions { PollTimeout = TimeSpan.FromSeconds(5) };
            response = DecodeResponse(await DurableTaskPolling.PollAsync(grain, TaskId, options, cancellationToken));
            if (response.IsCompleted)
            {
                return response;
            }
        }
    }

    private DurableTaskResponse DecodeResponse(DurableTaskResponse response)
    {
        return LastResponse = response;
    }
}
