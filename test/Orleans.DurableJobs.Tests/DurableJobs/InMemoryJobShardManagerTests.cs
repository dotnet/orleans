using System;
using System.Threading;
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
    public async Task InMemoryJobShardManager_ShardCreationAndAssignment()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardCreationAndAssignment(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_ReadFrozenShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ReadFrozenShard(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_LiveShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.LiveShard(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_JobMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobMetadata(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_ConcurrentShardAssignment_OwnershipConflicts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ConcurrentShardAssignment_OwnershipConflicts(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_ShardMetadataMerge()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardMetadataMerge(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_StopProcessingShard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.StopProcessingShard(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_RetryJobLater()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.RetryJobLater(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_JobCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.JobCancellation(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_ShardRegistrationRetry_IdCollisions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.ShardRegistrationRetry_IdCollisions(cts.Token);
    }

    [SkippableFact]
    public async Task InMemoryJobShardManager_UnregisterShard_WithJobsRemaining()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _runner.UnregisterShard_WithJobsRemaining(cts.Token);
    }
}
