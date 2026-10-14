using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxHeaderBehaviorTests : MessagingBehaviorTestBase
{
    public InboxHeaderBehaviorTests() : base(new HeaderFixture()) { }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawMessage_AbsentOptionalMetadataCommitsAndDeduplicates(bool empty)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        byte[] payload = empty ? [] : [0, 255, 128, 42];
        var message = Raw(MessageId.Create("raw", Guid.NewGuid().ToString("N")), payload);
        Assert.Null(message.Envelope.Subject);
        Assert.Null(message.Envelope.Headers);
        using var handler = new RawHandler(context);
        await OnTurnAsync(context, () => grain.HandlerOverride = handler);
        using var storage = Fixture.Storage.BlockAcknowledgement(JournalId.FromGrainId(context.GrainId));
        var delivery = DeliverAsync(receiver, message);
        await storage.WaitUntilEnteredAsync();
        Assert.False(delivery.IsCompleted);
        Assert.False(handler.Entered.Task.IsCompleted);
        storage.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        await WaitAsync(handler.Entered.Task);
        Assert.Equal(payload, handler.Message.Envelope.Payload.ToArray());
        handler.Release.TrySetResult();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(message.Envelope.MessageId, Assert.Single(completed.Effects).LogicalId);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);
        await receiver.RequestDeactivationAsync();
        await WaitAsync(context.Deactivated);
        var replayed = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, replayed.ActivationId);
        Assert.Equal(1, Assert.Single(replayed.Effects).Count);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);
    }

    [Fact]
    public async Task PendingDuplicate_ChangedCustomHeadersPreservesOriginal()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var key = MessageId.Create("custom", Guid.NewGuid().ToString("N"));
        var original = Raw(key, [1, 2, 3], "original");
        var changed = Raw(key, [1, 2, 3], "changed");
        using var handler = new RawHandler(context);
        await OnTurnAsync(context, () => grain.HandlerOverride = handler);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, original)).Status);
        await WaitAsync(handler.Entered.Task);
        var pending = context.ActivationServices.GetRequiredService<Inbox>();
        Envelope first = default;
        await OnTurnAsync(context, () => first = Assert.Single(pending.Messages()).Envelope);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(context.GrainId));
        var schedules = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, context.GrainId);
        await OnTurnAsync(context, async () =>
        {
            IMessagingExtension extension = context.ActivationServices.GetRequiredService<Inbox>();
            Assert.Equal(DeliveryStatus.Duplicate, (await extension.DeliverAsync(changed)).Status);
            var retained = Assert.Single(pending.Messages()).Envelope;
            Assert.Same(first.Payload, retained.Payload);
            Assert.Same(first.Headers, retained.Headers);
            Assert.True(Assert.IsType<Dictionary<string, byte[]>>(retained.Headers).TryGetValue("custom.trace", out var trace));
            Assert.Equal("original", Encoding.UTF8.GetString(trace));
        });
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(context.GrainId)));
        Assert.Equal(schedules, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, context.GrainId));
        handler.Release.TrySetResult();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
    }

    [Fact]
    public async Task UnknownHeaders_RecoverAndRemainAvailableAfterRetryAndDeadLetter()
    {
        var receiver = NewGrain();
        var key = MessageId.Create("headers", Guid.NewGuid().ToString("N"));
        var body = NewMessage(810, "poison") with { LogicalId = key, ThrowDuringPreparation = true };
        var application = CreateEnvelope(receiver, body);
        byte[] opaque = [0xff, 0, 0x80, 0x7f];
        var headers = new Dictionary<string, byte[]>(Assert.IsType<Dictionary<string, byte[]>>(application.Envelope.Headers),
            StringComparer.Ordinal)
        {
            ["future.opaque"] = opaque,
            ["future.number"] = new Serializer<int>(Sessions).SerializeToArray(8675309)
        };
        var message = application with { Envelope = application.Envelope with { Headers = headers } };
        var retry = Fixture.Metrics.WaitForCountAsync("orleans-messaging-inbox-messages-processed", 1, "retry");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, message)).Status);
        await retry;
        var previous = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await WaitAsync(previous.Deactivated);
        _ = await receiver.GetSnapshotAsync();
        var current = Fixture.GetGrainContext(receiver);
        Assert.NotSame(previous, current);
        var pending = await receiver.GetSnapshotAsync();
        Assert.Equal(1, pending.InboxCount);
        Assert.Empty(pending.InboxDeadLetters);
        await OnTurnAsync(current, () =>
        {
            var stored = Assert.Single(current.ActivationServices.GetRequiredService<Inbox>().Messages()).Envelope;
            var metadata = Assert.IsType<Dictionary<string, byte[]>>(stored.Headers);
            Assert.True(metadata.TryGetValue("future.opaque", out var bytes));
            Assert.Equal(opaque, bytes);
            Assert.True(metadata.TryGetValue("future.number", out var number));
            Assert.Equal(8675309, new Serializer<int>(Sessions).Deserialize(number));
        });
        Fixture.Clock.Advance(TimeSpan.FromHours(1));
        await receiver.RequestDeactivationAsync();
        await WaitAsync(current.Deactivated);
        _ = await receiver.GetSnapshotAsync();
        current = Fixture.GetGrainContext(receiver);
        var dead = await Fixture.WaitForDeadLetterCountAsync(receiver, 1);
        Assert.Equal(key, Assert.Single(dead.InboxDeadLetters).MessageId);
        Assert.Equal(2, Assert.Single(dead.InboxDeadLetters).AttemptCount);
        Assert.Empty(dead.Effects);
        var diagnostics = current.ActivationServices.GetRequiredService<IMessagingDiagnostics>();
        await OnTurnAsync(current, () =>
        {
            var retained = Assert.Single(diagnostics.InboxDeadLetters).Message.Envelope;
            var metadata = Assert.IsType<Dictionary<string, byte[]>>(retained.Headers);
            Assert.True(metadata.TryGetValue("future.opaque", out var bytes));
            Assert.Equal(opaque, bytes);
            Assert.True(metadata.TryGetValue("future.number", out var number));
            Assert.Equal(8675309, new Serializer<int>(Sessions).Deserialize(number));
            Assert.Equal(application.Envelope.Payload.ToArray(), retained.Payload.ToArray());
        });
        await receiver.RequestDeactivationAsync();
        await WaitAsync(current.Deactivated);
        _ = await receiver.GetSnapshotAsync();
        var recovered = Fixture.GetGrainContext(receiver);
        await OnTurnAsync(recovered, () =>
        {
            var retained = Assert.Single(recovered.ActivationServices.GetRequiredService<IMessagingDiagnostics>()
                .InboxDeadLetters).Message.Envelope;
            var metadata = Assert.IsType<Dictionary<string, byte[]>>(retained.Headers);
            Assert.True(metadata.TryGetValue("future.opaque", out var bytes));
            Assert.Equal(opaque, bytes);
            Assert.True(metadata.TryGetValue("future.number", out var number));
            Assert.Equal(8675309, new Serializer<int>(Sessions).Deserialize(number));
        });
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, Raw(key, []))).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidMessageHeader_RejectsBeforeSchedulingOrMutation(bool completed)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var key = MessageId.Create("malformed", Guid.NewGuid().ToString("N"));
        var message = Raw(key, [1, 2]);
        if (completed)
        {
            await OnTurnAsync(context, () =>
                InboxTestAccess.SetProcessedAt(context.ActivationServices.GetRequiredService<Inbox>(), key, Fixture.Clock.GetUtcNow()));
        }
        message = message with
        {
            Envelope = message.Envelope with
            {
                Headers = new(StringComparer.Ordinal) { ["invalid"] = null! }
            }
        };
        var writes = Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(context.GrainId));
        await OnTurnAsync(context, async () =>
        {
            IMessagingExtension extension = context.ActivationServices.GetRequiredService<Inbox>();
            await Assert.ThrowsAsync<ArgumentException>(() => extension.DeliverAsync(message, TestContext.Current.CancellationToken).AsTask());
        });
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(JournalId.FromGrainId(context.GrainId)));
        Assert.Equal(0, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, context.GrainId));
        var snapshot = await receiver.GetSnapshotAsync();
        Assert.Equal(0, snapshot.InboxCount);
        Assert.Equal(completed ? 1 : 0, snapshot.ProcessedMessageCount);
        Assert.Empty(snapshot.Effects);
    }

    private static InboxMessage Raw(MessageId key, byte[] payload, string? trace = null)
    {
        Dictionary<string, byte[]>? headers = null;
        if (trace is not null) (headers ??= new(StringComparer.Ordinal))["custom.trace"] = Encoding.UTF8.GetBytes(trace);
        return new() { Envelope = new() { MessageId = key, Payload = payload, Headers = headers } };
    }

    private static Task OnTurnAsync(IGrainContext context, Action action) =>
        OnTurnAsync(context, () => { action(); return Task.CompletedTask; });

    private static Task OnTurnAsync(IGrainContext context, Func<Task> action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() => { _ = Run(); });
        return result.Task;
        async Task Run()
        {
            try { await action(); result.SetResult(); }
            catch (Exception exception) { result.SetException(exception); }
        }
    }

    private static Task WaitAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private sealed class RawHandler(IGrainContext context) : IInboxHandler, IDisposable
    {
        public InboxMessage Message { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanHandle(IInboxHandlerContext context) => true;


        public async ValueTask HandleAsync(IInboxHandlerContext handler, CancellationToken cancellationToken)
        {
            Message = handler.Message;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var key = handler.Message.Envelope.MessageId;
            var effects = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<MessageId, MessageEffect>>("test-effects");
            effects[key] = new(key, 1, 800, "raw");
            handler.Complete();
        }

        public void Dispose() => Release.TrySetResult();
    }

    private sealed class HeaderFixture : MessagingClusterFixture
    {
        protected override void ConfigureOptions(DurableMessagingOptions options)
        {
            base.ConfigureOptions(options);
            options.MaxProcessingAttempts = 2;
            options.RetryInitialInterval = TimeSpan.FromHours(1);
        }
    }
}
