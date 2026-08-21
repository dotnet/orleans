using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.TestingHost;
using Tester;
using Tester.DurableJobs;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public class AzureStorageBlobDurableJobsTests : TestClusterPerTest
{
    private DurableJobTestsRunner _runner = null!;
    private string? _containerName;

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (!PreconditionsMet)
        {
            return;
        }
        _runner = new DurableJobTestsRunner(this.GrainFactory);
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        _containerName = GetContainerName(builder.Options.ServiceId);
        builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_containerName is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await CreateContainerClient(_containerName).DeleteIfExistsAsync(cancellationToken: cts.Token);
            }
        }
    }

    internal static string GetContainerName(string serviceId) => $"durablejobs-tests-{serviceId}";

    private static BlobContainerClient CreateContainerClient(string containerName)
    {
        return TestDefaultConfiguration.UseAadAuthentication
            ? new BlobContainerClient(new Uri(TestDefaultConfiguration.DataBlobUri, containerName), TestDefaultConfiguration.TokenCredential)
            : new BlobContainerClient(TestDefaultConfiguration.DataConnectionString, containerName);
    }

    public class SiloHostConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .UseAzureBlobDurableJobs(options => options.ConfigureTestDefaults())
                .AddMemoryGrainStorageAsDefault();

            hostBuilder.Services
                .AddOptions<AzureBlobJournalStorageOptions>()
                .Configure<IOptions<ClusterOptions>>(
                    static (options, clusterOptions) => options.ContainerName = GetContainerName(clusterOptions.Value.ServiceId));
        }
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task DurableJobGrain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DurableJobGrain(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobExecutionOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobExecutionOrder(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task PastDueTime()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.PastDueTime(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobWithMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobWithMetadata(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task MultipleGrains()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.MultipleGrains(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task DuplicateJobNames()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DuplicateJobNames(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task CancelNonExistentJob()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.CancelNonExistentJob(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task CancelAlreadyExecutedJob()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.CancelAlreadyExecutedJob(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task ConcurrentScheduling()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ConcurrentScheduling(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobPropertiesVerification()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobPropertiesVerification(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task DequeueCount()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.DequeueCount(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task ScheduleJobOnAnotherGrain()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ScheduleJobOnAnotherGrain(cts.Token);
    }

    [Fact, TestCategory("Azure"), TestCategory("DurableJobs")]
    public async Task JobRetry()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobRetry(cts.Token);
    }
}

internal static class AzureBlobDurableJobsTestConfiguration
{
    public static AzureBlobJournalStorageOptions ConfigureTestDefaults(this AzureBlobJournalStorageOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataBlobUri, TestDefaultConfiguration.TokenCredential);
        }
        else
        {
            options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataConnectionString!);
        }

        return options;
    }
}
