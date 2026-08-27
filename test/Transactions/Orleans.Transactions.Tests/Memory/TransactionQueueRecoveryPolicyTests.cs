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
        var cancellationToken = TestContext.Current.CancellationToken;
        var (grain, grainId, activationId) = await this.CreateGrainAsync(
            nameof(StorageConflict_DeactivatesImmediately),
            cancellationToken);
        this.fixture.StorageController.EnqueueWriteFault(grainId, new InconsistentStateException("Simulated storage conflict."));

        var deactivated = this.fixture.ActivationTracker.WaitForDeactivationCountAsync(
            grainId,
            expectedCount: 1,
            cancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(async () => await grain.Add(1, cancellationToken));
        await deactivated.WaitAsync(cancellationToken);

        this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(1);

        var activated = this.fixture.ActivationTracker.WaitForActivationCountAsync(
            grainId,
            expectedCount: 2,
            cancellationToken);
        var newActivationId = await grain.GetActivationId(cancellationToken);
        await activated.WaitAsync(cancellationToken);

        newActivationId.Should().NotBe(activationId);
        (await grain.GetValue(cancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task LaterRequests_RecoverAfterTransientStoreAndRecoveryFailures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (grain, grainId, activationId) = await this.CreateGrainAsync(
            nameof(LaterRequests_RecoverAfterTransientStoreAndRecoveryFailures),
            cancellationToken);
        this.QueueFailedStoreAndRecoveryWave(grainId, readFailureCount: 4);

        await this.AssertFailureWithoutDeactivationAsync(
            grain,
            grainId,
            activationId,
            async () => await grain.Add(1, cancellationToken),
            cancellationToken);

        var recovered = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                (await grain.Add(10, cancellationToken)).Should().Be(11);
                recovered = true;
                break;
            }
            catch
            {
                this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(0);
                (await grain.GetActivationId(cancellationToken)).Should().Be(activationId);
            }
        }

        recovered.Should().BeTrue();
        this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(0);
        (await grain.GetValue(cancellationToken)).Should().Be(11);
    }

    private async Task<(ITransactionQueueRecoveryPolicyGrain Grain, GrainId GrainId, Guid ActivationId)> CreateGrainAsync(
        string key,
        CancellationToken cancellationToken)
    {
        this.fixture.StorageController.Reset();
        this.fixture.ActivationTracker.Reset();

        var grain = this.fixture.GrainFactory.GetGrain<ITransactionQueueRecoveryPolicyGrain>(key);
        (await grain.Add(1, cancellationToken)).Should().Be(1);

        return (grain, grain.GetGrainId(), await grain.GetActivationId(cancellationToken));
    }

    private async Task AssertFailureWithoutDeactivationAsync(
        ITransactionQueueRecoveryPolicyGrain grain,
        GrainId grainId,
        Guid expectedActivationId,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await Assert.ThrowsAnyAsync<Exception>(operation);
        this.fixture.ActivationTracker.GetDeactivationCount(grainId).Should().Be(0);
        (await grain.GetActivationId(cancellationToken)).Should().Be(expectedActivationId);
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
