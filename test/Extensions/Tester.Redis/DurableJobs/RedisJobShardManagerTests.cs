using System.Threading.Tasks;
using Tester.DurableJobs;
using Xunit;

namespace Tester.Redis.DurableJobs;

/// <summary>
/// Redis-specific tests for job shard manager functionality.
/// Common tests are delegated to <see cref="JobShardManagerTestsRunner"/> for reusability across providers.
/// </summary>
[TestCategory("DurableJobs"), TestCategory("Redis")]
public class RedisJobShardManagerTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly RedisJobShardManagerTestFixture _fixture;
    private readonly JobShardManagerTestsRunner _runner;

    public RedisJobShardManagerTests()
    {
        // Create fixture and runner for common tests
        _fixture = new RedisJobShardManagerTestFixture();
        _runner = new JobShardManagerTestsRunner(_fixture);
    }

    public Task InitializeAsync()
    {
        Tester.TestUtils.CheckForRedis();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeAsync();
    }

    #region Common Tests (Delegated to Runner)

    /// <summary>
    /// Tests basic shard creation and assignment workflow.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_Creation_Assignation()
        => _runner.ShardCreationAndAssignment();

    /// <summary>
    /// Tests reading and consuming jobs from a frozen shard after ownership transfer.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_ReadFrozenShard()
        => _runner.ReadFrozenShard();

    /// <summary>
    /// Tests consuming jobs from a live shard.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_LiveShard()
        => _runner.LiveShard();

    /// <summary>
    /// Tests job metadata persistence across ownership transfers.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_JobMetadata()
        => _runner.JobMetadata();

    /// <summary>
    /// Tests concurrent shard assignment to verify ownership conflict resolution.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_ConcurrentShardAssignment_OwnershipConflicts()
        => _runner.ConcurrentShardAssignment_OwnershipConflicts();

    /// <summary>
    /// Tests shard metadata preservation across ownership transfers.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_ShardMetadataMerge()
        => _runner.ShardMetadataMerge();

    /// <summary>
    /// Tests stopping shard processing and verifying jobs remain for reassignment.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_StopProcessingShard()
        => _runner.StopProcessingShard();

    /// <summary>
    /// Tests retrying a job with a new due time.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_RetryJobLater()
        => _runner.RetryJobLater();

    /// <summary>
    /// Tests job cancellation before and during processing.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_JobCancellation()
        => _runner.JobCancellation();

    /// <summary>
    /// Tests that multiple shard registrations with the same time range produce unique IDs.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_ShardRegistrationRetry_IdCollisions()
        => _runner.ShardRegistrationRetry_IdCollisions();

    /// <summary>
    /// Tests that unregistering a shard with remaining jobs preserves the shard for reassignment.
    /// </summary>
    [SkippableFact, TestCategory("Functional")]
    public Task RedisJobShardManager_UnregisterShard_WithJobsRemaining()
        => _runner.UnregisterShard_WithJobsRemaining();

    #endregion
}
