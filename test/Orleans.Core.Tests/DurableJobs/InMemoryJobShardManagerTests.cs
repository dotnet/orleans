#nullable enable

using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Runtime;
using NSubstitute;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("DurableJobs")]
public class InMemoryJobShardManagerTests : IAsyncLifetime
{
    private static readonly SiloAddress Silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 1);
    private static readonly SiloAddress Silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5002), 2);
    private static readonly SiloAddress Silo3 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5003), 3);
    private static readonly SiloAddress Silo4 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5004), 4);

    public Task InitializeAsync() => InMemoryJobShardManager.ClearAllShardsAsync();

    public Task DisposeAsync() => InMemoryJobShardManager.ClearAllShardsAsync();

    [Fact]
    public async Task CreateShardAsync_CreatesShardOwnedBySilo()
    {
        var manager = new InMemoryJobShardManager(Silo1);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        var shard = await manager.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);

        Assert.NotNull(shard);
        Assert.Equal(minDueTime, shard.StartTime);
        Assert.Equal(maxDueTime, shard.EndTime);
    }

    [Fact]
    public async Task AssignJobShardsAsync_ReturnsOwnedShards()
    {
        var manager = new InMemoryJobShardManager(Silo1);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        var createdShard = await manager.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);
        var assignedShards = await manager.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);

        Assert.Single(assignedShards);
        Assert.Equal(createdShard.Id, assignedShards[0].Id);
    }

    [Fact]
    public async Task AssignJobShardsAsync_OrphanedShard_IsAssignedWithoutIncrementingAdoptedCount()
    {
        // Silo1 creates a shard and gracefully releases it
        var manager1 = new InMemoryJobShardManager(Silo1);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        var shard = await manager1.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);
        
        // Schedule a job so the shard isn't deleted on unregister
        await shard.TryScheduleJobAsync(new ScheduleJobRequest { Target = GrainId.Create("test", "grain1"), JobName = "TestJob", DueTime = minDueTime.AddMinutes(30), Metadata = null }, CancellationToken.None);
        
        // Gracefully unregister (sets owner to null)
        await manager1.UnregisterShardAsync(shard, CancellationToken.None);

        // Silo2 picks up the orphaned shard
        var manager2 = new InMemoryJobShardManager(Silo2);
        var assignedShards = await manager2.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);

        Assert.Single(assignedShards);
        Assert.Equal(shard.Id, assignedShards[0].Id);

        var ownershipInfo = await InMemoryJobShardManager.GetOwnershipInfoAsync(shard.Id);
        Assert.True(ownershipInfo.HasValue);
        Assert.Equal(Silo2.ToString(), ownershipInfo.Value.Owner);
        Assert.Equal(0, ownershipInfo.Value.AdoptedCount);
    }

    [Fact]
    public async Task AssignJobShardsAsync_AdoptedFromDeadSilo_IncrementsAdoptedCount()
    {
        // Setup membership service that reports Silo1 as dead
        var membershipService = CreateMembershipService(deadSilos: [Silo1]);

        // Silo1 creates a shard (simulating it was created before death)
        var manager1 = new InMemoryJobShardManager(Silo1, membershipService);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        var shard = await manager1.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);

        // Silo2 adopts the shard from dead Silo1
        var manager2 = new InMemoryJobShardManager(Silo2, membershipService, maxAdoptedCount: 3);
        var assignedShards = await manager2.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);

        // Shard should be assigned (adopted count = 1, under threshold)
        Assert.Single(assignedShards);
        Assert.Equal(shard.Id, assignedShards[0].Id);

        var ownershipInfo = await InMemoryJobShardManager.GetOwnershipInfoAsync(shard.Id);
        Assert.True(ownershipInfo.HasValue);
        Assert.Equal(Silo2.ToString(), ownershipInfo.Value.Owner);
        Assert.Equal(1, ownershipInfo.Value.AdoptedCount);
    }

    [Fact]
    public async Task AssignJobShardsAsync_PoisonedShard_IsNotAssigned()
    {
        // Setup membership service
        var membershipService = Substitute.For<IClusterMembershipService>();
        var snapshot = CreateMembershipSnapshot(deadSilos: [Silo1, Silo2, Silo3]);
        membershipService.CurrentSnapshot.Returns(snapshot);

        // Silo1 creates a shard
        var manager1 = new InMemoryJobShardManager(Silo1, membershipService, maxAdoptedCount: 2);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        await manager1.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);

        // Silo2 adopts from dead Silo1 (adopted count = 1)
        var manager2 = new InMemoryJobShardManager(Silo2, membershipService, maxAdoptedCount: 2);
        var shards2 = await manager2.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);
        Assert.Single(shards2);

        // Silo3 adopts from dead Silo2 (adopted count = 2)
        var manager3 = new InMemoryJobShardManager(Silo3, membershipService, maxAdoptedCount: 2);
        var shards3 = await manager3.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);
        Assert.Single(shards3);

        // Silo4 tries to adopt from dead Silo3 (adopted count would be 3, exceeds max of 2)
        var manager4 = new InMemoryJobShardManager(Silo4, membershipService, maxAdoptedCount: 2);
        var shards4 = await manager4.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);

        // Shard is poisoned and should not be assigned
        Assert.Empty(shards4);
    }

    [Fact]
    public async Task AssignJobShardsAsync_MaxAdoptedCountOfZero_NeverAssignsAdoptedShards()
    {
        // Setup membership service that reports Silo1 as dead
        var membershipService = CreateMembershipService(deadSilos: [Silo1]);

        // Silo1 creates a shard
        var manager1 = new InMemoryJobShardManager(Silo1, membershipService, maxAdoptedCount: 0);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        await manager1.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);

        // Silo2 tries to adopt from dead Silo1 with maxAdoptedCount=0
        var manager2 = new InMemoryJobShardManager(Silo2, membershipService, maxAdoptedCount: 0);
        var assignedShards = await manager2.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);

        // Shard should not be assigned (adopted count would be 1, exceeds max of 0)
        Assert.Empty(assignedShards);
    }

    [Fact]
    public async Task UseInMemoryDurableJobs_ConfiguredMaxAdoptedCount_IsApplied()
    {
        var membershipService = CreateMembershipService(deadSilos: [Silo2]);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        var ownerManager = new InMemoryJobShardManager(Silo2, membershipService, maxAdoptedCount: 3);
        await ownerManager.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);

        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(Silo1);

        var services = new ServiceCollection();
        services.AddSingleton(localSiloDetails);
        services.AddSingleton(membershipService);
        services.Configure<DurableJobsOptions>(options => options.MaxAdoptedCount = 0);
        services.UseInMemoryDurableJobs();

        using var serviceProvider = services.BuildServiceProvider();
        var manager = serviceProvider.GetRequiredService<InMemoryJobShardManager>();

        var assignedShards = await manager.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);
        Assert.Empty(assignedShards);
    }

    [Fact]
    public async Task AssignJobShardsAsync_ShardFromActiveSilo_IsNotAssigned()
    {
        // Setup membership service that reports Silo1 as active
        var membershipService = CreateMembershipService(activeSilos: [Silo1]);

        // Silo1 creates a shard
        var manager1 = new InMemoryJobShardManager(Silo1, membershipService);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        await manager1.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);

        // Silo2 tries to get shards - should not get Silo1's shard since Silo1 is active
        var manager2 = new InMemoryJobShardManager(Silo2, membershipService);
        var assignedShards = await manager2.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);

        Assert.Empty(assignedShards);
    }

    [Fact]
    public async Task UnregisterShardAsync_WithNoJobsRemaining_RemovesShard()
    {
        var manager = new InMemoryJobShardManager(Silo1);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        var shard = await manager.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);
        
        // Unregister with no jobs
        await manager.UnregisterShardAsync(shard, CancellationToken.None);

        // Shard should be removed, not reassignable
        var assignedShards = await manager.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);
        Assert.Empty(assignedShards);
    }

    [Fact]
    public async Task UnregisterShardAsync_WithJobsRemaining_MarksShardAsOrphaned()
    {
        var manager1 = new InMemoryJobShardManager(Silo1);
        var minDueTime = DateTimeOffset.UtcNow;
        var maxDueTime = minDueTime.AddHours(1);

        var shard = await manager1.CreateShardAsync(minDueTime, maxDueTime, new Dictionary<string, string>(), CancellationToken.None);
        
        // Add a job
        await shard.TryScheduleJobAsync(new ScheduleJobRequest { Target = GrainId.Create("test", "grain1"), JobName = "TestJob", DueTime = minDueTime.AddMinutes(30), Metadata = null }, CancellationToken.None);
        
        // Unregister with jobs remaining
        await manager1.UnregisterShardAsync(shard, CancellationToken.None);

        // Shard should be orphaned and available for another silo
        var manager2 = new InMemoryJobShardManager(Silo2);
        var assignedShards = await manager2.AssignJobShardsAsync(maxDueTime, int.MaxValue, CancellationToken.None);
        Assert.Single(assignedShards);
    }

    private static IClusterMembershipService CreateMembershipService(
        SiloAddress[]? activeSilos = null,
        SiloAddress[]? deadSilos = null)
    {
        var membershipService = Substitute.For<IClusterMembershipService>();
        var snapshot = CreateMembershipSnapshot(activeSilos, deadSilos);
        membershipService.CurrentSnapshot.Returns(snapshot);
        return membershipService;
    }

    private static ClusterMembershipSnapshot CreateMembershipSnapshot(
        SiloAddress[]? activeSilos = null,
        SiloAddress[]? deadSilos = null)
    {
        var builder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();

        if (activeSilos is not null)
        {
            foreach (var silo in activeSilos)
            {
                builder[silo] = new ClusterMember(silo, SiloStatus.Active, silo.ToString());
            }
        }

        if (deadSilos is not null)
        {
            foreach (var silo in deadSilos)
            {
                builder[silo] = new ClusterMember(silo, SiloStatus.Dead, silo.ToString());
            }
        }

        return new ClusterMembershipSnapshot(builder.ToImmutable(), new MembershipVersion(1));
    }
}
