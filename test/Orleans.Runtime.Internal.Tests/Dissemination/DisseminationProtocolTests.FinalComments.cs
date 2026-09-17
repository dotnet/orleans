#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public async Task MembershipInventoryOnlyRepairConvergesWithoutHeartbeat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40301);
        var sourceAddress = CreateSilo(40302);
        var live = CreateMembershipEntry(
            CreateSilo(40303), SiloStatus.Active, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(20));
        var retired = CreateMembershipEntry(CreateSilo(40304), SiloStatus.Dead, DateTime.UnixEpoch);
        var previous = CreateMembershipSnapshot(2, live, retired);
        var incoming = CreateMembershipSnapshot(2, live.WithIAmAliveTime(DateTime.UnixEpoch.AddSeconds(10)));
        var expected = CreateMembershipSnapshot(2, live);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        using var receiverManager = CreateMembershipReviewManager(local, out _);
        using var sourceManager = CreateMembershipReviewManager(sourceAddress, out _);
        await ((IMembershipManager)receiverManager).ProcessGossipSnapshot(previous, cancellationToken);
        await ((IMembershipManager)sourceManager).ProcessGossipSnapshot(expected, cancellationToken);
        var receiver = CreateMembershipNamespace((IMembershipManager)receiverManager, serializer);
        var source = CreateMembershipNamespace((IMembershipManager)sourceManager, serializer);
        var before = Assert.Single(receiver.Digests);
        var update = new MembershipTableSnapshotUpdate { Snapshot = incoming };
        var value = new DisseminationValue(DisseminationKey.Default, 0, 2, serializer.SerializeToArray(update));

        Assert.Equal(DisseminationApplyResult.Applied, await receiver.ApplyValueAsync(value, cancellationToken));
        AssertMembershipState(expected, receiverManager.MembershipTableSnapshot);
        Assert.NotEqual(before.Fingerprint, Assert.Single(receiver.Digests).Fingerprint);
        Assert.Equal(before.Version, Assert.Single(receiver.Digests).Version);
        Assert.Equal(Assert.Single(source.Digests), Assert.Single(receiver.Digests));
        Assert.Equal(DisseminationApplyResult.Duplicate, await receiver.ApplyValueAsync(value, cancellationToken));
        var staleInventory = new DisseminationValue(
            DisseminationKey.Default, 0, 2, serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = previous }));
        Assert.Equal(DisseminationApplyResult.Duplicate, await receiver.ApplyValueAsync(staleInventory, cancellationToken));
        AssertMembershipState(expected, receiverManager.MembershipTableSnapshot);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(10), incoming.Entries[live.SiloAddress].IAmAliveTime);

        var protocol = CreateProtocol(new FakeTransport(sourceAddress, local), [source]);
        try
        {
            var response = await protocol.ReceiveAntiEntropy(new DisseminationAntiEntropyRequest
            {
                Sender = local,
                Digests = new() { [source.Name] = [Assert.Single(receiver.Digests)] },
            }, cancellationToken);
            Assert.Empty(GetAntiEntropyResponseValues(response));
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task MembershipInventoryCleanupRespectsManagerLocalEntryRetention()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40311);
        var peer = CreateSilo(40312);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        using var manager = CreateMembershipReviewManager(local, out _);
        var localEntry = manager.MembershipTableSnapshot.Entries[local].WithStatus(SiloStatus.Dead);
        var previous = CreateMembershipSnapshot(
            2, localEntry, CreateMembershipEntry(peer, SiloStatus.Active, DateTime.UnixEpoch));
        await ((IMembershipManager)manager).ProcessGossipSnapshot(previous, cancellationToken);
        var current = manager.MembershipTableSnapshot;
        var incoming = CreateMembershipSnapshot(2, previous.Entries[peer]);
        var ns = CreateMembershipNamespace((IMembershipManager)manager, serializer);
        var value = new DisseminationValue(
            DisseminationKey.Default, 0, 2, serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = incoming }));

        Assert.True(incoming.IsSuccessorTo(current));
        Assert.Equal(DisseminationApplyResult.Duplicate, await ns.ApplyValueAsync(value, cancellationToken));
        Assert.Same(current, manager.MembershipTableSnapshot);
        Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.Entries[local].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisseminationStopReportsCallerCancellationAfterCleanup(bool cancelCaller)
    {
        var local = CreateSilo(40321);
        var transport = new FakeTransport(local, CreateSilo(40322));
        var options = new ReviewOptionsMonitor(new DisseminationOptions { Enabled = false });
        var clock = new ReviewTimeProvider();
        var details = new FakeLocalSiloDetails(local);
        var target = new DisseminationSystemTarget(
            details,
            transport.GrainFactory,
            new DisseminationMembership(transport.MembershipManager, details, Options.Create(options.CurrentValue)),
            options,
            [new FakeNamespace(local)],
            clock,
            NullLogger<DisseminationProtocol>.Instance,
            NullLogger<DisseminationBroadcastQueue>.Instance,
            CreatePhase4SystemTargetShared(local));
        var lifecycle = Substitute.For<ISiloLifecycle>();
        ILifecycleObserver? observer = null;
        lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>()).Returns(call =>
        {
            observer = call.ArgAt<ILifecycleObserver>(2);
            return new ReviewSubscription(static () => { });
        });
        ((ILifecycleParticipant<ISiloLifecycle>)target).Participate(lifecycle);
        Assert.NotNull(observer);
        await observer.OnStart(TestContext.Current.CancellationToken);
        await clock.WaitForChange(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
        await clock.WaitForChange(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (cancelCaller)
        {
            caller.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => observer.OnStop(caller.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            await observer.OnStop(caller.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        await options.Unsubscribed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, options.SubscriptionCount);
        Assert.Empty(transport.AntiEntropyRequests);
    }
}
