using System.Threading.Tasks;
using Tester.DurableJobs;
using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests;

public class InMemoryDurableJobsTests : HostedTestClusterEnsureDefaultStarted
{
    private readonly DurableJobTestsRunner _runner;

    public InMemoryDurableJobsTests(DefaultClusterFixture fixture) : base(fixture)
    {
        _runner = new DurableJobTestsRunner(this.GrainFactory);
    }

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task DurableJobGrain()
        => _runner.DurableJobGrain();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task JobExecutionOrder()
        => _runner.JobExecutionOrder();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task PastDueTime()
        => _runner.PastDueTime();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task JobWithMetadata()
        => _runner.JobWithMetadata();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task MultipleGrains()
        => _runner.MultipleGrains();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task DuplicateJobNames()
        => _runner.DuplicateJobNames();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task CancelNonExistentJob()
        => _runner.CancelNonExistentJob();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task CancelAlreadyExecutedJob()
        => _runner.CancelAlreadyExecutedJob();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task ConcurrentScheduling()
        => _runner.ConcurrentScheduling();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task JobPropertiesVerification()
        => _runner.JobPropertiesVerification();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task DequeueCount()
        => _runner.DequeueCount();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task ScheduleJobOnAnotherGrain()
        => _runner.ScheduleJobOnAnotherGrain();

    [Fact, TestCategory("BVT"), TestCategory("DurableJobs")]
    public Task JobRetry()
        => _runner.JobRetry();
}
