using System.Buffers;
using Documentation.Grains.DurableMessaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingSnippetTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly GrainId Sender = GrainId.Create("sender", "snippet");
    private static readonly GrainId Receiver = GrainId.Create("notification", "snippet");
    private Serializer Serializer => _services.GetRequiredService<Serializer>();
    private readonly ApplicationPayload _payload;
    private readonly List<DurableEnvelope> _owned = [];
    private readonly List<NotificationGrain> _grains = [];

    public DurableMessagingSnippetTests() => _payload = new(Serializer);

    private DurableEnvelope Own(DurableEnvelope envelope)
    {
        _owned.Add(envelope);
        return envelope;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("orders/2026/42")]
    public async Task NotificationReply_PreservesOptionalApplicationOperationKey(string? operationKey)
    {
        var key = operationKey is null ? null : HierarchicalKey.Create(operationKey);
        var attempt = Create(new Notify("received message", key, Sender));
        var handling = attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "count", "send", "complete" }, attempt.Events);
        await handling;
        var reply = Assert.Single(attempt.Output);
        Assert.Equal(Sender, reply.ReceiverId);
        Assert.Equal(Receiver, reply.SenderId);
        Assert.NotEqual(attempt.Context.Envelope.MessageId, reply.MessageId);
        Assert.Equal(new NotificationReceived("received message", key),
            _payload.Decode<NotificationReceived>(reply.Payload));
        Assert.Equal(8, attempt.Count.Value);
        if (key is not null)
        {
            Assert.Equal("received message", Assert.Single(attempt.Ledger).Value);
        }
        attempt.Inbox.Received(1).RegisterHandler(attempt.Grain);
        attempt.Outbox.Received(1).Send(reply);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationHandling_PreMutationCancellationPreservesBusinessState()
    {
        var attempt = Create(new Notify("received message", ResponseDestination: Sender));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Ledger);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_CancellationDuringLocalPreparationPreservesBusinessState()
    {
        var attempt = Create(new Notify("prepared message", ResponseDestination: Sender));
        using var cancellation = new CancellationTokenSource();
        // Reading the prior business value is still local preparation. Cancellation
        // at that boundary must be observed after the reply has been encoded.
        attempt.Count.OnRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, cancellation.Token));

        attempt.Count.OnRead = null;
        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Ledger);
        Assert.Empty(attempt.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationHandling_CancellationAfterFirstMutationCompletesBeforeMethodReturns(bool replyRequested)
    {
        var key = HierarchicalKey.Create("notifications/late-cancellation");
        var attempt = Create(new Notify("prepared message", key, replyRequested ? Sender : null));
        using var cancellation = new CancellationTokenSource();
        attempt.Count.OnWrite = () =>
        {
            Assert.False(cancellation.IsCancellationRequested);
            cancellation.Cancel();
        };

        var handling = attempt.Grain.HandleAsync(attempt.Context, cancellation.Token);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(replyRequested ? new[] { "count", "send", "complete" } : new[] { "count", "complete" }, attempt.Events);
        Assert.Equal("prepared message", Assert.Single(attempt.Ledger).Value);
        await handling;
        Assert.Equal(8, attempt.Count.Value);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationWithoutReply_StagesCountAndCompletes()
    {
        var attempt = Create(new Notify("received message"));
        var handling = attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "count", "complete" }, attempt.Events);
        await handling;
        Assert.Equal(8, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        attempt.Context.Received(1).Complete();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task NotificationHandling_InvalidBodyLeavesBusinessAndCompletionUnchanged(string? body)
    {
        var attempt = Create(new Notify(body));
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Contains("nonempty string", exception.Message, StringComparison.Ordinal);
        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Ledger);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_FreshTransportIdentityReusesBusinessOperation()
    {
        var key = HierarchicalKey.Create("campaigns/42/recipients/alice");
        var attempt = Create(new Notify("campaign text", key, Sender));
        await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
        var duplicate = Own(_payload.Envelope(Sender, Receiver, new Notify("campaign text", key, Sender)));
        Assert.NotEqual(attempt.Context.Envelope.MessageId, duplicate.MessageId);
        attempt.Context.Envelope.Returns(duplicate);
        await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.Equal(8, attempt.Count.Value);
        Assert.Single(attempt.Ledger);
        Assert.Equal(new[] { "count", "send", "complete", "send", "complete" }, attempt.Events);
        Assert.Equal(2, attempt.Output.Count);
        var codec = _payload;
        Assert.Equal(codec.Decode<NotificationReceived>(attempt.Output[0].Payload),
            codec.Decode<NotificationReceived>(attempt.Output[1].Payload));
    }

    [Fact]
    public async Task NotificationHandling_ConflictingOperationKeyPreservesOriginalText()
    {
        var key = HierarchicalKey.Create("campaigns/42/recipients/alice");
        var attempt = Create(new Notify("original", key));
        await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
        attempt.Context.Envelope.Returns(Own(_payload.Envelope(Sender, Receiver, new Notify("conflict", key))));
        attempt.Events.Clear();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(8, attempt.Count.Value);
        Assert.Equal("original", Assert.Single(attempt.Ledger).Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_UnexpectedApplicationKindPreservesBusinessState()
    {
        var attempt = Create(new NotificationReceived("receipt", null));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Events);
        Assert.Empty(attempt.Output);
    }

    [Fact]
    public void ShipmentPackage_EntriesRemainValidWhileDecodedOwnerIsRetained()
    {
        var request = new ReserveStock(HierarchicalKey.Create("orders/42/inventory/reserve"), 3, Sender);
        byte[] manifest = [1, 2, 3];
        BufferPackage retained;
        using (var encoder = new ArcBufferWriter())
        using (var encoded = ShipmentPackage.Encode(Serializer, encoder, request, manifest))
        using (var package = ShipmentPackage.Decode(Serializer, encoded))
        {
            manifest[0] = 99;
            Assert.Equal(request, ShipmentPackage.ReadReservation(Serializer, package));
            Assert.Equal(new[] { "manifest", "reservation" }, package.Keys.Order().ToArray());
            Assert.False(package.TryGetBytes("missing", out _));
            // Arc-backed decode retains the raw sub-slice instead of copying its page.
            Assert.Same(encoded.First, package.Buffer.First);
            retained = package.Retain();
        }

        using (retained)
        {
            Assert.Equal(request, ShipmentPackage.ReadReservation(Serializer, retained));
            Assert.True(retained.TryGetBytes("manifest", out var bytes));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
        }
        Assert.Throws<ObjectDisposedException>(() => retained.TryGetBytes("manifest", out _));
    }

    [Fact]
    public void ArcPayload_ConsumedSlicesSharePagesAndSurviveEncoderDisposal()
    {
        var serializer = _services.GetRequiredService<Serializer<Notify>>();
        var message = new Notify("retained", HierarchicalKey.Create("campaigns/42/alice"), Sender);
        ArcBuffer first;
        ArcBuffer second;
        using (var encoder = new ArcBufferWriter())
        {
            first = ArcPayloadEncoder.Encode(serializer, encoder, message);
            try
            {
                second = ArcPayloadEncoder.Encode(serializer, encoder, message with { Text = "different bytes" });
            }
            catch
            {
                first.Dispose();
                throw;
            }
        }

        using (first)
        using (second)
        {
            Assert.Same(first.First, second.First);
            Assert.True(second.Offset >= first.Offset + first.Length);
            Assert.Equal(message, ArcPayloadEncoder.DecodeRetained(Serializer, first));
            Assert.Equal("different bytes", serializer.Deserialize(second)!.Text);
            Assert.Equal(message, serializer.Deserialize(first)); // Decode borrowed input twice.
        }
    }

    [Fact]
    public void Envelope_RetainOwnsIndependentPinAndSerializationDoesNotConsumePayload()
    {
        DurableEnvelope retained;
        using (var codec = new ApplicationPayload(Serializer))
        using (var envelope = codec.Envelope(Sender, Receiver, new Notify("owned")))
        {
            retained = envelope.Retain();
        }

        using (retained)
        {
            using var encoder = new ArcBufferWriter();
            Serializer.Serialize(retained, encoder);
            using var first = encoder.ConsumeSlice(encoder.Length);
            Serializer.Serialize(retained, encoder);
            using var second = encoder.ConsumeSlice(encoder.Length);
            Assert.Equal(first.ToArray(), second.ToArray());
            using var decoded = Serializer.Deserialize<DurableEnvelope>(first);
            Assert.Equal(retained.MessageId, decoded.MessageId);
            Assert.Equal(new Notify("owned"), _payload.Decode<Notify>(decoded.Payload));
            Assert.Equal(new Notify("owned"), _payload.Decode<Notify>(retained.Payload));
        }
    }

    [Fact]
    public async Task NotificationHandling_StagingFailureReleasesLocalReplyAndLeavesCompletionUnstaged()
    {
        var inbox = Substitute.For<IDurableInbox>();
        var outbox = Substitute.For<IDurableOutbox>();
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(Own(_payload.Envelope(Sender, Receiver,
            new Notify("prepared", ResponseDestination: Sender))));
        ArcBuffer borrowedReply = default;
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            borrowedReply = call.Arg<DurableEnvelope>().Payload;
            throw new IOException("staging failed");
        });
        using (var grain = new NotificationGrain(inbox, outbox, Serializer, new TestCount([]), new TestLedger()))
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await grain.HandleAsync(context, TestContext.Current.CancellationToken));
            context.DidNotReceive().Complete();
            Assert.NotEqual(0, borrowedReply.Length);
        }
        // No state pin was admitted, and encoder teardown releases its own page pin.
        // A leaked local reply would keep this borrowed view valid.
        Assert.Throws<InvalidOperationException>(() => borrowedReply.ToArray());
    }

    private Attempt Create(object message)
    {
        var inbox = Substitute.For<IDurableInbox>();
        var outbox = Substitute.For<IDurableOutbox>();
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(Own(_payload.Envelope(Sender, Receiver, message)));
        var events = new List<string>();
        var output = new List<DurableEnvelope>();
        var count = new TestCount(events);
        var ledger = new TestLedger();
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            output.Add(Own(call.Arg<DurableEnvelope>().Retain()));
            events.Add("send");
        });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        var grain = new NotificationGrain(inbox, outbox, Serializer, count, ledger);
        _grains.Add(grain);
        return new(grain, inbox, outbox, context, count, ledger, output, events);
    }

    public void Dispose()
    {
        foreach (var grain in _grains) grain.Dispose();
        foreach (var envelope in _owned) envelope.Dispose();
        _payload.Dispose();
        _services.Dispose();
    }

    private sealed record Attempt(NotificationGrain Grain, IDurableInbox Inbox, IDurableOutbox Outbox,
        IInboxHandlerContext Context, TestCount Count, TestLedger Ledger, List<DurableEnvelope> Output, List<string> Events);

    private sealed class TestLedger : Dictionary<HierarchicalKey, string>, IDurableDictionary<HierarchicalKey, string>;

    private sealed class TestCount(List<string> events) : IDurableValue<int>
    {
        private int _value = 7;
        public Action? OnRead { get; set; }
        public Action? OnWrite { get; set; }
        public int Value
        {
            get { OnRead?.Invoke(); return _value; }
            set { _value = value; events.Add("count"); OnWrite?.Invoke(); }
        }
    }
}
