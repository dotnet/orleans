using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>Runs persisted admission boundaries through both supported journal encodings.</summary>
[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxAdmissionRecoveryTests
{
    [Theory]
    [InlineData("orleans-binary", false)]
    [InlineData("orleans-binary", true)]
    [InlineData("json", false)]
    [InlineData("json", true)]
    public async Task SentAtAndAdmissionFloor_JournalAndSnapshotRecoveryPreserveFencing(string format, bool snapshot)
    {
        await using var fixture = new FaultInjectionClusterFixture(format);
        await fixture.InitializeAsync();
        var receiver = fixture.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());
        var started = fixture.JobClock.GetUtcNow();
        var original = Message("old", started);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, original)).Status);
        await fixture.CrashAsync(receiver);
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        var inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.Equal(started, await fixture.OnGrainAsync(receiver, () =>
            InboxTestAccess.GetProcessedSentAt(inbox, original.Envelope.MessageId)));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, original)).Status);

        fixture.JobClock.Advance(TimeSpan.FromDays(2));
        var floor = fixture.JobClock.GetUtcNow() - TimeSpan.FromDays(1);
        var retained = Message("recent", fixture.JobClock.GetUtcNow());
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, retained)).Status);
        if (snapshot) fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        // A journal command (or its snapshot) durably advances the same floor.
        Assert.Equal(DeliveryStatus.Expired,
            (await DeliverAsync(receiver, Message("fenced", floor))).Status);
        await fixture.CrashAsync(receiver);
        fixture.JobClock.Advance(started - fixture.JobClock.GetUtcNow());
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.Equal(floor, await fixture.OnGrainAsync(receiver, () => InboxTestAccess.AdmissionFloor(inbox)));
        Assert.DoesNotContain(original.Envelope.MessageId,
            await fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, original)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, retained)).Status);
        Assert.Equal(retained.SentAt, await fixture.OnGrainAsync(receiver, () =>
            InboxTestAccess.GetProcessedSentAt(inbox, retained.Envelope.MessageId)));
        Assert.Equal(2, fixture.SourceSinkProbe.For(receiver).SinkOffers);
        Assert.Equal(2, fixture.SourceSinkProbe.For(receiver).SinkAcceptances);
    }

    [Theory]
    [InlineData("orleans-binary", "pending")]
    [InlineData("orleans-binary", "processed")]
    [InlineData("orleans-binary", "dead-letter")]
    [InlineData("json", "pending")]
    [InlineData("json", "processed")]
    [InlineData("json", "dead-letter")]
    public async Task Recovery_RejectsMissingAdmittedSentAtBeforeCompaction(string format, string kind)
    {
        await using var fixture = new FaultInjectionClusterFixture(format);
        await fixture.InitializeAsync();
        var receiver = fixture.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());
        _ = await receiver.GetOutboxCountAsync();
        var message = Message("missing-send-time", DateTimeOffset.MinValue);
        var inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        var snapshot = new InboxSnapshot
        {
            AdmissionFloor = fixture.JobClock.GetUtcNow(),
            Messages = kind == "pending" ? [new() { Message = message, State = new() }] : [],
            Processed = kind == "processed" ? [new() { MessageId = message.Envelope.MessageId, SentAt = DateTimeOffset.MinValue }] : [],
            DeadLetters = kind == "dead-letter" ? [new()
            {
                Message = message, DeadLetteredAt = fixture.JobClock.GetUtcNow(), Reason = "missing send time fixture", AttemptCount = 1
            }] : []
        };
        InboxSnapshot recovered;
        if (format == "json")
        {
            recovered = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, InboxJsonContext.Default.InboxSnapshot),
                InboxJsonContext.Default.InboxSnapshot)!;
        }
        else
        {
            var serializer = fixture.Client.ServiceProvider.GetRequiredService<Serializer<InboxSnapshot>>();
            recovered = serializer.Deserialize(serializer.SerializeToArray(snapshot))!;
        }
        await fixture.OnGrainAsync(receiver, () =>
        {
            ((IDurableValueCommandHandler<InboxCommand>)inbox).ApplySet(new()
            {
                Kind = InboxCommandKind.Snapshot,
                Snapshot = recovered
            });
            var error = Assert.Throws<InvalidOperationException>(() => ((IStateMachine)inbox).OnRecoveryCompleted());
            Assert.Equal("A recovered inbox entry requires its admitted send time.", error.Message);
        });
        Assert.Equal(0, fixture.SourceSinkProbe.For(receiver).SinkOffers);
    }

    [Fact]
    public void PersistedAdmissionFields_UseAdditiveStableFieldIds()
    {
        Assert.Equal(3U, typeof(InboxProcessedEntry).GetProperty(nameof(InboxProcessedEntry.SentAt))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Equal(3U, typeof(OutboxMessageState).GetProperty(nameof(OutboxMessageState.SentAt))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Null(typeof(OutboxMessageState).GetProperty("ExpirationTime"));
        Assert.Equal(3U, typeof(InboxSnapshot).GetProperty(nameof(InboxSnapshot.AdmissionFloor))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Equal(9U, typeof(InboxCommand).GetProperty(nameof(InboxCommand.SentAt))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Equal(10U, typeof(InboxCommand).GetProperty(nameof(InboxCommand.AdmissionFloor))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Null(typeof(InboxProcessedEntry).GetProperty(nameof(InboxProcessedEntry.Sequence))!.GetCustomAttribute<IdAttribute>());
        Assert.Null(typeof(InboxProcessedEntry).GetProperty("ProcessedAt"));
        Assert.Null(typeof(InboxCommand).GetProperty("ProcessedAt"));
        Assert.DoesNotContain(typeof(InboxProcessedEntry).GetMembers().SelectMany(static member => member.GetCustomAttributes<IdAttribute>()),
            static attribute => attribute.Id == 1);
        Assert.DoesNotContain(typeof(InboxCommand).GetMembers().SelectMany(static member => member.GetCustomAttributes<IdAttribute>()),
            static attribute => attribute.Id == 4);
    }

    private static InboxMessage Message(string name, DateTimeOffset sentAt) => new()
    {
        SentAt = sentAt,
        Envelope = new()
        {
            MessageId = MessageId.Create("admission-recovery", name, Guid.NewGuid().ToString("N")),
            Payload = [1, 2, 3],
            Subject = SourceSinkGrain.Subject
        }
    };

    private static async Task<DeliveryResult> DeliverAsync(ISourceSinkGrain receiver, InboxMessage message) =>
        await receiver.AsReference<IMessagingExtension>().DeliverAsync(message, TestContext.Current.CancellationToken);
}
