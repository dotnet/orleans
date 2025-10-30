using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Internal;
using Orleans.ScheduledJobs;
using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests;

public class ScheduledJobTests : HostedTestClusterEnsureDefaultStarted
{
    public ScheduledJobTests(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_ScheduledJobGrain()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-job-grain");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(5);
        var job1 = await grain.ScheduleJobAsync("TestJob", dueTime);
        Assert.NotNull(job1);
        Assert.Equal("TestJob", job1.Name);
        Assert.Equal(dueTime, job1.DueTime);
        var job2 = await grain.ScheduleJobAsync("TestJob2", dueTime);
        var job3 = await grain.ScheduleJobAsync("TestJob3", dueTime.AddSeconds(4));
        var job4 = await grain.ScheduleJobAsync("TestJob4", dueTime);
        var job5 = await grain.ScheduleJobAsync("TestJob5", dueTime.AddSeconds(1));
        var canceledJob = await grain.ScheduleJobAsync("CanceledJob", dueTime.AddSeconds(2));
        Assert.True(await grain.TryCancelJobAsync(canceledJob));
        // Wait for the job to run
        foreach (var job in new[] { job1, job2, job3, job4, job5 })
        {
            try
            {
                await grain.WaitForJobToRun(job.Id).WithTimeout(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                Assert.Fail($"The scheduled job {job.Name} did not run within the expected time.");
            }
        }
        // Verify the canceled job did not run
        Assert.False(await grain.HasJobRan(canceledJob.Id));
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_JobExecutionOrder()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-execution-order");
        var baseTime = DateTimeOffset.UtcNow.AddSeconds(2);

        var job1 = await grain.ScheduleJobAsync("FirstJob", baseTime);
        var job2 = await grain.ScheduleJobAsync("SecondJob", baseTime.AddSeconds(2));
        var job3 = await grain.ScheduleJobAsync("ThirdJob", baseTime.AddSeconds(4));

        await grain.WaitForJobToRun(job1.Id).WithTimeout(TimeSpan.FromSeconds(10));
        await grain.WaitForJobToRun(job2.Id).WithTimeout(TimeSpan.FromSeconds(10));
        await grain.WaitForJobToRun(job3.Id).WithTimeout(TimeSpan.FromSeconds(10));

        var time1 = await grain.GetJobExecutionTime(job1.Id);
        var time2 = await grain.GetJobExecutionTime(job2.Id);
        var time3 = await grain.GetJobExecutionTime(job3.Id);

        Assert.True(time1 < time2, $"Job1 executed at {time1}, Job2 at {time2}");
        Assert.True(time2 < time3, $"Job2 executed at {time2}, Job3 at {time3}");
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_PastDueTime()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-past-due");
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-5);

        var job = await grain.ScheduleJobAsync("PastDueJob", pastTime);
        Assert.NotNull(job);

        await grain.WaitForJobToRun(job.Id).WithTimeout(TimeSpan.FromSeconds(5));
        Assert.True(await grain.HasJobRan(job.Id));
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_JobWithMetadata()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-metadata");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);
        var metadata = new Dictionary<string, string>
        {
            ["UserId"] = "user123",
            ["Action"] = "SendEmail",
            ["Priority"] = "High"
        };

        var job = await grain.ScheduleJobAsync("MetadataJob", dueTime, metadata);
        Assert.NotNull(job);
        Assert.NotNull(job.Metadata);
        Assert.Equal(3, job.Metadata.Count);
        Assert.Equal("user123", job.Metadata["UserId"]);
        Assert.Equal("SendEmail", job.Metadata["Action"]);
        Assert.Equal("High", job.Metadata["Priority"]);

        await grain.WaitForJobToRun(job.Id).WithTimeout(TimeSpan.FromSeconds(10));

        var context = await grain.GetJobContext(job.Id);
        Assert.NotNull(context);
        Assert.NotNull(context.Job.Metadata);
        Assert.Equal("user123", context.Job.Metadata["UserId"]);
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_MultipleGrains()
    {
        var grain1 = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-grain-1");
        var grain2 = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-grain-2");
        var grain3 = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-grain-3");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job1 = await grain1.ScheduleJobAsync("Job1", dueTime);
        var job2 = await grain2.ScheduleJobAsync("Job2", dueTime);
        var job3 = await grain3.ScheduleJobAsync("Job3", dueTime);

        await grain1.WaitForJobToRun(job1.Id).WithTimeout(TimeSpan.FromSeconds(10));
        await grain2.WaitForJobToRun(job2.Id).WithTimeout(TimeSpan.FromSeconds(10));
        await grain3.WaitForJobToRun(job3.Id).WithTimeout(TimeSpan.FromSeconds(10));

        Assert.True(await grain1.HasJobRan(job1.Id));
        Assert.True(await grain2.HasJobRan(job2.Id));
        Assert.True(await grain3.HasJobRan(job3.Id));

        Assert.False(await grain1.HasJobRan(job2.Id));
        Assert.False(await grain2.HasJobRan(job3.Id));
        Assert.False(await grain3.HasJobRan(job1.Id));
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_DuplicateJobNames()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-duplicate-names");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job1 = await grain.ScheduleJobAsync("SameName", dueTime);
        var job2 = await grain.ScheduleJobAsync("SameName", dueTime.AddSeconds(1));
        var job3 = await grain.ScheduleJobAsync("SameName", dueTime.AddSeconds(2));

        Assert.NotEqual(job1.Id, job2.Id);
        Assert.NotEqual(job2.Id, job3.Id);
        Assert.NotEqual(job1.Id, job3.Id);

        Assert.Equal("SameName", job1.Name);
        Assert.Equal("SameName", job2.Name);
        Assert.Equal("SameName", job3.Name);

        await grain.WaitForJobToRun(job1.Id).WithTimeout(TimeSpan.FromSeconds(10));
        await grain.WaitForJobToRun(job2.Id).WithTimeout(TimeSpan.FromSeconds(10));
        await grain.WaitForJobToRun(job3.Id).WithTimeout(TimeSpan.FromSeconds(10));

        Assert.True(await grain.HasJobRan(job1.Id));
        Assert.True(await grain.HasJobRan(job2.Id));
        Assert.True(await grain.HasJobRan(job3.Id));
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_CancelNonExistentJob()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-cancel-nonexistent");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(10);

        var job = await grain.ScheduleJobAsync("RealJob", dueTime);
        
        var fakeJob = new ScheduledJob
        {
            Id = "non-existent-id",
            Name = "FakeJob",
            DueTime = dueTime,
            ShardId = job.ShardId,
            TargetGrainId = job.TargetGrainId
        };

        var cancelResult = await grain.TryCancelJobAsync(fakeJob);
        Assert.False(cancelResult);

        await Task.Delay(100);
        Assert.False(await grain.HasJobRan(fakeJob.Id));
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_CancelAlreadyExecutedJob()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-cancel-executed");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(2);

        var job = await grain.ScheduleJobAsync("QuickJob", dueTime);

        await grain.WaitForJobToRun(job.Id).WithTimeout(TimeSpan.FromSeconds(10));
        Assert.True(await grain.HasJobRan(job.Id));

        var cancelResult = await grain.TryCancelJobAsync(job);
        Assert.False(cancelResult);
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_ConcurrentScheduling()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-concurrent");
        var baseTime = DateTimeOffset.UtcNow.AddSeconds(5);
        var jobCount = 20;

        var scheduleTasks = new List<Task<IScheduledJob>>();
        for (int i = 0; i < jobCount; i++)
        {
            scheduleTasks.Add(grain.ScheduleJobAsync($"ConcurrentJob{i}", baseTime.AddMilliseconds(i * 100)));
        }

        var jobs = await Task.WhenAll(scheduleTasks);

        Assert.Equal(jobCount, jobs.Length);
        Assert.Equal(jobCount, jobs.Select(j => j.Id).Distinct().Count());

        var waitTasks = jobs.Select(j => grain.WaitForJobToRun(j.Id).WithTimeout(TimeSpan.FromSeconds(15)));
        await Task.WhenAll(waitTasks);

        foreach (var job in jobs)
        {
            Assert.True(await grain.HasJobRan(job.Id), $"Job {job.Name} did not run");
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_JobPropertiesVerification()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-properties");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);
        var metadata = new Dictionary<string, string> { ["Key"] = "Value" };

        var job = await grain.ScheduleJobAsync("PropertyTestJob", dueTime, metadata);

        Assert.NotNull(job.Id);
        Assert.NotEmpty(job.Id);
        Assert.Equal("PropertyTestJob", job.Name);
        Assert.Equal(dueTime, job.DueTime);
        Assert.NotNull(job.ShardId);
        Assert.NotEmpty(job.ShardId);
        Assert.NotNull(job.Metadata);
        Assert.Single(job.Metadata);

        await grain.WaitForJobToRun(job.Id).WithTimeout(TimeSpan.FromSeconds(10));

        var context = await grain.GetJobContext(job.Id);
        Assert.NotNull(context);
        Assert.Equal(job.Id, context.Job.Id);
        Assert.Equal(job.Name, context.Job.Name);
        Assert.NotNull(context.RunId);
        Assert.NotEmpty(context.RunId);
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_DequeueCount()
    {
        var grain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("test-dequeue-count");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job = await grain.ScheduleJobAsync("DequeueTestJob", dueTime);

        await grain.WaitForJobToRun(job.Id).WithTimeout(TimeSpan.FromSeconds(10));

        var context = await grain.GetJobContext(job.Id);
        Assert.NotNull(context);
        Assert.Equal(1, context.DequeueCount);
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public async Task Test_ScheduleJobOnAnotherGrain()
    {
        var schedulerGrain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.ISchedulerGrain>("scheduler-grain");
        var targetGrain = this.GrainFactory.GetGrain<UnitTests.GrainInterfaces.IScheduledJobGrain>("target-grain");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job = await schedulerGrain.ScheduleJobOnAnotherGrainAsync("target-grain", "CrossGrainJob", dueTime);

        Assert.NotNull(job);
        Assert.Equal("CrossGrainJob", job.Name);
        Assert.Equal(dueTime, job.DueTime);

        await targetGrain.WaitForJobToRun(job.Id).WithTimeout(TimeSpan.FromSeconds(10));

        Assert.True(await targetGrain.HasJobRan(job.Id));

        var context = await targetGrain.GetJobContext(job.Id);
        Assert.NotNull(context);
        Assert.Equal(job.Id, context.Job.Id);
        Assert.Equal("CrossGrainJob", context.Job.Name);
    }
}
