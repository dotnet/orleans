using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxEnvelopeValidationTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData("message", false)]
    [InlineData("sender", false)]
    [InlineData("data", false)]
    [InlineData("message", true)]
    [InlineData("sender", true)]
    [InlineData("data", true)]
    public async Task MalformedEnvelope_RejectsBeforeAcceptanceOrDuplicate(string field, bool existingKey)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        using var template = CreateEnvelope(receiver, NewMessage(180, field));
        var malformed = field switch
        {
            "message" => template.Value with { MessageId = Guid.Empty },
            "sender" => template.Value with { SenderId = default },
            "data" => template.Value with { Data = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var context = Fixture.GetGrainContext(receiver);
        if (existingKey)
        {
            var processed = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("__orleans.durable-messaging.inbox-processed");
            await OnTurnAsync(context, () => processed.Add((malformed.SenderId, malformed.MessageId), Fixture.Clock.GetUtcNow()));
            await receiver.RetryWriteStateAsync();
        }
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => DeliverAsync(receiver, malformed));

        var extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        await OnTurnAsync(context, () =>
        {
            var direct = extension.DeliverAsync(malformed, TestContext.Current.CancellationToken);
            Assert.True(direct.IsCompleted);
            Assert.Equal("envelope", Assert.Throws<ArgumentException>(() => direct.GetAwaiter().GetResult()).ParamName);
        });
        var expected = field == "message" ? "message ID" : field == "sender" ? "sender" : "data";
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        var after = await receiver.GetSnapshotAsync();
        Assert.Equal(0, after.InboxCount);
        Assert.Null(after.InboxJobId);
        Assert.Empty(after.Effects);
        Assert.Equal(existingKey ? 1 : 0, after.ProcessedMessageCount);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
    }

    [Fact]
    public async Task DirectEnvelopeWithExplicitValidIdentity_PreservesDeliveryAndDedupe()
    {
        var receiver = NewGrain();
        using var template = CreateEnvelope(receiver, NewMessage(187, "explicit-identity"));
        var envelope = template.Value with { MessageId = Guid.Parse("6ab5d1ea-c028-4dd0-8d84-c169c109569e") };
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
