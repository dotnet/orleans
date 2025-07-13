using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests;

/// <summary>
/// Tests System.Text.Json serialization of Orleans types including GrainId with various collection types.
/// </summary>
[TestCategory("Serialization"), TestCategory("BVT")]
public class JsonSerializerTests : HostedTestClusterEnsureDefaultStarted
{
    public JsonSerializerTests(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    [Fact, TestCategory("BVT"), TestCategory("Serialization")]
    public void Serialization_GrainId_RoundTrip()
    {
        var grainId = GrainId.Create("type", "key");
        var copy = HostedCluster.RoundTripSystemTextJsonSerialization(grainId);

        Assert.IsAssignableFrom<GrainId>(copy);
        Assert.Equal(grainId, copy);
    }

    [Fact, TestCategory("BVT"), TestCategory("Serialization")]
    public void Serialization_WithGrainIdType_RoundTrip()
    {
        var grainId = GrainId.Create("type", "key");
        WithGrainIdType data = new(grainId);

        var copy = HostedCluster.RoundTripSystemTextJsonSerialization(data);

        Assert.IsAssignableFrom<WithGrainIdType>(copy);
        Assert.Equal(grainId, copy.GrainId);
    }

    [Fact, TestCategory("BVT"), TestCategory("Serialization")]
    public void Serialization_WithGrainIdMapType_RoundTrip()
    {
        var grainId1 = GrainId.Create("type1", "key1");
        var grainId2 = GrainId.Create("type2", "key2");

        var map = new Dictionary<GrainId, int>();
        map.Add(grainId1, 1);
        map.Add(grainId2, 2);

        WithGrainIdMapType data = new(map);

        var copy = HostedCluster.RoundTripSystemTextJsonSerialization(data);

        Assert.IsAssignableFrom<WithGrainIdMapType>(copy);
        Assert.Equal(map, copy.Map);
    }

    [Fact, TestCategory("BVT"), TestCategory("Serialization")]
    public void Serialization_WithGrainIdConcurrentDictionaryType_RoundTrip()
    {
        var grainId = GrainId.Create("type", "key");
        var map = new ConcurrentDictionary<GrainId, int>();
        map.TryAdd(grainId, 1);

        WithGrainIdMapType data = new(map);

        var copy = HostedCluster.RoundTripSystemTextJsonSerialization(data);

        Assert.IsAssignableFrom<WithGrainIdMapType>(copy);
        Assert.Equal(map, copy.Map);
    }

    [Fact, TestCategory("BVT"), TestCategory("Serialization")]
    public void Serialization_WithGrainIdHashSetType_RoundTrip()
    {
        var grainId = GrainId.Create("type", "key");
        var set = new HashSet<GrainId>();
        set.Add(grainId);

        WithGrainIdSetType data = new(set);

        var copy = HostedCluster.RoundTripSystemTextJsonSerialization(data);

        Assert.IsAssignableFrom<WithGrainIdSetType>(copy);
        Assert.Equal(set, copy.Set);
    }
}
