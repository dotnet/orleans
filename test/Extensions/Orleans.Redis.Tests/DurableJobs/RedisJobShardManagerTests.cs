using System;
using System.Threading;
using System.Threading.Tasks;
using Tester.DurableJobs;
using Xunit;

namespace Tester.Redis.DurableJobs;

/// <summary>
/// Redis-specific tests for job shard manager functionality.
/// Common tests are delegated to <see cref="JobShardManagerTestsRunner"/> for reusability across providers.
/// </summary>
[TestCategory("Redis"), TestCategory("DurableJobs"), TestCategory("Functional")]
public class RedisJobShardManagerTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly RedisJobShardManagerTestFixture _fixture;
    private readonly JobShardManagerTestsRunner _runner;

    public RedisJobShardManagerTests()
    {
        TestUtils.CheckForRedis();

        // Create fixture and runner for common tests
        _fixture = new RedisJobShardManagerTestFixture();
        _runner = new JobShardManagerTestsRunner(_fixture);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();

    #region Common Tests (Delegated to Runner)

    /// <summary>
    /// Tests basic shard creation and assignment workflow.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_Creation_Assignment()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardCreationAndAssignment(cts.Token);
    }

    /// <summary>
    /// Tests reading and consuming jobs from a frozen shard after ownership transfer.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_ReadFrozenShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ReadFrozenShard(cts.Token);
    }

    /// <summary>
    /// Tests consuming jobs from a live shard.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_LiveShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.LiveShard(cts.Token);
    }

    /// <summary>
    /// Tests job metadata persistence across ownership transfers.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_JobMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobMetadata(cts.Token);
    }

    /// <summary>
    /// Tests concurrent shard assignment to verify ownership conflict resolution.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_ConcurrentShardAssignment_OwnershipConflicts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ConcurrentShardAssignment_OwnershipConflicts(cts.Token);
    }

    /// <summary>
    /// Tests shard metadata preservation across ownership transfers.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_ShardMetadataMerge()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardMetadataMerge(cts.Token);
    }

    /// <summary>
    /// Tests stopping shard processing and verifying jobs remain for reassignment.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_StopProcessingShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.StopProcessingShard(cts.Token);
    }

    /// <summary>
    /// Tests retrying a job with a new due time.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_RetryJobLater()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.RetryJobLater(cts.Token);
    }

    /// <summary>
    /// Tests job cancellation before and during processing.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_JobCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobCancellation(cts.Token);
    }

    /// <summary>
    /// Tests that multiple shard registrations with the same time range produce unique IDs.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_ShardRegistrationRetry_IdCollisions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardRegistrationRetry_IdCollisions(cts.Token);
    }

    /// <summary>
    /// Tests that unregistering a shard with remaining jobs preserves the shard for reassignment.
    /// </summary>
    [SkippableFact]
    public async Task RedisJobShardManager_UnregisterShard_WithJobsRemaining()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.UnregisterShard_WithJobsRemaining(cts.Token);
    }

    #endregion
}
