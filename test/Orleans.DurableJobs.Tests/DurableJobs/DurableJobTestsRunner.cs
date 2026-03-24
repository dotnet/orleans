using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.DurableJobs;
using Xunit;
using UnitTests.GrainInterfaces;

namespace Tester.DurableJobs;

/// <summary>
/// Contains the test logic for durable jobs that can be run against different providers.
/// This class is provider-agnostic and can be reused by test classes for InMemory, Azure, and other providers.
/// </summary>
public class DurableJobTestsRunner
{
    private readonly IGrainFactory _grainFactory;

    public DurableJobTestsRunner(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task DurableJobGrain(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-job-grain");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(5);
        var job1 = await grain.ScheduleJobAsync("TestJob", dueTime).WaitAsync(cancellationToken);
        Assert.NotNull(job1);
        Assert.Equal("TestJob", job1.Name);
        Assert.Equal(dueTime, job1.DueTime);
        var job2 = await grain.ScheduleJobAsync("TestJob2", dueTime).WaitAsync(cancellationToken);
        var job3 = await grain.ScheduleJobAsync("TestJob3", dueTime.AddSeconds(4)).WaitAsync(cancellationToken);
        var job4 = await grain.ScheduleJobAsync("TestJob4", dueTime).WaitAsync(cancellationToken);
        var job5 = await grain.ScheduleJobAsync("TestJob5", dueTime.AddSeconds(1)).WaitAsync(cancellationToken);
        var canceledJob = await grain.ScheduleJobAsync("CanceledJob", dueTime.AddSeconds(2)).WaitAsync(cancellationToken);
        Assert.True(await grain.TryCancelJobAsync(canceledJob).WaitAsync(cancellationToken));
        // Wait for the job to run
        foreach (var job in new[] { job1, job2, job3, job4, job5 })
        {
            try
            {
                await grain.WaitForJobToRun(job.Id).WaitAsync(cancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"The durable job {job.Name} did not run within the expected time.");
            }
        }
        // Verify the canceled job did not run
        Assert.False(await grain.HasJobRan(canceledJob.Id).WaitAsync(cancellationToken));
    }

    public async Task JobExecutionOrder(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-execution-order");
        var baseTime = DateTimeOffset.UtcNow.AddSeconds(2);

        var job1 = await grain.ScheduleJobAsync("FirstJob", baseTime).WaitAsync(cancellationToken);
        var job2 = await grain.ScheduleJobAsync("SecondJob", baseTime.AddSeconds(2)).WaitAsync(cancellationToken);
        var job3 = await grain.ScheduleJobAsync("ThirdJob", baseTime.AddSeconds(4)).WaitAsync(cancellationToken);

        await grain.WaitForJobToRun(job1.Id).WaitAsync(cancellationToken);
        await grain.WaitForJobToRun(job2.Id).WaitAsync(cancellationToken);
        await grain.WaitForJobToRun(job3.Id).WaitAsync(cancellationToken);

        var time1 = await grain.GetJobExecutionTime(job1.Id).WaitAsync(cancellationToken);
        var time2 = await grain.GetJobExecutionTime(job2.Id).WaitAsync(cancellationToken);
        var time3 = await grain.GetJobExecutionTime(job3.Id).WaitAsync(cancellationToken);

        Assert.True(time1 < time2, $"Job1 executed at {time1}, Job2 at {time2}");
        Assert.True(time2 < time3, $"Job2 executed at {time2}, Job3 at {time3}");
    }

    public async Task PastDueTime(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-past-due");
        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-5);

        var job = await grain.ScheduleJobAsync("PastDueJob", pastTime).WaitAsync(cancellationToken);
        Assert.NotNull(job);

        await grain.WaitForJobToRun(job.Id).WaitAsync(cancellationToken);
        Assert.True(await grain.HasJobRan(job.Id).WaitAsync(cancellationToken));
    }

    public async Task JobWithMetadata(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-metadata");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);
        var metadata = new Dictionary<string, string>
        {
            ["UserId"] = "user123",
            ["Action"] = "SendEmail",
            ["Priority"] = "High"
        };

        var job = await grain.ScheduleJobAsync("MetadataJob", dueTime, metadata).WaitAsync(cancellationToken);
        Assert.NotNull(job);
        Assert.NotNull(job.Metadata);
        Assert.Equal(3, job.Metadata.Count);
        Assert.Equal("user123", job.Metadata["UserId"]);
        Assert.Equal("SendEmail", job.Metadata["Action"]);
        Assert.Equal("High", job.Metadata["Priority"]);

        await grain.WaitForJobToRun(job.Id).WaitAsync(cancellationToken);

        var context = await grain.GetJobRun(job.Id).WaitAsync(cancellationToken);
        Assert.NotNull(context);
        Assert.NotNull(context.Job.Metadata);
        Assert.Equal("user123", context.Job.Metadata["UserId"]);
    }

