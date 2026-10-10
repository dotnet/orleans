using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class TypedOutboxExtensionsTests
{
    private const string Subject = "orders.result";
    private static GrainId Owner => GrainId.Create("warehouse", "reply-owner");
    private static GrainId ReceivedSender => GrainId.Create("customer", "original-command-sender");
    private static GrainId Destination => GrainId.Create("audit", "explicit-destination");
    private static HierarchicalKey CommandId => HierarchicalKey.Create("orders", "42", "reserve");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Send_SerializesExactEnvelopeWithOutboxOwnerAndExplicitDestination(bool addressable)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new DurableMessageType<string>(Subject, serializer);
        using var outbox = new RetainingOutbox(Owner);
        IAddressable reference = new AddressableDestination(Destination);

        if (addressable) outbox.Send(type, CommandId, reference, "typed reserve € / 東京");
        else outbox.Send(type, CommandId, Destination, "typed reserve € / 東京");

        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(1, outbox.Count);
        Assert.Equal(CommandId, staged.MessageId);
        Assert.Equal(Owner, staged.SenderId);
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.Equal(Subject, staged.Subject);
        Assert.Equal(serializer.SerializeToArray("typed reserve € / 東京"), staged.Payload.ToArray());
        Assert.Equal("typed reserve € / 東京", serializer.Deserialize(staged.Payload));
        Assert.True(outbox.TryGetMessage(CommandId, out var found));
        Assert.Equal(staged.Payload, found.Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SendReply_UsesResultChildExplicitDestinationAndOwnerNotReceivedSender(bool addressable)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new DurableMessageType<string>(Subject, serializer);
        using var received = new DurableMessageType<string>("orders.reserve", serializer)
            .Create(CommandId, ReceivedSender, Owner, "incoming reserve");
        var context = new CountingContext(received);
        using var outbox = new RetainingOutbox(Owner);
        IAddressable reference = new AddressableDestination(Destination);

        if (addressable) outbox.SendReply(type, context, reference, "approved €42");
        else outbox.SendReply(type, context, Destination, "approved €42");

        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(CommandId.CreateChildKey("result"), staged.MessageId);
        Assert.Equal("orders/42/reserve/result", staged.MessageId.ToString());
        Assert.Equal(4, staged.MessageId.SegmentCount);
        Assert.Equal(Owner, staged.SenderId);
        Assert.NotEqual(ReceivedSender, staged.SenderId);
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.NotEqual(ReceivedSender, staged.ReceiverId);
        Assert.Equal(Subject, staged.Subject);
        Assert.Equal(serializer.SerializeToArray("approved €42"), staged.Payload.ToArray());
        Assert.Equal("approved €42", serializer.Deserialize(staged.Payload));
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("incoming reserve", serializer.Deserialize(received.Payload));
        Assert.Equal(CommandId, received.MessageId);
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
        var type = new DurableMessageType<string>(Subject, serializer);
        var key = variation switch
        {
            "segments" => HierarchicalKey.Create(Enumerable.Repeat("s", 31).ToArray()),
            "ascii-bytes" => HierarchicalKey.Create(new string('k', 1017)),
            "multibyte-bytes" => HierarchicalKey.Create(new string('\u20ac', 339)),
            _ => throw new ArgumentOutOfRangeException(nameof(variation))
        };
        var context = Context(key);
        using var outbox = new RetainingOutbox(Owner);

        Invoke(outbox, type, context, key, Destination, "boundary result", reply: true, addressable: addressable);

        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(key.CreateChildKey("result"), staged.MessageId);
        if (variation == "segments") Assert.Equal(32, staged.MessageId.SegmentCount);
        else Assert.Equal(1024, System.Text.Encoding.UTF8.GetByteCount(staged.MessageId.ToString()));
        Assert.Equal(Owner, staged.SenderId);
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.Equal(serializer.SerializeToArray("boundary result"), staged.Payload.ToArray());
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TypedSend_ReleasesLocalOwnerWhileRawSendRetainedPinSurvives(bool reply, bool addressable)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var type = new DurableMessageType<byte[]>(Subject, serializer);
        var body = Enumerable.Range(0, 3_145_728).Select(i => (byte)(i * 31 + 7)).ToArray();
        var wire = serializer.SerializeToArray(body);
        using var outbox = new RetainingOutbox(Owner);
        var context = Context();

        Invoke(outbox, type, context, CommandId, Destination, body, reply, addressable);

        var staged = Assert.Single(outbox.Messages);
        var borrowed = outbox.LastBorrowed!.Value;
        Assert.Equal(wire, staged.Payload.ToArray());
        Assert.Equal(body, serializer.Deserialize(staged.Payload));
        Assert.Equal(body, serializer.Deserialize(borrowed.Payload));
        Assert.Equal(reply ? CommandId.CreateChildKey("result") : CommandId, staged.MessageId);
        Assert.Equal(Owner, staged.SenderId);
        Assert.Equal(Destination, staged.ReceiverId);
        Assert.Equal(0, context.CompletionCount);
        // The first and last pages may be shared with other pooled creates.
        // An interior consumed page belongs only to this multi-page payload:
        // neither the pooled writer nor a concurrent neighboring slice pins it.
        var interior = outbox.InteriorBorrowed!.Value;
        Assert.NotNull(interior.First.Next);
        var page = interior.First;
        var version = page.Version;
        outbox.ReleaseMessages();

        Assert.NotEqual(version, page.Version);
        Assert.Throws<InvalidOperationException>(() => interior.ToArray());
        Assert.Empty(outbox.Messages);
        Assert.Equal(0, outbox.Count);
        Assert.Equal(1, outbox.RawSendCalls);
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
    public void TypedSend_RawSendFailurePreservesExactExceptionAndReleasesLocalOwner(bool reply, bool addressable, bool retainBeforeFailure)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var type = new DurableMessageType<byte[]>(Subject, serializer);
        var body = Enumerable.Range(0, 3_145_728).Select(i => (byte)(i * 17 + 3)).ToArray();
        var sentinel = new IOException("rawSend sentinel");
        using var outbox = new RetainingOutbox(Owner) { Failure = sentinel, RetainBeforeFailure = retainBeforeFailure };
        var context = Context();

        var exception = Assert.Throws<IOException>(() =>
            Invoke(outbox, type, context, CommandId, Destination, body, reply, addressable));

        Assert.Same(sentinel, exception);
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(serializer.SerializeToArray(body), outbox.WireSeen);
        var borrowed = outbox.LastBorrowed!.Value;
        Assert.Equal(Owner, borrowed.SenderId);
        Assert.Equal(Destination, borrowed.ReceiverId);
        if (retainBeforeFailure)
        {
            var independent = Assert.Single(outbox.Messages);
            Assert.Equal(body, serializer.Deserialize(independent.Payload));
            outbox.ReleaseMessages();
        }
        var interior = outbox.InteriorBorrowed!.Value;
        Assert.Throws<InvalidOperationException>(() => interior.ToArray());
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
        var type = new DurableMessageType<string>(Subject, new Serializer<string>(codec, sessions));
        using var outbox = new RetainingOutbox(Owner);
        var context = Context();
        var sentinel = new InvalidDataException("committed encoder sentinel");
        codec.NextFailure = sentinel;

        var exception = Assert.Throws<InvalidDataException>(() =>
            Invoke(outbox, type, context, CommandId, Destination, "failing", reply, addressable));

        Assert.Same(sentinel, exception);
        Assert.Equal(1, codec.Writes);
        Assert.Equal(1, codec.CommittedFailures);
        Assert.Equal(0, outbox.RawSendCalls);
        Assert.Null(outbox.LastBorrowed);
        Assert.Empty(outbox.Messages);
        Assert.Equal(0, context.CompletionCount);
        Invoke(outbox, type, context, CommandId, Destination, "recovered €42", reply, addressable);
        var staged = Assert.Single(outbox.Messages);
        Assert.Equal(direct.SerializeToArray("recovered €42"), staged.Payload.ToArray());
        Assert.Equal("recovered €42", direct.Deserialize(staged.Payload));
        Assert.Equal(reply ? CommandId.CreateChildKey("result") : CommandId, staged.MessageId);
        Assert.Equal(2, codec.Writes);
        Assert.Equal(1, outbox.RawSendCalls);
        Assert.Equal(0, context.CompletionCount);
    }

    public static IEnumerable<object[]> ValidationCases()
    {
        foreach (var reply in new[] { false, true })
        foreach (var addressable in new[] { false, true })
        {
            foreach (var variation in new[] { "outbox", "messageType", "body", "sender", "segments", "bytes", "multibyte-bytes" })
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
        var type = new DurableMessageType<string>(Subject, new Serializer<string>(codec, sessions));
        using var realOutbox = new RetainingOutbox(variation == "sender" ? default : Owner);
        IDurableOutbox outbox = variation == "outbox" ? null! : realOutbox;
        var key = variation switch
        {
            "messageId" or "unset-parent" => default,
            // Reply adds one segment / seven UTF8 bytes ("/result").
            "segments" => HierarchicalKey.Create(Enumerable.Repeat("s", reply ? 32 : 33).ToArray()),
            "bytes" => HierarchicalKey.Create(new string('k', reply ? 1018 : 1025)),
            "multibyte-bytes" => HierarchicalKey.Create(new string('\u20ac', reply ? 339 : 341) + (reply ? "a" : "ab")),
            _ => CommandId
        };
        var context = Context(key);
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
                "sender" or "destination" or "segments" or "bytes" or "multibyte-bytes" or "messageId" => "envelope",
                _ => variation
            }, argumentError.ParamName);
        }
        Assert.Equal(0, codec.Writes);
        Assert.Equal(0, realOutbox.RawSendCalls);
        Assert.Null(realOutbox.LastBorrowed);
        Assert.Empty(realOutbox.Messages);
        Assert.Equal(0, realOutbox.Count);
        if (context is not null) Assert.Equal(0, context.CompletionCount);
        // Positive control: same codec still encodes and stages after rejection.
        using var positive = new RetainingOutbox(Owner);
        Invoke(positive,
            new DurableMessageType<string>(Subject, new Serializer<string>(codec, sessions)),
            Context(), CommandId, Destination, "valid control", reply, addressable);
        Assert.Equal(1, codec.Writes);
        Assert.Equal(1, positive.RawSendCalls);
        var staged = Assert.Single(positive.Messages);
        Assert.Equal(services.GetRequiredService<Serializer<string>>().SerializeToArray("valid control"), staged.Payload.ToArray());
        Assert.Equal(Owner, staged.SenderId);
        Assert.Equal(Destination, staged.ReceiverId);
    }

    private static ServiceProvider CreateServices() =>
        new ServiceCollection().AddSerializer().BuildServiceProvider();

    private static CountingContext Context(HierarchicalKey? key = null) => new(new DurableEnvelope
    {
        MessageId = key ?? CommandId,
        SenderId = ReceivedSender,
        ReceiverId = Owner,
        Subject = "orders.reserve",
        Payload = default
    });

    private static void Invoke<T>(IDurableOutbox outbox, DurableMessageType<T> type, CountingContext context,
        HierarchicalKey key, GrainId destination, T body, bool reply, bool addressable)
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

    private sealed class CountingContext(DurableEnvelope envelope) : IInboxHandlerContext
    {
        public DurableEnvelope Envelope { get; } = envelope;
        public int CompletionCount { get; private set; }
        public void Complete() => CompletionCount++;
    }

    private sealed class RetainingOutbox(GrainId senderId) : IDurableOutbox, IDisposable
    {
        private readonly List<DurableEnvelope> _messages = [];
        public GrainId SenderId { get; } = senderId;
        public int Count => _messages.Count;
        public IEnumerable<DurableEnvelope> Messages => _messages;
        public int RawSendCalls { get; private set; }
        public DurableEnvelope? LastBorrowed { get; private set; }
        public ArcBuffer? InteriorBorrowed { get; private set; }
        public byte[]? WireSeen { get; private set; }
        public Exception? Failure { get; set; }
        public bool RetainBeforeFailure { get; set; }

        public void Send(DurableEnvelope envelope)
        {
            RawSendCalls++;
            LastBorrowed = envelope; // Borrowed only. Never disposed by this fake.
            WireSeen = envelope.Payload.ToArray(); // Serialization must be complete on entry.
            if (envelope.Payload.Length > 2_097_152)
            {
                // Borrow an interior page without acquiring another owner.
                InteriorBorrowed = envelope.Payload.UnsafeSlice(
                    envelope.Payload.First.Length - envelope.Payload.Offset, 1);
            }
            if (Failure is { } failure)
            {
                if (RetainBeforeFailure) _messages.Add(envelope.Retain());
                throw failure;
            }
            _messages.Add(envelope.Retain()); // Independent durable-state owner.
        }

        public bool TryGetMessage(HierarchicalKey messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
        {
            foreach (var message in _messages)
                if (message.MessageId == messageId)
                {
                    envelope = message;
                    return true;
                }
            envelope = default;
            return false;
        }

        public void ReleaseMessages()
        {
            foreach (var envelope in _messages) envelope.Dispose();
            _messages.Clear();
        }
        public void Dispose() => ReleaseMessages();
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
