using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

public partial class JournaledJobShardManagerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PrunedOwner_AdoptionCountsDeathAndHonorsPoisonLimit(int adoptionLimit)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var storage = new VolatileJournalStorageProvider();
        using var services = CreateServices(storage, clock);
        var membership = new TestClusterMembershipService();
        var previousOwner = SiloAddress.New(System.Net.IPAddress.Loopback, 5100, 10);
        var nextOwner = SiloAddress.New(System.Net.IPAddress.Loopback, 5101, 11);
        membership.SetSiloStatus(previousOwner, SiloStatus.Active);
        membership.SetSiloStatus(nextOwner, SiloStatus.Active);
        var manager1 = CreateManager(services, membership, previousOwner);
        var manager2 = CreateManager(services, membership, nextOwner, new DurableJobsOptions { MaxAdoptedCount = adoptionLimit });
        await using var shard = await manager1.CreateShardAsync(
            now.AddMinutes(-1), now.AddHours(1), new Dictionary<string, string>(), cancellationToken);
        Assert.NotNull(await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "pruned-owner"),
            JobName = "pruned-owner-job",
            DueTime = now,
            Metadata = null
        }, cancellationToken));
        var storageId = ((JournaledJobShard)shard).StorageId;
        var original = await storage.CreateStorage(storageId).GetMetadataAsync(cancellationToken);
        Assert.NotNull(original);
        Assert.Equal("0", original.Properties["DurableJobsAdoptedCount"]);
        membership.SetSiloStatus(previousOwner, SiloStatus.Dead);
        membership.RemoveSilo(previousOwner);
        Assert.DoesNotContain(previousOwner, membership.CurrentSnapshot.Members.Keys);

        var assigned = await manager2.AssignJobShardsAsync(now.AddHours(1), int.MaxValue, cancellationToken);

        var metadata = await storage.CreateStorage(storageId).GetMetadataAsync(cancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal("1", metadata.Properties["DurableJobsAdoptedCount"]);
        Assert.Equal(now.ToString("O", CultureInfo.InvariantCulture), metadata.Properties["DurableJobsLastAdoptedTime"]);
        if (adoptionLimit == 0)
        {
            Assert.Empty(assigned);
            Assert.Equal(bool.TrueString, metadata.Properties["DurableJobsPoisoned"]);
            Assert.Null(await manager2.GetShardOwnerAsync(shard.Id, cancellationToken));
            Assert.Empty(await manager2.AssignJobShardsAsync(now.AddHours(1), int.MaxValue, cancellationToken));
            var repeated = await storage.CreateStorage(storageId).GetMetadataAsync(cancellationToken);
            Assert.NotNull(repeated);
            Assert.Equal("1", repeated.Properties["DurableJobsAdoptedCount"]);
        }
        else
        {
            var adopted = Assert.Single(assigned);
            Assert.True(adopted.IsAddingCompleted);
            Assert.Equal(nextOwner.ToParsableString(), metadata.Properties["DurableJobsOwner"]);
            await manager2.UnregisterShardAsync(adopted, cancellationToken);
        }
    }
}
