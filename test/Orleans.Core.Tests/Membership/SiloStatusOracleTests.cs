using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class SiloStatusOracleTests
{
    [Fact]
    public void LocalStatusChange_InvalidatesActiveSiloCache()
    {
        var localSilo = CreateSilo(11111);
        var remoteSilo = CreateSilo(11112);
        var snapshot = new MembershipTableSnapshot(
            new MembershipVersion(1),
            ImmutableDictionary<SiloAddress, MembershipEntry>.Empty
                .Add(localSilo, CreateEntry(localSilo, SiloStatus.Active))
                .Add(remoteSilo, CreateEntry(remoteSilo, SiloStatus.Active)));
        var membershipManager = Substitute.For<IMembershipManager>();
        membershipManager.CurrentSnapshot.Returns(snapshot);
        membershipManager.LocalSiloStatus.Returns(SiloStatus.Active);
        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(localSilo);
        var listenerManager = new SiloStatusListenerManager(
            membershipManager,
            localSiloDetails,
            NullLogger<SiloStatusListenerManager>.Instance,
            Substitute.For<IFatalErrorHandler>());
        var oracle = new SiloStatusOracle(
            localSiloDetails,
            membershipManager,
            NullLogger<SiloStatusOracle>.Instance,
            listenerManager);
        var listener = Substitute.For<ISiloStatusListener>();
        Assert.True(listenerManager.Subscribe(listener));

        Assert.Equal(new[] { localSilo, remoteSilo }.Order(), oracle.GetActiveSilos().Order());

        membershipManager.LocalSiloStatusChanged += Raise.Event<Action<SiloStatus>>(SiloStatus.ShuttingDown);

        Assert.Equal(new[] { remoteSilo }, oracle.GetActiveSilos());
        Assert.Equal(SiloStatus.ShuttingDown, oracle.GetApproximateSiloStatus(localSilo));
        Assert.DoesNotContain(localSilo, oracle.GetApproximateSiloStatuses(onlyActive: true));
        listener.Received(1).SiloStatusChangeNotification(localSilo, SiloStatus.ShuttingDown);

        membershipManager.LocalSiloStatusChanged += Raise.Event<Action<SiloStatus>>(SiloStatus.Active);

        Assert.Equal(new[] { remoteSilo }, oracle.GetActiveSilos());
        Assert.Equal(SiloStatus.ShuttingDown, oracle.GetApproximateSiloStatus(localSilo));
        listener.DidNotReceive().SiloStatusChangeNotification(localSilo, SiloStatus.Active);
    }

    private static MembershipEntry CreateEntry(SiloAddress silo, SiloStatus status) =>
        new()
        {
            SiloAddress = silo,
            SiloName = silo.ToString(),
            HostName = "localhost",
            Status = status,
        };

    private static SiloAddress CreateSilo(int port) =>
        SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), SiloAddress.AllocateNewGeneration());
}
