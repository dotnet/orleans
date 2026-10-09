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
            MessageId = Guid.Parse("32222222-2222-2222-2222-222222222222"),
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
            Property("MessageId", typeof(Guid), true),
            Property("SenderId", typeof(GrainId), true),
            Property("ReceiverId", typeof(GrainId), true),
            Property("Payload", typeof(ArcBuffer), true),
            Method("Retain", typeof(DurableEnvelope)), Method("Dispose", typeof(void)));
        AssertIds(typeof(DurableEnvelope), ("MessageId", 0u), ("SenderId", 1u), ("ReceiverId", 2u), ("Payload", 3u));
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
            Method("TryGetMessage", typeof(bool), typeof(GrainId), typeof(Guid), typeof(DurableEnvelope).MakeByRefType()));
        AssertSurface(typeof(IDurableOutbox),
            Property("Count", typeof(int)), Property("Messages", typeof(IEnumerable<DurableEnvelope>)),
            Method("Send", typeof(void), typeof(DurableEnvelope)),
            Method("TryGetMessage", typeof(bool), typeof(Guid), typeof(DurableEnvelope).MakeByRefType()));
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
        MessageId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        SenderId = GrainId.Create("sender", "contract"),
        ReceiverId = GrainId.Create("receiver", "contract"),
        Payload = payload
    };

    private static void AssertEnvelope(DurableEnvelope envelope, byte[] expected)
    {
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), envelope.MessageId);
        Assert.Equal(GrainId.Create("sender", "contract"), envelope.SenderId);
        Assert.Equal(GrainId.Create("receiver", "contract"), envelope.ReceiverId);
        Assert.Equal(expected.Length, envelope.Payload.Length);
        Assert.Equal(expected, envelope.Payload.ToArray());
        Assert.Equal(expected, envelope.Payload.AsReadOnlySequence().ToArray());
    }

}
