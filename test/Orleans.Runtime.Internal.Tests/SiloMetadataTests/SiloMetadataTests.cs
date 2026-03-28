using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.SiloMetadataTests;

/// <summary>
/// Tests for silo metadata configuration, retrieval, and synchronization across cluster.
/// </summary>
[TestCategory("SiloMetadata")]
public class SiloMetadataTests(SiloMetadataTests.Fixture fixture) : IClassFixture<SiloMetadataTests.Fixture>
{
    private static readonly List<KeyValuePair<string, string>> Metadata =
        [
            new("Orleans:Metadata:first", "1"),
            new("Orleans:Metadata:second", "2"),
            new("Orleans:Metadata:third", "3")
        ];

    public class Fixture : IAsyncLifetime
    {
        public InProcessTestCluster Cluster { get; private set; }
        public async Task DisposeAsync()
        {
            if (Cluster is { } cluster)
            {
                await cluster.DisposeAsync();
            }
        }

        public async Task InitializeAsync()
        {
            var builder = new InProcessTestClusterBuilder(3);
            builder.ConfigureSiloHost((options, hostBuilder) =>
            {
                hostBuilder.Configuration.AddInMemoryCollection(Metadata);
            });

            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder
                .UseSiloMetadata()
                .UseSiloMetadata(new Dictionary<string, string>
                {
                    {"host.id", Guid.NewGuid().ToString()}
                });
            });

            Cluster = builder.Build();
            await Cluster.DeployAsync();
            await Cluster.WaitForLivenessToStabilizeAsync();
        }
    }

    [Fact, TestCategory("Functional")]
    public void SiloMetadata_FromConfiguration_CanBeSetAndRead()
    {
        fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(Metadata.Select(kv => kv.Key.Split(':').Last()).ToArray());
    }

    [Fact, TestCategory("Functional")]
    public void SiloMetadata_HasConfiguredValues()
    {
        var first = fixture.Cluster.Silos.First();
        var firstSp = fixture.Cluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        var metadata = firstSiloMetadataCache.GetSiloMetadata(first.SiloAddress);
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.Metadata);
        Assert.True(metadata.Metadata.Count >= Metadata.Count);
        foreach (var kv in Metadata)
        {
            Assert.Equal(kv.Value, metadata.Metadata[kv.Key.Split(':').Last()]);
        }
    }

    [Fact, TestCategory("Functional")]
    public void SiloMetadata_CanBeSetAndRead()
    {
        fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(["host.id"]);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_NewSilosHaveMetadata()
    {
        await fixture.Cluster.StartAdditionalSiloAsync();
        await fixture.Cluster.WaitForLivenessToStabilizeAsync();
        fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(["host.id"]);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_RemovedSiloHasNoMetadata()
    {
        fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(["host.id"]);
        var first = fixture.Cluster.Silos.First();
        var firstSp = fixture.Cluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();

        var second = fixture.Cluster.Silos.Skip(1).First();
        var metadata = firstSiloMetadataCache.GetSiloMetadata(second.SiloAddress);
        Assert.NotNull(metadata);
        Assert.NotEmpty(metadata.Metadata);

        await fixture.Cluster.StopSiloAsync(second);
        metadata = firstSiloMetadataCache.GetSiloMetadata(second.SiloAddress);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.Metadata);
    }

    [Fact, TestCategory("Functional")]
    public void SiloMetadata_BadSiloAddressHasNoMetadata()
    {
        var first = fixture.Cluster.Silos.First();
        var firstSp = fixture.Cluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        var metadata = firstSiloMetadataCache.GetSiloMetadata(SiloAddress.Zero);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.Metadata);
    }
}

public static class SiloMetadataTestExtensions
{
    public static void AssertAllSiloMetadataMatchesOnAllSilos(this InProcessTestCluster hostedCluster, string[] expectedKeys)
    {
        var exampleSiloMetadata = new Dictionary<SiloAddress, SiloMetadata>();
        var first = hostedCluster.Silos.First();
        var firstSp = hostedCluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        foreach (var otherSilo in hostedCluster.Silos)
        {
            var metadata = firstSiloMetadataCache.GetSiloMetadata(otherSilo.SiloAddress);
            Assert.NotNull(metadata);
            Assert.NotNull(metadata.Metadata);
            foreach (var expectedKey in expectedKeys)
            {
                Assert.True(metadata.Metadata.ContainsKey(expectedKey));
            }
            exampleSiloMetadata.Add(otherSilo.SiloAddress, metadata);
        }

        foreach (var hostedClusterSilo in hostedCluster.Silos.Skip(1))
        {
            var sp = hostedCluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
            var siloMetadataCache = sp.GetRequiredService<ISiloMetadataCache>();
            var remoteMetadata = new Dictionary<SiloAddress, SiloMetadata>();
            foreach (var otherSilo in hostedCluster.Silos)
            {
                var metadata = siloMetadataCache.GetSiloMetadata(otherSilo.SiloAddress);
                Assert.NotNull(metadata);
                Assert.NotNull(metadata.Metadata);
                foreach (var expectedKey in expectedKeys)
                {
                    Assert.True(metadata.Metadata.ContainsKey(expectedKey));
                }
                remoteMetadata.Add(otherSilo.SiloAddress, metadata);
            }

            //Assert that the two dictionaries have the same keys and the values for those keys are the same
            Assert.Equal(exampleSiloMetadata.Count, remoteMetadata.Count);
            foreach (var kvp in exampleSiloMetadata)
            {
                Assert.Equal(kvp.Value.Metadata.Count, remoteMetadata[kvp.Key].Metadata.Count);
                foreach (var kvp2 in kvp.Value.Metadata)
                {
                    Assert.True(remoteMetadata[kvp.Key].Metadata.TryGetValue(kvp2.Key, out var value),
                        $"Key '{kvp2.Key}' not found in actual dictionary.");
                    Assert.Equal(kvp2.Value, value);
                }
            }
        }
    }
}