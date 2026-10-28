using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GatewayManagerCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_PreCanceled_DoesNotInvokeProvider(bool initialize)
    {
        var provider = new CancellableProvider(initialize);
        using var manager = CreateManager(provider, new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, provider.Initializations);
        Assert.Equal(0, provider.Lookups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_Cancellation_ReachesAndStopsProviderOperation(bool initialize)
    {
        var provider = new CancellableProvider(initialize);
        using var manager = CreateManager(provider, new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();
        var start = manager.StartAsync(cancellation.Token);
        var operation = await Await(provider.Operations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        try
        {
            Assert.Equal(cancellation.Token, operation.Token);
            Assert.False(start.IsCompleted);
            cancellation.Cancel();
            await Await(operation.CleanupEntered.Task);
            Assert.False(operation.Exited.Task.IsCompleted);
            operation.CleanupRelease.SetResult();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(start));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            await Await(operation.Exited.Task);
            Assert.True(operation.Token.IsCancellationRequested);
            Assert.Equal(initialize ? 0 : 1, provider.Lookups);
            Assert.Equal(1, provider.Initializations);
        }
        finally
        {
            operation.CleanupRelease.TrySetResult();
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_ShutdownCancelsOwnedOperation_StartupTokenIsDetached(bool expedited)
    {
        var provider = new CancellableProvider(blockInitialization: false) { BlockLookupAfter = 1 };
        var clock = new FakeTimeProvider();
        using var manager = CreateManager(provider, clock);
        using var startup = new CancellationTokenSource();
        await manager.StartAsync(startup.Token);
        startup.Cancel();
        if (expedited)
        {
            manager.ExpediteUpdateLiveGatewaysSnapshot();
        }
        else
        {
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var operation = await Await(provider.Operations.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        try
        {
            Assert.NotEqual(startup.Token, operation.Token);
            Assert.True(operation.Token.CanBeCanceled);
            Assert.False(operation.Token.IsCancellationRequested);
            var stop = manager.StopAsync(TestContext.Current.CancellationToken);
            await Await(operation.CleanupEntered.Task);
            Assert.True(operation.Token.IsCancellationRequested);
            operation.CleanupRelease.SetResult();
            await Await(stop);
            await Await(operation.Exited.Task);
            Assert.Equal(1, provider.Initializations);
            Assert.Equal(2, provider.Lookups);
        }
        finally
        {
            operation.CleanupRelease.TrySetResult();
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_LegacyProvider_CancellationBoundsCompatibilityWait(bool initialize)
    {
        var provider = new LegacyProvider(initialize);
        using var manager = CreateManager(provider, new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();
        var start = manager.StartAsync(cancellation.Token);
        try
        {
            Assert.Equal(1, provider.Initializations);
            Assert.Equal(initialize ? 0 : 1, provider.Lookups);
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(start));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.False(provider.Completion.Task.IsCompleted);
            Assert.True(start.IsCanceled);
        }
        finally
        {
            provider.Completion.TrySetResult([]);
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task StartAsync_LegacyProvider_PreservesSuccessfulGatewayDiscovery()
    {
        var provider = new LegacyProvider(blockInitialization: true);
        provider.Completion.SetResult([new Uri("gwy.tcp://127.0.0.1:30001/1")]);
        using var manager = CreateManager(provider, new FakeTimeProvider());

        await manager.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.Initializations);
        Assert.Equal(1, provider.Lookups);
        Assert.Equal(SiloAddress.FromParsableString("127.0.0.1:30001@0"), manager.GetLiveGateway());
        await manager.StopAsync(TestContext.Current.CancellationToken);
    }

    private static GatewayManager CreateManager(IGatewayListProvider provider, TimeProvider clock) =>
        new(Options.Create(new GatewayOptions { GatewayListRefreshPeriod = TimeSpan.FromMinutes(1) }),
            provider, NullLoggerFactory.Instance,
            new ConnectionManager(Options.Create(new ConnectionOptions()), null!, NullLogger<ConnectionManager>.Instance), clock);

    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class Operation(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Run()
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, Token);
            }
            finally
            {
                CleanupEntered.SetResult();
                await CleanupRelease.Task;
                Exited.SetResult();
            }
        }
    }

    private sealed class CancellableProvider(bool blockInitialization) : IGatewayListProvider
    {
        public Channel<Operation> Operations { get; } = Channel.CreateUnbounded<Operation>();
        public int Initializations { get; private set; }
        public int Lookups { get; private set; }
        public int BlockLookupAfter { get; init; }
        public TimeSpan MaxStaleness => TimeSpan.FromMinutes(1);
        public bool IsUpdatable => true;
        public Task InitializeGatewayListProvider() => throw new InvalidOperationException("Legacy initialization must not be used.");
        public Task<IList<Uri>> GetGateways() => throw new InvalidOperationException("Legacy lookup must not be used.");

        public Task InitializeGatewayListProviderAsync(CancellationToken cancellationToken = default)
        {
            Initializations++;
            return blockInitialization ? Block(cancellationToken) : Task.CompletedTask;
        }

        public async Task<IList<Uri>> GetGatewaysAsync(CancellationToken cancellationToken = default)
        {
            Lookups++;
            if (Lookups > BlockLookupAfter)
            {
                await Block(cancellationToken);
            }

            return [new Uri("gwy.tcp://127.0.0.1:30000/1")];
        }

        private Task Block(CancellationToken token)
        {
            var operation = new Operation(token);
            var task = operation.Run();
            Assert.True(Operations.Writer.TryWrite(operation));
            return task;
        }
    }

    private sealed class LegacyProvider(bool blockInitialization) : IGatewayListProvider
    {
        public TaskCompletionSource<IList<Uri>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Initializations { get; private set; }
        public int Lookups { get; private set; }
        public TimeSpan MaxStaleness => TimeSpan.FromMinutes(1);
        public bool IsUpdatable => true;
        public Task InitializeGatewayListProvider()
        {
            Initializations++;
            return blockInitialization ? Completion.Task : Task.CompletedTask;
        }

        public Task<IList<Uri>> GetGateways()
        {
            Lookups++;
            return Completion.Task;
        }
    }
}
