using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_Creation_Assignation()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);

        var membershipService = new InMemoryClusterMembershipService();

        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, "creation-assignation-" + Guid.NewGuid(), membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTimeOffset.UtcNow;
        var maxDate = date.AddHours(1);

        // Register multiple shards and ensure they are distinct
        // two of them have the same time range
        var shard1 = await manager.RegisterShard(localAddress, date, maxDate, _metadata, assignToCreator: true);
        var shard2 = await manager.RegisterShard(localAddress, date, maxDate, _metadata, assignToCreator: true);
        var shard3 = await manager.RegisterShard(localAddress, date.AddHours(2), maxDate, _metadata, assignToCreator: false);

        Assert.Distinct([shard1.Id, shard2.Id, shard3.Id]);

        // shard3 was not assigned to the creator silo
        var assignedShard = Assert.Single(await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(3)));
        Assert.Equal(shard3.Id, assignedShard.Id);

        // Mark the local silo as dead, and create a new incarnation
        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        // Now we can take over the two first shards
        var shards = await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Equal(2, shards.Count);
        Assert.Contains(shard1.Id, shards.Select(s => s.Id));
        Assert.Contains(shard2.Id, shards.Select(s => s.Id));

        // Register another silo
        var otherSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        membershipService.SetSiloStatus(otherSilo, SiloStatus.Active);

        // No unassigned shards
        Assert.Empty(await manager.AssignJobShardsAsync(otherSilo, DateTime.UtcNow.AddHours(1)));
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ReadFrozenShard()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var prefix = "read-frozen-shard-" + Guid.NewGuid();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddHours(1), _metadata, assignToCreator: false);

        // Schedule some jobs
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job3", DateTime.UtcNow.AddSeconds(10), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(6), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job4", DateTime.UtcNow.AddSeconds(15), null, CancellationToken.None);

        // Mark the local silo as dead, and create a new incarnation
        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        // Take over the shard
        manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        var shards = await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Single(shards);
        shard1 = shards[0];

        var counter = 1;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            await shard1.RemoveJobAsync(jobCtx.Job.Id, cts.Token);
            counter++;
        }
        Assert.Equal(5, counter);
        await manager.UnregisterShard(localAddress, shard1);

        // No unassigned shards
        Assert.Empty(await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1)));
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_LiveShard()
    {
        var startTime = DateTime.UtcNow;
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, "live-shard-" + Guid.NewGuid(), membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata);

        // Schedule some jobs
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job0", startTime.AddSeconds(5), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job2", startTime.AddSeconds(10), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job1", startTime.AddSeconds(6), null, CancellationToken.None);
        var lastJob = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job3", startTime.AddSeconds(15), null, CancellationToken.None);
        var jobToCancel = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job4", startTime.AddSeconds(25), null, CancellationToken.None);

        var counter = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await shard1.MarkAsCompleteAsync(CancellationToken.None);
        await shard1.RemoveJobAsync(jobToCancel.Id, CancellationToken.None);
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
            counter++;
        }
        Assert.Equal(4, counter);
        Assert.True(lastJob.DueTime <= DateTimeOffset.UtcNow);
        await manager.UnregisterShard(localAddress, shard1);

        // No unassigned shards
        Assert.Empty(await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1)));
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_StopProcessingShard()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, "stop-processing-" + Guid.NewGuid(), membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata, assignToCreator: true);

        // Schedule some jobs
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job3", DateTime.UtcNow.AddSeconds(10), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(6), null, CancellationToken.None);
        await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job4", DateTime.UtcNow.AddSeconds(15), null, CancellationToken.None);

        var counter = 1;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal($"job{counter}", jobCtx.Job.Name);
            if (counter == 2)
                break;
            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
            counter++;
        }
        Assert.Equal(2, counter);
        await manager.UnregisterShard(localAddress, shard1);

        var shards = await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Single(shards);
        Assert.Equal(shard1.Id, shards[0].Id);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_RetryJobLater()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();
        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, "retry-job-later-" + Guid.NewGuid(), membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);
        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata);
        // Schedule a job
        var job =  await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal("job1", jobCtx.Job.Name);
            var newDueTime = DateTimeOffset.UtcNow.AddSeconds(10);
            await shard1.RetryJobLaterAsync(jobCtx, newDueTime, CancellationToken.None);
            break;
        }
        // Consume again
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            Assert.Equal("job1", jobCtx.Job.Name);
            Assert.NotEqual(job.DueTime, jobCtx.Job.DueTime);
            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
            break;
        }
        await manager.UnregisterShard(localAddress, shard1);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_JobMetadata()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var prefix = "job-metadata-" + Guid.NewGuid();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata, assignToCreator: true);

        // Schedule jobs with different metadata
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

        var job1 = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), jobMetadata1, CancellationToken.None);
        var job2 = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(10), jobMetadata2, CancellationToken.None);
        var job3 = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target3"), "job3", DateTime.UtcNow.AddSeconds(15), null, CancellationToken.None);

        // Verify metadata is set on the scheduled jobs
        Assert.NotNull(job1.Metadata);
        Assert.Equal(3, job1.Metadata.Count);
        Assert.Equal("High", job1.Metadata["Priority"]);
        Assert.Equal("Payment", job1.Metadata["Category"]);
        Assert.Equal("12345", job1.Metadata["RequestId"]);

        Assert.NotNull(job2.Metadata);
        Assert.Equal(2, job2.Metadata.Count);
        Assert.Equal("Low", job2.Metadata["Priority"]);
        Assert.Equal("Notification", job2.Metadata["Category"]);

        Assert.Null(job3.Metadata);

        // Mark the local silo as dead and create a new incarnation to test metadata persistence
        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        // Take over the shard
        manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        var shards = await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Single(shards);
        shard1 = shards[0];

        // Consume jobs and verify metadata is preserved
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var jobsConsumed = 0;
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            jobsConsumed++;
            
            if (jobCtx.Job.Name == "job1")
            {
                Assert.NotNull(jobCtx.Job.Metadata);
                Assert.Equal(3, jobCtx.Job.Metadata.Count);
                Assert.Equal("High", jobCtx.Job.Metadata["Priority"]);
                Assert.Equal("Payment", jobCtx.Job.Metadata["Category"]);
                Assert.Equal("12345", jobCtx.Job.Metadata["RequestId"]);
            }
            else if (jobCtx.Job.Name == "job2")
            {
                Assert.NotNull(jobCtx.Job.Metadata);
                Assert.Equal(2, jobCtx.Job.Metadata.Count);
                Assert.Equal("Low", jobCtx.Job.Metadata["Priority"]);
                Assert.Equal("Notification", jobCtx.Job.Metadata["Category"]);
            }
            else if (jobCtx.Job.Name == "job3")
            {
                Assert.Null(jobCtx.Job.Metadata);
            }

            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
        }

        Assert.Equal(3, jobsConsumed);
        await manager.UnregisterShard(localAddress, shard1);

        // No unassigned shards
        Assert.Empty(await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1)));
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_JobCancellation()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        var prefix = "job-cancellation-" + Guid.NewGuid();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var date = DateTime.UtcNow;
        var shard1 = await manager.RegisterShard(localAddress, date, date.AddYears(1), _metadata, assignToCreator: true);

        // Schedule multiple jobs
        var job1 = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);
        var job2 = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(10), null, CancellationToken.None);
        var job3 = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target3"), "job3", DateTime.UtcNow.AddSeconds(15), null, CancellationToken.None);
        var job4 = await shard1.TryScheduleJobAsync(GrainId.Create("type", "target4"), "job4", DateTime.UtcNow.AddSeconds(20), null, CancellationToken.None);

        // Cancel job2 before processing starts
        await shard1.RemoveJobAsync(job2.Id, CancellationToken.None);

        // Verify initial job count (should be 3 after cancellation)
        var jobCount = await shard1.GetJobCountAsync();
        Assert.Equal(3, jobCount);

        // Start consuming jobs
        var consumedJobs = new List<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            consumedJobs.Add(jobCtx.Job.Name);
            
            // Cancel job4 during processing (after job1 is consumed)
            if (jobCtx.Job.Name == "job1")
            {
                await shard1.RemoveJobAsync(job4.Id, CancellationToken.None);
            }
            
            await shard1.RemoveJobAsync(jobCtx.Job.Id, CancellationToken.None);
            
            // Stop after consuming 2 jobs (job1 and job3)
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

        // Mark shard as dead and reassign to verify cancelled jobs are not in storage
        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        // Take over the shard
        manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        var shards = await manager.AssignJobShardsAsync(localAddress, DateTime.UtcNow.AddHours(1));
        Assert.Single(shards);
        shard1 = shards[0];

        // Verify no jobs remain (job1 and job3 were removed after consumption, job2 and job4 were cancelled)
        var remainingJobCount = await shard1.GetJobCountAsync();
        Assert.Equal(0, remainingJobCount);

        // Ensure no jobs are yielded when consuming
        var hasJobs = false;
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var jobCtx in shard1.ConsumeScheduledJobsAsync().WithCancellation(cts.Token))
        {
            hasJobs = true;
            break;
        }

        Assert.False(hasJobs);
        await manager.UnregisterShard(localAddress, shard1);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ConcurrentShardAssignment_OwnershipConflicts()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);

        var membershipService = new InMemoryClusterMembershipService();
        membershipService.SetSiloStatus(silo1Address, SiloStatus.Active);
        membershipService.SetSiloStatus(silo2Address, SiloStatus.Active);

        var prefix = "concurrent-ownership-" + Guid.NewGuid();
        var manager1 = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        var manager2 = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        var date = DateTime.UtcNow;

        var shard = await manager1.RegisterShard(silo1Address, date, date.AddHours(1), _metadata, assignToCreator: false);

        membershipService.SetSiloStatus(silo1Address, SiloStatus.Dead);

        var assignTask1 = manager1.AssignJobShardsAsync(silo2Address, DateTime.UtcNow.AddHours(1));
        var assignTask2 = manager2.AssignJobShardsAsync(silo2Address, DateTime.UtcNow.AddHours(1));

        await Task.WhenAll(assignTask1, assignTask2);

        var shards1 = await assignTask1;
        var shards2 = await assignTask2;

        var totalAssigned = shards1.Count + shards2.Count;
        Assert.Equal(1, totalAssigned);

        var assignedShard = shards1.Count > 0 ? shards1[0] : shards2[0];
        Assert.Equal(shard.Id, assignedShard.Id);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ShardRegistrationRetry_IdCollisions()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, "registration-retry-" + Guid.NewGuid(), membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        var date = DateTime.UtcNow;

        var shard1 = await manager.RegisterShard(localAddress, date, date.AddHours(1), _metadata, assignToCreator: true);
        var shard2 = await manager.RegisterShard(localAddress, date, date.AddHours(1), _metadata, assignToCreator: true);
        var shard3 = await manager.RegisterShard(localAddress, date, date.AddHours(1), _metadata, assignToCreator: true);

        Assert.Distinct([shard1.Id, shard2.Id, shard3.Id]);

        Assert.Contains($"{date:yyyyMMddHHmm}-{localAddress.ToParsableString()}-", shard1.Id);
        Assert.Contains($"{date:yyyyMMddHHmm}-{localAddress.ToParsableString()}-", shard2.Id);
        Assert.Contains($"{date:yyyyMMddHHmm}-{localAddress.ToParsableString()}-", shard3.Id);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_MultipleSilosCompetingForShard()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        var silo3Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5002), 0);

        var membershipService = new InMemoryClusterMembershipService();
        membershipService.SetSiloStatus(silo1Address, SiloStatus.Active);

        var prefix = "multiple-silos-" + Guid.NewGuid();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        var date = DateTime.UtcNow;
        var shard = await manager.RegisterShard(silo1Address, date, date.AddHours(1), _metadata, assignToCreator: false);

        membershipService.SetSiloStatus(silo1Address, SiloStatus.Dead);
        membershipService.SetSiloStatus(silo2Address, SiloStatus.Active);
        membershipService.SetSiloStatus(silo3Address, SiloStatus.Active);

        var manager2 = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        var manager3 = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        var task2 = manager2.AssignJobShardsAsync(silo2Address, DateTime.UtcNow.AddHours(1));
        var task3 = manager3.AssignJobShardsAsync(silo3Address, DateTime.UtcNow.AddHours(1));

        await Task.WhenAll(task2, task3);

        var shards2 = await task2;
        var shards3 = await task3;

        var totalAssignments = shards2.Count + shards3.Count;
        Assert.Equal(1, totalAssignments);

        var assignedToSilo2 = shards2.Any(s => s.Id == shard.Id);
        var assignedToSilo3 = shards3.Any(s => s.Id == shard.Id);

        Assert.True(assignedToSilo2 ^ assignedToSilo3);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_UnregisterShard_WrongOwner()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var silo1Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2Address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);

        var membershipService = new InMemoryClusterMembershipService();
        membershipService.SetSiloStatus(silo1Address, SiloStatus.Active);
        membershipService.SetSiloStatus(silo2Address, SiloStatus.Active);

        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, "unregister-wrong-owner-" + Guid.NewGuid(), membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        var date = DateTime.UtcNow;
        var shard = await manager.RegisterShard(silo1Address, date, date.AddHours(1), _metadata, assignToCreator: true);

        await shard.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await manager.UnregisterShard(silo2Address, shard));

        Assert.Contains("Cannot unregister a shard owned by another silo", exception.Message);

        var jobCount = await shard.GetJobCountAsync();
        Assert.Equal(1, jobCount);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_UnregisterShard_WithJobsRemaining()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var prefix = "unregister-jobs-remaining-" + Guid.NewGuid();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        var date = DateTime.UtcNow;
        var shard = await manager.RegisterShard(localAddress, date, date.AddHours(1), _metadata, assignToCreator: true);

        await shard.TryScheduleJobAsync(GrainId.Create("type", "target1"), "job1", DateTime.UtcNow.AddSeconds(5), null, CancellationToken.None);
        await shard.TryScheduleJobAsync(GrainId.Create("type", "target2"), "job2", DateTime.UtcNow.AddSeconds(10), null, CancellationToken.None);

        var jobCount = await shard.GetJobCountAsync();
        Assert.Equal(2, jobCount);

        await manager.UnregisterShard(localAddress, shard);

        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        var newSiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(newSiloAddress, SiloStatus.Active);

        var newManager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        var shards = await newManager.AssignJobShardsAsync(newSiloAddress, DateTime.UtcNow.AddHours(1));

        Assert.Single(shards);
        Assert.Equal(shard.Id, shards[0].Id);

        var remainingJobs = await shards[0].GetJobCountAsync();
        Assert.Equal(2, remainingJobs);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ShardMetadataMerge()
    {
        var options = Options.Create(new AzureStorageJobShardOptions());
        options.Value.ConfigureTestDefaults();

        var localAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var membershipService = new InMemoryClusterMembershipService();
        membershipService.SetSiloStatus(localAddress, SiloStatus.Active);

        var prefix = "metadata-merge-" + Guid.NewGuid();
        var manager = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);

        var date = DateTime.UtcNow;
        var customMetadata = new Dictionary<string, string>
        {
            { "Environment", "Production" },
            { "TenantId", "tenant-123" }
        };

        var shard = await manager.RegisterShard(localAddress, date, date.AddHours(1), customMetadata, assignToCreator: false);
        Assert.NotNull(shard.Metadata);
        Assert.Equal(customMetadata, shard.Metadata);

        var localAddress2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 1);
        membershipService.SetSiloStatus(localAddress, SiloStatus.Dead);
        membershipService.SetSiloStatus(localAddress2, SiloStatus.Active);

        var manager2 = new AzureStorageJobShardManager(options.Value.BlobServiceClient, options.Value.ContainerName, prefix, membershipService, NullLogger<AzureStorageJobShardManager>.Instance);
        var shard2 = await manager2.RegisterShard(localAddress, date, date.AddHours(1), customMetadata, assignToCreator: false);
        Assert.NotNull(shard2.Metadata);
        Assert.Equal(customMetadata, shard2.Metadata);
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

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotImplementedException();
    }
}
