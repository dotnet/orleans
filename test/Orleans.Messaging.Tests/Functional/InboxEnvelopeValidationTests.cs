using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxEnvelopeValidationTests : MessagingBehaviorTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultMessage_RejectsBeforeAcceptanceOrDuplicate(bool existingKey)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        // External deserialization can materialize invalid default fields before admission.
        var malformed = new InboxMessage { Envelope = default };
        var context = Fixture.GetGrainContext(receiver);
        if (existingKey)
        {
            var processedInbox = Fixture.GetInbox(receiver);
            await OnTurnAsync(context, () => InboxTestAccess.SetProcessedAt(processedInbox, malformed.Envelope.MessageId, Fixture.Clock.GetUtcNow()));
        }
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => DeliverAsync(receiver, malformed));

        IInboxExtension extension = Fixture.GetInbox(receiver);
        await OnTurnAsync(context, () =>
        {
            var direct = extension.DeliverAsync(malformed, TestContext.Current.CancellationToken);
            Assert.True(direct.IsCompleted);
            Assert.Throws<ArgumentException>(() => direct.GetAwaiter().GetResult());
        });
        Assert.Contains("message", exception.Message, StringComparison.OrdinalIgnoreCase);
        var after = await receiver.GetSnapshotAsync();
        Assert.Equal(0, after.InboxCount);
        Assert.Empty(after.Effects);
        Assert.Equal(existingKey ? 1 : 0, after.ProcessedMessageCount);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, receiver.GetGrainId()));
    }

    [Fact]
    public async Task DirectEnvelopeWithExplicitValidIdentity_PreservesDeliveryAndDedupe()
    {
        var receiver = NewGrain();
        var template = CreateEnvelope(receiver, NewMessage(187, "explicit-identity"));
        var envelope = TestApplicationProtocol.With(template, Sessions,
            messageId: HierarchicalKey.Parse("tenant/test/command/explicit", null));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); }
        });
        return done.Task;
    }
}
