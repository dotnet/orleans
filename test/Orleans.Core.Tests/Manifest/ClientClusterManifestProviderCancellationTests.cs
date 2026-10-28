using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class ClientClusterManifestProviderCancellationTests
{
    [Fact]
    public async Task StartAsync_IncompleteInitialization_StopPropagatesLifetimeCancellation()
    {
        await using var fixture = new Fixture();
        await fixture.Gateways.StartAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new TaskCompletionSource<ClusterManifestUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Remote.GetClusterManifestUpdate(Arg.Any<MajorMinorVersion>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entered.SetResult(call.Arg<CancellationToken>());
                return new ValueTask<ClusterManifestUpdate?>(rpc.Task);
            });
        using var startup = new CancellationTokenSource();
        var start = fixture.Provider.StartAsync(startup.Token);
        try
        {
            var lifetimeToken = await Await(entered.Task);

            await Await(fixture.Provider.StopAsync(TestContext.Current.CancellationToken));
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(start));

            Assert.False(startup.IsCancellationRequested);
            Assert.True(lifetimeToken.IsCancellationRequested);
            Assert.Equal(lifetimeToken, exception.CancellationToken);
            Assert.True(start.IsCanceled);
            Assert.Equal(1, fixture.Logger.StoppedCount);
            Assert.Equal(MajorMinorVersion.MinValue, fixture.Provider.Current.Version);
            Assert.False(rpc.Task.IsCompleted);
        }
        finally
        {
            rpc.TrySetException(new InvalidOperationException("Late RPC failure after initialization stopped."));
        }
    }

    [Fact]
    public async Task StartAsync_IncompleteInitialization_RunSetupFailurePropagatesOriginalFailure()
    {
        var failure = new InvalidOperationException("The manifest run cannot resolve its grain factory.");
        await using var fixture = new Fixture(factoryFailure: failure);
        using var startup = new CancellationTokenSource();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Await(fixture.Provider.StartAsync(startup.Token)));

        Assert.Same(failure, exception);
        Assert.False(startup.IsCancellationRequested);
        Assert.Equal(1, fixture.FactoryResolutions);
        Assert.Empty(fixture.Factory.ReceivedCalls());
        Assert.Empty(fixture.Remote.ReceivedCalls());
        Assert.Equal(1, fixture.Logger.StoppedCount);
        Assert.Equal(MajorMinorVersion.MinValue, fixture.Provider.Current.Version);
    }

    [Fact]
    public async Task StartAsync_PreCanceled_DoesNotRequestManifest()
    {
        await using var fixture = new Fixture();
        await fixture.Gateways.StartAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Provider.StartAsync(cancellation.Token));
        await fixture.Provider.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, fixture.FactoryResolutions);
        Assert.Empty(fixture.Factory.ReceivedCalls());
        Assert.Empty(fixture.Remote.ReceivedCalls());
        Assert.Equal(MajorMinorVersion.MinValue, fixture.Provider.Current.Version);
        Assert.DoesNotContain(LogLevel.Error, fixture.Logger.Levels);
    }

    [Fact]
    public async Task StartAsync_CanceledDuringRequest_JoinsOwnedRunWithoutJoiningRpc()
    {
        await using var fixture = new Fixture();
        await fixture.Gateways.StartAsync(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stoppedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stoppedRelease = new ManualResetEventSlim();
        var rpc = new TaskCompletionSource<ClusterManifestUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var testToken = TestContext.Current.CancellationToken;
        // Logging is the final synchronous boundary of RunAsync's finally block.
        // Hold that owned work, not the RPC callback or remote execution.
        fixture.Logger.OnStopped = () =>
        {
            stoppedEntered.SetResult();
            if (!stoppedRelease.Wait(TimeSpan.FromSeconds(10), testToken))
            {
                throw new TimeoutException("Test did not release owned manifest-run finalization.");
            }
        };
        fixture.Remote.GetClusterManifestUpdate(Arg.Any<MajorMinorVersion>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entered.SetResult(call.Arg<CancellationToken>());
                return new ValueTask<ClusterManifestUpdate?>(rpc.Task);
            });
        using var cancellation = new CancellationTokenSource();
        var start = fixture.Provider.StartAsync(cancellation.Token);
        var completedAfterCleanup = start.ContinueWith(
            _ => fixture.Logger.StoppedCount,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var requestToken = await Await(entered.Task);
            Assert.True(requestToken.CanBeCanceled);
            Assert.False(start.IsCompleted);

            var cancel = cancellation.CancelAsync();
            await Await(stoppedEntered.Task);

            Assert.True(requestToken.IsCancellationRequested);
            Assert.False(start.IsCompleted, "StartAsync must join its owned run task, including finalization.");
            Assert.False(rpc.Task.IsCompleted);
            stoppedRelease.Set();
            await Await(cancel);

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(start));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(start.IsCanceled);
            Assert.Equal(1, await Await(completedAfterCleanup));
            Assert.False(rpc.Task.IsCompleted);
            Assert.Equal(MajorMinorVersion.MinValue, fixture.Provider.Current.Version);
            Assert.DoesNotContain(fixture.Remote.ReceivedCalls(),
                call => call.GetMethodInfo().Name == nameof(IClusterManifestSystemTarget.GetClusterManifest));
        }
        finally
        {
            stoppedRelease.Set();
            rpc.TrySetException(new InvalidOperationException("Late runtime-owned manifest RPC failure."));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_CanceledWhileRpcAwaitsAcknowledgement_StopsBeforeAcknowledgement(bool legacyRpc)
    {
        await using var fixture = new Fixture();
        await fixture.Gateways.StartAsync(TestContext.Current.CancellationToken);
        var requestEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSignaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationManager = Substitute.For<IGrainCallCancellationManager>();
        cancellationManager.WhenForAnyArgs(manager => manager.SignalCancellation(default, default, default, default))
            .Do(_ => cancellationSignaled.TrySetResult());
        var completion = new ResponseCompletionSource();
        var rpc = completion.AsValueTask().AsTask();
        var invocation = Substitute.For<IInvokable>();
        invocation.IsCancellable.Returns(true);
        using var message = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(9101),
            SendingGrain = ClientGrainId.Create().GrainId,
            TargetGrain = GrainId.Create("manifest-cancellation-test", "target"),
            BodyObject = invocation
        };
        var unregistered = 0;
        var callback = new CallbackData(
            new SharedCallbackData(
                _ => Interlocked.Increment(ref unregistered),
                NullLogger<CallbackData>.Instance, new FakeTimeProvider(), TimeSpan.FromMinutes(1),
                cancelOnTimeout: false, waitForCancellationAcknowledgement: true, cancellationManager,
                new ApplicationRequestInstruments(fixture.Instruments)),
            completion, message);
        fixture.Remote.GetClusterManifestUpdate(Arg.Any<MajorMinorVersion>(), Arg.Any<CancellationToken>())
            .Returns(call => legacyRpc
                ? new ValueTask<ClusterManifestUpdate?>(Task.FromException<ClusterManifestUpdate?>(new NotSupportedException("Legacy gateway.")))
                : new ValueTask<ClusterManifestUpdate?>(ModernRequest(call.Arg<CancellationToken>())));
        fixture.Remote.GetClusterManifest(Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<ClusterManifest>(LegacyRequest(call.Arg<CancellationToken>())));
        using var cancellation = new CancellationTokenSource();
        var start = fixture.Provider.StartAsync(cancellation.Token);
        try
        {
            var requestToken = await Await(requestEntered.Task);
            cancellation.Cancel();
            await Await(cancellationSignaled.Task);

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(start));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(requestToken.IsCancellationRequested);
            Assert.True(start.IsCanceled);
            Assert.Equal(1, fixture.Logger.StoppedCount);
            Assert.False(callback.IsCompleted);
            Assert.False(rpc.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref unregistered));
            Assert.Equal(legacyRpc ? 1 : 0, fixture.Remote.ReceivedCalls()
                .Count(call => call.GetMethodInfo().Name == nameof(IClusterManifestSystemTarget.GetClusterManifest)));

            // Only NOW acknowledge cancellation. The callback remains runtime-owned after Start exits.
            using var response = Response.FromException(new OperationCanceledException(requestToken));
            callback.DoCallback(new Message { Direction = Message.Directions.Response, BodyObject = response });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(rpc));
            Assert.True(callback.IsCompleted);
            Assert.Equal(1, fixture.Logger.StoppedCount);
        }
        finally
        {
            // Rescue only after assertions have passed/failed, never to make startup cancellation pass.
            callback.OnHostShutdown();
            await ((Task)rpc).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        async Task<ClusterManifestUpdate?> ModernRequest(CancellationToken token)
        {
            callback.SubscribeForCancellation(token);
            requestEntered.SetResult(token);
            using var response = await rpc;
            return (ClusterManifestUpdate?)response.Result;
        }

        async Task<ClusterManifest> LegacyRequest(CancellationToken token)
        {
            callback.SubscribeForCancellation(token);
            requestEntered.SetResult(token);
            using var response = await rpc;
            return (ClusterManifest)response.Result!;
        }
    }

    [Fact]
    public async Task StartAsync_CanceledWithoutGateways_StopsWithoutFetchingManifest()
    {
        await using var fixture = new Fixture(emptyGateways: true);
        // An unstarted manager has no gateways. The run loop must cancel its no-gateway delay.
        using var cancellation = new CancellationTokenSource();
        var start = fixture.Provider.StartAsync(cancellation.Token);
        await Await(fixture.EmptyGatewaysRequested.Task);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(start));
        await fixture.Provider.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(fixture.Factory.ReceivedCalls());
        Assert.Empty(fixture.Remote.ReceivedCalls());
        Assert.Equal(MajorMinorVersion.MinValue, fixture.Provider.Current.Version);
        Assert.DoesNotContain(LogLevel.Error, fixture.Logger.Levels);
    }

    [Fact]
    public async Task StartAsync_CanceledAfterSuccess_ContinuesPublishingUntilStopped()
    {
        await using var fixture = new Fixture();
        await fixture.Gateways.StartAsync(TestContext.Current.CancellationToken);
        var refreshEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = new TaskCompletionSource<ClusterManifestUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Remote.GetClusterManifestUpdate(Arg.Any<MajorMinorVersion>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                return Interlocked.Increment(ref calls) switch
                {
                    1 => new ValueTask<ClusterManifestUpdate?>(Update(1)),
                    2 => new ValueTask<ClusterManifestUpdate?>(Refresh(token)),
                    _ => new ValueTask<ClusterManifestUpdate?>(WaitForStop(token))
                };
            });
        using var cancellation = new CancellationTokenSource();
        await Await(fixture.Provider.StartAsync(cancellation.Token));
        var refreshToken = await Await(refreshEntered.Task);
        await using var updates = fixture.Provider.Updates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(new MajorMinorVersion(1, 1), updates.Current.Version);
        var published = updates.MoveNextAsync().AsTask();

        cancellation.Cancel();

        Assert.False(refreshToken.IsCancellationRequested);
        refresh.SetResult(Update(2));
        Assert.True(await Await(published));
        Assert.Equal(new MajorMinorVersion(2, 2), updates.Current.Version);
        var ownedToken = await Await(nextEntered.Task);
        Assert.False(ownedToken.IsCancellationRequested);
        await Await(fixture.Provider.StopAsync(TestContext.Current.CancellationToken));
        Assert.True(ownedToken.IsCancellationRequested);
        Assert.Equal(1, fixture.Logger.StoppedCount);
        await Await(nextExited.Task);
        Assert.Equal(new MajorMinorVersion(2, 2), fixture.Provider.Current.Version);

        Task<ClusterManifestUpdate?> Refresh(CancellationToken token)
        {
            refreshEntered.SetResult(token);
            return refresh.Task.WaitAsync(token);
        }

        async Task<ClusterManifestUpdate?> WaitForStop(CancellationToken token)
        {
            nextEntered.SetResult(token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
            finally
            {
                nextExited.SetResult();
            }
        }
    }

    private static ClusterManifestUpdate Update(long major) =>
        new(new MajorMinorVersion(major, 0), ImmutableDictionary<SiloAddress, GrainManifest>.Empty, includesAllActiveServers: true);

    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private int _factoryResolutions;
        public int FactoryResolutions => Volatile.Read(ref _factoryResolutions);
        public IInternalGrainFactory Factory { get; } = Substitute.For<IInternalGrainFactory>();
        public IClusterManifestSystemTarget Remote { get; } = Substitute.For<IClusterManifestSystemTarget>();
        public ShutdownLogger Logger { get; } = new();
        public OrleansInstruments Instruments => _services.GetRequiredService<OrleansInstruments>();
        public GatewayManager Gateways { get; }
        public ClientClusterManifestProvider Provider { get; }
        public TaskCompletionSource EmptyGatewaysRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture(bool emptyGateways = false, Exception? factoryFailure = null)
        {
            _services = new ServiceCollection()
                .AddMetrics()
                .AddSingleton<OrleansInstruments>()
                .AddSingleton<IInternalGrainFactory>(_ =>
                {
                    Interlocked.Increment(ref _factoryResolutions);
                    if (factoryFailure is not null)
                    {
                        throw factoryFailure;
                    }

                    return Factory;
                })
                .BuildServiceProvider();
            Factory.GetGrain<IClusterManifestSystemTarget>(Arg.Any<GrainId>()).Returns(Remote);
            Gateways = new GatewayManager(
                Options.Create(new GatewayOptions { GatewayListRefreshPeriod = TimeSpan.FromHours(1) }),
                emptyGateways
                    ? new EmptyGatewayListProvider(EmptyGatewaysRequested)
                    : new StaticGatewayListProvider(Options.Create(new StaticGatewayListProviderOptions
                    {
                        Gateways = [new Uri("gwy.tcp://127.0.0.1:30000/1")]
                    }), Options.Create(new GatewayOptions())),
                NullLoggerFactory.Instance,
                new ConnectionManager(Options.Create(new ConnectionOptions()), null!, NullLogger<ConnectionManager>.Instance),
                new FakeTimeProvider());
            Provider = new ClientClusterManifestProvider(
                _services,
                new LocalClientDetails(Options.Create(new ClientMessagingOptions { LocalAddress = IPAddress.Loopback })),
                Gateways,
                Logger,
                new ClientManifestProvider([], Options.Create(new GrainTypeOptions()), null!),
                Options.Create(new TypeManagementOptions { TypeMapRefreshInterval = emptyGateways ? TimeSpan.FromHours(1) : TimeSpan.Zero }));
        }

        public sealed class ShutdownLogger : ILogger<ClientClusterManifestProvider>
        {
            private int _stoppedCount;
            public int StoppedCount => Volatile.Read(ref _stoppedCount);
            public ConcurrentQueue<LogLevel> Levels { get; } = new();
            public Action? OnStopped { get; set; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Levels.Enqueue(logLevel);
                if (formatter(state, exception) == "Stopped refreshing cluster manifest.")
                {
                    OnStopped?.Invoke();
                    Interlocked.Increment(ref _stoppedCount);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Gateways.StopAsync(TestContext.Current.CancellationToken);
            Gateways.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class EmptyGatewayListProvider(TaskCompletionSource requested) : IGatewayListProvider
    {
        public TimeSpan MaxStaleness => TimeSpan.FromHours(1);
        public bool IsUpdatable => true;
        public Task InitializeGatewayListProvider() => Task.CompletedTask;
        public Task<IList<Uri>> GetGateways()
        {
            requested.TrySetResult();
            return Task.FromResult<IList<Uri>>([]);
        }
    }
}
