using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT"), Trait("Suite", "BVT"), Trait("Provider", "None"), Trait("Area", "Serialization")]
[OwnershipCodecTests(typeof(Message))]
public sealed class MessageCodecTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(20_000, false)]
    [InlineData(20_000, true)]
    public void RoundTrip_PreservesHeadersAndLazyPayload(int payloadLength, bool polymorphic)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<Message>>();
        var objectSerializer = services.GetRequiredService<Serializer<object>>();
        using var original = CreateRequest(17, payloadLength);
        var expected = Assert.IsType<byte[]>(original.BodyObject);
        var bytes = polymorphic ? objectSerializer.SerializeToArray(original) : serializer.SerializeToArray(original);
        using var received = polymorphic
            ? Assert.IsType<Message>(objectSerializer.Deserialize(bytes))
            : Assert.IsType<Message>(serializer.Deserialize(bytes));

        Assert.Equal(original.Headers, received.Headers);
        Assert.Equal(original.Id, received.Id);
        Assert.Equal(original.SendingGrain, received.SendingGrain);
        Assert.Equal(original.TargetGrain, received.TargetGrain);
        Assert.Equal(original.InterfaceType, received.InterfaceType);
        Assert.Equal(original.InterfaceVersion, received.InterfaceVersion);
        Assert.Equal("context", received.RequestContextData!["marker"]);
        var body = Assert.IsType<MessageReadRequest>(received._bodyObject);

        using var forwarded = Assert.IsType<Message>(serializer.Deserialize(serializer.SerializeToArray(received)));
        Assert.Same(body, received._bodyObject);
        Assert.IsType<MessageReadRequest>(forwarded._bodyObject);
        Assert.Equal(expected, Assert.IsType<byte[]>(forwarded.BodyObject));
        Assert.Equal(expected, Assert.IsType<byte[]>(received.BodyObject));
    }

    [Fact]
    public void Null_RoundTrips()
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<Message>>();
        Assert.Null(serializer.Deserialize(serializer.SerializeToArray(null)));
    }

    [Fact]
    public void RepeatedReferences_PreserveMessageIdentity()
    {
        using var services = CreateServices();
        using var original = CreateRequest(1, 20_000);
        var serializer = services.GetRequiredService<Serializer<List<Message>>>();
        var result = Assert.IsType<List<Message>>(serializer.Deserialize(serializer.SerializeToArray([original, original])));
        using var received = result[0];
        Assert.Equal(2, result.Count);
        Assert.Same(received, result[1]);
        Assert.IsType<MessageReadRequest>(received._bodyObject);
        Assert.Equal(Assert.IsType<byte[]>(original.BodyObject), Assert.IsType<byte[]>(received.BodyObject));
    }

    internal static Message CreateRequest(int id, int payloadLength) => new()
    {
        Direction = Message.Directions.Request,
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("test", "caller"),
        TargetGrain = GrainId.Create("test", "target"),
        InterfaceType = GrainInterfaceType.Create("test-interface"),
        InterfaceVersion = 2,
        ForwardCount = 1,
        IsReadOnly = true,
        RequestContextData = new() { ["marker"] = "context" },
        BodyObject = Enumerable.Range(0, payloadLength).Select(i => (byte)(i + id)).ToArray(),
    };

    internal static ServiceProvider CreateServices() => new ServiceCollection()
        .AddSerializer()
        .AddMetrics()
        .AddSingleton(services =>
        {
            var instruments = new OrleansInstruments(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
            var messaging = new MessagingInstruments(instruments);
            var trace = new MessagingTrace(NullLoggerFactory.Instance, messaging, new MessagingProcessingInstruments(instruments));
            var sessions = services.GetRequiredService<SerializerSessionPool>();
            // These tests encode messages without routing them.
            return new MessageHandlerShared(
                trace,
                new ConnectionTrace(NullLoggerFactory.Instance),
                () => new MessageSerializer(sessions, new SiloMessagingOptions()),
                new MessageFactory(services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, trace),
                messageCenter: null!,
                messaging);
        })
        .BuildServiceProvider();
}

