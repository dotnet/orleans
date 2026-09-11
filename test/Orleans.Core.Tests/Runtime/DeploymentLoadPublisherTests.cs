using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
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
    [Fact]
    public async Task PublishStatistics_DefaultDisabled_PreservesDirectPublication()
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5));

        await rig.Publisher.PublishStatistics(TestContext.Current.CancellationToken);

        await rig.DirectTarget.Received(1).UpdateRuntimeStatistics(
            rig.LocalSilo, rig.Publisher.LocalRuntimeStatistics, TestContext.Current.CancellationToken);
        Assert.Empty(rig.Dissemination.ReceivedCalls());
        Assert.False(new DisseminationOptions().Enabled);
        Assert.False(new DeploymentLoadPublisherOptions().Dissemination.Enabled);
        Assert.False(new ClusterMembershipOptions().Dissemination.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStatistics_ConfirmedPeersUseDissemination(bool confirmed)
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5), enableDissemination: true);
        var remoteSilo = SiloAddress.FromParsableString("127.0.0.1:200@100");
        rig.Dissemination.GetUnconfirmedPeers(Arg.Any<IDisseminationNamespace>())
            .Returns(confirmed ? [] : new[] { remoteSilo });

        await rig.Publisher.PublishStatistics(TestContext.Current.CancellationToken);

        await rig.Dissemination.Received(1).Publish(
            Arg.Any<IDisseminationNamespace>(), rig.LocalSilo,
            rig.Publisher.LocalRuntimeStatistics.DateTime.Ticks, Arg.Any<CancellationToken>());
        await rig.DirectTarget.Received(confirmed ? 0 : 1).UpdateRuntimeStatistics(
            rig.LocalSilo, rig.Publisher.LocalRuntimeStatistics, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishStatistics_DisseminationCancelsIndependently_FallsBackToDirectPublication()
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5), enableDissemination: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        rig.Dissemination.Publish(
            Arg.Any<IDisseminationNamespace>(), Arg.Any<DisseminationKey>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromCanceled<bool>(cancellation.Token));

        await rig.Publisher.PublishStatistics(TestContext.Current.CancellationToken);

        await rig.DirectTarget.Received(1).UpdateRuntimeStatistics(
            rig.LocalSilo, rig.Publisher.LocalRuntimeStatistics, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishStatistics_CallerCancels_DoesNotFallBackToDirectPublication()
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5), enableDissemination: true);
        using var cancellation = new CancellationTokenSource();
        rig.Dissemination.Publish(
            Arg.Any<IDisseminationNamespace>(), Arg.Any<DisseminationKey>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<bool>(call.ArgAt<CancellationToken>(3));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Publisher.PublishStatistics(cancellation.Token));

        Assert.Empty(rig.DirectTarget.ReceivedCalls());
    }

    [Fact]
    public async Task PublishStatistics_DeadlineCancelsNativePublicationBeforeDirectDelivery()
    {
        using var rig = CreateTestRig(TimeSpan.FromSeconds(5), enableDissemination: true);
        var timeProvider = (FakeTimeProvider)rig.ServiceProvider.GetRequiredService<TimeProvider>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        rig.Dissemination.Publish(
            Arg.Any<IDisseminationNamespace>(), Arg.Any<DisseminationKey>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<bool>(PublishAsync(call.ArgAt<CancellationToken>(3))));
        var publication = rig.Publisher.PublishStatistics(TestContext.Current.CancellationToken);
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await publication.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(token.IsCancellationRequested);
        Assert.True(completed);
        await rig.DirectTarget.Received(1).UpdateRuntimeStatistics(
            rig.LocalSilo, rig.Publisher.LocalRuntimeStatistics, TestContext.Current.CancellationToken);

        async Task<bool> PublishAsync(CancellationToken cancellationToken)
        {
            started.SetResult(cancellationToken);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
            finally
            {
                completed = true;
            }
        }
    }

    [Fact]
    public async Task ApplyLoadStatistics_UsesPublisherSchedulerAndSuppressesDisseminationDuplicates()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        var listener = Substitute.For<ISiloStatisticsChangeListener>();
        listener.When(value => value.SiloStatisticsChangeNotification(rig.LocalSilo, Arg.Any<SiloRuntimeStatistics>()))
            .Do(_ =>
            {
                Assert.Same(rig.Publisher, RuntimeContext.Current);
                rig.Publisher.UnsubscribeStatisticsChangeEvents(listener);
            });
        var remaining = Substitute.For<ISiloStatisticsChangeListener>();
        rig.Publisher.SubscribeToStatisticsChangeEvents(listener);
        rig.Publisher.SubscribeToStatisticsChangeEvents(remaining);
        var ns = rig.ServiceProvider.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();
        var value = ns.CreateValue(rig.LocalSilo, rig.InitialStatistics);

        var applied = await ns.ApplyValueAsync(value, TestContext.Current.CancellationToken);
        var duplicate = await ns.ApplyValueAsync(value, TestContext.Current.CancellationToken);

        Assert.Equal(DisseminationApplyResult.Applied, applied);
        Assert.Equal(DisseminationApplyResult.Duplicate, duplicate);
        listener.Received(1).SiloStatisticsChangeNotification(rig.LocalSilo, Arg.Any<SiloRuntimeStatistics>());
        remaining.Received(1).SiloStatisticsChangeNotification(rig.LocalSilo, Arg.Any<SiloRuntimeStatistics>());
        Assert.Equal(rig.InitialStatistics.DateTime, rig.Publisher.PeriodicStatistics[rig.LocalSilo].DateTime);
    }

    [Fact]
    public async Task UpdateRuntimeStatistics_DefaultDisabled_PreservesDuplicateNotifications()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        var listener = Substitute.For<ISiloStatisticsChangeListener>();
        rig.Publisher.SubscribeToStatisticsChangeEvents(listener);

        await rig.Publisher.UpdateRuntimeStatistics(rig.LocalSilo, rig.InitialStatistics, TestContext.Current.CancellationToken);
        await rig.Publisher.UpdateRuntimeStatistics(rig.LocalSilo, rig.InitialStatistics, TestContext.Current.CancellationToken);

        listener.Received(2).SiloStatisticsChangeNotification(rig.LocalSilo, rig.InitialStatistics);
        Assert.Same(rig.InitialStatistics, rig.Publisher.PeriodicStatistics[rig.LocalSilo]);
    }

    [Theory]
    [InlineData(SiloStatus.Dead)]
    [InlineData(SiloStatus.None)]
    public async Task ApplyLoadStatistics_DepartedGenerationRemainsRemoved(SiloStatus status)
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        await rig.Publisher.UpdateRuntimeStatistics(rig.LocalSilo, rig.InitialStatistics, TestContext.Current.CancellationToken);
        var oracle = rig.ServiceProvider.GetRequiredService<ISiloStatusOracle>();
        oracle.GetApproximateSiloStatus(rig.LocalSilo).Returns(status);
        rig.Publisher.SiloStatusChangeNotification(rig.LocalSilo, SiloStatus.Dead);
        var ns = rig.ServiceProvider.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();

        var result = await ns.ApplyValueAsync(
            ns.CreateValue(rig.LocalSilo, rig.InitialStatistics), TestContext.Current.CancellationToken);

        Assert.Equal(DisseminationApplyResult.Rejected, result);
        Assert.False(rig.Publisher.PeriodicStatistics.ContainsKey(rig.LocalSilo));
        Assert.Equal(0, ns.GetVersion(rig.LocalSilo));
    }

    [Fact]
    public async Task ApplyLoadStatistics_PreCanceledToken_PreservesLocalState()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var ns = rig.ServiceProvider.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();
        var value = ns.CreateValue(rig.LocalSilo, rig.InitialStatistics);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ns.ApplyValueAsync(value, cancellation.Token));

        Assert.Empty(rig.Publisher.PeriodicStatistics);
    }

    [Fact]
    public async Task ApplyLoadStatistics_RequiresFullValue()
    {
        using var rig = CreateTestRig(TimeSpan.Zero);
        var ns = rig.ServiceProvider.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();
        var fullValue = ns.CreateValue(rig.LocalSilo, rig.InitialStatistics);
        var delta = new DisseminationValue(fullValue.Key, 1, fullValue.ToVersion, fullValue.Payload);

        Assert.Equal(DisseminationApplyResult.Rejected,
            await ns.ApplyValueAsync(delta, TestContext.Current.CancellationToken));
        Assert.Empty(rig.Publisher.PeriodicStatistics);
    }

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

    private static TestRig CreateTestRig(
        TimeSpan refreshTime,
        ITimerRegistry? timerRegistry = null,
        bool enableDissemination = false)
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
        var dissemination = Substitute.For<IDisseminationService>();
        dissemination.Publish(
            Arg.Any<IDisseminationNamespace>(), Arg.Any<DisseminationKey>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddMetrics();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
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
        services.AddSingleton(dissemination);
        services.AddSingleton(Substitute.For<IActivationWorkingSet>());
        services.AddSingleton(environmentStatistics);
        services.AddSingleton<IOptions<LoadSheddingOptions>>(loadSheddingOptions);
        services.AddOptions<DeploymentLoadPublisherOptions>().Configure(options =>
        {
            options.DeploymentLoadPublisherRefreshTime = refreshTime;
            options.Dissemination.Enabled = enableDissemination;
        });
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
        services.AddSingleton<DeploymentLoadStatisticsDisseminationNamespace>();
        var serviceProvider = services.BuildServiceProvider();
        return new(serviceProvider, serviceProvider.GetRequiredService<DeploymentLoadPublisher>(), directTarget, control, initialStatistics, localSilo, dissemination);
    }

    private sealed record TestRig(
        ServiceProvider ServiceProvider,
        DeploymentLoadPublisher Publisher,
        IDeploymentLoadPublisher DirectTarget,
        ISiloControl Control,
        SiloRuntimeStatistics InitialStatistics,
        SiloAddress LocalSilo,
        IDisseminationService Dissemination) : IDisposable
    {
        public void Dispose() => ServiceProvider.Dispose();
    }
}
