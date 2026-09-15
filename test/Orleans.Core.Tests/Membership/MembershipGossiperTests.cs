using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Scheduler;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dissemination")]
public class MembershipGossiperTests
{
    [Fact]
    public async Task GossipToRemoteSilos_EligibleLocalStatus_StartsDirectAndDisseminationBeforeEitherCompletes()
    {
        var directStarted = NewBarrier();
        var releaseDirect = NewBarrier();
        var directCompleted = NewBarrier();
        var disseminationStarted = NewBarrier();
        var releaseDissemination = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DisseminationKey? publishedKey = null;
        long? publishedVersion = null;
        CancellationToken publishedToken = default;

        using var rig = CreateDisseminationTestRig(
            async (_, cancellationToken) =>
            {
                directStarted.TrySetResult();
                await releaseDirect.Task.WaitAsync(cancellationToken);
                directCompleted.TrySetResult();
            },
            async (key, version, cancellationToken) =>
            {
                publishedKey = key;
                publishedVersion = version;
                publishedToken = cancellationToken;
                disseminationStarted.TrySetResult();
                return await releaseDissemination.Task.WaitAsync(cancellationToken);
            });
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Active);
        var gossipTask = rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], snapshot, rig.LocalSilo, SiloStatus.ShuttingDown, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(directStarted.Task, disseminationStarted.Task).WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(gossipTask.IsCompleted);
            Assert.False(directCompleted.Task.IsCompleted);
            Assert.Equal(1, rig.DisseminationServiceResolutionCount);
        }
        finally
        {
            releaseDirect.TrySetResult();
            releaseDissemination.TrySetResult(true);
        }

        await gossipTask;
        await directCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot, TestContext.Current.CancellationToken);
        await rig.DisseminationService.Received(1).Publish(
            Arg.Any<IDisseminationNamespace>(), DisseminationKey.Default, snapshot.Version.Value, TestContext.Current.CancellationToken);
        Assert.Equal(DisseminationKey.Default, publishedKey);
        Assert.Equal(snapshot.Version.Value, publishedVersion);
        Assert.Equal(TestContext.Current.CancellationToken, publishedToken);
    }

    [Fact]
    public async Task GossipToRemoteSilos_IneligibleLocalStatus_SkipsDisseminationAndCompletesDirectGossip()
    {
        MembershipTableSnapshot? deliveredSnapshot = null;
        using var rig = CreateDisseminationTestRig(
            (snapshot, _) =>
            {
                deliveredSnapshot = snapshot;
                return Task.CompletedTask;
            },
            (_, _, _) => throw new Xunit.Sdk.XunitException("Ineligible membership must not publish via dissemination."));
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Dead);

        await rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], snapshot, rig.LocalSilo, SiloStatus.Dead, TestContext.Current.CancellationToken);

        Assert.Same(snapshot, deliveredSnapshot);
        await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.DisseminationServiceResolutionCount);
        Assert.Empty(rig.DisseminationService.ReceivedCalls());
    }

    [Fact]
    public async Task GossipToRemoteSilos_CallerCancellation_CancelsDirectGossipAndDisseminationPublish()
    {
        var directStarted = NewBarrier();
        var disseminationStarted = NewBarrier();
        CancellationToken directToken = default;
        CancellationToken publishedToken = default;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var rig = CreateDisseminationTestRig(
            async (_, cancellationToken) =>
            {
                directToken = cancellationToken;
                directStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            async (_, _, cancellationToken) =>
            {
                publishedToken = cancellationToken;
                disseminationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Active);
        var gossipTask = rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], snapshot, rig.LocalSilo, SiloStatus.Stopping, cancellation.Token);

        try
        {
            await Task.WhenAll(directStarted.Task, disseminationStarted.Task).WaitAsync(TestContext.Current.CancellationToken);
            await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot, cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gossipTask);
        Assert.True(gossipTask.IsCanceled);
        Assert.Equal(cancellation.Token, directToken);
        Assert.Equal(cancellation.Token, publishedToken);
        Assert.True(directToken.IsCancellationRequested);
        Assert.True(publishedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task GossipToRemoteSilos_PreCanceled_DoesNotSendOrPublish()
    {
        using var cancellation = new CancellationTokenSource();
        using var rig = CreateDisseminationTestRig(
            (_, _) => Task.CompletedTask,
            (_, _, _) => ValueTask.FromResult(true));
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Active);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], snapshot, rig.LocalSilo, SiloStatus.Active, cancellation.Token));

        Assert.Empty(rig.RemoteMembershipService.ReceivedCalls());
        Assert.Empty(rig.DisseminationService.ReceivedCalls());
    }

    [Fact]
    public async Task MembershipGossiperStartsDirectGossipBeforeDissemination()
    {
        var directStarted = NewBarrier();
        var releaseDirect = NewBarrier();
        var disseminationStarted = NewBarrier();
        var releaseDissemination = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutionOrder = new List<string>();
        using var rig = CreateDisseminationTestRig(
            async (_, cancellationToken) =>
            {
                directStarted.TrySetResult();
                await releaseDirect.Task.WaitAsync(cancellationToken);
            },
            async (_, _, cancellationToken) =>
            {
                disseminationStarted.TrySetResult();
                return await releaseDissemination.Task.WaitAsync(cancellationToken);
            },
            resolutionOrder);
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Active);
        var gossipTask = rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], snapshot, rig.LocalSilo, SiloStatus.ShuttingDown, TestContext.Current.CancellationToken);

        try
        {
            await Task.WhenAll(directStarted.Task, disseminationStarted.Task).WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["MembershipSystemTargetResolved", "DisseminationServiceResolved"], resolutionOrder);
        }
        finally
        {
            releaseDirect.TrySetResult();
            releaseDissemination.TrySetResult(true);
        }

        await gossipTask;
        await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot, TestContext.Current.CancellationToken);
        await rig.DisseminationService.Received(1).Publish(
            Arg.Any<IDisseminationNamespace>(), DisseminationKey.Default, snapshot.Version.Value, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MembershipGossiperDirectDeliveryRemainsAuthoritativeWhenDisseminationFails()
    {
        var disseminationFailure = new InvalidOperationException("Simulated dissemination failure.");
        DisseminationKey? publishedKey = null;
        long? publishedVersion = null;
        using var rig = CreateDisseminationTestRig(
            (_, _) => Task.CompletedTask,
            (key, version, _) =>
            {
                publishedKey = key;
                publishedVersion = version;
                throw disseminationFailure;
            });
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Active);

        await rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], snapshot, rig.LocalSilo, SiloStatus.ShuttingDown, TestContext.Current.CancellationToken);

        Assert.Equal(DisseminationKey.Default, publishedKey);
        Assert.Equal(snapshot.Version.Value, publishedVersion);
        await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot, TestContext.Current.CancellationToken);
        await rig.DisseminationService.Received(1).Publish(
            Arg.Any<IDisseminationNamespace>(), DisseminationKey.Default, snapshot.Version.Value, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MembershipGossiperPreservesEligibilityAndCallerCancellation()
    {
        var directStarted = NewBarrier();
        CancellationToken directToken = default;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var rig = CreateDisseminationTestRig(
            async (_, cancellationToken) =>
            {
                directToken = cancellationToken;
                directStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            (_, _, _) => throw new Xunit.Sdk.XunitException("Ineligible membership must not publish via dissemination."));
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Dead);
        var gossipTask = rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], snapshot, rig.LocalSilo, SiloStatus.Dead, cancellation.Token);

        try
        {
            await directStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
        }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gossipTask);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, directToken);
        Assert.Equal(0, rig.DisseminationServiceResolutionCount);
        Assert.Empty(rig.DisseminationService.ReceivedCalls());
        await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot, cancellation.Token);
    }

    [Theory]
    [InlineData("Gossip")]
    [InlineData("Probe")]
    [InlineData("IndirectProbe")]
    public async Task MembershipRpc_CallerCancellationReachesRemoteAndCompletesRequest(string operation)
    {
        using var rig = CreateTestRig();
        using var cancellation = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Remote.MembershipChangeNotification(Arg.Any<MembershipTableSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(call => StartRequest(call.ArgAt<CancellationToken>(1)));
        rig.Remote.Ping(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => StartRequest(call.ArgAt<CancellationToken>(1)));
        rig.Remote.ProbeIndirectly(Arg.Any<SiloAddress>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => StartRequest(call.ArgAt<CancellationToken>(3)));

        var request = Invoke(rig, operation, cancellation.Token);
        var remoteToken = await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(cancellation.Token, remoteToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(request.IsCanceled);

        async Task<IndirectProbeResponse> StartRequest(CancellationToken token)
        {
            requestStarted.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The infinite delay completes by cancellation.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledOperation_CancellationPreservesCalleeResult(bool sameContext)
    {
        using var rig = CreateTestRig();
        using var cancellation = new CancellationTokenSource();
        var target = rig.ServiceProvider.GetRequiredService<MembershipSystemTarget>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? operation = null;
        if (sameContext)
        {
            await target.RunOrQueueTask(() =>
            {
                operation = target.RunOrQueueTask(CompleteOnCancellation, cancellation.Token);
                return Task.CompletedTask;
            });
        }
        else
        {
            operation = target.RunOrQueueTask(CompleteOnCancellation, cancellation.Token);
        }

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.NotNull(operation);
        Assert.Equal(42, await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(operation.IsCompletedSuccessfully);

        async Task<int> CompleteOnCancellation(CancellationToken token)
        {
            started.SetResult();
            await token.WhenCancelled();
            return 42;
        }
    }

    [Theory]
    [InlineData("Gossip")]
    [InlineData("Probe")]
    [InlineData("IndirectProbe")]
    public async Task MembershipRpc_PreCanceled_SendsNoRequests(string operation)
    {
        using var rig = CreateTestRig();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(rig, operation, cancellation.Token));

        Assert.Empty(rig.Remote.ReceivedCalls());
    }

    private static Task Invoke(TestRig rig, string operation, CancellationToken cancellationToken) => operation switch
    {
        "Gossip" => rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo], new MembershipTableSnapshot(new MembershipVersion(1), ImmutableDictionary<SiloAddress, MembershipEntry>.Empty),
            rig.LocalSilo, SiloStatus.Stopping, cancellationToken),
        "Probe" => rig.Prober.Probe(rig.RemoteSilo, 1, cancellationToken),
        "IndirectProbe" => rig.Prober.ProbeIndirectly(rig.RemoteSilo, rig.LocalSilo, TimeSpan.FromSeconds(5), 1, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static TestRig CreateTestRig()
    {
        var localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var remoteSilo = SiloAddress.FromParsableString("127.0.0.1:200@100");
        var localDetails = Substitute.For<ILocalSiloDetails>();
        localDetails.SiloAddress.Returns(localSilo);
        var remote = Substitute.For<IMembershipService>();
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, Arg.Any<SiloAddress>()).Returns(remote);
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<CatalogInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        services.AddSingleton<GrainInstruments>();
        services.AddSingleton<MessagingInstruments>();
        services.AddSingleton<MessagingProcessingInstruments>();
        services.AddSingleton<MembershipSystemTarget>(provider =>
        {
            var shared = new SystemTargetShared(
                runtimeClient: null!,
                localDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                new ActivationDirectory(provider.GetRequiredService<CatalogInstruments>()),
                provider.GetRequiredService<SchedulerInstruments>(),
                provider.GetRequiredService<GrainInstruments>(),
                provider.GetRequiredService<MessagingInstruments>(),
                provider.GetRequiredService<MessagingProcessingInstruments>());
            return new MembershipSystemTarget(
                Substitute.For<IMembershipManager>(),
                NullLogger<MembershipSystemTarget>.Instance,
                grainFactory,
                provider.GetRequiredService<MessagingInstruments>(),
                shared,
                TimeProvider.System);
        });
        var serviceProvider = services.BuildServiceProvider();
        return new(serviceProvider,
            new MembershipGossiper(serviceProvider, localDetails, NullLogger<MembershipGossiper>.Instance),
            new RemoteSiloProber(serviceProvider), remote, localSilo, remoteSilo);
    }

    private static DisseminationTestRig CreateDisseminationTestRig(
        Func<MembershipTableSnapshot, CancellationToken, Task> directGossip,
        Func<DisseminationKey, long, CancellationToken, ValueTask<bool>> disseminationPublish,
        List<string>? resolutionOrder = null)
    {
        var localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var remoteSilo = SiloAddress.FromParsableString("127.0.0.1:200@100");
        var localDetails = Substitute.For<ILocalSiloDetails>();
        localDetails.SiloAddress.Returns(localSilo);
        var remote = Substitute.For<IMembershipService>();
        remote.MembershipChangeNotification(Arg.Any<MembershipTableSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(call => directGossip(call.ArgAt<MembershipTableSnapshot>(0), call.ArgAt<CancellationToken>(1)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, Arg.Any<SiloAddress>()).Returns(remote);
        var membershipManager = Substitute.For<IMembershipManager>();
        var options = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
        options.CurrentValue.Returns(new ClusterMembershipOptions
        {
            Dissemination = new DisseminationNamespaceOptions { Enabled = true },
        });
        var disseminationNamespace = new MembershipDisseminationNamespace(membershipManager, options, serializer: null!);
        var disseminationService = Substitute.For<IDisseminationService>();
        disseminationService.Publish(
            Arg.Any<IDisseminationNamespace>(), Arg.Any<DisseminationKey>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => disseminationPublish(
                call.ArgAt<DisseminationKey>(1), call.ArgAt<long>(2), call.ArgAt<CancellationToken>(3)));
        var disseminationServiceResolutionCount = 0;
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<CatalogInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        services.AddSingleton<GrainInstruments>();
        services.AddSingleton<MessagingInstruments>();
        services.AddSingleton<MessagingProcessingInstruments>();
        services.AddSingleton(localDetails);
        services.AddSingleton(disseminationNamespace);
        services.AddSingleton<IDisseminationService>(_ =>
        {
            disseminationServiceResolutionCount++;
            resolutionOrder?.Add("DisseminationServiceResolved");
            return disseminationService;
        });
        services.AddSingleton<MembershipSystemTarget>(provider =>
        {
            resolutionOrder?.Add("MembershipSystemTargetResolved");
            var shared = new SystemTargetShared(
                runtimeClient: null!,
                localDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                new ActivationDirectory(provider.GetRequiredService<CatalogInstruments>()),
                provider.GetRequiredService<SchedulerInstruments>(),
                provider.GetRequiredService<GrainInstruments>(),
                provider.GetRequiredService<MessagingInstruments>(),
                provider.GetRequiredService<MessagingProcessingInstruments>());
            return new MembershipSystemTarget(
                membershipManager,
                NullLogger<MembershipSystemTarget>.Instance,
                grainFactory,
                provider.GetRequiredService<MessagingInstruments>(),
                shared,
                TimeProvider.System);
        });
        var serviceProvider = services.BuildServiceProvider();
        return new(
            serviceProvider,
            new MembershipGossiper(serviceProvider, localDetails, NullLogger<MembershipGossiper>.Instance),
            remote,
            disseminationService,
            localSilo,
            remoteSilo,
            () => disseminationServiceResolutionCount);
    }

    private static MembershipTableSnapshot CreateSnapshot(SiloAddress localSilo, SiloAddress remoteSilo, SiloStatus localStatus)
    {
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new(
            new MembershipVersion(7),
            ImmutableDictionary<SiloAddress, MembershipEntry>.Empty
                .Add(localSilo, CreateEntry(localSilo, localStatus, startTime))
                .Add(remoteSilo, CreateEntry(remoteSilo, SiloStatus.Active, startTime)));
    }

    private static MembershipEntry CreateEntry(SiloAddress silo, SiloStatus status, DateTime startTime) => new()
    {
        SiloAddress = silo,
        Status = status,
        HostName = "localhost",
        SiloName = silo.ToParsableString(),
        StartTime = startTime,
        IAmAliveTime = startTime,
    };

    private static TaskCompletionSource NewBarrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record DisseminationTestRig(
        ServiceProvider ServiceProvider,
        MembershipGossiper Gossiper,
        IMembershipService RemoteMembershipService,
        IDisseminationService DisseminationService,
        SiloAddress LocalSilo,
        SiloAddress RemoteSilo,
        Func<int> DisseminationServiceResolutions) : IDisposable
    {
        public int DisseminationServiceResolutionCount => DisseminationServiceResolutions();

        public void Dispose() => ServiceProvider.Dispose();
    }

    private sealed record TestRig(
        ServiceProvider ServiceProvider,
        MembershipGossiper Gossiper,
        RemoteSiloProber Prober,
        IMembershipService Remote,
        SiloAddress LocalSilo,
        SiloAddress RemoteSilo) : IDisposable
    {
        public void Dispose() => ServiceProvider.Dispose();
    }
}