[Trait("Category", "BVT"), Trait("Suite", "BVT"), Trait("Provider", "None"), Trait("Area", "Serialization")]
[OwnershipCodecTests(typeof(GrainMigrationPackage))]
public sealed class GrainMigrationPackageCodecTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void RoundTrip_PreservesContextAndRequestOrder(int requestCount, bool polymorphic)
    {
        using var services = MessageCodecTests.CreateServices();
        using var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("state", 42));
        var package = new GrainMigrationPackage
        {
            GrainId = GrainId.Create("test", "target"),
            MigrationContext = context,
            Requests = requestCount < 0 ? null : Enumerable.Range(0, requestCount)
                .Select(i => MessageCodecTests.CreateRequest(i, 20_000)).ToList(),
        };
        var serializer = services.GetRequiredService<Serializer<GrainMigrationPackage>>();
        var objectSerializer = services.GetRequiredService<Serializer<object>>();
        var bytes = polymorphic ? objectSerializer.SerializeToArray(package) : serializer.SerializeToArray(package);
        var result = polymorphic
            ? Assert.IsType<GrainMigrationPackage>(objectSerializer.Deserialize(bytes))
            : serializer.Deserialize(bytes);
        try
        {
            Assert.Equal(package.GrainId, result.GrainId);
            Assert.Null(result.Source);
            Assert.True(result.MigrationContext.TryGetValue<int>("state", out var state));
            Assert.Equal(42, state);
            if (requestCount < 0)
            {
                Assert.Null(result.Requests);
            }
            else
            {
                var requests = Assert.IsType<List<Message>>(result.Requests);
                Assert.Equal(requestCount, requests.Count);
                for (var i = 0; i < requests.Count; i++)
                {
                    Assert.Equal(new CorrelationId(i), requests[i].Id);
                    Assert.IsType<MessageReadRequest>(requests[i]._bodyObject);
                    Assert.Equal(Assert.IsType<byte[]>(package.Requests![i].BodyObject), Assert.IsType<byte[]>(requests[i].BodyObject));
                }
            }
        }
        finally
        {
            result.ReleasePayload();
        }
    }

    [Fact]
    public void CopyAndSerialization_ShareOnePayloadOwner()
    {
        using var services = MessageCodecTests.CreateServices();
        var messageSerializer = services.GetRequiredService<Serializer<Message>>();
        using var original = MessageCodecTests.CreateRequest(1, 20_000);
        var expected = Assert.IsType<byte[]>(original.BodyObject);
        using var request = Assert.IsType<Message>(messageSerializer.Deserialize(messageSerializer.SerializeToArray(original)));
        using var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("state", "preserved"));
        var source = SiloAddress.New(IPAddress.Loopback, 12345, 1);
        var target = SiloAddress.New(IPAddress.Loopback, 12346, 1);
        var package = new GrainMigrationPackage { GrainId = request.TargetGrain, MigrationContext = context, Requests = [request] };
        var migration = new ActivationMigration(
            manager: null!,
            new GrainAddress { SiloAddress = source, GrainId = package.GrainId, ActivationId = ActivationId.NewId() },
            target, package, package.Requests, routes: null, size: 0, CancellationToken.None);
        var copy = services.GetRequiredService<DeepCopier>().Copy(migration.Package);
        Assert.Same(migration, copy.Source);
        Assert.Same(context, copy.MigrationContext);
        Assert.Same(package.Requests, copy.Requests);
        Assert.IsType<MessageReadRequest>(request._bodyObject);
        Assert.True(migration.TrySend());
        try
        {
            var serializer = services.GetRequiredService<Serializer<GrainMigrationPackage>>();
            var bytes = serializer.SerializeToArray(copy);
            Assert.Null(request._bodyObject);
            Assert.Empty(context.Keys);
            Assert.Throws<InvalidOperationException>(() => serializer.SerializeToArray(migration.Package));

            var received = serializer.Deserialize(bytes);
            try
            {
                Assert.Null(received.Source);
                Assert.True(received.MigrationContext.TryGetValue<string>("state", out var state));
                Assert.Equal("preserved", state);
                var message = Assert.Single(received.Requests!);
                Assert.IsType<MessageReadRequest>(message._bodyObject);
                Assert.Equal(expected, Assert.IsType<byte[]>(message.BodyObject));
            }
            finally
            {
                received.ReleasePayload();
            }
        }
        finally
        {
            migration.Complete();
        }
    }
}
