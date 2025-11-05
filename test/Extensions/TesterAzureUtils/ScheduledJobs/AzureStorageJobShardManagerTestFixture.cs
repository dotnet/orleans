using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.DurableJobs;
using Orleans.DurableJobs.AzureStorage;
using Tester.AzureUtils;
using Tester.DurableJobs;

namespace Orleans.Tests.DurableJobs.AzureStorage;

/// <summary>
/// Azure Storage implementation of <see cref="IJobShardManagerTestFixture"/>.
/// Provides the infrastructure needed to run shared job shard manager tests against Azure Storage.
/// </summary>
internal sealed class AzureStorageJobShardManagerTestFixture : IJobShardManagerTestFixture
{
    private readonly IOptions<AzureStorageJobShardOptions> _storageOptions;

    public AzureStorageJobShardManagerTestFixture()
    {
        _storageOptions = Options.Create(new AzureStorageJobShardOptions());
        _storageOptions.Value.ConfigureTestDefaults();
        _storageOptions.Value.ContainerName = "test-container-" + Guid.NewGuid().ToString("N");
    }

    public JobShardManager CreateManager(ILocalSiloDetails localSiloDetails, IClusterMembershipService membershipService)
    {
        return new AzureStorageJobShardManager(
            localSiloDetails,
            _storageOptions,
            membershipService,
            NullLoggerFactory.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup storage container
        var client = _storageOptions.Value.BlobServiceClient;
        var container = client.GetBlobContainerClient(_storageOptions.Value.ContainerName);
        await container.DeleteIfExistsAsync();
    }
}
