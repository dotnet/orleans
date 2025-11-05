using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.DurableJobs;
using Xunit;

namespace Tester.DurableJobs;

/// <summary>
/// Contains provider-agnostic test logic for job shard managers that can be run against different providers.
/// This class is similar to <see cref="DurableJobTestsRunner"/> but operates at the infrastructure layer,
/// testing shard lifecycle management, ownership, and failover semantics.
/// </summary>
public class JobShardManagerTestsRunner
{
    private readonly IJobShardManagerTestFixture _fixture;
    private readonly IDictionary<string, string> _testMetadata;
    private readonly InMemoryClusterMembershipService _membershipService;

    public JobShardManagerTestsRunner(IJobShardManagerTestFixture fixture)
    {
        _fixture = fixture;
        _testMetadata = new Dictionary<string, string>
        {
            { "CreatedBy", "UnitTest" },
            { "Purpose", "Testing" }
        };
        _membershipService = new InMemoryClusterMembershipService();
    }

    /// <summary>
    /// Sets the status of a silo in the cluster membership service.
    /// </summary>
    private void SetSiloStatus(SiloAddress siloAddress, SiloStatus status)
    {
        _membershipService.SetSiloStatus(siloAddress, status);
    }

    /// <summary>
    /// Creates a job shard manager for the given silo address.
    /// </summary>
    private JobShardManager CreateManager(SiloAddress siloAddress)
    {
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        return _fixture.CreateManager(localSiloDetails, _membershipService);
    }

    /// <summary>
    /// Tests basic shard creation and assignment workflow.
    /// Verifies that shards are created with unique IDs and correctly assigned to their creator silo.
    /// </summary>
    public async Task ShardCreationAndAssignment()
    {
        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);

        SetSiloStatus(silo1Address, SiloStatus.Active);
        SetSiloStatus(silo2Address, SiloStatus.Active);
        var silo1Manager = CreateManager(silo1Address);
        var silo2Manager = CreateManager(silo2Address);

        var date = DateTimeOffset.UtcNow;
        var maxDate = date.AddHours(1);

        // Register multiple shards and ensure they are distinct
        // two of them have the same time range
        var shard1 = await silo1Manager.CreateShardAsync(date, maxDate, _testMetadata, CancellationToken.None);
        var shard2 = await silo1Manager.CreateShardAsync(date, maxDate, _testMetadata, CancellationToken.None);
        var shard3 = await silo1Manager.CreateShardAsync(date.AddHours(2), maxDate, _testMetadata, CancellationToken.None);

        Assert.Distinct([shard1.Id, shard2.Id, shard3.Id]);