    public async Task MultipleGrains(CancellationToken cancellationToken)
    {
        var grain1 = _grainFactory.GetGrain<IDurableJobGrain>("test-grain-1");
        var grain2 = _grainFactory.GetGrain<IDurableJobGrain>("test-grain-2");
        var grain3 = _grainFactory.GetGrain<IDurableJobGrain>("test-grain-3");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job1 = await grain1.ScheduleJobAsync("Job1", dueTime).WaitAsync(cancellationToken);
        var job2 = await grain2.ScheduleJobAsync("Job2", dueTime).WaitAsync(cancellationToken);
        var job3 = await grain3.ScheduleJobAsync("Job3", dueTime).WaitAsync(cancellationToken);

        await grain1.WaitForJobToRun(job1.Id).WaitAsync(cancellationToken);
        await grain2.WaitForJobToRun(job2.Id).WaitAsync(cancellationToken);
        await grain3.WaitForJobToRun(job3.Id).WaitAsync(cancellationToken);

        Assert.True(await grain1.HasJobRan(job1.Id).WaitAsync(cancellationToken));
        Assert.True(await grain2.HasJobRan(job2.Id).WaitAsync(cancellationToken));
        Assert.True(await grain3.HasJobRan(job3.Id).WaitAsync(cancellationToken));

        Assert.False(await grain1.HasJobRan(job2.Id).WaitAsync(cancellationToken));
        Assert.False(await grain2.HasJobRan(job3.Id).WaitAsync(cancellationToken));
        Assert.False(await grain3.HasJobRan(job1.Id).WaitAsync(cancellationToken));
    }

    public async Task DuplicateJobNames(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-duplicate-names");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job1 = await grain.ScheduleJobAsync("SameName", dueTime).WaitAsync(cancellationToken);
        var job2 = await grain.ScheduleJobAsync("SameName", dueTime.AddSeconds(1)).WaitAsync(cancellationToken);
        var job3 = await grain.ScheduleJobAsync("SameName", dueTime.AddSeconds(2)).WaitAsync(cancellationToken);

        Assert.NotEqual(job1.Id, job2.Id);
        Assert.NotEqual(job2.Id, job3.Id);
        Assert.NotEqual(job1.Id, job3.Id);

        Assert.Equal("SameName", job1.Name);
        Assert.Equal("SameName", job2.Name);
        Assert.Equal("SameName", job3.Name);

        await grain.WaitForJobToRun(job1.Id).WaitAsync(cancellationToken);
        await grain.WaitForJobToRun(job2.Id).WaitAsync(cancellationToken);
        await grain.WaitForJobToRun(job3.Id).WaitAsync(cancellationToken);

        Assert.True(await grain.HasJobRan(job1.Id).WaitAsync(cancellationToken));
        Assert.True(await grain.HasJobRan(job2.Id).WaitAsync(cancellationToken));
        Assert.True(await grain.HasJobRan(job3.Id).WaitAsync(cancellationToken));
    }

    public async Task CancelNonExistentJob(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-cancel-nonexistent");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(10);

        var job = await grain.ScheduleJobAsync("RealJob", dueTime).WaitAsync(cancellationToken);
        
        var fakeJob = new DurableJob
        {
            Id = "non-existent-id",
            Name = "FakeJob",
            DueTime = dueTime,
            ShardId = job.ShardId,
            TargetGrainId = job.TargetGrainId
        };

        var cancelResult = await grain.TryCancelJobAsync(fakeJob).WaitAsync(cancellationToken);
        Assert.False(cancelResult);

        await Task.Delay(100, cancellationToken);
        Assert.False(await grain.HasJobRan(fakeJob.Id).WaitAsync(cancellationToken));
    }

    public async Task CancelAlreadyExecutedJob(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-cancel-executed");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(2);

        var job = await grain.ScheduleJobAsync("QuickJob", dueTime).WaitAsync(cancellationToken);

        await grain.WaitForJobToRun(job.Id).WaitAsync(cancellationToken);
        Assert.True(await grain.HasJobRan(job.Id).WaitAsync(cancellationToken));

        var cancelResult = await grain.TryCancelJobAsync(job).WaitAsync(cancellationToken);
        Assert.False(cancelResult);
    }

    public async Task ConcurrentScheduling(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-concurrent");
        var baseTime = DateTimeOffset.UtcNow.AddSeconds(5);
        var jobCount = 20;

        var scheduleTasks = new List<Task<DurableJob>>();
        for (int i = 0; i < jobCount; i++)
        {
            scheduleTasks.Add(grain.ScheduleJobAsync($"ConcurrentJob{i}", baseTime.AddMilliseconds(i * 100)));
        }

        var jobs = await Task.WhenAll(scheduleTasks).WaitAsync(cancellationToken);

        Assert.Equal(jobCount, jobs.Length);
        Assert.Equal(jobCount, jobs.Select(j => j.Id).Distinct().Count());

        var waitTasks = jobs.Select(j => grain.WaitForJobToRun(j.Id));
        await Task.WhenAll(waitTasks).WaitAsync(cancellationToken);

        foreach (var job in jobs)
        {
            Assert.True(await grain.HasJobRan(job.Id).WaitAsync(cancellationToken), $"Job {job.Name} did not run");
        }
    }

