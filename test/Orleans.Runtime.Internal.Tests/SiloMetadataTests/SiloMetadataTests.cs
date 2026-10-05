using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace UnitTests.SiloMetadataTests;

/// <summary>
/// Tests for silo metadata configuration, retrieval, and synchronization across cluster.
/// </summary>
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("SiloMetadata")]
public class SiloMetadataTests(SiloMetadataTests.Fixture fixture) : IClassFixture<SiloMetadataTests.Fixture>
{
    private static readonly TimeSpan MetadataConvergenceTimeout = TimeSpan.FromSeconds(30);

    private static readonly List<KeyValuePair<string, string?>> Metadata =
        [
            new("Orleans:Metadata:first", "1"),
            new("Orleans:Metadata:second", "2"),
            new("Orleans:Metadata:third", "3")
        ];

    public class Fixture : IAsyncLifetime
    {
        public DiagnosticEventCollector MetadataEvents { get; } = new(SiloMetadataEvents.ListenerName);
        public InProcessTestCluster Cluster { get; private set; } = null!;
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Cluster is { } cluster)
                {
                    await cluster.DisposeAsync();
                }
            }
            finally
            {
                MetadataEvents.Dispose();
            }
        }

        public async ValueTask InitializeAsync()
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
            await Cluster.WaitForClusterManifestToStabilizeAsync();
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_FromConfiguration_CanBeSetAndRead()
    {
        await fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(
            fixture.MetadataEvents,
            Metadata.Select(kv => kv.Key.Split(':').Last()).ToArray(),
            MetadataConvergenceTimeout,
            TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_HasConfiguredValues()
    {
        await fixture.Cluster.WaitForSiloMetadataConvergenceAsync(
            fixture.MetadataEvents,
            Metadata.Select(kv => kv.Key.Split(':').Last()).ToArray(),
            MetadataConvergenceTimeout,
            TestContext.Current.CancellationToken);
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
    public async Task SiloMetadata_CanBeSetAndRead()
    {
        await fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(
            fixture.MetadataEvents,
            ["host.id"],
            MetadataConvergenceTimeout,
            TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_NewSilosHaveMetadata()
    {
        await fixture.Cluster.StartAdditionalSiloAsync();
        await fixture.Cluster.WaitForLivenessToStabilizeAsync();
        await fixture.Cluster.WaitForClusterManifestToStabilizeAsync();
        await fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(
            fixture.MetadataEvents,
            ["host.id"],
            MetadataConvergenceTimeout,
            TestContext.Current.CancellationToken);
    }

    [Fact, TestCategory("Functional")]
    public async Task SiloMetadata_RemovedSiloHasNoMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Cluster.AssertAllSiloMetadataMatchesOnAllSilos(
            fixture.MetadataEvents,
            ["host.id"],
            MetadataConvergenceTimeout,
            cancellationToken);
        var first = fixture.Cluster.Silos.First();
        var firstSp = fixture.Cluster.GetSiloServiceProvider(first.SiloAddress);
        var firstSiloMetadataCache = firstSp.GetRequiredService<ISiloMetadataCache>();

        var second = fixture.Cluster.Silos.Skip(1).First();
        var metadata = firstSiloMetadataCache.GetSiloMetadata(second.SiloAddress);
        Assert.NotNull(metadata);
        Assert.NotEmpty(metadata.Metadata);

        var metadataRemoval = fixture.Cluster.WaitForSiloMetadataRemovalAsync(
            fixture.MetadataEvents,
            second.SiloAddress,
            MetadataConvergenceTimeout,
            cancellationToken);
        await fixture.Cluster.StopSiloAsync(second, cancellationToken);
        await metadataRemoval;
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
    public static async Task AssertAllSiloMetadataMatchesOnAllSilos(
        this InProcessTestCluster hostedCluster,
        DiagnosticEventCollector events,
        string[] expectedKeys,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await hostedCluster.WaitForSiloMetadataConvergenceAsync(events, expectedKeys, timeout, cancellationToken);

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
                Assert.True(
                    metadata.Metadata.ContainsKey(expectedKey),
                    $"Metadata cache on '{first.SiloAddress}' is missing key '{expectedKey}' for silo '{otherSilo.SiloAddress}'.");
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
                    Assert.True(
                        metadata.Metadata.ContainsKey(expectedKey),
                        $"Metadata cache on '{hostedClusterSilo.SiloAddress}' is missing key '{expectedKey}' for silo '{otherSilo.SiloAddress}'.");
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

    public static Task WaitForSiloMetadataConvergenceAsync(
        this InProcessTestCluster hostedCluster,
        DiagnosticEventCollector events,
        string[] expectedKeys,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var siloAddresses = hostedCluster.Silos.Select(static silo => silo.SiloAddress).ToArray();
        var convergenceTasks = siloAddresses.Select(observerSiloAddress =>
        {
            var serviceProvider = hostedCluster.GetSiloServiceProvider(observerSiloAddress);
            var cache = serviceProvider.GetRequiredService<ISiloMetadataCache>();
            return WaitForCacheStateAsync(
                events,
                observerSiloAddress,
                () => siloAddresses.All(siloAddress =>
                {
                    var metadata = cache.GetSiloMetadata(siloAddress);
                    return expectedKeys.All(metadata.Metadata.ContainsKey);
                }),
                $"contain keys [{string.Join(", ", expectedKeys)}] for silos [{string.Join(", ", siloAddresses.Select(static address => address.ToString()))}]",
                timeout,
                cancellationToken);
        });
        return Task.WhenAll(convergenceTasks);
    }

    public static Task WaitForSiloMetadataRemovalAsync(
        this InProcessTestCluster hostedCluster,
        DiagnosticEventCollector events,
        SiloAddress removedSiloAddress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var observers = hostedCluster.Silos
            .Select(static silo => silo.SiloAddress)
            .Where(siloAddress => !siloAddress.Equals(removedSiloAddress))
            .ToArray();
        var removalTasks = observers.Select(observerSiloAddress =>
        {
            var serviceProvider = hostedCluster.GetSiloServiceProvider(observerSiloAddress);
            var cache = serviceProvider.GetRequiredService<ISiloMetadataCache>();
            return WaitForCacheStateAsync(
                events,
                observerSiloAddress,
                () => cache.GetSiloMetadata(removedSiloAddress).Metadata.Count == 0,
                $"remove metadata for '{removedSiloAddress}'",
                timeout,
                cancellationToken);
        });
        return Task.WhenAll(removalTasks);
    }

    private static async Task WaitForCacheStateAsync(
        DiagnosticEventCollector events,
        SiloAddress observerSiloAddress,
        Func<bool> predicate,
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lastSequence = events
            .GetEvents(nameof(SiloMetadataEvents.CacheUpdated))
            .Select(static diagnosticEvent => diagnosticEvent.Payload)
            .OfType<SiloMetadataEvents.CacheUpdated>()
            .Where(updated => updated.ObserverSiloAddress.Equals(observerSiloAddress))
            .Select(static updated => updated.Sequence)
            .DefaultIfEmpty()
            .Max();
        var stopwatch = Stopwatch.StartNew();

        while (!predicate())
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateTimeoutException();
            }

            try
            {
                var diagnosticEvent = await events.WaitForEventAsync(
                    nameof(SiloMetadataEvents.CacheUpdated),
                    diagnosticEvent => diagnosticEvent.Payload is SiloMetadataEvents.CacheUpdated updated
                        && updated.ObserverSiloAddress.Equals(observerSiloAddress)
                        && updated.Sequence > lastSequence,
                    remaining,
                    cancellationToken);
                lastSequence = ((SiloMetadataEvents.CacheUpdated)diagnosticEvent.Payload!).Sequence;
            }
            catch (TimeoutException)
            {
                throw CreateTimeoutException();
            }
        }

        TimeoutException CreateTimeoutException()
        {
            var observedStates = events
                .GetEvents(nameof(SiloMetadataEvents.CacheUpdated))
                .Select(static diagnosticEvent => diagnosticEvent.Payload)
                .OfType<SiloMetadataEvents.CacheUpdated>()
                .Where(updated => updated.ObserverSiloAddress.Equals(observerSiloAddress))
                .Select(updated => $"sequence {updated.Sequence}, version {updated.MembershipVersion}: [{string.Join(", ", updated.CachedSilos)}]");
            return new TimeoutException(
                $"Timed out waiting for metadata cache on '{observerSiloAddress}' to {expectedState}. "
                + $"Observed states: {string.Join("; ", observedStates)}");
        }
    }
}
