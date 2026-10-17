using Orleans.DurableTasks;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>Verifies that journaled task state persists completion sequences and continues them after recovery.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskCompletionSequenceStorageTests
{
    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public async Task CompletionSequencesRecoverAndContinueAboveTheRecoveredMaximum(string journalFormatKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new DurableTaskJournalHarness(journalFormatKey);
        var first = TaskId.Parse("root/first");
        var second = TaskId.Parse("root/second");
        var pending = TaskId.Parse("root/pending");
        var later = TaskId.Parse("root/later");
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            var storage = activation.Storage;
            storage.SetResponse(first, storage.GetOrCreateTask(first, request: null), DurableTaskResponse.FromResult(1));
            storage.SetResponse(second, storage.GetOrCreateTask(second, request: null), DurableTaskResponse.FromResult(2));
            _ = storage.GetOrCreateTask(pending, request: null);
            await storage.WriteAsync(cancellationToken);
        }

        await using var recovery = await harness.ActivateAsync(cancellationToken);
        Assert.True(recovery.Storage.TryGetTask(first, out var recoveredFirst));
        Assert.True(recovery.Storage.TryGetTask(second, out var recoveredSecond));
        Assert.True(recovery.Storage.TryGetTask(pending, out var recoveredPending));
        Assert.Equal(1, recoveredFirst.CompletionSequence);
        Assert.Equal(2, recoveredSecond.CompletionSequence);
        Assert.Equal(0, recoveredPending.CompletionSequence);

        var state = recovery.Storage.GetOrCreateTask(later, request: null);
        recovery.Storage.SetResponse(later, state, DurableTaskResponse.Completed);
        Assert.Equal(3, state.CompletionSequence);
    }
}
