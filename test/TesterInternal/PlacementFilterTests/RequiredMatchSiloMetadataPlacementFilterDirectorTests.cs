using System.Net;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement.Filtering;
using Xunit;

namespace UnitTests.PlacementFilterTests;

[TestCategory("Placement"), TestCategory("Filters"), TestCategory("SiloMetadata")]
public class RequiredMatchSiloMetadataPlacementFilterDirectorTests
{
    [Fact, TestCategory("Functional")]
    public void RequiredMatchSiloMetadataPlacementFilterDirector_CanBeCreated()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var director = new RequiredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testLocalSiloAddress, SiloMetadata.Empty}
            }));
        Assert.NotNull(director);
    }

    [Fact, TestCategory("Functional")]
    public void RequiredMatchSiloMetadataPlacementFilterDirector_CanBeCalled()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var director = new RequiredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testLocalSiloAddress, SiloMetadata.Empty}
            }));
        var result = director.Filter(new RequiredMatchSiloMetadataPlacementFilterStrategy(), default,
            new List<SiloAddress>() { testLocalSiloAddress }
        ).ToList();
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact, TestCategory("Functional")]
    public void RequiredMatchSiloMetadataPlacementFilterDirector_FiltersToNothingWhenNoEntry()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var testOtherSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1001, 1);
        var siloMetadata = new SiloMetadata();
        siloMetadata.AddMetadata("metadata.key", "something");
        var director = new RequiredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testOtherSiloAddress, SiloMetadata.Empty},
                {testLocalSiloAddress, siloMetadata},
            }));
        var result = director.Filter(new RequiredMatchSiloMetadataPlacementFilterStrategy(["metadata.key"], 0), default,
            new List<SiloAddress>() { testOtherSiloAddress }).ToList();
        Assert.Empty(result);
    }

    [Fact, TestCategory("Functional")]
    public void RequiredMatchSiloMetadataPlacementFilterDirector_FiltersToNothingWhenDifferentValue()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var testOtherSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1001, 1);
        var localSiloMetadata = new SiloMetadata();
        localSiloMetadata.AddMetadata("metadata.key", "local");
        var otherSiloMetadata = new SiloMetadata();
        otherSiloMetadata.AddMetadata("metadata.key", "other");
        var director = new RequiredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testOtherSiloAddress, otherSiloMetadata},
                {testLocalSiloAddress, localSiloMetadata},
            }));
        var result = director.Filter(new RequiredMatchSiloMetadataPlacementFilterStrategy(["metadata.key"], 0), default,
            new List<SiloAddress>() { testOtherSiloAddress }).ToList();
        Assert.Empty(result);
    }

    [Fact, TestCategory("Functional")]
    public void RequiredMatchSiloMetadataPlacementFilterDirector_FiltersToSiloWhenMatching()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var testOtherSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1001, 1);
        var localSiloMetadata = new SiloMetadata();
        localSiloMetadata.AddMetadata("metadata.key", "same");
        var otherSiloMetadata = new SiloMetadata();
        otherSiloMetadata.AddMetadata("metadata.key", "same");
        var director = new RequiredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testOtherSiloAddress, otherSiloMetadata},
                {testLocalSiloAddress, localSiloMetadata},
            }));
        var result = director.Filter(new RequiredMatchSiloMetadataPlacementFilterStrategy(["metadata.key"], 0), default,
            new List<SiloAddress>() { testOtherSiloAddress }).ToList();
        Assert.NotEmpty(result);
    }

    [Fact, TestCategory("Functional")]
    public void RequiredMatchSiloMetadataPlacementFilterDirector_FiltersToMultipleSilosWhenMatching()
    {
        var testLocalSiloAddress = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1000, 1);
        var testOtherSiloAddress1 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1001, 1);
        var testOtherSiloAddress2 = SiloAddress.New(IPAddress.Parse("1.1.1.1"), 1002, 1);
        var localSiloMetadata = new SiloMetadata();
        localSiloMetadata.AddMetadata("metadata.key", "same");
        var otherSiloMetadata1 = new SiloMetadata();
        otherSiloMetadata1.AddMetadata("metadata.key", "same");
        var otherSiloMetadata2 = new SiloMetadata();
        otherSiloMetadata2.AddMetadata("metadata.key", "same");
        var director = new RequiredMatchSiloMetadataPlacementFilterDirector(
            new TestLocalSiloDetails("name", "clusterId", "dnsHostName",
                testLocalSiloAddress,
                testLocalSiloAddress),
            new TestSiloMetadataCache(new Dictionary<SiloAddress, SiloMetadata>()
            {
                {testOtherSiloAddress1, otherSiloMetadata1},
                {testOtherSiloAddress2, otherSiloMetadata2},
                {testLocalSiloAddress, localSiloMetadata},
            }));
        var result = director.Filter(new RequiredMatchSiloMetadataPlacementFilterStrategy(["metadata.key"], 0), default,
            new List<SiloAddress>() { testOtherSiloAddress1, testOtherSiloAddress2 }).ToList();
        Assert.NotEmpty(result);
        Assert.Equal(2, result.Count);
    }

}