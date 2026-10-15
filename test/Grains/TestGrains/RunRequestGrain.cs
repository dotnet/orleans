using System.Collections.Concurrent;
using System.Reflection;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public abstract class RunRequestGrainBase : Grain, IRunRequestGrain, IIncomingGrainCallFilter
{
    private static readonly ConcurrentDictionary<Guid, RunRequestGrainBase> RetainedInstances = new();
    private readonly List<string> _log = [];
    private readonly List<string> _filteredCalls = [];
    private TaskCompletionSource? _gate;
    private Task _pending = Task.CompletedTask;
    private RequestFilterMode _requestFilterMode;


    public async Task Invoke(IIncomingGrainCallContext context)
    {
        _filteredCalls.Add($"{context.InterfaceName}.{context.MethodName}");
        if (context.InterfaceName == "IGrainRequestInvoker")
        {
            switch (_requestFilterMode)
            {
                case RequestFilterMode.Throw:
                    throw new InvalidOperationException("Filter failed.");
                case RequestFilterMode.ThrowAfterInvoke:
                    await context.Invoke();
                    throw new InvalidOperationException("Filter failed after invoke.");
                case RequestFilterMode.ShortCircuit:
                    context.Result = "filtered";
                    return;
            }
        }

        await context.Invoke();
    }

    public Task SetRequestFilterMode(RequestFilterMode mode)
    {
        _requestFilterMode = mode;
        return Task.CompletedTask;
    }

    public async Task StartRequestDuringCall()
    {
        _log.Add("call-start");
        _pending = this.RunRequestAsync(_ => _log.Add("request"));

        await Task.Delay(200);
        _log.Add("call-end");
    }

    public async Task StartInterleavingRequestDuringCall()
    {
        _log.Add("call-start");
        _pending = this.RunRequestAsync(_ => _log.Add("request"), new LocalRequestOptions { Interleave = true });

        await Task.Delay(200);
        _log.Add("call-end");
    }


    public Task<int> QueueUnlimitedRequests(int count)
    {
        var admitted = new List<Task>(count);
        for (var i = 0; i < count; i++)
        {
            admitted.Add(QueueLoggedRequest($"request-{i}", default));
        }

        _pending = Task.WhenAll(admitted);
        return Task.FromResult(admitted.Count(request => !request.IsFaulted));
    }

    public Task StartCustomInvokableRequest(string value)
    {
        var completion = new ResponseCompletionSource<string?>();
        ((IGrainBase)this).GrainContext.ReceiveLocalRequest(new CustomRequest(value), completion, default);
        _pending = completion.AsValueTask().AsTask();
        return Task.CompletedTask;
    }

    public async Task<string?> RunCancellableCustomRequest()
    {
        using var source = new GrainCancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new ResponseCompletionSource<string?>();

        // The request interleaves so that it runs while this call waits for it.
        ((IGrainBase)this).GrainContext.ReceiveLocalRequest(new CancellableRequest(source.Token, started), completion, new LocalRequestOptions { Interleave = true });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await source.Cancel();
        return await completion.AsValueTask().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task StartBlockingRequest()
    {
        var gate = _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = this.RunRequestAsync(async _ =>
        {
            _log.Add("request-start");
            await gate.Task;
            _log.Add("request-end");
        });

        return Task.CompletedTask;
    }

    public Task ReleaseBlockingRequest()
    {
        _gate!.SetResult();
        return Task.CompletedTask;
    }

    public Task Ping()
    {
        _log.Add("ping");
        return Task.CompletedTask;
    }

    public Task StartValueRequest(int value)
    {
        _pending = this.RunRequestAsync(static (value, _) => Task.FromResult<string?>((value * 2).ToString()), value);
        return Task.CompletedTask;
    }

    public Task<List<string>> QueueRequests(int count)
    {
        var outcomes = new List<string>(count);
        var admitted = new List<Task>(count);
        for (var i = 0; i < count; i++)
        {
            var request = QueueLoggedRequest($"request-{i}", EnforceQueueLimit);
            if (request.IsFaulted)
            {
                outcomes.Add(request.Exception!.InnerException!.GetType().Name);
            }
            else
            {
                outcomes.Add("queued");
                admitted.Add(request);
            }
        }

        _pending = Task.WhenAll(admitted);
        return Task.FromResult(outcomes);
    }

    public async Task<int> QueueRequestsConcurrently(int count)
    {
        // Dedicated threads, released together, queue the requests while this call blocks the activation.
        using var barrier = new Barrier(count);
        var requests = await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait();
                return QueueLoggedRequest($"request-{i}", EnforceQueueLimit);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)));

        var admitted = requests.Where(request => !request.IsFaulted).ToList();
        if (requests.FirstOrDefault(request => request.IsFaulted && request.Exception!.InnerException is not LimitExceededException) is { } unexpected)
        {
            await unexpected;
        }

        _pending = Task.WhenAll(admitted);
        return admitted.Count;
    }

    private static readonly LocalRequestOptions EnforceQueueLimit = new() { EnforceQueueLimit = true };

    private Task QueueLoggedRequest(string entry, LocalRequestOptions options) =>
        this.RunRequestAsync(static (state, _) => state.Grain._log.Add(state.Entry), (Grain: this, Entry: entry), options);

    public Task StartRequestContextRequest(string value)
    {
        RequestContext.Set("run-request", value);
        _pending = this.RunRequestAsync(static _ => Task.FromResult(RequestContext.Get("run-request") as string));
        return Task.CompletedTask;
    }

    public Task StartFaultingRequest(bool synchronous)
    {
        _pending = synchronous
            ? this.RunRequestAsync(Fail)
            : this.RunRequestAsync(static async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Request failed.");
            });

        return Task.CompletedTask;
    }

    private static void Fail(CancellationToken cancellationToken) => throw new InvalidOperationException("Request failed.");

    public Task StartCanceledRequest(bool cancelBeforeStart)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeStart)
        {
            cancellation.Cancel();
        }

        _pending = this.RunRequestAsync(_ => _log.Add("canceled-request"), cancellation.Token);

        cancellation.Cancel();
        return Task.CompletedTask;
    }

    public async Task<string?> AwaitPendingRequest()
    {
        await _pending;
        return (_pending as Task<string?>)?.Result;
    }

    public Task<List<string>> GetLog() => Task.FromResult(_log.ToList());

    public Task<List<string>> GetFilteredCalls() => Task.FromResult(_filteredCalls.ToList());

    public Task RetainInstance()
    {
        RetainedInstances[this.GetPrimaryKey()] = this;
        return Task.CompletedTask;
    }

    public Task DeactivateAndRetainInstance()
    {
        RetainedInstances[this.GetPrimaryKey()] = this;
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues a request on the retained instance from outside of the activation.
    /// </summary>
    public static Task<string?> RunRequestOnRetainedInstance(Guid key) =>
        RetainedInstances[key].RunRequestAsync(
            static (grain, _) =>
            {
                grain._log.Add("outside");
                return Task.FromResult<string?>("ran");
            },
            RetainedInstances[key]);

    /// <summary>
    /// Queues a custom request on the retained instance whose rejection queues a second request from within the completion.
    /// </summary>
    /// <returns>The number of times the first request completed and the second request's response.</returns>
    public static async Task<(int FirstCompletions, Response SecondResponse)> QueueChainedRequestsOnRetainedInstance(Guid key)
    {
        var context = ((IGrainBase)RetainedInstances[key]).GrainContext;
        var second = new ChainingCompletion(context, next: null);
        var first = new ChainingCompletion(context, second);
        context.ReceiveLocalRequest(new CustomRequest("first"), first, default);
        var secondResponse = await second.Response;
        await first.Response;
        return (first.Completions, secondResponse);
    }
}

