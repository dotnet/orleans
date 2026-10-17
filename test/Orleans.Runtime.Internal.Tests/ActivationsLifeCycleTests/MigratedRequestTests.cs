using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

/// <summary>
/// Verifies how requests which migrate with an activation carry their remaining time to live.
/// </summary>
[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed class MigratedRequestTests
{
    private static readonly ServiceProvider Services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static SerializerSessionPool Pool => Services.GetRequiredService<SerializerSessionPool>();

    [Fact]
    public void Body_FromCallerOnThisSilo_IsDeliveredAsAnEqualCopy()
    {
        var request = CreateRequest(timeToLive: null);
        var body = new List<string> { "first", "second" };
        request.BodyObject = body;

        var delivered = MigratedRequest.FromMessage(request, Pool).ToMessage(GrainId.Create("test", "target"), SiloAddress.FromParsableString("127.0.0.1:11111@1"), Pool);

        // The caller's pending call keeps its own body, and the destination receives an equal copy.
        Assert.Same(body, request.BodyObject);
        var copy = Assert.IsType<List<string>>(delivered.BodyObject);
        Assert.NotSame(body, copy);
        Assert.Equal(body, copy);
    }

    [Fact]
    public async Task TimeToLive_AgesUntilSerialization()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var request = MigratedRequest.FromMessage(CreateRequest(TimeSpan.FromMinutes(1)), Pool);

        // Time spent before the request is serialized, such as in the outbound queue, counts against its budget.
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        var received = serializer.Deserialize<MigratedRequest>(serializer.SerializeToArray(request))!;

        var delivered = received.ToMessage(GrainId.Create("test", "target"), SiloAddress.FromParsableString("127.0.0.1:11111@1"), Pool);
        Assert.InRange(delivered.TimeToLive!.Value, TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1) - TimeSpan.FromMilliseconds(400));
        Assert.Equal(1, delivered.ForwardCount);
    }

    [Fact]
    public async Task ExpiredRequest_RemainsExpiredAfterSerialization()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var request = MigratedRequest.FromMessage(CreateRequest(TimeSpan.FromMilliseconds(100)), Pool);

        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        var received = serializer.Deserialize<MigratedRequest>(serializer.SerializeToArray(request))!;

        Assert.True(received.ToMessage(GrainId.Create("test", "target"), SiloAddress.FromParsableString("127.0.0.1:11111@1"), Pool).IsExpired);
    }

    [Fact]
    public void InfiniteTimeToLive_IsPreserved()
    {
        var request = MigratedRequest.FromMessage(CreateRequest(timeToLive: null), Pool);

        Assert.Null(request.TimeToLiveMilliseconds);
        Assert.Null(request.ToMessage(GrainId.Create("test", "target"), SiloAddress.FromParsableString("127.0.0.1:11111@1"), Pool).TimeToLive);
    }

    [Fact]
    public void PooledRequestContextValue_SurvivesMeasuringAndSending()
    {
        var serializer = Services.GetRequiredService<Serializer>();
        var expected = CreatePayload(0x11);
        var request = CreateRequest(timeToLive: null);
        request.RequestContextData = new() { ["pooled"] = CreatePooledBuffer(expected) };
        var migrated = MigratedRequest.FromMessage(request, Pool);

        // A migration serializes the captured request once to measure it and once to send it. Serializing a pooled buffer returns its storage to the pool,
        // clearing it, so the captured request must hold serialized data rather than the value itself.
        serializer.SerializeToArray(migrated);
        var received = serializer.Deserialize<MigratedRequest>(serializer.SerializeToArray(migrated))!;

        var delivered = received.ToMessage(GrainId.Create("test", "target"), SiloAddress.FromParsableString("127.0.0.1:11111@1"), Pool);
        Assert.Equal(expected, ((PooledBuffer)delivered.RequestContextData!["pooled"]).ToArray());
    }

    [Fact]
    public void RetainedRequest_IsRestoredWithItsPooledValues()
    {
        var expectedBody = CreatePayload(0x22);
        var expectedContext = CreatePayload(0x33);
        var request = CreateRequest(timeToLive: null);
        request.BodyObject = CreatePooledBuffer(expectedBody);
        request.RequestContextData = new() { ["pooled"] = CreatePooledBuffer(expectedContext) };

        // Capturing the request serializes its body and context, which returns their pooled storage to the pool, clearing it.
        var migrated = MigratedRequest.FromMessage(request, Pool);

        // A request which stays with the source is restored from the captured data before it is forwarded.
        migrated.RestoreTo(request, Pool);
        Assert.Equal(expectedBody, ((PooledBuffer)request.BodyObject!).ToArray());
        Assert.Equal(expectedContext, ((PooledBuffer)request.RequestContextData!["pooled"]).ToArray());
    }

    private static byte[] CreatePayload(byte value) => Enumerable.Repeat(value, 20_000).ToArray();

    private static PooledBuffer CreatePooledBuffer(byte[] contents)
    {
        var buffer = new PooledBuffer();
        buffer.Write(contents);
        return buffer;
    }

    private static Message CreateRequest(TimeSpan? timeToLive) => new()
    {
        Direction = Message.Directions.Request,
        Id = new CorrelationId(17),
        SendingGrain = GrainId.Create("test", "caller"),
        TargetGrain = GrainId.Create("test", "target"),
        TimeToLive = timeToLive,
    };
}
