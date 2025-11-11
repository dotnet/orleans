using System.Threading.Tasks;
using Xunit;

namespace Tester.DurableJobs;

/// <summary>
/// Tests for <see cref="InMemoryJobShardManager"/> using the <see cref="JobShardManagerTestsRunner"/>.
/// These tests verify shard lifecycle management, ownership, and failover semantics for the InMemory provider.
/// </summary>
[TestCategory("BVT"), TestCategory("DurableJobs")]
public class InMemoryJobShardManagerTests : IAsyncLifetime
{
    private readonly InMemoryJobShardManagerTestFixture _fixture;
    private readonly JobShardManagerTestsRunner _runner;

    public InMemoryJobShardManagerTests()
    {
        _fixture = new InMemoryJobShardManagerTestFixture();
        _runner = new JobShardManagerTestsRunner(_fixture);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [SkippableFact]
    public Task InMemoryJobShardManager_ShardCreationAndAssignment() => _runner.ShardCreationAndAssignment();

    [SkippableFact]
    public Task InMemoryJobShardManager_ReadFrozenShard() => _runner.ReadFrozenShard();

    [SkippableFact]
    public Task InMemoryJobShardManager_LiveShard() => _runner.LiveShard();

    [SkippableFact]
    public Task InMemoryJobShardManager_JobMetadata() => _runner.JobMetadata();

    [SkippableFact]
    public Task InMemoryJobShardManager_ConcurrentShardAssignment_OwnershipConflicts() => _runner.ConcurrentShardAssignment_OwnershipConflicts();

    [SkippableFact]
    public Task InMemoryJobShardManager_ShardMetadataMerge() => _runner.ShardMetadataMerge();

    [SkippableFact]
    public Task InMemoryJobShardManager_StopProcessingShard() => _runner.StopProcessingShard();

    [SkippableFact]
    public Task InMemoryJobShardManager_RetryJobLater() => _runner.RetryJobLater();

    [SkippableFact]
    public Task InMemoryJobShardManager_JobCancellation() => _runner.JobCancellation();

    [SkippableFact]
    public Task InMemoryJobShardManager_ShardRegistrationRetry_IdCollisions() => _runner.ShardRegistrationRetry_IdCollisions();

    [SkippableFact]
    public Task InMemoryJobShardManager_UnregisterShard_WithJobsRemaining() => _runner.UnregisterShard_WithJobsRemaining();
}