    public async Task JobPropertiesVerification(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-properties");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);
        var metadata = new Dictionary<string, string> { ["Key"] = "Value" };

        var job = await grain.ScheduleJobAsync("PropertyTestJob", dueTime, metadata).WaitAsync(cancellationToken);

        Assert.NotNull(job.Id);
        Assert.NotEmpty(job.Id);
        Assert.Equal("PropertyTestJob", job.Name);
        Assert.Equal(dueTime, job.DueTime);
        Assert.NotNull(job.ShardId);
        Assert.NotEmpty(job.ShardId);
        Assert.NotNull(job.Metadata);
        Assert.Single(job.Metadata);

        await grain.WaitForJobToRun(job.Id).WaitAsync(cancellationToken);

        var context = await grain.GetJobRun(job.Id).WaitAsync(cancellationToken);
        Assert.NotNull(context);
        Assert.Equal(job.Id, context.Job.Id);
        Assert.Equal(job.Name, context.Job.Name);
        Assert.NotNull(context.RunId);
        Assert.NotEmpty(context.RunId);
    }

    public async Task DequeueCount(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IDurableJobGrain>("test-dequeue-count");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job = await grain.ScheduleJobAsync("DequeueTestJob", dueTime).WaitAsync(cancellationToken);

        await grain.WaitForJobToRun(job.Id).WaitAsync(cancellationToken);

        var context = await grain.GetJobRun(job.Id).WaitAsync(cancellationToken);
        Assert.NotNull(context);
        Assert.Equal(1, context.DequeueCount);
    }

    public async Task ScheduleJobOnAnotherGrain(CancellationToken cancellationToken)
    {
        var schedulerGrain = _grainFactory.GetGrain<ISchedulerGrain>("scheduler-grain");
        var targetGrain = _grainFactory.GetGrain<IDurableJobGrain>("target-grain");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(3);

        var job = await schedulerGrain.ScheduleJobOnAnotherGrainAsync("target-grain", "CrossGrainJob", dueTime).WaitAsync(cancellationToken);

        Assert.NotNull(job);
        Assert.Equal("CrossGrainJob", job.Name);
        Assert.Equal(dueTime, job.DueTime);

        await targetGrain.WaitForJobToRun(job.Id).WaitAsync(cancellationToken);

        Assert.True(await targetGrain.HasJobRan(job.Id).WaitAsync(cancellationToken));

        var context = await targetGrain.GetJobRun(job.Id).WaitAsync(cancellationToken);
        Assert.NotNull(context);
        Assert.Equal(job.Id, context.Job.Id);
        Assert.Equal("CrossGrainJob", context.Job.Name);
    }

    public async Task JobRetry(CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IRetryTestGrain>("retry-test-grain");
        var dueTime = DateTimeOffset.UtcNow.AddSeconds(2);
        var metadata = new Dictionary<string, string>
        {
            ["FailUntilAttempt"] = "3"
        };

        var job = await grain.ScheduleJobAsync("RetryJob", dueTime, metadata).WaitAsync(cancellationToken);

        Assert.NotNull(job);
        Assert.Equal("RetryJob", job.Name);
        Assert.NotNull(job.Metadata);
        Assert.Equal("3", job.Metadata["FailUntilAttempt"]);

        // Wait for the job to eventually succeed (with retries)
        // Default retry policy: retry up to 5 times with exponential backoff (1s, 2s, 4s, 8s, 16s)
        // We expect 3 attempts: fail at DequeueCount=1, fail at DequeueCount=2, succeed at DequeueCount=3
        // Total time: ~2s (initial) + 1s (first retry delay) + 2s (second retry delay) = ~5s
        await grain.WaitForJobToSucceed(job.Id).WaitAsync(cancellationToken);

        Assert.True(await grain.HasJobSucceeded(job.Id).WaitAsync(cancellationToken));

        var attemptCount = await grain.GetJobExecutionAttemptCount(job.Id).WaitAsync(cancellationToken);
        Assert.Equal(3, attemptCount);

        var dequeueCountHistory = await grain.GetJobDequeueCountHistory(job.Id).WaitAsync(cancellationToken);
        Assert.Equal(3, dequeueCountHistory.Count);
        Assert.Equal(1, dequeueCountHistory[0]);
        Assert.Equal(2, dequeueCountHistory[1]);
        Assert.Equal(3, dequeueCountHistory[2]);

        var finalContext = await grain.GetFinalJobRun(job.Id).WaitAsync(cancellationToken);
        Assert.NotNull(finalContext);
        Assert.Equal(3, finalContext.DequeueCount);
        Assert.Equal(job.Id, finalContext.Job.Id);
    }
}
