using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TestingHost;
using Tester;
using Tester.ScheduledJobs;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.ScheduledJobs;

public class AzureStorageBlobScheduledJobsTests : TestClusterPerTest
{
    private ScheduledJobTestsRunner _runner;

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new ScheduledJobTestsRunner(this.GrainFactory);
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();
    }

    public class SiloHostConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .UseAzureBlobScheduledJobs(options => options.ConfigureTestDefaults())
                .AddMemoryGrainStorageAsDefault();
        }
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task ScheduledJobGrain()
        => _runner.ScheduledJobGrain();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task JobExecutionOrder()
        => _runner.JobExecutionOrder();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task PastDueTime()
        => _runner.PastDueTime();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task JobWithMetadata()
        => _runner.JobWithMetadata();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task MultipleGrains()
        => _runner.MultipleGrains();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task DuplicateJobNames()
        => _runner.DuplicateJobNames();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task CancelNonExistentJob()
        => _runner.CancelNonExistentJob();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task CancelAlreadyExecutedJob()
        => _runner.CancelAlreadyExecutedJob();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task ConcurrentScheduling()
        => _runner.ConcurrentScheduling();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task JobPropertiesVerification()
        => _runner.JobPropertiesVerification();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task DequeueCount()
        => _runner.DequeueCount();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task ScheduleJobOnAnotherGrain()
        => _runner.ScheduleJobOnAnotherGrain();

    [SkippableFact, TestCategory("Azure"), TestCategory("ScheduledJobs")]
    public Task JobRetry()
        => _runner.JobRetry();
}
