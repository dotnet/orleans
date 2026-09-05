using Orleans.DurableMessaging.Tests.Support;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableTaskActivationRecoveryTests : IAsyncLifetime
{
    private readonly DurableMessagingClusterFixture fixture = new();

    public ValueTask InitializeAsync() => fixture.InitializeAsync();
    public ValueTask DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task DefaultConfiguration_RecoversTerminalDurableRpcAcrossActivation()
    {
        const int argument = 41;
        const int expectedResult = 130;
        const string rootId = "default-durable-rpc-round-trip";
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = fixture.Client.GetGrain<IDurableTaskRecoveryTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var activationBefore = await grain.GetActivationIdAsync();

        var scheduledBefore = await grain.ComputeAsync(argument).ScheduleAsync(rootId, cancellationToken);
        var resultBefore = await scheduledBefore.WaitAsync(cancellationToken);

        Assert.Equal(TaskId.CreateRoot(rootId), scheduledBefore.Id);
        Assert.Equal(expectedResult, resultBefore);
        Assert.Equal(
            new DurableTaskInvocationSnapshot(1, activationBefore, argument),
            fixture.DurableTaskExecutionProbe.GetSnapshot(grainId));

        var recoveryRead = fixture.Storage.BlockRead(JournalId.FromGrainId(grainId));
        await grain.RequestDeactivationAsync();
        var reactivation = grain.GetActivationIdAsync();
        try
        {
            await recoveryRead.WaitUntilEnteredAsync();
            Assert.False(reactivation.IsCompleted);
        }
        finally
        {
            recoveryRead.Release();
        }

        var activationAfter = await reactivation;
        Assert.NotEqual(activationBefore, activationAfter);

        var scheduledAfter = await grain.ComputeAsync(argument).ScheduleAsync(rootId, cancellationToken);
        var resultAfter = await scheduledAfter.WaitAsync(cancellationToken);

        Assert.Equal(TaskId.CreateRoot(rootId), scheduledAfter.Id);
        Assert.Equal(scheduledBefore.Id, scheduledAfter.Id);
        Assert.Equal(expectedResult, resultAfter);
        Assert.Equal(
            new DurableTaskInvocationSnapshot(1, activationBefore, argument),
            fixture.DurableTaskExecutionProbe.GetSnapshot(grainId));
    }
}
