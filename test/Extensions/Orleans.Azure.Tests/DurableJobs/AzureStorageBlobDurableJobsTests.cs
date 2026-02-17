using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TestingHost;
using Tester;
using Tester.DurableJobs;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

public class AzureStorageBlobDurableJobsTests : TestClusterPerTest
{
    private DurableJobTestsRunner _runner;

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new DurableJobTestsRunner(this.GrainFactory);
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
                .UseAzureBlobDurableJobs(options => options.ConfigureTestDefaults())
                .AddMemoryGrainStorageAsDefault();
        }
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task DurableJobGrain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DurableJobGrain(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobExecutionOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobExecutionOrder(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task PastDueTime()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.PastDueTime(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobWithMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobWithMetadata(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task MultipleGrains()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.MultipleGrains(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task DuplicateJobNames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DuplicateJobNames(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task CancelNonExistentJob()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.CancelNonExistentJob(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task CancelAlreadyExecutedJob()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.CancelAlreadyExecutedJob(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task ConcurrentScheduling()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ConcurrentScheduling(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobPropertiesVerification()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobPropertiesVerification(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task DequeueCount()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DequeueCount(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task ScheduleJobOnAnotherGrain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ScheduleJobOnAnotherGrain(cts.Token);
    }

    [SkippableFact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobRetry()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobRetry(cts.Token);
    }
}
