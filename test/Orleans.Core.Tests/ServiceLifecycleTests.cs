using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

#nullable enable

namespace NonSilo.Tests;

[TestCategory("BVT"), TestCategory("Lifecycle")]
public class ServiceLifecycleTests
{
    private readonly ServiceLifecycle<ISiloLifecycle> _lifecycle;
    private readonly CancelableSiloLifecycleSubject _subject;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public ServiceLifecycleTests(ITestOutputHelper output)
    {
        var factory = new LoggerFactory([new XunitLoggerProvider(output)]);

        _subject = new CancelableSiloLifecycleSubject(factory.CreateLogger<SiloLifecycleSubject>());
        _lifecycle = new ServiceLifecycle<ISiloLifecycle>(factory.CreateLogger<ServiceLifecycle<ISiloLifecycle>>());

        _lifecycle.Participate(_subject);
    }

    private static (Task<object?> Task, IDisposable Registration) RegisterCallback(
        IServiceLifecycleStage stage,
        Action<object?, CancellationToken>? action = null,
        object? state = null,
        bool terminateOnError = true)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    
        var registration = stage.Register((s, ct) =>
        {
            try
            {
                action?.Invoke(s, ct);
                tcs.TrySetResult(s);
            }
            catch (Exception ex)
            {
                // We set the exception on the TCS so the test can inspect the specific failure of this callback.
                tcs.TrySetException(ex);
    
                // We rethrow so the LifecycleSubject behaves according to TerminateOnError.
                throw;
            }
            return Task.CompletedTask;
        }, state, terminateOnError);
    
