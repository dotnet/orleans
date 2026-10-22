using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Networking.Shared;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Session;
using Orleans.Serialization.Buffers;
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
    public void RoundTrip_PreservesHeadersAndEagerPayload(int payloadLength, bool polymorphic)
    {
        using var services = CreateServices();
        var serializer = services.GetRequiredService<Serializer<Message>>();
        var objectSerializer = services.GetRequiredService<Serializer<object>>();
        var original = CreateRequest(17, payloadLength);
        original.TimeToLive = TimeSpan.FromDays(1);
        var expected = Assert.IsType<byte[]>(original.BodyObject);
        var bytes = polymorphic ? objectSerializer.SerializeToArray(original) : serializer.SerializeToArray(original);
        var received = Assert.IsType<Message>(polymorphic ? objectSerializer.Deserialize(bytes) : serializer.Deserialize(bytes));
        Assert.Equal(original.Headers, received.Headers);
        Assert.Equal(new CorrelationId(17), received.Id);
        Assert.Equal(original.SendingGrain, received.SendingGrain);
        Assert.Equal(original.TargetGrain, received.TargetGrain);
        Assert.Equal(original.InterfaceType, received.InterfaceType);
        Assert.Equal(2, received.InterfaceVersion);
        Assert.Equal(1, received.ForwardCount);
        Assert.True(received.IsReadOnly);
        Assert.True(received.TimeToLive > TimeSpan.Zero);
        Assert.Equal("context", received.RequestContextData!["marker"]);
        Assert.Equal(expected, Assert.IsType<byte[]>(received.BodyObject));
        var forwarded = Assert.IsType<Message>(serializer.Deserialize(serializer.SerializeToArray(received)));
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
        var original = CreateRequest(1, 20_000);
        var serializer = services.GetRequiredService<Serializer<List<Message>>>();
        var result = Assert.IsType<List<Message>>(serializer.Deserialize(serializer.SerializeToArray([original, original])));
        Assert.Equal(2, result.Count);
        Assert.Same(result[0], result[1]);
        Assert.Equal(Assert.IsType<byte[]>(original.BodyObject), Assert.IsType<byte[]>(result[0].BodyObject));
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
        .AddSingleton<SharedMemoryPool>()
        .AddSingleton<IOptions<SiloMessagingOptions>>(Options.Create(new SiloMessagingOptions()))
        .BuildServiceProvider();
}

