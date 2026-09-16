#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactAcknowledgmentsRequireCallerSupport(bool supportsCompact)
    {
        var local = CreateSilo(40701);
        var peer = CreateSilo(40702);
        var ns = new FakeNamespace(local);
        var protocol = CreateProtocol(new FakeTransport(local, peer), ns);
        var batch = new DisseminationBroadcastBatch
        {
            Sender = peer,
            SupportsCompactAcknowledgments = supportsCompact,
            Values = CreateValueGroups(ns.CreateItem(peer, "first", 1), ns.CreateItem(peer, "second", 2)),
        };
        try
        {
            var response = await protocol.ReceiveBroadcast(batch, TestContext.Current.CancellationToken);

            Assert.Equal(supportsCompact, response.AllVersionsAcknowledged);
            Assert.Empty(response.UnsupportedNamespaces);
            Assert.Equal(ns.Name, Assert.Single(response.Acknowledgments.Keys));
            Assert.Equal(1, ns.GetVersion("first"));
            Assert.Equal(2, ns.GetVersion("second"));
            if (supportsCompact)
            {
                Assert.Empty(response.Acknowledgments[ns.Name]);
            }
            else
            {
                Assert.Equal(new long[] { 1, 2 }, response.Acknowledgments[ns.Name].Select(static value => value.Version));
            }
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CompactAcknowledgmentsPreserveExactAheadVersions()
    {
        var local = CreateSilo(40711);
        var peer = CreateSilo(40712);
        var ns = new FakeNamespace(local);
        ns.SetValue("value", 3);
        var protocol = CreateProtocol(new FakeTransport(local, peer), ns);
        try
        {
            var response = await protocol.ReceiveBroadcast(new()
            {
                Sender = peer,
                SupportsCompactAcknowledgments = true,
                Values = CreateValueGroups(ns.CreateItem(peer, "value", 2)),
            }, TestContext.Current.CancellationToken);

            Assert.False(response.AllVersionsAcknowledged);
            Assert.Equal(3, Assert.Single(response.Acknowledgments[ns.Name]).Version);
            Assert.Equal(3, ns.GetVersion("value"));
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("disabled")]
    [InlineData("item-limit")]
    public async Task CompactAcknowledgmentsRetainExplicitPartialOutcomes(string scenario)
    {
        var local = CreateSilo(40721);
        var peer = CreateSilo(40722);
        var ns = new ProtocolReviewNamespace(local);
        var other = new FakeNamespace(local, "other");
        other.Options.Enabled = scenario != "disabled";
        ns.ApplyHandler = (value, token) => value.Key == new DisseminationKey("second") && scenario == "rejected"
            ? new(DisseminationApplyResult.Rejected)
            : ns.Inner.ApplyValueAsync(value, token);
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns, other], options =>
        {
            options.MaxBatchItems = scenario == "item-limit" ? 1 : 10;
        });
        var values = CreateValueGroups(ns.Inner.CreateItem(peer, "first", 1), ns.Inner.CreateItem(peer, "second", 2));
        if (scenario == "disabled")
        {
            values[other.Name] = [other.CreateItem(peer, "other", 1)];
        }

        try
        {
            var response = await protocol.ReceiveBroadcast(new()
            {
                Sender = peer,
                SupportsCompactAcknowledgments = true,
                Values = values,
            }, TestContext.Current.CancellationToken);

            Assert.False(response.AllVersionsAcknowledged);
            Assert.Equal(1, response.Acknowledgments[ns.Name].Single(value => value.Key == new DisseminationKey("first")).Version);
            if (scenario == "rejected")
            {
                Assert.Equal(0, response.Acknowledgments[ns.Name].Single(value => value.Key == new DisseminationKey("second")).Version);
            }
            else if (scenario == "item-limit")
            {
                Assert.Single(response.Acknowledgments[ns.Name]);
                Assert.Equal(0, ns.GetVersion("second"));
            }
            else
            {
                Assert.Equal(other.Name, Assert.Single(response.UnsupportedNamespaces));
                Assert.False(response.Acknowledgments.ContainsKey(other.Name));
            }
        }
        finally
        {
            await protocol.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void CompactAcknowledgmentFieldsPreserveLegacyWireSchemas()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var peer = CreateSilo(40731);
        var current = new DisseminationBroadcastBatch
        {
            Sender = peer,
            SupportsCompactAcknowledgments = true,
            Values = CreateValueGroups(new FakeNamespace(peer).CreateItem(peer, "value", 1)),
        };
        var legacyBatch = Assert.IsType<CompactReviewLegacyBatch>(
            serializer.Deserialize<CompactReviewLegacyBatch>(serializer.SerializeToArray(current)));
        Assert.Equal(peer, legacyBatch.Sender);
        Assert.Equal(1, Assert.Single(legacyBatch.Values[FakeNamespace.DefaultName]).Value.ToVersion);
        var request = Assert.IsType<DisseminationBroadcastBatch>(
            serializer.Deserialize<DisseminationBroadcastBatch>(serializer.SerializeToArray(legacyBatch)));
        Assert.False(request.SupportsCompactAcknowledgments);

        var legacyResponse = new CompactReviewLegacyResponse
        {
            Acknowledgments = new() { [FakeNamespace.DefaultName] = [new("value", 3)] },
        };
        var response = Assert.IsType<DisseminationBroadcastResponse>(
            serializer.Deserialize<DisseminationBroadcastResponse>(serializer.SerializeToArray(legacyResponse)));
        Assert.False(response.AllVersionsAcknowledged);
        Assert.Equal(3, Assert.Single(response.Acknowledgments[FakeNamespace.DefaultName]).Version);
        var oldReader = Assert.IsType<CompactReviewLegacyResponse>(
            serializer.Deserialize<CompactReviewLegacyResponse>(serializer.SerializeToArray(response)));
        Assert.Equal(3, Assert.Single(oldReader.Acknowledgments[FakeNamespace.DefaultName]).Version);
        var compact = new DisseminationBroadcastResponse
        {
            AllVersionsAcknowledged = true,
            Acknowledgments = new() { [FakeNamespace.DefaultName] = [] },
        };
        var ignoresNewField = Assert.IsType<CompactReviewLegacyResponse>(
            serializer.Deserialize<CompactReviewLegacyResponse>(serializer.SerializeToArray(compact)));
        Assert.Equal(FakeNamespace.DefaultName, Assert.Single(ignoresNewField.Acknowledgments.Keys));
        Assert.Empty(ignoresNewField.Acknowledgments[FakeNamespace.DefaultName]);
    }

    [Fact]
    public void CompactAcknowledgmentBodyIsSmallerAndRoundTrips()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var explicitResponse = new DisseminationBroadcastResponse
        {
            Acknowledgments = new()
            {
                [FakeNamespace.DefaultName] = CreateSilos(128).Select(silo => new DigestEntry(silo, 639000000000000000)).ToList(),
            },
        };
        var compactResponse = new DisseminationBroadcastResponse
        {
            AllVersionsAcknowledged = true,
            Acknowledgments = new() { [FakeNamespace.DefaultName] = [] },
        };

        var explicitBytes = serializer.SerializeToArray(explicitResponse);
        var compactBytes = serializer.SerializeToArray(compactResponse);
        var roundTrip = Assert.IsType<DisseminationBroadcastResponse>(
            serializer.Deserialize<DisseminationBroadcastResponse>(compactBytes));

        Assert.True(roundTrip.AllVersionsAcknowledged);
        Assert.Empty(roundTrip.Acknowledgments[FakeNamespace.DefaultName]);
        Assert.True(compactBytes.Length * 4 < explicitBytes.Length,
            $"Compact acknowledgment body={compactBytes.Length} bytes; explicit body={explicitBytes.Length} bytes.");
    }

    [Fact]
    public async Task PruningScratchInventoryIsClearedAfterEnumerationFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40741);
        var peer = CreateSilo(40742);
        var ns = new CompactReviewInventoryNamespace(local);
        ns.Inner.SetValue("old", 1);
        var transport = new FakeTransport(local, peer);
        var queue = CreateBroadcastQueue(transport, [ns], timeProvider: new FakeTimeProvider());
        var membership = new DisseminationMembership(
            transport.MembershipManager, new FakeLocalSiloDetails(local),
            Microsoft.Extensions.Options.Options.Create(new DisseminationOptions()));
        try
        {
            Assert.True(queue.Notify(peer, ns, "old"));
            await queue.FlushPendingBroadcast(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Single(transport.BroadcastBatches);

            ns.ReadKeys = FailingKeys;
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.Prune(membership.CurrentSnapshots, cancellationToken));
            ns.ReadKeys = static () => new DisseminationKey[] { "new" };
            await queue.Prune(membership.CurrentSnapshots, cancellationToken);
            ns.ReadKeys = static () => new DisseminationKey[] { "old", "new" };

            Assert.True(queue.Notify(peer, ns, "old", force: false));
            await queue.FlushPendingBroadcast(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(2, transport.BroadcastBatches.Count);
            Assert.All(transport.BroadcastBatches, batch => Assert.Equal(1, Assert.Single(GetBroadcastValues(batch.Batch)).Value.ToVersion));
        }
        finally
        {
            await queue.StopAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        static IEnumerable<DisseminationKey> FailingKeys()
        {
            yield return "old";
            throw new InvalidOperationException("Inventory enumeration failed after producing a key.");
        }
    }

    private sealed class CompactReviewInventoryNamespace(SiloAddress local) : IDisseminationNamespace
    {
        public FakeNamespace Inner { get; } = new(local);
        public Func<IEnumerable<DisseminationKey>> ReadKeys { get; set; } = static () => new DisseminationKey[] { "old" };
        public DisseminationNamespace Name => Inner.Name;
        public DisseminationNamespaceOptions Options => Inner.Options;
        public IEnumerable<DisseminationKey> Keys => ReadKeys();
        public IEnumerable<DigestEntry> Digests => Inner.Digests;
        public long GetVersion(DisseminationKey key) => Inner.GetVersion(key);
        public DisseminationRepairResult CreateRepair(in DisseminationRepairRequest request) => Inner.CreateRepair(request);
        public ValueTask<DisseminationApplyResult> ApplyValueAsync(DisseminationValue value, CancellationToken cancellationToken) =>
            Inner.ApplyValueAsync(value, cancellationToken);
    }

    [GenerateSerializer]
    internal sealed class CompactReviewLegacyBatch
    {
        [Id(0)] public SiloAddress Sender { get; init; } = null!;
        [Id(1)] public Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> Values { get; init; } = [];
    }

    [GenerateSerializer]
    internal sealed class CompactReviewLegacyResponse
    {
        [Id(0)] public Dictionary<DisseminationNamespace, List<DigestEntry>> Acknowledgments { get; init; } = [];
        [Id(1)] public List<DisseminationNamespace> UnsupportedNamespaces { get; init; } = [];
    }
}
