using Orleans.DurableMessaging.Tests.Support;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MultiSiloDurableMessagingCollection : ICollectionFixture<MultiSiloDurableMessagingClusterFixture>
{
    public const string Name = "Durable messaging multi-silo cluster";
}

[Collection(MultiSiloDurableMessagingCollection.Name)]
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class MultiSiloDurableMessagingFailoverTests(MultiSiloDurableMessagingClusterFixture fixture)
{
    [Fact]
    public async Task ReceiverOwnerStops_DuringBlockedHandler_NewOwnerRecoversStableJobAndProcessesOnce()
    {
        var receiver = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        using var barrier = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/failover");
        var sender = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        var logicalId = Guid.NewGuid();

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/failover",
            new DurableTestMessage(logicalId, 81, "failover"));
        await barrier.WaitUntilEnteredAsync();
        var before = await receiver.GetSnapshotAsync();
        var owner = fixture.Cluster.Silos.Single(
            silo => silo.SiloAddress.ToParsableString() == before.SiloAddress);

        await fixture.Cluster.KillSiloAsync(owner);
        await fixture.Cluster.WaitForLivenessToStabilizeAsync();
        var reactivated = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, reactivated.ActivationId);
        barrier.Release();
        DurableEndpointSnapshot recovered;
        try
        {
            recovered = await fixture.WaitForEffectCountAsync(receiver, 1);
        }
        catch (TimeoutException exception)
        {
            var snapshot = await receiver.GetSnapshotAsync();
            throw new TimeoutException(
                $"Recovery did not complete. Activation={snapshot.ActivationId}, silo={snapshot.SiloAddress}, inbox={snapshot.InboxCount}, effects={snapshot.Effects.Count}, deadLetters={snapshot.InboxDeadLetters.Count}.",
                exception);
        }

        Assert.Equal(reactivated.ActivationId, recovered.ActivationId);
        Assert.NotEqual(before.SiloAddress, recovered.SiloAddress);
        var effect = Assert.Single(recovered.Effects);
        Assert.Equal(logicalId, effect.LogicalId);
        Assert.Equal(1, effect.Count);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Single(fixture.Cluster.Silos);
    }
}