        return (tcs.Task, registration);
    }

    [Fact]
    public async Task BasicCallbackExecution()
    {
        var callbackState = "test-state";

        var (task, _) = RegisterCallback(_lifecycle.Started, (state, ct) => { }, callbackState);

        await _subject.OnStart();

        var result = await task.WaitAsync(Timeout);
        Assert.Equal(callbackState, result);
    }

    [Fact]
    public async Task Stage_WaitAsync()
    {
        var waitTask = _lifecycle.Started.WaitAsync();

        Assert.False(waitTask.IsCompleted);

        await _subject.OnStart();
        await waitTask.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Stage_WaitAsync_Cancellation()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource();

        // We register a blocking callback to keep the stage in a "running" state.
        // This forces WaitAsync to actually block, allowing us to verify that cancelling
        // the token interrupts the wait as expected.

        _lifecycle.Started.Register((_, _) => tcs.Task);

        var startTask = _subject.OnStart();
        var waitTask = _lifecycle.Started.WaitAsync(cts.Token);

        Assert.False(waitTask.IsCompleted, "WaitAsync should be paused waiting for the stage to complete");

        await cts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask);

        tcs.SetResult();

        await startTask;
    }

    [Fact]
    public async Task Stage_NotifyCompleted_IsIdempotent()
    {
        var stage = new ServiceLifecycleNotificationStage(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "Started");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;

        stage.Register(async (_, _) =>
        {
            Interlocked.Increment(ref executionCount);
            await gate.Task;
        }, state: null, terminateOnError: true);

        var first = stage.NotifyCompleted(CancellationToken.None);
        var second = stage.NotifyCompleted(CancellationToken.None);

        Assert.False(second.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        await stage.NotifyCompleted(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task CallbackDisposal_PreventsExecution()
    {
        var (task, registration) = RegisterCallback(_lifecycle.Stopping);

        registration.Dispose();

        await _subject.OnStart();
        await _subject.OnStop();

        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task CancellationToken_TriggeredOnStageCompletion()
    {
        var tcs = new TaskCompletionSource();

        using var registration = _lifecycle.Stopping.Token.Register(tcs.SetResult);

        await _subject.OnStart();
        await _subject.OnStop();

        await tcs.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ErrorHandling_TerminateOnErrorFalse()
    {
        var (task, _) = RegisterCallback(
        _lifecycle.Started,
        (state, ct) => throw new InvalidOperationException("Test"),
        terminateOnError: false);

        await _subject.OnStart();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(Timeout));
        Assert.Equal("Test", ex.Message);
    }

    [Fact]
    public async Task ErrorHandling_TerminateOnErrorTrue()
    {
        var (task, _) = RegisterCallback(
            _lifecycle.Started,
            (state, ct) => throw new InvalidOperationException("Test"),
            terminateOnError: true);

        var startTask = _subject.OnStart();

        // This ensures the callback actually executed and we aren't just catching the lifecycle aborting.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(Timeout));
        Assert.Equal("Test", ex.Message);

        // Now verify the lifecycle start failed as expected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => startTask);
    }

    [Fact]
    public async Task ErrorHandling_TerminateOnErrorTrue_MultipleFailures()
    {
        var (task1, _) = RegisterCallback(
            _lifecycle.Started,
            (_, _) => throw new InvalidOperationException("first"),
            terminateOnError: true);

        var (task2, _) = RegisterCallback(
            _lifecycle.Started,
            (_, _) => throw new ArgumentException("second"),
            terminateOnError: true);

        var startTask = _subject.OnStart();

        // We swallow the start exception initially so we can inspect the individual tasks.
        try
        {
            await startTask;
        }
        catch
        {
            // Ignore
        }

        // Now we wait for both TCS signals to complete (rather 'fail') before asserting.
        // This prevents racing between the OnStart exception propagation and the TCS setting.

        try { await task1.WaitAsync(Timeout); } catch { }
        try { await task2.WaitAsync(Timeout); } catch { }

        await Assert.ThrowsAsync<InvalidOperationException>(() => task1);
        await Assert.ThrowsAsync<ArgumentException>(() => task2);
    }

    [Fact]
    public async Task LateRegistration_ExecutedImmediately()
    {
        await _subject.OnStart();
        await _subject.OnStop();

        // Registering after stage completes should run immediately
        var (task, _) = RegisterCallback(_lifecycle.Stopping);

        await task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConcurrentCallbacks_RegistrationSafe()
    {
        const int Count = 50;

        var startSignal = new ManualResetEventSlim(false);
        var tasks = new Task[Count];
        var executionCount = 0;

        for (var i = 0; i < Count; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                startSignal.Wait();
                RegisterCallback(_lifecycle.Started, (_, _) => Interlocked.Increment(ref executionCount));
            });
        }

        startSignal.Set();

        await Task.WhenAll(tasks);
        await _subject.OnStart();

        Assert.Equal(Count, executionCount);
    }

    [Fact]
    public async Task MultipleStages_ExecuteInOrder()
    {
        var executionOrder = new ConcurrentQueue<string>();

        RegisterCallback(_lifecycle.Started, (_, _) => executionOrder.Enqueue("Started"));
        RegisterCallback(_lifecycle.Stopping, (_, _) => executionOrder.Enqueue("Stopping"));

        // We capture the stopped task to wait on it specifically.
        var (stoppedTask, _) = RegisterCallback(_lifecycle.Stopped, (_, _) => executionOrder.Enqueue("Stopped"));

        await _subject.OnStart();
        await _subject.OnStop();
        await stoppedTask.WaitAsync(Timeout);

        var order = executionOrder.ToArray();

        Assert.Equal(3, order.Length);
        Assert.Equal("Started", order[0]);
        Assert.Equal("Stopping", order[1]);
        Assert.Equal("Stopped", order[2]);
    }

    [Fact]
    public async Task BackgroundWorker_StopsOnCancellation()
    {
        var workerExited = new TaskCompletionSource();
        var token = _lifecycle.Stopping.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                workerExited.SetResult();
            }
        });

        await _subject.OnStart();
        await _subject.OnStop();

        await workerExited.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Lifecycle_CancellationToken_PassedToCallback()
    {
        var tcs = new TaskCompletionSource<bool>();

        // We manually register here because the logic is specific to CT handling inside the callback
        // and returns a Task result different from the standard flow.
        _lifecycle.Started.Register(async (state, ct) =>
        {
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                tcs.SetResult(true);
            }
        });

        var startTask = _subject.OnStart();

        await _subject.CancelStartAsync();

        try
        {
            await startTask;
        }
        catch (OperationCanceledException)
        {

        }

        var tokenWasCancelled = await tcs.Task.WaitAsync(Timeout);
        Assert.True(tokenWasCancelled);
    }

    /// <summary>
    /// A simple cancelable version of the real subject to test for cancellations.
    /// </summary>
    public class CancelableSiloLifecycleSubject(ILogger<SiloLifecycleSubject> logger) : SiloLifecycleSubject(logger)
    {
        private readonly CancellationTokenSource _cts = new();

        public override Task OnStart(CancellationToken cancellationToken = default)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            return base.OnStart(linkedCts.Token);
        }

        public Task CancelStartAsync()
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }
    }
}

