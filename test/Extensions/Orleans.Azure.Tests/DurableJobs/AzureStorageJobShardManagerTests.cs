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
    public async Task AzureStorageJobShardManager_Creation_Assignation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardCreationAndAssignment(cts.Token);
    }

    /// <summary>
    /// Tests reading and consuming jobs from a frozen shard after ownership transfer.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ReadFrozenShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ReadFrozenShard(cts.Token);
    }

    /// <summary>
    /// Tests consuming jobs from a live shard.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_LiveShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.LiveShard(cts.Token);
    }

    /// <summary>
    /// Tests job metadata persistence across ownership transfers.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_JobMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobMetadata(cts.Token);
    }

    /// <summary>
    /// Tests concurrent shard assignment to verify ownership conflict resolution.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ConcurrentShardAssignment_OwnershipConflicts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ConcurrentShardAssignment_OwnershipConflicts(cts.Token);
    }

    /// <summary>
    /// Tests shard metadata preservation across ownership transfers.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ShardMetadataMerge()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardMetadataMerge(cts.Token);
    }

    #endregion

    /// <summary>
    /// Tests stopping shard processing and verifying jobs remain for reassignment.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_StopProcessingShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.StopProcessingShard(cts.Token);
    }

    /// <summary>
    /// Tests retrying a job with a new due time.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_RetryJobLater()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.RetryJobLater(cts.Token);
    }

    /// <summary>
    /// Tests job cancellation before and during processing.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_JobCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobCancellation(cts.Token);
    }

    /// <summary>
    /// Tests that multiple shard registrations with the same time range produce unique IDs.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_ShardRegistrationRetry_IdCollisions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardRegistrationRetry_IdCollisions(cts.Token);
    }

    /// <summary>
    /// Tests that unregistering a shard with remaining jobs preserves the shard for reassignment.
    /// This test is delegated to the runner for reuse across providers.
    /// </summary>
    [SkippableFact, TestCategory("Azure"), TestCategory("Functional")]
    public async Task AzureStorageJobShardManager_UnregisterShard_WithJobsRemaining()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.UnregisterShard_WithJobsRemaining(cts.Token);
    }
}
