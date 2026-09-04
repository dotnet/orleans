using System.Reflection;
using System.Text.Json;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using Orleans.Streaming.JsonConverters;
using Orleans.Streams;
using TestExtensions;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetBatchContainer"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Streaming")]
public class AdoNetBatchContainerTests(TestEnvironmentFixture fixture)
{
    [Fact]
    public void AdoNetBatchContainer_Constructs()
    {
        // arrange
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1) };
        var requestContext = new Dictionary<string, object> { { "MyKey", "Value" } };

        // act
        var container = new AdoNetBatchContainer(streamId, events, requestContext);

        // assert
        Assert.Equal(streamId, container.StreamId);
        Assert.Equal(events, container.Events);
        Assert.Equal(requestContext, container.RequestContext);
        Assert.Null(container.SequenceToken);
        Assert.Equal(0, container.Dequeued);
    }

    [Fact]
    public void AdoNetBatchContainer_FromMessage_CreatesContainer()
    {
        // arrange
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1), new OtherModel(2), new TestModel(3), new OtherModel(4) };
        var requestContext = new Dictionary<string, object> { { "MyKey", "Value" } };
        var temp = new AdoNetBatchContainer(streamId, events, requestContext);
        var serializer = fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var payload = serializer.SerializeToArray(temp);
        var message = new AdoNetStreamMessage(
            "MyServiceId",
            "MyProviderId",
            "MyQueueId",
            123,
            streamId.FullKey.ToArray(),
            streamId.Namespace.Length,
            DateTime.UtcNow,
            payload);

        // act
        var container = AdoNetBatchContainer.FromMessage(serializer, message);

        // assert
        Assert.Equal(streamId, container.StreamId);
        Assert.Equal(events, container.Events);
        Assert.Equal(requestContext, container.RequestContext);
        Assert.Equal(
            new AdoNetStreamSequenceToken("MyServiceId", "MyProviderId", "MyQueueId", 123),
            container.SequenceToken);
        Assert.Equal(0, container.Dequeued);

        var restored = serializer.Deserialize(serializer.SerializeToArray(container))!;
        var restoredToken = Assert.IsType<AdoNetStreamSequenceToken>(restored.SequenceToken);
        Assert.Equal("MyServiceId", restoredToken.ServiceId);
        Assert.Equal("MyProviderId", restoredToken.ProviderId);
        Assert.Equal("MyQueueId", restoredToken.QueueId);
    }

    [Fact]
    public void AdoNetToken_SystemTextJsonRoundTripPreservesComparablePosition()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new EventSequenceTokenJsonConverter());
        StreamSequenceToken original = new AdoNetStreamSequenceToken(
            "service",
            "provider",
            "queue",
            42,
            3);

        var restored = Assert.IsType<PartitionedStreamSequenceToken>(
            System.Text.Json.JsonSerializer.Deserialize<StreamSequenceToken>(
                System.Text.Json.JsonSerializer.Serialize(original, options),
                options));

        Assert.True(original.Equals(restored));
        Assert.True(restored.Equals(original));
        Assert.Equal(original.GetHashCode(), restored.GetHashCode());
    }

    [Fact]
    public void AdoNetToken_SerializerIdsFollowBaseTokenIds()
    {
        Assert.Equal(2u, GetId(nameof(AdoNetStreamSequenceToken.ServiceId)));
        Assert.Equal(3u, GetId(nameof(AdoNetStreamSequenceToken.ProviderId)));
        Assert.Equal(4u, GetId(nameof(AdoNetStreamSequenceToken.QueueId)));

        static uint GetId(string propertyName)
            => typeof(AdoNetStreamSequenceToken)
                .GetProperty(propertyName)!
                .GetCustomAttribute<Orleans.IdAttribute>()!
                .Id;
    }

    [Fact]
    public void AdoNetBatchContainer_ToMessagePayload_CreatesPayload()
    {
        // arrange
        var serializer = fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1), new OtherModel(2), new TestModel(3), new OtherModel(4) };
        var requestContext = new Dictionary<string, object> { { "MyKey", "Value" } };

        // act
        var payload = AdoNetBatchContainer.ToMessagePayload(serializer, streamId, events, requestContext);

        // assert
        var container = serializer.Deserialize(payload);
        Assert.NotNull(container);
        Assert.Equal(streamId, container.StreamId);
        Assert.Equal(events, container.Events);
        Assert.Equal(requestContext, container.RequestContext);
        Assert.Null(container.SequenceToken);
        Assert.Equal(0, container.Dequeued);
    }

    [Fact]
    public void RecoverableDataAdapter_UsesIdentityColumnsAndDecodesPayloadLazily()
    {
        var serializer = fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var payload = AdoNetBatchContainer.ToMessagePayload(
            serializer,
            streamId,
            [new TestModel(1)],
            requestContext: null);
        var message = new AdoNetStreamMessage(
            "service",
            "provider",
            "queue",
            42,
            streamId.FullKey.ToArray(),
            streamId.Namespace.Length,
            DateTime.UtcNow,
            payload);
        var adapter = new AdoNetRecoverableStreamDataAdapter("service", "provider", "queue", serializer);

        var position = adapter.GetStreamPosition(message);
        var cached = adapter.FromQueueMessage(
            position,
            message,
            DateTime.UtcNow,
            size => new byte[size]);

        Assert.Equal(streamId, cached.StreamId);
        Assert.Equal("42", adapter.GetOffset(ref cached));
        var batch = Assert.IsType<AdoNetBatchContainer>(adapter.GetBatchContainer(ref cached));
        Assert.Equal(streamId, batch.StreamId);
        Assert.Equal([new TestModel(1)], batch.GetEvents<TestModel>().Select(item => item.Item1));
        Assert.Equal(new AdoNetStreamSequenceToken("service", "provider", "queue", 42), batch.SequenceToken);
    }

    [Fact]
    public void AdoNetBatchContainer_GetEvents_ThrowsOnHalfBaked()
    {
        // arrange
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1), new OtherModel(2), new TestModel(3), new OtherModel(4) };
        var requestContext = new Dictionary<string, object> { { "MyKey", "Value" } };

        // act
        var container = new AdoNetBatchContainer(streamId, events, requestContext);

        // assert
        Assert.Throws<InvalidOperationException>(container.GetEvents<TestModel>);
    }

    [Fact]
    public void AdoNetBatchContainer_GetEvents_FiltersEvents()
    {
        // arrange
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1), new OtherModel(2), new TestModel(3), new OtherModel(4) };
        var requestContext = new Dictionary<string, object> { { "MyKey", "Value" } };
        var temp = new AdoNetBatchContainer(streamId, events, requestContext);
        var serializer = fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var payload = serializer.SerializeToArray(temp);
        var message = new AdoNetStreamMessage(
            "MyServiceId",
            "MyProviderId",
            "MyQueueId",
            123,
            streamId.FullKey.ToArray(),
            streamId.Namespace.Length,
            DateTime.UtcNow,
            payload);

        // act
        var container = AdoNetBatchContainer.FromMessage(serializer, message);

        // assert
        Assert.Equal([new TestModel(1), new TestModel(3)], container.GetEvents<TestModel>().Select(x => x.Item1));
        Assert.Equal(
            [new AdoNetStreamSequenceToken("MyServiceId", "MyProviderId", "MyQueueId", 123, 0), new AdoNetStreamSequenceToken("MyServiceId", "MyProviderId", "MyQueueId", 123, 2)],
            container.GetEvents<TestModel>().Select(x => x.Item2));
        Assert.Equal([new OtherModel(2), new OtherModel(4)], container.GetEvents<OtherModel>().Select(x => x.Item1));
        Assert.Equal(
            [new AdoNetStreamSequenceToken("MyServiceId", "MyProviderId", "MyQueueId", 123, 1), new AdoNetStreamSequenceToken("MyServiceId", "MyProviderId", "MyQueueId", 123, 3)],
            container.GetEvents<OtherModel>().Select(x => x.Item2));
    }

    [Fact]
    public void AdoNetBatchContainer_ImportsRequestContext()
    {
        // arrange
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1) };
        var requestContext = new Dictionary<string, object> { { "MyKey", "Value" } };

        // act
        var container = new AdoNetBatchContainer(streamId, events, requestContext);

        // assert
        Assert.Equal(streamId, container.StreamId);
        Assert.Equal(events, container.Events);
        Assert.Equal(requestContext, container.RequestContext);
        Assert.Null(container.SequenceToken);
        Assert.Equal(0, container.Dequeued);
    }

    [Fact]
    public void AdoNetBatchContainer_ToString_Renders()
    {
        // arrange
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1) };
        var requestContext = new Dictionary<string, object> { { "MyKey", "Value" } };
        var container = new AdoNetBatchContainer(streamId, events, requestContext);

        // act
        var result = container.ToString();

        // assert
        Assert.Equal($"[{nameof(AdoNetBatchContainer)}:Stream={streamId},#Items={events.Count}]", result);
    }

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetBatchContainerTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetBatchContainerTests.OtherModel")]
    public record OtherModel(
        [property: Id(0)] int Value);
}
