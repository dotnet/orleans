using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
[TestCategory("BVT")]
public sealed class ClientHardShutdownTests
{
    [Fact]
    public async Task TestCluster_KillClientAsync_SuppressesItsCancellation()
    {
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(
            initialSilosCount: 0,
            new RecordingPortAllocator());

        await AssertIntentionalCancellationIsSuppressedAsync(
            cluster,
            () => cluster.KillClientAsync(),
            () => cluster.ClientHost);
    }

    [Fact]
    public async Task InProcessTestCluster_KillClientAsync_SuppressesItsCancellation()
    {
        await using var cluster = new InProcessTestCluster(
            new InProcessTestClusterOptions { InitializeClientOnDeploy = false },
            new RecordingPortAllocator());

        await AssertIntentionalCancellationIsSuppressedAsync(
            cluster,
            () => cluster.KillClientAsync(),
            () => cluster.ClientHost);
    }

    [Fact]
    public async Task TestCluster_KillClientAsync_PreservesShutdownFailure()
    {
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(
            initialSilosCount: 0,
            new RecordingPortAllocator());

        await AssertShutdownFailureIsPreservedAsync(
            cluster,
            () => cluster.KillClientAsync(),
            () => cluster.ClientHost);
    }

    [Fact]
    public async Task InProcessTestCluster_KillClientAsync_PreservesShutdownFailure()
    {
        await using var cluster = new InProcessTestCluster(
            new InProcessTestClusterOptions { InitializeClientOnDeploy = false },
            new RecordingPortAllocator());

        await AssertShutdownFailureIsPreservedAsync(
            cluster,
            () => cluster.KillClientAsync(),
            () => cluster.ClientHost);
    }

    private static async Task AssertIntentionalCancellationIsSuppressedAsync(
        object cluster,
        Func<Task> killClientAsync,
        Func<IHost?> getClientHost)
    {
        var hostedService = new CancellationStopHostedService();
        var host = new HostBuilder()
            .ConfigureServices(services => services.AddSingleton<IHostedService>(hostedService))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var client = new RecordingHost(host);
        SetClientHost(cluster, client);

        await killClientAsync();

        Assert.True(client.StopToken.IsCancellationRequested);
        Assert.True(hostedService.StopToken.IsCancellationRequested);
        Assert.NotEqual(client.StopToken, hostedService.StopToken);
        Assert.Equal(1, client.DisposeCount);
        Assert.Null(getClientHost());

        var aggregateCancellation = new AggregateException(
            new OperationCanceledException(),
            new TaskCanceledException());
        client = new RecordingHost(_ => Task.FromException(aggregateCancellation));
        SetClientHost(cluster, client);

        await killClientAsync();

        Assert.Equal(1, client.DisposeCount);
        Assert.Null(getClientHost());
    }

    private static async Task AssertShutdownFailureIsPreservedAsync(
        object cluster,
        Func<Task> killClientAsync,
        Func<IHost?> getClientHost)
    {
        var expected = new InvalidOperationException("Controlled client shutdown failure.");
        var client = new RecordingHost(_ => Task.FromException(expected));
        SetClientHost(cluster, client);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(killClientAsync);

        Assert.Same(expected, actual);
        Assert.True(client.StopToken.IsCancellationRequested);
        Assert.Equal(1, client.DisposeCount);
        Assert.Null(getClientHost());

        var aggregateFailure = new AggregateException(new OperationCanceledException(), expected);
        client = new RecordingHost(_ => Task.FromException(aggregateFailure));
        SetClientHost(cluster, client);

        var actualAggregate = await Assert.ThrowsAsync<AggregateException>(killClientAsync);

        Assert.Same(aggregateFailure, actualAggregate);
        Assert.Equal(1, client.DisposeCount);
        Assert.Null(getClientHost());
    }

    private static void SetClientHost(object cluster, IHost client)
    {
        var property = cluster.GetType().GetProperty(
            "ClientHost",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{cluster.GetType().Name}.ClientHost was not found.");

        property.SetValue(cluster, client);
    }

    private sealed class RecordingHost : IHost
    {
        private readonly IHost? _inner;
        private readonly Func<CancellationToken, Task>? _stop;
        private readonly ServiceProvider? _services;

        public RecordingHost(IHost inner)
        {
            _inner = inner;
        }

        public RecordingHost(Func<CancellationToken, Task> stop)
        {
            _stop = stop;
            _services = new ServiceCollection().BuildServiceProvider();
        }

        public IServiceProvider Services => _inner?.Services ?? _services!;

        public CancellationToken StopToken { get; private set; }

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            _inner?.StartAsync(cancellationToken) ?? Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopToken = cancellationToken;
            return _inner?.StopAsync(cancellationToken) ?? _stop!(cancellationToken);
        }

        public void Dispose()
        {
            DisposeCount++;
            _inner?.Dispose();
            _services?.Dispose();
        }
    }

    private sealed class CancellationStopHostedService : IHostedService
    {
        public CancellationToken StopToken { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopToken = cancellationToken;
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
