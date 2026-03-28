using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using TestExtensions;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetBatchContainer"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("AdoNet"), TestCategory("Streaming")]
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
        var message = new AdoNetStreamMessage("MyServiceId", "MyProviderId", "MyQueueId", 123, 234, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, payload);

        // act
        var container = AdoNetBatchContainer.FromMessage(serializer, message);

        // assert
        Assert.Equal(streamId, container.StreamId);
        Assert.Equal(events, container.Events);
        Assert.Equal(requestContext, container.RequestContext);
        Assert.Equal(new EventSequenceTokenV2(123), container.SequenceToken);
        Assert.Equal(234, container.Dequeued);
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
        Assert.Equal(streamId, container.StreamId);
        Assert.Equal(events, container.Events);
        Assert.Equal(requestContext, container.RequestContext);
        Assert.Null(container.SequenceToken);
        Assert.Equal(0, container.Dequeued);
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
        var message = new AdoNetStreamMessage("MyServiceId", "MyProviderId", "MyQueueId", 123, 234, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, payload);

        // act
        var container = AdoNetBatchContainer.FromMessage(serializer, message);

        // assert
        Assert.Equal([new TestModel(1), new TestModel(3)], container.GetEvents<TestModel>().Select(x => x.Item1));
        Assert.Equal([new EventSequenceTokenV2(123, 0), new EventSequenceTokenV2(123, 1)], container.GetEvents<TestModel>().Select(x => x.Item2));
        Assert.Equal([new OtherModel(2), new OtherModel(4)], container.GetEvents<OtherModel>().Select(x => x.Item1));
        Assert.Equal([new EventSequenceTokenV2(123, 0), new EventSequenceTokenV2(123, 1)], container.GetEvents<OtherModel>().Select(x => x.Item2));
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