/// <summary>
/// A completion source which queues a request completed by <paramref name="next"/> each time it completes.
/// </summary>
internal sealed class ChainingCompletion(IGrainContext context, ChainingCompletion? next) : IResponseCompletionSource
{
    private readonly TaskCompletionSource<Response> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completions;

    public Task<Response> Response => _response.Task;

    public int Completions => Volatile.Read(ref _completions);

    public void Complete(Response value)
    {
        Interlocked.Increment(ref _completions);
        if (next is not null)
        {
            context.ReceiveLocalRequest(new CustomRequest("next"), next, default);
        }

        _response.TrySetResult(value);
    }

    public void Complete() => Complete(Orleans.Serialization.Invocation.Response.Completed);
}

/// <summary>
/// A hand-written request delivered through <see cref="IGrainContext.ReceiveLocalRequest"/>.
/// </summary>
internal sealed class CustomRequest(string value, TaskCompletionSource<string?>? invoked = null) : Request<string?>
{
    public const string InterfaceName = "ICustomRequests";
    public const string MethodName = "Apply";
    private RunRequestGrainBase? _grain;

    public override object GetTarget() => _grain!;

    public override void SetTarget(ITargetHolder holder) => _grain = (RunRequestGrainBase?)holder.GetTarget();

    protected override ValueTask<string?> InvokeInner()
    {
        var result = $"{value}:{_grain is not null}";
        invoked?.TrySetResult(result);
        return new(result);
    }

