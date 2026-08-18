using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.TypeSystem;
using Orleans.Streams;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public class RegisterProducerVersionToleranceTests
{
    private const string LegacyMethodId = "B5FFB7F3";
    private const string CurrentMethodId = "CDCAB1B2";
    private const string CancellationSensitiveMethodId = "13D8CA40";

    [Fact]
    public void LegacyRegisterProducerRequest_DeserializesAsCurrentRequest()
    {
        using var endpoint = CreateEndpoint();
        var serializer = endpoint.GetRequiredService<ObjectSerializer>();
        var (currentRequestType, typeIdentity) = AssertCurrentRequestIdentity(endpoint);
        var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", "legacy-caller"));
        var producerId = GrainId.Create("stream-producer", "legacy-caller");
        var request = new LegacyRegisterProducerRequest
        {
            StreamId = streamId,
            StreamProducer = producerId,
        };

        var payload = Serialize(serializer, request, typeof(LegacyRegisterProducerRequest));

        var actual = Assert.IsAssignableFrom<IInvokable>(
            serializer.Deserialize(payload, currentRequestType));
        Assert.Equal(currentRequestType, actual.GetType());
        Assert.Equal(4, actual.GetArgumentCount());
        Assert.Equal(streamId, Assert.IsType<QualifiedStreamId>(actual.GetArgument(0)));
        Assert.Equal(producerId, Assert.IsType<GrainId>(actual.GetArgument(1)));
        Assert.Equal(default, Assert.IsType<MembershipVersion>(actual.GetArgument(2)));
        Assert.Equal(default, Assert.IsType<CancellationToken>(actual.GetArgument(3)));
        Assert.Equal(typeIdentity, RuntimeTypeNameFormatter.Format(actual.GetType()));
    }

    [Fact]
    public void CurrentRegisterProducerRequest_DeserializesAsLegacyRequest()
    {
        using var endpoint = CreateEndpoint();
        var serializer = endpoint.GetRequiredService<ObjectSerializer>();
        var (currentRequestType, typeIdentity) = AssertCurrentRequestIdentity(endpoint);
        var streamId = new QualifiedStreamId("provider", StreamId.Create("namespace", "current-caller"));
        var producerId = GrainId.Create("stream-producer", "current-caller");
        var membershipVersion = new MembershipVersion(42);
        var request = Assert.IsAssignableFrom<IInvokable>(Activator.CreateInstance(currentRequestType));
        request.SetArgument(0, streamId);
        request.SetArgument(1, producerId);
        request.SetArgument(2, membershipVersion);
        request.SetArgument(3, new CancellationToken(canceled: true));

        var payload = Serialize(serializer, request, currentRequestType);

        var actual = Assert.IsType<LegacyRegisterProducerRequest>(
            serializer.Deserialize(payload, typeof(LegacyRegisterProducerRequest)));
        Assert.Equal(2, actual.GetArgumentCount());
        Assert.Equal(streamId, actual.StreamId);
        Assert.Equal(producerId, actual.StreamProducer);
        Assert.Equal(typeIdentity, RuntimeTypeNameFormatter.Format(request.GetType()));
    }

    [Theory]
    [InlineData("B5FFB7F3", "13D8CA40")]
    [InlineData("2821FCF5", "1B0B06EB")]
    [InlineData("C017B47D", "4503AF37")]
    [InlineData("5E7E20BC", "617DCE7B")]
    [InlineData("20AA72BF", "711E5C1E")]
    [InlineData("7DBE84FA", "7552D8E2")]
    [InlineData("974334B6", "75FABD9B")]
    [InlineData("29B61035", "7774F1DA")]
    [InlineData("8A033955", "FB50AC30")]
    [InlineData("5F72C5CF", "FC03F44B")]
    public void CancellationSensitiveMethodIdentity_ResolvesAsCurrentRequest(
        string currentMethodId,
        string cancellationSensitiveMethodId)
    {
        using var currentEndpoint = CreateEndpoint();
        var requestType = GetRequestType(currentMethodId);
        var currentIdentity = RuntimeTypeNameFormatter.Format(requestType);
        Assert.EndsWith($",\"{currentMethodId}\")", currentIdentity, StringComparison.Ordinal);
        var cancellationSensitiveIdentity = currentIdentity.Replace(
            $",\"{currentMethodId}\")",
            $",\"{cancellationSensitiveMethodId}\")",
            StringComparison.Ordinal);

        Assert.Equal(
            requestType,
            currentEndpoint.GetRequiredService<TypeConverter>().Parse(cancellationSensitiveIdentity));
    }

    private static (Type RequestType, string TypeIdentity) AssertCurrentRequestIdentity(
        IServiceProvider currentEndpoint)
    {
        var requestType = GetRequestType(LegacyMethodId);
        var aliases = requestType.GetCustomAttributes<CompoundTypeAliasAttribute>().ToArray();
        Assert.Single(aliases);
        Assert.Contains(aliases, IsLegacyMethodAlias);

        var typeIdentity = RuntimeTypeNameFormatter.Format(requestType);
        Assert.EndsWith($",\"{LegacyMethodId}\")", typeIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(CurrentMethodId, typeIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(CancellationSensitiveMethodId, typeIdentity, StringComparison.Ordinal);
        Assert.Equal(
            requestType,
            currentEndpoint.GetRequiredService<TypeConverter>().Parse(typeIdentity));
        Assert.Equal(
            requestType,
            currentEndpoint.GetRequiredService<TypeConverter>().Parse(typeIdentity.Replace(
                $",\"{LegacyMethodId}\")",
                $",\"{CurrentMethodId}\")",
                StringComparison.Ordinal)));

        return (requestType, typeIdentity);
    }

    private static Type GetRequestType(string methodId) =>
        typeof(IPubSubRendezvousGrain).Assembly.GetTypes().Single(type =>
            typeof(IInvokable).IsAssignableFrom(type)
            && type.GetCustomAttributes<CompoundTypeAliasAttribute>().Any(
                attribute => Equals(attribute.Components[^1], methodId)));

    private static bool IsLegacyMethodAlias(CompoundTypeAliasAttribute attribute)
        => Equals(attribute.Components[^1], LegacyMethodId);

    private static byte[] Serialize(ObjectSerializer serializer, object value, Type type)
    {
        Memory<byte> destination = new byte[4096];
        serializer.Serialize(value, ref destination, type);
        return destination.ToArray();
    }

    private static ServiceProvider CreateEndpoint()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        return services.BuildServiceProvider();
    }
}

[GenerateSerializer]
internal sealed class LegacyRegisterProducerRequest : TaskRequest<ISet<PubSubSubscriptionState>>
{
    private static readonly MethodInfo Method = typeof(ILegacyRegisterProducerContract).GetMethod(
        nameof(ILegacyRegisterProducerContract.RegisterProducer))!;
    [NonSerialized]
    private ILegacyRegisterProducerContract? _target;

    [Id(0)]
    public QualifiedStreamId StreamId;

    [Id(1)]
    public GrainId StreamProducer;

    public override int GetArgumentCount() => 2;

    public override object GetArgument(int index) => index switch
    {
        0 => StreamId,
        1 => StreamProducer,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public override void SetArgument(int index, object value)
    {
        switch (index)
        {
            case 0:
                StreamId = (QualifiedStreamId)value;
                return;
            case 1:
                StreamProducer = (GrainId)value;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public override object GetTarget() => _target!;

    public override void SetTarget(ITargetHolder holder)
        => _target = (ILegacyRegisterProducerContract)(holder.GetTarget()
            ?? throw new InvalidOperationException("The request target is not available."));

    public override void Dispose()
    {
        StreamId = default;
        StreamProducer = default;
        _target = null;
    }

    public override string GetMethodName() => nameof(ILegacyRegisterProducerContract.RegisterProducer);

    public override string GetInterfaceName() => typeof(IPubSubRendezvousGrain).FullName!;

    public override string GetActivityName() => "IPubSubRendezvousGrain/RegisterProducer";

    public override Type GetInterfaceType() => typeof(IPubSubRendezvousGrain);

    public override MethodInfo GetMethod() => Method;

    protected override Task<ISet<PubSubSubscriptionState>> InvokeInner()
        => _target!.RegisterProducer(StreamId, StreamProducer);
}

internal interface ILegacyRegisterProducerContract
{
    Task<ISet<PubSubSubscriptionState>> RegisterProducer(
        QualifiedStreamId streamId,
        GrainId streamProducer);
}
