using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;
using Orleans.Timers;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Runtime;

[TestCategory("BVT"), TestCategory("Placement")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class DeploymentLoadPublisherTests
{
    [Theory]
    [InlineData(-5000)]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Lifecycle_NonPositiveRefreshInterval_PreservesInitialDirectPublication(int refreshMilliseconds)
    {
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var rig = CreateTestRig(TimeSpan.FromMilliseconds(refreshMilliseconds), timerRegistry);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        ((ILifecycleParticipant<ISiloLifecycle>)rig.Publisher).Participate(lifecycle);

        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        await lifecycle.OnStop(TestContext.Current.CancellationToken);

        var statistics = rig.Publisher.LocalRuntimeStatistics;
        await rig.DirectTarget.Received(1).UpdateRuntimeStatistics(
            rig.LocalSilo, statistics, TestContext.Current.CancellationToken);
        Assert.Same(statistics, rig.Publisher.PeriodicStatistics[rig.LocalSilo]);
        Assert.Empty(timerRegistry.ReceivedCalls());
    }

    [Fact]
    public async Task PublishStatistics_PreCanceledToken_PreservesLocalState()
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Publisher.PublishStatistics(cancellation.Token));

        Assert.Null(rig.Publisher.LocalRuntimeStatistics);
        Assert.Empty(rig.Publisher.PeriodicStatistics);
        Assert.Empty(rig.DirectTarget.ReceivedCalls());
    }

    [Fact]
    public async Task PublicationTimer_PropagatesCancellationToRuntimeStatisticsRpc()
    {
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var timer = Substitute.For<IGrainTimer>();
        timer.When(value => value.Dispose()).Do(_ => timerCancellation.Cancel());
        var timerRegistry = Substitute.For<ITimerRegistry>();
        Func<CancellationToken, Task>? tick = null;
        timerRegistry.RegisterGrainTimer(
            Arg.Any<IGrainContext>(),
            Arg.Any<Func<Func<CancellationToken, Task>, CancellationToken, Task>>(),
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<GrainTimerCreationOptions>()).Returns(call =>
            {
                var callback = call.ArgAt<Func<Func<CancellationToken, Task>, CancellationToken, Task>>(1);
                var state = call.ArgAt<Func<CancellationToken, Task>>(2);
                tick = token => callback(state, token);
                return timer;
            });
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5), timerRegistry);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        ((ILifecycleParticipant<ISiloLifecycle>)rig.Publisher).Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        Task publication = Task.CompletedTask;

        try
        {
            Assert.NotNull(tick);
            var requestStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.DirectTarget.UpdateRuntimeStatistics(
                Arg.Any<SiloAddress>(), Arg.Any<SiloRuntimeStatistics>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var token = call.ArgAt<CancellationToken>(2);
                    requestStarted.TrySetResult(token);
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                });

            publication = tick(timerCancellation.Token);
            var rpcToken = await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(timerCancellation.Token, rpcToken);
        }
        finally
        {
            await lifecycle.OnStop(TestContext.Current.CancellationToken);
        }

        Assert.True(timerCancellation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publication.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        timer.Received(1).Dispose();
    }

    [Fact]
    public async Task PublishStatistics_CancellationPreservesNativeCompletion()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.DirectTarget.UpdateRuntimeStatistics(
            Arg.Any<SiloAddress>(), Arg.Any<SiloRuntimeStatistics>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                requested.SetResult();
                await call.ArgAt<CancellationToken>(2).WhenCancelled();
            });
        var publication = rig.Publisher.PublishStatistics(cancellation.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await publication.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(publication.IsCompletedSuccessfully);
        Assert.Same(rig.Publisher.LocalRuntimeStatistics, rig.Publisher.PeriodicStatistics[rig.LocalSilo]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshClusterStatistics_Cancellation_CancelsNativeRequests(bool useLinkedReceiverToken)
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var requested = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedRequests = 0;
        rig.Control.GetRuntimeStatistics(Arg.Any<CancellationToken>()).Returns(call => ReadStatistics(call.ArgAt<CancellationToken>(0)));
        var refresh = rig.Publisher.RefreshClusterStatistics(cancellation.Token);
        var requestToken = await requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(cancellation.Token, requestToken);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.Empty(rig.Publisher.PeriodicStatistics);

        async Task<SiloRuntimeStatistics> ReadStatistics(CancellationToken token)
        {
            using var receiverCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (Interlocked.Increment(ref startedRequests) == 2)
            {
                requested.TrySetResult(token);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, useLinkedReceiverToken ? receiverCancellation.Token : token);
            return rig.InitialStatistics;
        }
    }

    [Fact]
    public async Task RefreshClusterStatistics_CompletedRequests_PreserveNativeResults()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedRequests = 0;
        rig.Control.GetRuntimeStatistics(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref startedRequests) == 2)
            {
                requested.TrySetResult();
            }

            await call.ArgAt<CancellationToken>(0).WhenCancelled();
            return rig.InitialStatistics;
        });
        var refresh = rig.Publisher.RefreshClusterStatistics(cancellation.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(refresh.IsCompletedSuccessfully);
        Assert.Equal(2, rig.Publisher.PeriodicStatistics.Count);
        Assert.All(rig.Publisher.PeriodicStatistics.Values, statistics => Assert.Same(rig.InitialStatistics, statistics));
    }

    [Fact]
    public async Task StatisticsSubscribers_CanUnsubscribeDuringNotification()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        var first = Substitute.For<ISiloStatisticsChangeListener>();
        var second = Substitute.For<ISiloStatisticsChangeListener>();
        first.When(listener => listener.SiloStatisticsChangeNotification(rig.LocalSilo, rig.InitialStatistics))
            .Do(_ => rig.Publisher.UnsubscribeStatisticsChangeEvents(first));
        Assert.True(rig.Publisher.SubscribeToStatisticsChangeEvents(first));
        Assert.True(rig.Publisher.SubscribeToStatisticsChangeEvents(second));

        await rig.Publisher.UpdateRuntimeStatistics(rig.LocalSilo, rig.InitialStatistics, TestContext.Current.CancellationToken);

        first.Received(1).SiloStatisticsChangeNotification(rig.LocalSilo, rig.InitialStatistics);
        second.Received(1).SiloStatisticsChangeNotification(rig.LocalSilo, rig.InitialStatistics);
        Assert.False(rig.Publisher.UnsubscribeStatisticsChangeEvents(first));
        Assert.Same(rig.InitialStatistics, rig.Publisher.PeriodicStatistics[rig.LocalSilo]);
    }

    [Fact]
    public async Task StatisticsSubscribers_DeliverToRemainingSubscribersBeforeSurfacingFailure()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        var first = Substitute.For<ISiloStatisticsChangeListener>();
        var second = Substitute.For<ISiloStatisticsChangeListener>();
        var failure = new InvalidOperationException("Subscriber failure");
        first.When(listener => listener.SiloStatisticsChangeNotification(rig.LocalSilo, rig.InitialStatistics))
            .Do(_ => throw failure);
        rig.Publisher.SubscribeToStatisticsChangeEvents(first);
        rig.Publisher.SubscribeToStatisticsChangeEvents(second);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Publisher.UpdateRuntimeStatistics(rig.LocalSilo, rig.InitialStatistics, TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        second.Received(1).SiloStatisticsChangeNotification(rig.LocalSilo, rig.InitialStatistics);
        Assert.Same(rig.InitialStatistics, rig.Publisher.PeriodicStatistics[rig.LocalSilo]);
    }

    private static TestRig CreateTestRig(TimeSpan refreshTime, ITimerRegistry? timerRegistry = null)
    {
        var localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var remoteSilo = SiloAddress.FromParsableString("127.0.0.1:200@100");
        var localDetails = Substitute.For<ILocalSiloDetails>();
        localDetails.SiloAddress.Returns(localSilo);
        var statusOracle = Substitute.For<ISiloStatusOracle>();
        statusOracle.GetApproximateSiloStatus(Arg.Any<SiloAddress>()).Returns(SiloStatus.Active);
        statusOracle.GetApproximateSiloStatuses(onlyActive: true).Returns(new Dictionary<SiloAddress, SiloStatus>
        {
            [localSilo] = SiloStatus.Active,
            [remoteSilo] = SiloStatus.Active,
        });
        var directTarget = Substitute.For<IDeploymentLoadPublisher>();
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(
            Constants.DeploymentLoadPublisherSystemTargetType, remoteSilo).Returns(directTarget);
        var environmentStatistics = Substitute.For<IEnvironmentStatisticsProvider>();
        var loadSheddingOptions = Options.Create(new LoadSheddingOptions());
        var initialStatistics = new SiloRuntimeStatistics(
            0, 0, environmentStatistics, loadSheddingOptions, DateTime.UnixEpoch);
        var control = Substitute.For<ISiloControl>();
        control.GetRuntimeStatistics(Arg.Any<CancellationToken>()).Returns(Task.FromResult(initialStatistics));
        grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, Arg.Any<SiloAddress>()).Returns(control);

        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<CatalogInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        services.AddSingleton<GrainInstruments>();
        services.AddSingleton<MessagingInstruments>();
        services.AddSingleton<MessagingProcessingInstruments>();
        services.AddSingleton(localDetails);
        services.AddSingleton(statusOracle);
        services.AddSingleton(grainFactory);
        services.AddSingleton(Substitute.For<IActivationWorkingSet>());
        services.AddSingleton(environmentStatistics);
        services.AddSingleton<IOptions<LoadSheddingOptions>>(loadSheddingOptions);
        services.AddOptions<DeploymentLoadPublisherOptions>().Configure(
            options => options.DeploymentLoadPublisherRefreshTime = refreshTime);
        services.AddSingleton<ActivationDirectory>();
        services.AddSingleton(serviceProvider => new SystemTargetShared(
            runtimeClient: null!,
            localDetails,
            NullLoggerFactory.Instance,
            Options.Create(new SchedulingOptions()),
            grainReferenceActivator: null!,
            timerRegistry: timerRegistry!,
            serviceProvider.GetRequiredService<ActivationDirectory>(),
            serviceProvider.GetRequiredService<SchedulerInstruments>(),
            serviceProvider.GetRequiredService<GrainInstruments>(),
            serviceProvider.GetRequiredService<MessagingInstruments>(),
            serviceProvider.GetRequiredService<MessagingProcessingInstruments>()));
        services.AddSingleton<DeploymentLoadPublisher>();
        var serviceProvider = services.BuildServiceProvider();
        return new(serviceProvider, serviceProvider.GetRequiredService<DeploymentLoadPublisher>(), directTarget, control, initialStatistics, localSilo);
    }

    private sealed record TestRig(
        ServiceProvider ServiceProvider,
        DeploymentLoadPublisher Publisher,
        IDeploymentLoadPublisher DirectTarget,
        ISiloControl Control,
        SiloRuntimeStatistics InitialStatistics,
        SiloAddress LocalSilo) : IDisposable
    {
        public void Dispose() => ServiceProvider.Dispose();
    }
}
