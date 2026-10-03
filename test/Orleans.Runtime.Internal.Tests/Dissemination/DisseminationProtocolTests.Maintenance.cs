using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public async Task ReceiveTrafficSharesInventoryMaintenanceUntilTimeOrMembershipChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41201);
        var peer = CreateSilo(41202);
        var transport = new FakeTransport(local, peer);
        var clock = new FakeTimeProvider();
        var ns = new CompactReviewInventoryNamespace(local);
        var inventories = 0;
        DisseminationKey[] keys = ["value"];
        ns.ReadKeys = () =>
        {
            inventories++;
            return keys;
        };
        var protocol = CreateProtocol(transport, [ns], timeProvider: clock);
        try
        {
            for (var version = 1; version <= 2000; version++)
            {
                await Receive(version);
            }
            Assert.Equal(1, inventories);
            Assert.Equal(2000, ns.GetVersion("value"));

            clock.Advance(TimeSpan.FromMilliseconds(999));
            await Receive(2001);
            Assert.Equal(1, inventories);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Receive(2002);
            Assert.Equal(2, inventories);

            transport.Peers.Add(CreateSilo(41203));
            await Receive(2003);
            Assert.Equal(3, inventories);
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }

        Task Receive(long version) => protocol.ReceiveBroadcast(new()
        {
            Sender = peer,
            Values = CreateValueGroups(ns.Name, ns.Inner.CreateItem(peer, "value", version)),
        }, cancellationToken);
    }

    [Fact]
    public async Task FailedInventoryMaintenanceIsRetriedWithoutWaitingForTheInterval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41211);
        var peer = CreateSilo(41212);
        var ns = new CompactReviewInventoryNamespace(local);
        var inventories = 0;
        ns.ReadKeys = () => ++inventories == 1
            ? throw new InvalidOperationException("Inventory failed.")
            : new DisseminationKey[] { "value" };
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns], timeProvider: new FakeTimeProvider());
        var batch = new DisseminationBroadcastBatch
        {
            Sender = peer,
            Values = CreateValueGroups(ns.Name, ns.Inner.CreateItem(peer, "value", 1)),
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.ReceiveBroadcast(batch, cancellationToken));
            var response = await protocol.ReceiveBroadcast(batch, cancellationToken);
            Assert.Equal(2, inventories);
            Assert.Equal(1, Assert.Single(response.Acknowledgments[ns.Name]).Version);
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task InventoryMaintenanceAllowsSynchronousPublicationReentry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41221);
        var peer = CreateSilo(41222);
        var ns = new CompactReviewInventoryNamespace(local);
        ns.Inner.SetValue("nested", 1);
        var protocol = CreateProtocol(new FakeTransport(local, peer), [ns], timeProvider: new FakeTimeProvider());
        Task<bool>? nested = null;
        ns.ReadKeys = () =>
        {
            nested = protocol.Publish(ns, "nested", 1, cancellationToken).AsTask();
            Assert.True(nested.IsCompletedSuccessfully);
            return new DisseminationKey[] { "nested", "value" };
        };
        try
        {
            await protocol.ReceiveBroadcast(new()
            {
                Sender = peer,
                Values = CreateValueGroups(ns.Name, ns.Inner.CreateItem(peer, "value", 1)),
            }, cancellationToken);

            Assert.NotNull(nested);
            Assert.True(await nested);
            Assert.Equal(1, ns.GetVersion("value"));
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }
    }

    [Theory]
    [InlineData(1, 64, 1)]
    [InlineData(8, 16, 2)]
    public async Task AntiEntropyUsesIndependentResponseAndReceiveBudgets(int repairItems, int repairBytes, int expectedItems)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(41231);
        var peer = CreateSilo(41232);
        var ns = new FakeNamespace(local);
        var transport = new FakeTransport(local, peer);
        var protocol = CreateProtocol(transport, ns, options =>
        {
            options.MaxBatchItems = 8;
            options.MaxBatchBytes = 64;
            options.Overlay.MaxAntiEntropyBatchItems = repairItems;
            options.Overlay.MaxAntiEntropyBatchBytes = repairBytes;
        });
        var keys = new DisseminationKey[] { "first", "second", "third", "fourth" };
        ns.ExpectedKeys.UnionWith(keys);
        transport.ExchangeAntiEntropyHandler = (target, request, _) =>
        {
            Assert.Equal(repairItems, request.MaxResponseItems);
            Assert.Equal(repairBytes, request.MaxResponseBytes);
            return ValueTask.FromResult(new DisseminationAntiEntropyResponse
            {
                Sender = target,
                Values = CreateValueGroups(ns.Name, keys.Select(key => ns.CreateItem(peer, key, 1)).ToArray()),
            });
        };
        try
        {
            await protocol.RunAntiEntropyRound(cancellationToken);
            Assert.Equal(expectedItems, keys.Count(key => ns.GetVersion(key) == 1));

            var broadcast = await protocol.ReceiveBroadcast(new()
            {
                Sender = peer,
                Values = CreateValueGroups(ns.Name, keys.Select(key => ns.CreateItem(peer, key, 2)).ToArray()),
            }, cancellationToken);
            Assert.Equal(keys.Length, broadcast.Acknowledgments[ns.Name].Count);
            Assert.All(keys, key => Assert.Equal(2, ns.GetVersion(key)));

            var repair = await protocol.ReceiveAntiEntropy(new()
            {
                Sender = peer,
                SupportedNamespaces = [ns.Name],
                Digests = new() { [ns.Name] = keys.Select(key => new DigestEntry(key, 0)).ToList() },
            }, cancellationToken);
            Assert.Equal(expectedItems, GetAntiEntropyResponseValues(repair).Count());
            Assert.True(repair.Truncated);
        }
        finally
        {
            await protocol.StopAsync(cancellationToken);
        }
    }
}