    public override void Dispose() { }

    public override string GetMethodName() => MethodName;

    public override string GetInterfaceName() => InterfaceName;

    public override string GetActivityName() => $"{InterfaceName}/{MethodName}";

    public override Type GetInterfaceType() => typeof(IRunRequestGrain);

    public override MethodInfo GetMethod() => typeof(IRunRequestGrain).GetMethod(nameof(IRunRequestGrain.Ping))!;
}

/// <summary>
/// A hand-written request which waits for its <see cref="GrainCancellationToken"/> argument to be canceled.
/// </summary>
internal sealed class CancellableRequest(GrainCancellationToken token, TaskCompletionSource started) : Request<string?>
{
    private GrainCancellationToken _token = token;
    private RunRequestGrainBase? _grain;

    public override object GetTarget() => _grain!;

    public override void SetTarget(ITargetHolder holder) => _grain = (RunRequestGrainBase?)holder.GetTarget();

    protected override async ValueTask<string?> InvokeInner()
    {
        started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, _token.CancellationToken);
            return "completed";
        }
        catch (OperationCanceledException)
        {
            return "canceled";
        }
    }

    public override int GetArgumentCount() => 1;

    public override object GetArgument(int index) => index == 0 ? _token : throw new ArgumentOutOfRangeException(nameof(index));

    public override void SetArgument(int index, object value) => _token = index == 0 ? (GrainCancellationToken)value : throw new ArgumentOutOfRangeException(nameof(index));

    public override void Dispose() { }

    public override string GetMethodName() => "WaitForCancellation";

    public override string GetInterfaceName() => CustomRequest.InterfaceName;

    public override string GetActivityName() => $"{CustomRequest.InterfaceName}/WaitForCancellation";

    public override Type GetInterfaceType() => typeof(IRunRequestGrain);

    public override MethodInfo GetMethod() => typeof(IRunRequestGrain).GetMethod(nameof(IRunRequestGrain.Ping))!;
}

public sealed class NonReentrantRunRequestGrain : RunRequestGrainBase, INonReentrantRunRequestGrain
{
}

[Reentrant]
public sealed class ReentrantRunRequestGrain : RunRequestGrainBase, IReentrantRunRequestGrain
{
}

[MayInterleave(nameof(CanInterleave))]
public sealed class MayInterleaveRunRequestGrain : RunRequestGrainBase, IMayInterleaveRunRequestGrain
{
    public static bool CanInterleave(IInvokable request) => request is CustomRequest;

    public async Task<string?> RunCustomRequestDuringBlockingRequest(bool observeCompletion)
    {
        var invoked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = observeCompletion ? new ResponseCompletionSource<string?>() : null;
        ((IGrainBase)this).GrainContext.ReceiveLocalRequest(new CustomRequest("custom", invoked), completion, default);

        var result = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return completion is null ? result : await completion.AsValueTask();
    }
}
