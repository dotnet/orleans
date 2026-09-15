#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
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
    public async Task LoadRepairReusesOnlySuccessfullyAppliedPayloads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = CreatePhase5DeploymentLoadPublisherHarness();
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var ns = new DeploymentLoadStatisticsDisseminationNamespace(
            harness.Publisher, new TestOptionsMonitor<DeploymentLoadPublisherOptions>(new()), serializer);
        var first = CreatePhase5Statistics(1);
        var value = new DisseminationValue(harness.ActiveOne, 0, first.DateTime.Ticks, serializer.SerializeToArray(first));

        Assert.Equal(DisseminationApplyResult.Applied, await ns.ApplyValueAsync(value, cancellationToken));
        Assert.True(value.Payload.Equals(Repair().Payload));

        var duplicate = new DisseminationValue(value.Key, 0, value.ToVersion, value.Payload.ToArray());
        Assert.Equal(DisseminationApplyResult.Duplicate, await ns.ApplyValueAsync(duplicate, cancellationToken));
        Assert.True(value.Payload.Equals(Repair().Payload));

        var second = CreatePhase5Statistics(2);
        var replacement = new DisseminationValue(value.Key, 0, second.DateTime.Ticks, serializer.SerializeToArray(second));
        Assert.Equal(DisseminationApplyResult.Applied, await ns.ApplyValueAsync(replacement, cancellationToken));
        Assert.True(replacement.Payload.Equals(Repair().Payload));
        Assert.Equal(DisseminationApplyResult.Obsolete, await ns.ApplyValueAsync(value, cancellationToken));
        Assert.True(replacement.Payload.Equals(Repair().Payload));

        var malformed = new DisseminationValue(value.Key, 0, replacement.ToVersion + 1, replacement.Payload);
        Assert.Equal(DisseminationApplyResult.Rejected, await ns.ApplyValueAsync(malformed, cancellationToken));
        Assert.True(replacement.Payload.Equals(Repair().Payload));

        DisseminationValue Repair()
        {
            var repair = ns.CreateRepair(new(value.Key, null, null, 1, 1024, 1024));
            Assert.Equal(DisseminationRepairStatus.Produced, repair.Status);
            return Assert.Single(repair.Values);
        }
    }

    [Fact]
    public void MembershipKeyInventoryRequiresNoSnapshotOrFingerprint()
    {
        var manager = Substitute.For<IMembershipManager>();
        manager.CurrentSnapshot.Returns(_ => throw new InvalidOperationException("Key enumeration must not read membership state."));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var ns = CreateMembershipNamespace(manager, services.GetRequiredService<Serializer>());

        Assert.Equal(DisseminationKey.Default, Assert.Single(ns.Keys));
    }

    [Fact]
    public async Task QueuePruningUsesKeyInventoryWithoutBuildingDigests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40601);
        var peer = CreateSilo(40602);
        var transport = new FakeTransport(local, peer);
        var ns = new InventoryOnlyNamespace(local);
        ns.Inner.SetValue("value", 1);
        var queue = CreateBroadcastQueue(transport, [ns], timeProvider: new FakeTimeProvider());
        var membership = new DisseminationMembership(
            transport.MembershipManager,
            new FakeLocalSiloDetails(local),
            Microsoft.Extensions.Options.Options.Create(new DisseminationOptions()));
        try
        {
            Assert.True(queue.Notify(peer, ns, "value"));
            await queue.Prune(membership.CurrentSnapshots, cancellationToken);
            await queue.FlushPendingBroadcast(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await queue.Prune(membership.CurrentSnapshots, cancellationToken);

            Assert.Equal(1, Assert.Single(GetBroadcastValues(Assert.Single(transport.BroadcastBatches).Batch)).Value.ToVersion);
        }
        finally
        {
            await queue.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    [Fact]
    public async Task ReceiveBudgetRetainsCursorForZeroByteSuffixAndIgnoresEmptyNamespaces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40611);
        var peer = CreateSilo(40612);
        var ns = new ProtocolReviewNamespace(local);
        var empty = new FakeNamespace(local, "empty");
        ns.ApplyHandler = (value, _) =>
        {
            ns.Inner.SetValue(value.Key, value.ToVersion);
            return new(DisseminationApplyResult.Applied);
        };
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns, empty], options =>
        {
            options.MaxBatchBytes = 8;
            options.MaxBatchItems = 2;
        });
        var batch = new DisseminationBroadcastBatch
        {
            Sender = peer,
            Values = new()
            {
                [ns.Name] =
                [
                    CreateDisseminationValue(peer, new("first", 0, 1, new byte[8])),
                    CreateDisseminationValue(peer, new("second", 0, 1, ReadOnlyMemory<byte>.Empty)),
                ],
                [empty.Name] = [],
            },
        };
        try
        {
            var first = await protocol.ReceiveBroadcast(batch, cancellationToken);
            Assert.Equal("first", Assert.IsType<string>(Assert.Single(first.Acknowledgments[ns.Name]).Key.Value));
            Assert.False(first.Acknowledgments.ContainsKey(empty.Name));
            Assert.Equal(0, ns.GetVersion("second"));

            var second = await protocol.ReceiveBroadcast(batch, cancellationToken);
            Assert.Equal("second", Assert.IsType<string>(Assert.Single(second.Acknowledgments[ns.Name]).Key.Value));
            Assert.Equal(new DisseminationKey[] { "first", "second" }, ns.Attempts.Select(static value => value.Key));
            Assert.Equal(1, ns.GetVersion("second"));
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }
    }

    private sealed class InventoryOnlyNamespace(SiloAddress local) : IDisseminationNamespace
    {
        public FakeNamespace Inner { get; } = new(local);
        public DisseminationNamespace Name => Inner.Name;
        public DisseminationNamespaceOptions Options => Inner.Options;
        public IEnumerable<DisseminationKey> Keys => Inner.Digests.Select(static digest => digest.Key);
        public IEnumerable<DigestEntry> Digests => throw new InvalidOperationException("Pruning must use key inventory.");
        public long GetVersion(DisseminationKey key) => Inner.GetVersion(key);
        public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request) => Inner.CreateRepair(request);
        public ValueTask<DisseminationApplyResult> ApplyValueAsync(DisseminationValue value, CancellationToken cancellationToken) =>
            Inner.ApplyValueAsync(value, cancellationToken);
    }
}
