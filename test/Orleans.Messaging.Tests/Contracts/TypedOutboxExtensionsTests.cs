using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class TypedOutboxExtensionsTests
{
    private const string Subject = "orders.result";
    private static GrainId Owner => GrainId.Create("warehouse", "reply-owner");
    private static GrainId ReceivedSender => GrainId.Create("customer", "original-command-sender");
    private static GrainId Destination => GrainId.Create("audit", "explicit-destination");
    private static MessageId CommandId => MessageId.Create("orders", "42", "reserve");

    [Fact]
    public void SendBatch_EnumeratesOnceAndStagesEachMessageBeforeAdvancing()
    {
        using var services = CreateServices();
        var type = new MessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var outbox = new StoringOutbox();
        var enumerations = 0;
        IEnumerable<(MessageId, GrainId, string)> Messages()
        {
            Assert.Equal(1, ++enumerations);
            yield return (CommandId, Destination, "first");
            Assert.Equal(1, outbox.RawSendCalls);
            Assert.Equal("first", type.Decode(Assert.Single(outbox.Messages).Envelope));
            yield return (new MessageId(CommandId.Key.CreateChildKey("second")), ReceivedSender, "second");
        }

        outbox.Send(type, Messages());

        Assert.Equal(1, enumerations);
        Assert.Equal(2, outbox.RawSendCalls);
        Assert.Equal(new[] { CommandId, new MessageId(CommandId.Key.CreateChildKey("second")) }, outbox.Messages.Select(value => value.Envelope.MessageId));
        Assert.Equal(new[] { Destination, ReceivedSender }, outbox.Messages.Select(value => value.ReceiverId));
        Assert.All(outbox.Messages, value => Assert.Equal(Subject, GetSubject(value.Envelope)));
        Assert.Equal(new[] { "first", "second" }, outbox.Messages.Select(message => type.Decode(message.Envelope)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SendBatch_PreparationFailurePreservesEarlierIntentAndOriginalError(bool encodingFailure)
    {
        using var services = CreateServices();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var codec = new FailingCodec(sessions.CodecProvider.GetCodec<string>());
        var type = new MessageType<string>(Subject, new Serializer<string>(codec, sessions));
        var outbox = new StoringOutbox();
        var sentinel = new InvalidDataException("later preparation failed");
        IEnumerable<(MessageId, GrainId, string)> Messages()
        {
            yield return (CommandId, Destination, "first");
            if (!encodingFailure) throw sentinel;
            codec.NextFailure = sentinel;
            yield return (new MessageId(CommandId.Key.CreateChildKey("second")), Destination, "second");
        }

        Assert.Same(sentinel, Assert.Throws<InvalidDataException>(() => outbox.Send(type, Messages())));
        Assert.Equal(1, outbox.RawSendCalls);
        var earlier = Assert.Single(outbox.Messages);
        Assert.Equal(CommandId, earlier.Envelope.MessageId);
        Assert.Equal(Destination, earlier.ReceiverId);
        Assert.Equal(Subject, GetSubject(earlier.Envelope));
        var earlierPayload = earlier.Envelope.Payload.ToArray();
        Assert.Equal("first", type.Decode(earlier.Envelope));
        Assert.Equal(encodingFailure ? 2 : 1, codec.Writes);
        outbox.Send(type, new MessageId(CommandId.Key.CreateChildKey("second")), Destination, "recovered");
        Assert.Equal(new[] { "first", "recovered" }, outbox.Messages.Select(message => type.Decode(message.Envelope)));
        Assert.Equal(2, outbox.RawSendCalls);
        var retained = outbox.Messages.First();
        Assert.Equal(earlierPayload, retained.Envelope.Payload);
        Assert.Equal(new[] { CommandId, new MessageId(CommandId.Key.CreateChildKey("second")) },
            outbox.Messages.Select(message => message.Envelope.MessageId));
        Assert.All(outbox.Messages, message => Assert.Equal(Destination, message.ReceiverId));
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("messageType")]
    [InlineData("messages")]
    public void SendBatch_RejectsNullArgumentsBeforeEncodingOrStaging(string variation)
    {
        using var services = CreateServices();
        var actual = new StoringOutbox();
        var type = new MessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        IOutbox outbox = actual;
        IEnumerable<(MessageId, GrainId, string)> messages = [(CommandId, Destination, "body")];
        switch (variation)
        {
            case "outbox": outbox = null!; break;
            case "messageType": type = null!; break;
            case "messages": messages = null!; break;
        }

        Assert.Equal(variation, Assert.Throws<ArgumentNullException>(() => outbox.Send(type, messages)).ParamName);
        Assert.Equal(0, actual.RawSendCalls);
        Assert.Empty(actual.Messages);
    }

    [Fact]
    public void SendBatch_EmptyBatchStagesNothing()
    {
        using var services = CreateServices();
        var outbox = new StoringOutbox();
        var type = new MessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());

        outbox.Send(type, []);

        Assert.Equal(0, outbox.RawSendCalls);
        Assert.Empty(outbox.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SendBatch_RawFailurePreservesPublishedArrayAndOriginalError(bool storeBeforeFailure)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var type = new MessageType<byte[]>(Subject, serializer);
        var body = new byte[3_145_728];
        body[body.Length / 2] = 17;
        var sentinel = new IOException("raw staging failed");
        var outbox = new StoringOutbox() { Failure = sentinel, StoreBeforeFailure = storeBeforeFailure };
        (MessageId, GrainId, byte[])[] messages =
        [
            (CommandId, Destination, body),
            (new MessageId(CommandId.Key.CreateChildKey("second")), Destination, body)
        ];

        Assert.Same(sentinel, Assert.Throws<IOException>(() => outbox.Send(type, messages)));
        Assert.Equal(1, outbox.RawSendCalls);
        var published = outbox.LastPublished!.Value;
        Assert.NotNull(outbox.WireSeen);
        Assert.Same(published.Envelope.Payload, outbox.WireSeen);
        Assert.Equal(serializer.SerializeToArray(body), published.Envelope.Payload.ToArray());
        Assert.Equal(body, serializer.Deserialize(published.Envelope.Payload));
        if (storeBeforeFailure)
        {
            Assert.Same(published.Envelope.Payload, Assert.Single(outbox.Messages).Envelope.Payload);
            outbox.ClearMessages();
        }
        Assert.Empty(outbox.Messages);
        Assert.Equal(body, serializer.Deserialize(published.Envelope.Payload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Send_SerializesExactEnvelopeWithExplicitDestination(bool addressable)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var outbox = new StoringOutbox();
        IAddressable reference = new AddressableDestination(Destination);

        if (addressable) outbox.Send(type, CommandId, reference, "typed reserve € / 東京");
        else outbox.Send(type, CommandId, Destination, "typed reserve € / 東京");

        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(1, outbox.Count);
        Assert.Equal(CommandId, staged.Envelope.MessageId);
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.Equal(Subject, GetSubject(staged.Envelope));
        Assert.Equal(serializer.SerializeToArray("typed reserve € / 東京"), staged.Envelope.Payload.ToArray());
        Assert.Equal("typed reserve € / 東京", serializer.Deserialize(staged.Envelope.Payload));
        AssertEnvelopeFields(staged.Envelope);
        Assert.True(outbox.TryGetMessage(CommandId, out var found));
        Assert.Same(staged.Envelope.Payload, found.Envelope.Payload);
        Assert.Null(staged.Envelope.DeliverAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Send_CarriesOptionalDeliverAt(bool addressable)
    {
        using var services = CreateServices();
        var type = new MessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var outbox = new StoringOutbox();
        var deliverAt = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));

        if (addressable) outbox.Send(type, CommandId, new AddressableDestination(Destination), "scheduled", deliverAt);
        else outbox.Send(type, CommandId, Destination, "scheduled", deliverAt);

        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(deliverAt, staged.Envelope.DeliverAt);
        Assert.Equal(deliverAt.Offset, staged.Envelope.DeliverAt!.Value.Offset);
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.Equal("scheduled", type.Decode(staged.Envelope));
        Assert.Equal(deliverAt, type.Create(CommandId, Destination, "scheduled", deliverAt).Envelope.DeliverAt);
        Assert.Null(type.Create(CommandId, Destination, "scheduled").Envelope.DeliverAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SendReply_UsesResultChildAndExplicitDestination(bool addressable)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var received = new MessageType<string>("orders.reserve", serializer)
            .Create(CommandId, Owner, "incoming reserve");
        var context = new CountingContext(received.ToInboxMessage());
        var outbox = new StoringOutbox();
        IAddressable reference = new AddressableDestination(Destination);

        if (addressable) outbox.SendReply(type, context, reference, "approved €42");
        else outbox.SendReply(type, context, Destination, "approved €42");

        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(new MessageId(CommandId.Key.CreateChildKey("result")), staged.Envelope.MessageId);
        Assert.Equal("orders/42/reserve/result", staged.Envelope.MessageId.ToString());
        Assert.Equal(4, staged.Envelope.MessageId.Key.CountSegments());
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.NotEqual(ReceivedSender, staged.ReceiverId);
        Assert.Equal(Subject, GetSubject(staged.Envelope));
        Assert.Equal(serializer.SerializeToArray("approved €42"), staged.Envelope.Payload.ToArray());
        Assert.Equal("approved €42", serializer.Deserialize(staged.Envelope.Payload));
        AssertEnvelopeFields(staged.Envelope);
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("incoming reserve", serializer.Deserialize(received.Envelope.Payload));
        Assert.Equal(CommandId, received.Envelope.MessageId);
    }

    [Theory]
    [InlineData(false, "segments")]
    [InlineData(true, "segments")]
    [InlineData(false, "ascii-bytes")]
    [InlineData(true, "ascii-bytes")]
    [InlineData(false, "multibyte-bytes")]
    [InlineData(true, "multibyte-bytes")]
    public void SendReply_ResultChildAtAdmissionLimitEncodesAndStages(bool addressable, string variation)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var key = variation switch
        {
            "segments" => MessageId.Create(Enumerable.Repeat("s", 31).ToArray()),
            "ascii-bytes" => MessageId.Create(new string('k', 1017)),
            "multibyte-bytes" => MessageId.Create(new string('\u20ac', 339)),
            _ => throw new ArgumentOutOfRangeException(nameof(variation))
        };
        var context = Context(key);
        var outbox = new StoringOutbox();

        Invoke(outbox, type, context, key, Destination, "boundary result", reply: true, addressable: addressable);

        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(new MessageId(key.Key.CreateChildKey("result")), staged.Envelope.MessageId);
        if (variation == "segments") Assert.Equal(32, staged.Envelope.MessageId.Key.CountSegments());
        else Assert.Equal(1024, System.Text.Encoding.UTF8.GetByteCount(staged.Envelope.MessageId.ToString()));
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.Equal(serializer.SerializeToArray("boundary result"), staged.Envelope.Payload.ToArray());
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TypedSend_StoresPublishedArrayAndPreservesBytesAfterRemoval(bool reply, bool addressable)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var type = new MessageType<byte[]>(Subject, serializer);
        var body = Enumerable.Range(0, 3_145_728).Select(i => (byte)(i * 31 + 7)).ToArray();
        var wire = serializer.SerializeToArray(body);
        var outbox = new StoringOutbox();
        var context = Context();

        Invoke(outbox, type, context, CommandId, Destination, body, reply, addressable);

        var staged = Assert.Single(outbox.Messages);
        var published = outbox.LastPublished!.Value;
        Assert.NotNull(outbox.WireSeen);
        Assert.Equal(wire, staged.Envelope.Payload.ToArray());
        Assert.Equal(body, serializer.Deserialize(staged.Envelope.Payload));
        Assert.Same(published.Envelope.Payload, staged.Envelope.Payload);
        Assert.Same(published.Envelope.Payload, outbox.WireSeen);
        Assert.NotSame(body, published.Envelope.Payload);
        Assert.Equal(body, serializer.Deserialize(published.Envelope.Payload));
        Assert.Equal(reply ? new MessageId(CommandId.Key.CreateChildKey("result")) : CommandId, staged.Envelope.MessageId);
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.Equal(0, context.CompletionCount);
        outbox.ClearMessages();

        Assert.Empty(outbox.Messages);
        Assert.Equal(0, outbox.Count);
        Assert.Equal(wire, published.Envelope.Payload.ToArray());
        Assert.Equal(body, serializer.Deserialize(published.Envelope.Payload));
        Assert.Equal(1, outbox.RawSendCalls);
        Invoke(outbox, type, context, CommandId, Destination, body, reply, addressable);
        var subsequent = Assert.Single(outbox.Messages);
        Assert.NotSame(published.Envelope.Payload, subsequent.Envelope.Payload);
        Assert.Equal(wire, subsequent.Envelope.Payload.ToArray());
        Assert.Equal(wire, published.Envelope.Payload.ToArray());
        Assert.Equal(2, outbox.RawSendCalls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void TypedSend_RawSendFailurePreservesExactExceptionAndPublishedArray(bool reply, bool addressable, bool storeBeforeFailure)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var type = new MessageType<byte[]>(Subject, serializer);
        var body = Enumerable.Range(0, 3_145_728).Select(i => (byte)(i * 17 + 3)).ToArray();
        var sentinel = new IOException("rawSend sentinel");
        var outbox = new StoringOutbox() { Failure = sentinel, StoreBeforeFailure = storeBeforeFailure };
        var context = Context();

        var exception = Assert.Throws<IOException>(() =>
            Invoke(outbox, type, context, CommandId, Destination, body, reply, addressable));

        Assert.Same(sentinel, exception);
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.NotNull(outbox.WireSeen);
        Assert.Equal(serializer.SerializeToArray(body), outbox.WireSeen.ToArray());
        var published = outbox.LastPublished!.Value;
        Assert.Equal(Destination, published.ReceiverId);
        Assert.Equal(reply ? new MessageId(CommandId.Key.CreateChildKey("result")) : CommandId, published.Envelope.MessageId);
        Assert.Equal(Subject, GetSubject(published.Envelope));
        Assert.Same(outbox.WireSeen, published.Envelope.Payload);
        if (storeBeforeFailure)
        {
            var stored = Assert.Single(outbox.Messages);
            Assert.Same(published.Envelope.Payload, stored.Envelope.Payload);
            Assert.Equal(body, serializer.Deserialize(stored.Envelope.Payload));
            outbox.ClearMessages();
        }
        Assert.Equal(body, serializer.Deserialize(published.Envelope.Payload));
        Assert.Empty(outbox.Messages);
        Assert.Equal(0, outbox.Count);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TypedSend_PartialEncodingFailureNeverStagesAndNextCallHasPristineWire(bool reply, bool addressable)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var codec = new FailingCodec(sessions.CodecProvider.GetCodec<string>());
        var type = new MessageType<string>(Subject, new Serializer<string>(codec, sessions));
        var outbox = new StoringOutbox();
        var context = Context();
        var sentinel = new InvalidDataException("committed encoder sentinel");
        codec.NextFailure = sentinel;

        var exception = Assert.Throws<InvalidDataException>(() =>
            Invoke(outbox, type, context, CommandId, Destination, "failing", reply, addressable));

        Assert.Same(sentinel, exception);
        Assert.Equal(1, codec.Writes);
        Assert.Equal(1, codec.CommittedFailures);
        Assert.Equal(0, outbox.RawSendCalls);
        Assert.Null(outbox.LastPublished);
        Assert.Null(outbox.WireSeen);
        Assert.Empty(outbox.Messages);
        Assert.Equal(0, context.CompletionCount);
        Invoke(outbox, type, context, CommandId, Destination, "recovered €42", reply, addressable);
        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(direct.SerializeToArray("recovered €42"), staged.Envelope.Payload.ToArray());
        Assert.Equal("recovered €42", direct.Deserialize(staged.Envelope.Payload));
        Assert.Equal(reply ? new MessageId(CommandId.Key.CreateChildKey("result")) : CommandId, staged.Envelope.MessageId);
        Assert.Equal(2, codec.Writes);
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(0, context.CompletionCount);
    }

    public static IEnumerable<object[]> ValidationCases()
    {
        foreach (var reply in new[] { false, true })
            foreach (var addressable in new[] { false, true })
            {
                foreach (var variation in new[] { "outbox", "messageType", "body", "bytes", "multibyte-bytes" })
                    yield return [reply, addressable, variation];
                if (reply)
                {
                    yield return [reply, addressable, "context"];
                    yield return [reply, addressable, "unset-parent"];
                }
                else yield return [reply, addressable, "messageId"];
                yield return [reply, addressable, addressable ? "null-reference" : "destination"];
                if (addressable) yield return [reply, addressable, "invalid-reference"];
            }
    }

    [Theory]
    [MemberData(nameof(ValidationCases))]
    public void TypedSend_ValidationFailureNeverEncodesOrStages(bool reply, bool addressable, string variation)
    {
        using var services = CreateServices();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var codec = new FailingCodec(sessions.CodecProvider.GetCodec<string>());
        var type = new MessageType<string>(Subject, new Serializer<string>(codec, sessions));
        var realOutbox = new StoringOutbox();
        IOutbox outbox = variation == "outbox" ? null! : realOutbox;
        var key = variation switch
        {
            "messageId" or "unset-parent" => default,
            // Reply adds seven UTF8 bytes ("/result").
            "bytes" => MessageId.Create(new string('k', reply ? 1018 : 1025)),
            "multibyte-bytes" => MessageId.Create(new string('\u20ac', reply ? 339 : 341) + (reply ? "a" : "ab")),
            _ => CommandId
        };
        var context = Context(reply ? key : null);
        var destination = variation is "destination" or "invalid-reference" ? default : Destination;
        var body = variation == "body" ? null! : "never encoded";
        if (variation == "messageType") type = null!;
        if (variation == "context") context = null!;

        var exception = Record.Exception(() =>
        {
            if (addressable)
            {
                IAddressable reference = variation == "null-reference" ? null! : new AddressableDestination(destination);
                if (reply) outbox.SendReply(type, context, reference, body);
                else outbox.Send(type, key, reference, body);
            }
            else if (reply) outbox.SendReply(type, context, destination, body);
            else outbox.Send(type, key, destination, body);
        });

        if (variation == "unset-parent")
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            var argumentError = Assert.IsAssignableFrom<ArgumentException>(exception);
            Assert.Equal(variation switch
            {
                "null-reference" => "destination",
                "invalid-reference" => "grain",
                "destination" => "receiverId",
                "bytes" or "multibyte-bytes" or "messageId" => "messageId",
                _ => variation
            }, argumentError.ParamName);
        }
        Assert.Equal(0, codec.Writes);
        Assert.Equal(0, realOutbox.RawSendCalls);
        Assert.Null(realOutbox.LastPublished);
        Assert.Null(realOutbox.WireSeen);
        Assert.Empty(realOutbox.Messages);
        Assert.Equal(0, realOutbox.Count);
        if (context is not null) Assert.Equal(0, context.CompletionCount);
        // Positive control: same codec still encodes and stages after rejection.
        var positive = new StoringOutbox();
        Invoke(positive,
            new MessageType<string>(Subject, new Serializer<string>(codec, sessions)),
            Context(), CommandId, Destination, "valid control", reply, addressable);
        Assert.Equal(1, codec.Writes);
        Assert.Equal(1, positive.RawSendCalls);
        var staged = Assert.Single(positive.Messages);
        Assert.Equal(services.GetRequiredService<Serializer<string>>().SerializeToArray("valid control"), staged.Envelope.Payload.ToArray());
        Assert.Equal(Destination, staged.ReceiverId);
    }

    private static ServiceProvider CreateServices() =>
        new ServiceCollection().AddSerializer().BuildServiceProvider();

    private static CountingContext Context(MessageId? key = null)
    {
        var messageId = key ?? CommandId;
        if (messageId == default) return new(new InboxMessage { Envelope = default });
        using var services = CreateServices();
        return new(new InboxMessage
        {
            Envelope = new Envelope
            {
                MessageId = messageId,
                Payload = [],
                Subject = "orders.reserve",
                Headers = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["sender"] = services.GetRequiredService<Serializer<GrainId>>().SerializeToArray(ReceivedSender)
                }
            }
        });
    }

    private static void Invoke<T>(IOutbox outbox, MessageType<T> type, CountingContext context,
        MessageId key, GrainId destination, T body, bool reply, bool addressable)
    {
        if (addressable)
        {
            IAddressable reference = new AddressableDestination(destination);
            if (reply) outbox.SendReply(type, context, reference, body);
            else outbox.Send(type, key, reference, body);
        }
        else if (reply) outbox.SendReply(type, context, destination, body);
        else outbox.Send(type, key, destination, body);
    }

    private sealed class AddressableDestination : IAddressable, IGrainBase
    {
        public AddressableDestination(GrainId id)
        {
            GrainContext = Substitute.For<IGrainContext>();
            GrainContext.GrainId.Returns(id);
        }
        public IGrainContext GrainContext { get; }
    }

    private sealed class CountingContext(InboxMessage message) : IInboxHandlerContext
    {
        public InboxMessage Message { get; } = message;
        public int CompletionCount { get; private set; }
        public void Complete() => CompletionCount++;
        public void Fail(string reason) => throw new InvalidOperationException("Unexpected dead letter.");
    }

    private sealed class StoringOutbox : IOutbox
    {
        private readonly List<OutboxMessage> _messages = [];
        public int Count => _messages.Count;
        public IEnumerable<OutboxMessage> Messages => _messages;
        public int RawSendCalls { get; private set; }
        public OutboxMessage? LastPublished { get; private set; }
        public byte[]? WireSeen { get; private set; }
        public Exception? Failure { get; set; }
        public bool StoreBeforeFailure { get; set; }

        public void Send(OutboxMessage message)
        {
            RawSendCalls++;
            LastPublished = message;
            WireSeen = message.Envelope.Payload;
            if (Failure is { } failure)
            {
                if (StoreBeforeFailure) _messages.Add(message);
                throw failure;
            }
            _messages.Add(message);
        }

        public void Send(MessageId messageId) => throw new NotSupportedException();

        public IOutbox Register(IOutboxMessageSource source) => throw new NotSupportedException();

        public bool TryGetMessage(MessageId messageId, [MaybeNullWhen(false)] out OutboxMessage envelope)
        {
            foreach (var message in _messages)
                if (message.Envelope.MessageId == messageId)
                {
                    envelope = message;
                    return true;
                }
            envelope = default;
            return false;
        }

        public void ClearMessages() => _messages.Clear();
    }

    private static string GetSubject(Envelope envelope)
    {
        return Assert.IsType<string>(envelope.Subject);
    }


    private static void AssertEnvelopeFields(Envelope envelope)
    {
        Assert.Equal(Subject, envelope.Subject);
        Assert.Null(envelope.Headers);
    }

    private sealed class FailingCodec(IFieldCodec<string> inner) : IFieldCodec<string>
    {
        public int Writes { get; private set; }
        public int CommittedFailures { get; private set; }
        public Exception? NextFailure { get; set; }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
            [AllowNull] Type expectedType, [AllowNull] string value) where TBufferWriter : IBufferWriter<byte>
        {
            Writes++;
            if (NextFailure is { } failure)
            {
                NextFailure = null;
                writer.WriteByte(0x7e);
                writer.Commit();
                CommittedFailures++;
                throw failure;
            }
            inner.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }

        [return: MaybeNull]
        public string ReadValue<TInput>(ref Reader<TInput> reader, Field field) => inner.ReadValue(ref reader, field);
    }
}
