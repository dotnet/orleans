using System.Threading.Tasks;
using Tester.ScheduledJobs;
using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests;

public class InMemoryScheduledJobsTests : HostedTestClusterEnsureDefaultStarted
{
    private readonly ScheduledJobTestsRunner _runner;

    public InMemoryScheduledJobsTests(DefaultClusterFixture fixture) : base(fixture)
    {
        _runner = new ScheduledJobTestsRunner(this.GrainFactory);
    }

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task ScheduledJobGrain()
        => _runner.ScheduledJobGrain();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task JobExecutionOrder()
        => _runner.JobExecutionOrder();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task PastDueTime()
        => _runner.PastDueTime();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task JobWithMetadata()
        => _runner.JobWithMetadata();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task MultipleGrains()
        => _runner.MultipleGrains();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task DuplicateJobNames()
        => _runner.DuplicateJobNames();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task CancelNonExistentJob()
        => _runner.CancelNonExistentJob();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task CancelAlreadyExecutedJob()
        => _runner.CancelAlreadyExecutedJob();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task ConcurrentScheduling()
        => _runner.ConcurrentScheduling();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task JobPropertiesVerification()
        => _runner.JobPropertiesVerification();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task DequeueCount()
        => _runner.DequeueCount();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task ScheduleJobOnAnotherGrain()
        => _runner.ScheduleJobOnAnotherGrain();

    [Fact, TestCategory("BVT"), TestCategory("ScheduledJobs")]
    public Task JobRetry()
        => _runner.JobRetry();
}
