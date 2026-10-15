using System.Collections.Concurrent;
using Orleans.Concurrency;
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
        _pending = this.RunRequestAsync(_ =>
        {
            _log.Add("request");
            return Task.CompletedTask;
        });

        await Task.Delay(200);
        _log.Add("call-end");
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
            var request = QueueLoggedRequest($"request-{i}");
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
                return QueueLoggedRequest($"request-{i}");
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

    private Task QueueLoggedRequest(string entry) =>
        this.RunRequestAsync(static (state, _) =>
        {
            state.Grain._log.Add(state.Entry);
            return Task.CompletedTask;
        }, (Grain: this, Entry: entry));

    public Task StartRequestContextRequest(string value)
    {
        RequestContext.Set("run-request", value);
        _pending = this.RunRequestAsync(static _ => Task.FromResult(RequestContext.Get("run-request") as string));
        return Task.CompletedTask;
    }

    public Task StartFaultingRequest()
    {
        _pending = this.RunRequestAsync(static async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Request failed.");
        });

        return Task.CompletedTask;
    }

    public Task StartCanceledRequest(bool cancelBeforeStart)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeStart)
        {
            cancellation.Cancel();
        }

        _pending = this.RunRequestAsync(_ =>
        {
            _log.Add("canceled-request");
            return Task.CompletedTask;
        }, cancellation.Token);

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
}

public sealed class NonReentrantRunRequestGrain : RunRequestGrainBase, INonReentrantRunRequestGrain
{
}

[Reentrant]
public sealed class ReentrantRunRequestGrain : RunRequestGrainBase, IReentrantRunRequestGrain
{
}
