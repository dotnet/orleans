using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.SiloMetadataTests;


[TestCategory("SiloMetadata")]
public class SiloMetadataConfigTests : TestClusterPerTest
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public static readonly List<KeyValuePair<string, string>> Metadata =
        [
            new("Orleans:Metadata:first", "1"),
            new("Orleans:Metadata:second", "2"),
            new("Orleans:Metadata:third", "3")
        ];

        public void Configure(ISiloBuilder hostBuilder)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(Metadata)
                .Build();
            hostBuilder.UseSiloMetadata(config);
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_CanBeSetAndRead()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        HostedCluster.AssertAllSiloMetadataMatchesOnAllSilos(SiloConfigurator.Metadata.Select(kv => kv.Key.Split(':').Last()).ToArray());
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_HasConfiguredValues()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();

        var first = HostedCluster.Silos.First();
        var firstSp = HostedCluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        var metadata = firstSiloMetadataCache.GetMetadata(first.SiloAddress);
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.Metadata);
        Assert.Equal(SiloConfigurator.Metadata.Count, metadata.Metadata.Count);
        foreach (var kv in SiloConfigurator.Metadata)
        {
            Assert.Equal(kv.Value, metadata.Metadata[kv.Key.Split(':').Last()]);
        }
    }
}

[TestCategory("SiloMetadata")]
public class SiloMetadataTests : TestClusterPerTest
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder.UseSiloMetadata(new Dictionary<string, string>
            {
                {"host.id", Guid.NewGuid().ToString()}
            });
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_CanBeSetAndRead()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        HostedCluster.AssertAllSiloMetadataMatchesOnAllSilos(["host.id"]);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_NewSilosHaveMetadata()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        await HostedCluster.StartAdditionalSiloAsync();
        HostedCluster.AssertAllSiloMetadataMatchesOnAllSilos(["host.id"]);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_RemovedSiloHasNoMetadata()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        HostedCluster.AssertAllSiloMetadataMatchesOnAllSilos(["host.id"]);
        var first = HostedCluster.Silos.First();
        var firstSp = HostedCluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();

        var second = HostedCluster.Silos.Skip(1).First();
        var metadata = firstSiloMetadataCache.GetMetadata(second.SiloAddress);
        Assert.NotNull(metadata);
        Assert.NotEmpty(metadata.Metadata);

        await HostedCluster.StopSiloAsync(second);
        metadata = firstSiloMetadataCache.GetMetadata(second.SiloAddress);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.Metadata);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_BadSiloAddressHasNoMetadata()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var first = HostedCluster.Silos.First();
        var firstSp = HostedCluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        var metadata = firstSiloMetadataCache.GetMetadata(SiloAddress.Zero);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.Metadata);
    }
}

public static class SiloMetadataTestExtensions
{
    public static void AssertAllSiloMetadataMatchesOnAllSilos(this TestCluster hostedCluster, string[] expectedKeys)
    {
        var exampleSiloMetadata = new Dictionary<SiloAddress, SiloMetadata>();
        var first = hostedCluster.Silos.First();
        var firstSp = hostedCluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        foreach (var otherSilo in hostedCluster.Silos)
        {
            var metadata = firstSiloMetadataCache.GetMetadata(otherSilo.SiloAddress);
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
                var metadata = siloMetadataCache.GetMetadata(otherSilo.SiloAddress);
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