#nullable enable

using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs.Tests;
using Orleans.Hosting;
using Orleans.Runtime;
using Tester;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.DurableJobs;

[TestCategory("Azure"), TestCategory("DurableJobs")]
public sealed class AzureBlobJournaledJobShardManagerTests(AzureBlobJournaledJobShardManagerTestFixture fixture)
    : JobShardManagerTestsRunner(fixture), IClassFixture<AzureBlobJournaledJobShardManagerTestFixture>;

public sealed class AzureBlobJournaledJobShardManagerTestFixture : IJobShardManagerTestFixture
{
    public async Task<IJobShardManagerTestScope> CreateScopeAsync()
    {
        TestUtils.CheckForAzureStorage();

        var containerName = "durablejobs-shard-tests-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.UseAzureBlobDurableJobs(options =>
        {
            options.ConfigureTestDefaults();
            options.ContainerName = containerName;
        });

        var serviceProvider = services.BuildServiceProvider();
        var lifecycle = new SiloLifecycleSubject(serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        foreach (var participant in serviceProvider.GetServices<ILifecycleParticipant<ISiloLifecycle>>())
        {
            participant.Participate(lifecycle);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await lifecycle.OnStart(cts.Token);
        return new AzureBlobJournaledJobShardManagerTestScope(serviceProvider, lifecycle, CreateContainerClient(containerName));
    }

    private static BlobContainerClient CreateContainerClient(string containerName)
    {
        return TestDefaultConfiguration.UseAadAuthentication
            ? new BlobContainerClient(new Uri(TestDefaultConfiguration.DataBlobUri, containerName), TestDefaultConfiguration.TokenCredential)
            : new BlobContainerClient(TestDefaultConfiguration.DataConnectionString, containerName);
    }

    private sealed class AzureBlobJournaledJobShardManagerTestScope(
        ServiceProvider services,
        SiloLifecycleSubject lifecycle,
        BlobContainerClient container) : JournaledJobShardManagerTestScope(services)
    {
        public override async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await lifecycle.OnStop(cts.Token);
            await base.DisposeAsync();
            await container.DeleteIfExistsAsync(cancellationToken: cts.Token);
        }
    }
}
