using System.Net;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Xunit;

namespace UnitTests.PlacementFilterTests;

[TestCategory("Placement"), TestCategory("Filters"), TestCategory("SiloMetadata")]
public class PreferredMatchSiloMetadataPlacementFilterDirectorTests
{
    [Fact, TestCategory("Functional")]
    public void CanBeCreated()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var director = new PreferredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                { testLocalSiloAddress, SiloMetadata.Empty }
            }));
        Assert.NotNull(director);
    }

    [Fact, TestCategory("Functional")]
    public void CanBeCalled()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var director = new PreferredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testLocalSiloAddress, SiloMetadata.Empty}
            }));
        var result = director.Filter(new PreferredMatchSiloMetadataPlacementFilterStrategy(), default,
            new List<SiloAddress>() { testLocalSiloAddress }
        ).ToList();
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact, TestCategory("Functional")]
    public void FiltersToAllWhenNoEntry()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var testOtherSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1001, 1);
        var siloMetadata = new SiloMetadata();
        siloMetadata.AddMetadata("metadata.key", "something");
        var director = new PreferredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testOtherSiloAddress, SiloMetadata.Empty},
                {testLocalSiloAddress, siloMetadata},
            }));
        var result = director.Filter(new PreferredMatchSiloMetadataPlacementFilterStrategy(["metadata.key"], 1, 0), default,
            new List<SiloAddress>() { testOtherSiloAddress }).ToList();
        Assert.NotEmpty(result);
    }


    [Theory, TestCategory("Functional")]
    [InlineData(1, 3, "no.match")]
    [InlineData(2, 3, "no.match")]
    [InlineData( 1, 1, "one.match")]
    [InlineData( 2, 3, "one.match")]
    [InlineData( 1, 2, "two.match")]
    [InlineData( 2, 2, "two.match")]
    [InlineData( 3, 3, "two.match")]
    [InlineData( 1, 3, "all.match")]
    [InlineData( 2, 3, "all.match")]

    public void FiltersOnSingleMetadata(int minCandidates, int expectedCount, string key)
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var testOtherSiloAddress1 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1001, 1);
        var testOtherSiloAddress2 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1002, 1);
        var testOtherSiloAddress3 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1003, 1);
        var localSiloMetadata = new SiloMetadata();
        localSiloMetadata.AddMetadata("all.match", "match");
        localSiloMetadata.AddMetadata("one.match", "match");
        localSiloMetadata.AddMetadata("two.match", "match");
        localSiloMetadata.AddMetadata("no.match", "match");
        var otherSiloMetadata1 = new SiloMetadata();
        otherSiloMetadata1.AddMetadata("all.match", "match");
        otherSiloMetadata1.AddMetadata("one.match", "match");
        otherSiloMetadata1.AddMetadata("two.match", "match");
        otherSiloMetadata1.AddMetadata("no.match", "nomatch");
        var otherSiloMetadata2 = new SiloMetadata();
        otherSiloMetadata2.AddMetadata("all.match", "match");
        otherSiloMetadata2.AddMetadata("one.match", "nomatch");
        otherSiloMetadata2.AddMetadata("two.match", "match");
        otherSiloMetadata2.AddMetadata("no.match", "nomatch");
        var otherSiloMetadata3 = new SiloMetadata();
        otherSiloMetadata3.AddMetadata("all.match", "match");
        otherSiloMetadata3.AddMetadata("one.match", "nomatch");
        otherSiloMetadata3.AddMetadata("two.match", "nomatch");
        otherSiloMetadata3.AddMetadata("no.match", "nomatch");
        var director = new PreferredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testOtherSiloAddress1, otherSiloMetadata1},
                {testOtherSiloAddress2, otherSiloMetadata2},
                {testOtherSiloAddress3, otherSiloMetadata3},
                {testLocalSiloAddress, localSiloMetadata},
            }));
        var result = director.Filter(new PreferredMatchSiloMetadataPlacementFilterStrategy([key], minCandidates, 0), default,
            new List<SiloAddress>() { testOtherSiloAddress1, testOtherSiloAddress2, testOtherSiloAddress3 }).ToList();
        Assert.NotEmpty(result);
        Assert.Equal(expectedCount, result.Count);
    }

    [Theory, TestCategory("Functional")]
    [InlineData(1, 3, "no.match", "all.match")]
    [InlineData(1, 1, "no.match", "one.match", "two.match")]
    [InlineData(2, 2, "no.match", "one.match", "two.match")]
    [InlineData(3, 3, "no.match", "one.match", "two.match")]
    [InlineData(1, 3, "all.match", "no.match")]
    [InlineData(1, 1, "one.match", "all.match")]
    [InlineData(2, 3, "one.match", "all.match")]
    [InlineData(1, 1, "no.match", "one.match", "all.match")]
    [InlineData(2, 3, "no.match", "one.match", "all.match")]

    public void FiltersOnMultipleMetadata(int minCandidates, int expectedCount, params string[] keys)
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var testOtherSiloAddress1 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1001, 1);
        var testOtherSiloAddress2 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1002, 1);
        var testOtherSiloAddress3 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1003, 1);
        var localSiloMetadata = new SiloMetadata();
        localSiloMetadata.AddMetadata("all.match", "match");
        localSiloMetadata.AddMetadata("one.match", "match");
        localSiloMetadata.AddMetadata("two.match", "match");
        localSiloMetadata.AddMetadata("no.match", "match");
        var otherSiloMetadata1 = new SiloMetadata();
        otherSiloMetadata1.AddMetadata("all.match", "match");
        otherSiloMetadata1.AddMetadata("one.match", "match");
        otherSiloMetadata1.AddMetadata("two.match", "match");
        otherSiloMetadata1.AddMetadata("no.match", "not.match");
        var otherSiloMetadata2 = new SiloMetadata();
        otherSiloMetadata2.AddMetadata("all.match", "match");
        otherSiloMetadata2.AddMetadata("one.match", "nomatch");
        otherSiloMetadata2.AddMetadata("two.match", "match");
        otherSiloMetadata2.AddMetadata("no.match", "not.match");
        var otherSiloMetadata3 = new SiloMetadata();
        otherSiloMetadata3.AddMetadata("all.match", "match");
        otherSiloMetadata3.AddMetadata("one.match", "not.match");
        otherSiloMetadata3.AddMetadata("two.match", "not.match");
        otherSiloMetadata3.AddMetadata("no.match", "not.match");
        var director = new PreferredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testOtherSiloAddress1, otherSiloMetadata1},
                {testOtherSiloAddress2, otherSiloMetadata2},
                {testOtherSiloAddress3, otherSiloMetadata3},
                {testLocalSiloAddress, localSiloMetadata},
            }));
        var result = director.Filter(new PreferredMatchSiloMetadataPlacementFilterStrategy(keys, minCandidates, 0), default,
            new List<SiloAddress>() { testOtherSiloAddress1, testOtherSiloAddress2, testOtherSiloAddress3 }).ToList();
        Assert.NotEmpty(result);
        Assert.Equal(expectedCount, result.Count);
    }
}