[Trait("Category", "BVT"), Trait("Suite", "BVT"), Trait("Provider", "None"), Trait("Area", "Serialization")]
[OwnershipCodecTests(typeof(GrainMigrationPackage))]
public sealed class GrainMigrationPackageCodecTests
{
    [Fact]
    public void Copy_PreservesManagedMigrationSourceAndPayload()
    {
        using var services = MessageCodecTests.CreateServices();
        using var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("state", 42));
        var request = MessageCodecTests.CreateRequest(1, 20_000);
        List<Message> requests = [request];
        var source = SiloAddress.New(IPAddress.Loopback, 12345, 1);
        var target = SiloAddress.New(IPAddress.Loopback, 12346, 1);
        var migration = new ActivationMigration(null!, new GrainAddress
        {
            GrainId = request.TargetGrain,
            SiloAddress = source,
            ActivationId = ActivationId.NewId(),
        }, target, new GrainMigrationPackage { GrainId = request.TargetGrain, MigrationContext = context, Requests = requests },
            requests, routes: null, size: 0, CancellationToken.None);
        var copy = services.GetRequiredService<DeepCopier>().Copy(migration.Package);
        Assert.Equal(request.TargetGrain, copy.GrainId);
        Assert.Same(migration, copy.Source);
        Assert.Same(context, copy.MigrationContext);
        Assert.Same(requests, copy.Requests);
        Assert.Same(request, Assert.Single(copy.Requests!));
        Assert.True(copy.MigrationContext.TryGetValue<int>("state", out var state));
        Assert.Equal(42, state);
    }

    [Fact]
    public void SourcePackage_SingleWriteReusesPreparedArgumentsAndRejectsDuplicateOrLateWrites()
    {
        using var services = MessageCodecTests.CreateServices();
        var payload = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 7)).ToArray();
        var contextPayload = Enumerable.Repeat((byte)0xA3, 30_000).ToArray();
        var pooledBody = new PooledBuffer();
        pooledBody.Write(payload);
        var pooledContext = new PooledBuffer();
        pooledContext.Write(contextPayload);
        var request = MessageCodecTests.CreateRequest(23, 0);
        request.BodyObject = pooledBody;
        request.RequestContextData!["pooled"] = pooledContext;
        request.TimeToLive = TimeSpan.FromDays(1);
        var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("state", contextPayload));
        using var messageSerializer = MessageCodec.CreateMessageSerializer(services);
        var frame = MessageCodec.PrepareFrame(messageSerializer, request);
        // PooledBuffer serialization consumed the underlying segments. Neither the boxed original nor a copy can
        // safely be serialized again. Remove the eager values to prove that the package uses the prepared frame.
        request.BodyObject = null;
        request.RequestContextData = null;
        var preparedBytes = frame.ToArray();
        var source = SiloAddress.New(IPAddress.Loopback, 12345, 1);
        var target = SiloAddress.New(IPAddress.Loopback, 12346, 1);
        List<Message> requests = [request];
        var migration = new ActivationMigration(null!, new GrainAddress
        {
            GrainId = request.TargetGrain,
            SiloAddress = source,
            ActivationId = ActivationId.NewId(),
        }, target, new GrainMigrationPackage { GrainId = request.TargetGrain, MigrationContext = context, Requests = requests },
            requests, routes: null, size: frame.Length, CancellationToken.None, [frame]);
        Assert.True(migration.TrySend());
        var serializer = services.GetRequiredService<Serializer<GrainMigrationPackage>>();
        // Model the queued request reaching its original deadline after preparation without waiting.
        // Write the retained frame verbatim; the captured TTL is allowed to be stale.
        request.TimeToLive = TimeSpan.FromDays(-1);
        var bytes = serializer.SerializeToArray(migration.Package);
        Assert.True(bytes.AsSpan().IndexOf(preparedBytes) >= 0, "The package must contain the exact retained message frame.");
        // This write consumed context's pooled buffer. Only the destination owns a readable/disposable context now.
        var received = serializer.Deserialize(bytes);
        using var receivedContext = received.MigrationContext;
        Assert.True(receivedContext.TryGetValue<byte[]>("state", out var state));
        Assert.Equal(contextPayload, state);
        var carried = Assert.Single(received.Requests!);
        using var body = Assert.IsType<PooledBuffer>(carried.BodyObject);
        using var requestContext = Assert.IsType<PooledBuffer>(carried.RequestContextData!["pooled"]);
        Assert.Equal(payload, body.ToArray());
        Assert.Equal(contextPayload, requestContext.ToArray());
        Assert.Equal(new CorrelationId(23), carried.Id);
        Assert.Equal(1, carried.ForwardCount);
        Assert.True(carried.TimeToLive > TimeSpan.Zero);
        Assert.Equal(preparedBytes, frame);
        var duplicate = Assert.Throws<InvalidOperationException>(() => serializer.SerializeToArray(migration.Package));
        Assert.Equal("The migration payload has already been serialized.", duplicate.Message);
        migration.Complete();
        var late = Assert.Throws<OperationCanceledException>(() => serializer.SerializeToArray(migration.Package));
        Assert.Equal("The migration completed before its payload was serialized.", late.Message);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void RoundTrip_PreservesContextAndRequestOrder(int requestCount, bool polymorphic)
    {
        using var services = MessageCodecTests.CreateServices();
        var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("state", 42));
        var package = new GrainMigrationPackage
        {
            GrainId = GrainId.Create("test", "target"),
            MigrationContext = context,
            Requests = requestCount < 0 ? null : Enumerable.Range(0, requestCount).Select(i => MessageCodecTests.CreateRequest(i, 20_000)).ToList(),
        };
        var serializer = services.GetRequiredService<Serializer<GrainMigrationPackage>>();
        var objectSerializer = services.GetRequiredService<Serializer<object>>();
        var bytes = polymorphic ? objectSerializer.SerializeToArray(package) : serializer.SerializeToArray(package);
        var result = polymorphic ? Assert.IsType<GrainMigrationPackage>(objectSerializer.Deserialize(bytes)) : serializer.Deserialize(bytes);
        using var receivedContext = result.MigrationContext;
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
                Assert.Equal(Assert.IsType<byte[]>(package.Requests![i].BodyObject), Assert.IsType<byte[]>(requests[i].BodyObject));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedPackage_RejectsMissingContextOrNullRequest(bool missingContext)
    {
        using var services = MessageCodecTests.CreateServices();
        var package = new GrainMigrationPackage
        {
            GrainId = GrainId.Create("test", "malformed"),
            MigrationContext = missingContext ? null! : new MigrationContext(services.GetRequiredService<SerializerSessionPool>()),
            Requests = missingContext ? null : [null!],
        };
        var serializer = services.GetRequiredService<Serializer<GrainMigrationPackage>>();
        var bytes = serializer.SerializeToArray(package);
        var error = Assert.Throws<InvalidMessageFrameException>(() => serializer.Deserialize(bytes));
        Assert.Equal(missingContext ? "A migration package must contain a migration context." : "A migration package cannot contain a null request.", error.Message);
    }

    [Fact]
    public void LegacyPackage_PreservesOriginalFieldIdsAndMethodAlias()
    {
        using var services = MessageCodecTests.CreateServices();
        var legacySourceContext = CreateContext();
        var grainId = GrainId.Create("test", "legacy");
        var legacy = new LegacyMigrationPackage { GrainId = grainId, MigrationContext = legacySourceContext };
        var serializer = services.GetRequiredService<Serializer<GrainMigrationPackage>>();
        var legacySerializer = services.GetRequiredService<Serializer<LegacyMigrationPackage>>();
        var legacyBytes = legacySerializer.SerializeToArray(legacy);
        var package = serializer.Deserialize(legacyBytes);
        using var modernContext = package.MigrationContext;
        Assert.Equal(grainId, package.GrainId);
        Assert.Null(package.Requests);
        Assert.True(package.MigrationContext.TryGetValue<int>("state", out var state));
        Assert.Equal(42, state);
        var requestFree = new GrainMigrationPackage
        {
            GrainId = grainId,
            MigrationContext = CreateContext(),
        };
        var requestFreeOldReader = legacySerializer.Deserialize(serializer.SerializeToArray(requestFree));
        using var requestFreeOldContext = requestFreeOldReader.MigrationContext;
        Assert.Equal(grainId, requestFreeOldReader.GrainId);
        Assert.True(requestFreeOldContext.TryGetValue<int>("state", out var requestFreeState));
        Assert.Equal(42, requestFreeState);
        // Each write consumes a fresh source context; the first destination context is never reserialized.
        var modern = new GrainMigrationPackage
        {
            GrainId = grainId,
            MigrationContext = CreateContext(),
            Requests = [MessageCodecTests.CreateRequest(17, 20_000)],
        };
        var oldReader = legacySerializer.Deserialize(serializer.SerializeToArray(modern));
        using var oldContext = oldReader.MigrationContext;
        Assert.Equal(grainId, oldReader.GrainId);
        Assert.True(oldContext.TryGetValue<int>("state", out var oldState));
        Assert.Equal(42, oldState);
        var method = typeof(IActivationMigrationManagerSystemTarget).GetMethod(nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))!;
        Assert.Equal("29E9E63F", method.GetCustomAttribute<AliasAttribute>()!.Alias);

        MigrationContext CreateContext()
        {
            var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
            Assert.True(context.TryAddValue("state", 42));
            return context;
        }
    }

    [GenerateSerializer]
    internal struct LegacyMigrationPackage
    {
        [Id(0)] public GrainId GrainId { get; set; }
        [Id(1)] public MigrationContext MigrationContext { get; set; }
    }
}
