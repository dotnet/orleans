using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class EnvelopeContractTests
{
    private static MessageId Id => MessageId.Create("orders", "123");
    private static GrainId Receiver => GrainId.Create("receiver", "123");

    [Fact]
    public void RequiredPayloadAndOptionalMetadata()
    {
        var envelope = Create([]);
        Validate(envelope);
        Validate(new InboxMessage { Envelope = envelope });
        Assert.Equal(Id, envelope.MessageId);
        Assert.Empty(envelope.Payload);
        Assert.Null(envelope.Subject);
        Assert.Null(envelope.Headers);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<Envelope>>();
        var decoded = serializer.Deserialize(serializer.SerializeToArray(envelope));
        Validate(decoded);
        Assert.Empty(decoded.Payload);
        Assert.Null(decoded.Subject);
        Assert.Null(decoded.Headers);
        Assert.Throws<ArgumentException>(() => Validate(new OutboxMessage { Envelope = envelope, ReceiverId = default }));
        Assert.Throws<ArgumentException>(() => Validate(default(Envelope)));
        Assert.Throws<ArgumentException>(() => Validate(envelope with { Payload = null! }));
    }

    [Fact]
    public void FirstClassSubjectAndEncodedSenderUseByteHeaders()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var senderSerializer = services.GetRequiredService<Serializer<GrainId>>();
        var sender = GrainId.Create("sender", "123");
        var envelope = Create([1]) with
        {
            Subject = "orders.v1",
            Headers = new(StringComparer.Ordinal)
            {
                ["subject"] = "extra subject"u8.ToArray(),
                ["sender"] = senderSerializer.SerializeToArray(sender),
                ["custom"] = "first"u8.ToArray(),
                ["CUSTOM"] = "second"u8.ToArray()
            }
        };
        Validate(envelope);
        Assert.Equal("orders.v1", envelope.Subject);
        Assert.Equal(sender, senderSerializer.Deserialize(envelope.Headers["sender"]));
        Assert.Equal("extra subject"u8.ToArray(), envelope.Headers["subject"]);
        Assert.Equal("first"u8.ToArray(), envelope.Headers["custom"]);
        Assert.Equal("second"u8.ToArray(), envelope.Headers["CUSTOM"]);
        Assert.False(envelope.Headers.TryAdd("custom", "replacement"u8.ToArray()));
        Assert.Equal("first"u8.ToArray(), envelope.Headers["custom"]);
        var serializer = services.GetRequiredService<Serializer<Envelope>>();
        var decoded = serializer.Deserialize(serializer.SerializeToArray(envelope));
        Assert.NotNull(decoded.Headers);
        Assert.Equal("orders.v1", decoded.Subject);
        Assert.Equal(sender, senderSerializer.Deserialize(decoded.Headers["sender"]));
        Assert.NotSame(envelope.Headers["sender"], decoded.Headers["sender"]);
        Assert.Equal("extra subject"u8.ToArray(), decoded.Headers["subject"]);
    }

    [Fact]
    public void StructAssignmentAndLocalConversionSharePublishedObjects()
    {
        var envelope = Create([0, 255, 128]) with { Headers = new(StringComparer.Ordinal) { ["trace"] = "original"u8.ToArray() } };
        var assigned = envelope;
        var outgoing = new OutboxMessage { Envelope = envelope, ReceiverId = Receiver };
        var incoming = outgoing.ToInboxMessage();
        Assert.Same(envelope.Payload, assigned.Payload);
        Assert.Same(envelope.Headers, assigned.Headers);
        Assert.Same(envelope.Payload, incoming.Envelope.Payload);
        Assert.Same(envelope.Headers, incoming.Envelope.Headers);
        Assert.Equal(Id, incoming.Envelope.MessageId);
        Assert.Null(typeof(InboxMessage).GetProperty("ReceiverId"));
        Assert.Null(typeof(Envelope).GetProperty("ReceiverId"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100000)]
    public void OrdinaryCopyAndIndependentWireRoundTrip(int length)
    {
        using var sending = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receiving = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var envelope = Create(payload) with
        {
            Subject = "orders.v1",
            Headers = new(StringComparer.Ordinal) { ["custom"] = "retained"u8.ToArray(), ["CUSTOM"] = "distinct"u8.ToArray(), ["empty"] = [] }
        };
        var serializer = sending.GetRequiredService<Serializer<Envelope>>();
        var wire = serializer.SerializeToArray(envelope);
        var decoded = receiving.GetRequiredService<Serializer<Envelope>>().Deserialize(wire);
        var copied = sending.GetRequiredService<DeepCopier>().Copy(envelope);
        foreach (var actual in new[] { decoded, copied })
        {
            Validate(actual);
            Assert.Equal(Id, actual.MessageId);
            Assert.Equal(payload, actual.Payload);
            Assert.Equal("orders.v1", actual.Subject);
            Assert.NotNull(actual.Headers);
            Assert.NotSame(envelope.Headers, actual.Headers);
            Assert.Equal("retained"u8.ToArray(), actual.Headers["custom"]);
            Assert.Equal("distinct"u8.ToArray(), actual.Headers["CUSTOM"]);
            Assert.Empty(actual.Headers["empty"]);
            Assert.NotSame(envelope.Headers["custom"], actual.Headers["custom"]);
            actual.Headers["custom"][0] = 0;
            Assert.Equal("retained"u8.ToArray(), envelope.Headers["custom"]);
            if (length > 0)
            {
                Assert.NotSame(envelope.Payload, actual.Payload);
                actual.Payload[0] = 42;
                Assert.Equal((byte)0, envelope.Payload[0]);
            }
        }
        Assert.Equal(wire, serializer.SerializeToArray(envelope));
    }

    [Fact]
    public void DeliverAtRoundTripsThroughCreateAndIndependentWire()
    {
        using var sending = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receiving = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var deliverAt = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        var type = new MessageType<string>("orders.v1", sending.GetRequiredService<Serializer<string>>());
        var outgoing = type.Create(Id, Receiver, "scheduled", deliverAt);
        Assert.Equal(deliverAt, outgoing.Envelope.DeliverAt);
        Validate(outgoing);

        var wire = sending.GetRequiredService<Serializer<OutboxMessage>>().SerializeToArray(outgoing);
        var decoded = receiving.GetRequiredService<Serializer<OutboxMessage>>().Deserialize(wire);
        var copied = sending.GetRequiredService<DeepCopier>().Copy(outgoing);
        foreach (var actual in new[] { decoded, copied })
        {
            Assert.Equal(deliverAt, actual.Envelope.DeliverAt);
            Assert.Equal(deliverAt.Offset, actual.Envelope.DeliverAt!.Value.Offset);
            Assert.Equal(Receiver, actual.ReceiverId);
            Assert.Equal("scheduled", type.Decode(actual.Envelope));
            Assert.True(Invoke<bool>("EnvelopeEquivalence", "AreEquivalent", outgoing, actual));
        }
    }

    [Fact]
    public void PayloadUsesOrdinaryExternallyBoundOrleansSerializer()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = Create(serializer.SerializeToArray("typed application payload")) with { Subject = "typed.v1" };
        Assert.Equal("typed application payload", serializer.Deserialize(envelope.Payload));
        Assert.Equal("typed.v1", envelope.Subject);
        Assert.Null(envelope.Headers);
    }

    [Theory]
    [InlineData("InboxMessage")]
    [InlineData("OutboxMessage")]
    [InlineData("InboxDeadLetter")]
    [InlineData("OutboxDeadLetter")]
    public void MessageAndDeadLetterCopyPreservesMetadataAndIsolation(string kind)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var envelope = Create([0, 255]) with { Subject = "subject.v1", Headers = new() { ["custom"] = "value"u8.ToArray() } };
        object value = kind.StartsWith("Inbox", StringComparison.Ordinal) ? new InboxMessage { Envelope = envelope }
            : new OutboxMessage { Envelope = envelope, ReceiverId = Receiver };
        if (kind.EndsWith("DeadLetter", StringComparison.Ordinal))
        {
            var type = typeof(Envelope).Assembly.GetType("Orleans.Messaging." + kind, true)!;
            var record = Activator.CreateInstance(type)!;
            type.GetProperty("Message")!.SetValue(record, value);
            type.GetProperty("DeadLetteredAt")!.SetValue(record, DateTimeOffset.UnixEpoch);
            type.GetProperty("Reason")!.SetValue(record, "failure");
            type.GetProperty("AttemptCount")!.SetValue(record, 3);
            value = record;
        }
        var serializer = services.GetRequiredService<Serializer>();
        foreach (var result in new[] { serializer.Deserialize<object>(serializer.SerializeToArray(value)), services.GetRequiredService<DeepCopier>().Copy(value) })
        {
            Assert.NotNull(result);
            var message = kind.EndsWith("DeadLetter", StringComparison.Ordinal) ? result.GetType().GetProperty("Message")!.GetValue(result)! : result;
            var actual = message is InboxMessage incoming ? incoming.Envelope : Assert.IsType<OutboxMessage>(message).Envelope;
            Assert.Equal(new byte[] { 0, 255 }, actual.Payload);
            Assert.NotSame(envelope.Payload, actual.Payload);
            Assert.Equal("subject.v1", actual.Subject);
            Assert.NotNull(actual.Headers);
            Assert.NotSame(envelope.Headers, actual.Headers);
            Assert.Equal("value"u8.ToArray(), actual.Headers["custom"]);
            Assert.NotSame(envelope.Headers!["custom"], actual.Headers["custom"]);
            if (message is OutboxMessage outgoing) Assert.Equal(Receiver, outgoing.ReceiverId);
            if (kind.EndsWith("DeadLetter", StringComparison.Ordinal)) Assert.Equal(3, result.GetType().GetProperty("AttemptCount")!.GetValue(result));
        }
    }

    [Fact]
    public void PublicDeadLetterRoundTripsThroughOrleansSerialization()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var letter = new DeadLetter<InboxMessage>
        {
            Message = new InboxMessage { Envelope = Create([1, 2]) with { Subject = "subject.v1" } },
            DeadLetteredAt = DateTimeOffset.UnixEpoch,
            Reason = "failure",
            AttemptCount = 3,
            Code = "orders/invalid"
        };
        var serializer = services.GetRequiredService<Serializer<DeadLetter<InboxMessage>>>();

        var result = Assert.IsType<DeadLetter<InboxMessage>>(serializer.Deserialize(serializer.SerializeToArray(letter)));

        Assert.Equal(letter.Message.Envelope.MessageId, result.Message.Envelope.MessageId);
        Assert.Equal(new byte[] { 1, 2 }, result.Message.Envelope.Payload);
        Assert.Equal("subject.v1", result.Message.Envelope.Subject);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.DeadLetteredAt);
        Assert.Equal("failure", result.Reason);
        Assert.Equal(3, result.AttemptCount);
        Assert.Equal("orders/invalid", result.Code);
    }

    [Fact]
    public void SimpleEnvelopeSurfaceUsesGeneratedOrdinaryFields()
    {
        AssertIds(typeof(Envelope), ("MessageId", 0u), ("Payload", 1u), ("Subject", 2u), ("Headers", 3u), ("DeliverAt", 4u));
        AssertIds(typeof(InboxMessage), ("Envelope", 0u));
        AssertIds(typeof(OutboxMessage), ("Envelope", 0u), ("ReceiverId", 1u));
        Assert.Equal(typeof(Dictionary<string, byte[]>), typeof(Envelope).GetProperty("Headers")!.PropertyType);
        Assert.Equal(typeof(byte[]), typeof(Envelope).GetProperty("Payload")!.PropertyType);
        Assert.Null(typeof(Envelope).GetCustomAttribute<ImmutableAttribute>());
        Assert.Single(typeof(Envelope).GetCustomAttributes<GenerateSerializerAttribute>());
        Assert.Equal("Orleans.Messaging.Envelope", typeof(Envelope).GetCustomAttribute<AliasAttribute>()!.Alias);
        Assert.Equal(typeof(DateTimeOffset?), typeof(Envelope).GetProperty("DeliverAt")!.PropertyType);
        Assert.Equal(new[] { "DeliverAt", "Headers", "MessageId", "Payload", "Subject" }, typeof(Envelope).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Null(typeof(Envelope).Assembly.GetType("Orleans.Messaging.PackedEnvelopeHeaders"));
        Assert.Null(typeof(Envelope).Assembly.GetType("Orleans.Messaging.EnvelopeBuilder"));
        Assert.Equal(typeof(void), typeof(IInboxHandlerContext).GetMethod("Fail")!.ReturnType);
        Assert.Equal(typeof(string), Assert.Single(typeof(IInboxHandlerContext).GetMethod("Fail")!.GetParameters()).ParameterType);
        Assert.Equal(new[] { "Accepted:1", "Backpressured:3", "DeadLettered:5", "Duplicate:2", "HandlerNotFound:4" },
            Enum.GetNames<DeliveryStatus>().Select(name => $"{name}:{(int)Enum.Parse<DeliveryStatus>(name)}").Order(StringComparer.Ordinal));
        AssertIds(typeof(DeliveryResult), ("Status", 0u), ("Message", 1u), ("Code", 2u));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var outgoing = new OutboxMessage { Envelope = Create([7]), ReceiverId = Receiver };
        var other = outgoing with { ReceiverId = GrainId.Create("receiver", "other") };
        var serializer = services.GetRequiredService<Serializer<InboxMessage>>();
        Assert.Equal(serializer.SerializeToArray(outgoing.ToInboxMessage()), serializer.SerializeToArray(other.ToInboxMessage()));
    }

    [Theory]
    [InlineData("same", true, true)]
    [InlineData("headers", true, true)]
    [InlineData("sender", true, true)]
    [InlineData("receiver", false, true)]
    [InlineData("id", false, false)]
    [InlineData("child", false, false)]
    [InlineData("subject", false, false)]
    [InlineData("subject-case", false, false)]
    [InlineData("no-subject", false, false)]
    [InlineData("payload", false, false)]
    [InlineData("payload-length", false, false)]
    [InlineData("deliver-at-offset", true, true)]
    [InlineData("deliver-at", false, false)]
    [InlineData("no-deliver-at", false, false)]
    public void PendingCommandEqualityIgnoresExtraHeadersAndSender(string change, bool outbox, bool inbox)
    {
        var deliverAt = new DateTimeOffset(2040, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var envelope = Create([0, 255, 128]) with { Subject = "subject.v1", Headers = new() { ["custom"] = "first"u8.ToArray() }, DeliverAt = deliverAt };
        var original = new OutboxMessage { Envelope = envelope, ReceiverId = Receiver };
        var repeated = new OutboxMessage
        {
            ReceiverId = change == "receiver" ? GrainId.Create("receiver", "other") : Receiver,
            Envelope = envelope with
            {
                MessageId = change == "id" ? MessageId.Create("other") : change == "child" ? new MessageId(Id.Key.CreateChildKey("child")) : Id,
                Payload = change == "payload" ? [0, 255, 129] : change == "payload-length" ? [0, 255] : [0, 255, 128],
                Subject = change == "subject" ? "other" : change == "subject-case" ? "Subject.v1" : change == "no-subject" ? null : "subject.v1",
                Headers = new() { ["custom"] = Encoding.UTF8.GetBytes(change == "headers" ? "second" : "first"), ["sender"] = change == "sender" ? [2] : [1] },
                DeliverAt = change switch
                {
                    "deliver-at-offset" => deliverAt.ToOffset(TimeSpan.FromHours(2)),
                    "deliver-at" => deliverAt.AddTicks(1),
                    "no-deliver-at" => null,
                    _ => deliverAt
                }
            }
        };
        Assert.Equal(outbox, Invoke<bool>("EnvelopeEquivalence", "AreEquivalent", original, repeated));
        Assert.Equal(inbox, Invoke<bool>("EnvelopeEquivalence", "AreSameCommand", original.ToInboxMessage(), repeated.ToInboxMessage()));
        Assert.Equal(inbox, Invoke<bool>("EnvelopeEquivalence", "AreSameCommand", original.Envelope, repeated.Envelope));
        Assert.Equal("first"u8.ToArray(), original.Envelope.Headers!["custom"]);
        Assert.Equal(new byte[] { 0, 255, 128 }, original.Envelope.Payload);
    }

    [Fact]
    public void PendingRawCommandsAllowAbsentSubject()
    {
        var first = Create([]);
        var repeated = first with { Headers = new() { ["custom"] = "different"u8.ToArray() } };
        Assert.True(Invoke<bool>("EnvelopeEquivalence", "AreSameCommand", first, repeated));
        Assert.Null(first.Subject);
        Assert.Null(first.Headers);
    }

    [Theory]
    [InlineData("id", 1024, true)]
    [InlineData("id", 1025, false)]
    // Segment depth is bounded only by the encoded message ID size.
    [InlineData("depth", 512, true)]
    [InlineData("depth", 513, false)]
    [InlineData("unicode-id", 512, true)]
    [InlineData("unicode-id", 513, false)]
    [InlineData("subject", 256, true)]
    [InlineData("subject", 257, false)]
    [InlineData("unicode-subject", 128, true)]
    [InlineData("unicode-subject", 129, false)]
    public void IdentityAndSubjectAdmissionBoundaries(string field, int length, bool valid)
    {
        var envelope = Create([]) with
        {
            MessageId = field switch
            {
                "id" => MessageId.Create(new string('x', length)),
                "depth" => MessageId.Create(Enumerable.Repeat("x", length).ToArray()),
                "unicode-id" => MessageId.Create(new string('\u00e9', length)),
                _ => Id
            },
            Subject = field.Contains("subject", StringComparison.Ordinal) ? new string(field == "subject" ? 's' : '\u00e9', length) : null
        };
        if (valid) { Validate(envelope); Assert.Empty(envelope.Payload); }
        else Assert.Throws<ArgumentException>(() => Validate(envelope));
    }

    [Theory]
    [InlineData("empty-subject")]
    [InlineData("invalid-subject")]
    [InlineData("empty-header-key")]
    [InlineData("null-header-value")]
    [InlineData("lone-high-surrogate-header-key")]
    [InlineData("lone-low-surrogate-header-key")]
    [InlineData("reversed-surrogate-header-key")]
    public void InvalidMetadataIsRejectedBeforeAdmission(string invalid)
    {
        var envelope = Create([1]) with
        {
            Subject = invalid == "empty-subject" ? "" : invalid == "invalid-subject" ? "\ud800" : null,
            Headers = invalid switch
            {
                "empty-header-key" => new() { [""] = "value"u8.ToArray() },
                "null-header-value" => new() { ["key"] = null! },
                "lone-high-surrogate-header-key" => new() { ["trace-\ud83d"] = "value"u8.ToArray() },
                "lone-low-surrogate-header-key" => new() { ["\ude00-trace"] = "value"u8.ToArray() },
                "reversed-surrogate-header-key" => new() { ["trace-\ude00\ud83d"] = "value"u8.ToArray() },
                _ => null
            }
        };
        Assert.ThrowsAny<ArgumentException>(() => Validate(envelope));
        Assert.ThrowsAny<ArgumentException>(() => Validate(new InboxMessage { Envelope = envelope }));
        Assert.ThrowsAny<ArgumentException>(() => Validate(new OutboxMessage { Envelope = envelope, ReceiverId = Receiver }));
        Assert.Equal(new byte[] { 1 }, envelope.Payload);
    }

    [Fact]
    public void SurrogatePairHeaderKeysRoundTripThroughOrleansSerialization()
    {
        using var sending = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receiving = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var envelope = Create([1]) with
        {
            MessageId = MessageId.Create("order-\U0001F600"),
            Headers = new(StringComparer.Ordinal) { ["trace-\U0001F600"] = "first"u8.ToArray(), ["trace-\U0001F601"] = "second"u8.ToArray() }
        };
        Validate(envelope);

        var decoded = receiving.GetRequiredService<Serializer<Envelope>>().Deserialize(sending.GetRequiredService<Serializer<Envelope>>().SerializeToArray(envelope));

        Validate(decoded);
        Assert.Equal(envelope.MessageId, decoded.MessageId);
        Assert.NotNull(decoded.Headers);
        Assert.Equal(["trace-\U0001F600", "trace-\U0001F601"], decoded.Headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("first"u8.ToArray(), decoded.Headers["trace-\U0001F600"]);
        Assert.Equal("second"u8.ToArray(), decoded.Headers["trace-\U0001F601"]);
    }

    private static Envelope Create(byte[] payload) => new() { MessageId = Id, Payload = payload };
    private static void Validate<T>(T message) => Invoke<object?>("EnvelopeValidation", "Validate", message!);
    private static T Invoke<T>(string type, string method, params object[] args)
    {
        var helper = typeof(Envelope).Assembly.GetType("Orleans.Messaging." + type, true)!;
        try { return (T)helper.GetMethod(method, args.Select(a => a.GetType()).ToArray())!.Invoke(null, args)!; }
        catch (TargetInvocationException exception) when (exception.InnerException is { } cause)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
            throw;
        }
    }
    private static void AssertIds(Type type, params (string Name, uint Id)[] expected)
    {
        var actual = type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(member => member.GetCustomAttributes<IdAttribute>().Select(id => (member.Name, id.Id)))
            .OrderBy(p => p.Id).ToArray();
        Assert.Equal(expected.OrderBy(p => p.Id), actual);
    }
}
