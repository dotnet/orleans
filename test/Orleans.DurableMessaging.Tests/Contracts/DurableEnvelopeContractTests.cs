using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableEnvelopeContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(100000)]
    public void EnvelopeSerializer_RoundTripsOpaquePayloadAcrossIndependentProviders(int length)
    {
        var expected = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        using var writer = new ArcBufferWriter();
        writer.Write(expected);
        using var envelope = Envelope(writer.PeekSlice(writer.Length));
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = sendingServices.GetRequiredService<Serializer<DurableEnvelope>>();
        var wire = serializer.SerializeToArray(envelope);
        using var decoded = receivingServices.GetRequiredService<Serializer<DurableEnvelope>>().Deserialize(wire);
        AssertEnvelope(decoded, expected);
        AssertEnvelope(envelope, expected);
        AssertEnvelope(envelope, expected); // Serialization must not consume the caller owner.
        Assert.Equal(wire, serializer.SerializeToArray(envelope));
    }

    [Fact]
    public void EnvelopeDeepCopy_RetainsIndependentPayloadOwner()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0x00, 0xff, 0x80, 0xea });
        var envelope = Envelope(writer.PeekSlice(writer.Length));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var copied = services.GetRequiredService<DeepCopier>().Copy(envelope);
        Assert.Same(envelope.Payload.First, copied.Payload.First);
        envelope.Dispose();
        writer.Dispose();
        AssertEnvelope(copied, [0x00, 0xff, 0x80, 0xea]);
    }

    [Fact]
    public void EnvelopeSerializer_RepeatedPayloads_ProduceIndependentOwners()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0xff, 0x00, 0x81 });
        using var first = Envelope(writer.PeekSlice(writer.Length));
        using var second = first.Retain() with
        {
            MessageId = HierarchicalKey.Create("32222222-2222-2222-2222-222222222222"),
            ReceiverId = GrainId.Create("receiver", "other")
        };
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var wire = sendingServices.GetRequiredService<Serializer<DurableEnvelope[]>>().SerializeToArray([first, second]);
        var decoded = receivingServices.GetRequiredService<Serializer<DurableEnvelope[]>>().Deserialize(wire)!;
        try
        {
            Assert.Equal(2, decoded.Length);
            AssertEnvelope(decoded[0], [0xff, 0x00, 0x81]);
            Assert.Equal(second.MessageId, decoded[1].MessageId);
            Assert.Equal(first.SenderId, decoded[1].SenderId);
            Assert.Equal(second.ReceiverId, decoded[1].ReceiverId);
            decoded[0].Dispose();
            Assert.Equal(new byte[] { 0xff, 0x00, 0x81 }, decoded[1].Payload.ToArray());
        }
        finally
        {
            // decoded[0] was consumed above. Each result has its own lifetime.
            decoded[1].Dispose();
        }
    }

    [Theory]
    [InlineData("DurableEnvelope")]
    [InlineData("InboxDeadLetter")]
    [InlineData("OutboxDeadLetter")]
    public void PartialDeserialization_ReleasesPayloadReadBeforeMalformedTail(string contract)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var payloadWriter = new ArcBufferWriter();
        payloadWriter.Write(new byte[] { 0x00, 0xff, 0x80 });
        using var envelope = Envelope(payloadWriter.PeekSlice(payloadWriter.Length));
        object value = envelope;
        if (contract != "DurableEnvelope")
        {
            var type = typeof(DurableEnvelope).Assembly.GetType($"Orleans.DurableMessaging.{contract}", throwOnError: true)!;
            value = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetProperty("Envelope")!.SetValue(value, envelope);
            type.GetProperty("DeadLetteredAt")!.SetValue(value, DateTimeOffset.UtcNow);
            type.GetProperty("Reason")!.SetValue(value, "failure");
            type.GetProperty("AttemptCount")!.SetValue(value, 1);
        }
        var serializer = services.GetRequiredService<Serializer>();
        var encoded = serializer.SerializeToArray(value);
        using var wireWriter = new ArcBufferWriter();
        wireWriter.Write(encoded);
        using var truncated = wireWriter.PeekSlice(wireWriter.Length - 1);
        var refs = typeof(ArcBufferPage).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var before = (int)refs.GetValue(truncated.First)!;
        Assert.ThrowsAny<Exception>(() => serializer.Deserialize<object>(truncated));
        Assert.Equal(before, (int)refs.GetValue(truncated.First)!);
        AssertEnvelope(envelope, [0x00, 0xff, 0x80]);
    }

    [Fact]
    public void OpaqueContracts_ExposeExactPublicSurfaceAndContiguousIds()
    {
        Assert.True(typeof(DurableEnvelope).IsPublic);
        Assert.True(typeof(DurableEnvelope).IsValueType);
        Assert.Single(typeof(DurableEnvelope).GetCustomAttributes<IsReadOnlyAttribute>());
        Assert.Empty(typeof(DurableEnvelope).GetCustomAttributes<GenerateSerializerAttribute>());
        Assert.Equal("Orleans.DurableMessaging.DurableEnvelope", Assert.Single(
            typeof(DurableEnvelope).GetCustomAttributes<AliasAttribute>()).Alias);
        AssertSurface(typeof(DurableEnvelope),
            Property("MessageId", typeof(HierarchicalKey), true),
            Property("SenderId", typeof(GrainId), true),
            Property("ReceiverId", typeof(GrainId), true),
            Property("Payload", typeof(ArcBuffer), true),
            Property("Subject", typeof(string), true),
            Method("Retain", typeof(DurableEnvelope)), Method("Dispose", typeof(void)));
        AssertIds(typeof(DurableEnvelope), ("MessageId", 0u), ("SenderId", 1u), ("ReceiverId", 2u), ("Payload", 3u), ("Subject", 4u));
        foreach (var property in typeof(DurableEnvelope).GetProperties())
        {
            Assert.Single(property.GetCustomAttributes<RequiredMemberAttribute>());
            Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }

        Assert.False(typeof(IInboxHandler).IsGenericType);
        AssertSurface(typeof(IInboxHandler),
            Method("HandleAsync", typeof(ValueTask), typeof(IInboxHandlerContext), typeof(CancellationToken)));
        AssertSurface(typeof(IInboxHandlerContext),
            Property("Envelope", typeof(DurableEnvelope)), Method("Complete", typeof(void)));
        AssertSurface(typeof(IDurableInbox),
            Property("Count", typeof(int)), Property("Capacity", typeof(int)),
            Property("Messages", typeof(IEnumerable<DurableEnvelope>)),
            Method("RegisterHandler", typeof(void), typeof(IInboxHandler)),
            Method("TryGetMessage", typeof(bool), typeof(HierarchicalKey), typeof(DurableEnvelope).MakeByRefType()));
        AssertSurface(typeof(IDurableOutbox),
            Property("Count", typeof(int)), Property("Messages", typeof(IEnumerable<DurableEnvelope>)),
            Method("Send", typeof(void), typeof(DurableEnvelope)),
            Method("TryGetMessage", typeof(bool), typeof(HierarchicalKey), typeof(DurableEnvelope).MakeByRefType()));
        Assert.True(Assert.Single(typeof(IDurableInbox).GetMethod("TryGetMessage")!.GetParameters(), p => p.ParameterType.IsByRef).IsOut);
        Assert.True(Assert.Single(typeof(IDurableOutbox).GetMethod("TryGetMessage")!.GetParameters(), p => p.ParameterType.IsByRef).IsOut);

        Assert.Equal(new[] { "Accepted:0", "Backpressured:2", "DeadLettered:4", "Duplicate:1", "HandlerNotFound:3" },
            Enum.GetNames<DeliveryStatus>().Select(name => $"{name}:{(int)Enum.Parse<DeliveryStatus>(name)}").Order(StringComparer.Ordinal));
        AssertIds(typeof(DeliveryResult), ("Status", 0u), ("Message", 1u));
        AssertSurface(typeof(DeliveryResult), Property("Status", typeof(DeliveryStatus), true), Property("Message", typeof(string), true),
            StaticMethod("Accepted", typeof(DeliveryResult)), StaticMethod("Duplicate", typeof(DeliveryResult)),
            StaticMethod("Backpressured", typeof(DeliveryResult)), StaticMethod("HandlerNotFound", typeof(DeliveryResult)),
            StaticMethod("DeadLettered", typeof(DeliveryResult), typeof(string)));

        Assert.True(typeof(ArcBuffer).IsPublic);
        Assert.True(typeof(ArcBuffer).IsValueType);
        Assert.Contains(typeof(IDisposable), typeof(ArcBuffer).GetInterfaces());

    }

    [Theory]
    [InlineData("id", 1024, true)]
    [InlineData("id", 1025, false)]
    [InlineData("subject", 256, true)]
    [InlineData("subject", 257, false)]
    [InlineData("depth", 32, true)]
    [InlineData("depth", 33, false)]
    public void Admission_IdentityAndSubjectLimits_EnforceExactBoundaries(string field, int length, bool valid)
    {
        using var envelope = Envelope(default) with
        {
            MessageId = field switch
            {
                "id" => HierarchicalKey.Create(new string('x', length)),
                "depth" => HierarchicalKey.Create(Enumerable.Repeat("x", length).ToArray()),
                _ => HierarchicalKey.Create("command")
            },
            Subject = field == "subject" ? new string('s', length) : "contract.v1"
        };
        if (valid)
        {
            Validate(envelope);
            Assert.True(envelope.Payload.IsEmpty);
        }
        else
        {
            var error = Assert.Throws<ArgumentException>(() => Validate(envelope));
            Assert.Contains(field == "depth" ? "segments" : "UTF-8 bytes", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("id", 512, true)]
    [InlineData("id", 513, false)]
    [InlineData("subject", 128, true)]
    [InlineData("subject", 129, false)]
    public void Admission_CountsUTF8BytesRatherThanUTF16Characters(string field, int repetitions, bool valid)
    {
        var unicode = new string((char)0xE9, repetitions);
        using var envelope = Envelope(default) with
        {
            MessageId = field == "id" ? HierarchicalKey.Create(unicode) : HierarchicalKey.Create("command"),
            Subject = field == "subject" ? unicode : "contract.v1"
        };
        if (valid) Validate(envelope);
        else Assert.Throws<ArgumentException>(() => Validate(envelope));
        Assert.Equal(repetitions * 2, System.Text.Encoding.UTF8.GetByteCount(unicode));
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void Admission_CountsEscapedCanonicalKeyBytes(int repetitions, bool valid)
    {
        using var envelope = Envelope(default) with { MessageId = HierarchicalKey.Create(new string('/', repetitions)) };
        Assert.Equal(repetitions * 2, envelope.MessageId.Length);
        Assert.Equal(1, envelope.MessageId.SegmentCount);
        if (valid) Validate(envelope);
        else Assert.Throws<ArgumentException>(() => Validate(envelope));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("sender")]
    [InlineData("receiver")]
    [InlineData("empty-subject")]
    [InlineData("null-subject")]
    [InlineData("invalid-unicode-id")]
    [InlineData("invalid-unicode-subject")]
    public void Admission_InvalidMetadata_RejectsBeforePayloadUse(string field)
    {
        using var envelope = Envelope(default);
        var invalid = field switch
        {
            "id" => envelope with { MessageId = default },
            "sender" => envelope with { SenderId = default },
            "receiver" => envelope with { ReceiverId = default },
            "empty-subject" => envelope with { Subject = "" },
            "null-subject" => envelope with { Subject = null! },
            "invalid-unicode-id" => envelope with { MessageId = HierarchicalKey.Create(new string((char)0xD800, 1)) },
            "invalid-unicode-subject" => envelope with { Subject = new string((char)0xD800, 1) },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.ThrowsAny<ArgumentException>(() => Validate(invalid));
        Assert.Equal("contract.v1", envelope.Subject);
        Assert.False(envelope.MessageId.IsDefault);
        Assert.True(envelope.Payload.IsEmpty);
    }

    [Theory]
    [InlineData("subject.v1")]
    [InlineData("Subject.V1")]
    [InlineData(" subject.v1 ")]
    [InlineData(" ")]
    public void ValidateSubject_PreservesExactOrdinalSpelling(string subject)
    {
        InvokeInternal("DurableEnvelopeValidation", "ValidateSubject", subject);
        using var envelope = Envelope(default) with { Subject = subject };
        Validate(envelope);
        Assert.Equal(subject, envelope.Subject);
    }

    [Fact]
    public void KeyGeneralUtility_RemainsUnboundedByTransportAdmissionPolicy()
    {
        var key = HierarchicalKey.Create(Enumerable.Repeat(new string('x', 1024), 33).ToArray());
        Assert.Equal(33, key.SegmentCount);
        Assert.Equal(33 * 1024 + 32, key.Length);
        Assert.Equal(key, HierarchicalKey.Parse(key.ToString()));
        using var envelope = Envelope(default) with { MessageId = key };
        Assert.Throws<ArgumentException>(() => Validate(envelope));
    }

    [Theory]
    [InlineData("same", true, true)]
    [InlineData("id", false, false)]
    [InlineData("child", false, false)]
    [InlineData("sender", false, true)]
    [InlineData("receiver", false, false)]
    [InlineData("subject", false, false)]
    [InlineData("subject-case", false, false)]
    [InlineData("payload", false, false)]
    [InlineData("payload-length", false, false)]
    public void Equivalence_PreservesOutboxIntentAndSenderIndependentPendingCommand(string change, bool outbox, bool inbox)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0, 255, 128 });
        using var original = Envelope(writer.PeekSlice(writer.Length));
        using var otherWriter = new ArcBufferWriter();
        otherWriter.Write(change switch { "payload" => new byte[] { 0, 255, 129 }, "payload-length" => new byte[] { 0, 255 }, _ => new byte[] { 0, 255, 128 } });
        using var repeated = original with
        {
            Payload = otherWriter.PeekSlice(otherWriter.Length),
            MessageId = change == "id" ? HierarchicalKey.Create("other") : change == "child" ? original.MessageId.CreateChildKey("child") : original.MessageId,
            SenderId = change == "sender" ? GrainId.Create("sender", "forwarder") : original.SenderId,
            ReceiverId = change == "receiver" ? GrainId.Create("receiver", "other") : original.ReceiverId,
            Subject = change == "subject" ? "other.v1" : change == "subject-case" ? "Contract.v1" : original.Subject
        };
        Assert.Equal(outbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreEquivalent", original, repeated));
        Assert.Equal(inbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreSameCommand", original, repeated));
        Assert.Equal(outbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreEquivalent", repeated, original));
        Assert.Equal(inbox, InvokeInternal<bool>("DurableEnvelopeEquivalence", "AreSameCommand", repeated, original));
        Assert.Equal(new byte[] { 0, 255, 128 }, original.Payload.ToArray());
    }

    [Theory]
    [InlineData("Envelope")]
    [InlineData("InboxDeadLetter")]
    [InlineData("OutboxDeadLetter")]
    public void CurrentIdentityAndSubject_RoundTripInSharedDeadLetterContracts(string contract)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0x00, 0xFF, 0x81 });
        using var envelope = Envelope(writer.PeekSlice(writer.Length)) with
        {
            MessageId = HierarchicalKey.Create("tenant", "orders/42", "reserve"),
            Subject = "inventory.reserve.v1"
        };
        object value = envelope;
        if (contract != "Envelope")
        {
            var type = typeof(DurableEnvelope).Assembly.GetType($"Orleans.DurableMessaging.{contract}", throwOnError: true)!;
            value = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetProperty("Envelope")!.SetValue(value, envelope);
            type.GetProperty("DeadLetteredAt")!.SetValue(value, DateTimeOffset.UnixEpoch);
            type.GetProperty("Reason")!.SetValue(value, "poison");
            type.GetProperty("AttemptCount")!.SetValue(value, 3);
        }
        var serializer = services.GetRequiredService<Serializer>();
        var decoded = serializer.Deserialize<object>(serializer.SerializeToArray(value));
        Assert.NotNull(decoded);
        Assert.IsType(value.GetType(), decoded);
        using var actual = contract == "Envelope" ? Assert.IsType<DurableEnvelope>(decoded)
            : Assert.IsType<DurableEnvelope>(decoded.GetType().GetProperty("Envelope")!.GetValue(decoded));
        Assert.Equal(envelope.MessageId, actual.MessageId);
        Assert.Equal(@"tenant/orders\/42/reserve", actual.MessageId.ToString());
        Assert.Equal(envelope.Subject, actual.Subject);
        Assert.Equal(envelope.SenderId, actual.SenderId);
        Assert.Equal(envelope.ReceiverId, actual.ReceiverId);
        Assert.Equal(new byte[] { 0, 255, 129 }, actual.Payload.ToArray());
        Assert.Equal(new byte[] { 0, 255, 129 }, envelope.Payload.ToArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    public void EnvelopeCodec_MissingOrNullSubject_ReleasesPayloadReadBeforeRejection(string subject)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var payloadWriter = new ArcBufferWriter();
        payloadWriter.Write(new byte[] { 0x00, 0xFF, 0x80 });
        using var envelope = Envelope(payloadWriter.PeekSlice(payloadWriter.Length));
        using var session = services.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>().GetSession();
        using var output = new ArcBufferWriter();
        var writer = Orleans.Serialization.Buffers.Writer.Create(output, session);
        Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(session);
        writer.WriteFieldHeader(0, typeof(DurableEnvelope), typeof(DurableEnvelope), Orleans.Serialization.WireProtocol.WireType.TagDelimited);
        services.GetRequiredService<Orleans.Serialization.Codecs.IFieldCodec<HierarchicalKey>>()
            .WriteField(ref writer, 0, typeof(HierarchicalKey), envelope.MessageId);
        var grains = services.GetRequiredService<Orleans.Serialization.Codecs.IFieldCodec<GrainId>>();
        grains.WriteField(ref writer, 1, typeof(GrainId), envelope.SenderId);
        grains.WriteField(ref writer, 1, typeof(GrainId), envelope.ReceiverId);
        services.GetRequiredService<Orleans.Serialization.Codecs.IFieldCodec<ArcBuffer>>()
            .WriteField(ref writer, 1, typeof(ArcBuffer), envelope.Payload);
        if (subject == "null")
        {
            services.GetRequiredService<Orleans.Serialization.Codecs.IFieldCodec<string>>()
                .WriteField(ref writer, 1, typeof(string), null!);
        }
        writer.WriteEndObject();
        writer.Commit();
        using var bytes = output.PeekSlice(output.Length);
        var refs = typeof(ArcBufferPage).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var before = Assert.IsType<int>(refs.GetValue(bytes.First));
        var error = Assert.Throws<FormatException>(() => services.GetRequiredService<Serializer<DurableEnvelope>>().Deserialize(bytes));
        Assert.Equal("The envelope subject field is missing or null.", error.Message);
        Assert.Equal(before, Assert.IsType<int>(refs.GetValue(bytes.First)));
        Assert.Equal(new byte[] { 0, 255, 128 }, envelope.Payload.ToArray());
        Assert.Equal("contract.v1", envelope.Subject);
    }

    private static void Validate(DurableEnvelope envelope) => InvokeInternal("DurableEnvelopeValidation", "Validate", envelope);

    private static T InvokeInternal<T>(string typeName, string methodName, params object[] arguments) =>
        Assert.IsType<T>(InvokeInternal(typeName, methodName, arguments));

    private static object? InvokeInternal(string typeName, string methodName, params object[] arguments)
    {
        var type = typeof(DurableEnvelope).Assembly.GetType($"Orleans.DurableMessaging.{typeName}", throwOnError: true)!;
        try
        {
            return type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is { } cause)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
            throw;
        }
    }

    private static void AssertIds(Type type, params (string Name, uint Id)[] expected)
    {
        var actual = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(member => member.GetCustomAttributes<IdAttribute>().Select(id => (member.Name, id.Id)))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(pair => pair.Name, StringComparer.Ordinal), actual);
        Assert.Equal(expected.Length, actual.Select(pair => pair.Id).Distinct().Count());
    }

    private static void AssertSurface(Type type, params string[] expected)
    {
        var types = new[] { type }.Concat(type.GetInterfaces());
        var actual = types.SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(member => member.DeclaringType != typeof(object) && member.DeclaringType != typeof(ValueType))
            .Select(member => member switch
            {
                PropertyInfo property => (property.GetMethod!.IsStatic ? "static " : "") +
                    Property(property.Name, property.PropertyType, property.SetMethod is not null),
                MethodInfo method when !method.IsSpecialName => (method.IsStatic ? "static " : "") +
                    Method(method.Name, method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToArray()) +
                    (method.IsGenericMethod ? $" generic:{method.GetGenericArguments().Length}" : ""),
                MethodInfo => null,
                ConstructorInfo => null,
                _ => $"unexpected:{member.MemberType}:{member.Name}"
            }).Where(signature => signature is not null).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string Property(string name, Type type, bool set = false) =>
        $"property {TypeName(type)} {name} get{(set ? "/set" : "")}";
    private static string Method(string name, Type result, params Type[] parameters) =>
        $"method {TypeName(result)} {name}({string.Join(",", parameters.Select(TypeName))})";
    private static string StaticMethod(string name, Type result, params Type[] parameters) => "static " + Method(name, result, parameters);
    private static string TypeName(Type type) => type.FullName!;

    private static DurableEnvelope Envelope(ArcBuffer payload) => new()
    {
        MessageId = HierarchicalKey.Create("11111111-1111-1111-1111-111111111111"),
        SenderId = GrainId.Create("sender", "contract"),
        ReceiverId = GrainId.Create("receiver", "contract"),
        Subject = "contract.v1",
        Payload = payload
    };

    private static void AssertEnvelope(DurableEnvelope envelope, byte[] expected)
    {
        Assert.Equal(HierarchicalKey.Create("11111111-1111-1111-1111-111111111111"), envelope.MessageId);
        Assert.Equal(GrainId.Create("sender", "contract"), envelope.SenderId);
        Assert.Equal(GrainId.Create("receiver", "contract"), envelope.ReceiverId);
        Assert.Equal("contract.v1", envelope.Subject);
        Assert.Equal(expected.Length, envelope.Payload.Length);
        Assert.Equal(expected, envelope.Payload.ToArray());
        Assert.Equal(expected, envelope.Payload.AsReadOnlySequence().ToArray());
    }

}
