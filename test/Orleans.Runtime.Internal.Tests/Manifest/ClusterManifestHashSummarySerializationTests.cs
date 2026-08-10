using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Manifest;

[TestCategory("BVT"), TestCategory("Serialization")]
public sealed class ClusterManifestHashSummarySerializationTests
{
    // ClusterManifestHashSummary is sent over the wire by GetClusterManifestHashSummary(). Its
    // SiloManifestHashes collection must use a type that has a serialization codec; a FrozenDictionary
    // has none, which previously caused the RPC to throw CodecNotFoundException (silently swallowed by
    // the caller). This test round-trips the type through the real serializer to lock that in.
    [Fact]
    public void ClusterManifestHashSummaryRoundTripsThroughSerializer()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();

        var siloA = SiloAddress.New(IPAddress.Loopback, 11111, 1);
        var siloB = SiloAddress.New(IPAddress.Loopback, 11112, 2);
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(3, 7),
            new Dictionary<SiloAddress, ManifestHash>
            {
                [siloA] = new ManifestHash("hash-a"),
                [siloB] = new ManifestHash("hash-b"),
            });

        var roundTripped = Assert.IsType<ClusterManifestHashSummary>(
            serializer.Deserialize<ClusterManifestHashSummary>(serializer.SerializeToArray(summary)));

        Assert.Equal(summary.Version, roundTripped.Version);
        Assert.Equal(summary.SiloManifestHashes.Count, roundTripped.SiloManifestHashes.Count);
        Assert.Equal(new ManifestHash("hash-a"), roundTripped.SiloManifestHashes[siloA]);
        Assert.Equal(new ManifestHash("hash-b"), roundTripped.SiloManifestHashes[siloB]);
    }
}
