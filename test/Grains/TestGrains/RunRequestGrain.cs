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

    public async Task<int> QueueRequests(int count)
    {
        var requests = new List<Task>(count);
        for (var i = 0; i < count; i++)
        {
            requests.Add(this.RunRequestAsync(static (state, _) => state.Grain._log.Add(state.Entry), (Grain: this, Entry: $"request-{i}"), new LocalRequestOptions { Interleave = true }));
        }

        // The requests interleave with this call, which waits for them so that no other call arrives while they are queued.
        await Task.WhenAll(requests);
        return _log.Count;
    }

    public Task StartCustomInvokableRequest(string value)
    {
        var request = new CustomRequest(value);
        ((IGrainBase)this).GrainContext.ReceiveLocalRequest(request, default);
        _pending = request.GetResultAsync();
        return Task.CompletedTask;
    }

    public Task<bool> ReceiveLocalRequestReturnsResultInstance() => ResultInstanceRequest.ReturnsResultInstanceAsync(this);

    public async Task<string> RunCustomRequestLifecycle()
    {
        // The request interleaves so that it runs while this call waits for it.
        var request = new CustomRequest("lifecycle");
        ((IGrainBase)this).GrainContext.ReceiveLocalRequest(request, new LocalRequestOptions { Interleave = true });
        var response = await request.Outcome.WaitAsync(TimeSpan.FromSeconds(10));
        await request.Disposed.WaitAsync(TimeSpan.FromSeconds(10));
        return $"{response.Result}:disposed-before-completion={request.DisposedBeforeCompletion}";
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
    /// Queues a custom request on the retained instance whose rejection queues a second request from within <see cref="ILocalRequest.Complete"/>.
    /// </summary>
    /// <returns>The number of times the first request completed and the second request's response.</returns>
    public static async Task<(int FirstCompletions, Response SecondResponse)> QueueChainedRequestsOnRetainedInstance(Guid key)
    {
        var context = ((IGrainBase)RetainedInstances[key]).GrainContext;
        var second = new CustomRequest("second");
        var first = new CustomRequest("first", _ => context.ReceiveLocalRequest(second, default));
        context.ReceiveLocalRequest(first, default);
        var secondResponse = await second.Outcome;
        await first.Outcome;
        return (first.Completions, secondResponse);
    }
}

/// <summary>
/// A hand-written request delivered through <see cref="IGrainContext.ReceiveLocalRequest"/>, which records its outcome.
/// </summary>
internal sealed class CustomRequest(string value, Action<Response>? onComplete = null) : Request<string?>, ILocalRequest
{
    public const string InterfaceName = "ICustomRequests";
    public const string MethodName = "Apply";
    private readonly TaskCompletionSource<Response> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RunRequestGrainBase? _grain;
    private int _completions;

    public string Value => value;

    public Task<Response> Outcome => _outcome.Task;

    public Task Disposed => _disposed.Task;

    public bool DisposedBeforeCompletion { get; private set; }

    public int Completions => Volatile.Read(ref _completions);

    public async Task<string?> GetResultAsync()
    {
        var response = await Outcome;
        return response.Exception is { } exception ? throw exception : (string?)response.Result;
    }

    public void Complete(Response response)
    {
        Interlocked.Increment(ref _completions);
        DisposedBeforeCompletion = _disposed.Task.IsCompleted;
        _outcome.TrySetResult(response);
        onComplete?.Invoke(response);
    }

    public override object GetTarget() => _grain!;

    public override void SetTarget(ITargetHolder holder) => _grain = (RunRequestGrainBase?)holder.GetTarget();

    protected override ValueTask<string?> InvokeInner() => new($"{value}:{_grain is not null}");

    public override void Dispose() => _disposed.TrySetResult();

    public override string GetMethodName() => MethodName;

    public override string GetInterfaceName() => InterfaceName;

    public override string GetActivityName() => $"{InterfaceName}/{MethodName}";

    public override Type GetInterfaceType() => typeof(IRunRequestGrain);

    public override MethodInfo GetMethod() => typeof(IRunRequestGrain).GetMethod(nameof(IRunRequestGrain.Ping))!;
}

/// <summary>
/// A local request which returns a mutable result instance and records the response it receives.
/// </summary>
internal sealed class ResultInstanceRequest(List<string> result) : Request<List<string>>, ILocalRequest
{
    private readonly TaskCompletionSource<Response> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private object? _grain;

    /// <summary>
    /// Returns whether a local request on <paramref name="grain"/> completes with the same result instance which it returned.
    /// </summary>
    public static async Task<bool> ReturnsResultInstanceAsync(IGrainBase grain)
    {
        var result = new List<string> { "mutable" };
        var request = new ResultInstanceRequest(result);

        // The request interleaves so that it runs while the calling request waits for it.
        grain.GrainContext.ReceiveLocalRequest(request, new LocalRequestOptions { Interleave = true });
        var response = await request._outcome.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return ReferenceEquals(result, response.Result);
    }

    public void Complete(Response response) => _outcome.TrySetResult(response);

    public override object GetTarget() => _grain!;

    public override void SetTarget(ITargetHolder holder) => _grain = holder.GetTarget();

    protected override ValueTask<List<string>> InvokeInner() => new(result);

    public override void Dispose() { }

    public override string GetMethodName() => "ReturnResult";

    public override string GetInterfaceName() => CustomRequest.InterfaceName;

    public override string GetActivityName() => $"{CustomRequest.InterfaceName}/ReturnResult";

    public override Type GetInterfaceType() => typeof(IRunRequestGrain);

    public override MethodInfo GetMethod() => typeof(IRunRequestGrain).GetMethod(nameof(IRunRequestGrain.Ping))!;
}

public sealed class UnfilteredRunRequestGrain : Grain, IUnfilteredRunRequestGrain
{
    public Task<bool> ReceiveLocalRequestReturnsResultInstance() => ResultInstanceRequest.ReturnsResultInstanceAsync(this);
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
    public const string RejectedValue = "rejected";

    public static bool CanInterleave(IInvokable request) => request switch
    {
        CustomRequest { Value: RejectedValue } => throw new InvalidOperationException("Predicate failed."),
        CustomRequest => true,
        _ => false,
    };

    public async Task<string> RejectRequestsWithThrowingCompletion()
    {
        // Both requests are queued in one turn, so the activation rejects them together when the predicate throws.
        var context = ((IGrainBase)this).GrainContext;
        var first = new CustomRequest(RejectedValue, _ => throw new InvalidOperationException("Completion failed."));
        var second = new CustomRequest(RejectedValue);
        context.ReceiveLocalRequest(first, default);
        context.ReceiveLocalRequest(second, default);

        var secondResponse = await second.Outcome.WaitAsync(TimeSpan.FromSeconds(10));
        var firstResponse = await first.Outcome.WaitAsync(TimeSpan.FromSeconds(10));
        return $"{firstResponse.Exception?.Message}:{first.Completions}|{secondResponse.Exception?.Message}:{second.Completions}";
    }

    public Task<string?> RunCustomRequestDuringBlockingRequest()
    {
        var request = new CustomRequest("custom");
        ((IGrainBase)this).GrainContext.ReceiveLocalRequest(request, default);
        return request.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
