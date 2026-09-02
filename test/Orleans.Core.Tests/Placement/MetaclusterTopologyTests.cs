using System.Collections.Immutable;
using CsCheck;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Placement;

[TestArea("Placement")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("FullyQualifiedName", "UnitTests.Placement.MetaclusterTopologyTests")]
public sealed class MetaclusterTopologyTests
{
    private static readonly Gen<int[]> TopologyOrder = Gen.Int.Array[4];
    private static readonly Gen<int> MalformedTopologyCase = Gen.Int.Select(static value => (int)((uint)value % 9));

    [Fact]
    public async Task MetaclusterOptions_Defaults_AreDisabledAndFailClosed()
    {
        var options = new MetaclusterOptions();

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), options.ClusterOwnershipLeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(20), options.ClusterOwnershipLeaseRenewalWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), options.ClusterLocationCacheDuration);
        Assert.Empty(options.Clusters);
        Assert.Empty(options.ExportedSystemTargets);

        var provider = CreateProvider(new ClusterOptions { ServiceId = " ", ClusterId = "" }, options);
        var topology = await provider.GetTopology(TestContext.Current.CancellationToken);
        var localCluster = Assert.Single(topology.Clusters);

        Assert.Equal(new ClusterIdentity(ClusterOptions.DefaultServiceId, ClusterOptions.DefaultClusterId),
            new ClusterIdentity(topology.ServiceId, localCluster.Key));
        Assert.Equal(0, topology.Epoch);
        Assert.Equal(MetaclusterClusterState.Active, localCluster.Value.State);
        Assert.Empty(localCluster.Value.RelayEndpoints);
    }

    [Fact]
    public async Task MetaclusterOptionsValidator_AcceptsValidEnabledOptions()
    {
        var options = CreateValidOptions();
        options.Clusters.Add("west", [new Uri("https://west.example:8443/gateway")]);
        options.ExportedSystemTargets.Add("orleans.system.catalog");

        var exception = Record.Exception(() => CreateValidator(options).ValidateConfiguration());
        var topology = await CreateProvider(
            new ClusterOptions { ServiceId = "service", ClusterId = "local" },
            options).GetTopology(TestContext.Current.CancellationToken);

        Assert.Null(exception);
        Assert.Equal(2, options.Clusters.Count);
        Assert.Single(options.ExportedSystemTargets);
        Assert.Equal("service", topology.ServiceId);
        Assert.Equal(["east", "local", "west"], topology.Clusters.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MetaclusterOptionsValidator_AcceptsDisabledDefaultOptions()
    {
        var options = new MetaclusterOptions();

        var exception = Record.Exception(() => CreateValidator(options).ValidateConfiguration());

        Assert.Null(exception);
        Assert.False(options.Enabled);
        Assert.Empty(options.Clusters);
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsMissingLocalCluster()
    {
        var options = CreateValidOptions();
        options.Clusters.Remove("east");
        options.Clusters.Add(" ", [new Uri("https://unnamed.example/gateway")]);
        var clusterOptions = new ClusterOptions { ServiceId = "service", ClusterId = " " };

        var validatorException = Assert.Throws<OrleansConfigurationException>(
            () => CreateValidator(options).ValidateConfiguration());
        var providerException = Assert.Throws<OrleansConfigurationException>(
            () => CreateProvider(clusterOptions, CreateValidOptions()));

        Assert.Contains("identities", validatorException.Message, StringComparison.Ordinal);
        Assert.Contains("non-empty", validatorException.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ClusterOptions.ClusterId), providerException.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ClusterOptions.ServiceId), providerException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsTopologyKeyIdentityMismatch()
    {
        var clusters = EmptyClusters.Add("dictionary-key", CreateCluster("descriptor-id"));

        var exception = Assert.Throws<ArgumentException>(() => new MetaclusterTopology("service", 1, clusters));

        Assert.Equal("clusters", exception.ParamName);
        Assert.Contains("dictionary-key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("descriptor-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsDuplicateClusterIdentity()
    {
        var options = CreateValidOptions();
        var originalEndpoints = options.Clusters["east"];

        var exception = Assert.Throws<ArgumentException>(
            () => options.Clusters.Add("east", [new Uri("https://duplicate.example/gateway")]));

        Assert.Contains("east", exception.Message, StringComparison.Ordinal);
        Assert.Single(options.Clusters);
        Assert.Same(originalEndpoints, options.Clusters["east"]);
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsCrossServiceCluster()
    {
        var topology = new MetaclusterTopology(
            "service-a",
            1,
            EmptyClusters.Add("east", CreateCluster("east")));
        var topologyIdentities = topology.Clusters.Keys
            .Select(clusterId => new ClusterIdentity(topology.ServiceId, clusterId))
            .ToArray();

        Assert.Contains(new ClusterIdentity("service-a", "east"), topologyIdentities);
        Assert.DoesNotContain(new ClusterIdentity("service-b", "east"), topologyIdentities);
        Assert.All(topologyIdentities, identity => Assert.Equal(topology.ServiceId, identity.ServiceId));
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsRelativeOrMalformedEndpoint()
    {
        var relativeOptions = CreateValidOptions();
        relativeOptions.Clusters["east"] = [new Uri("relay/east", UriKind.Relative)];
        var nullEndpointOptions = CreateValidOptions();
        nullEndpointOptions.Clusters["east"] = [null!];
        var nullEndpointsOptions = CreateValidOptions();
        nullEndpointsOptions.Clusters["east"] = null!;

        var relativeException = Assert.Throws<OrleansConfigurationException>(
            () => CreateValidator(relativeOptions).ValidateConfiguration());
        var nullException = Assert.Throws<OrleansConfigurationException>(
            () => CreateValidator(nullEndpointOptions).ValidateConfiguration());
        var uninitializedException = Assert.Throws<OrleansConfigurationException>(
            () => CreateValidator(nullEndpointsOptions).ValidateConfiguration());

        Assert.Equal(relativeException.Message, nullException.Message);
        Assert.Contains("east", relativeException.Message, StringComparison.Ordinal);
        Assert.Contains("absolute URI", relativeException.Message, StringComparison.Ordinal);
        Assert.Contains("initialized", uninitializedException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsNonPositiveLeaseDuration()
    {
        foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1) })
        {
            var options = CreateValidOptions();
            options.ClusterOwnershipLeaseDuration = duration;

            var exception = Assert.Throws<OrleansConfigurationException>(
                () => CreateValidator(options).ValidateConfiguration());

            Assert.Contains(nameof(MetaclusterOptions.ClusterOwnershipLeaseDuration), exception.Message, StringComparison.Ordinal);
            Assert.Contains("positive", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsInvalidRenewalInterval()
    {
        foreach (var renewalWindow in new[]
        {
            TimeSpan.FromTicks(-1),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2)
        })
        {
            var options = CreateValidOptions();
            options.ClusterOwnershipLeaseDuration = TimeSpan.FromMinutes(1);
            options.ClusterOwnershipLeaseRenewalWindow = renewalWindow;

            var exception = Assert.Throws<OrleansConfigurationException>(
                () => CreateValidator(options).ValidateConfiguration());

            Assert.Contains(nameof(MetaclusterOptions.ClusterOwnershipLeaseRenewalWindow), exception.Message, StringComparison.Ordinal);
            Assert.Contains("shorter", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MetaclusterOptionsValidator_RejectsNegativeCacheDuration()
    {
        var options = CreateValidOptions();
        options.ClusterLocationCacheDuration = TimeSpan.FromTicks(-1);

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => CreateValidator(options).ValidateConfiguration());

        Assert.Contains(nameof(MetaclusterOptions.ClusterLocationCacheDuration), exception.Message, StringComparison.Ordinal);
        Assert.Contains("negative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaclusterTopology_UsesClusterIdentityComparer()
    {
        var topology = new MetaclusterTopology(
            "service",
            11,
            ImmutableDictionary<string, MetaclusterCluster>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("east", CreateCluster("east")));
        var equivalentIdentity = string.Concat("ea", "st");

        Assert.Equal(StringComparer.Ordinal, topology.Clusters.KeyComparer);
        Assert.Same(topology.Clusters["east"], topology.Clusters[equivalentIdentity]);
        Assert.False(topology.Clusters.ContainsKey("EAST"));
        Assert.Equal(new ClusterIdentity("service", "east"), new ClusterIdentity(topology.ServiceId, equivalentIdentity));
    }

    [Fact]
    public void MetaclusterTopology_SeparatesActiveDrainingAndRemovedClusters()
    {
        var topology = new MetaclusterTopology(
            "service",
            23,
            EmptyClusters
                .Add("active", new MetaclusterCluster(
                    "active",
                    MetaclusterClusterState.Active,
                    default,
                    metadata: null))
                .Add("draining", CreateCluster("draining", MetaclusterClusterState.Draining))
                .Add("removed", CreateCluster("removed", MetaclusterClusterState.Removed)));

        var active = topology.Clusters.Values.Where(cluster => cluster.State == MetaclusterClusterState.Active).Select(cluster => cluster.ClusterId);
        var draining = topology.Clusters.Values.Where(cluster => cluster.State == MetaclusterClusterState.Draining).Select(cluster => cluster.ClusterId);
        var removed = topology.Clusters.Values.Where(cluster => cluster.State == MetaclusterClusterState.Removed).Select(cluster => cluster.ClusterId);

        Assert.Equal(["active"], active);
        Assert.Equal(["draining"], draining);
        Assert.Equal(["removed"], removed);
        Assert.Equal(3, topology.Clusters.Count);
        Assert.Equal(23, topology.Epoch);
        Assert.Empty(topology.Clusters["active"].RelayEndpoints);
        Assert.Empty(topology.Clusters["active"].Metadata);
        Assert.Equal(StringComparer.Ordinal, topology.Clusters["active"].Metadata.KeyComparer);
    }

    [Fact]
    public void MetaclusterTopology_RejectsNullOrInconsistentDescriptors()
    {
        var nullDictionaryException = Assert.Throws<ArgumentNullException>(
            () => new MetaclusterTopology("service", 0, null!));
        var nullDescriptorException = Assert.Throws<ArgumentException>(
            () => new MetaclusterTopology("service", 0, EmptyClusters.Add("east", null!)));
        var inconsistentException = Assert.Throws<ArgumentException>(
            () => new MetaclusterTopology("service", 0, EmptyClusters.Add("east", CreateCluster("west"))));
        var nullEndpointsOptions = CreateValidOptions();
        nullEndpointsOptions.Clusters["east"] = null!;
        var providerException = Assert.Throws<OrleansConfigurationException>(
            () => CreateProvider(
                new ClusterOptions { ServiceId = "service", ClusterId = "local" },
                nullEndpointsOptions));

        Assert.Equal("clusters", nullDictionaryException.ParamName);
        Assert.Equal("clusters", nullDescriptorException.ParamName);
        Assert.Equal("clusters", inconsistentException.ParamName);
        Assert.Contains("no cluster descriptor", nullDescriptorException.Message, StringComparison.Ordinal);
        Assert.Contains("does not match", inconsistentException.Message, StringComparison.Ordinal);
        Assert.Contains("east", providerException.Message, StringComparison.Ordinal);
        Assert.Contains("initialized", providerException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaticMetaclusterTopologyProvider_ReturnsConfiguredSnapshot()
    {
        var clusterOptions = new ClusterOptions { ServiceId = "service", ClusterId = "local" };
        var options = CreateValidOptions();
        var eastEndpoints = options.Clusters["east"];
        var provider = CreateProvider(clusterOptions, options);

        options.Clusters["east"] = [new Uri("https://changed.example/gateway")];
        options.Clusters.Add("late", [new Uri("https://late.example/gateway")]);
        var first = await provider.GetTopology(TestContext.Current.CancellationToken);
        var second = await provider.GetTopology(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal("service", first.ServiceId);
        Assert.Equal(0, first.Epoch);
        Assert.Equal(["east", "local"], first.Clusters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(eastEndpoints, first.Clusters["east"].RelayEndpoints);
        Assert.Empty(first.Clusters["local"].RelayEndpoints);
        Assert.All(first.Clusters.Values, cluster => Assert.Equal(MetaclusterClusterState.Active, cluster.State));
        Assert.DoesNotContain("late", first.Clusters);
    }

    [Fact]
    public async Task StaticMetaclusterTopologyProvider_WatchWaitsUntilCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = CreateProvider(
            new ClusterOptions { ServiceId = "service", ClusterId = "local" },
            CreateValidOptions());
        await using var enumerator = provider.Watch(cancellation.Token).GetAsyncEnumerator(
            TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("service", enumerator.Current.ServiceId);
        var pendingUpdate = enumerator.MoveNextAsync().AsTask();
        Assert.False(pendingUpdate.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingUpdate);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task StaticMetaclusterTopologyProvider_WatchObservesPreCanceledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = CreateProvider(
            new ClusterOptions { ServiceId = "service", ClusterId = "local" },
            CreateValidOptions());
        await using var enumerator = provider.Watch(cancellation.Token).GetAsyncEnumerator(
            TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("service", enumerator.Current.ServiceId);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void CsCheck_TopologyConstruction_IsOrderInvariant()
    {
        var descriptors = new[]
        {
            CreateCluster("active", MetaclusterClusterState.Active, "https://active.example/gateway"),
            CreateCluster("draining", MetaclusterClusterState.Draining, "https://draining.example/gateway"),
            CreateCluster("removed", MetaclusterClusterState.Removed, "https://removed.example/gateway"),
            CreateCluster("backup", MetaclusterClusterState.Active, "https://backup.example/gateway")
        };
        var expected = CreateTopology(descriptors);

        TopologyOrder.Sample(
            orderKeys =>
            {
                var permutation = descriptors
                    .Select((descriptor, index) => (Descriptor: descriptor, Order: orderKeys[index]))
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.Descriptor.ClusterId, StringComparer.Ordinal)
                    .Select(item => item.Descriptor);
                var actual = CreateTopology(permutation);
                var history = $"seed=0N0XIzNsQ0O2; order=[{string.Join(",", orderKeys)}]";

                Assert.True(expected.Clusters.Keys.Order(StringComparer.Ordinal)
                    .SequenceEqual(actual.Clusters.Keys.Order(StringComparer.Ordinal)), history);
                Assert.True(GetClustersInState(expected, MetaclusterClusterState.Active)
                    .SequenceEqual(GetClustersInState(actual, MetaclusterClusterState.Active)), history);
                Assert.True(GetClustersInState(expected, MetaclusterClusterState.Draining)
                    .SequenceEqual(GetClustersInState(actual, MetaclusterClusterState.Draining)), history);
                Assert.True(GetClustersInState(expected, MetaclusterClusterState.Removed)
                    .SequenceEqual(GetClustersInState(actual, MetaclusterClusterState.Removed)), history);

                foreach (var expectedEntry in expected.Clusters)
                {
                    var actualEntry = actual.Clusters[expectedEntry.Key];
                    Assert.True(expectedEntry.Value.RelayEndpoints.SequenceEqual(actualEntry.RelayEndpoints), history);
                    Assert.True(expectedEntry.Value.Metadata.SequenceEqual(actualEntry.Metadata), history);
                }
            },
            seed: "0N0XIzNsQ0O2",
            iter: 120,
            threads: 1,
            print: static values => $"[{string.Join(",", values)}]");
    }

    [Fact]
    public void CsCheck_MalformedTopologyInput_IsRejected()
    {
        foreach (var testCase in Enumerable.Range(0, 9))
        {
            AssertMalformedTopologyInput(testCase, "exhaustive");
        }

        MalformedTopologyCase.Sample(
            testCase => AssertMalformedTopologyInput(testCase, "seed=0N0XIzNsQ0O3"),
            seed: "0N0XIzNsQ0O3",
            iter: 180,
            threads: 1,
            print: static testCase => $"malformed-case={testCase}");
    }

    private static void AssertMalformedTopologyInput(int testCase, string history)
    {
        var exception = Record.Exception(CreateMalformedTopologyInput(testCase));
        Assert.True(
            exception is ArgumentException,
            $"{history}; malformed-case={testCase}; exception={exception}");
    }

    private static ImmutableDictionary<string, MetaclusterCluster> EmptyClusters =>
        ImmutableDictionary<string, MetaclusterCluster>.Empty.WithComparers(StringComparer.Ordinal);

    private static MetaclusterOptions CreateValidOptions()
    {
        var result = new MetaclusterOptions
        {
            Enabled = true,
            ClusterOwnershipLeaseDuration = TimeSpan.FromMinutes(2),
            ClusterOwnershipLeaseRenewalWindow = TimeSpan.FromSeconds(30),
            ClusterLocationCacheDuration = TimeSpan.FromSeconds(15)
        };
        result.Clusters.Add("east", [new Uri("https://east.example:8443/gateway")]);
        return result;
    }

    private static MetaclusterOptionsValidator CreateValidator(MetaclusterOptions options) =>
        new(Options.Create(options));

    private static StaticMetaclusterTopologyProvider CreateProvider(
        ClusterOptions clusterOptions,
        MetaclusterOptions metaclusterOptions) =>
        new(Options.Create(clusterOptions), Options.Create(metaclusterOptions));

    private static MetaclusterCluster CreateCluster(
        string clusterId,
        MetaclusterClusterState state = MetaclusterClusterState.Active,
        string endpoint = "https://relay.example/gateway") =>
        new(
            clusterId,
            state,
            [new Uri(endpoint)],
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("region", clusterId));

    private static MetaclusterTopology CreateTopology(IEnumerable<MetaclusterCluster> descriptors)
    {
        var clusters = EmptyClusters;
        foreach (var descriptor in descriptors)
        {
            clusters = clusters.Add(descriptor.ClusterId, descriptor);
        }

        return new MetaclusterTopology("service", 42, clusters);
    }

    private static string[] GetClustersInState(MetaclusterTopology topology, MetaclusterClusterState state) =>
        topology.Clusters.Values
            .Where(cluster => cluster.State == state)
            .Select(cluster => cluster.ClusterId)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static Action CreateMalformedTopologyInput(int testCase) => testCase switch
    {
        0 => () => _ = new MetaclusterTopology("", 0, EmptyClusters),
        1 => () => _ = new MetaclusterTopology("service", -1, EmptyClusters),
        2 => () => _ = new MetaclusterTopology("service", 0, null!),
        3 => () => _ = new MetaclusterTopology("service", 0, EmptyClusters.Add("east", null!)),
        4 => () => _ = new MetaclusterTopology("service", 0, EmptyClusters.Add("east", CreateCluster("west"))),
        5 => () => _ = new MetaclusterCluster("", MetaclusterClusterState.Active, []),
        6 => () => _ = new MetaclusterCluster("east", (MetaclusterClusterState)byte.MaxValue, []),
        7 => () => _ = new MetaclusterCluster(
            "east",
            MetaclusterClusterState.Active,
            [new Uri("relay/east", UriKind.Relative)]),
        _ => () => _ = new MetaclusterCluster(
            "east",
            MetaclusterClusterState.Active,
            [null!])
    };

    [Fact]
    public void CsCheck_TopologyConstruction_PreservesFullSnapshotAcrossInsertionOrders()
    {
        Gen.Int.Array[8].Sample(
            values =>
            {
                var descriptors = new[]
                {
                    CreateCluster("alpha", (MetaclusterClusterState)((uint)values[4] % 3), "https://alpha.example/gateway"),
                    CreateCluster("beta", (MetaclusterClusterState)((uint)values[5] % 3), "https://beta.example/gateway"),
                    CreateCluster("gamma", (MetaclusterClusterState)((uint)values[6] % 3), "https://gamma.example/gateway"),
                    CreateCluster("delta", (MetaclusterClusterState)((uint)values[7] % 3), "https://delta.example/gateway")
                };
                var expected = CreateTopology(descriptors);
                var actual = CreateTopology(
                    descriptors
                        .Select((descriptor, index) => (Descriptor: descriptor, Order: values[index]))
                        .OrderBy(item => item.Order)
                        .ThenBy(item => item.Descriptor.ClusterId, StringComparer.Ordinal)
                        .Select(item => item.Descriptor));
                var history = $"values=[{string.Join(",", values)}]";

                Assert.True(
                    Snapshot(expected).SequenceEqual(Snapshot(actual), StringComparer.Ordinal),
                    history);
                Assert.Equal(expected.ServiceId, actual.ServiceId);
                Assert.Equal(expected.Epoch, actual.Epoch);
                Assert.Equal(StringComparer.Ordinal, actual.Clusters.KeyComparer);
            },
            seed: "0N0XIzNsQ0P2T1",
            iter: 128,
            threads: 1,
            print: static values => $"order-and-state=[{string.Join(",", values)}]");

        static string[] Snapshot(MetaclusterTopology topology) =>
            topology.Clusters
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry =>
                    $"{entry.Key}|{entry.Value.ClusterId}|{entry.Value.State}|"
                    + $"{string.Join(",", entry.Value.RelayEndpoints)}|"
                    + $"{string.Join(",", entry.Value.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}")
                .ToArray();
    }
}
