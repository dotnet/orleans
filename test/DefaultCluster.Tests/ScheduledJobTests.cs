using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Internal;
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
}
