using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost.InMemoryTransport;
using UnitTests.SchedulerTests;
using Xunit;

namespace UnitTests.Runtime;

[Collection(ConsoleErrorCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
[TestCategory("Lifecycle")]
public class SiloStartupCleanupTests
{
    [Fact]
    public async Task FailureBeforeActive_StopsResourcesBeforeDisposingLoggingProvider()
    {
        var expected = new OrleansClusterConnectivityCheckFailedException("Controlled pre-Active connectivity failure.");
        await using var fixture = new Fixture(expected);
        fixture.Resource.BeforeActive = _ =>
        {
            Assert.True(fixture.Directory.Running);
            Assert.Equal(SystemStatus.Starting, fixture.Silo.SystemStatus);
            Assert.Equal(ServiceLifecycleStage.ApplicationServices, fixture.Lifecycle.HighestCompletedStage);
            return Task.CompletedTask;
        };

        var actual = await Assert.ThrowsAsync<OrleansClusterConnectivityCheckFailedException>(
            () => fixture.Host.RunAsync(TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.True(fixture.Resource.FailureStageEntered);
        Assert.False(fixture.Resource.ActiveStageEntered);
        Assert.Equal(1, fixture.Logs.DisposeCalls);
        Assert.Equal(new CleanupSnapshot(1, true, false, true), fixture.Logs.SnapshotAtDispose);
        Assert.Equal(1, fixture.Resource.FailureStageStopCalls);
        Assert.True(fixture.Lifecycle.LowestStoppedStage < ServiceLifecycleStage.RuntimeInitialize);
        Assert.Contains(fixture.Logs.Entries, entry => entry.EventId.Id == (int)ErrorCode.SiloStartError && ReferenceEquals(expected, entry.Exception));
        Assert.Contains(fixture.Logs.Entries, entry => entry.Message == "Startup resource worker stopped.");
        Assert.Equal(0, fixture.Logs.WritesAfterDispose);
    }

    [Fact]
    public async Task FailureBeforeRuntimeInitialize_RollsBackWithoutRequiringStartingState()
    {
        var expected = new InvalidOperationException("Controlled early startup failure.");
        await using var fixture = new Fixture();
        var earlyStageStops = 0;
        fixture.Lifecycle.Subscribe("EarlyFailure", ServiceLifecycleStage.RuntimeInitialize - 2, _ =>
        {
            Assert.Equal(SystemStatus.Created, fixture.Silo.SystemStatus);
            throw expected;
        }, _ =>
        {
            earlyStageStops++;
            return Task.CompletedTask;
        });

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Host.RunAsync(TestContext.Current.CancellationToken)));

        Assert.Equal(1, earlyStageStops);
        Assert.Equal(new CleanupSnapshot(0, true, false, true), fixture.Logs.SnapshotAtDispose);
        Assert.Equal(1, fixture.Logs.DisposeCalls);
        Assert.Equal(SystemStatus.Terminated, fixture.Silo.SystemStatus);
    }

    [Fact]
    public async Task Termination_WaitsForStagesBelowRuntimeInitialize()
    {
        var expected = new OrleansClusterConnectivityCheckFailedException("Controlled pre-Active failure.");
        await using var fixture = new Fixture(expected);
        var stopEntered = CompletionSource();
        var releaseStop = CompletionSource();
        fixture.Lifecycle.Subscribe("LastStopBarrier", ServiceLifecycleStage.RuntimeInitialize - 2,
            _ => Task.CompletedTask, async _ =>
            {
                stopEntered.TrySetResult();
                await releaseStop.Task;
            });
        var run = fixture.Host.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await stopEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.NotEqual(SystemStatus.Terminated, fixture.Silo.SystemStatus);
            Assert.False(fixture.Silo.SiloTerminated.IsCompleted);
            Assert.Equal(0, fixture.Logs.DisposeCalls);
            releaseStop.TrySetResult();
            Assert.Same(expected, await Assert.ThrowsAsync<OrleansClusterConnectivityCheckFailedException>(() => run));
            Assert.Equal(new CleanupSnapshot(1, true, false, true), fixture.Logs.SnapshotAtDispose);
            Assert.Equal(1, fixture.Logs.DisposeCalls);
        }
        finally
        {
            releaseStop.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyStart_RepeatedStopAndDispose_StopResourcesOnce(bool canceledStop)
    {
        await using var fixture = new Fixture();
        await fixture.Host.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Resource.ActiveStageEntered);
        Assert.True(fixture.Directory.Running);
        Assert.Equal(SystemStatus.Running, fixture.Silo.SystemStatus);
        Assert.False(fixture.Silo.SiloTerminated.IsCompleted);

        var stopToken = canceledStop ? new CancellationToken(canceled: true) : TestContext.Current.CancellationToken;
        var stop = fixture.Silo.StopAsync(stopToken);
        var repeatedStop = fixture.Silo.StopAsync(new CancellationToken(canceled: true));
        var dispose = fixture.Silo.DisposeAsync().AsTask();
        await Task.WhenAll(stop, repeatedStop, dispose);
        fixture.Silo.Dispose();

        Assert.Equal(1, fixture.Resource.StopCalls);
        Assert.True(fixture.Resource.Worker.IsCompletedSuccessfully);
        Assert.False(fixture.Directory.Running);
        Assert.True(fixture.Silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, fixture.Silo.SystemStatus);
        Assert.Equal(0, fixture.Logs.DisposeCalls);
        await ((IAsyncDisposable)fixture.Host).DisposeAsync();
        Assert.Equal(1, fixture.Logs.DisposeCalls);
        Assert.Equal(new CleanupSnapshot(1, true, false, true), fixture.Logs.SnapshotAtDispose);
        Assert.Equal(0, fixture.Logs.WritesAfterDispose);
    }

    [Fact]
    public async Task CanceledStartup_UsesIndependentCleanupLifetime()
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var fixture = new Fixture();
        fixture.Resource.BeforeActive = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var start = fixture.Host.StartAsync(startup.Token);
        await fixture.Resource.ValidateEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        startup.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        var cleanupToken = await fixture.Resource.StopEntered.Task;
        Assert.True(cleanupToken.CanBeCanceled);
        Assert.False(cleanupToken.IsCancellationRequested);
        Assert.Equal(1, fixture.Resource.StopCalls);
        Assert.True(fixture.Resource.Worker.IsCompletedSuccessfully);
        Assert.False(fixture.Directory.Running);
        Assert.True(fixture.Silo.SiloTerminated.IsCompletedSuccessfully);
        await fixture.Silo.StopAsync(new CancellationToken(canceled: true));
        await fixture.Silo.DisposeAsync();
        await ((IAsyncDisposable)fixture.Host).DisposeAsync();
        Assert.Equal(new CleanupSnapshot(1, true, false, true), fixture.Logs.SnapshotAtDispose);
        Assert.Equal(1, fixture.Logs.DisposeCalls);
    }

    [Fact]
    public async Task PreCanceledStartup_TerminatesWithoutStartingStages()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Silo.StartAsync(new CancellationToken(canceled: true)));

        Assert.False(fixture.Resource.FailureStageEntered);
        Assert.Equal(0, fixture.Resource.StopCalls);
        Assert.False(fixture.Directory.Running);
        Assert.True(fixture.Silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, fixture.Silo.SystemStatus);
        await fixture.Silo.StopAsync(new CancellationToken(canceled: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStartStopAndDispose_WaitForOwnedStartup(bool startupFails)
    {
        var expected = startupFails ? new OrleansClusterConnectivityCheckFailedException("Controlled startup race.") : null;
        await using var fixture = new Fixture(expected);
        var releaseStart = CompletionSource();
        if (startupFails)
        {
            fixture.Resource.BeforeActive = ct => releaseStart.Task.WaitAsync(ct);
        }
        else
        {
            fixture.Resource.AtActive = ct => releaseStart.Task.WaitAsync(ct);
        }

        var start = fixture.Silo.StartAsync(TestContext.Current.CancellationToken);
        var entered = startupFails ? fixture.Resource.ValidateEntered.Task : fixture.Resource.ActiveEntered.Task;
        await entered.WaitAsync(TestContext.Current.CancellationToken);
        var duplicateStart = fixture.Silo.StartAsync(TestContext.Current.CancellationToken);
        var stop = fixture.Silo.StopAsync(new CancellationToken(canceled: true));
        var dispose = fixture.Silo.DisposeAsync().AsTask();
        try
        {
            Assert.False(duplicateStart.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, fixture.Resource.StopCalls);
            releaseStart.TrySetResult();
            if (startupFails)
            {
                Assert.Same(expected, await Assert.ThrowsAsync<OrleansClusterConnectivityCheckFailedException>(() => start));
            }
            else
            {
                await start;
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => duplicateStart);
            await Task.WhenAll(stop, dispose);
            Assert.Equal(1, fixture.Resource.StopCalls);
            Assert.True(fixture.Silo.SiloTerminated.IsCompletedSuccessfully);
            Assert.False(fixture.Directory.Running);
            Assert.Equal(0, fixture.Logs.DisposeCalls);
        }
        finally
        {
            releaseStart.TrySetResult();
        }
    }

    [Fact]
    public async Task CleanupDeadline_CancelsGracefulWorkWithoutAbandoningCleanup()
    {
        var expected = new OrleansClusterConnectivityCheckFailedException("Controlled cleanup deadline failure.");
        await using var fixture = new Fixture(expected, builder =>
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero));
        var cancellationObserved = CompletionSource();
        var releaseCleanup = CompletionSource();
        fixture.Resource.AfterStop = async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
            }

            await releaseCleanup.Task;
        };
        var run = fixture.Host.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.False(fixture.Silo.SiloTerminated.IsCompleted);
            Assert.Equal(0, fixture.Logs.DisposeCalls);
            releaseCleanup.TrySetResult();
            Assert.Same(expected, await Assert.ThrowsAsync<OrleansClusterConnectivityCheckFailedException>(() => run));
            Assert.Equal(new CleanupSnapshot(1, true, false, true), fixture.Logs.SnapshotAtDispose);
            Assert.Equal(1, fixture.Logs.DisposeCalls);
            Assert.Contains(fixture.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("graceful shutdown timeout", StringComparison.Ordinal));
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }
    }

    [Fact]
    public async Task CleanupObserverFailure_PreservesStartupExceptionAndStopsLowerStages()
    {
        var expected = new OrleansClusterConnectivityCheckFailedException("Controlled startup failure.");
        var cleanupFailure = new InvalidOperationException("Controlled observer cleanup failure.");
        await using var fixture = new Fixture(expected);
        fixture.Resource.AfterStop = _ => Task.FromException(cleanupFailure);

        Assert.Same(expected, await Assert.ThrowsAsync<OrleansClusterConnectivityCheckFailedException>(
            () => fixture.Host.RunAsync(TestContext.Current.CancellationToken)));

        Assert.Equal(new CleanupSnapshot(1, true, false, true), fixture.Logs.SnapshotAtDispose);
        Assert.Contains(fixture.Logs.Entries, entry => ReferenceEquals(cleanupFailure, entry.Exception));
        Assert.True(fixture.Lifecycle.LowestStoppedStage < ServiceLifecycleStage.RuntimeInitialize);
    }

    [Fact]
    public async Task CleanupTraversalFailure_FaultsTerminationAndReportsBothFailures()
    {
        var expected = new OrleansClusterConnectivityCheckFailedException("Controlled startup failure.");
        var cleanupFailure = new InvalidOperationException("Controlled lifecycle traversal failure.");
        await using var fixture = new Fixture(expected, builder =>
            builder.Services.AddSingleton<SiloLifecycleSubject>(services =>
                new FailingStopLifecycle(services.GetRequiredService<ILogger<SiloLifecycleSubject>>(), cleanupFailure)));

        Assert.Same(expected, await Assert.ThrowsAsync<OrleansClusterConnectivityCheckFailedException>(
            () => fixture.Host.RunAsync(TestContext.Current.CancellationToken)));

        Assert.Equal(new CleanupSnapshot(1, true, false, false), fixture.Logs.SnapshotAtDispose);
        Assert.NotEqual(SystemStatus.Terminated, fixture.Silo.SystemStatus);
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Silo.SiloTerminated));
        Assert.Same(cleanupFailure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Silo.StopAsync(new CancellationToken(canceled: true))));
        var diagnostic = Assert.Single(fixture.Logs.Entries, entry => entry.Message == "Error cleaning up a failed silo startup.");
        var errors = Assert.IsType<AggregateException>(diagnostic.Exception).InnerExceptions;
        Assert.Equal(new Exception[] { expected, cleanupFailure }, errors);
    }

    [Theory]
    [InlineData((int)ErrorCode.SiloStartError)]
    [InlineData((int)ErrorCode.SiloShuttingDown)]
    [InlineData((int)ErrorCode.SiloShutDown)]
    public async Task LoggingFailure_DoesNotMaskStartupOrSkipCleanup(int failingEvent)
    {
        var expected = new OrleansClusterConnectivityCheckFailedException("Controlled startup failure.");
        await using var fixture = new Fixture(expected);
        fixture.Logs.Failure = entry => entry.EventId.Id == failingEvent
            ? new ObjectDisposedException("Controlled EventLog provider")
            : null;
        using var emergencyOutput = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(emergencyOutput);
        try
        {
            Assert.Same(expected, await Assert.ThrowsAsync<OrleansClusterConnectivityCheckFailedException>(
                () => fixture.Host.RunAsync(TestContext.Current.CancellationToken)));
            Assert.Contains("Controlled EventLog provider", emergencyOutput.ToString(), StringComparison.Ordinal);
            Assert.Equal(new CleanupSnapshot(1, true, false, true), fixture.Logs.SnapshotAtDispose);
            Assert.Equal(1, fixture.Logs.DisposeCalls);
            Assert.Contains(fixture.Logs.Entries, entry => entry.EventId.Id == (int)ErrorCode.SiloStartError && ReferenceEquals(expected, entry.Exception));
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static TaskCompletionSource CompletionSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IAsyncDisposable
    {
        public StartupResource Resource { get; }
        public RecordingProvider Logs { get; } = new();
        public IHost Host { get; }
        public Silo Silo { get; }
        public LocalGrainDirectory Directory { get; }
        public ISiloLifecycle Lifecycle { get; }

        public Fixture(Exception? startupFailure = null, Action<ISiloBuilder>? configure = null)
        {
            Resource = new StartupResource(startupFailure);
            Host = new HostBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.Services.AddSingleton<ILoggerProvider>(_ => Logs);
                })
                .UseOrleans(builder =>
                {
                    builder.UseLocalhostClustering();
                    builder.UseInMemoryTransport(new InMemoryTransportConnectionHub());
                    builder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(services =>
                    {
                        Resource.SetLogger(services.GetRequiredService<ILogger<StartupResource>>());
                        return Resource;
                    });
                    configure?.Invoke(builder);
                })
                .Build();
            Silo = Host.Services.GetRequiredService<Silo>();
            Directory = Host.Services.GetRequiredService<LocalGrainDirectory>();
            Lifecycle = Host.Services.GetRequiredService<ISiloLifecycle>();
            Logs.OnDispose = () => new CleanupSnapshot(Resource.StopCalls, Resource.Worker.IsCompletedSuccessfully, Directory.Running, Silo.SiloTerminated.IsCompletedSuccessfully);
        }

        public async ValueTask DisposeAsync()
        {
            // Keep a failing regression from leaking the unpatched runtime's lifecycle-owned work.
            if (Directory.Running)
            {
                await Directory.StopAsync();
            }

            await Resource.CleanupAsync();
            await ((IAsyncDisposable)Host).DisposeAsync();
            Resource.DisposeCancellation();
        }
    }

    private sealed class StartupResource(Exception? startupFailure) : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly CancellationTokenSource _stopping = new();
        private ILogger? _logger;

        public Task Worker { get; private set; } = Task.CompletedTask;
        public int StopCalls { get; private set; }
        public int FailureStageStopCalls { get; private set; }
        public bool FailureStageEntered { get; private set; }
        public bool ActiveStageEntered { get; private set; }
        public TaskCompletionSource ValidateEntered { get; } = CompletionSource();
        public TaskCompletionSource ActiveEntered { get; } = CompletionSource();
        public TaskCompletionSource<CancellationToken> StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task> BeforeActive { get; set; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> AtActive { get; set; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> AfterStop { get; set; } = _ => Task.CompletedTask;

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<StartupResource>(ServiceLifecycleStage.RuntimeServices, OnStart, OnStop);
            lifecycle.Subscribe("ControlledConnectivityFailure", ServiceLifecycleStage.ValidateInitialConnectivity, async ct =>
            {
                FailureStageEntered = true;
                ValidateEntered.TrySetResult();
                await BeforeActive(ct);
                if (startupFailure is { } failure)
                {
                    throw failure;
                }
            }, _ =>
            {
                FailureStageStopCalls++;
                return Task.CompletedTask;
            });
            lifecycle.Subscribe("ActiveStageProbe", ServiceLifecycleStage.Active, async ct =>
            {
                ActiveStageEntered = true;
                ActiveEntered.TrySetResult();
                await AtActive(ct);
            });
        }

        public void SetLogger(ILogger logger) => _logger = logger;

        private Task OnStart(CancellationToken cancellationToken)
        {
            Worker = RunWorkerAsync();
            return Task.CompletedTask;
        }

        private async Task RunWorkerAsync()
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }

            _logger!.LogInformation("Startup resource worker stopped.");
        }

        private async Task OnStop(CancellationToken cancellationToken)
        {
            StopCalls++;
            StopEntered.TrySetResult(cancellationToken);
            await CleanupAsync();
            await AfterStop(cancellationToken);
        }

        public async Task CleanupAsync()
        {
            await _stopping.CancelAsync();
            await Worker;
        }

        public void DisposeCancellation() => _stopping.Dispose();
    }

    private sealed class FailingStopLifecycle(ILogger<SiloLifecycleSubject> logger, Exception failure) : SiloLifecycleSubject(logger)
    {
        public override async Task OnStop(CancellationToken cancellationToken = default)
        {
            await base.OnStop(cancellationToken);
            throw failure;
        }
    }

    private sealed record CleanupSnapshot(int StopCalls, bool WorkerCompleted, bool DirectoryRunning, bool SiloTerminated);
    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception? Exception, string Message);

    private sealed class RecordingProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public Func<CleanupSnapshot>? OnDispose { get; set; }
        public Func<LogEntry, Exception?>? Failure { get; set; }
        public CleanupSnapshot? SnapshotAtDispose { get; private set; }
        public int DisposeCalls;
        public int WritesAfterDispose;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
            SnapshotAtDispose = OnDispose?.Invoke();
            Interlocked.Increment(ref DisposeCalls);
        }

        private sealed class RecordingLogger(RecordingProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // Record late writes instead of throwing an unhandled exception on a worker thread.
                if (Volatile.Read(ref provider.DisposeCalls) != 0)
                {
                    Interlocked.Increment(ref provider.WritesAfterDispose);
                }

                var entry = new LogEntry(logLevel, eventId, exception, formatter(state, exception));
                provider.Entries.Enqueue(entry);
                if (provider.Failure?.Invoke(entry) is { } failure)
                {
                    throw failure;
                }
            }
        }
    }
}
