using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Orleans.ScheduledJobs.AzureStorage;
using Xunit;

namespace Tester.AzureUtils.ScheduledJobs;

[TestCategory("ScheduledJobs")]
public class AzureStorageJobShardManagerTests : AzureStorageBasicTests
{
    private readonly IDictionary<string, string> _metadata = new Dictionary<string, string>
    {
        { "CreatedBy", "UnitTest" },
        { "Purpose", "Testing" }
    };

    [Fact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_Creation_Assignation()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();
        options.Value.ContainerName = "jobshardmanager" + Guid.NewGuid();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);

        var membershipService = new InMemoryClusterMembershipService();

        var manager = new AzureStorageJobShardManager(options, membershipService);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTimeOffset.Now;
        var maxDate = date.AddHours(1);

        // Register multiple shards and ensure they are distinct
        // two of them have the same time range
        var shard1 = await manager.RegisterShard(localAddress, date, maxDate, _metadata);
        var shard2 = await manager.RegisterShard(localAddress, date, maxDate, _metadata);
        var shard3 = await manager.RegisterShard(localAddress, date.AddHours(2), maxDate, _metadata);

        Assert.Distinct([shard1.Id, shard2.Id, shard3.Id]);

        // No unassigned shards
        Assert.Empty(await manager.GetJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1)));

        // Mark the local silo as dead, and create a new incarnation
        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        // Now we can take over the two first shards
        var shards = await manager.GetJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Equal(2, shards.Count);
        Assert.Contains(shard1.Id, shards.Select(s => s.Id));
        Assert.Contains(shard2.Id, shards.Select(s => s.Id));

        // Register another silo
        var otherSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        membershipService.SetSiloStatus(otherSilo, SiloStatus.Active);

        // No unassigned shards
        Assert.Empty(await manager.GetJobShardsAsync(otherSilo, DateTime.UtcNow.AddHours(1)));
    }

    [Fact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ReadFrozenShard()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();
        options.Value.ContainerName = "jobshardmanager" + Guid.NewGuid();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var manager = new AzureStorageJobShardManager(options, membershipService);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddHours(1), _metadata);

        // Schedule some jobs
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job3", DateTime.UtcNow.AddSeconds(10));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(6));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job4", DateTime.UtcNow.AddSeconds(15));

        // Mark the local silo as dead, and create a new incarnation
        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        // Take over the shard
        var shards = await manager.GetJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Single(shards);
        shard1 = shards[0];

        var counter = 1;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            await shard1.RemoveJobAsync(jobCtx.Job.Id);
            counter++;
        }
        Assert.Equal(5, counter);
        await manager.UnregisterShard(localAddress, shard1);

        // No unassigned shards
        Assert.Empty(await manager.GetJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1)));
    }

    [Fact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_LiveShard()
    {
        var startTime = DateTime.UtcNow;
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();
        options.Value.MaxShardDuration = TimeSpan.FromSeconds(20);
        options.Value.ContainerName = "jobshardmanager" + Guid.NewGuid();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var manager = new AzureStorageJobShardManager(options, membershipService);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata);

        // Schedule some jobs
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job0", startTime.AddSeconds(5));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job2", startTime.AddSeconds(10));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target2"), "job1", startTime.AddSeconds(6));
        var lastJob = await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job3", startTime.AddSeconds(15));
        var jobToCancel = await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job4", startTime.AddSeconds(25));

        var counter = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await shard1.MarkAsComplete();
        await shard1.RemoveJobAsync(jobToCancel.Id);
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            await shard1.RemoveJobAsync(jobCtx.Job.Id);
            counter++;
        }
        Assert.Equal(4, counter);
        Assert.True(lastJob.DueTime <= DateTimeOffset.Now);
        await manager.UnregisterShard(localAddress, shard1);

        // No unassigned shards
        Assert.Empty(await manager.GetJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1)));
    }

    [Fact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_StopProcessingShard()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();
        options.Value.MaxShardDuration = TimeSpan.FromSeconds(20);
        options.Value.ContainerName = "jobshardmanager" + Guid.NewGuid();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var manager = new AzureStorageJobShardManager(options, membershipService);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata);

        // Schedule some jobs
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job3", DateTime.UtcNow.AddSeconds(10));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(6));
        await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job4", DateTime.UtcNow.AddSeconds(15));

        var counter = 1;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            if (counter == 2)
                break;
            await shard1.RemoveJobAsync(jobCtx.Job.Id);
            counter++;
        }
        Assert.Equal(2, counter);
        await manager.UnregisterShard(localAddress, shard1);

        var shards = await manager.GetJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Single(shards);
        Assert.Equal(shard1.Id, shards[0].Id);
    }

    [Fact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_RetryJobLater()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();
        options.Value.ContainerName = "jobshardmanager" + Guid.NewGuid();
        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var manager = new AzureStorageJobShardManager(options, membershipService);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);
        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata);
        // Schedule a job
        var job =  await shard1.ScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5));
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal("job1", jobCtx.Job.Name);
            var newDueTime = DateTimeOffset.UtcNow.AddSeconds(10);
            await shard1.RetryJobLaterAsync(jobCtx, newDueTime);
            break;
        }
        // Consume again
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal("job1", jobCtx.Job.Name);
            await shard1.RemoveJobAsync(jobCtx.Job.Id);
            break;
        }
        await manager.UnregisterShard(localAddress, shard1);
    }

    private class InMemoryClusterMembershipService : IClusterMembershipService
    {
        public ClusterMembershipSnapshot CurrentSnapshot => new ClusterMembershipSnapshot(silos.ToImmutableDictionary(), new MembershipVersion(_version));

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => throw new NotImplementedException();

        private Dictionary<SiloAddress, ClusterMember> silos = new();
        private int _version = 0;

        public void SetSiloStatus(SiloAddress address, SiloStatus status)
        {
            silos[address] = new ClusterMember(address, status, address.ToParsableString());
            _version++;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default) => throw new NotImplementedException();

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotImplementedException();
    }
}
