using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
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
[TestArea("Runtime")]
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

        using var rig = CreateTestRig(
            async _ =>
            {
                directStarted.TrySetResult();
                await releaseDirect.Task;
                directCompleted.TrySetResult();
            },
            async (key, version, cancellationToken) =>
            {
                publishedKey = key;
                publishedVersion = version;
                publishedToken = cancellationToken;
                disseminationStarted.TrySetResult();
                return await releaseDissemination.Task;
            });
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Active);
        var partners = new List<SiloAddress> { rig.RemoteSilo };

        var gossipTask = rig.Gossiper.GossipToRemoteSilos(
            partners,
            snapshot,
            rig.LocalSilo,
            SiloStatus.ShuttingDown,
            CancellationToken.None);

        await Task.WhenAll(directStarted.Task, disseminationStarted.Task);
        Assert.False(gossipTask.IsCompleted);
        Assert.False(directCompleted.Task.IsCompleted);
        Assert.Equal(1, rig.DisseminationServiceResolutionCount);

        releaseDirect.TrySetResult();
        releaseDissemination.TrySetResult(true);
        await gossipTask;
        await directCompleted.Task;

        await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot);
        await rig.DisseminationService.Received(1).Publish(
            Arg.Any<IDisseminationNamespace>(),
            DisseminationKey.Default,
            snapshot.Version.Value,
            CancellationToken.None);
        Assert.Equal(DisseminationKey.Default, publishedKey);
        Assert.Equal(snapshot.Version.Value, publishedVersion);
        Assert.Equal(CancellationToken.None, publishedToken);
    }

    [Fact]
    public async Task GossipToRemoteSilos_IneligibleLocalStatus_SkipsDisseminationAndCompletesDirectGossip()
    {
        var directStarted = NewBarrier();
        MembershipTableSnapshot? deliveredSnapshot = null;

        using var rig = CreateTestRig(
            snapshot =>
            {
                deliveredSnapshot = snapshot;
                directStarted.TrySetResult();
                return Task.CompletedTask;
            },
            (_, _, _) => throw new Xunit.Sdk.XunitException("Ineligible membership must not publish via dissemination."));
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Dead);

        var gossipTask = rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo],
            snapshot,
            rig.LocalSilo,
            SiloStatus.Dead,
            CancellationToken.None);

        await directStarted.Task;
        await gossipTask;

        Assert.Same(snapshot, deliveredSnapshot);
        await rig.RemoteMembershipService.Received(1).MembershipChangeNotification(snapshot);
        Assert.Equal(0, rig.DisseminationServiceResolutionCount);
        Assert.Empty(rig.DisseminationService.ReceivedCalls());
    }

    [Fact]
    public async Task GossipToRemoteSilos_CallerCancellation_CancelsDirectWrapperAndDisseminationPublish()
    {
        var directStarted = NewBarrier();
        var releaseDirect = NewBarrier();
        var directCompleted = NewBarrier();
        var disseminationStarted = NewBarrier();
        var disseminationCancellationObserved = NewBarrier();
        var disseminationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken publishedToken = default;

        using var cancellation = new CancellationTokenSource();
        using var rig = CreateTestRig(
            async _ =>
            {
                directStarted.TrySetResult();
                await releaseDirect.Task;
                directCompleted.TrySetResult();
            },
            (_, _, cancellationToken) =>
            {
                publishedToken = cancellationToken;
                cancellationToken.Register(() =>
                {
                    disseminationCancellationObserved.TrySetResult();
                    disseminationCompletion.TrySetCanceled(cancellationToken);
                });
                disseminationStarted.TrySetResult();
                return new ValueTask<bool>(disseminationCompletion.Task);
            });
        var snapshot = CreateSnapshot(rig.LocalSilo, rig.RemoteSilo, SiloStatus.Active);
        var gossipTask = rig.Gossiper.GossipToRemoteSilos(
            [rig.RemoteSilo],
            snapshot,
            rig.LocalSilo,
            SiloStatus.Stopping,
            cancellation.Token);

        try
        {
            await Task.WhenAll(directStarted.Task, disseminationStarted.Task);
            cancellation.Cancel();

            await disseminationCancellationObserved.Task;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gossipTask);
            Assert.Equal(cancellation.Token, publishedToken);
            Assert.True(publishedToken.IsCancellationRequested);
            Assert.False(directCompleted.Task.IsCompleted);
        }
        finally
        {
            releaseDirect.TrySetResult();
            await directCompleted.Task;
        }
    }

    private static MembershipGossiperTestRig CreateTestRig(
        Func<MembershipTableSnapshot, Task> directGossip,
        Func<DisseminationKey, long, CancellationToken, ValueTask<bool>> disseminationPublish)
    {
        var localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
        var remoteSilo = SiloAddress.FromParsableString("127.0.0.1:200@100");
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);
        var remoteMembershipService = Substitute.For<IMembershipService>();
        remoteMembershipService
            .MembershipChangeNotification(Arg.Any<MembershipTableSnapshot>())
            .Returns(call => directGossip(call.ArgAt<MembershipTableSnapshot>(0)));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory
            .GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, Arg.Any<SiloAddress>())
            .Returns(remoteMembershipService);
        var membershipManager = Substitute.For<IMembershipManager>();
        var options = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
        options.CurrentValue.Returns(new ClusterMembershipOptions
        {
            Dissemination = new DisseminationNamespaceOptions { Enabled = true },
        });
        var disseminationNamespace = new MembershipDisseminationNamespace(membershipManager, options, serializer: null!);
        var disseminationService = Substitute.For<IDisseminationService>();
        disseminationService
            .Publish(
                Arg.Any<IDisseminationNamespace>(),
                Arg.Any<DisseminationKey>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(call => disseminationPublish(
                call.ArgAt<DisseminationKey>(1),
                call.ArgAt<long>(2),
                call.ArgAt<CancellationToken>(3)));

        var disseminationServiceResolutionCount = 0;
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<CatalogInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        services.AddSingleton<GrainInstruments>();
        services.AddSingleton<MessagingInstruments>();
        services.AddSingleton<MessagingProcessingInstruments>();
        services.AddSingleton(localSiloDetails);
        services.AddSingleton(disseminationNamespace);
        services.AddSingleton<IDisseminationService>(_ =>
        {
            disseminationServiceResolutionCount++;
            return disseminationService;
        });
        services.AddSingleton<MembershipSystemTarget>(serviceProvider =>
        {
            var shared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                new ActivationDirectory(serviceProvider.GetRequiredService<CatalogInstruments>()),
                serviceProvider.GetRequiredService<SchedulerInstruments>(),
                serviceProvider.GetRequiredService<GrainInstruments>(),
                serviceProvider.GetRequiredService<MessagingInstruments>(),
                serviceProvider.GetRequiredService<MessagingProcessingInstruments>());
            return new MembershipSystemTarget(
                membershipManager,
                NullLogger<MembershipSystemTarget>.Instance,
                grainFactory,
                serviceProvider.GetRequiredService<MessagingInstruments>(),
                shared,
                new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        });
        var serviceProvider = services.BuildServiceProvider();
        var gossiper = new MembershipGossiper(
            serviceProvider,
            localSiloDetails,
            NullLogger<MembershipGossiper>.Instance);

        return new(
            serviceProvider,
            gossiper,
            remoteMembershipService,
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

    private sealed record MembershipGossiperTestRig(
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
}
