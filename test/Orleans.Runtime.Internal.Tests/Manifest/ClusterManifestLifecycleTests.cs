using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest;

[TestSuite("BVT"), TestProvider("None")]
[TestCategory("BVT"), TestCategory("Manifest")]
public sealed class ClusterManifestLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledProviders_ConvergeAcrossJoinsDeparturesAndRollingModeChanges(bool mixedModes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overrides = new ConcurrentDictionary<string, bool>();
        var builder = new InProcessTestClusterBuilder(2);
        builder.ConfigureHost(host => TestDefaultConfiguration.ConfigureHostConfiguration(host.Configuration));
        builder.ConfigureSilo((specific, silo) =>
        {
            var enabled = overrides.TryGetValue(specific.SiloName, out var configured)
                ? configured : !mixedModes || specific.SiloName == "Silo_0";
            silo.Configure<ClusterManifestOptions>(options => options.EnableContentAddressedRetrieval = enabled);
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);
        await AssertConvergedAsync(cluster, "initial deployment", cancellationToken);
        Assert.True(cluster.Silos[0].ServiceProvider.GetRequiredService<IOptions<ClusterManifestOptions>>().Value.EnableContentAddressedRetrieval);
        Assert.Equal(!mixedModes, cluster.Silos[1].ServiceProvider.GetRequiredService<IOptions<ClusterManifestOptions>>().Value.EnableContentAddressedRetrieval);

        var joined = await cluster.StartAdditionalSiloAsync().WaitAsync(cancellationToken);
        await AssertConvergedAsync(cluster, "additional silo join", cancellationToken);
        await cluster.StopSiloAsync(joined, cancellationToken);
        await AssertConvergedAsync(cluster, "additional silo departure", cancellationToken);

        var rolling = cluster.Silos[1];
        var previousAddress = rolling.SiloAddress;
        overrides[rolling.Name] = mixedModes;
        rolling = Assert.IsType<InProcessSiloHandle>(await cluster.RestartSiloAsync(rolling).WaitAsync(cancellationToken));
        Assert.NotEqual(previousAddress, rolling.SiloAddress);
        Assert.Equal(mixedModes, rolling.ServiceProvider.GetRequiredService<IOptions<ClusterManifestOptions>>().Value.EnableContentAddressedRetrieval);
        await AssertConvergedAsync(cluster, "rolling mode change", cancellationToken);

        overrides[rolling.Name] = !mixedModes;
        rolling = Assert.IsType<InProcessSiloHandle>(await cluster.RestartSiloAsync(rolling).WaitAsync(cancellationToken));
        Assert.Equal(!mixedModes, rolling.ServiceProvider.GetRequiredService<IOptions<ClusterManifestOptions>>().Value.EnableContentAddressedRetrieval);
        await AssertConvergedAsync(cluster, "rolling mode rollback", cancellationToken);
    }

    private static async Task AssertConvergedAsync(InProcessTestCluster cluster, string phase, CancellationToken cancellationToken)
    {
        var silos = cluster.GetActiveSilos().ToArray();
        var expected = silos.ToDictionary(silo => silo.SiloAddress, silo => silo.ServiceProvider.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var observations = silos.Select(async silo =>
        {
            var provider = silo.ServiceProvider.GetRequiredService<IClusterManifestProvider>();
            await using var updates = provider.Updates.GetAsyncEnumerator(deadline.Token);
            while (await updates.MoveNextAsync())
            {
                var current = updates.Current;
                if (current.Silos.Count == expected.Count && expected.All(entry =>
                    current.Silos.TryGetValue(entry.Key, out var manifest) && manifest.Equals(entry.Value)))
                {
                    Assert.Equal(provider.LocalGrainManifest, current.Silos[silo.SiloAddress]);
                    return;
                }
            }

            Assert.Fail($"Manifest update stream ended during {phase} on {silo.SiloAddress}.");
        }).ToArray();

        try
        {
            await Task.WhenAll(observations);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Manifest convergence failed during {phase}. Expected: {string.Join(", ", expected.Keys)}. "
                + string.Join("; ", silos.Select(silo =>
                    $"{silo.SiloAddress}: {string.Join(", ", silo.ServiceProvider.GetRequiredService<IClusterManifestProvider>().Current.Silos.Keys)}")));
        }
    }
}
