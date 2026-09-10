using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;
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
    [Theory]
    [InlineData("Gossip")]
    [InlineData("Probe")]
    [InlineData("IndirectProbe")]
    public async Task MembershipRpc_CallerCancellationBoundsWaitAndReachesRemote(string operation)
    {
        using var rig = CreateTestRig();
        using var cancellation = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<IndirectProbeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Assert.False(response.Task.IsCompleted);
        response.SetException(new InvalidOperationException("Late membership RPC failure"));

        Task<IndirectProbeResponse> StartRequest(CancellationToken token)
        {
            requestStarted.TrySetResult(token);
            return response.Task;
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
            new MembershipGossiper(serviceProvider, NullLogger<MembershipGossiper>.Instance),
            new RemoteSiloProber(serviceProvider), remote, localSilo, remoteSilo);
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
