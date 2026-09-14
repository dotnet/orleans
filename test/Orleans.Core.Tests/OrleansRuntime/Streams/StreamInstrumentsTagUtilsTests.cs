using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Streaming;
using Orleans.Streams;
using Xunit;
using TagList = System.Diagnostics.TagList;

#nullable enable
namespace UnitTests.OrleansRuntime.Streams;

public class StreamInstrumentsTagUtilsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
    public void GrainTagsUseStableLowCardinalityDimensions()
    {
        var firstStreamId = new QualifiedStreamId("ProviderName", StreamId.Create("Orders", "user-1"));
        var secondStreamId = new QualifiedStreamId("ProviderName", StreamId.Create("Payments", "user-2"));
        var firstGrainId = GrainId.Create("UnitTests.Streaming.OrderProducerGrain", "user-1");
        var secondGrainId = GrainId.Create("UnitTests.Streaming.OrderProducerGrain", "user-2");

        var firstTags = ToTagDictionary(StreamInstrumentsTagUtils.InitializeTags(firstStreamId, firstGrainId));
        var secondTags = ToTagDictionary(StreamInstrumentsTagUtils.InitializeTags(secondStreamId, secondGrainId));

        Assert.Equal(2, firstTags.Count);
        Assert.Equal(firstTags.Count, secondTags.Count);
        foreach (var tag in firstTags)
        {
            Assert.True(secondTags.TryGetValue(tag.Key, out var otherValue));
            Assert.Equal(tag.Value, otherValue);
        }

        Assert.Equal("ProviderName", firstTags["provider"]);
        Assert.Equal("UnitTests.Streaming.OrderProducerGrain", firstTags["grain_type"]);
        Assert.DoesNotContain("namespace", firstTags.Keys);
        Assert.DoesNotContain("stream", firstTags.Keys);
        Assert.DoesNotContain("producer", firstTags.Keys);
        Assert.DoesNotContain("subscription", firstTags.Keys);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
    public void StreamOnlyTagsUseUnknownGrainType()
    {
        var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("Orders", "user-1"));

        var tags = ToTagDictionary(StreamInstrumentsTagUtils.InitializeTags(streamId));

        Assert.Equal(2, tags.Count);
        Assert.Equal("ProviderName", tags["provider"]);
        Assert.Equal("unknown", tags["grain_type"]);
        Assert.DoesNotContain("namespace", tags.Keys);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
    public void QueueTagsIncludeOnlyBoundedDimensions()
    {
        var queueId = QueueId.GetQueueId("QueuePrefix", 1, 42);

        var tags = ToTagDictionary(StreamInstrumentsTagUtils.InitializeTags(queueId, "ProviderName"));

        Assert.Equal(2, tags.Count);
        Assert.Equal("ProviderName", tags["provider"]);
        Assert.Equal(queueId.ToStringWithHashCode(), tags["queue"]);
        Assert.DoesNotContain("grain_type", tags.Keys);
        Assert.DoesNotContain("namespace", tags.Keys);
    }

    private static Dictionary<string, object?> ToTagDictionary(TagList tags)
    {
        var result = new Dictionary<string, object?>();
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }
}
