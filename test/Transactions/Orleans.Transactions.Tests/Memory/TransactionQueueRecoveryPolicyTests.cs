using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public sealed class TransactionQueueRecoveryPolicyTests : IClassFixture<TransactionQueueRecoveryPolicyFixture>
{
    private readonly TransactionQueueRecoveryPolicyFixture fixture;

    public TransactionQueueRecoveryPolicyTests(TransactionQueueRecoveryPolicyFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task StorageConflict_DeactivatesImmediately()
    {
        var (grain, grainId, activationId) = await this.CreateGrainAsync(nameof(StorageConflict_DeactivatesImmediately));
        this.fixture.StorageController.EnqueueWriteFault(grainId, new InconsistentStateException("Simulated storage conflict."));

        var deactivated = this.fixture.ActivationTracker.WaitForDeactivationCountAsync(grainId, expectedCount: 1);
        await Assert.ThrowsAnyAsync<Exception>(async () => await grain.Add(1));
        await deactivated;

        this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(1);

        var activated = this.fixture.ActivationTracker.WaitForActivationCountAsync(grainId, expectedCount: 2);
        var newActivationId = await grain.GetActivationId();
        await activated;

        newActivationId.Should().NotBe(activationId);
        (await grain.GetValue()).Should().Be(1);
    }

    [Fact]
    public async Task LaterRequests_RecoverAfterTransientStoreAndRecoveryFailures()
    {
        var (grain, grainId, activationId) = await this.CreateGrainAsync(nameof(LaterRequests_RecoverAfterTransientStoreAndRecoveryFailures));
        this.QueueFailedStoreAndRecoveryWave(grainId, readFailureCount: 4);

        await this.AssertFailureWithoutDeactivationAsync(grain, grainId, activationId, async () => await grain.Add(1));

        var recovered = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                (await grain.Add(10)).Should().Be(11);
                recovered = true;
                break;
            }
            catch
            {
                this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(0);
                (await grain.GetActivationId()).Should().Be(activationId);
            }
        }

        recovered.Should().BeTrue();
        this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(0);
        (await grain.GetValue()).Should().Be(11);
    }

    private async Task<(ITransactionQueueRecoveryPolicyGrain Grain, GrainId GrainId, Guid ActivationId)> CreateGrainAsync(string key)
    {
        this.fixture.StorageController.Reset();
        this.fixture.ActivationTracker.Reset();

        var grain = this.fixture.GrainFactory.GetGrain<ITransactionQueueRecoveryPolicyGrain>(key);
        (await grain.Add(1)).Should().Be(1);

        return (grain, grain.GetGrainId(), await grain.GetActivationId());
    }

    private async Task AssertFailureWithoutDeactivationAsync(
        ITransactionQueueRecoveryPolicyGrain grain,
        GrainId grainId,
        Guid expectedActivationId,
        Func<Task> operation)
    {
        await Assert.ThrowsAnyAsync<Exception>(operation);
        this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(0);
        (await grain.GetActivationId()).Should().Be(expectedActivationId);
    }

    private void QueueFailedStoreAndRecoveryWave(GrainId grainId, int readFailureCount)
    {
        this.fixture.StorageController.EnqueueWriteFault(grainId, new InvalidOperationException("Simulated storage write failure."));
        this.fixture.StorageController.EnqueueReadFaults(
            grainId,
            readFailureCount,
            attempt => new InvalidOperationException($"Simulated recovery load failure {attempt}."));
    }
}
