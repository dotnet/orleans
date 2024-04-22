using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.AdoNet;
using TestExtensions;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueDataAdapter"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("AdoNet"), TestCategory("Streaming")]
public class AdoNetQueueDataAdapterTests(TestEnvironmentFixture environment)
{
    private readonly TestEnvironmentFixture _environment = environment;

    [GenerateSerializer]
    [Alias("TestModel")]
    public record TestModel([property: Id(0)] int Value);

    [Fact]
    public void Cycles()
    {
        // arrange
        var serializer = _environment.Services.GetRequiredService<Serializer<AdoNetBatchContainer>>();
        var adapter = new AdoNetQueueDataAdapter(serializer);
        var streamId = StreamId.Create("SomeNamespace", "SomeKey");
        var context = new Dictionary<string, object> { { "SomeKey", 123 } };
        var sequenceId = Random.Shared.Next();

        // act - convert to message
        var events = new[] { new TestModel(1), new TestModel(2), new TestModel(3) };
        var message = adapter.ToQueueMessage(streamId, events, null, context);

        // assert - message was produced
        Assert.NotNull(message);
        Assert.NotEmpty(message);

        // act - convert to container
        var container = adapter.FromQueueMessage(message, sequenceId);

        // assert - container was produced
        Assert.NotNull(container);

        // assert - container is of known type
        var adonet = Assert.IsType<AdoNetBatchContainer>(container);
        Assert.Equal(streamId, adonet.StreamId);
        Assert.Equal(events, adonet.Events);

        // assert - token was generated
        var token = Assert.IsType<EventSequenceTokenV2>(adonet.SequenceToken);
        Assert.Equal(sequenceId, token.SequenceNumber);
        Assert.Equal(0, token.EventIndex);

        // assert - events are filtered
        Assert.Empty(adonet.GetEvents<Version>());
        Assert.Collection(adonet.GetEvents<TestModel>(),
            x =>
            {
                Assert.Equal(x.Item1, events[0]);
                Assert.Equal(sequenceId, x.Item2.SequenceNumber);
                Assert.Equal(0, x.Item2.EventIndex);
            },
            x =>
            {
                Assert.Equal(x.Item1, events[1]);
                Assert.Equal(sequenceId, x.Item2.SequenceNumber);
                Assert.Equal(1, x.Item2.EventIndex);
            },
            x =>
            {
                Assert.Equal(x.Item1, events[2]);
                Assert.Equal(sequenceId, x.Item2.SequenceNumber);
                Assert.Equal(2, x.Item2.EventIndex);
            });

        // assert - context was preserved
        Assert.NotNull(adonet.RequestContext);
        Assert.Equal(context.OrderBy(x => x.Key), adonet.RequestContext.OrderBy(x => x.Key));
        Assert.True(adonet.ImportRequestContext());
        Assert.Equal(context["SomeKey"], RequestContext.Get("SomeKey"));
    }

    [Fact]
    public void Cycles_AfterSelfDeserialization()
    {
        // arrange
        var serializer = _environment.Services.GetRequiredService<Serializer<AdoNetBatchContainer>>();
        var adapter = new AdoNetQueueDataAdapter(serializer);
        var streamId = StreamId.Create("SomeNamespace", "SomeKey");
        var context = new Dictionary<string, object> { { "SomeKey", 123 } };
        var sequenceId = Random.Shared.Next();

        // act - roundtrip the data adapter itself
        var serialized = _environment.Services.GetRequiredService<Serializer<AdoNetQueueDataAdapter>>().SerializeToArray(adapter);
        adapter = _environment.Services.GetRequiredService<Serializer<AdoNetQueueDataAdapter>>().Deserialize(serialized);

        // act - convert to message
        var events = new[] { new TestModel(1), new TestModel(2), new TestModel(3) };
        var message = adapter.ToQueueMessage(streamId, events, null, context);

        // assert - message was produced
        Assert.NotNull(message);
        Assert.NotEmpty(message);

        // act - convert to container
        var container = adapter.FromQueueMessage(message, sequenceId);

        // assert - container was produced
        Assert.NotNull(container);

        // assert - container is of known type
        var adonet = Assert.IsType<AdoNetBatchContainer>(container);
        Assert.Equal(streamId, adonet.StreamId);
        Assert.Equal(events, adonet.Events);

        // assert - token was generated
        var token = Assert.IsType<EventSequenceTokenV2>(adonet.SequenceToken);
        Assert.Equal(sequenceId, token.SequenceNumber);
        Assert.Equal(0, token.EventIndex);

        // assert - events are filtered
        Assert.Empty(adonet.GetEvents<Version>());
        Assert.Collection(adonet.GetEvents<TestModel>(),
            x =>
            {
                Assert.Equal(x.Item1, events[0]);
                Assert.Equal(sequenceId, x.Item2.SequenceNumber);
                Assert.Equal(0, x.Item2.EventIndex);
            },
            x =>
            {
                Assert.Equal(x.Item1, events[1]);
                Assert.Equal(sequenceId, x.Item2.SequenceNumber);
                Assert.Equal(1, x.Item2.EventIndex);
            },
            x =>
            {
                Assert.Equal(x.Item1, events[2]);
                Assert.Equal(sequenceId, x.Item2.SequenceNumber);
                Assert.Equal(2, x.Item2.EventIndex);
            });

        // assert - context was preserved
        Assert.NotNull(adonet.RequestContext);
        Assert.Equal(context.OrderBy(x => x.Key), adonet.RequestContext.OrderBy(x => x.Key));
        Assert.True(adonet.ImportRequestContext());
        Assert.Equal(context["SomeKey"], RequestContext.Get("SomeKey"));
    }
}