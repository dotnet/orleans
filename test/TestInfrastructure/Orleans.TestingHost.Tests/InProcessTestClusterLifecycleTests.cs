using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost.Logging;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("TestingHost")]
[TestCategory("Functional")]
public sealed class InProcessTestClusterLifecycleTests
{
    private const string DiagnosticCategory = "Orleans.TestingHost.Tests.LifecycleFailureProbe";
    private const int ConcurrentLogReaderCount = 4;
    private const int ConcurrentLogReadsPerWorker = 100;
    private const int ConcurrentLogWriterCount = 8;
    private const int ConcurrentLogWritesPerWorker = 2_500;
    private static readonly EventId SiloStartupFailureEvent = new(7101, "SiloStartupFailed");
    private static readonly EventId ClientStartupFailureEvent = new(7102, "ClientStartupFailed");
    private static readonly EventId SiloStopFailureEvent = new(7103, "SiloStopFailed");

    [Fact]
    public async Task Log_RemainsConsistentDuringConcurrentWritesAndReads()
    {
        await using var cluster = new InProcessTestCluster(new InProcessTestClusterOptions(), new FixedPortAllocator());
        var start = CreateCompletionSource();
        var writers = Enumerable.Range(0, ConcurrentLogWriterCount).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < ConcurrentLogWritesPerWorker; i++)
            {
                Assert.Empty(cluster.GetActiveSilos());
            }
        }));
        var readers = Enumerable.Range(0, ConcurrentLogReaderCount).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < ConcurrentLogReadsPerWorker; i++)
            {
                cluster.GetLog();
                await Task.Yield();
            }
        }));

        start.TrySetResult();
        await Task.WhenAll(writers.Concat(readers));

        var entries = cluster.GetLog().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ConcurrentLogWriterCount * ConcurrentLogWritesPerWorker, entries.Length);
        Assert.All(entries, entry => Assert.StartsWith("GetActiveSilos: 0 Silos=", entry, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployAsync_WhenOneSiloStartupFails_DisposesEveryBuiltHostAndReportsRootFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expectedFailure = new InvalidOperationException("Controlled Silo_1 startup failure.");
        var coordinator = new SiloStartupCoordinator(expectedStarts: 2);
        var startedSilo = new LifecycleControl("Silo_0", coordinator);
        var failedSilo = new LifecycleControl("Silo_1", coordinator)
        {
            StartFailure = expectedFailure
        };
        var startedSiloDisposal = new DisposalTracker();
        var failedSiloDisposal = new DisposalTracker();
        var logBuffer = new InMemoryLogBuffer();
        var allocator = new FixedPortAllocator();
        var builder = CreateBuilder(initialSilosCount: 2, initializeClientOnDeploy: false);
        builder.ConfigureSiloHost((options, hostBuilder) =>
        {
            if (options.SiloName == startedSilo.Name)
            {
                AddControlledLifecycle(hostBuilder.Services, startedSilo, startedSiloDisposal, logBuffer);
            }
            else
            {
                Assert.Equal(failedSilo.Name, options.SiloName);
                AddControlledLifecycle(hostBuilder.Services, failedSilo, failedSiloDisposal, logBuffer);
            }
        });

        var cluster = new InProcessTestCluster(builder.Options, allocator);
        try
        {
            var deployment = cluster.DeployAsync(cancellationToken);
            await coordinator.BothStartsObserved.Task.WaitAsync(cancellationToken);
            await coordinator.SuccessfulSiloStarted.Task.WaitAsync(cancellationToken);

            coordinator.ReleaseFailure.TrySetResult();
            await startedSilo.StopEntered.Task.WaitAsync(cancellationToken);
            var startedHandle = Assert.Single(cluster.Silos);
            startedSilo.ReleaseStop.TrySetResult();

            var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => deployment);

            Assert.Same(expectedFailure, actualFailure);
            Assert.Equal("Controlled Silo_1 startup failure.", actualFailure.Message);
            Assert.False(startedHandle.IsActive);
            Assert.Empty(cluster.Silos);
            AssertClientUnavailable(cluster);
            Assert.Equal(1, startedSiloDisposal.DisposeCount);
            Assert.Equal(1, failedSiloDisposal.DisposeCount);
            Assert.Equal([1, 1], allocator.AllocationRequests.Order());
            Assert.Equal(0, allocator.DisposeCount);
            AssertProviderDisposed(startedHandle.ServiceProvider);
            AssertProviderDisposed(failedSilo.ServiceProvider);

            var diagnostic = Assert.Single(
                logBuffer.AllEntries,
                entry => entry.Category == DiagnosticCategory && entry.EventId == SiloStartupFailureEvent);
            Assert.Equal(LogLevel.Error, diagnostic.LogLevel);
            Assert.Equal("Silo Silo_1 startup failed by test control.", diagnostic.Message);
            Assert.Same(expectedFailure, diagnostic.Exception);
            Assert.DoesNotContain("Done initializing cluster", cluster.GetLog(), StringComparison.Ordinal);

            await cluster.DisposeAsync();
            await cluster.DisposeAsync();

            Assert.Equal(1, startedSiloDisposal.DisposeCount);
            Assert.Equal(1, failedSiloDisposal.DisposeCount);
            Assert.Equal(1, allocator.DisposeCount);
            Assert.Same(expectedFailure, diagnostic.Exception);
        }
        finally
        {
            coordinator.ReleaseFailure.TrySetResult();
            startedSilo.ReleaseStop.TrySetResult();
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeployAsync_WhenClientStartupFails_DisposesClientAndSilosAndRetainsDiagnostics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expectedFailure = new InvalidOperationException("Controlled cluster client startup failure.");
        var silo = new LifecycleControl("Silo_0");
        var client = new LifecycleControl("TestClusterClient")
        {
            RequiredPredecessor = silo.Started.Task,
            StartFailure = expectedFailure
        };
        var siloDisposal = new DisposalTracker();
        var clientDisposal = new DisposalTracker();
        var logBuffer = new InMemoryLogBuffer();
        var allocator = new FixedPortAllocator();
        var builder = CreateBuilder(initialSilosCount: 1, initializeClientOnDeploy: true);
        builder.ConfigureSiloHost((options, hostBuilder) =>
        {
            Assert.Equal(silo.Name, options.SiloName);
            AddControlledLifecycle(hostBuilder.Services, silo, siloDisposal, logBuffer);
        });
        builder.ConfigureClientHost(hostBuilder =>
            AddControlledLifecycle(hostBuilder.Services, client, clientDisposal, logBuffer));

        var cluster = new InProcessTestCluster(builder.Options, allocator);
        try
        {
            var deployment = cluster.DeployAsync(cancellationToken);
            await silo.Started.Task.WaitAsync(cancellationToken);
            await client.StartEntered.Task.WaitAsync(cancellationToken);
            Assert.True(silo.Started.Task.IsCompletedSuccessfully);

            client.ReleaseStart.TrySetResult();
            await silo.StopEntered.Task.WaitAsync(cancellationToken);
            var siloHandle = Assert.Single(cluster.Silos);
            silo.ReleaseStop.TrySetResult();

            var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => deployment);

            Assert.Same(expectedFailure, actualFailure);
            Assert.Equal("Controlled cluster client startup failure.", actualFailure.Message);
            Assert.False(siloHandle.IsActive);
            Assert.Empty(cluster.Silos);
            Assert.Null(cluster.ClientHost);
            AssertClientUnavailable(cluster);
            Assert.Equal(1, siloDisposal.DisposeCount);
            Assert.Equal(1, clientDisposal.DisposeCount);
            Assert.Equal([1], allocator.AllocationRequests);
            Assert.Equal(0, allocator.DisposeCount);
            AssertProviderDisposed(siloHandle.ServiceProvider);
            AssertProviderDisposed(client.ServiceProvider);

            var clusterLog = cluster.GetLog();
            var doneIndex = clusterLog.IndexOf("Done initializing cluster", StringComparison.Ordinal);
            var clientIndex = clusterLog.IndexOf("Initializing Cluster Client", StringComparison.Ordinal);
            Assert.True(doneIndex >= 0);
            Assert.True(clientIndex > doneIndex);

            var diagnostic = Assert.Single(
                logBuffer.AllEntries,
                entry => entry.Category == DiagnosticCategory && entry.EventId == ClientStartupFailureEvent);
            Assert.Equal(LogLevel.Error, diagnostic.LogLevel);
            Assert.Equal("Client TestClusterClient startup failed by test control.", diagnostic.Message);
            Assert.Same(expectedFailure, diagnostic.Exception);

            await cluster.DisposeAsync();
            await cluster.DisposeAsync();

            Assert.Equal(1, siloDisposal.DisposeCount);
            Assert.Equal(1, clientDisposal.DisposeCount);
            Assert.Equal(1, allocator.DisposeCount);
            Assert.Same(expectedFailure, diagnostic.Exception);
            Assert.Contains(diagnostic, logBuffer.AllEntries);
        }
        finally
        {
            client.ReleaseStart.TrySetResult();
            silo.ReleaseStop.TrySetResult();
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopSiloAsync_WhenHostStopFails_RemovesAndDisposesHandle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expectedFailure = new InvalidOperationException("Controlled Silo_0 stop failure.");
        var silo = new LifecycleControl("Silo_0")
        {
            StopFailure = expectedFailure
        };
        var siloDisposal = new DisposalTracker();
        var logBuffer = new InMemoryLogBuffer();
        var allocator = new FixedPortAllocator();
        var builder = CreateBuilder(initialSilosCount: 1, initializeClientOnDeploy: false);
        builder.ConfigureSiloHost((options, hostBuilder) =>
        {
            Assert.Equal(silo.Name, options.SiloName);
            AddControlledLifecycle(hostBuilder.Services, silo, siloDisposal, logBuffer);
        });

        var cluster = new InProcessTestCluster(builder.Options, allocator);
        try
        {
            await cluster.DeployAsync(cancellationToken);
            await silo.Started.Task.WaitAsync(cancellationToken);
            var handle = Assert.Single(cluster.Silos);

            var stopping = cluster.StopSiloAsync(handle, cancellationToken);
            await silo.StopEntered.Task.WaitAsync(cancellationToken);
            silo.ReleaseStop.TrySetResult();

            var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => stopping);

            Assert.Same(expectedFailure, actualFailure);
            Assert.Equal("Controlled Silo_0 stop failure.", actualFailure.Message);
            Assert.False(handle.IsActive);
            Assert.Empty(cluster.Silos);
            Assert.Equal(1, siloDisposal.DisposeCount);
            Assert.Equal([1], allocator.AllocationRequests);
            Assert.Equal(0, allocator.DisposeCount);
            AssertProviderDisposed(handle.ServiceProvider);

            var diagnostic = Assert.Single(
                logBuffer.AllEntries,
                entry => entry.Category == DiagnosticCategory && entry.EventId == SiloStopFailureEvent);
            Assert.Equal(LogLevel.Error, diagnostic.LogLevel);
            Assert.Equal("Silo Silo_0 stop failed by test control.", diagnostic.Message);
            Assert.Same(expectedFailure, diagnostic.Exception);

            await cluster.StopAllSilosAsync(cancellationToken);
            await cluster.DisposeAsync();
            await cluster.DisposeAsync();

            Assert.Equal(1, siloDisposal.DisposeCount);
            Assert.Equal(1, allocator.DisposeCount);
            Assert.Same(expectedFailure, diagnostic.Exception);
        }
        finally
        {
            silo.ReleaseStop.TrySetResult();
            await DisposeClusterAfterStopFailureAsync(cluster, expectedFailure);
        }
    }

    private static InProcessTestClusterBuilder CreateBuilder(short initialSilosCount, bool initializeClientOnDeploy)
    {
        var builder = new InProcessTestClusterBuilder(initialSilosCount);
        builder.Options.ConfigureFileLogging = false;
        builder.Options.InitializeClientOnDeploy = initializeClientOnDeploy;
        return builder;
    }

    private static void AddControlledLifecycle(
        IServiceCollection services,
        LifecycleControl control,
        DisposalTracker disposalTracker,
        InMemoryLogBuffer logBuffer)
    {
        services.AddLogging(logging => logging.AddProvider(new InMemoryLoggerProvider(logBuffer)));
        services.AddSingleton(_ => disposalTracker);
        services.AddSingleton<IHostedService>(serviceProvider =>
            new ControlledHostedService(
                control,
                serviceProvider,
                serviceProvider.GetRequiredService<DisposalTracker>(),
                serviceProvider.GetRequiredService<ILoggerFactory>()));
    }

    private static void AssertClientUnavailable(InProcessTestCluster cluster)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => cluster.Client);
        Assert.Equal(
            "The test cluster client is unavailable because the cluster has not been deployed or has been stopped.",
            exception.Message);
    }

    private static void AssertProviderDisposed(IServiceProvider serviceProvider) =>
        Assert.Throws<ObjectDisposedException>(() => serviceProvider.GetRequiredService<DisposalTracker>());

    private static async Task DisposeClusterAfterStopFailureAsync(
        InProcessTestCluster cluster,
        InvalidOperationException expectedFailure)
    {
        try
        {
            await cluster.DisposeAsync();
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, expectedFailure))
        {
            await cluster.DisposeAsync();
        }
    }

    private sealed class ControlledHostedService(
        LifecycleControl control,
        IServiceProvider serviceProvider,
        DisposalTracker disposalTracker,
        ILoggerFactory loggerFactory) : IHostedLifecycleService
    {
        private readonly ILogger _logger = loggerFactory.CreateLogger(DiagnosticCategory);

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _ = disposalTracker;
            control.ServiceProvider = serviceProvider;
            control.StartCoordinator?.ObserveStart();
            control.StartEntered.TrySetResult();

            if (control.RequiredPredecessor is { } predecessor)
            {
                await predecessor.WaitAsync(cancellationToken);
            }

            if (control.StartFailure is { } failure)
            {
                if (control.StartCoordinator is { } coordinator)
                {
                    await coordinator.SuccessfulSiloStarted.Task.WaitAsync(cancellationToken);
                    await coordinator.ReleaseFailure.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await control.ReleaseStart.Task.WaitAsync(cancellationToken);
                }

                var eventId = control.Name == "TestClusterClient"
                    ? ClientStartupFailureEvent
                    : SiloStartupFailureEvent;
                var role = control.Name == "TestClusterClient" ? "Client" : "Silo";
                _logger.LogError(
                    eventId,
                    failure,
                    "{Role} {Name} startup failed by test control.",
                    role,
                    control.Name);
                throw failure;
            }
        }

        public Task StartedAsync(CancellationToken cancellationToken)
        {
            control.Started.TrySetResult();
            control.StartCoordinator?.SuccessfulSiloStarted.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            control.StopEntered.TrySetResult();
            await control.ReleaseStop.Task.WaitAsync(cancellationToken);

            if (control.StopFailure is { } failure)
            {
                _logger.LogError(
                    SiloStopFailureEvent,
                    failure,
                    "Silo {Name} stop failed by test control.",
                    control.Name);
                throw failure;
            }
        }

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LifecycleControl(string name, SiloStartupCoordinator? startCoordinator = null)
    {
        public string Name { get; } = name;
        public SiloStartupCoordinator? StartCoordinator { get; } = startCoordinator;
        public Exception? StartFailure { get; init; }
        public Exception? StopFailure { get; init; }
        public Task? RequiredPredecessor { get; init; }
        public IServiceProvider ServiceProvider { get; set; } = null!;
        public TaskCompletionSource StartEntered { get; } = CreateCompletionSource();
        public TaskCompletionSource Started { get; } = CreateCompletionSource();
        public TaskCompletionSource ReleaseStart { get; } = CreateCompletionSource();
        public TaskCompletionSource StopEntered { get; } = CreateCompletionSource();
        public TaskCompletionSource ReleaseStop { get; } = CreateCompletionSource();
    }

    private sealed class SiloStartupCoordinator(int expectedStarts)
    {
        private int _remainingStarts = expectedStarts;

        public TaskCompletionSource BothStartsObserved { get; } = CreateCompletionSource();
        public TaskCompletionSource SuccessfulSiloStarted { get; } = CreateCompletionSource();
        public TaskCompletionSource ReleaseFailure { get; } = CreateCompletionSource();

        public void ObserveStart()
        {
            if (Interlocked.Decrement(ref _remainingStarts) == 0)
            {
                BothStartsObserved.TrySetResult();
            }
        }
    }

    private sealed class DisposalTracker : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class FixedPortAllocator : ITestClusterPortAllocator
    {
        private readonly ConcurrentQueue<int> _allocationRequests = new();
        private int _nextPortPair;
        private int _disposeCount;

        public IReadOnlyList<int> AllocationRequests => [.. _allocationRequests];
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public (int, int) AllocateConsecutivePortPairs(int numPorts)
        {
            _allocationRequests.Enqueue(numPorts);
            var pair = Interlocked.Increment(ref _nextPortPair);
            return (20_000 + pair, 30_000 + pair);
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private static TaskCompletionSource CreateCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
