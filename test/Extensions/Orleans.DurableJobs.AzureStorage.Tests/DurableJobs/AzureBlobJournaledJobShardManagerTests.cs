#nullable enable

using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs.Tests;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Tester;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
[TestCategory("Azure"), TestCategory("DurableJobs")]
public sealed class AzureBlobJournaledJobShardManagerTests(AzureBlobJournaledJobShardManagerTestFixture fixture)
    : JobShardManagerTestsRunner(fixture), IClassFixture<AzureBlobJournaledJobShardManagerTestFixture>;

public sealed class AzureBlobJournaledJobShardManagerTestFixture : IJobShardManagerTestFixture
{
    public async Task<IJobShardManagerTestScope> CreateScopeAsync(CancellationToken cancellationToken)
    {
        TestUtils.CheckForAzureStorage();

        var containerName = "durablejobs-shard-tests-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(TimeProvider.System);
        services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        services.UseAzureBlobDurableJobs(options =>
        {
            options.ConfigureTestDefaults();
            options.ContainerName = containerName;
        });

        var serviceProvider = services.BuildServiceProvider();
        var lifecycle = new SiloLifecycleSubject(serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        var storageProvider = serviceProvider.GetRequiredService<IJournalStorageProvider>();
        Assert.IsAssignableFrom<ILifecycleParticipant<ISiloLifecycle>>(storageProvider).Participate(lifecycle);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        await lifecycle.OnStart(cts.Token);
        return new AzureBlobJournaledJobShardManagerTestScope(serviceProvider, lifecycle, CreateContainerClient(containerName));
    }

    private static BlobContainerClient CreateContainerClient(string containerName)
    {
        return TestDefaultConfiguration.UseAadAuthentication
            ? new BlobContainerClient(new Uri(TestDefaultConfiguration.DataBlobUri, containerName), TestDefaultConfiguration.TokenCredential)
            : new BlobContainerClient(TestDefaultConfiguration.AzureStorageConnectionString, containerName);
    }

    private sealed class AzureBlobJournaledJobShardManagerTestScope(
        ServiceProvider services,
        SiloLifecycleSubject lifecycle,
        BlobContainerClient container) : JournaledJobShardManagerTestScope(services)
    {
        public override async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await lifecycle.OnStop(cts.Token);
            }
            catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }

            await base.DisposeAsync();
            try
            {
                await container.DeleteIfExistsAsync(cancellationToken: cts.Token);
            }
            catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }
        }
    }
}