        // All shards are now assigned to the creator silo
        var assignedShards = await silo1Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(3), CancellationToken.None);
        Assert.Equal(3, assignedShards.Count);
        Assert.Contains(shard1.Id, assignedShards.Select(s => s.Id));
        Assert.Contains(shard2.Id, assignedShards.Select(s => s.Id));
        Assert.Contains(shard3.Id, assignedShards.Select(s => s.Id));
        var emptyShards = await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(3), CancellationToken.None);
        Assert.Empty(emptyShards);

        // Mark the local silo as dead
        SetSiloStatus(silo1Address, SiloStatus.Dead);

        // Now we can take over all three shards
        var shards = await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(3), CancellationToken.None);
        Assert.Equal(3, shards.Count);
        Assert.Contains(shard1.Id, shards.Select(s => s.Id));
        Assert.Contains(shard2.Id, shards.Select(s => s.Id));
        Assert.Contains(shard3.Id, shards.Select(s => s.Id));

        // Register another silo
        var silo3Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5002), 0);
        SetSiloStatus(silo3Address, SiloStatus.Active);
        var silo3Manager = CreateManager(silo3Address);

        // No unassigned shards
        Assert.Empty(await silo3Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None));
    }

    /// <summary>
    /// Tests reading and consuming jobs from a shard after ownership transfer.
    /// Verifies that jobs are preserved during failover and can be consumed by the new owner.
    /// </summary>
    public async Task ReadFrozenShard()
    {
        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        SetSiloStatus(silo1Address, SiloStatus.Active);
        SetSiloStatus(silo2Address, SiloStatus.Active);
        var silo1Manager = CreateManager(silo1Address);
        var silo2Manager = CreateManager(silo2Address);

        var date = DateTime.UtcNow;
        var shard1 = await silo1Manager.CreateShardAsync(date, date.AddHours(1), _testMetadata, CancellationToken.None);

        // Schedule some jobs
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", date.AddSeconds(1), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job3", date.AddSeconds(3), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", date.AddSeconds(2), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job4", date.AddSeconds(4), null, CancellationToken.None);

        // Mark the silo1 as dead, and create a new incarnation
        SetSiloStatus(silo1Address, SiloStatus.Dead);

        // Take over the shard
        var shards = await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);
        shard1 = shards[0];

        var counter = 1;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var jobCtx in shard1.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            await shard1.RemoveJobAsync(jobCtx.Job.Id, cts.Token);
            counter++;
        }
        Assert.Equal(5, counter);
        await silo2Manager.UnregisterShardAsync(shard1, CancellationToken.None);

        // No unassigned shards
        Assert.Empty(await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None));
    }

    /// <summary>
    /// Tests consuming jobs from a live shard (one that continues to accept new jobs).
    /// Verifies job scheduling, consumption, and cancellation during processing.
    /// </summary>
    public async Task LiveShard()
    {
        var startTime = DateTime.UtcNow;
        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);
        var manager = CreateManager(localAddress);

        var date = DateTime.UtcNow;
        var shard1 = await manager.CreateShardAsync(date, date.AddYears(1), _testMetadata, CancellationToken.None);

        // Schedule some jobs
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job0", startTime.AddSeconds(1), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job2", startTime.AddSeconds(3), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job1", startTime.AddSeconds(2), null, CancellationToken.None);
        var lastJob = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job3", startTime.AddSeconds(4), null, CancellationToken.None);
        var jobToCancel = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job4", startTime.AddSeconds(5), null, CancellationToken.None);

        var counter = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await shard1.MarkAsCompleteAsync(CancellationToken.None);
        await shard1.RemoveJobAsync(jobToCancel.Id, CancellationToken.None);
        await foreach (var jobCtx in shard1.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
            counter++;
        }
        Assert.Equal(4, counter);
        Assert.True(lastJob.DueTime <= DateTimeOffset.UtcNow);
        await manager.UnregisterShardAsync(shard1, CancellationToken.None);

        // No unassigned shards
        Assert.Empty(await manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None));
    }

    /// <summary>
    /// Tests job metadata persistence and retrieval across shard ownership transfer.
    /// </summary>
    public async Task JobMetadata()
    {
        // Initialize 2 silos with two managers
        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);

        SetSiloStatus(silo1Address, SiloStatus.Active);
        SetSiloStatus(silo2Address, SiloStatus.Active);
        var silo1Manager = CreateManager(silo1Address);
        var silo2Manager = CreateManager(silo2Address);

        var date = DateTime.UtcNow;
        var shard = await silo1Manager.CreateShardAsync(date, date.AddYears(1), _testMetadata, CancellationToken.None);

        // Schedule jobs with different metadata on a single shard
        var jobMetadata1 = new Dictionary<string, string>
        {
            { "Priority", "High" },
            { "Category", "Payment" },
            { "RequestId", "12345" }
        };
        var jobMetadata2 = new Dictionary<string, string>
        {
            { "Priority", "Low" },
            { "Category", "Notification" }
        };

        var job1 = await shard.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(1), jobMetadata1, CancellationToken.None);
        var job2 = await shard.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(2), jobMetadata2, CancellationToken.None);
        var job3 = await shard.TryScheduleJobAsync(GrainId.Create("type", "target3"), "job3", DateTime.UtcNow.AddSeconds(3), null, CancellationToken.None);

        // Verify metadata is set on the durable jobs
        Assert.Equal(jobMetadata1, job1.Metadata);
        Assert.Equal(jobMetadata2, job2.Metadata);
        Assert.Null(job3.Metadata);

        // Mark the silo owning the shard as dead
        SetSiloStatus(silo1Address, SiloStatus.Dead);

        // Take over the shard with the other silo
        var shards = await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);
        shard = shards[0];

        // Consume jobs and verify metadata is preserved
        var consumedJobs = new List<DurableJob>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var jobCtx in shard.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job);
            await shard.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
        }

        Assert.Equal(3, consumedJobs.Count);
        
        var consumedJob1 = consumedJobs.First(j => j.Name == "job1");
        var consumedJob2 = consumedJobs.First(j => j.Name == "job2");
        var consumedJob3 = consumedJobs.First(j => j.Name == "job3");

        Assert.Equal(jobMetadata1, consumedJob1.Metadata);
        Assert.Equal(jobMetadata2, consumedJob2.Metadata);
        Assert.Null(consumedJob3.Metadata);

        await silo2Manager.UnregisterShardAsync(shard, CancellationToken.None);
    }

    /// <summary>
    /// Tests concurrent shard assignment to verify that only one silo can claim ownership of an orphaned shard.
    /// </summary>
    public async Task ConcurrentShardAssignment_OwnershipConflicts()
    {
        // Initialize 3 silos with 3 managers
        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        var silo3Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5002), 0);

        SetSiloStatus(silo1Address, SiloStatus.Active);
        SetSiloStatus(silo2Address, SiloStatus.Active);
        SetSiloStatus(silo3Address, SiloStatus.Active);
        var silo1Manager = CreateManager(silo1Address);
        var silo2Manager = CreateManager(silo2Address);
        var silo3Manager = CreateManager(silo3Address);

        var date = DateTime.UtcNow;

        // Create two shards on the first silo
        var shard1 = await silo1Manager.CreateShardAsync(date, date.AddHours(1), _testMetadata, CancellationToken.None);
        var shard2 = await silo1Manager.CreateShardAsync(date, date.AddHours(2), _testMetadata, CancellationToken.None);

        // Mark the first silo as dead
        SetSiloStatus(silo1Address, SiloStatus.Dead);

        // Concurrently try to assign shards from silo2 and silo3
        var assignTask2 = silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(3), CancellationToken.None);
        var assignTask3 = silo3Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(3), CancellationToken.None);

        await Task.WhenAll(assignTask2, assignTask3);

        var shards2 = await assignTask2;
        var shards3 = await assignTask3;

        // Verify that only one silo was able to assign each shard (no duplicates)
        var totalAssignments = shards2.Count + shards3.Count;
        Assert.Equal(2, totalAssignments);

        var allAssignedShardIds = shards2.Select(s => s.Id).Concat(shards3.Select(s => s.Id)).ToList();
        Assert.Contains(shard1.Id, allAssignedShardIds);
        Assert.Contains(shard2.Id, allAssignedShardIds);
        Assert.Equal(2, allAssignedShardIds.Distinct().Count());
    }

    /// <summary>
    /// Tests that shard metadata is correctly preserved and merged during ownership transfers.
    /// </summary>
    public async Task ShardMetadataMerge()
    {
        // Initialize 2 silos with 2 managers
        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);

        SetSiloStatus(silo1Address, SiloStatus.Active);
        SetSiloStatus(silo2Address, SiloStatus.Active);
        var silo1Manager = CreateManager(silo1Address);
        var silo2Manager = CreateManager(silo2Address);

        var date = DateTime.UtcNow;

        // Create a shard on silo1 with some metadata, then update the metadata and verify it is merged correctly
        var customMetadata = new Dictionary<string, string>
        {
            { "Environment", "Production" },
            { "TenantId", "tenant-123" }
        };

        var shard = await silo1Manager.CreateShardAsync(date, date.AddHours(1), customMetadata, CancellationToken.None);
        Assert.NotNull(shard.Metadata);
        Assert.All(customMetadata, kvp =>
        {
            Assert.True(shard.Metadata.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value, shard.Metadata[kvp.Key]);
        });

        // Schedule a job to ensure shard persistence
        await shard.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);

        SetSiloStatus(silo1Address, SiloStatus.Dead);

        // Take over the shard from silo2 and verify the metadata is preserved
        var shards = await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);
        shard = shards[0];

        Assert.NotNull(shard.Metadata);
        Assert.All(customMetadata, kvp =>
        {
            Assert.True(shard.Metadata.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value, shard.Metadata[kvp.Key]);
        });
    }

    /// <summary>
    /// Tests stopping shard processing and verifying jobs remain for reassignment.
    /// </summary>
    public async Task StopProcessingShard()
    {
        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);
        var manager = CreateManager(localAddress);

        var date = DateTime.UtcNow;
        var shard1 = await manager.CreateShardAsync(date, date.AddYears(1), _testMetadata, CancellationToken.None);

        // Schedule some jobs
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job3", DateTime.UtcNow.AddSeconds(10), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(6), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job4", DateTime.UtcNow.AddSeconds(15), null, CancellationToken.None);

        var counter = 1;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await foreach (var jobCtx in shard1.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            if (counter == 2)
                break;
            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
            counter++;
        }
        Assert.Equal(2, counter);
        await manager.UnregisterShardAsync(shard1, CancellationToken.None);

        var shards = await manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);
        Assert.Equal(shard1.Id, shards[0].Id);
    }

    /// <summary>
    /// Tests retrying a job with a new due time.
    /// </summary>
    public async Task RetryJobLater()
    {
        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);
        var manager = CreateManager(localAddress);
        var date = DateTime.UtcNow;
        var shard1 = await manager.CreateShardAsync(date, date.AddYears(1), _testMetadata, CancellationToken.None);

        // Schedule a job
        var job = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(1), null, CancellationToken.None);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await foreach (var jobCtx in shard1.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal("job1", jobCtx.Job.Name);
            var newDueTime = DateTimeOffset.UtcNow.AddSeconds(1);
            await shard1.RetryJobLaterAsync(jobCtx, newDueTime, CancellationToken.None);
            break;
        }

        // Consume again
        await foreach (var jobCtx in shard1.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal("job1", jobCtx.Job.Name);
            Assert.NotEqual(job.DueTime, jobCtx.Job.DueTime);
            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
            break;
        }
        await manager.UnregisterShardAsync(shard1, CancellationToken.None);
    }
    

    /// <summary>
    /// Tests job cancellation before and during processing.
    /// </summary>
    public async Task JobCancellation()
    {
        // Initialize 2 silos with two managers
        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);

        SetSiloStatus(silo1Address, SiloStatus.Active);
        SetSiloStatus(silo2Address, SiloStatus.Active);
        var silo1Manager = CreateManager(silo1Address);
        var silo2Manager = CreateManager(silo2Address);

        var date = DateTime.UtcNow;
        var shard = await silo1Manager.CreateShardAsync(date, date.AddYears(1), _testMetadata, CancellationToken.None);

        // Schedule multiple jobs in a single shard
        var job1 = await shard.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddMilliseconds(500), null, CancellationToken.None);
        var job2 = await shard.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddMilliseconds(1000), null, CancellationToken.None);
        var job3 = await shard.TryScheduleJobAsync(GrainId.Create("type", "target3"), "job3", DateTime.UtcNow.AddMilliseconds(1500), null, CancellationToken.None);
        var job4 = await shard.TryScheduleJobAsync(GrainId.Create("type", "target4"), "job4", DateTime.UtcNow.AddMilliseconds(2000), null, CancellationToken.None);

        // Cancel job2 before processing starts
        await shard.RemoveJobAsync(job2.Id, CancellationToken.None);

        // Start consuming jobs
        var consumedJobs = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await foreach (var jobCtx in shard.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job.Name);

            // Cancel job4 during processing (after job1 is consumed)
            if (jobCtx.Job.Name == "job1")
            {
                await shard.RemoveJobAsync(job4.Id, CancellationToken.None);
            }

            await shard.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);

            if (consumedJobs.Count >= 2)
            {
                break;
            }
        }

        // Verify that only job1 and job3 were consumed (job2 was cancelled before consumption, job4 was cancelled during)
        Assert.Equal(2, consumedJobs.Count);
        Assert.Contains("job1", consumedJobs);
        Assert.Contains("job3", consumedJobs);
        Assert.DoesNotContain("job2", consumedJobs);
        Assert.DoesNotContain("job4", consumedJobs);

        // Mark the shard owner silo as dead and reassign to verify cancelled jobs are not in storage
        SetSiloStatus(silo1Address, SiloStatus.Dead);

        var shards = await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);
        shard = shards[0];

        var hasJobs = false;
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var jobCtx in shard.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            hasJobs = true;
            break;
        }

        Assert.False(hasJobs);
        await silo2Manager.UnregisterShardAsync(shard, CancellationToken.None);
    }

    /// <summary>
    /// Tests that multiple shard registrations with the same time range produce unique IDs.
    /// </summary>
    public async Task ShardRegistrationRetry_IdCollisions()
    {
        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        SetSiloStatus(localAddress, SiloStatus.Active);

        var manager = CreateManager(localAddress);

        var date = DateTime.UtcNow;

        var shard1 = await manager.CreateShardAsync(date, date.AddHours(1), _testMetadata, CancellationToken.None);
        var shard2 = await manager.CreateShardAsync(date, date.AddHours(1), _testMetadata, CancellationToken.None);
        var shard3 = await manager.CreateShardAsync(date, date.AddHours(1), _testMetadata, CancellationToken.None);

        Assert.Distinct([shard1.Id, shard2.Id, shard3.Id]);
    }

    /// <summary>
    /// Tests that unregistering a shard with remaining jobs preserves the shard for reassignment.
    /// </summary>
    public async Task UnregisterShard_WithJobsRemaining()
    {
        // Initialize 2 silos with 2 managers
        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);

        SetSiloStatus(silo1Address, SiloStatus.Active);
        SetSiloStatus(silo2Address, SiloStatus.Active);
        var silo1Manager = CreateManager(silo1Address);
        var silo2Manager = CreateManager(silo2Address);

        var date = DateTime.UtcNow;
        var shard = await silo1Manager.CreateShardAsync(date, date.AddHours(1), _testMetadata, CancellationToken.None);

        // Create a shard on silo1, schedule some jobs, then unregister the shard
        await shard.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(1), null, CancellationToken.None);
        await shard.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(2), null, CancellationToken.None);

        await silo1Manager.UnregisterShardAsync(shard, CancellationToken.None);

        // The shard should NOT have been deleted since there were jobs remaining
        SetSiloStatus(silo1Address, SiloStatus.Dead);

        // Take over the shard from silo2 and consume the jobs
        var shards = await silo2Manager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Single(shards);
        Assert.Equal(shard.Id, shards[0].Id);

        var consumedJobs = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var jobCtx in shards[0].ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job.Name);
            await shards[0].RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
        }

        Assert.Equal(2, consumedJobs.Count);
        Assert.Contains("job1", consumedJobs);
        Assert.Contains("job2", consumedJobs);
        await silo2Manager.UnregisterShardAsync(shards[0], CancellationToken.None);
    }

    /// <summary>
    /// Simple implementation of <see cref="ILocalSiloDetails"/> for testing.
    /// </summary>
    private sealed class TestLocalSiloDetails : ILocalSiloDetails
    {
        public TestLocalSiloDetails(SiloAddress siloAddress)
        {
            SiloAddress = siloAddress;
        }

        public string Name => SiloAddress.ToString();

        public string ClusterId => "TestCluster";

        public string DnsHostName => SiloAddress.ToString();

        public SiloAddress SiloAddress { get; }

        public SiloAddress GatewayAddress => SiloAddress;
    }

    /// <summary>
    /// Simple in-memory implementation of <see cref="IClusterMembershipService"/> for testing.
    /// </summary>
    private sealed class InMemoryClusterMembershipService : IClusterMembershipService
    {
        private readonly Dictionary<SiloAddress, ClusterMember> _silos = new();
        private int _version = 0;

        public ClusterMembershipSnapshot CurrentSnapshot => 
            new ClusterMembershipSnapshot(_silos.ToImmutableDictionary(), new MembershipVersion(_version));

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => throw new NotImplementedException();

        public void SetSiloStatus(SiloAddress address, SiloStatus status)
        {
            _silos[address] = new ClusterMember(address, status, address.ToParsableString());
            _version++;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => 
            ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotImplementedException();
    }
}
