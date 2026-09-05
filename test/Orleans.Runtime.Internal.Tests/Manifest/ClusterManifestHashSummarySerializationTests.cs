using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.Manifest;

[TestCategory("BVT"), TestCategory("Serialization")]
public sealed class ClusterManifestHashSummarySerializationTests
{
    [Fact]
    public void ManifestRpcContractsExposeCancellationTokens()
    {
        foreach (var contract in new[] { typeof(IClusterManifestSystemTarget), typeof(ISiloManifestSystemTarget) })
        {
            foreach (var method in contract.GetMethods())
            {
                var parameters = method.GetParameters();
                var cancellation = Assert.Single(parameters, parameter => parameter.ParameterType == typeof(CancellationToken));
                Assert.Equal("cancellationToken", cancellation.Name);
                Assert.Equal(parameters[^1], cancellation);
                if (method.Name is nameof(IClusterManifestSystemTarget.GetClusterManifestHashSummary)
                    or nameof(IClusterManifestSystemTarget.GetSiloManifestHash)
                    or nameof(IClusterManifestSystemTarget.GetSiloManifestByHash))
                {
                    Assert.False(cancellation.HasDefaultValue);
                }
            }
        }
    }

    [Fact]
    public void ManifestHashCalculatorReusesHashForImmutableManifest()
    {
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

        var first = ManifestHashCalculator.ComputeHash(manifest);
        var second = ManifestHashCalculator.ComputeHash(manifest);

        Assert.Same(first.Value, second.Value);
        Assert.Equal("AC3C68A3F09A51744D328E7EE3D099A85C190B600BD88BFE132DD63B74CCA2BB", first.Value);
    }

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

    [Fact]
    public void ClusterManifestHashSummaryRoundTripsHashAndSiloAddress()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var silo = SiloAddress.New(IPAddress.Parse("127.0.0.42"), 23456, 7);
        var expectedHash = new ManifestHash("sha256:0123456789abcdef");
        var summary = new ClusterManifestHashSummary(
            new MajorMinorVersion(11, 13),
            new Dictionary<SiloAddress, ManifestHash> { [silo] = expectedHash });

        var result = serializer.Deserialize<ClusterManifestHashSummary>(
            serializer.SerializeToArray(summary));

        Assert.Equal(new MajorMinorVersion(11, 13), result!.Version);
        var entry = Assert.Single(result.SiloManifestHashes);
        Assert.Equal(silo, entry.Key);
        Assert.Equal(expectedHash, entry.Value);
        Assert.Equal("sha256:0123456789abcdef", entry.Value.Value);
    }

    [Fact]
    public void ClusterManifestHashFetchContractPreservesManifestHashIdentity()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var expected = new ManifestHash("sha256:fedcba9876543210");

        var result = serializer.Deserialize<ManifestHash>(serializer.SerializeToArray(expected));
        var method = typeof(IClusterManifestSystemTarget).GetMethod(
            nameof(IClusterManifestSystemTarget.GetSiloManifestByHash));

        Assert.Equal(expected, result);
        Assert.Equal("sha256:fedcba9876543210", result.Value);
        Assert.NotNull(method);
        Assert.Equal(typeof(ValueTask<GrainManifest?>), method!.ReturnType);
        Assert.Collection(
            method.GetParameters(),
            parameter =>
            {
                Assert.Equal("hash", parameter.Name);
                Assert.Equal(typeof(ManifestHash), parameter.ParameterType);
            },
            parameter =>
            {
                Assert.Equal("cancellationToken", parameter.Name);
                Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
            });
    }
}
