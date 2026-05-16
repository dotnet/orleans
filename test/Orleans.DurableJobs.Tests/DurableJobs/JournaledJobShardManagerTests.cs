using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
public class JournaledJobShardManagerTests
{
    [Fact]
    public async Task ReleasedShard_IsClaimedClosedAndReplayedFromJournal()
    {
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var end = start.AddHours(1);
        var shard = await manager1.CreateShardAsync(
            start,
            end,
            new Dictionary<string, string> { ["Purpose"] = "JournaledManagerTest" },
            CancellationToken.None);

        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = new Dictionary<string, string> { ["Kind"] = "Replay" }
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        await manager1.UnregisterShardAsync(shard, CancellationToken.None);

        var claimed = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        var claimedShard = Assert.Single(claimed);
        Assert.True(claimedShard.IsAddingCompleted);
        Assert.Equal("JournaledManagerTest", claimedShard.Metadata!["Purpose"]);

        var rejected = await claimedShard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target2"),
            JobName = "new-job",
            DueTime = DateTimeOffset.UtcNow,
            Metadata = null
        }, CancellationToken.None);
        Assert.Null(rejected);

        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in claimedShard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            consumed.Add(jobContext);
            await claimedShard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None);
        }

        var replayed = Assert.Single(consumed);
        Assert.Equal(scheduled.Id, replayed.Job.Id);
        Assert.Equal("Replay", replayed.Job.Metadata!["Kind"]);
        Assert.Equal(1, replayed.DequeueCount);

        await manager2.UnregisterShardAsync(claimedShard, CancellationToken.None);
        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyShard_IsDeletedWhenUnregistered()
    {
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);

        var manager = CreateManager(services, membership, silo);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var shard = await manager.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "EmptyShardDelete" },
            CancellationToken.None);
        var storageId = ((JournaledJobShard)shard).StorageId;

        Assert.NotNull(await storageProvider.GetPropertiesAsync(storageId));

        await manager.UnregisterShardAsync(shard, CancellationToken.None);

        Assert.Null(await storageProvider.GetPropertiesAsync(storageId));
        Assert.Empty(await ToListAsync(storageProvider.ListAsync(JobShardId.StoragePrefix)));
    }

    [Fact]
    public async Task ClosedLocalShard_CanStillPersistRemovals()
    {
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5015), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);

        var manager = CreateManager(services, membership, silo);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "ClosedLocalShard" },
            CancellationToken.None);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "closed-local-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        await shard.MarkAsCompleteAsync(CancellationToken.None);

        await foreach (var jobContext in shard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            Assert.Equal(scheduled.Id, jobContext.Job.Id);
            Assert.True(await shard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None));
        }

        Assert.Equal(0, await shard.GetJobCountAsync());
        await manager.UnregisterShardAsync(shard, CancellationToken.None);
    }

    [Fact]
    public async Task DeadOwnerShard_IsAdoptedClosedAndReplayedFromJournal()
    {
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5020), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5021), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager1.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "DeadOwnerAdoption" },
            CancellationToken.None);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "dead-owner-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = new Dictionary<string, string> { ["Kind"] = "Adopted" }
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        var claimed = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        var claimedShard = Assert.Single(claimed);
        Assert.True(claimedShard.IsAddingCompleted);
        Assert.Equal("DeadOwnerAdoption", claimedShard.Metadata!["Purpose"]);
        Assert.Equal(silo2, await manager2.GetShardOwnerAsync(claimedShard.Id, CancellationToken.None));

        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in claimedShard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            consumed.Add(jobContext);
            await claimedShard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None);
        }

        var replayed = Assert.Single(consumed);
        Assert.Equal(scheduled.Id, replayed.Job.Id);
        Assert.Equal("Adopted", replayed.Job.Metadata!["Kind"]);
        Assert.Equal(1, replayed.DequeueCount);

        await manager2.UnregisterShardAsync(claimedShard, CancellationToken.None);
        await shard.DisposeAsync();
    }

    [Fact]
    public async Task DeadOwnerShard_IsPoisonedAfterAdoptionLimitExceeded()
    {
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5030), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5031), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2, new DurableJobsOptions { MaxAdoptedCount = 0 });
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager1.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "PoisonedShard" },
            CancellationToken.None);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "poisoned-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None));
        Assert.Null(await manager2.GetShardOwnerAsync(shard.Id, CancellationToken.None));
        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None));

        await shard.DisposeAsync();
    }

    private static ServiceProvider CreateServices(VolatileJournalStorageProvider storageProvider)
    {
        var builder = new TestSiloBuilder();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IJournalStorageProvider>(storageProvider);
        builder.Services.AddSingleton<IJournalStorageCatalog>(storageProvider);
        return builder.Services.BuildServiceProvider();
    }

    private static JournaledJobShardManager CreateManager(
        IServiceProvider services,
        TestClusterMembershipService membership,
        SiloAddress siloAddress,
        DurableJobsOptions options = null)
        => new(
            new TestLocalSiloDetails(siloAddress),
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            Options.Create(options ?? new DurableJobsOptions()),
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestLocalSiloDetails(SiloAddress siloAddress) : ILocalSiloDetails
    {
        public string Name => SiloAddress.ToParsableString();

        public string ClusterId => "TestCluster";

        public string DnsHostName => SiloAddress.ToParsableString();

        public SiloAddress SiloAddress { get; } = siloAddress;

        public SiloAddress GatewayAddress => SiloAddress;
    }

    private sealed class TestClusterMembershipService : IClusterMembershipService
    {
        private ImmutableDictionary<SiloAddress, ClusterMember> _members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty;
        private long _version;

        public ClusterMembershipSnapshot CurrentSnapshot => new(_members, new MembershipVersion(_version));

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => GetMembershipUpdates();

        public void SetSiloStatus(SiloAddress siloAddress, SiloStatus status)
        {
            _members = _members.SetItem(siloAddress, new ClusterMember(siloAddress, status, siloAddress.ToParsableString()));
            _version++;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        private static async IAsyncEnumerable<ClusterMembershipSnapshot> GetMembershipUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
