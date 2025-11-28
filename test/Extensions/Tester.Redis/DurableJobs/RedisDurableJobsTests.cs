using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.DurableJobs;
using TestExtensions;
using Xunit;

namespace Tester.Redis.DurableJobs;

/// <summary>
/// Integration tests for Redis DurableJobs using a test cluster.
/// </summary>
public class RedisDurableJobsTests : TestClusterPerTest
{
    private DurableJobTestsRunner _runner = null!;

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();

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
            var connectionString = TestDefaultConfiguration.RedisConnectionString;
            hostBuilder
                .UseRedisDurableJobs(options =>
                {
                    options.CreateMultiplexer = async _ => await ConnectionMultiplexer.ConnectAsync(connectionString);
                    options.ShardPrefix = "test-shard";
                })
                .AddMemoryGrainStorageAsDefault();
        }
    }

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task DurableJobGrain()
        => _runner.DurableJobGrain();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task JobExecutionOrder()
        => _runner.JobExecutionOrder();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task PastDueTime()
        => _runner.PastDueTime();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task JobWithMetadata()
        => _runner.JobWithMetadata();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task MultipleGrains()
        => _runner.MultipleGrains();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task DuplicateJobNames()
        => _runner.DuplicateJobNames();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task CancelNonExistentJob()
        => _runner.CancelNonExistentJob();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task CancelAlreadyExecutedJob()
        => _runner.CancelAlreadyExecutedJob();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task ConcurrentScheduling()
        => _runner.ConcurrentScheduling();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task JobPropertiesVerification()
        => _runner.JobPropertiesVerification();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task DequeueCount()
        => _runner.DequeueCount();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task ScheduleJobOnAnotherGrain()
        => _runner.ScheduleJobOnAnotherGrain();

    [SkippableFact, TestCategory("Redis"), TestCategory("DurableJobs")]
    public Task JobRetry()
        => _runner.JobRetry();
}
