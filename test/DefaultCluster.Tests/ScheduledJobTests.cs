using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
        var dueTime = DateTimeOffset.Now.AddSeconds(5);
        var job1 = await grain.ScheduleJobAsync("TestJob", dueTime);
        Assert.NotNull(job1);
        Assert.Equal("TestJob", job1.Name);
        Assert.Equal(dueTime, job1.DueTime);
        var job2 = await grain.ScheduleJobAsync("TestJob2", dueTime);
        var job3 = await grain.ScheduleJobAsync("TestJob3", dueTime.AddSeconds(2));
        var job4 = await grain.ScheduleJobAsync("TestJob4", dueTime);
        var job5 = await grain.ScheduleJobAsync("TestJob5", dueTime.AddSeconds(1));
        // Wait for the job to run
        await Task.Delay(TimeSpan.FromSeconds(10));
        Assert.True(await grain.HasJobRan(job1.Id), "The scheduled job did not run as expected.");
        Assert.True(await grain.HasJobRan(job2.Id), "The scheduled job did not run as expected.");
        Assert.True(await grain.HasJobRan(job3.Id), "The scheduled job did not run as expected.");
        Assert.True(await grain.HasJobRan(job4.Id), "The scheduled job did not run as expected.");
        Assert.True(await grain.HasJobRan(job5.Id), "The scheduled job did not run as expected.");
    }
}
