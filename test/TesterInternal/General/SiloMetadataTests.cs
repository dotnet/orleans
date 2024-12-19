using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.General;

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
        await this.HostedCluster.WaitForLivenessToStabilizeAsync();
        AssertAllSiloMetadataMatchesOnAllSilos();
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_NewSilosHaveMetadata()
    {
        await this.HostedCluster.WaitForLivenessToStabilizeAsync();
        await HostedCluster.StartAdditionalSiloAsync();
        AssertAllSiloMetadataMatchesOnAllSilos();
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_RemovedSiloHasNoMetadata()
    {
        await this.HostedCluster.WaitForLivenessToStabilizeAsync();
        AssertAllSiloMetadataMatchesOnAllSilos();
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
        await this.HostedCluster.WaitForLivenessToStabilizeAsync();
        var first = this.HostedCluster.Silos.First();
        var firstSp = HostedCluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        var metadata = firstSiloMetadataCache.GetMetadata(SiloAddress.Zero);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.Metadata);
    }

    private void AssertAllSiloMetadataMatchesOnAllSilos()
    {
        var exampleSiloMetadata = new Dictionary<SiloAddress, SiloMetadata>();
        var first = this.HostedCluster.Silos.First();
        var firstSp = HostedCluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();
        foreach (var otherSilo in this.HostedCluster.Silos)
        {
            var metadata = firstSiloMetadataCache.GetMetadata(otherSilo.SiloAddress);
            Assert.NotNull(metadata);
            Assert.NotNull(metadata.Metadata);
            Assert.True(metadata.Metadata.ContainsKey("host.id"));
            exampleSiloMetadata.Add(otherSilo.SiloAddress, metadata);
        }
        foreach (var hostedClusterSilo in this.HostedCluster.Silos.Skip(1))
        {
            var sp = HostedCluster.GetSiloServiceProvider(hostedClusterSilo.SiloAddress);
            var siloMetadataCache = sp.GetRequiredService<ISiloMetadataCache>();
            var remoteMetadata = new Dictionary<SiloAddress, SiloMetadata>();
            foreach (var otherSilo in this.HostedCluster.Silos)
            {
                var metadata = siloMetadataCache.GetMetadata(otherSilo.SiloAddress);
                Assert.NotNull(metadata);
                Assert.NotNull(metadata.Metadata);
                Assert.True(metadata.Metadata.ContainsKey("host.id"));
                remoteMetadata.Add(otherSilo.SiloAddress, metadata);
            }
            //Assert that the two dictionaries have the same keys and the values for those keys are the same
            Assert.Equal(exampleSiloMetadata.Count, remoteMetadata.Count);
            foreach (var kvp in exampleSiloMetadata)
            {
                Assert.Equal(kvp.Value.Metadata.Count, remoteMetadata[kvp.Key].Metadata.Count);
                foreach (var kvp2 in kvp.Value.Metadata)
                {
                    Assert.True(remoteMetadata[kvp.Key].Metadata.TryGetValue(kvp2.Key, out var value), $"Key '{kvp2.Key}' not found in actual dictionary.");
                    Assert.Equal(kvp2.Value, value);
                }
            }
        }
    }
}