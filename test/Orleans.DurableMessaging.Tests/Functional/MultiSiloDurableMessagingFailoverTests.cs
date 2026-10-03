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
    public async Task ReceiverOwnerStops_DuringBlockedHandler_NewOwnerRecoversJournaledOwnershipAndProcessesOnce()
    {
        var receiver = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        using var barrier = fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/failover");
        var sender = fixture.Client.GetGrain<IDurableMessagingTestGrain>(Guid.NewGuid());
        var logicalId = Guid.NewGuid();
        const string jobName = "orleans.messaging.inbox-drain";
        fixture.JobManagerProbe.DuplicateNext(jobName);

        await sender.SendAsync(
            receiver.GetGrainId(),
            "messages/failover",
            new DurableTestMessage(logicalId, 81, "failover"));
        await barrier.WaitUntilEnteredAsync();
        var jobs = fixture.JobManagerProbe.GetScheduledJobs(jobName, receiver.GetGrainId());
        Assert.Equal(2, jobs.Count);
        Assert.Equal(2, jobs.Select(static job => job.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(
            jobs.Select(static job => job.Metadata!["orleans.messaging.ownership-id"])
                .Distinct(StringComparer.Ordinal));
        var before = fixture.GetSnapshot(receiver);
        Assert.Contains(
            jobs,
            job => job.Id == before.InboxJob?.Id && job.ShardId == before.InboxJob?.ShardId);
        var owner = fixture.Cluster.Silos.Single(
            silo => silo.SiloAddress.ToParsableString() == before.SiloAddress);
        var recoveredSnapshot = fixture.SnapshotProbe.WaitAsync(
            receiver.GetGrainId(),
            snapshot => snapshot.ActivationId != before.ActivationId);

        await fixture.Cluster.KillSiloAsync(owner, TestContext.Current.CancellationToken);
        await fixture.Cluster.WaitForLivenessToStabilizeAsync();
        var activationRequest = receiver.GetSnapshotAsync();
        // Recovery publishes the ownership snapshot even while the resumed handler holds its turn.
        var reactivated = await recoveredSnapshot;
        Assert.NotEqual(before.ActivationId, reactivated.ActivationId);
        Assert.Equal(before.InboxJobId, reactivated.InboxJobId);
        Assert.Equal(before.InboxJob?.Id, reactivated.InboxJob?.Id);
        Assert.Equal(before.InboxJob?.ShardId, reactivated.InboxJob?.ShardId);
        Assert.Equal(1, fixture.JobManagerProbe.GetSuccessCount(jobName, receiver.GetGrainId()));
        barrier.Release();
        Assert.Equal(reactivated.ActivationId, (await activationRequest).ActivationId);
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
