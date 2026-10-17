using Documentation.Grains.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingSnippetTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly GrainId Sender = GrainId.Create("sender", "snippet");
    private static readonly GrainId Receiver = GrainId.Create("notification", "snippet");
    private static readonly MessageId Command = MessageId.Create("notifications", "42");
    private Serializer Serializer => _services.GetRequiredService<Serializer>();

    private readonly Messages _messages;

    public MessagingSnippetTests() => _messages = new(Serializer);

    private MessageType<T> Type<T>() => typeof(Messages).GetProperties()
        .Select(property => property.GetValue(_messages)).OfType<MessageType<T>>().Single();

    [Fact]
    public async Task NotificationReply_DerivesResultIdentityFromApplicationCommand()
    {
        var attempt = await CreateAsync(new Notify("received message", Sender));
        var handling = attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "send", "count", "complete" }, attempt.Events);
        await handling;
        var reply = Assert.Single(attempt.Output);
        Assert.Equal(Sender, reply.ReceiverId);
        Assert.Equal(new MessageId(Command.Key.CreateChildKey("result")), reply.Envelope.MessageId);
        Assert.Equal(Type<NotificationReceived>().Subject, GetSubject(reply.Envelope));
        Assert.Equal(new NotificationReceived("received message", Command), Type<NotificationReceived>().Decode(reply.Envelope));
        Assert.Equal(8, attempt.Count.Value);
        attempt.Outbox.Received(1).Send(reply);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationHandling_PreMutationCancellationPreservesBusinessState()
    {
        var attempt = await CreateAsync(new Notify("received message", Sender));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await attempt.Handler.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_CancellationDuringSynchronousPreparationPreservesCompletedOutcome()
    {
        var attempt = await CreateAsync(new Notify("prepared message", Sender));
        using var cancellation = new CancellationTokenSource();
        attempt.Count.OnRead = cancellation.Cancel;

        await attempt.Handler.HandleAsync(attempt.Context, cancellation.Token);

        attempt.Count.OnRead = null;
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(8, attempt.Count.Value);
        Assert.Single(attempt.Output);
        Assert.Equal(new[] { "send", "count", "complete" }, attempt.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationHandling_CancellationAfterFirstMutationCompletesBeforeMethodReturns(bool replyRequested)
    {
        var attempt = await CreateAsync(new Notify("prepared message", replyRequested ? Sender : null));
        using var cancellation = new CancellationTokenSource();
        attempt.Count.OnWrite = () =>
        {
            Assert.False(cancellation.IsCancellationRequested);
            cancellation.Cancel();
        };

        var handling = attempt.Handler.HandleAsync(attempt.Context, cancellation.Token);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(replyRequested ? new[] { "send", "count", "complete" } : new[] { "count", "complete" }, attempt.Events);
        await handling;
        Assert.Equal(8, attempt.Count.Value);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationWithoutReply_StagesCountAndCompletes()
    {
        var attempt = await CreateAsync(new Notify("received message"));
        var handling = attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

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
        var attempt = await CreateAsync(new Notify(body));
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.IsType(body is null ? typeof(ArgumentNullException) : typeof(ArgumentException), exception);
        Assert.Equal("message.Text", exception.ParamName);
        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_UnexpectedSubjectPreservesBusinessState()
    {
        var attempt = await CreateAsync(new Notify("prepared"));
        var unexpected = Type<NotificationReceived>().Create(
            Command, Receiver, new NotificationReceived("receipt", Command));
        attempt.Context.Message.Returns(unexpected.ToInboxMessage());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Events);
        Assert.Empty(attempt.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shipment_OrdinarySerializationRoundTripPreservesReservationAndManifest(bool emptyManifest)
    {
        var request = new ReserveStock(3, Sender);
        byte[] manifest = [1, 2, 3];
        if (emptyManifest) manifest = [];
        var shipment = new Shipment(request, manifest);
        var serializer = _services.GetRequiredService<Serializer<Shipment>>();

        var bytes = serializer.SerializeToArray(shipment);
        if (!emptyManifest) manifest[0] = 99;
        var decoded = Assert.IsType<Shipment>(serializer.Deserialize(bytes));

        Assert.Equal(request, decoded.Reservation);
        Assert.NotSame(shipment, decoded);
        if (emptyManifest)
        {
            Assert.Empty(decoded.Manifest);
        }
        else
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Manifest);
            Assert.NotSame(manifest, decoded.Manifest);
        }
    }

    [Fact]
    public async Task ShipmentSender_StagesOrdinaryRecordBeforeAwaitingJournalAcknowledgement()
    {
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var shipments = _messages.Shipment;
        var grain = new ShipmentSenderGrain(outbox, state, _messages);
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new List<OutboxMessage>();
        var events = new List<string>();
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call =>
        {
            output.Add(call.Arg<OutboxMessage>());
            events.Add("send");
        });
        state.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            events.Add("write");
            return new ValueTask(acknowledgement.Task);
        });
        var shipment = new Shipment(new ReserveStock(3, Sender), [1, 2, 3]);

        var sending = grain.SendAsync(Command, Receiver, shipment);

        Assert.False(sending.IsCompleted);
        Assert.Equal(new[] { "send", "write" }, events);
        shipment.Manifest[0] = 99;
        var envelope = Assert.Single(output);
        Assert.Equal(Command, envelope.Envelope.MessageId);
        Assert.Equal(Receiver, envelope.ReceiverId);
        Assert.Equal(_messages.Shipment.Subject, GetSubject(envelope.Envelope));
        var received = shipments.Decode(envelope.Envelope);
        Assert.Equal(shipment.Reservation, received.Reservation);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.Manifest);
        acknowledgement.SetResult();
        await sending;
        await state.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShipmentSender_InvalidBodyLeavesIntentAndJournalWriteUnstaged()
    {
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var grain = new ShipmentSenderGrain(outbox, state, _messages);

        await Assert.ThrowsAsync<ArgumentNullException>(() => grain.SendAsync(Command, Receiver, null!));

        outbox.DidNotReceive().Send(Arg.Any<OutboxMessage>());
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Envelope_CopyAndSerializationPreserveBytesAndIsolateDecodedArrays()
    {
        var created = Type<Notify>().Create(Command, Receiver, new Notify("managed"));
        var envelope = created with
        {
            Envelope = created.Envelope with
            {
                Headers = new() { ["sender"] = _services.GetRequiredService<Serializer<GrainId>>().SerializeToArray(Sender) }
            }
        };
        Assert.NotNull(envelope.Envelope.Headers);
        var originalBytes = envelope.Envelope.Payload.ToArray();
        var view = envelope;
        Assert.Same(envelope.Envelope.Payload, view.Envelope.Payload);
        Assert.Same(envelope.Envelope.Headers, view.Envelope.Headers);
        var copied = _services.GetRequiredService<DeepCopier<OutboxMessage>>().Copy(envelope);
        Assert.NotNull(copied.Envelope.Headers);
        Assert.Equal(envelope.Envelope.MessageId, copied.Envelope.MessageId);
        Assert.Equal(GetSender(envelope.Envelope), GetSender(copied.Envelope));
        Assert.Equal(envelope.ReceiverId, copied.ReceiverId);
        Assert.Equal(GetSubject(envelope.Envelope), GetSubject(copied.Envelope));
        Assert.NotSame(envelope.Envelope.Payload, copied.Envelope.Payload);
        Assert.NotSame(envelope.Envelope.Headers, copied.Envelope.Headers);
        Assert.NotSame(envelope.Envelope.Headers["sender"], copied.Envelope.Headers["sender"]);
        Assert.Equal(originalBytes, copied.Envelope.Payload.ToArray());

        var first = Serializer.SerializeToArray(envelope);
        var second = Serializer.SerializeToArray(envelope);
        Assert.Equal(first, second);
        Assert.NotSame(first, second);
        var decoded = Serializer.Deserialize<OutboxMessage>(first);
        Assert.NotNull(decoded.Envelope.Headers);
        Assert.Equal(envelope.Envelope.MessageId, decoded.Envelope.MessageId);
        Assert.Equal(GetSender(envelope.Envelope), GetSender(decoded.Envelope));
        Assert.Equal(envelope.ReceiverId, decoded.ReceiverId);
        Assert.Equal(GetSubject(envelope.Envelope), GetSubject(decoded.Envelope));
        Assert.NotSame(envelope.Envelope.Payload, decoded.Envelope.Payload);
        Assert.NotSame(copied.Envelope.Payload, decoded.Envelope.Payload);
        Assert.NotSame(envelope.Envelope.Headers, decoded.Envelope.Headers);
        Assert.NotSame(copied.Envelope.Headers, decoded.Envelope.Headers);
        Assert.NotSame(envelope.Envelope.Headers["sender"], decoded.Envelope.Headers["sender"]);
        Assert.NotSame(copied.Envelope.Headers["sender"], decoded.Envelope.Headers["sender"]);
        Assert.Equal(originalBytes, decoded.Envelope.Payload.ToArray());
        Assert.Equal(new Notify("managed"), Type<Notify>().Decode(decoded.Envelope));
        Assert.Equal(new Notify("managed"), Type<Notify>().Decode(copied.Envelope));
        Assert.Equal(new Notify("managed"), Type<Notify>().Decode(envelope.Envelope));
        Assert.Equal(originalBytes, envelope.Envelope.Payload.ToArray());
        var originalSender = envelope.Envelope.Headers["sender"].ToArray();
        envelope.Envelope.Payload[0] ^= 0xff;
        envelope.Envelope.Headers["sender"][0] ^= 0xff;
        envelope.Envelope.Headers.Add("extra", [42]);
        foreach (var independent in new[] { copied.Envelope, decoded.Envelope })
        {
            Assert.NotNull(independent.Headers);
            Assert.Equal(originalBytes, independent.Payload);
            Assert.Equal(originalSender, independent.Headers["sender"]);
            Assert.False(independent.Headers.ContainsKey("extra"));
            Assert.Equal(new Notify("managed"), Type<Notify>().Decode(independent));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShipmentEnvelope_TraceHeaderAndPayloadSurviveIndependentRpcCopy(bool emptyManifest)
    {
        var traceId = Guid.Parse("0a165cab-a41e-45ef-a884-b558309cf1d8");
        var shipment = new Shipment(new ReserveStock(3, Sender), emptyManifest ? [] : [1, 2, 255]);
        var outgoing = ShipmentEnvelope.Create(Serializer, Command, Sender, Receiver, shipment, traceId);
        Assert.NotNull(outgoing.Envelope.Headers);
        var originalPayload = outgoing.Envelope.Payload.ToArray();
        var originalHeaders = outgoing.Envelope.Headers.ToDictionary(
            header => header.Key, header => header.Value.ToArray(), StringComparer.Ordinal);
        var copied = _services.GetRequiredService<DeepCopier<OutboxMessage>>().Copy(outgoing);
        var decoded = Serializer.Deserialize<OutboxMessage>(Serializer.SerializeToArray(outgoing));
        var inbox = outgoing.ToInboxMessage();
        var received = Serializer.Deserialize<InboxMessage>(Serializer.SerializeToArray(inbox));
        var copiedIncoming = _services.GetRequiredService<DeepCopier<InboxMessage>>().Copy(inbox);
        var traceSerializer = _services.GetRequiredService<Serializer<Guid>>();

        Assert.Equal(traceId, ShipmentEnvelope.ReadTrace(inbox, traceSerializer));
        Assert.Equal(traceId, ShipmentEnvelope.ReadTrace(received, traceSerializer));
        Assert.Equal(traceId, ShipmentEnvelope.ReadTrace(copiedIncoming, traceSerializer));
        Assert.Same(outgoing.Envelope.Payload, inbox.Envelope.Payload);
        Assert.Same(outgoing.Envelope.Headers, inbox.Envelope.Headers);
        var independentEnvelopes = new[] { outgoing.Envelope, copied.Envelope, decoded.Envelope,
            received.Envelope, copiedIncoming.Envelope };
        for (var index = 0; index < independentEnvelopes.Length; index++)
        {
            var firstEnvelope = independentEnvelopes[index];
            Assert.NotNull(firstEnvelope.Headers);
            for (var other = index + 1; other < independentEnvelopes.Length; other++)
            {
                var secondEnvelope = independentEnvelopes[other];
                Assert.NotNull(secondEnvelope.Headers);
                Assert.NotSame(firstEnvelope.Payload, secondEnvelope.Payload);
                Assert.NotSame(firstEnvelope.Headers, secondEnvelope.Headers);
                foreach (var key in originalHeaders.Keys)
                    Assert.NotSame(firstEnvelope.Headers[key], secondEnvelope.Headers[key]);
            }
        }
        Assert.NotNull(inbox.Envelope.Headers);
        foreach (var key in originalHeaders.Keys)
            Assert.Same(outgoing.Envelope.Headers[key], inbox.Envelope.Headers[key]);
        Assert.Equal(Receiver, copied.ReceiverId);
        Assert.Equal(Receiver, decoded.ReceiverId);
        if (!emptyManifest) shipment.Manifest[0] = 99;
        foreach (var envelope in independentEnvelopes)
        {
            Assert.NotNull(envelope.Headers);
            Assert.Equal(Command, envelope.MessageId);
            Assert.Equal(originalPayload, envelope.Payload);
            Assert.Equal(new[] { "sender", "trace-id" }, envelope.Headers.Keys.OrderBy(key => key, StringComparer.Ordinal));
            foreach (var header in originalHeaders)
                Assert.Equal(header.Value, envelope.Headers[header.Key]);
            Assert.Equal(_messages.Shipment.Subject, GetSubject(envelope));
            Assert.Equal(Sender, GetSender(envelope));
            Assert.True(envelope.Headers.TryGetValue("trace-id", out var traceBytes));
            Assert.Equal(traceSerializer.SerializeToArray(traceId), traceBytes);
            Assert.Equal(traceId, traceSerializer.Deserialize(traceBytes));
            var payload = Assert.IsType<Shipment>(
                _services.GetRequiredService<Serializer<Shipment>>().Deserialize(envelope.Payload));
            Assert.Equal(new ReserveStock(3, Sender), payload.Reservation);
            Assert.Equal(emptyManifest ? Array.Empty<byte>() : new byte[] { 1, 2, 255 }, payload.Manifest);
            Assert.NotSame(shipment, payload);
            Assert.False(envelope.Headers.ContainsKey("Trace-Id"));
            Assert.False(envelope.Headers.ContainsKey("subject"));
            Assert.False(envelope.Headers.ContainsKey("payload"));
        }
        foreach (var header in originalHeaders)
            Assert.Equal(header.Value, outgoing.Envelope.Headers[header.Key]);

        var missingTrace = _messages.Shipment.Create(Command, Receiver,
                new Shipment(new ReserveStock(3, Sender), emptyManifest ? [] : [1, 2, 255])).ToInboxMessage();
        Assert.Null(missingTrace.Envelope.Headers);
        var missingTraceBytes = missingTrace.Envelope.Payload.ToArray();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ShipmentEnvelope.ReadTrace(missingTrace, traceSerializer));
        Assert.Equal("The shipment requires its trace-id header.", exception.Message);
        Assert.Equal(missingTraceBytes, missingTrace.Envelope.Payload);
        Assert.Equal(Command, missingTrace.Envelope.MessageId);
        var missingTracePayload = Assert.IsType<Shipment>(
            _services.GetRequiredService<Serializer<Shipment>>().Deserialize(missingTrace.Envelope.Payload));
        Assert.Equal(new ReserveStock(3, Sender), missingTracePayload.Reservation);
        Assert.Equal(emptyManifest ? Array.Empty<byte>() : new byte[] { 1, 2, 255 }, missingTracePayload.Manifest);
    }

    [Fact]
    public void Envelope_RawAndTypedCustomHeadersRoundTripWithPayload()
    {
        var traceId = Guid.Parse("2a14d07e-a278-409a-91ed-dc2911d9561c");
        byte[] customBytes = [0, 17, 128, 255];
        var traceSerializer = _services.GetRequiredService<Serializer<Guid>>();
        var envelope = new Envelope
        {
            MessageId = Command,
            Subject = Type<Notify>().Subject,
            Payload = _services.GetRequiredService<Serializer<Notify>>().SerializeToArray(new Notify("custom headers")),
            Headers = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["sender"] = _services.GetRequiredService<Serializer<GrainId>>().SerializeToArray(Sender),
                ["custom-raw"] = customBytes,
                ["custom-trace"] = traceSerializer.SerializeToArray(traceId)
            }
        };
        Assert.NotNull(envelope.Headers);
        Assert.Same(customBytes, envelope.Headers["custom-raw"]);
        var copied = _services.GetRequiredService<DeepCopier<Envelope>>().Copy(envelope);
        var decoded = Serializer.Deserialize<Envelope>(Serializer.SerializeToArray(envelope));
        Assert.NotNull(copied.Headers);
        Assert.NotNull(decoded.Headers);

        Assert.NotSame(envelope.Payload, copied.Payload);
        Assert.NotSame(envelope.Payload, decoded.Payload);
        Assert.NotSame(copied.Payload, decoded.Payload);
        Assert.NotSame(envelope.Headers, copied.Headers);
        Assert.NotSame(envelope.Headers, decoded.Headers);
        Assert.NotSame(copied.Headers, decoded.Headers);
        foreach (var key in envelope.Headers.Keys)
        {
            Assert.NotSame(envelope.Headers[key], copied.Headers[key]);
            Assert.NotSame(envelope.Headers[key], decoded.Headers[key]);
            Assert.NotSame(copied.Headers[key], decoded.Headers[key]);
        }
        foreach (var value in new[] { envelope, copied, decoded })
        {
            Assert.NotNull(value.Headers);
            Assert.Equal(Command, value.MessageId);
            Assert.Equal(Type<Notify>().Subject, GetSubject(value));
            Assert.Equal(Sender, GetSender(value));
            Assert.Equal(new Notify("custom headers"), Type<Notify>().Decode(value));
            Assert.True(value.Headers.TryGetValue("custom-raw", out var raw));
            Assert.Equal(new byte[] { 0, 17, 128, 255 }, raw);
            Assert.True(value.Headers.TryGetValue("custom-trace", out var traceBytes));
            Assert.Equal(traceId, traceSerializer.Deserialize(traceBytes));
            Assert.Equal(traceSerializer.SerializeToArray(traceId), traceBytes);
            Assert.NotSame(raw, traceBytes);
            Assert.NotSame(raw, value.Payload);
            Assert.Equal(_services.GetRequiredService<Serializer<Notify>>().SerializeToArray(new Notify("custom headers")),
                value.Payload.ToArray());
            Assert.Equal(new[] { "custom-raw", "custom-trace", "sender" },
                value.Headers.Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.False(value.Headers.ContainsKey("not-present"));
            Assert.False(value.Headers.ContainsKey("Custom-Raw"));
        }
        customBytes[0] = 99;
        envelope.Headers.Add("extra", [42]);
        Assert.Equal(new byte[] { 99, 17, 128, 255 }, envelope.Headers["custom-raw"]);
        foreach (var independent in new[] { copied, decoded })
        {
            Assert.NotNull(independent.Headers);
            Assert.Equal(new byte[] { 0, 17, 128, 255 }, independent.Headers["custom-raw"]);
            Assert.False(independent.Headers.ContainsKey("extra"));
        }
    }

    [Fact]
    public async Task NotificationHandling_AbsentSubjectPreservesBusinessStateAndCompletion()
    {
        var attempt = await CreateAsync(new Notify("prepared"));
        var incoming = new InboxMessage
        {
            Envelope = new Envelope
            {
                MessageId = Command,
                Payload = _services.GetRequiredService<Serializer<Notify>>().SerializeToArray(new Notify("prepared"))
            }
        };
        attempt.Context.Message.Returns(incoming);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Events);
        Assert.Empty(attempt.Output);
        attempt.Context.DidNotReceive().Complete();
        attempt.Context.DidNotReceive().Fail(Arg.Any<string>());
        attempt.Outbox.DidNotReceive().Send(Arg.Any<OutboxMessage>());
        Assert.Equal(Command, incoming.Envelope.MessageId);
        Assert.Equal(new Notify("prepared"),
            _services.GetRequiredService<Serializer<Notify>>().Deserialize(incoming.Envelope.Payload));
    }

    [Fact]
    public async Task NotificationHandling_StagingFailureLeavesBusinessAndCompletionUnstaged()
    {
        var outbox = Substitute.For<IOutbox>();
        var context = Substitute.For<IInboxHandlerContext>();
        var input = Type<Notify>().Create(Command, Receiver, new Notify("prepared", Sender));
        context.Message.Returns(input.ToInboxMessage());
        byte[] publishedReply = [];
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call =>
        {
            publishedReply = call.Arg<OutboxMessage>().Envelope.Payload.ToArray();
            throw new IOException("staging failed");
        });
        var handler = new TestInbox();
        var count = new TestCount([]);
        var grain = new NotificationGrain(handler, outbox, _messages, count);
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(async () =>
            await handler.HandleAsync(context, TestContext.Current.CancellationToken));
        context.DidNotReceive().Complete();
        Assert.NotEmpty(publishedReply);
        Assert.Equal(new NotificationReceived("prepared", Command),
            _services.GetRequiredService<Serializer<NotificationReceived>>().Deserialize(publishedReply));
        Assert.Equal(7, count.Value);
    }

    private async Task<Attempt> CreateAsync(Notify message)
    {
        var outbox = Substitute.For<IOutbox>();
        var context = Substitute.For<IInboxHandlerContext>();
        var input = Type<Notify>().Create(Command, Receiver, message);
        context.Message.Returns(input.ToInboxMessage());
        var events = new List<string>();
        var output = new List<OutboxMessage>();
        var count = new TestCount(events);
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call =>
        {
            output.Add(call.Arg<OutboxMessage>());
            events.Add("send");
        });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        var handler = new TestInbox();
        var grain = new NotificationGrain(handler, outbox, _messages, count);
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);
        return new(grain, handler, outbox, context, count, output, events);
    }

    public void Dispose() => _services.Dispose();

    private static string GetSubject(Envelope envelope)
    {
        return Assert.IsType<string>(envelope.Subject);
    }

    private GrainId GetSender(Envelope envelope)
    {
        Assert.NotNull(envelope.Headers);
        Assert.True(envelope.Headers.TryGetValue("sender", out var bytes));
        return _services.GetRequiredService<Serializer<GrainId>>().Deserialize(bytes);
    }

    private sealed record Attempt(NotificationGrain Grain, TestInbox Handler, IOutbox Outbox,
        IInboxHandlerContext Context, TestCount Count, List<OutboxMessage> Output, List<string> Events);

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
