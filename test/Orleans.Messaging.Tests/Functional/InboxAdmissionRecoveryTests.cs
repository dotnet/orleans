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
    public async Task CutoffAndAdmissionFloor_JournalAndSnapshotRecoveryPreserveFencing(string format, bool snapshot)
    {
        await using var fixture = new FaultInjectionClusterFixture(format);
        await fixture.InitializeAsync();
        var receiver = fixture.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());
        var started = fixture.JobClock.GetUtcNow();
        var original = Message("finite-cutoff") with { RedeliveryDeadline = started + TimeSpan.FromHours(1) };
        var legacy = Message("legacy-null");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, original)).Status);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, legacy)).Status);
        await fixture.CrashAsync(receiver);
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        var inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.Equal(original.RedeliveryDeadline, await fixture.OnGrainAsync(receiver, () =>
            InboxTestAccess.GetProcessedCutoff(inbox, original.Envelope.MessageId)));
        Assert.Null(await fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedCutoff(inbox, legacy.Envelope.MessageId)));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, original)).Status);

        fixture.JobClock.Advance(TimeSpan.FromDays(2));
        var floor = fixture.JobClock.GetUtcNow() - TimeSpan.FromDays(1);
        var retained = Message("retained-finite") with { RedeliveryDeadline = started + TimeSpan.FromDays(10) };
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, retained)).Status);
        if (snapshot) fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        // A journal command (or its snapshot) durably advances the same floor.
        Assert.Equal(DeliveryStatus.Expired,
            (await DeliverAsync(receiver, Message("fenced") with { RedeliveryDeadline = floor })).Status);
        await fixture.CrashAsync(receiver);
        fixture.JobClock.Advance(started - fixture.JobClock.GetUtcNow());
        Assert.Equal(0, await receiver.GetOutboxCountAsync());
        inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.Equal(floor, await fixture.OnGrainAsync(receiver, () => InboxTestAccess.AdmissionFloor(inbox)));
        Assert.DoesNotContain(original.Envelope.MessageId,
            await fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, original)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, legacy)).Status);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, retained)).Status);
        Assert.Equal(started + TimeSpan.FromDays(10), await fixture.OnGrainAsync(receiver, () =>
            InboxTestAccess.GetProcessedCutoff(inbox, retained.Envelope.MessageId)));
        var changed = original with { RedeliveryDeadline = floor.AddTicks(1) };
        // A compacted ID is fenced by its original cutoff, not an immortal per-ID tombstone.
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, changed)).Status);
        Assert.Equal(4, fixture.SourceSinkProbe.For(receiver).SinkOffers);
        Assert.Equal(3, fixture.SourceSinkProbe.For(receiver).SinkAcceptances);
    }

    [Theory]
    [InlineData("orleans-binary")]
    [InlineData("json")]
    public async Task LegacySnapshot_AbsentFloorAndCutoffKeepHistoricalCompletionsPermanent(string format)
    {
        await using var fixture = new FaultInjectionClusterFixture(format);
        await fixture.InitializeAsync();
        var receiver = fixture.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());
        _ = await receiver.GetOutboxCountAsync();
        var message = Message("legacy-snapshot");
        var inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        InboxSnapshot historical;
        if (format == "json")
        {
            var node = JsonSerializer.SerializeToNode(new InboxSnapshot
            {
                Processed = [new() { MessageId = message.Envelope.MessageId }]
            }, InboxJsonContext.Default.InboxSnapshot)!;
            node.AsObject().Remove("AdmissionFloor");
            node["Processed"]![0]!.AsObject().Remove("RedeliveryDeadline");
            node["Processed"]![0]!["ProcessedAt"] = JsonSerializer.SerializeToNode(DateTimeOffset.MaxValue);
            historical = JsonSerializer.Deserialize(node.ToJsonString(), InboxJsonContext.Default.InboxSnapshot)!;
        }
        else
        {
            var oldSerializer = fixture.Client.ServiceProvider.GetRequiredService<Serializer<LegacyInboxSnapshot>>();
            var currentSerializer = fixture.Client.ServiceProvider.GetRequiredService<Serializer<InboxSnapshot>>();
            historical = currentSerializer.Deserialize(oldSerializer.SerializeToArray(new LegacyInboxSnapshot
            {
                Processed = [new() { MessageId = message.Envelope.MessageId, ProcessedAt = DateTimeOffset.MaxValue }]
            }))!;
        }
        Assert.Equal(DateTimeOffset.MinValue, historical.AdmissionFloor);
        Assert.Null(Assert.Single(historical.Processed).RedeliveryDeadline);
        await fixture.OnGrainAsync(receiver, () =>
            ((IDurableValueCommandHandler<InboxCommand>)inbox).ApplySet(new()
            {
                Kind = InboxCommandKind.Snapshot,
                Snapshot = historical
            }));
        Assert.Equal(DateTimeOffset.MinValue, await fixture.OnGrainAsync(receiver, () => InboxTestAccess.AdmissionFloor(inbox)));
        fixture.JobClock.Advance(TimeSpan.FromDays(400));
        fixture.Storage.RequestSnapshot(JournalId.FromGrainId(receiver.GetGrainId()));
        await receiver.WriteAsync();
        var floor = fixture.JobClock.GetUtcNow() - TimeSpan.FromDays(1);
        await fixture.CrashAsync(receiver);
        fixture.JobClock.Advance(-TimeSpan.FromDays(800));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);
        inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.Equal(floor, await fixture.OnGrainAsync(receiver, () => InboxTestAccess.DurableAdmissionFloor(inbox)));
        Assert.Contains(message.Envelope.MessageId, await fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        Assert.Equal(0, fixture.SourceSinkProbe.For(receiver).SinkOffers);
    }

    [Theory]
    [InlineData("orleans-binary")]
    [InlineData("json")]
    public async Task LegacyCompletionCommand_IgnoresRetiredTimestampAndPreservesItsCutoff(string format)
    {
        await using var fixture = new FaultInjectionClusterFixture(format);
        await fixture.InitializeAsync();
        var receiver = fixture.Client.GetGrain<ISourceSinkGrain>(Guid.NewGuid());
        _ = await receiver.GetOutboxCountAsync();
        var started = fixture.JobClock.GetUtcNow();
        var cutoff = started + TimeSpan.FromHours(1);
        var message = Message("legacy-complete") with { RedeliveryDeadline = cutoff };
        InboxCommand historical;
        if (format == "json")
        {
            var node = JsonSerializer.SerializeToNode(new InboxCommand
            {
                Kind = InboxCommandKind.Complete,
                MessageId = message.Envelope.MessageId,
                RedeliveryDeadline = cutoff
            }, InboxJsonContext.Default.InboxCommand)!;
            node["ProcessedAt"] = JsonSerializer.SerializeToNode(DateTimeOffset.MaxValue);
            historical = JsonSerializer.Deserialize(node.ToJsonString(), InboxJsonContext.Default.InboxCommand)!;
        }
        else
        {
            var oldSerializer = fixture.Client.ServiceProvider.GetRequiredService<Serializer<LegacyInboxCompletionCommand>>();
            var currentSerializer = fixture.Client.ServiceProvider.GetRequiredService<Serializer<InboxCommand>>();
            historical = currentSerializer.Deserialize(oldSerializer.SerializeToArray(new LegacyInboxCompletionCommand
            {
                Kind = InboxCommandKind.Complete,
                MessageId = message.Envelope.MessageId,
                ProcessedAt = DateTimeOffset.MaxValue,
                RedeliveryDeadline = cutoff
            }))!;
        }
        Assert.Equal(InboxCommandKind.Complete, historical.Kind);
        Assert.Equal(message.Envelope.MessageId, historical.MessageId);
        Assert.Equal(cutoff, historical.RedeliveryDeadline);
        var inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        await fixture.OnGrainAsync(receiver, () => ((IDurableValueCommandHandler<InboxCommand>)inbox).ApplySet(historical));
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        fixture.Storage.RequestSnapshot(journal);
        await receiver.WriteAsync();
        await fixture.CrashAsync(receiver);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, message)).Status);

        // The historical MaxValue completion timestamp cannot prolong retention beyond the finite cutoff's fence.
        fixture.JobClock.Advance(TimeSpan.FromDays(2));
        fixture.Storage.RequestSnapshot(journal);
        await receiver.WriteAsync();
        await fixture.CrashAsync(receiver);
        fixture.JobClock.Advance(started - fixture.JobClock.GetUtcNow());
        Assert.Equal(DeliveryStatus.Expired, (await DeliverAsync(receiver, message)).Status);
        inbox = fixture.GetGrainContext(receiver).ActivationServices.GetRequiredService<Inbox>();
        Assert.DoesNotContain(message.Envelope.MessageId, await fixture.OnGrainAsync(receiver, () => InboxTestAccess.GetProcessedKeys(inbox)));
        Assert.Equal(0, fixture.SourceSinkProbe.For(receiver).SinkOffers);
    }

    [Fact]
    public void PersistedAdmissionFields_UseAdditiveStableFieldIds()
    {
        Assert.Equal(3U, typeof(InboxProcessedEntry).GetProperty(nameof(InboxProcessedEntry.RedeliveryDeadline))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Equal(3U, typeof(InboxSnapshot).GetProperty(nameof(InboxSnapshot.AdmissionFloor))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Equal(9U, typeof(InboxCommand).GetProperty(nameof(InboxCommand.RedeliveryDeadline))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Equal(10U, typeof(InboxCommand).GetProperty(nameof(InboxCommand.AdmissionFloor))!.GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Null(typeof(InboxProcessedEntry).GetProperty(nameof(InboxProcessedEntry.Sequence))!.GetCustomAttribute<IdAttribute>());
        Assert.Null(typeof(InboxProcessedEntry).GetProperty("ProcessedAt"));
        Assert.Null(typeof(InboxCommand).GetProperty("ProcessedAt"));
        Assert.DoesNotContain(typeof(InboxProcessedEntry).GetMembers().SelectMany(static member => member.GetCustomAttributes<IdAttribute>()),
            static attribute => attribute.Id == 1);
        Assert.DoesNotContain(typeof(InboxCommand).GetMembers().SelectMany(static member => member.GetCustomAttributes<IdAttribute>()),
            static attribute => attribute.Id == 4);
    }

    private static InboxMessage Message(string name) => new()
    {
        Envelope = new()
        {
            MessageId = MessageId.Create("admission-recovery", name, Guid.NewGuid().ToString("N")),
            Payload = [1, 2, 3],
            Subject = SourceSinkGrain.Subject
        }
    };

    // These intentionally omit the admission fields: binary recovery must read bytes produced by the old schema,
    // not merely a current object with the new properties assigned their default values.
    [GenerateSerializer]
    internal sealed class LegacyInboxSnapshot
    {
        [Id(1)] public LegacyInboxProcessedEntry[] Processed { get; init; } = [];
    }

    [GenerateSerializer]
    internal sealed class LegacyInboxProcessedEntry
    {
        [Id(0)] public MessageId MessageId { get; init; }
        [Id(1)] public DateTimeOffset ProcessedAt { get; init; }
    }

    [GenerateSerializer]
    internal sealed class LegacyInboxCompletionCommand
    {
        [Id(0)] public InboxCommandKind Kind { get; init; }
        [Id(2)] public MessageId MessageId { get; init; }
        [Id(4)] public DateTimeOffset ProcessedAt { get; init; }
        [Id(9)] public DateTimeOffset? RedeliveryDeadline { get; init; }
    }

    private static async Task<DeliveryResult> DeliverAsync(ISourceSinkGrain receiver, InboxMessage message) =>
        await receiver.AsReference<IMessagingExtension>().DeliverAsync(message, TestContext.Current.CancellationToken);
}
