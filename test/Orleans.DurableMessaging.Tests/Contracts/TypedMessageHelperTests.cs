using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Hosting;
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
public sealed class TypedMessageHelperTests
{
    private const string Subject = "orders.reserve";
    private static GrainId Sender => GrainId.Create("sender", "typed-helper");
    private static GrainId Receiver => GrainId.Create("receiver", "warehouse");
    private static HierarchicalKey MessageKey => HierarchicalKey.Create("orders","42","reserve");

    [Fact]
    public void DurableMessageType_RejectsNullSerializer()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DurableMessageType<string>(Subject, null!));
        Assert.Equal("serializer", exception.ParamName);
    }

    [Fact]
    public void DurableMessageType_RejectsNullSubject()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var exception = Assert.Throws<ArgumentNullException>(
            () => new DurableMessageType<string>(null!, serializer));
        Assert.Equal("subject", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(Subject, new DurableMessageType<string>(Subject, serializer).Subject);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("ascii")]
    [InlineData("multibyte")]
    public void DurableMessageType_RejectsEmptyOrOversizedUtf8Subject(string variation)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var subject = InvalidSubject(variation);

        var exception = Assert.Throws<ArgumentException>(() => new DurableMessageType<string>(subject, serializer));
        Assert.Equal("subject", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(Subject, new DurableMessageType<string>(Subject, serializer).Subject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DurableMessageType_AcceptsSubjectAtUtf8ByteLimit(bool multibyte)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var subject = SubjectAtLimit(multibyte);
        var messageType = new DurableMessageType<string>(subject, serializer);
        using var envelope = DirectEnvelope(serializer, subject, "boundary reserve €42");

        Assert.Equal(256, Encoding.UTF8.GetByteCount(subject));
        Assert.Equal(subject, messageType.Subject);
        Assert.Equal("boundary reserve €42", messageType.Decode(envelope));
    }

    [Theory]
    [InlineData("reserve order 42: € / 東京")]
    [InlineData("")]
    [InlineData("first line\nsecond line")]
    public void DurableMessageType_DecodeReadsDirectSerializerPayload(string body)
    {
        using var sendingServices = CreateServices();
        using var receivingServices = CreateServices();
        var sendingSerializer = sendingServices.GetRequiredService<Serializer<string>>();
        var receivingSerializer = receivingServices.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(sendingSerializer, Subject, body);
        var expectedWire = sendingSerializer.SerializeToArray(body);
        var messageType = new DurableMessageType<string>(Subject, receivingSerializer);

        Assert.Equal(body, messageType.Decode(envelope));
        Assert.Equal(Subject, messageType.Subject);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Equal(body, receivingSerializer.Deserialize(envelope.Payload));
    }

    [Theory]
    [InlineData("Orders.Reserve")]
    [InlineData("orders.cancel")]
    public void DurableMessageType_DecodeRejectsWrongSubjectBeforeCodecRead(string wrongSubject)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var sentinel = new InvalidDataException("read must not run");
        probe.ReadFailure = sentinel;
        var messageType = new DurableMessageType<string>(Subject, serializer);
        using var envelope = DirectEnvelope(direct, wrongSubject, "reserve €42");
        var expectedWire = direct.SerializeToArray("reserve €42");

        var exception = Assert.Throws<ArgumentException>(() => messageType.Decode(envelope));

        Assert.Equal("envelope", exception.ParamName);
        Assert.NotSame(sentinel, exception);
        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Equal("reserve €42", direct.Deserialize(envelope.Payload));
    }

    [Fact]
    public void DurableMessageType_DecodeRejectsDirectSerializedNull()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, null!);
        var expectedWire = serializer.SerializeToArray(null);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        Assert.Null(serializer.Deserialize(envelope.Payload));

        var exception = Assert.Throws<ArgumentException>(() => messageType.Decode(envelope));

        Assert.Equal("envelope", exception.ParamName);
        Assert.Equal(Subject, messageType.Subject);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Null(serializer.Deserialize(envelope.Payload));
    }

    [Fact]
    public void AddDurableMessageType_RejectsNullServiceCollection()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => DurableMessageTypeExtensions.AddDurableMessageType<string>(null!, Subject));
        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void AddDurableMessageType_RejectsNullSubjectWithoutChangingServices()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        var before = services.ToArray();

        var exception = Assert.Throws<ArgumentNullException>(
            () => services.AddDurableMessageType<string>(null!));

        Assert.Equal("subject", exception.ParamName);
        AssertUnchangedServices(before, services);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("ascii")]
    [InlineData("multibyte")]
    public void AddDurableMessageType_RejectsEmptyOrOversizedUtf8SubjectWithoutChangingServices(string variation)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        var before = services.ToArray();
        var subject = InvalidSubject(variation);

        var exception = Assert.Throws<ArgumentException>(() => services.AddDurableMessageType<string>(subject));

        Assert.Equal("subject", exception.ParamName);
        AssertUnchangedServices(before, services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddDurableMessageType_AcceptsSubjectAtUtf8ByteLimit(bool multibyte)
    {
        var subject = SubjectAtLimit(multibyte);
        var services = new ServiceCollection();
        services.AddSerializer();
        Assert.Same(services, services.AddDurableMessageType<string>(subject));
        using var provider = services.BuildServiceProvider();
        var descriptor = provider.GetRequiredKeyedService<DurableMessageType<string>>(subject);
        using var envelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), subject, "registered boundary");

        Assert.Equal(256, Encoding.UTF8.GetByteCount(subject));
        Assert.Equal(subject, descriptor.Subject);
        Assert.Equal("registered boundary", descriptor.Decode(envelope));
        Assert.Same(descriptor, provider.GetRequiredKeyedService<DurableMessageType<string>>(subject));
    }

    [Fact]
    public void AddDurableMessageType_ReturnsOriginalCollectionAndRegistersExactKeyedSingleton()
    {
        var services = new ServiceCollection();
        var result = services.AddDurableMessageType<string>(Subject);
        Assert.Same(services, result);
        Assert.Equal(2, services.Count);
        var registration = Assert.Single(services, descriptor => descriptor.IsKeyedService);
        Assert.True(registration.IsKeyedService);
        Assert.Equal(Subject, registration.ServiceKey);
        Assert.Equal(typeof(DurableMessageType<string>), registration.ServiceType);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);

        // Successful registration before adding serializers proves lazy resolution.
        services.AddSerializer();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var messageType = provider.GetRequiredKeyedService<DurableMessageType<string>>(Subject);
        Assert.Equal(Subject, messageType.Subject);
        Assert.Same(messageType, provider.GetRequiredKeyedService<DurableMessageType<string>>(Subject));
        Assert.Same(messageType, scope.ServiceProvider.GetRequiredKeyedService<DurableMessageType<string>>(Subject));
        using var envelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), Subject, "keyed reserve");
        Assert.Equal("keyed reserve", messageType.Decode(envelope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddDurableMessageType_RegistersClosedSingletonSerializerFactoryBeforeKeyedBinding(bool valueType)
    {
        if (valueType) AssertClosedSerializerFactory(731);
        else AssertClosedSerializerFactory("closed factory €42");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AddDurableMessageType_PreservesExistingCustomClosedSerializerAndUsesItForBinding(bool valueType, bool factory)
    {
        if (valueType) AssertCustomClosedSerializerPreserved(731, factory);
        else AssertCustomClosedSerializerPreserved("custom closed €42", factory);
    }

    [Fact]
    public void AddDurableMessageType_AllowsMultipleSubjectsForSamePayloadType()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddDurableMessageType<string>(Subject).AddDurableMessageType<string>("orders.cancel");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var reserve = provider.GetRequiredKeyedService<DurableMessageType<string>>(Subject);
        var cancel = provider.GetRequiredKeyedService<DurableMessageType<string>>("orders.cancel");

        Assert.Equal(Subject, reserve.Subject);
        Assert.Equal("orders.cancel", cancel.Subject);
        Assert.NotSame(reserve, cancel);
        Assert.Same(reserve, provider.GetRequiredKeyedService<DurableMessageType<string>>(Subject));
        Assert.Same(cancel, provider.GetRequiredKeyedService<DurableMessageType<string>>("orders.cancel"));
        Assert.Same(reserve, scope.ServiceProvider.GetRequiredKeyedService<DurableMessageType<string>>(Subject));
        Assert.Same(cancel, scope.ServiceProvider.GetRequiredKeyedService<DurableMessageType<string>>("orders.cancel"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddDurableMessageType_RejectsSameAndCrossTypeDuplicateSubjectsWithoutChangingServices(bool crossType)
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddDurableMessageType<string>(Subject);
        var before = services.ToArray();

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (crossType)
            {
                services.AddDurableMessageType<int>(Subject);
            }
            else
            {
                services.AddDurableMessageType<string>(Subject);
            }
        });

        AssertUnchangedServices(before, services);
        using var provider = services.BuildServiceProvider();
        var original = provider.GetRequiredKeyedService<DurableMessageType<string>>(Subject);
        Assert.Equal(Subject, original.Subject);
        using var envelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), Subject, "original binding");
        Assert.Equal("original binding", original.Decode(envelope));
        Assert.Null(provider.GetKeyedService<DurableMessageType<int>>(Subject));
    }

    [Fact]
    public void AddDurableMessageType_AllowsCaseDistinctSubjects()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddDurableMessageType<string>(Subject).AddDurableMessageType<string>("Orders.Reserve");
        using var provider = services.BuildServiceProvider();
        var lower = provider.GetRequiredKeyedService<DurableMessageType<string>>(Subject);
        var upper = provider.GetRequiredKeyedService<DurableMessageType<string>>("Orders.Reserve");

        Assert.Equal(Subject, lower.Subject);
        Assert.Equal("Orders.Reserve", upper.Subject);
        Assert.NotSame(lower, upper);
        using var lowerEnvelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), Subject, "lower");
        using var upperEnvelope = DirectEnvelope(provider.GetRequiredService<Serializer<string>>(), "Orders.Reserve", "upper");
        Assert.Equal("lower", lower.Decode(lowerEnvelope));
        Assert.Equal("upper", upper.Decode(upperEnvelope));
        Assert.Throws<ArgumentException>(() => lower.Decode(upperEnvelope));
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("receiver")]
    [InlineData("messageId")]
    [InlineData("key-segments")]
    [InlineData("key-ascii-bytes")]
    [InlineData("key-multibyte-bytes")]
    public void DurableMessageType_CreateRejectsInvalidIdentitiesBeforeCodecWrite(string invalidIdentity)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var sender = invalidIdentity == "sender" ? default : Sender;
        var receiver = invalidIdentity == "receiver" ? default : Receiver;
        var key = invalidIdentity switch
        {
            "messageId" => default,
            "key-segments" => HierarchicalKey.Create(Enumerable.Repeat("s", 33).ToArray()),
            "key-ascii-bytes" => HierarchicalKey.Create(new string('k', 1025)),
            "key-multibyte-bytes" => HierarchicalKey.Create(new string('\u20ac', 341) + "ab"),
            _ => MessageKey
        };
        DurableEnvelope? result = null;
        try
        {
            var exception = Assert.Throws<ArgumentException>(() =>
            {
                result = messageType.Create(key, sender, receiver, "must not encode");
            });
            Assert.Equal("envelope", exception.ParamName);
            Assert.Null(result);
            Assert.Equal(0, probe.WriteCount);
        }
        finally
        {
            // Also release an unexpected owner if a validation mutation succeeds.
            result?.Dispose();
        }
    }

    [Fact]
    public void DurableMessageType_CreateUsesExplicitIdentitiesAndSuppliedHierarchicalKey()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var key = HierarchicalKey.Create("orders","42","reserve");
        using var first = messageType.Create(key, Sender, Receiver, "reserve first");
        var otherReceiver = GrainId.Create("receiver", "other-warehouse");
        var otherSender = GrainId.Create("sender", "other-owner");
        using var second = messageType.Create(key, otherSender, otherReceiver, "reserve second");

        AssertEnvelopeIdentity(first, key, Sender, Receiver, Subject);
        AssertEnvelopeIdentity(second, key, otherSender, otherReceiver, Subject);
        Assert.Equal("orders/42/reserve", first.MessageId.ToString());
        Assert.Equal("orders/42/reserve", second.MessageId.ToString());
        Assert.Equal(3, first.MessageId.SegmentCount);
        Assert.Equal(3, second.MessageId.SegmentCount);
        Assert.Equal("reserve first", serializer.Deserialize(first.Payload));
        Assert.Equal("reserve second", serializer.Deserialize(second.Payload));
    }

    [Theory]
    [InlineData("segments")]
    [InlineData("ascii-bytes")]
    [InlineData("multibyte-bytes")]
    public void DurableMessageType_CreateAcceptsMessageKeyAtAdmissionLimits(string variation)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var key = variation switch
        {
            "segments" => HierarchicalKey.Create(Enumerable.Repeat("s", 32).ToArray()),
            "ascii-bytes" => HierarchicalKey.Create(new string('k', 1024)),
            "multibyte-bytes" => HierarchicalKey.Create(new string('\u20ac', 341) + "a"),
            _ => throw new ArgumentOutOfRangeException(nameof(variation))
        };

        using var envelope = messageType.Create(key, Sender, Receiver, "admitted boundary");

        Assert.Equal(1, probe.WriteCount);
        AssertEnvelopeIdentity(envelope, key, Sender, Receiver, Subject);
        if (variation == "segments")
        {
            Assert.Equal(32, envelope.MessageId.SegmentCount);
        }
        else
        {
            Assert.Equal(1024, Encoding.UTF8.GetByteCount(envelope.MessageId.ToString()));
        }
        Assert.Equal("admitted boundary", services.GetRequiredService<Serializer<string>>().Deserialize(envelope.Payload));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("empty-string")]
    [InlineData("int")]
    [InlineData("zero-int")]
    [InlineData("negative-int")]
    [InlineData("bytes")]
    [InlineData("empty-bytes")]
    public void DurableMessageType_CreateMatchesDirectSerializerWireFormat(string contract)
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
    public void DurableMessageType_CreatePreservesIndependentSlicesAcrossPoolReuseAndNeighborDisposal()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var messageType = new DurableMessageType<byte[]>(Subject, serializer);
        var firstBody = new byte[] { 9, 8, 7, 255 };
        var largeBody = Enumerable.Range(0, 1_048_576).Select(i => (byte)(i * 31 + 7)).ToArray();
        var lastBody = new byte[] { 42, 0, 128, 255, 19, 21 };
        var largeWire = serializer.SerializeToArray(largeBody);
        var lastWire = serializer.SerializeToArray(lastBody);
        DurableEnvelope? first = null;
        DurableEnvelope? large = null;
        DurableEnvelope? last = null;
        try
        {
            first = messageType.Create(MessageKey, Sender, Receiver, firstBody);
            large = messageType.Create(MessageKey, Sender, Receiver, largeBody);
            // Public span enumeration proves actual segmentation, not an assumed
            // PageCount API or a guessed relationship between body size and capacity.
            Assert.True(CountSegments(large.Value.Payload) > 1);
            Assert.True(largeBody.Length > ArcBufferWriter.MinimumPageSize);
            Assert.Equal(serializer.SerializeToArray(firstBody), first.Value.Payload.ToArray());
            first.Value.Dispose();
            first = null;

            last = messageType.Create(MessageKey, Sender, Receiver, lastBody);
            Assert.NotEqual(large.Value.Payload, last.Value.Payload);

            Assert.Equal(largeWire, large.Value.Payload.ToArray());
            Assert.Equal(lastWire, last.Value.Payload.ToArray());
            Assert.Equal(largeBody, serializer.Deserialize(large.Value.Payload));
            Assert.Equal(lastBody, serializer.Deserialize(last.Value.Payload));
            AssertEnvelopeIdentity(large.Value, MessageKey, Sender, Receiver, Subject);
            AssertEnvelopeIdentity(last.Value, MessageKey, Sender, Receiver, Subject);
        }
        finally
        {
            first?.Dispose();
            large?.Dispose();
            last?.Dispose();
        }
    }

    [Fact]
    public void DurableMessageType_CreateResetsCommittedPartialOutputAfterCodecFailure()
    {
        using var services = CreateServices();
        var pristine = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        using var outstanding = messageType.Create(MessageKey, Sender, Receiver, "outstanding € order");
        var outstandingWire = pristine.SerializeToArray("outstanding € order");
        var sentinel = new InvalidDataException("committed partial codec failure");
        probe.NextWriteFailure = sentinel;
        DurableEnvelope? failedResult = null;
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
            {
                failedResult = messageType.Create(MessageKey, Sender, Receiver, "failing preparation");
            });
            Assert.Same(sentinel, exception);
            Assert.Null(failedResult);
            Assert.Equal(2, probe.WriteCount);
            Assert.Equal(1, probe.CommittedFailureCount);
            Assert.Equal((byte)0x7e, probe.CommittedFailureByte);

            using var recovered = messageType.Create(MessageKey, Sender, Receiver, "recovered 東京 reserve");
            Assert.Equal(3, probe.WriteCount);
            Assert.Equal(pristine.SerializeToArray("recovered 東京 reserve"), recovered.Payload.ToArray());
            Assert.Equal("recovered 東京 reserve", pristine.Deserialize(recovered.Payload));
            Assert.Equal(outstandingWire, outstanding.Payload.ToArray());
            Assert.Equal("outstanding € order", pristine.Deserialize(outstanding.Payload));
            AssertEnvelopeIdentity(recovered, MessageKey, Sender, Receiver, Subject);
        }
        finally
        {
            failedResult?.Dispose();
        }
    }

    [Fact]
    public void DurableMessageType_CreateRetainedEnvelopeSurvivesOriginalOwnerReleaseAndSubsequentCreate()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var original = messageType.Create(MessageKey, Sender, Receiver, "surviving reserve");
        using var surviving = original.Retain();
        original.Dispose();
        using var subsequent = messageType.Create(MessageKey.CreateChildKey("next"), Sender, Receiver, "new reserve");

        Assert.Equal(2, probe.WriteCount);
        Assert.NotEqual(surviving.Payload, subsequent.Payload);
        Assert.Equal(MessageKey, surviving.MessageId);
        Assert.Equal(MessageKey.CreateChildKey("next"), subsequent.MessageId);
        Assert.Equal("surviving reserve", services.GetRequiredService<Serializer<string>>().Deserialize(surviving.Payload));
        Assert.Equal("new reserve", services.GetRequiredService<Serializer<string>>().Deserialize(subsequent.Payload));
    }

    [Fact]
    public void DurableMessageType_CreateRejectsNullBodyBeforeCodecWrite()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);

        var exception = Assert.Throws<ArgumentNullException>(
            () => messageType.Create(MessageKey, Sender, Receiver, null!));

        Assert.Equal("body", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        using var control = messageType.Create(MessageKey, Sender, Receiver, "nonnull recovery");
        Assert.Equal(1, probe.WriteCount);
        Assert.Equal("nonnull recovery", services.GetRequiredService<Serializer<string>>().Deserialize(control.Payload));
    }

    [Fact]
    public void DurableMessageType_CreateDoesNotConsumeResourceBodyAndRetainOwnsIndependentPin()
    {
        using var services = CreateServices();
        var codec = new ResourceCodec();
        var serializer = new Serializer<OwnedResource>(codec, services.GetRequiredService<SerializerSessionPool>());
        var type = new DurableMessageType<OwnedResource>(Subject, serializer);
        using var body = new OwnedResource("resource €42");
        var original = type.Create(MessageKey, Sender, Receiver, body);
        using var retained = original.Retain();
        original.Dispose();

        Assert.Equal(0, body.DisposeCount);
        Assert.Equal(MessageKey, retained.MessageId);
        Assert.Equal(services.GetRequiredService<Serializer<string>>().SerializeToArray(body.Name), retained.Payload.ToArray());
        using var decoded = type.Decode(retained);
        Assert.NotSame(body, decoded);
        Assert.Same(codec.LastDecoded, decoded);
        Assert.Equal("resource €42", decoded.Name);
        Assert.Equal(0, decoded.DisposeCount);
        Assert.Equal(1, codec.ReadCount);
    }

    [Fact]
    public void DurableInboxDispatcher_RegisterRejectsNullMessageType()
    {
        var dispatcher = new DurableInboxDispatcher();
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
    public void DurableInboxDispatcher_RegisterRejectsNullHandler()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var dispatcher = new DurableInboxDispatcher();

        Func<string, IInboxHandlerContext, CancellationToken, ValueTask>? handler = null;
        var exception = Assert.Throws<ArgumentNullException>(() => dispatcher.Register(messageType, handler!));

        Assert.Equal("handler", exception.ParamName);
        Assert.Equal(0, probe.ReadCount);
        Assert.Same(dispatcher, dispatcher.Register(messageType, (_, _, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task DurableInboxDispatcher_HandleAsyncRejectsNullContext()
    {
        using var services = CreateServices();
        var dispatcher = new DurableInboxDispatcher();
        var messageType = new DurableMessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await dispatcher.HandleAsync(null!, CancellationToken.None));

        Assert.Equal("context", exception.ParamName);
        // A rejected null caller does not start handling or freeze registration.
        Assert.Same(dispatcher, dispatcher.Register(messageType, (_, _, _) => ValueTask.CompletedTask));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableInboxDispatcher_RegisterReturnsSelfAndRejectsSameAndCrossTypeDuplicatesWithoutReplacingHandler(
        bool crossType)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var dispatcher = new DurableInboxDispatcher();
        var originalCalls = 0;
        var replacementCalls = 0;
        string? captured = null;
        Assert.Same(dispatcher, dispatcher.Register(messageType, (body, _, _) =>
        {
            originalCalls++;
            captured = body;
            return ValueTask.CompletedTask;
        }));

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (crossType)
            {
                dispatcher.Register(new DurableMessageType<int>(Subject, services.GetRequiredService<Serializer<int>>()),
                    (_, _, _) =>
                    {
                        replacementCalls++;
                        return ValueTask.CompletedTask;
                    });
            }
            else
            {
                dispatcher.Register(messageType, (_, _, _) =>
                {
                    replacementCalls++;
                    return ValueTask.CompletedTask;
                });
            }
        });
        using var envelope = DirectEnvelope(serializer, Subject, "original delegate €42");
        var context = new CountingInboxHandlerContext(envelope);
        await dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.Equal("original delegate €42", captured);
        Assert.Equal(1, originalCalls);
        Assert.Equal(0, replacementCalls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task DurableInboxDispatcher_HandleAsyncRoutesMultipleExactSubjectsAndPayloadTypes()
    {
        using var services = CreateServices();
        var strings = services.GetRequiredService<Serializer<string>>();
        var integers = services.GetRequiredService<Serializer<int>>();
        var dispatcher = new DurableInboxDispatcher();
        var lowerValues = new List<string>();
        var upperValues = new List<string>();
        var integerValues = new List<int>();
        dispatcher.Register(new DurableMessageType<string>(Subject, strings), (body, _, _) =>
        {
            lowerValues.Add(body);
            return ValueTask.CompletedTask;
        });
        dispatcher.Register(new DurableMessageType<string>("Orders.Reserve", strings), (body, _, _) =>
        {
            upperValues.Add(body);
            return ValueTask.CompletedTask;
        });
        dispatcher.Register(new DurableMessageType<int>("orders.quantity", integers), (body, _, _) =>
        {
            integerValues.Add(body);
            return ValueTask.CompletedTask;
        });
        using var lowerEnvelope = DirectEnvelope(strings, Subject, "lower €42");
        using var upperEnvelope = DirectEnvelope(strings, "Orders.Reserve", "upper 東京");
        using var integerEnvelope = DirectEnvelope(integers, "orders.quantity", 42_017);
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

    [Theory]
    [InlineData("success")]
    [InlineData("unknown")]
    [InlineData("canceled")]
    public async Task DurableInboxDispatcher_FirstDispatchAttemptFreezesRegistration(string firstAttempt)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var direct = services.GetRequiredService<Serializer<string>>();
        var dispatcher = new DurableInboxDispatcher();
        var values = new List<string>();
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, _, _) =>
        {
            values.Add(body);
            return ValueTask.CompletedTask;
        });
        using var envelope = DirectEnvelope(direct, firstAttempt == "unknown" ? "orders.unknown" : Subject, "first");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        if (firstAttempt == "canceled")
        {
            cancellation.Cancel();
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await dispatcher.HandleAsync(context, cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        else if (firstAttempt == "unknown")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await dispatcher.HandleAsync(context, CancellationToken.None));
        }
        else
        {
            await dispatcher.HandleAsync(context, CancellationToken.None);
        }

        Assert.Equal(firstAttempt == "success" ? 1 : 0, probe.ReadCount);
        Assert.Equal(firstAttempt == "success" ? 1 : 0, values.Count);
        Assert.Equal(0, context.CompletionCount);
        Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Register(new DurableMessageType<string>("orders.later", direct), (_, _, _) => ValueTask.CompletedTask));
        using var subsequentEnvelope = DirectEnvelope(direct, Subject, "still registered");
        var subsequentContext = new CountingInboxHandlerContext(subsequentEnvelope);
        await dispatcher.HandleAsync(subsequentContext, CancellationToken.None);
        Assert.Equal(firstAttempt == "success" ? new[] { "first", "still registered" } : new[] { "still registered" }, values);
        Assert.Equal(firstAttempt == "success" ? 2 : 1, probe.ReadCount);
        Assert.Equal(0, subsequentContext.CompletionCount);
    }

    [Fact]
    public async Task DurableInboxDispatcher_HandleAsyncRejectsUnknownSubjectBeforeDecodeOrHandler()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        probe.ReadFailure = new InvalidDataException("unknown subject must not decode");
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        var direct = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(direct, "orders.unknown", "valid reserve €42");
        var context = new CountingInboxHandlerContext(envelope);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Equal(0, probe.ReadCount);
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("valid reserve €42", direct.Deserialize(envelope.Payload));
    }

    [Fact]
    public async Task DurableInboxDispatcher_HandleAsyncRejectsMalformedKnownPayloadBeforeHandler()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var wire = serializer.SerializeToArray("nonempty truncated €42");
        using var envelope = WireEnvelope(Subject, wire[..^1]);
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        // StringCodec.ReadRaw -> Reader.EnsureAvailable rejects the declared
        // length exceeding remaining bytes, independently of subject routing.
        Assert.Throws<IndexOutOfRangeException>(() => serializer.Deserialize(envelope.Payload));

        await Assert.ThrowsAsync<IndexOutOfRangeException>(
            async () => await dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Equal(Subject, envelope.Subject);
        Assert.Equal(wire[..^1], envelope.Payload.ToArray());
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task DurableInboxDispatcher_HandleAsyncPassesExactContextAndCancellationToken()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "context €42");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new DurableInboxDispatcher();
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        CancellationToken capturedToken = default;
        var calls = 0;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, caller, token) =>
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
    public async Task DurableInboxDispatcher_HandleAsyncPropagatesSynchronousValueTaskCompletionWithoutAutomaticComplete()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "synchronous reserve");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        string? captured = null;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, _, _) =>
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
    public async Task DurableInboxDispatcher_HandleAsyncPropagatesSynchronousFaultedValueTask()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "faulted task reserve");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        var sentinel = new InvalidDataException("synchronous faulted ValueTask");
        var calls = 0;
        string? captured = null;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, _, _) =>
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
    public async Task DurableInboxDispatcher_HandleAsyncPropagatesSynchronousDelegateException()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "throwing delegate reserve");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        var sentinel = new InvalidDataException("delegate throws before returning");
        var calls = 0;
        string? captured = null;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, _, _) =>
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
    public async Task DurableInboxDispatcher_HandleAsyncPropagatesDeferredValueTaskCompletion()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "deferred €42");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        CancellationToken capturedToken = default;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, caller, token) =>
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
        var lateRegistration = Record.Exception(() => dispatcher.Register(
            new DurableMessageType<string>("orders.later", serializer), (_, _, _) => ValueTask.CompletedTask));
        completion.SetResult();
        await dispatch;

        Assert.False(completedBeforeRelease);
        Assert.IsType<InvalidOperationException>(lateRegistration);
        Assert.Equal(0, completionsBeforeRelease);
        Assert.Equal("deferred €42", capturedBody);
        Assert.Same(context, capturedContext);
        Assert.Equal(cancellation.Token, capturedToken);
        Assert.Equal(1, calls);
        Assert.Equal(0, context.CompletionCount);
        Assert.Equal("deferred €42", serializer.Deserialize(envelope.Payload));
    }

    [Fact]
    public async Task DurableInboxDispatcher_HandleAsyncPropagatesDeferredValueTaskFault()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "deferred fault €42");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinel = new InvalidDataException("controlled deferred fault");
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        CancellationToken capturedToken = default;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, caller, token) =>
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
        var lateRegistration = Record.Exception(() => dispatcher.Register(
            new DurableMessageType<string>("orders.later", serializer), (_, _, _) => ValueTask.CompletedTask));
        completion.SetException(sentinel);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () => await dispatch);

        Assert.False(completedBeforeRelease);
        Assert.IsType<InvalidOperationException>(lateRegistration);
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
    public async Task DurableInboxDispatcher_HandleAsyncStagesOnlyExplicitDelegateCompletion()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "explicit completion");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        string? capturedBody = null;
        IInboxHandlerContext? capturedContext = null;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (body, caller, _) =>
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
    public async Task DurableInboxDispatcher_HandleAsyncHonorsPreCanceledTokenWithoutDecodeHandlerOrComplete(bool unknownSubject)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        probe.ReadFailure = new InvalidDataException("canceled attempts must not decode");
        var direct = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(direct, unknownSubject ? "orders.unknown" : Subject, "valid canceled reserve");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var dispatcher = new DurableInboxDispatcher();
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (_, _, _) =>
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
    public async Task DurableInboxDispatcher_HandleAsyncLeavesDecodedResourceApplicationOwned()
    {
        using var services = CreateServices();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var codec = new ResourceCodec();
        var serializer = new Serializer<OwnedResource>(codec, sessions);
        using var original = new OwnedResource("owned reserve €42");
        var envelope = DirectEnvelope(serializer, Subject, original);
        var ownsEnvelope = true;
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        OwnedResource? captured = null;
        var calls = 0;
        dispatcher.Register(new DurableMessageType<OwnedResource>(Subject, serializer), (body, _, _) =>
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
            // End the payload lifetime after dispatch, before the application's
            // resource lifetime. The context's copy was only borrowed.
            envelope.Dispose();
            ownsEnvelope = false;
            Assert.Equal("owned reserve €42", decoded.Name);
            Assert.Equal(0, decoded.DisposeCount);
            decoded.Dispose();
            Assert.Equal(1, decoded.DisposeCount);
            Assert.Equal(0, original.DisposeCount);
        }
        finally
        {
            if (ownsEnvelope)
            {
                envelope.Dispose();
            }

            if (codec.LastDecoded is { DisposeCount: 0 } resource)
            {
                resource.Dispose();
            }
        }
    }

    [Fact]
    public async Task DurableInboxDispatcher_HandleAsyncRejectsSerializedNullBeforeHandler()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, null!);
        var expectedWire = serializer.SerializeToArray(null);
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        dispatcher.Register(new DurableMessageType<string>(Subject, serializer), (_, caller, _) =>
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
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = DirectEnvelope(serializer, Subject, "synchronous");
        var context = new CountingInboxHandlerContext(envelope);
        var dispatcher = new DurableInboxDispatcher();
        var calls = 0;
        var registered = dispatcher.Register(type, (body, caller) =>
        {
            Assert.Equal("synchronous", body);
            Assert.Same(context, caller);
            calls++;
            if (complete) caller.Complete();
        });

        var outcome = dispatcher.HandleAsync(context, CancellationToken.None);

        Assert.Same(dispatcher, registered);
        Assert.True(outcome.IsCompletedSuccessfully);
        Assert.Equal(1, calls);
        Assert.Equal(complete ? 1 : 0, context.CompletionCount);
        await outcome;
    }

    [Fact]
    public void RegisterAction_RejectsNullArguments()
    {
        using var services = CreateServices();
        var type = new DurableMessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var dispatcher = new DurableInboxDispatcher();
        var missingType = Assert.Throws<ArgumentNullException>(() =>
            dispatcher.Register<string>(null!, (_, _) => { }));
        Action<string, IInboxHandlerContext>? handler = null;
        var missingHandler = Assert.Throws<ArgumentNullException>(() => dispatcher.Register(type, handler!));

        Assert.Equal("messageType", missingType.ParamName);
        Assert.Equal("handler", missingHandler.ParamName);
        Assert.Same(dispatcher, dispatcher.Register(type, (_, _) => { }));
    }

    [Fact]
    public void RegisterAction_PropagatesOriginalDelegateFailure()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        using var envelope = DirectEnvelope(serializer, Subject, "failure");
        var context = new CountingInboxHandlerContext(envelope);
        var sentinel = new IOException("synchronous handler failed");
        var dispatcher = new DurableInboxDispatcher().Register(
            new DurableMessageType<string>(Subject, serializer), (_, _) => throw sentinel);

        var exception = Assert.Throws<IOException>(() => dispatcher.HandleAsync(context, CancellationToken.None));

        Assert.Same(sentinel, exception);
        Assert.Equal(0, context.CompletionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterAction_CancellationBeforeEntryLeavesHandlerAndCompletionUntouched(bool cancelDuringDecode)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        using var cancellation = new CancellationTokenSource();
        if (cancelDuringDecode) probe.OnRead = cancellation.Cancel;
        else cancellation.Cancel();
        using var envelope = DirectEnvelope(direct, Subject, "cancellation");
        var context = new CountingInboxHandlerContext(envelope);
        var calls = 0;
        var dispatcher = new DurableInboxDispatcher().Register(
            new DurableMessageType<string>(Subject, serializer), (_, _) => calls++);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            dispatcher.HandleAsync(context, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancelDuringDecode ? 1 : 0, probe.ReadCount);
        Assert.Equal(0, calls);
        Assert.Equal(0, context.CompletionCount);
    }

    [Fact]
    public async Task RegisterAction_CoexistsWithTaskReturningHandlerAndFrozenSubjectRoutes()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var synchronous = new DurableMessageType<string>(Subject, serializer);
        var asynchronous = new DurableMessageType<string>("stock.async.v1", serializer);
        var inbox = Substitute.For<IDurableInbox>();
        IInboxHandler registered = null!;
        DurableInboxDispatcher configured = null!;
        inbox.When(value => value.RegisterHandler(Arg.Any<IInboxHandler>())).Do(call => registered = call.Arg<IInboxHandler>());
        var seen = new List<string>();
        inbox.RegisterHandlers(routes =>
        {
            configured = routes;
            routes.Register(synchronous, (body, context) =>
            {
                seen.Add(body);
                context.Complete();
            }).Register(asynchronous, (body, context, _) =>
            {
                seen.Add(body);
                context.Complete();
                return ValueTask.CompletedTask;
            });
        });

        Assert.Throws<InvalidOperationException>(() => configured.Register(
            new DurableMessageType<string>("stock.late.v1", serializer), (_, _) => { }));
        using var first = DirectEnvelope(serializer, synchronous.Subject, "sync");
        using var second = DirectEnvelope(serializer, asynchronous.Subject, "async");
        var firstContext = new CountingInboxHandlerContext(first);
        var secondContext = new CountingInboxHandlerContext(second);
        await registered.HandleAsync(firstContext, CancellationToken.None);
        await registered.HandleAsync(secondContext, CancellationToken.None);

        Assert.Equal(new[] { "sync", "async" }, seen);
        Assert.Equal(1, firstContext.CompletionCount);
        Assert.Equal(1, secondContext.CompletionCount);
        inbox.Received(1).RegisterHandler(configured);
    }

    [Fact]
    public async Task RegisterAction_DuplicateSubjectPreservesOriginalHandler()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new DurableMessageType<string>(Subject, serializer);
        var originalCalls = 0;
        var replacementCalls = 0;
        var dispatcher = new DurableInboxDispatcher().Register(type, (_, _) => originalCalls++);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Register(type, (_, _) => replacementCalls++));
        using var envelope = DirectEnvelope(serializer, Subject, "original");
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
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = type.Create(MessageKey, Sender, Receiver, "stateful €42");
        var context = new CountingInboxHandlerContext(envelope);
        var state = new HandlerState { Complete = complete };
        Action<string, HandlerState, IInboxHandlerContext> handler = ObserveState;
        Assert.Null(handler.Target); // Named static method, not a captured test closure.
        var dispatcher = new DurableInboxDispatcher();
        Assert.Same(dispatcher, dispatcher.Register(type, state, handler));

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
    public async Task RegisterState_MixedStaticReferenceAndStructArgumentsRouteOrdinalSubjectsAndFreeze()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var syncType = new DurableMessageType<string>(Subject, serializer);
        var asyncType = new DurableMessageType<string>("Orders.Reserve", serializer);
        var syncState = new HandlerState { Complete = true };
        var asyncState = new HandlerState();
        var argument = new StructHandlerState(asyncState, 731);
        Action<string, HandlerState, IInboxHandlerContext> sync = ObserveState;
        Func<string, StructHandlerState, IInboxHandlerContext, CancellationToken, ValueTask> asyncHandler = ObserveStructState;
        Assert.Null(sync.Target);
        Assert.Null(asyncHandler.Target);
        var dispatcher = new DurableInboxDispatcher().Register(syncType, syncState, sync).Register(asyncType, argument, asyncHandler);
        using var syncEnvelope = syncType.Create(MessageKey, Sender, Receiver, "lower");
        using var asyncEnvelope = asyncType.Create(MessageKey, Sender, Receiver, "upper");
        var syncContext = new CountingInboxHandlerContext(syncEnvelope);
        var asyncContext = new CountingInboxHandlerContext(asyncEnvelope);
        using var cancellation = new CancellationTokenSource();

        await dispatcher.HandleAsync(syncContext, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Register(
            new DurableMessageType<string>("later", serializer), syncState, sync));
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
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = type.Create(MessageKey, Sender, Receiver, "struct state");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var observation = new HandlerState { Complete = true };
        var argument = new StructHandlerState(observation, 731);
        var dispatcher = new DurableInboxDispatcher();
        if (asynchronous)
        {
            Func<string, StructHandlerState, IInboxHandlerContext, CancellationToken, ValueTask> handler = ObserveStructState;
            Assert.Null(handler.Target);
            Assert.Same(dispatcher, dispatcher.Register(type, argument, handler));
        }
        else
        {
            Action<string, StructHandlerState, IInboxHandlerContext> handler = ObserveStructAction;
            Assert.Null(handler.Target);
            Assert.Same(dispatcher, dispatcher.Register(type, argument, handler));
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
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = type.Create(MessageKey, Sender, Receiver, "deferred state");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new HandlerState { Outcome = new ValueTask(gate.Task) };
        Func<string, HandlerState, IInboxHandlerContext, CancellationToken, ValueTask> handler = ObserveAsyncState;
        Assert.Null(handler.Target);
        var dispatcher = new DurableInboxDispatcher().Register(type, state, handler);

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
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = type.Create(MessageKey, Sender, Receiver, "sentinel");
        var context = new CountingInboxHandlerContext(envelope);
        var sentinel = new IOException("stateful sync sentinel");
        var state = new HandlerState();
        var dispatcher = new DurableInboxDispatcher();
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RegisterState_CancellationBeforeOrDuringDecodePreventsAllDelegateEffects(bool asynchronous, bool duringDecode)
    {
        using var services = CreateServices();
        var direct = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        using var cancellation = new CancellationTokenSource();
        if (duringDecode) probe.OnRead = cancellation.Cancel;
        else cancellation.Cancel();
        using var envelope = DirectEnvelope(direct, Subject, "canceled state");
        var context = new CountingInboxHandlerContext(envelope);
        var state = new HandlerState { Complete = true };
        var type = new DurableMessageType<string>(Subject, serializer);
        var dispatcher = new DurableInboxDispatcher();
        if (asynchronous) dispatcher.Register(type, state, ObserveAsyncState);
        else dispatcher.Register(type, state, ObserveState);

        var exception = Assert.Throws<OperationCanceledException>(() => dispatcher.HandleAsync(context, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(duringDecode ? 1 : 0, probe.ReadCount);
        Assert.Equal(0, state.Calls);
        Assert.Null(state.Body);
        Assert.Null(state.Context);
        Assert.Equal(0, context.CompletionCount);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Register(
            new DurableMessageType<string>("late", serializer), state, ObserveState));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterState_CancellationInsideSuccessfulDelegatePreservesExplicitCompletion(bool asynchronous)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = type.Create(MessageKey, Sender, Receiver, "completed despite cancel");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var state = new HandlerState { Complete = true, CancelOnInvoke = cancellation };
        var dispatcher = new DurableInboxDispatcher();
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
        using var envelope = variation == "malformed"
            ? WireEnvelope(Subject, wire[..^1])
            : DirectEnvelope(direct, variation == "unknown" ? "Orders.Reserve" : Subject,
                variation == "null" ? null! : "payload");
        var context = new CountingInboxHandlerContext(envelope);
        var type = new DurableMessageType<string>(Subject, serializer);
        var state = new HandlerState { Complete = true };
        var dispatcher = new DurableInboxDispatcher();
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
    public async Task RegisterState_DuplicateSameOrCrossTypePreservesOriginalState(bool crossType)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new DurableMessageType<string>(Subject, serializer);
        var original = new HandlerState();
        var replacement = new HandlerState { Complete = true };
        var dispatcher = new DurableInboxDispatcher().Register(type, original, ObserveState);
        if (crossType)
            Assert.Throws<InvalidOperationException>(() => dispatcher.Register(
                new DurableMessageType<int>(Subject, services.GetRequiredService<Serializer<int>>()),
                replacement, ObserveNumberState));
        else
            Assert.Throws<InvalidOperationException>(() => dispatcher.Register(type, replacement, ObserveAsyncState));
        using var envelope = type.Create(MessageKey, Sender, Receiver, "original state");
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
        var type = new DurableMessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var state = new HandlerState();
        var dispatcher = new DurableInboxDispatcher();
        if (asynchronous)
        {
            Assert.Equal("messageType", Assert.Throws<ArgumentNullException>(() =>
                dispatcher.Register<string, HandlerState>(null!, state, ObserveAsyncState)).ParamName);
            Assert.Equal("handler", Assert.Throws<ArgumentNullException>(() => dispatcher.Register(type, state,
                (Func<string, HandlerState, IInboxHandlerContext, CancellationToken, ValueTask>)null!)).ParamName);
            Assert.Same(dispatcher, dispatcher.Register(type, state, ObserveAsyncState));
        }
        else
        {
            Assert.Equal("messageType", Assert.Throws<ArgumentNullException>(() =>
                dispatcher.Register<string, HandlerState>(null!, state, ObserveState)).ParamName);
            Assert.Equal("handler", Assert.Throws<ArgumentNullException>(() => dispatcher.Register(type, state,
                (Action<string, HandlerState, IInboxHandlerContext>)null!)).ParamName);
            Assert.Same(dispatcher, dispatcher.Register(type, state, ObserveState));
        }
        using var envelope = type.Create(MessageKey, Sender, Receiver, "positive control");
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
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = type.Create(MessageKey, Sender, Receiver, "allocation control");
        var context = new CountingInboxHandlerContext(envelope);
        var baselineState = new HandlerState();
        var structState = new HandlerState();
        var baseline = new DurableInboxDispatcher();
        var withStruct = new DurableInboxDispatcher();
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

    [Fact]
    public void RegisterHandlers_RejectsNullInboxBeforeConfiguration()
    {
        var configured = false;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            DurableInboxExtensions.RegisterHandlers(null!, _ => configured = true));

        Assert.Equal("inbox", exception.ParamName);
        Assert.False(configured);
    }

    [Fact]
    public void RegisterHandlers_RejectsNullConfigurationWithoutInstallingHandler()
    {
        var inbox = Substitute.For<IDurableInbox>();

        var exception = Assert.Throws<ArgumentNullException>(() => inbox.RegisterHandlers(null!));

        Assert.Equal("configure", exception.ParamName);
        inbox.DidNotReceive().RegisterHandler(Arg.Any<IInboxHandler>());
    }

    [Fact]
    public void RegisterHandlers_RejectsEmptyConfigurationWithoutInstallingHandler()
    {
        var inbox = Substitute.For<IDurableInbox>();

        var exception = Assert.Throws<InvalidOperationException>(() => inbox.RegisterHandlers(_ => { }));

        Assert.Contains("at least one", exception.Message, StringComparison.Ordinal);
        inbox.DidNotReceive().RegisterHandler(Arg.Any<IInboxHandler>());
    }

    [Fact]
    public void RegisterHandlers_ConfigurationFailurePreservesOriginalErrorAndInbox()
    {
        using var services = CreateServices();
        var type = new DurableMessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var inbox = Substitute.For<IDurableInbox>();
        var sentinel = new InvalidDataException("configuration failed");

        var exception = Assert.Throws<InvalidDataException>(() => inbox.RegisterHandlers(routes =>
        {
            routes.Register(type, (_, _, _) => ValueTask.CompletedTask);
            throw sentinel;
        }));

        Assert.Same(sentinel, exception);
        inbox.DidNotReceive().RegisterHandler(Arg.Any<IInboxHandler>());
    }

    [Fact]
    public void RegisterHandlers_DuplicateSubjectFailsBeforeInstallingHandler()
    {
        using var services = CreateServices();
        var first = new DurableMessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var second = new DurableMessageType<int>(Subject, services.GetRequiredService<Serializer<int>>());
        var inbox = Substitute.For<IDurableInbox>();

        Assert.Throws<InvalidOperationException>(() => inbox.RegisterHandlers(routes => routes
            .Register(first, (_, _, _) => ValueTask.CompletedTask)
            .Register(second, (_, _, _) => ValueTask.CompletedTask)));

        inbox.DidNotReceive().RegisterHandler(Arg.Any<IInboxHandler>());
    }

    [Fact]
    public async Task RegisterHandlers_InstallsOneFrozenDispatcherAndRoutesDifferentBodyTypes()
    {
        using var services = CreateServices();
        var text = new DurableMessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var number = new DurableMessageType<int>("stock.restock.v1", services.GetRequiredService<Serializer<int>>());
        var inbox = Substitute.For<IDurableInbox>();
        IInboxHandler handler = null!;
        DurableInboxDispatcher configured = null!;
        inbox.When(value => value.RegisterHandler(Arg.Any<IInboxHandler>())).Do(call => handler = call.Arg<IInboxHandler>());
        var values = new List<object>();
        inbox.RegisterHandlers(routes =>
        {
            configured = routes;
            routes.Register(text, (body, context, _) =>
            {
                values.Add(body);
                context.Complete();
                return ValueTask.CompletedTask;
            }).Register(number, (body, context, _) =>
            {
                values.Add(body);
                context.Complete();
                return ValueTask.CompletedTask;
            });
        });

        Assert.Same(configured, handler);
        Assert.Throws<InvalidOperationException>(() => configured.Register(
            new DurableMessageType<string>("stock.late.v1", services.GetRequiredService<Serializer<string>>()),
            (_, _, _) => ValueTask.CompletedTask));
        using var reservation = DirectEnvelope(services.GetRequiredService<Serializer<string>>(), Subject, "reserve");
        using var restock = DirectEnvelope(services.GetRequiredService<Serializer<int>>(), number.Subject, 5);
        var reservationContext = new CountingInboxHandlerContext(reservation);
        var restockContext = new CountingInboxHandlerContext(restock);
        await handler.HandleAsync(reservationContext, CancellationToken.None);
        await handler.HandleAsync(restockContext, CancellationToken.None);

        Assert.Equal(new object[] { "reserve", 5 }, values);
        Assert.Equal(1, reservationContext.CompletionCount);
        Assert.Equal(1, restockContext.CompletionCount);
        inbox.Received(1).RegisterHandler(configured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisterHandlers_PreservesDeferredOutcomeAndExplicitCompletion(bool fail)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var type = new DurableMessageType<string>(Subject, serializer);
        using var envelope = DirectEnvelope(serializer, Subject, "prepare");
        var context = new CountingInboxHandlerContext(envelope);
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinel = new IOException("preparation failed");
        var inbox = Substitute.For<IDurableInbox>();
        IInboxHandler registered = null!;
        inbox.When(value => value.RegisterHandler(Arg.Any<IInboxHandler>())).Do(call => registered = call.Arg<IInboxHandler>());
        inbox.RegisterHandlers(routes => routes.Register(type, async (body, caller, token) =>
        {
            Assert.Equal("prepare", body);
            Assert.Same(context, caller);
            Assert.Equal(cancellation.Token, token);
            await release.Task;
            caller.Complete();
        }));

        var pending = registered.HandleAsync(context, cancellation.Token);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, context.CompletionCount);
        if (fail)
        {
            release.SetException(sentinel);
            var exception = await Assert.ThrowsAsync<IOException>(async () => await pending);
            Assert.Same(sentinel, exception);
            Assert.Equal(0, context.CompletionCount);
        }
        else
        {
            release.SetResult();
            await pending;
            Assert.Equal(1, context.CompletionCount);
        }
    }

    [Fact]
    public void RegisterHandlers_PropagatesCoreRegistrationRejection()
    {
        using var services = CreateServices();
        var type = new DurableMessageType<string>(Subject, services.GetRequiredService<Serializer<string>>());
        var inbox = Substitute.For<IDurableInbox>();
        var sentinel = new InvalidOperationException("A handler is already registered.");
        inbox.When(value => value.RegisterHandler(Arg.Any<IInboxHandler>())).Do(_ => throw sentinel);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            inbox.RegisterHandlers(routes => routes.Register(type, (_, _, _) => ValueTask.CompletedTask)));

        Assert.Same(sentinel, exception);
        inbox.Received(1).RegisterHandler(Arg.Any<DurableInboxDispatcher>());
    }

    private static ServiceProvider CreateServices() =>
        new ServiceCollection().AddSerializer().BuildServiceProvider();

    private static void AssertClosedSerializerFactory<T>(T body)
    {
        var services = new ServiceCollection();
        services.AddDurableMessageType<T>(Subject).AddDurableMessageType<T>("orders.cancel");
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
        var first = provider.GetRequiredKeyedService<DurableMessageType<T>>(Subject);
        var second = provider.GetRequiredKeyedService<DurableMessageType<T>>("orders.cancel");
        using var envelope = first.Create(MessageKey, Sender, Receiver, body);
        using var other = second.Create(MessageKey, Sender, Receiver, body);
        Assert.Equal(serializer.SerializeToArray(body), envelope.Payload.ToArray());
        Assert.Equal(body, first.Decode(envelope));
        Assert.Equal(body, second.Decode(other));
        AssertEnvelopeIdentity(envelope, MessageKey, Sender, Receiver, Subject);
        AssertEnvelopeIdentity(other, MessageKey, Sender, Receiver, "orders.cancel");
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

        services.AddDurableMessageType<T>(Subject).AddDurableMessageType<T>("orders.cancel");

        Assert.Same(original, Assert.Single(services, descriptor =>
            !descriptor.IsKeyedService && descriptor.ServiceType == typeof(Serializer<T>)));
        Assert.Equal(0, factoryCalls);
        // No untyped Serializer service is installed here. Replacing this closed
        // custom registration with the default factory would fail resolution.
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(custom, provider.GetRequiredService<Serializer<T>>());
        Assert.Same(custom, scope.ServiceProvider.GetRequiredService<Serializer<T>>());
        var first = provider.GetRequiredKeyedService<DurableMessageType<T>>(Subject);
        var second = provider.GetRequiredKeyedService<DurableMessageType<T>>("orders.cancel");
        using var envelope = first.Create(MessageKey, Sender, Receiver, body);
        using var other = second.Create(MessageKey, Sender, Receiver, body);
        Assert.Equal(2, probe.WriteCount);
        Assert.Equal(codecServices.GetRequiredService<Serializer<T>>().SerializeToArray(body), envelope.Payload.ToArray());
        Assert.Equal(body, first.Decode(envelope));
        Assert.Equal(body, second.Decode(other));
        Assert.Equal(2, probe.ReadCount);
        Assert.Equal(factory ? 1 : 0, factoryCalls);
        AssertEnvelopeIdentity(envelope, MessageKey, Sender, Receiver, Subject);
        AssertEnvelopeIdentity(other, MessageKey, Sender, Receiver, "orders.cancel");
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

    private static long MeasureDispatchAllocations(DurableInboxDispatcher dispatcher, IInboxHandlerContext context, int count)
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

    private static DurableEnvelope DirectEnvelope<T>(Serializer<T> serializer, string subject, [AllowNull] T body)
    {
        using var writer = new ArcBufferWriter();
        serializer.Serialize(body, writer);
        // ConsumeSlice pins a new owner before the temporary writer is released.
        return Envelope(subject, writer.ConsumeSlice(writer.Length));
    }

    private static DurableEnvelope WireEnvelope(string subject, byte[] wire)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(wire);
        return Envelope(subject, writer.ConsumeSlice(writer.Length));
    }

    private static DurableEnvelope Envelope(string subject, ArcBuffer payload) => new()
    {
        MessageId = HierarchicalKey.Create("orders","42","reserve"),
        SenderId = Sender,
        ReceiverId = Receiver,
        Subject = subject,
        Payload = payload
    };

    private static void AssertEnvelopeIdentity(
        DurableEnvelope envelope, HierarchicalKey key, GrainId sender, GrainId receiver, string subject)
    {
        Assert.Equal(key, envelope.MessageId);
        Assert.Equal(sender, envelope.SenderId);
        Assert.Equal(receiver, envelope.ReceiverId);
        Assert.Equal(subject, envelope.Subject);
    }

    private static void AssertCreateWire<T>(
        IServiceProvider sendingServices, IServiceProvider receivingServices, T body)
    {
        var sending = sendingServices.GetRequiredService<Serializer<T>>();
        var receiving = receivingServices.GetRequiredService<Serializer<T>>();
        var messageType = new DurableMessageType<T>(Subject, sending);
        var key = MessageKey;
        var expectedWire = sending.SerializeToArray(body);
        using var envelope = messageType.Create(key, Sender, Receiver, body);
        Assert.Equal(expectedWire, envelope.Payload.ToArray());
        Assert.Equal(body, receiving.Deserialize(envelope.Payload));
        AssertEnvelopeIdentity(envelope, key, Sender, Receiver, Subject);
    }

    private static void AssertUnchangedServices(ServiceDescriptor[] before, IServiceCollection after)
    {
        Assert.Equal(before.Length, after.Count);
        for (var i = 0; i < before.Length; i++)
        {
            Assert.Same(before[i], after[i]);
        }
    }

    private static int CountSegments(ArcBuffer buffer)
    {
        var result = 0;
        foreach (var segment in buffer)
        {
            Assert.False(segment.IsEmpty);
            result++;
        }

        return result;
    }

    private sealed class CountingInboxHandlerContext(DurableEnvelope envelope) : IInboxHandlerContext
    {
        // Borrowed struct copy. Only the test's original envelope is disposed.
        public DurableEnvelope Envelope { get; } = envelope;
        public int CompletionCount { get; private set; }
        public void Complete() => CompletionCount++;
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
            return LastDecoded = new OwnedResource(name);
        }
    }
}
