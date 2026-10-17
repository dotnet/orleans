using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Hosting;
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
public sealed class TypedMessageHelperTests
{
    private const string Subject = "orders.reserve";
    private static GrainId Sender => GrainId.Create("sender", "typed-helper");
    private static GrainId Receiver => GrainId.Create("receiver", "warehouse");
    private static MessageId MessageKey => MessageId.Create("orders", "42", "reserve");

    [Fact]
    public void MessageType_RejectsNullSerializer()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new MessageType<string>(Subject, (Serializer<string>)null!));
        Assert.Equal("serializer", exception.ParamName);

        exception = Assert.Throws<ArgumentNullException>(
            () => new MessageType<string>(Subject, (Serializer)null!));
        Assert.Equal("serializer", exception.ParamName);
    }

    [Fact]
    public void MessageType_CreatedFromSerializer_RoundTripsBody()
    {
        using var services = CreateServices();
        var messageType = new MessageType<string>(Subject, services.GetRequiredService<Serializer>());

        var message = messageType.Create(MessageKey, Receiver, "payload");

        Assert.Equal(Subject, messageType.Subject);
        Assert.Equal(Subject, message.Envelope.Subject);
        Assert.Equal("payload", messageType.Decode(message.Envelope));
    }

    [Fact]
    public void MessageType_RejectsNullSubject()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var exception = Assert.Throws<ArgumentNullException>(
            () => new MessageType<string>(null!, serializer));
        Assert.Equal("subject", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(Subject, new MessageType<string>(Subject, serializer).Subject);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("ascii")]
    [InlineData("multibyte")]
    public void MessageType_RejectsEmptyOrOversizedUtf8Subject(string variation)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var subject = InvalidSubject(variation);

        var exception = Assert.Throws<ArgumentException>(() => new MessageType<string>(subject, serializer));
        Assert.Equal("subject", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(Subject, new MessageType<string>(Subject, serializer).Subject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MessageType_AcceptsSubjectAtUtf8ByteLimit(bool multibyte)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var subject = SubjectAtLimit(multibyte);
        var messageType = new MessageType<string>(subject, serializer);
        var envelope = DirectEnvelope(serializer, subject, "boundary reserve €42");

        Assert.Equal(256, Encoding.UTF8.GetByteCount(subject));
        Assert.Equal(subject, messageType.Subject);
        Assert.Equal("boundary reserve €42", messageType.Decode(envelope));
    }

    [Theory]
    [InlineData("reserve order 42: € / 東京")]
    [InlineData("")]
    [InlineData("first line\nsecond line")]
    public void MessageType_DecodeReadsDirectSerializerPayload(string body)
    {
        using var sendingServices = CreateServices();
        using var receivingServices = CreateServices();
        var sendingSerializer = sendingServices.GetRequiredService<Serializer<string>>();
        var receivingSerializer = receivingServices.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(sendingSerializer, Subject, body);
        var expectedWire = sendingSerializer.SerializeToArray(body);
        var messageType = new MessageType<string>(Subject, receivingSerializer);

        Assert.Equal(body, messageType.Decode(envelope));
        Assert.Equal(Subject, messageType.Subject);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Equal(body, receivingSerializer.Deserialize(envelope.Payload));
    }

    [Theory]
    [InlineData("Orders.Reserve")]
    [InlineData("orders.cancel")]
    public void MessageType_DecodeRejectsWrongSubjectBeforeCodecRead(string wrongSubject)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var sentinel = new InvalidDataException("read must not run");
        probe.ReadFailure = sentinel;
        var messageType = new MessageType<string>(Subject, serializer);
        var envelope = DirectEnvelope(direct, wrongSubject, "reserve €42");
        var expectedWire = direct.SerializeToArray("reserve €42");

        var exception = Assert.Throws<ArgumentException>(() => messageType.Decode(envelope));

        Assert.Equal("envelope", exception.ParamName);
        Assert.NotSame(sentinel, exception);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Equal("reserve €42", direct.Deserialize(envelope.Payload));
    }

    [Fact]
    public void MessageType_DecodeRejectsDirectSerializedNull()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, null!);
        var expectedWire = serializer.SerializeToArray(null);
        var messageType = new MessageType<string>(Subject, serializer);
        Assert.Null(serializer.Deserialize(envelope.Payload));

        var exception = Assert.Throws<ArgumentException>(() => messageType.Decode(envelope));

        Assert.Equal("envelope", exception.ParamName);
        Assert.Equal(Subject, messageType.Subject);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Null(serializer.Deserialize(envelope.Payload));
    }

    [Fact]
    public void AddMessageType_RejectsNullServiceCollection()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => MessageTypeExtensions.AddMessageType<string>(null!, Subject));
        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddMessageType_RejectsNullSubjectWithoutChangingServices()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        var before = services.ToArray();

        var exception = Assert.Throws<ArgumentNullException>(
            () => services.AddMessageType<string>(null!));

        Assert.Equal("subject", exception.ParamName);
        AssertUnchangedServices(before, services);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("ascii")]
    [InlineData("multibyte")]
    public void AddMessageType_RejectsEmptyOrOversizedUtf8SubjectWithoutChangingServices(string variation)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        var before = services.ToArray();
        var subject = InvalidSubject(variation);

        var exception = Assert.Throws<ArgumentException>(() => services.AddMessageType<string>(subject));

        Assert.Equal("subject", exception.ParamName);
        AssertUnchangedServices(before, services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddMessageType_AcceptsSubjectAtUtf8ByteLimit(bool multibyte)
    {
        var subject = SubjectAtLimit(multibyte);
        var services = new ServiceCollection();
        services.AddSerializer();
        Assert.Same(services, services.AddMessageType<string>(subject));
        using var provider = services.BuildServiceProvider();
        var descriptor = provider.GetRequiredKeyedService<MessageType<string>>(subject);
        var envelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), subject, "registered boundary");

        Assert.Equal(256, Encoding.UTF8.GetByteCount(subject));
        Assert.Equal(subject, descriptor.Subject);
        Assert.Equal("registered boundary", descriptor.Decode(envelope));
        Assert.Same(descriptor, provider.GetRequiredKeyedService<MessageType<string>>(subject));
    }

    [Fact]
    public void AddMessageType_ReturnsOriginalCollectionAndRegistersExactKeyedSingleton()
    {
        var services = new ServiceCollection();
        var result = services.AddMessageType<string>(Subject);
        Assert.Same(services, result);
        Assert.Equal(2, services.Count);
        var registration = Assert.Single(services, descriptor => descriptor.IsKeyedService);
        Assert.True(registration.IsKeyedService);
        Assert.Equal(Subject, registration.ServiceKey);
        Assert.Equal(typeof(MessageType<string>), registration.ServiceType);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);

        // Successful registration before adding serializers proves lazy resolution.
        services.AddSerializer();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var messageType = provider.GetRequiredKeyedService<MessageType<string>>(Subject);
        Assert.Equal(Subject, messageType.Subject);
        Assert.Same(messageType, provider.GetRequiredKeyedService<MessageType<string>>(Subject));
        Assert.Same(messageType, scope.ServiceProvider.GetRequiredKeyedService<MessageType<string>>(Subject));
        var envelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), Subject, "keyed reserve");
        Assert.Equal("keyed reserve", messageType.Decode(envelope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddMessageType_RegistersClosedSingletonSerializerFactoryBeforeKeyedBinding(bool valueType)
    {
        if (valueType) AssertClosedSerializerFactory(731);
        else AssertClosedSerializerFactory("closed factory €42");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AddMessageType_PreservesExistingCustomClosedSerializerAndUsesItForBinding(bool valueType, bool factory)
    {
        if (valueType) AssertCustomClosedSerializerPreserved(731, factory);
        else AssertCustomClosedSerializerPreserved("custom closed €42", factory);
    }

    [Fact]
    public void AddMessageType_AllowsMultipleSubjectsForSamePayloadType()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddMessageType<string>(Subject).AddMessageType<string>("orders.cancel");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var reserve = provider.GetRequiredKeyedService<MessageType<string>>(Subject);
        var cancel = provider.GetRequiredKeyedService<MessageType<string>>("orders.cancel");

        Assert.Equal(Subject, reserve.Subject);
        Assert.Equal("orders.cancel", cancel.Subject);
        Assert.NotSame(reserve, cancel);
        Assert.Same(reserve, provider.GetRequiredKeyedService<MessageType<string>>(Subject));
        Assert.Same(cancel, provider.GetRequiredKeyedService<MessageType<string>>("orders.cancel"));
        Assert.Same(reserve, scope.ServiceProvider.GetRequiredKeyedService<MessageType<string>>(Subject));
        Assert.Same(cancel, scope.ServiceProvider.GetRequiredKeyedService<MessageType<string>>("orders.cancel"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddMessageType_RejectsSameAndCrossTypeDuplicateSubjectsWithoutChangingServices(bool crossType)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddMessageType<string>(Subject);
        var before = services.ToArray();

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (crossType)
            {
                services.AddMessageType<int>(Subject);
            }
            else
            {
                services.AddMessageType<string>(Subject);
            }
        });

        AssertUnchangedServices(before, services);
        using var provider = services.BuildServiceProvider();
        var original = provider.GetRequiredKeyedService<MessageType<string>>(Subject);
        Assert.Equal(Subject, original.Subject);
        var envelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), Subject, "original binding");
        Assert.Equal("original binding", original.Decode(envelope));
        Assert.Null(provider.GetKeyedService<MessageType<int>>(Subject));
    }

    [Fact]
    public void AddMessageType_AllowsCaseDistinctSubjects()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddMessageType<string>(Subject).AddMessageType<string>("Orders.Reserve");
        using var provider = services.BuildServiceProvider();
        var lower = provider.GetRequiredKeyedService<MessageType<string>>(Subject);
        var upper = provider.GetRequiredKeyedService<MessageType<string>>("Orders.Reserve");

        Assert.Equal(Subject, lower.Subject);
        Assert.Equal("Orders.Reserve", upper.Subject);
        Assert.NotSame(lower, upper);
        var lowerEnvelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), Subject, "lower");
        var upperEnvelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), "Orders.Reserve", "upper");
        Assert.Equal("lower", lower.Decode(lowerEnvelope));
        Assert.Equal("upper", upper.Decode(upperEnvelope));
        Assert.Throws<ArgumentException>(() => lower.Decode(upperEnvelope));
    }

    [Theory]
    [InlineData("receiver")]
    [InlineData("messageId")]
    [InlineData("key-ascii-bytes")]
    [InlineData("key-multibyte-bytes")]
    public void MessageType_CreateRejectsInvalidIdentitiesBeforeCodecWrite(string invalidIdentity)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new MessageType<string>(Subject, serializer);
        var receiver = invalidIdentity == "receiver" ? default : Receiver;
        var key = invalidIdentity switch
        {
            "messageId" => default,
            "key-ascii-bytes" => MessageId.Create(new string('k', 1025)),
            "key-multibyte-bytes" => MessageId.Create(new string('\u20ac', 341) + "ab"),
            _ => MessageKey
        };
        OutboxMessage? result = null;
        var exception = Assert.Throws<ArgumentException>(() =>
        {
            result = messageType.Create(key, receiver, "must not encode");
        });
        Assert.Equal(invalidIdentity switch
        {
            "receiver" => "receiverId",
            _ => "messageId"
        }, exception.ParamName);
        Assert.Null(result);
        Assert.Equal(0, probe.WriteCount);
    }

    [Fact]
    public void MessageType_CreateUsesExplicitIdentitiesAndSuppliedHierarchicalKey()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var messageType = new MessageType<string>(Subject, serializer);
        var key = MessageId.Create("orders", "42", "reserve");
        var first = messageType.Create(key, Receiver, "reserve first");
        var otherReceiver = GrainId.Create("receiver", "other-warehouse");
        var second = messageType.Create(key, otherReceiver, "reserve second");

        AssertEnvelopeIdentity(first, key, Receiver, Subject);
        AssertEnvelopeIdentity(second, key, otherReceiver, Subject);
        Assert.Equal("orders/42/reserve", first.Envelope.MessageId.ToString());
        Assert.Equal("orders/42/reserve", second.Envelope.MessageId.ToString());
        Assert.Equal(3, first.Envelope.MessageId.Key.CountSegments());
        Assert.Equal(3, second.Envelope.MessageId.Key.CountSegments());
        Assert.Equal("reserve first", serializer.Deserialize(first.Envelope.Payload));
        Assert.Equal("reserve second", serializer.Deserialize(second.Envelope.Payload));
    }

    [Theory]
    [InlineData("segments")]
    [InlineData("ascii-bytes")]
    [InlineData("multibyte-bytes")]
    public void MessageType_CreateAcceptsMessageKeyAtAdmissionLimits(string variation)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new MessageType<string>(Subject, serializer);
        var key = variation switch
        {
            "segments" => MessageId.Create(Enumerable.Repeat("s", 32).ToArray()),
            "ascii-bytes" => MessageId.Create(new string('k', 1024)),
            "multibyte-bytes" => MessageId.Create(new string('\u20ac', 341) + "a"),
            _ => throw new ArgumentOutOfRangeException(nameof(variation))
        };

        var envelope = messageType.Create(key, Receiver, "admitted boundary");

        Assert.Equal(1, probe.WriteCount);
        AssertEnvelopeIdentity(envelope, key, Receiver, Subject);
        if (variation == "segments")
        {
            Assert.Equal(32, envelope.Envelope.MessageId.Key.CountSegments());
        }
        else
        {
            Assert.Equal(1024, Encoding.UTF8.GetByteCount(envelope.Envelope.MessageId.ToString()));
        }
        Assert.Equal("admitted boundary", services.GetRequiredService<Serializer<string>>().Deserialize(envelope.Envelope.Payload));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("empty-string")]
    [InlineData("int")]
    [InlineData("zero-int")]
    [InlineData("negative-int")]
    [InlineData("bytes")]
    [InlineData("empty-bytes")]
    public void MessageType_CreateMatchesDirectSerializerWireFormat(string contract)
    {
        using var services = CreateServices();
        using var receivingServices = CreateServices();
        switch (contract)
        {
            case "string":
                AssertCreateWire(services, receivingServices, "reserve €42 / 東京");
                break;
            case "empty-string":
                AssertCreateWire(services, receivingServices, "");
                break;
            case "int":
                AssertCreateWire(services, receivingServices, 42_017);
                break;
            case "zero-int":
                AssertCreateWire(services, receivingServices, 0);
                break;
            case "negative-int":
                AssertCreateWire(services, receivingServices, -709);
                break;
            case "bytes":
                AssertCreateWire(services, receivingServices, new byte[] { 0, 255, 128, 17, 42 });
                break;
            case "empty-bytes":
                AssertCreateWire(services, receivingServices, Array.Empty<byte>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contract));
        }

    }

    [Fact]
    public void MessageType_CreateAllocatesIndependentArraysAcrossSubsequentCalls()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var messageType = new MessageType<byte[]>(Subject, serializer);
        var firstBody = new byte[] { 9, 8, 7, 255 };
        var largeBody = Enumerable.Range(0, 1_048_576).Select(i => (byte)(i * 31 + 7)).ToArray();
        var lastBody = new byte[] { 42, 0, 128, 255, 19, 21 };
        var largeWire = serializer.SerializeToArray(largeBody);
        var lastWire = serializer.SerializeToArray(lastBody);
        var first = messageType.Create(MessageKey, Receiver, firstBody);
        var repeated = messageType.Create(MessageKey, Receiver, firstBody);
        var large = messageType.Create(MessageKey, Receiver, largeBody);
        var last = messageType.Create(MessageKey, Receiver, lastBody);

        Assert.NotNull(first.Envelope.Payload);
        Assert.NotSame(firstBody, first.Envelope.Payload);
        Assert.NotSame(first.Envelope.Payload, repeated.Envelope.Payload);
        Assert.NotSame(first.Envelope.Payload, large.Envelope.Payload);
        Assert.NotSame(large.Envelope.Payload, last.Envelope.Payload);
        Assert.Equal(serializer.SerializeToArray(firstBody), first.Envelope.Payload.ToArray());
        Assert.Equal(first.Envelope.Payload.ToArray(), repeated.Envelope.Payload.ToArray());
        firstBody[0] = 99;
        Assert.Equal(new byte[] { 9, 8, 7, 255 }, serializer.Deserialize(first.Envelope.Payload));
        Assert.Equal(new byte[] { 9, 8, 7, 255 }, serializer.Deserialize(repeated.Envelope.Payload));
        Assert.Equal(largeWire, large.Envelope.Payload.ToArray());
        Assert.Equal(lastWire, last.Envelope.Payload.ToArray());
        Assert.Equal(largeBody, serializer.Deserialize(large.Envelope.Payload));
        Assert.Equal(lastBody, serializer.Deserialize(last.Envelope.Payload));
        AssertEnvelopeIdentity(large, MessageKey, Receiver, Subject);
        AssertEnvelopeIdentity(last, MessageKey, Receiver, Subject);
    }

    [Fact]
    public void MessageType_EncodingFailurePreservesOutstandingBytesAndPristineRetry()
    {
        using var services = CreateServices();
        var pristine = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new MessageType<string>(Subject, serializer);
        var outstanding = messageType.Create(MessageKey, Receiver, "outstanding € order");
        var outstandingWire = pristine.SerializeToArray("outstanding € order");
        var sentinel = new InvalidDataException("committed partial codec failure");
        probe.NextWriteFailure = sentinel;
        OutboxMessage? failedResult = null;
        var exception = Assert.Throws<InvalidDataException>(() =>
        {
            failedResult = messageType.Create(MessageKey, Receiver, "failing preparation");
        });
        Assert.Same(sentinel, exception);
        Assert.Null(failedResult);
        Assert.Equal(2, probe.WriteCount);
        Assert.Equal(1, probe.CommittedFailureCount);
        Assert.Equal((byte)0x7e, probe.CommittedFailureByte);

        var recovered = messageType.Create(MessageKey, Receiver, "recovered 東京 reserve");
        Assert.Equal(3, probe.WriteCount);
        Assert.Equal(pristine.SerializeToArray("recovered 東京 reserve"), recovered.Envelope.Payload.ToArray());
        Assert.Equal("recovered 東京 reserve", pristine.Deserialize(recovered.Envelope.Payload));
        Assert.Equal(outstandingWire, outstanding.Envelope.Payload.ToArray());
        Assert.Equal("outstanding € order", pristine.Deserialize(outstanding.Envelope.Payload));
        Assert.NotSame(outstanding.Envelope.Payload, recovered.Envelope.Payload);
        AssertEnvelopeIdentity(recovered, MessageKey, Receiver, Subject);
    }

    [Fact]
    public void MessageType_EnvelopeCopySharesStableArrayAcrossSubsequentCreate()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new MessageType<string>(Subject, serializer);
        var original = messageType.Create(MessageKey, Receiver, "surviving reserve");
        var surviving = original;
        Assert.Same(original.Envelope.Payload, surviving.Envelope.Payload);
        var expected = original.Envelope.Payload.ToArray();
        original = default;
        var subsequent = messageType.Create(new MessageId(MessageKey.Key.CreateChildKey("next")), Receiver, "new reserve");

        Assert.Equal(2, probe.WriteCount);
        Assert.NotSame(surviving.Envelope.Payload, subsequent.Envelope.Payload);
        Assert.Equal(expected, surviving.Envelope.Payload.ToArray());
        Assert.Equal(MessageKey, surviving.Envelope.MessageId);
        Assert.Equal(new MessageId(MessageKey.Key.CreateChildKey("next")), subsequent.Envelope.MessageId);
        Assert.Equal("surviving reserve", services.GetRequiredService<Serializer<string>>().Deserialize(surviving.Envelope.Payload));
        Assert.Equal("new reserve", services.GetRequiredService<Serializer<string>>().Deserialize(subsequent.Envelope.Payload));
    }

    [Fact]
    public void MessageType_CreateRejectsNullBodyBeforeCodecWrite()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new MessageType<string>(Subject, serializer);

        var exception = Assert.Throws<ArgumentNullException>(
            () => messageType.Create(MessageKey, Receiver, null!));

        Assert.Equal("body", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        var control = messageType.Create(MessageKey, Receiver, "nonnull recovery");
        Assert.Equal(1, probe.WriteCount);
        Assert.Equal("nonnull recovery", services.GetRequiredService<Serializer<string>>().Deserialize(control.Envelope.Payload));
    }

    [Fact]
    public void MessageType_CreateLeavesResourceBodyAndDecodedValueApplicationOwned()
    {
        using var services = CreateServices();
        var codec = new ResourceCodec();
        var serializer = new Serializer<OwnedResource>(codec, services.GetRequiredService<SerializerSessionPool>());
        var type = new MessageType<OwnedResource>(Subject, serializer);
        using var body = new OwnedResource("resource €42");
        var original = type.Create(MessageKey, Receiver, body);
        var retained = original;
        Assert.Same(original.Envelope.Payload, retained.Envelope.Payload);

        Assert.Equal(0, body.DisposeCount);
        Assert.Equal(MessageKey, retained.Envelope.MessageId);
        Assert.Equal(services.GetRequiredService<Serializer<string>>().SerializeToArray(body.Name), retained.Envelope.Payload.ToArray());
        using var decoded = type.Decode(retained.Envelope);
        Assert.NotSame(body, decoded);
        Assert.Same(codec.LastDecoded, decoded);
        Assert.Equal("resource €42", decoded.Name);
        Assert.Equal(0, decoded.DisposeCount);
        Assert.Equal(1, codec.ReadCount);
    }

    [Fact]
    public void InboxDispatcher_RegisterRejectsNullMessageType()
    {
        var dispatcher = new TestInbox();
        var calls = 0;
        var exception = Assert.Throws<ArgumentNullException>(() => dispatcher.Register<string>(null!, (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        }));

        Assert.Equal("messageType", exception.ParamName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void InboxDispatcher_RegisterRejectsNullHandler()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new MessageType<string>(Subject, serializer);
        var dispatcher = new TestInbox();

        Func<string, IInboxHandlerContext, CancellationToken, ValueTask>? handler = null;
        var exception = Assert.Throws<ArgumentNullException>(() => dispatcher.Register(messageType, handler!));

        Assert.Equal("handler", exception.ParamName);
        Assert.Equal(0, probe.ReadCount);
        dispatcher.Register(messageType, (_, _, _) => ValueTask.CompletedTask);
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncRoutesMultipleExactSubjectsAndPayloadTypes()
    {
        using var services = CreateServices();
        var strings = services.GetRequiredService<Serializer<string>>();
        var integers = services.GetRequiredService<Serializer<int>>();
        var dispatcher = new TestInbox();
        var lowerValues = new List<string>();
        var upperValues = new List<string>();
        var integerValues = new List<int>();
        dispatcher.Register(new MessageType<string>(Subject, strings), (body, _, _) =>
        {
            lowerValues.Add(body);
            return ValueTask.CompletedTask;
        });
        dispatcher.Register(new MessageType<string>("Orders.Reserve", strings), (body, _, _) =>
        {
            upperValues.Add(body);
            return ValueTask.CompletedTask;
        });
        dispatcher.Register(new MessageType<int>("orders.quantity", integers), (body, _, _) =>
        {
            integerValues.Add(body);
            return ValueTask.CompletedTask;
        });
        var lowerEnvelope = DirectEnvelope(strings, Subject, "lower €42");
        var upperEnvelope = DirectEnvelope(strings, "Orders.Reserve", "upper 東京");
        var integerEnvelope = DirectEnvelope(integers, "orders.quantity", 42_017);
        var lowerContext = new CountingInboxHandlerContext(lowerEnvelope);
        var upperContext = new CountingInboxHandlerContext(upperEnvelope);
        var integerContext = new CountingInboxHandlerContext(integerEnvelope);

        await dispatcher.HandleAsync(upperContext, CancellationToken.None);
        Assert.Equal(new[] { "upper 東京" }, upperValues);
        Assert.Empty(lowerValues);
        Assert.Empty(integerValues);
        await dispatcher.HandleAsync(integerContext, CancellationToken.None);
        Assert.Equal(new[] { 42_017 }, integerValues);
        Assert.Equal(new[] { "upper 東京" }, upperValues);
        Assert.Empty(lowerValues);
        await dispatcher.HandleAsync(lowerContext, CancellationToken.None);
        Assert.Equal(new[] { "lower €42" }, lowerValues);
        Assert.Equal(new[] { "upper 東京" }, upperValues);
        Assert.Equal(new[] { 42_017 }, integerValues);
        Assert.Equal(0, lowerContext.CompletionCount);
        Assert.Equal(0, upperContext.CompletionCount);
        Assert.Equal(0, integerContext.CompletionCount);
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncRejectsUnknownSubjectBeforeDecodeOrHandler()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        probe.ReadFailure = new InvalidDataException("unknown subject must not decode");
        var dispatcher = new TestInbox();
        var calls = 0;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        var direct = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(direct, "orders.unknown", "valid reserve €42");
        var context = new CountingInboxHandlerContext(envelope);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("valid reserve €42", direct.Deserialize(envelope.Payload));
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncRejectsMalformedKnownPayloadBeforeHandler()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var wire = serializer.SerializeToArray("nonempty truncated €42");
        var envelope = WireEnvelope(Subject, wire[..^1]);
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        var calls = 0;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        // StringCodec.ReadRaw -> Reader.EnsureAvailable rejects the declared
        // length exceeding remaining bytes, independently of subject routing.
        Assert.Throws<IndexOutOfRangeException>(() => serializer.Deserialize(envelope.Payload));

        await Assert.ThrowsAsync<IndexOutOfRangeException>(
            async () => await dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Equal(Subject, GetSubject(envelope));
        Assert.Equal(wire[..^1], envelope.Payload.ToArray());
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncPassesExactContextAndCancellationToken()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "context €42");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new TestInbox();
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        CancellationToken capturedToken = default;
        var calls = 0;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (body, caller, token) =>
        {
            capturedBody = body;
            capturedContext = caller;
            capturedToken = token;
            calls++;
            return ValueTask.CompletedTask;
        });

        await dispatcher.HandleAsync(context, cancellation.Token);

        Assert.Equal("context €42", capturedBody);
        Assert.Same(context, capturedContext);
        Assert.Equal(cancellation.Token, capturedToken);
        Assert.True(capturedToken.CanBeCanceled);
        Assert.False(capturedToken.IsCancellationRequested);
        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncPropagatesSynchronousValueTaskCompletionWithoutAutomaticComplete()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "synchronous reserve");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        var calls = 0;
        string? captured = null;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (body, _, _) =>
        {
            calls++;
            captured = body;
            return ValueTask.CompletedTask;
        });

        var dispatch = dispatcher.HandleAsync(context, CancellationToken.None);
        Assert.True(dispatch.IsCompletedSuccessfully);
        await dispatch;

        Assert.Equal("synchronous reserve", captured);
        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncPropagatesSynchronousFaultedValueTask()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "faulted task reserve");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        var sentinel = new InvalidDataException("synchronous faulted ValueTask");
        var calls = 0;
        string? captured = null;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (body, _, _) =>
        {
            calls++;
            captured = body;
            return ValueTask.FromException(sentinel);
        });

        var dispatch = dispatcher.HandleAsync(context, CancellationToken.None);
        Assert.True(dispatch.IsCompleted);
        Assert.False(dispatch.IsCompletedSuccessfully);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () => await dispatch);

        Assert.Same(sentinel, exception);
        Assert.Equal("faulted task reserve", captured);
        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncPropagatesSynchronousDelegateException()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "throwing delegate reserve");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        var sentinel = new InvalidDataException("delegate throws before returning");
        var calls = 0;
        string? captured = null;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (body, _, _) =>
        {
            calls++;
            captured = body;
            throw sentinel;
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Same(sentinel, exception);
        Assert.Equal("throwing delegate reserve", captured);
        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncPropagatesDeferredValueTaskCompletion()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "deferred €42");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new TestInbox();
        var calls = 0;
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        CancellationToken capturedToken = default;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (body, caller, token) =>
        {
            calls++;
            capturedBody = body;
            capturedContext = caller;
            capturedToken = token;
            return new ValueTask(completion.Task);
        });

        var dispatch = dispatcher.HandleAsync(context, cancellation.Token);
        // Record the controlled incomplete state, then release and observe once
        // before assertions, so even assertion failure cannot strand a dispatch.
        var completedBeforeRelease = dispatch.IsCompleted;
        var completionsBeforeRelease = context.CompletionCount;
        completion.SetResult();
        await dispatch;

        Assert.False(completedBeforeRelease);
        Assert.Equal(0, completionsBeforeRelease);
        Assert.Equal("deferred €42", capturedBody);
        Assert.Same(context, capturedContext);
        Assert.Equal(cancellation.Token, capturedToken);
        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("deferred €42", serializer.Deserialize(envelope.Payload));
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncPropagatesDeferredValueTaskFault()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "deferred fault €42");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinel = new InvalidDataException("controlled deferred fault");
        var dispatcher = new TestInbox();
        var calls = 0;
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        CancellationToken capturedToken = default;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (body, caller, token) =>
        {
            calls++;
            capturedBody = body;
            capturedContext = caller;
            capturedToken = token;
            return new ValueTask(completion.Task);
        });

        var dispatch = dispatcher.HandleAsync(context, cancellation.Token);
        var completedBeforeRelease = dispatch.IsCompleted;
        var completionsBeforeRelease = context.CompletionCount;
        completion.SetException(sentinel);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () => await dispatch);

        Assert.False(completedBeforeRelease);
        Assert.Equal(0, completionsBeforeRelease);
        Assert.Same(sentinel, exception);
        Assert.Equal("deferred fault €42", capturedBody);
        Assert.Same(context, capturedContext);
        Assert.Equal(cancellation.Token, capturedToken);
        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("deferred fault €42", serializer.Deserialize(envelope.Payload));
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncStagesOnlyExplicitDelegateCompletion()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "explicit completion");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        var calls = 0;
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (body, caller, _) =>
        {
            calls++;
            capturedBody = body;
            capturedContext = caller;
            caller.Complete();
            return ValueTask.CompletedTask;
        });

        await dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.Equal("explicit completion", capturedBody);
        Assert.Same(context, capturedContext);
        Assert.Equal(1, calls);
        Assert.Equal(1, context.CompletionCount);
        Assert.Equal("explicit completion", serializer.Deserialize(envelope.Payload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboxDispatcher_HandleAsyncHonorsPreCanceledTokenWithoutDecodeHandlerOrComplete(bool unknownSubject)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        probe.ReadFailure = new InvalidDataException("canceled attempts must not decode");
        var direct = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(direct, unknownSubject ? "orders.unknown" : Subject, "valid canceled reserve");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var dispatcher = new TestInbox();
        dispatcher.Register(new MessageType<string>(Subject, serializer), (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await dispatcher.HandleAsync(context, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("valid canceled reserve", direct.Deserialize(envelope.Payload));
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncLeavesDecodedResourceApplicationOwned()
    {
        using var services = CreateServices();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var codec = new ResourceCodec();
        var serializer = new Serializer<OwnedResource>(codec, sessions);
        using var original = new OwnedResource("owned reserve €42");
        var envelope = DirectEnvelope(serializer, Subject, original);
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        OwnedResource? captured = null;
        var calls = 0;
        dispatcher.Register(new MessageType<OwnedResource>(Subject, serializer), (body, _, _) =>
        {
            calls++;
            captured = body;
            return ValueTask.CompletedTask;
        });
        try
        {
            await dispatcher.HandleAsync(context, CancellationToken.None);

            var decoded = Assert.IsType<OwnedResource>(captured);
            Assert.Same(codec.LastDecoded, decoded);
            Assert.NotSame(original, decoded);
            Assert.Equal("owned reserve €42", decoded.Name);
            Assert.Equal(1, calls);
            Assert.Equal(1, codec.ReadCount);
            Assert.Equal(0, context.CompletionCount);
            Assert.Equal(0, original.DisposeCount);
            Assert.Equal(0, decoded.DisposeCount);
            envelope = default;
            Assert.Equal("owned reserve €42", decoded.Name);
            Assert.Equal(0, decoded.DisposeCount);
            decoded.Dispose();
            Assert.Equal(1, decoded.DisposeCount);
            Assert.Equal(0, original.DisposeCount);
        }
        finally
        {
            if (codec.LastDecoded is { DisposeCount: 0 } resource)
            {
                resource.Dispose();
            }
        }
    }

    [Fact]
    public async Task InboxDispatcher_CancellationDuringDecodeDeliversDecodedResourceToHandler()
    {
        using var services = CreateServices();
        var codec = new ResourceCodec();
        var serializer = new Serializer<OwnedResource>(codec, services.GetRequiredService<SerializerSessionPool>());
        using var original = new OwnedResource("canceled resource €42");
        var envelope = DirectEnvelope(serializer, Subject, original);
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        codec.OnRead = cancellation.Cancel;
        OwnedResource? captured = null;
        var observedToken = CancellationToken.None;
        var dispatcher = new TestInbox();
        dispatcher.Register(new MessageType<OwnedResource>(Subject, serializer), (body, _, token) =>
        {
            captured = body;
            observedToken = token;
            return ValueTask.CompletedTask;
        });
        try
        {
            await dispatcher.HandleAsync(context, cancellation.Token);

            var decoded = Assert.IsType<OwnedResource>(captured);
            Assert.Same(codec.LastDecoded, decoded);
            Assert.Equal(1, codec.ReadCount);
            Assert.Equal(cancellation.Token, observedToken);
            Assert.True(observedToken.IsCancellationRequested);
            Assert.Equal(0, decoded.DisposeCount);
            Assert.Equal(0, context.CompletionCount);
        }
        finally
        {
            codec.LastDecoded?.Dispose();
        }
    }

    [Fact]
    public async Task InboxDispatcher_HandleAsyncRejectsSerializedNullBeforeHandler()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, null!);
        var expectedWire = serializer.SerializeToArray(null);
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        var calls = 0;
        dispatcher.Register(new MessageType<string>(Subject, serializer), (_, caller, _) =>
        {
            calls++;
            caller.Complete();
            return ValueTask.CompletedTask;
        });
        Assert.Null(serializer.Deserialize(envelope.Payload));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Equal("envelope", exception.ParamName);
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Null(serializer.Deserialize(envelope.Payload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterAction_InvokesDecodedBodySynchronouslyWithExplicitCompletion(bool complete)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var envelope = DirectEnvelope(serializer, Subject, "synchronous");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new TestInbox();
        var calls = 0;
        dispatcher.Register(type, (body, caller) =>
        {
            Assert.Equal("synchronous", body);
            Assert.Same(context, caller);
            calls++;
            if (complete) caller.Complete();
        });

        var outcome = dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.True(outcome.IsCompletedSuccessfully);
        Assert.Equal(1, calls);
        Assert.Equal(complete ? 1 : 0, context.CompletionCount);
        await outcome;
    }

    [Fact]
    public void RegisterAction_RejectsNullArguments()
    {
        using var services = CreateServices();
        var type = new MessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var dispatcher = new TestInbox();
        var missingType = Assert.Throws<ArgumentNullException>(() =>
            dispatcher.Register<string>(null!, (_, _) => { }));
        Action<string, IInboxHandlerContext>? handler = null;
        var missingHandler = Assert.Throws<ArgumentNullException>(() => dispatcher.Register(type, handler!));

        Assert.Equal("messageType", missingType.ParamName);
        Assert.Equal("handler", missingHandler.ParamName);
        dispatcher.Register(type, (_, _) => { });
    }

    [Fact]
    public void RegisterAction_PropagatesOriginalDelegateFailure()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var envelope = DirectEnvelope(serializer, Subject, "failure");
        var context = new CountingInboxHandlerContext(envelope);
        var sentinel = new IOException("synchronous handler failed");
        var dispatcher = new TestInbox().Register(
            new MessageType<string>(Subject, serializer), (_, _) => throw sentinel);

        var exception = Assert.Throws<IOException>(() => dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Same(sentinel, exception);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public void RegisterAction_CancellationBeforeEntryLeavesHandlerAndCompletionUntouched()
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var envelope = DirectEnvelope(direct, Subject, "cancellation");
        var context = new CountingInboxHandlerContext(envelope);
        var calls = 0;
        var dispatcher = new TestInbox().Register(
            new MessageType<string>(Subject, serializer), (_, _) => calls++);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            dispatcher.HandleAsync(context, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task RegisterAction_DuplicateSubjectDispatchesToFirstRegistration()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var originalCalls = 0;
        var replacementCalls = 0;
        var dispatcher = new TestInbox().Register(type, (_, _) => originalCalls++);
        dispatcher.Register(type, (_, _) => replacementCalls++);
        var envelope = DirectEnvelope(serializer, Subject, "original");
        var context = new CountingInboxHandlerContext(envelope);

        await dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.Equal(1, originalCalls);
        Assert.Equal(0, replacementCalls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterStateAction_StaticDelegateReceivesExactReferenceStateBodyAndContext(bool complete)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var envelope = type.Create(MessageKey, Receiver, "stateful €42");
        var context = new CountingInboxHandlerContext(envelope);
        var state = new HandlerState { Complete = complete };
        Action<string, HandlerState, IInboxHandlerContext> handler = ObserveState;
        Assert.Null(handler.Target); // Named static method, not a captured test closure.
        var dispatcher = new TestInbox();
        dispatcher.Register(type, state, handler);

        var outcome = dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.True(outcome.IsCompletedSuccessfully);
        await outcome;
        Assert.Equal("stateful €42", state.Body);
        Assert.Same(context, state.Context);
        Assert.Same(state, state.Argument);
        Assert.Equal(1, state.Calls);
        Assert.Equal(complete ? 1 : 0, context.CompletionCount);
    }

    [Fact]
    public async Task RegisterState_MixedStaticReferenceAndStructArgumentsRouteOrdinalSubjects()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var syncType = new MessageType<string>(Subject, serializer);
        var asyncType = new MessageType<string>("Orders.Reserve", serializer);
        var syncState = new HandlerState { Complete = true };
        var asyncState = new HandlerState();
        var argument = new StructHandlerState(asyncState, 731);
        Action<string, HandlerState, IInboxHandlerContext> sync = ObserveState;
        Func<string, StructHandlerState, IInboxHandlerContext, CancellationToken, ValueTask> asyncHandler = ObserveStructState;
        Assert.Null(sync.Target);
        Assert.Null(asyncHandler.Target);
        var dispatcher = new TestInbox().Register(syncType, syncState, sync).Register(asyncType, argument, asyncHandler);
        var syncEnvelope = syncType.Create(MessageKey, Receiver, "lower");
        var asyncEnvelope = asyncType.Create(MessageKey, Receiver, "upper");
        var syncContext = new CountingInboxHandlerContext(syncEnvelope);
        var asyncContext = new CountingInboxHandlerContext(asyncEnvelope);
        using var cancellation = new CancellationTokenSource();

        await dispatcher.HandleAsync(syncContext, CancellationToken.None);
        await dispatcher.HandleAsync(asyncContext, cancellation.Token);

        Assert.Equal("lower", syncState.Body);
        Assert.Equal("upper", asyncState.Body);
        Assert.Same(syncContext, syncState.Context);
        Assert.Same(asyncContext, asyncState.Context);
        Assert.Equal(731, asyncState.Marker);
        Assert.Equal(cancellation.Token, asyncState.Token);
        Assert.Equal(1, syncState.Calls);
        Assert.Equal(1, asyncState.Calls);
        Assert.Equal(1, syncContext.CompletionCount);
        Assert.Equal(0, asyncContext.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterState_StaticStructArgumentIsStoredByValueAndInvokedExactly(bool asynchronous)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var envelope = type.Create(MessageKey, Receiver, "struct state");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var observation = new HandlerState { Complete = true };
        var argument = new StructHandlerState(observation, 731);
        var dispatcher = new TestInbox();
        if (asynchronous)
        {
            Func<string, StructHandlerState, IInboxHandlerContext, CancellationToken, ValueTask> handler = ObserveStructState;
            Assert.Null(handler.Target);
            dispatcher.Register(type, argument, handler);
        }
        else
        {
            Action<string, StructHandlerState, IInboxHandlerContext> handler = ObserveStructAction;
            Assert.Null(handler.Target);
            dispatcher.Register(type, argument, handler);
        }
        argument = argument with { Marker = 999 };

        await dispatcher.HandleAsync(context, cancellation.Token);

        Assert.Equal(999, argument.Marker);
        Assert.Equal(731, observation.Marker);
        Assert.Equal("struct state", observation.Body);
        Assert.Same(context, observation.Context);
        Assert.Same(observation, observation.Argument);
        Assert.Equal(asynchronous ? cancellation.Token : default, observation.Token);
        Assert.Equal(1, observation.Calls);
        Assert.Equal(1, context.CompletionCount);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task RegisterStateAsync_ReturnsActualDeferredValueTaskAndExactOutcome(string outcomeKind)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var envelope = type.Create(MessageKey, Receiver, "deferred state");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new HandlerState { Outcome = new ValueTask(gate.Task) };
        Func<string, HandlerState, IInboxHandlerContext, CancellationToken, ValueTask> handler = ObserveAsyncState;
        Assert.Null(handler.Target);
        var dispatcher = new TestInbox().Register(type, state, handler);

        var outcome = dispatcher.HandleAsync(context, cancellation.Token);

        Assert.Equal(state.Outcome, outcome); // No async adapter replacing the actual ValueTask.
        Assert.False(outcome.IsCompleted);
        Assert.Equal("deferred state", state.Body);
        Assert.Same(state, state.Argument);
        Assert.Same(context, state.Context);
        Assert.Equal(cancellation.Token, state.Token);
        Assert.Equal(1, state.Calls);
        Assert.Equal(0, context.CompletionCount);
        var sentinel = new IOException("stateful deferred sentinel");
        if (outcomeKind == "fault")
        {
            gate.SetException(sentinel);
            Assert.Same(sentinel, await Assert.ThrowsAsync<IOException>(async () => await outcome));
        }
        else if (outcomeKind == "cancel")
        {
            cancellation.Cancel();
            gate.SetCanceled(cancellation.Token);
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await outcome);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        else
        {
            // Cancellation after a delegate's successful work must not change its outcome.
            cancellation.Cancel();
            gate.SetResult();
            await outcome;
            Assert.True(outcome.IsCompletedSuccessfully);
        }
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal(1, state.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterState_PropagatesOriginalSynchronousFailureWithoutCompleting(bool faultedValueTask)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var envelope = type.Create(MessageKey, Receiver, "sentinel");
        var context = new CountingInboxHandlerContext(envelope);
        var sentinel = new IOException("stateful sync sentinel");
        var state = new HandlerState();
        var dispatcher = new TestInbox();
        if (faultedValueTask)
        {
            state.Outcome = ValueTask.FromException(sentinel);
            dispatcher.Register(type, state, ObserveAsyncState);
            var outcome = dispatcher.HandleAsync(context, CancellationToken.None);
            Assert.Equal(state.Outcome, outcome);
            Assert.Same(sentinel, await Assert.ThrowsAsync<IOException>(async () => await outcome));
        }
        else
        {
            state.Failure = sentinel;
            dispatcher.Register(type, state, ObserveState);
            Assert.Same(sentinel, Assert.Throws<IOException>(() => dispatcher.HandleAsync(context, CancellationToken.None)));
        }
        Assert.Equal("sentinel", state.Body);
        Assert.Same(context, state.Context);
        Assert.Equal(1, state.Calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterState_CancellationBeforeDecodePreventsAllDelegateEffects(bool asynchronous)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var envelope = DirectEnvelope(direct, Subject, "canceled state");
        var context = new CountingInboxHandlerContext(envelope);
        var state = new HandlerState { Complete = true };
        var type = new MessageType<string>(Subject, serializer);
        var dispatcher = new TestInbox();
        if (asynchronous) dispatcher.Register(type, state, ObserveAsyncState);
        else dispatcher.Register(type, state, ObserveState);

        var exception = Assert.Throws<OperationCanceledException>(() => dispatcher.HandleAsync(context, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(0, state.Calls);
        Assert.Null(state.Body);
        Assert.Null(state.Context);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterState_CancellationInsideSuccessfulDelegatePreservesExplicitCompletion(bool asynchronous)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var envelope = type.Create(MessageKey, Receiver, "completed despite cancel");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var state = new HandlerState { Complete = true, CancelOnInvoke = cancellation };
        var dispatcher = new TestInbox();
        if (asynchronous) dispatcher.Register(type, state, ObserveAsyncState);
        else dispatcher.Register(type, state, ObserveState);

        var outcome = dispatcher.HandleAsync(context, cancellation.Token);
        await outcome;

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(outcome.IsCompletedSuccessfully);
        Assert.Equal("completed despite cancel", state.Body);
        Assert.Same(context, state.Context);
        Assert.Equal(1, state.Calls);
        Assert.Equal(1, context.CompletionCount);
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "codec-failure")]
    [InlineData(true, "codec-failure")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    public void RegisterState_UnknownMalformedOrNullPayloadRejectsBeforeEffects(bool asynchronous, string variation)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var sentinel = new InvalidDataException("malformed stateful payload");
        if (variation == "codec-failure") probe.ReadFailure = sentinel;
        var wire = direct.SerializeToArray("nonempty truncated €42");
        var envelope = variation == "malformed"
            ? WireEnvelope(Subject, wire[..^1])
            : DirectEnvelope(direct, variation == "unknown" ? "Orders.Reserve" : Subject,
                variation == "null" ? null! : "payload");
        var context = new CountingInboxHandlerContext(envelope);
        var type = new MessageType<string>(Subject, serializer);
        var state = new HandlerState { Complete = true };
        var dispatcher = new TestInbox();
        if (asynchronous) dispatcher.Register(type, state, ObserveAsyncState);
        else dispatcher.Register(type, state, ObserveState);

        if (variation == "unknown")
            Assert.Throws<InvalidOperationException>(() => dispatcher.HandleAsync(context, CancellationToken.None));
        else if (variation == "codec-failure")
            Assert.Same(sentinel, Assert.Throws<InvalidDataException>(() => dispatcher.HandleAsync(context, CancellationToken.None)));
        else if (variation == "malformed")
        {
            Assert.Throws<IndexOutOfRangeException>(() => dispatcher.HandleAsync(context, CancellationToken.None));
            Assert.Equal(wire[..^1], envelope.Payload.ToArray());
        }
        else
            Assert.Equal("envelope", Assert.Throws<ArgumentException>(() =>
                dispatcher.HandleAsync(context, CancellationToken.None)).ParamName);

        Assert.Equal(variation == "unknown" ? 0 : 1, probe.ReadCount);
        Assert.Equal(0, state.Calls);
        Assert.Null(state.Context);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterState_DuplicateSameOrCrossTypeDispatchesToFirstRegistration(bool crossType)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var original = new HandlerState();
        var replacement = new HandlerState { Complete = true };
        var dispatcher = new TestInbox().Register(type, original, ObserveState);
        if (crossType)
            dispatcher.Register(
                new MessageType<int>(Subject, services.GetRequiredService<Serializer<int>>()),
                replacement, ObserveNumberState);
        else
            dispatcher.Register(type, replacement, ObserveAsyncState);
        var envelope = type.Create(MessageKey, Receiver, "original state");
        var context = new CountingInboxHandlerContext(envelope);

        await dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.Equal("original state", original.Body);
        Assert.Same(original, original.Argument);
        Assert.Equal(1, original.Calls);
        Assert.Equal(0, replacement.Calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterState_RejectsNullTypeAndDelegateWithoutReservingSubject(bool asynchronous)
    {
        using var services = CreateServices();
        var type = new MessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var state = new HandlerState();
        var dispatcher = new TestInbox();
        if (asynchronous)
        {
            Assert.Equal("messageType", Assert.Throws<ArgumentNullException>(() =>
                dispatcher.Register<string, HandlerState>(null!, state, ObserveAsyncState)).ParamName);
            Assert.Equal("handler", Assert.Throws<ArgumentNullException>(() => dispatcher.Register(type, state,
                (Func<string, HandlerState, IInboxHandlerContext, CancellationToken, ValueTask>)null!)).ParamName);
            dispatcher.Register(type, state, ObserveAsyncState);
        }
        else
        {
            Assert.Equal("messageType", Assert.Throws<ArgumentNullException>(() =>
                dispatcher.Register<string, HandlerState>(null!, state, ObserveState)).ParamName);
            Assert.Equal("handler", Assert.Throws<ArgumentNullException>(() => dispatcher.Register(type, state,
                (Action<string, HandlerState, IInboxHandlerContext>)null!)).ParamName);
            dispatcher.Register(type, state, ObserveState);
        }
        var envelope = type.Create(MessageKey, Receiver, "positive control");
        var context = new CountingInboxHandlerContext(envelope);
        Assert.True(dispatcher.HandleAsync(context, CancellationToken.None).IsCompletedSuccessfully);
        Assert.Equal("positive control", state.Body);
        Assert.Equal(1, state.Calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterState_StructArgumentDispatchDoesNotAddPerInvocationBoxingAllocations(bool asynchronous)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);
        var envelope = type.Create(MessageKey, Receiver, "allocation control");
        var context = new CountingInboxHandlerContext(envelope);
        var baselineState = new HandlerState();
        var structState = new HandlerState();
        var baseline = new TestInbox();
        var withStruct = new TestInbox();
        if (asynchronous)
        {
            baseline.Register(type, baselineState, ObserveAsyncState);
            withStruct.Register(type, new StructHandlerState(structState, 731), ObserveStructState);
        }
        else
        {
            baseline.Register(type, baselineState, ObserveState);
            withStruct.Register(type, new StructHandlerState(structState, 731), ObserveStructAction);
        }
        // Same serializer/body/context, warmed routes and session pool. No assertions,
        // async continuations, timing, or boxed arguments inside the measured loops.
        MeasureDispatchAllocations(baseline, context, 64);
        MeasureDispatchAllocations(withStruct, context, 64);
        var referenceBytes = MeasureDispatchAllocations(baseline, context, 256);
        var structBytes = MeasureDispatchAllocations(withStruct, context, 256);

        Assert.True(structBytes <= referenceBytes, $"Struct dispatch allocated {structBytes}, reference dispatch {referenceBytes}.");
        Assert.Equal(320, baselineState.Calls);
        Assert.Equal(320, structState.Calls);
        Assert.Equal(731, structState.Marker);
        Assert.Equal("allocation control", structState.Body);
        Assert.Same(context, structState.Context);
        Assert.Equal(0, context.CompletionCount);
    }

    private static ServiceProvider CreateServices() =>
        new ServiceCollection().AddSerializer().BuildServiceProvider();

    private static void AssertClosedSerializerFactory<T>(T body)
    {
        var services = new ServiceCollection();
        services.AddMessageType<T>(Subject).AddMessageType<T>("orders.cancel");
        var closed = Assert.Single(services, descriptor =>
            !descriptor.IsKeyedService && descriptor.ServiceType == typeof(Serializer<T>));
        Assert.Equal(ServiceLifetime.Singleton, closed.Lifetime);
        Assert.Null(closed.ImplementationType);
        Assert.Null(closed.ImplementationInstance);
        var factory = closed.ImplementationFactory;
        Assert.NotNull(factory);
        var bindings = services.Where(descriptor => descriptor.IsKeyedService).ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.All(bindings, descriptor => Assert.True(services.IndexOf(closed) < services.IndexOf(descriptor)));
        // Factory registration succeeds before serializer infrastructure exists:
        // it must be lazy and must use the closed contract, including struct T.
        services.AddSerializer();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var serializer = provider.GetRequiredService<Serializer<T>>();
        Assert.Same(serializer, scope.ServiceProvider.GetRequiredService<Serializer<T>>());
        var factoryProvider = Substitute.For<IServiceProvider>();
        factoryProvider.GetService(typeof(Serializer)).Returns(provider.GetRequiredService<Serializer>());
        factoryProvider.ClearReceivedCalls();
        var createdByFactory = Assert.IsType<Serializer<T>>(factory(factoryProvider));
        factoryProvider.Received(1).GetService(typeof(Serializer));
        factoryProvider.DidNotReceive().GetService(typeof(Serializer<T>));
        Assert.Equal(serializer.SerializeToArray(body), createdByFactory.SerializeToArray(body));
        var first = provider.GetRequiredKeyedService<MessageType<T>>(Subject);
        var second = provider.GetRequiredKeyedService<MessageType<T>>("orders.cancel");
        var envelope = first.Create(MessageKey, Receiver, body);
        var other = second.Create(MessageKey, Receiver, body);
        Assert.Equal(serializer.SerializeToArray(body), envelope.Envelope.Payload.ToArray());
        Assert.Equal(body, first.Decode(envelope.Envelope));
        Assert.Equal(body, second.Decode(other.Envelope));
        AssertEnvelopeIdentity(envelope, MessageKey, Receiver, Subject);
        AssertEnvelopeIdentity(other, MessageKey, Receiver, "orders.cancel");
        Assert.NotSame(first, second);
    }

    private static void AssertCustomClosedSerializerPreserved<T>(T body, bool factory)
    {
        using var codecServices = CreateServices();
        var (custom, probe) = CreateProbedSerializer<T>(codecServices);
        var services = new ServiceCollection();
        var factoryCalls = 0;
        if (factory)
            services.AddSingleton<Serializer<T>>(_ =>
            {
                factoryCalls++;
                return custom;
            });
        else services.AddSingleton(custom);
        var original = Assert.Single(services);

        services.AddMessageType<T>(Subject).AddMessageType<T>("orders.cancel");

        Assert.Same(original, Assert.Single(services, descriptor =>
            !descriptor.IsKeyedService && descriptor.ServiceType == typeof(Serializer<T>)));
        Assert.Equal(0, factoryCalls);
        // No untyped Serializer service is installed here. Replacing this closed
        // custom registration with the default factory would fail resolution.
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(custom, provider.GetRequiredService<Serializer<T>>());
        Assert.Same(custom, scope.ServiceProvider.GetRequiredService<Serializer<T>>());
        var first = provider.GetRequiredKeyedService<MessageType<T>>(Subject);
        var second = provider.GetRequiredKeyedService<MessageType<T>>("orders.cancel");
        var envelope = first.Create(MessageKey, Receiver, body);
        var other = second.Create(MessageKey, Receiver, body);
        Assert.Equal(2, probe.WriteCount);
        Assert.Equal(codecServices.GetRequiredService<Serializer<T>>().SerializeToArray(body), envelope.Envelope.Payload.ToArray());
        Assert.Equal(body, first.Decode(envelope.Envelope));
        Assert.Equal(body, second.Decode(other.Envelope));
        Assert.Equal(2, probe.ReadCount);
        Assert.Equal(factory ? 1 : 0, factoryCalls);
        AssertEnvelopeIdentity(envelope, MessageKey, Receiver, Subject);
        AssertEnvelopeIdentity(other, MessageKey, Receiver, "orders.cancel");
    }

    private static void ObserveState(string body, HandlerState argument, IInboxHandlerContext context)
    {
        argument.Calls++;
        argument.Body = body;
        argument.Context = context;
        argument.Argument = argument;
        if (argument.Failure is { } failure) throw failure;
        argument.CancelOnInvoke?.Cancel();
        if (argument.Complete) context.Complete();
    }

    private static ValueTask ObserveAsyncState(string body, HandlerState argument, IInboxHandlerContext context, CancellationToken token)
    {
        argument.Token = token;
        ObserveState(body, argument, context);
        return argument.Outcome;
    }

    private static ValueTask ObserveStructState(string body, StructHandlerState argument, IInboxHandlerContext context, CancellationToken token)
    {
        argument.Observation.Marker = argument.Marker;
        return ObserveAsyncState(body, argument.Observation, context, token);
    }

    private static void ObserveStructAction(string body, StructHandlerState argument, IInboxHandlerContext context)
    {
        argument.Observation.Marker = argument.Marker;
        ObserveState(body, argument.Observation, context);
    }

    private static ValueTask ObserveNumberState(int body, HandlerState argument, IInboxHandlerContext context, CancellationToken token)
    {
        argument.Marker = body;
        return ObserveAsyncState("number", argument, context, token);
    }

    private static long MeasureDispatchAllocations(IInbox dispatcher, IInboxHandlerContext context, int count)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++)
            dispatcher.HandleAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private readonly record struct StructHandlerState(HandlerState Observation, int Marker);

    private sealed class HandlerState
    {
        public int Calls { get; set; }
        public string? Body { get; set; }
        public object? Argument { get; set; }
        public IInboxHandlerContext? Context { get; set; }
        public CancellationToken Token { get; set; }
        public int Marker { get; set; }
        public bool Complete { get; set; }
        public CancellationTokenSource? CancelOnInvoke { get; set; }
        public Exception? Failure { get; set; }
        public ValueTask Outcome { get; set; }
    }

    private static string InvalidSubject(string variation) => variation switch
    {
        "empty" => "",
        "ascii" => new string('s', 257),
        "multibyte" => new string('\u20ac', 85) + "ab",
        _ => throw new ArgumentOutOfRangeException(nameof(variation))
    };

    private static string SubjectAtLimit(bool multibyte) =>
        multibyte ? new string('\u20ac', 85) + "a" : new string('s', 256);

    private static (Serializer<T> Serializer, ProbeCodec<T> Probe) CreateProbedSerializer<T>(IServiceProvider services)
    {
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var probe = new ProbeCodec<T>(sessions.CodecProvider.GetCodec<T>());
        return (new Serializer<T>(probe, sessions), probe);
    }

    private static Envelope DirectEnvelope<T>(Serializer<T> serializer, string subject, [AllowNull] T body) =>
        Envelope(subject, serializer.SerializeToArray(body));

    private static Envelope WireEnvelope(string subject, byte[] wire) =>
        Envelope(subject, wire);

    private static Envelope Envelope(string subject, byte[] payload)
    {
        return new Envelope
        {
            MessageId = MessageKey,
            Payload = payload,
            Subject = subject
        };
    }

    private static void AssertEnvelopeIdentity(
        OutboxMessage message, MessageId key, GrainId receiver, string subject)
    {
        Assert.Equal(key, message.Envelope.MessageId);
        Assert.Equal(receiver, message.ReceiverId);
        Assert.Equal(subject, GetSubject(message.Envelope));
    }

    private static string GetSubject(Envelope envelope)
    {
        return Assert.IsType<string>(envelope.Subject);
    }

    private static void AssertCreateWire<T>(
        IServiceProvider sendingServices, IServiceProvider receivingServices, T body)
    {
        var sending = sendingServices.GetRequiredService<Serializer<T>>();
        var receiving = receivingServices.GetRequiredService<Serializer<T>>();
        var messageType = new MessageType<T>(Subject, sending);
        var key = MessageKey;
        var expectedWire = sending.SerializeToArray(body);
        var envelope = messageType.Create(key, Receiver, body);
        Assert.Equal(expectedWire, envelope.Envelope.Payload.ToArray());
        Assert.Equal(body, receiving.Deserialize(envelope.Envelope.Payload));
        AssertEnvelopeIdentity(envelope, key, Receiver, Subject);
    }

    private static void AssertUnchangedServices(ServiceDescriptor[] before, IServiceCollection after)
    {
        Assert.Equal(before.Length, after.Count);
        for (var i = 0; i < before.Length; i++)
        {
            Assert.Same(before[i], after[i]);
        }
    }

    private sealed class CountingInboxHandlerContext(InboxMessage message) : IInboxHandlerContext
    {
        public CountingInboxHandlerContext(Envelope envelope) : this(new InboxMessage { Envelope = envelope }) { }
        public CountingInboxHandlerContext(OutboxMessage outgoing) : this(outgoing.ToInboxMessage()) { }

        public InboxMessage Message { get; } = message;
        public int CompletionCount { get; private set; }
        public void Complete() => CompletionCount++;
        public void Fail(string reason) => throw new InvalidOperationException("Unexpected dead letter.");
    }

    private sealed class ProbeCodec<T>(IFieldCodec<T> inner) : IFieldCodec<T>
    {
        public int WriteCount { get; private set; }
        public int ReadCount { get; private set; }
        public int CommittedFailureCount { get; private set; }
        public byte? CommittedFailureByte { get; private set; }
        public Exception? NextWriteFailure { get; set; }
        public Exception? ReadFailure { get; set; }
        public Action? OnRead { get; set; }

        public void WriteField<TBufferWriter>(
            ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] T value)
            where TBufferWriter : IBufferWriter<byte>
        {
            WriteCount++;
            if (NextWriteFailure is { } failure)
            {
                NextWriteFailure = null;
                writer.WriteByte(0x7e);
                writer.Commit();
                CommittedFailureByte = 0x7e;
                CommittedFailureCount++;
                throw failure;
            }

            inner.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }

        [return: MaybeNull]
        public T ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReadCount++;
            if (ReadFailure is { } failure) throw failure;
            var value = inner.ReadValue(ref reader, field);
            OnRead?.Invoke();
            return value;
        }
    }

    private sealed class OwnedResource(string name) : IDisposable
    {
        public string Name { get; } = name;
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class ResourceCodec : IFieldCodec<OwnedResource>
    {
        public OwnedResource? LastDecoded { get; private set; }
        public int ReadCount { get; private set; }
        public Action? OnRead { get; set; }

        public void WriteField<TBufferWriter>(
            ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType,
            [AllowNull] OwnedResource value) where TBufferWriter : IBufferWriter<byte>
        {
            ArgumentNullException.ThrowIfNull(value);
            // Manual local wire contract: the resource's name uses genuine string
            // encoding. No global registration or generated disposal/copy behavior.
            StringCodec.WriteField(ref writer, fieldIdDelta, value.Name);
        }

        [return: MaybeNull]
        public OwnedResource ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReadCount++;
            var name = StringCodec.ReadValue(ref reader, field)
                ?? throw new InvalidDataException("Resource name must not be null.");
            LastDecoded = new OwnedResource(name);
            OnRead?.Invoke();
            return LastDecoded;
        }
    }

    [Fact]
    public void MessageType_DecodeRejectsAbsentSubjectBeforeCodecRead()
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        probe.ReadFailure = new InvalidDataException("payload codec must not run");
        var type = new MessageType<string>(Subject, serializer);
        var envelope = new Envelope { MessageId = MessageKey, Payload = direct.SerializeToArray("unrouted €42") };

        Assert.Equal("envelope", Assert.Throws<ArgumentException>(() => type.Decode(envelope)).ParamName);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(0, probe.WriteCount);
        Assert.Equal("unrouted €42", direct.Deserialize(envelope.Payload));
        Assert.Equal(MessageKey, envelope.MessageId);
        Assert.Null(envelope.Subject);
        Assert.Null(envelope.Headers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InboxDispatcher_SubjectHeaderFailurePrecedesCodecHandlerAndOutboxMutation(
        bool absent, bool asynchronous)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        probe.ReadFailure = new InvalidDataException("codec must not run");
        var type = new MessageType<string>(Subject, serializer);
        var envelope = new Envelope
        {
            MessageId = MessageKey,
            Payload = direct.SerializeToArray("untouched"),
            Subject = absent ? null : "Orders.Reserve"
        };
        var context = new CountingInboxHandlerContext(envelope);
        var outbox = Substitute.For<IOutbox>();
        var stock = 7;
        var calls = 0;
        var dispatcher = new TestInbox();
        void Mutate(string body, IInboxHandlerContext caller)
        {
            calls++;
            stock--;
            outbox.Send(type, new MessageId(MessageKey.Key.CreateChildKey("result")), Receiver, body);
            caller.Complete();
        }
        if (asynchronous)
            dispatcher.Register(type, (body, caller, _) =>
            {
                Mutate(body, caller);
                return ValueTask.CompletedTask;
            });
        else dispatcher.Register(type, Mutate);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.HandleAsync(context, CancellationToken.None));

        // Handlers match subjects exactly, so an absent subject matches no typed handler.
        Assert.Contains("No handler is registered", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(0, probe.WriteCount);
        Assert.Equal(0, calls);
        Assert.Equal(7, stock);
        Assert.Equal(0, context.CompletionCount);
        outbox.DidNotReceive().Send(Arg.Any<OutboxMessage>());
        Assert.Equal("untouched", direct.Deserialize(context.Message.Envelope.Payload));
        Assert.Equal(MessageKey, context.Message.Envelope.MessageId);
        if (absent)
        {
            Action<Envelope> validate = EnvelopeValidation.Validate;
            foreach (var invalidSubject in new[] { "", new string('s', 257), new string('\u20ac', 85) + "ab" })
            {
                var invalid = new Envelope
                {
                    MessageId = MessageKey,
                    Payload = envelope.Payload,
                    Subject = invalidSubject
                };
                Assert.Equal("subject", Assert.Throws<ArgumentException>(() =>
                    validate(invalid)).ParamName);
                Assert.Same(envelope.Payload, invalid.Payload);
            }
            var invalidUtf16 = new Envelope
            {
                MessageId = MessageKey,
                Payload = envelope.Payload,
                Subject = "\ud800"
            };
            Assert.Throws<EncoderFallbackException>(() => validate(invalidUtf16));
            var missingPayload = new Envelope { MessageId = MessageKey, Payload = null!, Subject = Subject };
            Assert.Equal("envelope", Assert.Throws<ArgumentException>(() =>
                validate(missingPayload)).ParamName);
            var valid = new Envelope { MessageId = MessageKey, Payload = envelope.Payload, Subject = Subject };
            validate(valid);
            Assert.Equal(Subject, GetSubject(valid));
            Assert.Equal("untouched", direct.Deserialize(valid.Payload));
            Assert.Equal(0, probe.ReadCount);
            Assert.Equal(0, calls);
            Assert.Equal(7, stock);
            Assert.Equal(0, context.CompletionCount);
            outbox.DidNotReceive().Send(Arg.Any<OutboxMessage>());
        }
    }

    [Fact]
    public void MessageType_CreateSharesEnvelopeObjectsAndDecodesAllMessageRoles()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(Subject, serializer);

        var outgoing = type.Create(MessageKey, Receiver, "shared payload € / 東京");
        var incoming = outgoing.ToInboxMessage();
        var envelope = outgoing.Envelope;

        Assert.Equal(MessageKey, envelope.MessageId);
        Assert.Equal(Receiver, outgoing.ReceiverId);
        Assert.Equal(Subject, GetSubject(envelope));
        Assert.Null(envelope.Headers);
        Assert.Equal(serializer.SerializeToArray("shared payload € / 東京"), envelope.Payload);
        Assert.Same(envelope.Payload, incoming.Envelope.Payload);
        Assert.Same(envelope.Subject, incoming.Envelope.Subject);
        Assert.Null(incoming.Envelope.Headers);
        Assert.Equal("shared payload € / 東京", type.Decode(envelope));
        Assert.Equal("shared payload € / 東京", type.Decode(incoming.Envelope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Envelope_IdAndPayloadOnlyPermitEmptyPayloadAndOptionalMetadata(bool emptyPayload)
    {
        byte[] bytes = emptyPayload ? [] : [0, 17, 255];
        var envelope = new Envelope { MessageId = MessageKey, Payload = bytes };

        Assert.Equal(MessageKey, envelope.MessageId);
        Assert.Equal(bytes, envelope.Payload.ToArray());
        Assert.Same(bytes, envelope.Payload);
        Assert.Null(envelope.Subject);
        Assert.Null(envelope.Headers);
    }

    [Fact]
    public void InboxDispatcher_SubjectHeaderLookupDoesNotAllocateDecodedSubjectStrings()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new MessageType<string>(SubjectAtLimit(multibyte: true), serializer);
        var outgoing = type.Create(MessageKey, Receiver, "allocation control");
        var context = new CountingInboxHandlerContext(outgoing);
        var state = new HandlerState();
        var dispatcher = new TestInbox().Register(type, state, ObserveState);
        for (var i = 0; i < 64; i++) type.Decode(outgoing.Envelope);
        MeasureDispatchAllocations(dispatcher, context, 64);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++) type.Decode(outgoing.Envelope);
        var decodeBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var dispatchBytes = MeasureDispatchAllocations(dispatcher, context, 256);

        Assert.True(dispatchBytes <= decodeBytes,
            $"Subject dispatch allocated {dispatchBytes}, direct decode allocated {decodeBytes}.");
        Assert.Equal(320, state.Calls);
        Assert.Equal("allocation control", state.Body);
        Assert.Same(context, state.Context);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal(SubjectAtLimit(multibyte: true), GetSubject(outgoing.Envelope));
        Assert.Equal(MessageKey, outgoing.Envelope.MessageId);
        Assert.Equal("allocation control", type.Decode(context.Message.Envelope));
    }

    [Fact]
    public void AddMessageType_GrainIdPayloadCollapsesSharedSerializerRegistration()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddMessageType<GrainId>(Subject));

        Assert.Equal(2, services.Count);
        var closed = services[0];
        Assert.False(closed.IsKeyedService);
        Assert.Equal(typeof(Serializer<GrainId>), closed.ServiceType);
        Assert.Equal(ServiceLifetime.Singleton, closed.Lifetime);
        Assert.Null(closed.ImplementationInstance);
        Assert.Null(closed.ImplementationType);
        Assert.NotNull(closed.ImplementationFactory);
        var binding = services[1];
        Assert.True(binding.IsKeyedService);
        Assert.Equal(typeof(MessageType<GrainId>), binding.ServiceType);
        Assert.Equal(Subject, binding.ServiceKey);
        Assert.Equal(ServiceLifetime.Singleton, binding.Lifetime);
        var bindingFactory = binding.KeyedImplementationFactory;
        Assert.NotNull(bindingFactory);

        // Registration must be lazy: the serializer infrastructure is added later.
        services.AddSerializer();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var serializer = provider.GetRequiredService<Serializer<GrainId>>();
        var messageType = provider.GetRequiredKeyedService<MessageType<GrainId>>(Subject);
        Assert.Equal(Subject, messageType.Subject);
        Assert.Same(serializer, scope.ServiceProvider.GetRequiredService<Serializer<GrainId>>());
        Assert.Same(messageType, scope.ServiceProvider.GetRequiredKeyedService<MessageType<GrainId>>(Subject));
        Assert.Same(messageType, provider.GetRequiredKeyedService<MessageType<GrainId>>(Subject));
        var factoryProvider = Substitute.For<IServiceProvider>();
        factoryProvider.GetService(typeof(Serializer<GrainId>)).Returns(serializer);
        factoryProvider.ClearReceivedCalls();

        var fromFactory = Assert.IsType<MessageType<GrainId>>(bindingFactory(factoryProvider, Subject));

        Assert.Equal(Subject, fromFactory.Subject);
        factoryProvider.Received(1).GetService(typeof(Serializer<GrainId>));
        factoryProvider.DidNotReceive().GetService(typeof(Serializer));

        // An existing closed serializer serves both roles without replacement or
        // an untyped Serializer dependency, and its factory runs only once.
        using var codecServices = CreateServices();
        var (custom, probe) = CreateProbedSerializer<GrainId>(codecServices);
        var customServices = new ServiceCollection();
        var factoryCalls = 0;
        customServices.AddSingleton<Serializer<GrainId>>(_ =>
        {
            factoryCalls++;
            return custom;
        });
        var original = Assert.Single(customServices);

        Assert.Same(customServices, customServices.AddMessageType<GrainId>(Subject));

        Assert.Equal(2, customServices.Count);
        Assert.Same(original, customServices[0]);
        Assert.Same(original, Assert.Single(customServices, descriptor =>
            !descriptor.IsKeyedService && descriptor.ServiceType == typeof(Serializer<GrainId>)));
        Assert.Equal(0, factoryCalls);
        using var customProvider = customServices.BuildServiceProvider();
        using var customScope = customProvider.CreateScope();
        Assert.Null(customProvider.GetService<Serializer>());
        var customBinding = customProvider.GetRequiredKeyedService<MessageType<GrainId>>(Subject);
        Assert.Equal(Subject, customBinding.Subject);
        Assert.Same(customBinding, customScope.ServiceProvider.GetRequiredKeyedService<MessageType<GrainId>>(Subject));
        Assert.Same(custom, customProvider.GetRequiredService<Serializer<GrainId>>());
        Assert.Same(custom, customScope.ServiceProvider.GetRequiredService<Serializer<GrainId>>());
        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, probe.WriteCount);
        Assert.Equal(0, probe.ReadCount);

        var bytes = custom.SerializeToArray(Receiver);

        Assert.Equal(codecServices.GetRequiredService<Serializer<GrainId>>().SerializeToArray(Receiver), bytes);
        Assert.Equal(Receiver, custom.Deserialize(bytes));
        Assert.Equal(1, probe.WriteCount);
        Assert.Equal(1, probe.ReadCount);
        Assert.Equal(1, factoryCalls);

        var outgoing = customBinding.Create(MessageKey, Receiver, Receiver);
        Assert.Equal(2, probe.WriteCount);
        Assert.Equal(Receiver, outgoing.ReceiverId);
        Assert.Equal(MessageKey, outgoing.Envelope.MessageId);
        Assert.Equal(Receiver, customBinding.Decode(outgoing.Envelope));
        Assert.Null(outgoing.Envelope.Headers);
        Assert.Equal(2, probe.ReadCount);
        Assert.Equal(1, factoryCalls);
    }
}
