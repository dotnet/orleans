using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Internal;
using Orleans.DurableJobs;
using Orleans.DurableJobs.AzureStorage;
using Orleans.Tests.DurableJobs.AzureStorage;
using Tester.DurableJobs;
using Xunit;
using Xunit.Sdk;

namespace Tester.AzureUtils.DurableJobs;

/// <summary>
/// Azure Storage-specific tests for job shard manager functionality.
/// Common tests are delegated to <see cref="JobShardManagerTestsRunner"/> for reusability across providers.
/// Provider-specific tests (e.g., batching) remain here.
/// </summary>
[TestCategory("DurableJobs")]
public class AzureStorageJobShardManagerTests : AzureStorageBasicTests, IAsyncDisposable
{
    private readonly AzureStorageJobShardManagerTestFixture _fixture;
    private readonly JobShardManagerTestsRunner _runner;

    internal IOptions<AzureStorageJobShardOptions> StorageOptions { get; }

    public AzureStorageJobShardManagerTests()
    {
        StorageOptions = Options.Create(new AzureStorageJobShardOptions());
        StorageOptions.Value.ConfigureTestDefaults();
        StorageOptions.Value.ContainerName = "test-container-" + Guid.NewGuid().ToString("N");

        // Create fixture and runner for common tests
        _fixture = new AzureStorageJobShardManagerTestFixture();
        _runner = new JobShardManagerTestsRunner(_fixture);
    }

    public async ValueTask DisposeAsync() 
    {
        // Cleanup storage container
        var client = StorageOptions.Value.BlobServiceClient;
        var container = client.GetBlobContainerClient(StorageOptions.Value.ContainerName);
        await container.DeleteIfExistsAsync();
        
        // Cleanup fixture
        await _fixture.DisposeAsync();
    }

    #region Common Tests (Delegated to Runner)

    /// <summary>
    /// Tests basic shard creation and assignment workflow.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_Creation_Assignation()
        => _runner.ShardCreationAndAssignment();

    /// <summary>
    /// Tests reading and consuming jobs from a frozen shard after ownership transfer.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_ReadFrozenShard()
        => _runner.ReadFrozenShard();

    /// <summary>
    /// Tests consuming jobs from a live shard.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_LiveShard()
        => _runner.LiveShard();

    /// <summary>
    /// Tests job metadata persistence across ownership transfers.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_JobMetadata()
        => _runner.JobMetadata();

    /// <summary>
    /// Tests concurrent shard assignment to verify ownership conflict resolution.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_ConcurrentShardAssignment_OwnershipConflicts()
        => _runner.ConcurrentShardAssignment_OwnershipConflicts();

    /// <summary>
    /// Tests shard metadata preservation across ownership transfers.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_ShardMetadataMerge()
        => _runner.ShardMetadataMerge();

    #endregion

    /// <summary>
    /// Tests stopping shard processing and verifying jobs remain for reassignment.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_StopProcessingShard()
        => _runner.StopProcessingShard();

    /// <summary>
    /// Tests retrying a job with a new due time.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_RetryJobLater()
        => _runner.RetryJobLater();

    /// <summary>
    /// Tests job cancellation before and during processing.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_JobCancellation()
        => _runner.JobCancellation();

    /// <summary>
    /// Tests that multiple shard registrations with the same time range produce unique IDs.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_ShardRegistrationRetry_IdCollisions()
        => _runner.ShardRegistrationRetry_IdCollisions();

    /// <summary>
    /// Tests that unregistering a shard with remaining jobs preserves the shard for reassignment.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public Task AzureStorageJobShardManager_UnregisterShard_WithJobsRemaining()
        => _runner.UnregisterShard_WithJobsRemaining();
}
