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
        var registration = Assert.Single(services);
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

    [Fact]
    public void DurableMessageWriter_RejectsNullContext()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new DurableMessageWriter(null!));
        Assert.Equal("context", exception.ParamName);
    }

    [Fact]
    public void DurableMessageWriter_CreateRejectsNullMessageTypeBeforeCodecWrite()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var validType = new DurableMessageType<string>(Subject, serializer);
        var context = ActivationContext(Sender);
        using var writer = new DurableMessageWriter(context);

        var exception = Assert.Throws<ArgumentNullException>(
            () => writer.Create<string>(null!, MessageKey, Receiver, "reserve"));

        Assert.Equal("messageType", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        // The probe is not attached to the null descriptor. A real subsequent write
        // is the positive control, not a claim that an unrelated probe proves order.
        using var control = writer.Create(validType, MessageKey, Receiver, "valid control");
        Assert.Equal(1, probe.WriteCount);
        Assert.Equal("valid control", services.GetRequiredService<Serializer<string>>().Deserialize(control.Payload));
        AssertPreparationOnly(context);
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("receiver")]
    [InlineData("messageId")]
    [InlineData("key-segments")]
    [InlineData("key-ascii-bytes")]
    [InlineData("key-multibyte-bytes")]
    public void DurableMessageWriter_CreateRejectsInvalidIdentitiesBeforeCodecWrite(string invalidIdentity)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var context = ActivationContext(invalidIdentity == "sender" ? default : Sender);
        using var writer = new DurableMessageWriter(context);
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
                result = writer.Create(messageType, key, receiver, "must not encode");
            });
            Assert.Equal("envelope", exception.ParamName);
            Assert.Null(result);
            Assert.Equal(0, probe.WriteCount);
            AssertPreparationOnly(context);
        }
        finally
        {
            // Also release an unexpected owner if a validation mutation succeeds.
            result?.Dispose();
        }
    }

    [Fact]
    public void DurableMessageWriter_CreateUsesActivationIdentityAndSuppliedHierarchicalKey()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<string>>();
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var context = ActivationContext(Sender);
        using var writer = new DurableMessageWriter(context);
        var key = HierarchicalKey.Create("orders","42","reserve");
        using var first = writer.Create(messageType, key, Receiver, "reserve first");
        var otherReceiver = GrainId.Create("receiver", "other-warehouse");
        using var second = writer.Create(messageType, key, otherReceiver, "reserve second");

        AssertEnvelopeIdentity(first, key, Sender, Receiver, Subject);
        AssertEnvelopeIdentity(second, key, Sender, otherReceiver, Subject);
        Assert.Equal("orders/42/reserve", first.MessageId.ToString());
        Assert.Equal("orders/42/reserve", second.MessageId.ToString());
        Assert.Equal(3, first.MessageId.SegmentCount);
        Assert.Equal(3, second.MessageId.SegmentCount);
        Assert.Equal("reserve first", serializer.Deserialize(first.Payload));
        Assert.Equal("reserve second", serializer.Deserialize(second.Payload));
        AssertPreparationOnly(context);
    }

    [Theory]
    [InlineData("segments")]
    [InlineData("ascii-bytes")]
    [InlineData("multibyte-bytes")]
    public void DurableMessageWriter_CreateAcceptsMessageKeyAtAdmissionLimits(string variation)
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var context = ActivationContext(Sender);
        using var writer = new DurableMessageWriter(context);
        var key = variation switch
        {
            "segments" => HierarchicalKey.Create(Enumerable.Repeat("s", 32).ToArray()),
            "ascii-bytes" => HierarchicalKey.Create(new string('k', 1024)),
            "multibyte-bytes" => HierarchicalKey.Create(new string('\u20ac', 341) + "a"),
            _ => throw new ArgumentOutOfRangeException(nameof(variation))
        };

        using var envelope = writer.Create(messageType, key, Receiver, "admitted boundary");

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
        AssertPreparationOnly(context);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("empty-string")]
    [InlineData("int")]
    [InlineData("negative-int")]
    [InlineData("bytes")]
    [InlineData("empty-bytes")]
    public void DurableMessageWriter_CreateMatchesDirectSerializerWireFormat(string contract)
    {
        using var services = CreateServices();
        using var receivingServices = CreateServices();
        var context = ActivationContext(Sender);
        using var writer = new DurableMessageWriter(context);
        switch (contract)
        {
            case "string":
                AssertWriterWire(writer, services, receivingServices, "reserve €42 / 東京");
                break;
            case "empty-string":
                AssertWriterWire(writer, services, receivingServices, "");
                break;
            case "int":
                AssertWriterWire(writer, services, receivingServices, 42_017);
                break;
            case "negative-int":
                AssertWriterWire(writer, services, receivingServices, -709);
                break;
            case "bytes":
                AssertWriterWire(writer, services, receivingServices, new byte[] { 0, 255, 128, 17, 42 });
                break;
            case "empty-bytes":
                AssertWriterWire(writer, services, receivingServices, Array.Empty<byte>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contract));
        }

        AssertPreparationOnly(context);
    }

    [Fact]
    public void DurableMessageWriter_CreatePreservesIndependentSlicesAcrossReuseAndDisposal()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<byte[]>>();
        var messageType = new DurableMessageType<byte[]>(Subject, serializer);
        var context = ActivationContext(Sender);
        var writer = new DurableMessageWriter(context);
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
            first = writer.Create(messageType, MessageKey, Receiver, firstBody);
            large = writer.Create(messageType, MessageKey, Receiver, largeBody);
            // Public span enumeration proves actual segmentation, not an assumed
            // PageCount API or a guessed relationship between body size and capacity.
            Assert.True(CountSegments(large.Value.Payload) > 1);
            Assert.True(largeBody.Length > ArcBufferWriter.MinimumPageSize);
            Assert.Equal(serializer.SerializeToArray(firstBody), first.Value.Payload.ToArray());
            first.Value.Dispose();
            first = null;

            last = writer.Create(messageType, MessageKey, Receiver, lastBody);
            writer.Dispose();

            Assert.Equal(largeWire, large.Value.Payload.ToArray());
            Assert.Equal(lastWire, last.Value.Payload.ToArray());
            Assert.Equal(largeBody, serializer.Deserialize(large.Value.Payload));
            Assert.Equal(lastBody, serializer.Deserialize(last.Value.Payload));
            AssertEnvelopeIdentity(large.Value, MessageKey, Sender, Receiver, Subject);
            AssertEnvelopeIdentity(last.Value, MessageKey, Sender, Receiver, Subject);
            AssertPreparationOnly(context);
        }
        finally
        {
            first?.Dispose();
            large?.Dispose();
            last?.Dispose();
            writer.Dispose();
        }
    }

    [Fact]
    public void DurableMessageWriter_CreateResetsCommittedPartialOutputAfterCodecFailure()
    {
        using var services = CreateServices();
        var pristine = services.GetRequiredService<Serializer<string>>();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var context = ActivationContext(Sender);
        using var writer = new DurableMessageWriter(context);
        using var outstanding = writer.Create(messageType, MessageKey, Receiver, "outstanding € order");
        var outstandingWire = pristine.SerializeToArray("outstanding € order");
        var sentinel = new InvalidDataException("committed partial codec failure");
        probe.NextWriteFailure = sentinel;
        DurableEnvelope? failedResult = null;
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
            {
                failedResult = writer.Create(messageType, MessageKey, Receiver, "failing preparation");
            });
            Assert.Same(sentinel, exception);
            Assert.Null(failedResult);
            Assert.Equal(2, probe.WriteCount);
            Assert.Equal(1, probe.CommittedFailureCount);
            Assert.Equal((byte)0x7e, probe.CommittedFailureByte);

            using var recovered = writer.Create(messageType, MessageKey, Receiver, "recovered 東京 reserve");
            Assert.Equal(3, probe.WriteCount);
            Assert.Equal(pristine.SerializeToArray("recovered 東京 reserve"), recovered.Payload.ToArray());
            Assert.Equal("recovered 東京 reserve", pristine.Deserialize(recovered.Payload));
            Assert.Equal(outstandingWire, outstanding.Payload.ToArray());
            Assert.Equal("outstanding € order", pristine.Deserialize(outstanding.Payload));
            AssertEnvelopeIdentity(recovered, MessageKey, Sender, Receiver, Subject);
            AssertPreparationOnly(context);
        }
        finally
        {
            failedResult?.Dispose();
        }
    }

    [Fact]
    public void DurableMessageWriter_DisposeIsIdempotentAndCreateRejectsDisposedWriterBeforeCodecWrite()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var context = ActivationContext(Sender);
        using var writer = new DurableMessageWriter(context);
        using var surviving = writer.Create(messageType, MessageKey, Receiver, "surviving reserve");
        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => writer.Create(messageType, MessageKey, Receiver, "must not encode"));

        Assert.Equal(1, probe.WriteCount);
        Assert.Equal("surviving reserve", services.GetRequiredService<Serializer<string>>().Deserialize(surviving.Payload));
        AssertPreparationOnly(context);
    }

    [Fact]
    public void DurableMessageWriter_CreateRejectsNullBodyBeforeCodecWrite()
    {
        using var services = CreateServices();
        var (serializer, probe) = CreateProbedSerializer<string>(services);
        var messageType = new DurableMessageType<string>(Subject, serializer);
        var context = ActivationContext(Sender);
        using var writer = new DurableMessageWriter(context);

        var exception = Assert.Throws<ArgumentNullException>(
            () => writer.Create(messageType, MessageKey, Receiver, null!));

        Assert.Equal("body", exception.ParamName);
        Assert.Equal(0, probe.WriteCount);
        using var control = writer.Create(messageType, MessageKey, Receiver, "nonnull recovery");
        Assert.Equal(1, probe.WriteCount);
        Assert.Equal("nonnull recovery", services.GetRequiredService<Serializer<string>>().Deserialize(control.Payload));
        AssertPreparationOnly(context);
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

        var exception = Assert.Throws<ArgumentNullException>(() => dispatcher.Register(messageType, null!));

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

    private static ServiceProvider CreateServices() =>
        new ServiceCollection().AddSerializer().BuildServiceProvider();

    private static string InvalidSubject(string variation) => variation switch
    {
        "empty" => "",
        "ascii" => new string('s', 257),
        "multibyte" => new string('\u20ac', 85) + "ab",
        _ => throw new ArgumentOutOfRangeException(nameof(variation))
    };

    private static string SubjectAtLimit(bool multibyte) =>
        multibyte ? new string('\u20ac', 85) + "a" : new string('s', 256);

    private static IGrainContext ActivationContext(GrainId sender)
    {
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(sender);
        context.ClearReceivedCalls();
        return context;
    }

    private static void AssertPreparationOnly(IGrainContext context)
    {
        // Create accepts only activation identity; it has no Send/Complete seam.
        // Check actual activation interactions rather than an unrelated outbox spy.
        var call = Assert.Single(context.ReceivedCalls());
        Assert.Equal("get_GrainId", call.GetMethodInfo().Name);
        Assert.Empty(call.GetArguments());
    }

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

    private static void AssertWriterWire<T>(
        DurableMessageWriter writer, IServiceProvider sendingServices, IServiceProvider receivingServices, T body)
    {
        var sending = sendingServices.GetRequiredService<Serializer<T>>();
        var receiving = receivingServices.GetRequiredService<Serializer<T>>();
        var messageType = new DurableMessageType<T>(Subject, sending);
        var key = MessageKey;
        var expectedWire = sending.SerializeToArray(body);
        using var envelope = writer.Create(messageType, key, Receiver, body);
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
            return inner.ReadValue(ref reader, field);
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
