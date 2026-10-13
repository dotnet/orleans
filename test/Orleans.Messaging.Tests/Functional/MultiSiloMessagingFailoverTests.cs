using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MultiSiloMessagingCollection : ICollectionFixture<MultiSiloMessagingClusterFixture>
{
    public const string Name = "Messaging multi-silo cluster";
}

[Collection(MultiSiloMessagingCollection.Name)]
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MultiSiloMessagingFailoverTests(MultiSiloMessagingClusterFixture fixture)
{
    [Fact]
    public async Task ReceiverSiloStops_DuringBlockedHandler_NewActivationRecoversFromJournalAndProcessesOnce()
    {
        var receiver = fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());
        using var barrier = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/failover");
        var sender = fixture.Client.GetGrain<IMessagingTestGrain>(Guid.NewGuid());
        var logicalId = HierarchicalKey.Create("failover", Guid.NewGuid().ToString("N"));
        fixture.JobManagerProbe.DuplicateNext(MessagingPump.JobName, receiver.GetGrainId());

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/failover",
            new TestMessage(logicalId, 81, "failover"));
        await barrier.WaitUntilEnteredAsync();
        Assert.True(fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, receiver.GetGrainId()).Count >= 2);
        var before = fixture.GetSnapshot(receiver);
        var owner = fixture.Cluster.Silos.Single(
            silo => silo.SiloAddress.ToParsableString() == before.SiloAddress);

        await fixture.Cluster.KillSiloAsync(owner, TestContext.Current.CancellationToken);
        await fixture.Cluster.WaitForLivenessToStabilizeAsync();
        var activationRequest = receiver.GetSnapshotAsync();
        barrier.Release();
        var reactivated = await activationRequest;
        Assert.NotEqual(before.ActivationId, reactivated.ActivationId);
        EndpointSnapshot recovered;
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
