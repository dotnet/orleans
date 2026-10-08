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
    [InlineData("empty")]
    [InlineData("span")]
    [InlineData("sequence")]
    [InlineData("arc")]
    public void EnvelopeSerializer_RoundTripsOpaquePayloadAcrossIndependentProviders(string payloadKind)
    {
        byte[] expected = payloadKind == "empty" ? [] : [0x00, 0xff, 0x80, 0x13, 0xea, 0x7f, 0x00];
        var source = expected.ToArray();
        ImmutableBuffer payload;
        switch (payloadKind)
        {
            case "empty":
                payload = ImmutableBuffer.Empty;
                break;
            case "span":
                payload = new ImmutableBuffer(source.AsSpan());
                break;
            case "sequence":
                var first = new Segment(source.AsMemory(0, 2));
                var last = first.Append(source.AsMemory(2, 3)).Append(source.AsMemory(5));
                payload = new ImmutableBuffer(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
                break;
            case "arc":
                using (var writer = new ArcBufferWriter())
                {
                    writer.Write(source);
                    using var arc = writer.PeekSlice(writer.Length);
                    payload = new ImmutableBuffer(arc);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(payloadKind));
        }

        var envelope = Envelope(payload);
        Array.Fill(source, (byte)0x41);
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var wire = sendingServices.GetRequiredService<Serializer<DurableEnvelope>>().SerializeToArray(envelope);
        var decoded = receivingServices.GetRequiredService<Serializer<DurableEnvelope>>().Deserialize(wire);
        GC.Collect();

        AssertEnvelope(decoded, expected);
        AssertEnvelope(envelope, expected);
        Assert.NotSame(payload, decoded.Payload);
    }

    [Fact]
    public void EnvelopeDeepCopy_SharesImmutablePayloadAndPreservesEnvelopeValues()
    {
        byte[] source = [0x00, 0xff, 0x80, 0xea];
        var envelope = Envelope(new ImmutableBuffer(source.AsSpan()));
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();

        var copied = services.GetRequiredService<DeepCopier>().Copy(envelope);
        Array.Fill(source, (byte)0x42);
        GC.Collect();

        AssertEnvelope(copied, [0x00, 0xff, 0x80, 0xea]);
        Assert.Same(envelope.Payload, copied.Payload);
        AssertEnvelope(envelope, [0x00, 0xff, 0x80, 0xea]);
    }

    [Fact]
    public void EnvelopeSerializer_RepeatedPayloadReferences_PreserveGraphSharing()
    {
        byte[] source = [0xff, 0x00, 0x81];
        var first = Envelope(new ImmutableBuffer(source.AsSpan()));
        var second = first with
        {
            MessageId = Guid.Parse("32222222-2222-2222-2222-222222222222"),
            ReceiverId = GrainId.Create("receiver", "other")
        };
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();

        var wire = sendingServices.GetRequiredService<Serializer<DurableEnvelope[]>>().SerializeToArray([first, second]);
        Array.Fill(source, (byte)0x43);
        var decoded = receivingServices.GetRequiredService<Serializer<DurableEnvelope[]>>().Deserialize(wire)!;

        Assert.Equal(2, decoded.Length);
        AssertEnvelope(decoded[0], [0xff, 0x00, 0x81]);
        Assert.Equal(second.MessageId, decoded[1].MessageId);
        Assert.Equal(first.SenderId, decoded[1].SenderId);
        Assert.Equal(second.ReceiverId, decoded[1].ReceiverId);
        Assert.Equal(new byte[] { 0xff, 0x00, 0x81 }, decoded[1].Payload.Memory.ToArray());
        Assert.Same(decoded[0].Payload, decoded[1].Payload);
        Assert.NotSame(first.Payload, decoded[0].Payload);
    }

    [Fact]
    public void OpaqueContracts_ExposeExactPublicSurfaceAndContiguousIds()
    {
        Assert.True(typeof(DurableEnvelope).IsPublic);
        Assert.True(typeof(DurableEnvelope).IsValueType);
        Assert.Single(typeof(DurableEnvelope).GetCustomAttributes<IsReadOnlyAttribute>());
        Assert.Single(typeof(DurableEnvelope).GetCustomAttributes<GenerateSerializerAttribute>());
        Assert.Equal("Orleans.DurableMessaging.DurableEnvelope", Assert.Single(
            typeof(DurableEnvelope).GetCustomAttributes<AliasAttribute>()).Alias);
        AssertSurface(typeof(DurableEnvelope),
            Property("MessageId", typeof(Guid), true),
            Property("SenderId", typeof(GrainId), true),
            Property("ReceiverId", typeof(GrainId), true),
            Property("Payload", typeof(ImmutableBuffer), true));
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

        Assert.True(typeof(ImmutableBuffer).IsPublic);
        Assert.True(typeof(ImmutableBuffer).IsSealed);
        Assert.Single(typeof(ImmutableBuffer).GetCustomAttributes<ImmutableAttribute>());
        Assert.Single(typeof(ImmutableBuffer).GetCustomAttributes<GenerateSerializerAttribute>());
        Assert.Empty(typeof(ImmutableBuffer).GetInterfaces());
        Assert.Equal(new[] { typeof(ArcBuffer), typeof(ReadOnlySequence<byte>), typeof(ReadOnlySpan<byte>) }.Select(TypeName).Order(StringComparer.Ordinal),
            typeof(ImmutableBuffer).GetConstructors().Select(c => TypeName(Assert.Single(c.GetParameters()).ParameterType)).Order(StringComparer.Ordinal));
        AssertSurface(typeof(ImmutableBuffer), Property("Memory", typeof(ReadOnlyMemory<byte>)),
            Property("Length", typeof(int)), "static " + Property("Empty", typeof(ImmutableBuffer)),
            Method("AsReadOnlySequence", typeof(ReadOnlySequence<byte>)),
            StaticMethod("Create", typeof(ImmutableBuffer), typeof(Action<IBufferWriter<byte>>)));

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

    private static DurableEnvelope Envelope(ImmutableBuffer payload) => new()
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
        Assert.Equal(expected, envelope.Payload.Memory.ToArray());
        Assert.Equal(expected, envelope.Payload.AsReadOnlySequence().ToArray());
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
