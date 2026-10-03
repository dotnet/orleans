using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Utilities;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class ClusterServiceViewProviderTests
{
    private static readonly SiloAddress Silo = SiloAddress.FromParsableString("127.0.0.1:11111@1");

    [Fact]
    public async Task KeyedProvidersIsolateServiceConfigurationAndEpochs()
    {
        using var membership = new MembershipSource();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>("orders", (_, _) =>
            CreateProvider(membership, "orders", partitions: 1, epoch: 3));
        services.AddKeyedSingleton<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>("jobs", (_, _) =>
            CreateProvider(membership, "jobs", partitions: 4, epoch: 7));
        await using var serviceProvider = services.BuildServiceProvider();
        var orders = serviceProvider.GetRequiredKeyedService<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>("orders");
        var jobs = serviceProvider.GetRequiredKeyedService<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>("jobs");

        Assert.NotSame(orders, jobs);
        Assert.Same(orders, serviceProvider.GetRequiredKeyedService<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>("orders"));
        membership.Publish(Snapshot(5));
        var orderView = Assert.IsType<MembershipBasedClusterServiceView>(
            await orders.RefreshAtLeastAsync(new(3, new(5)), TestContext.Current.CancellationToken));
        var jobView = Assert.IsType<MembershipBasedClusterServiceView>(
            await jobs.RefreshAtLeastAsync(new(7, new(5)), TestContext.Current.CancellationToken));

        Assert.Equal(new ClusterServiceViewId(3, new(5)), orderView.Id);
        Assert.Equal(new ClusterServiceViewId(7, new(5)), jobView.Id);
        Assert.Equal("orders", orderView.Configuration.ServiceId);
        Assert.Equal("jobs", jobView.Configuration.ServiceId);
        Assert.Single(orderView.Topology.GetMemberRangesByPartition(Silo));
        Assert.Equal(4, jobView.Topology.GetMemberRangesByPartition(Silo).Length);
        Assert.Equal(orderView.ClusterMembershipSnapshot, jobView.ClusterMembershipSnapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task MembershipProviderRejectsRefreshAcrossProviderEpochs(long requestedEpoch)
    {
        using var membership = new MembershipSource();
        await using var provider = CreateProvider(membership, "service", partitions: 1, epoch: 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.RefreshViewAsync(new(requestedEpoch, new(100)), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains($"cannot refresh to epoch {requestedEpoch}", error.Message);
        Assert.Equal(0, membership.RefreshCalls);
    }

    [Fact]
    public async Task ForcedRefreshWaitsForTheMembershipSnapshotObservedByTheSource()
    {
        using var membership = new MembershipSource();
        await using var provider = CreateProvider(membership, "service", partitions: 1, epoch: 2);
        membership.Publish(Snapshot(9));

        var view = await provider.RefreshViewAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterServiceViewId(2, new(9)), view.Id);
        Assert.Same(membership.CurrentSnapshot, view.ClusterMembershipSnapshot);
        Assert.Equal(1, membership.RefreshCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectoryUsesItsKeyedProviderAndContainerOwnsTheProvider(bool registerBeforeDirectory)
    {
        using var membership = new MembershipSource();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Substitute.For<IInternalGrainFactory>());
        var siloBuilder = Substitute.For<ISiloBuilder>();
        siloBuilder.Services.Returns(services);
        if (registerBeforeDirectory)
        {
            RegisterProvider();
        }

#pragma warning disable ORLEANSEXP003
        siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        if (!registerBeforeDirectory)
        {
            RegisterProvider();
        }

        var serviceProvider = services.BuildServiceProvider();
        try
        {
            var provider = Assert.IsType<MembershipBasedClusterServiceViewProvider>(
                serviceProvider.GetRequiredKeyedService<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>(DirectoryMembershipSnapshot.ServiceId));
            var directory = serviceProvider.GetRequiredService<DirectoryMembershipService>();
            Assert.Same(membership, directory.ClusterMembershipService);
            Assert.Equal(3, directory.PartitionsPerSilo);
            membership.Publish(Snapshot(4));

            var view = await directory.RefreshViewAsync(new(4), TestContext.Current.CancellationToken);

            Assert.Equal(new MembershipVersion(4), view.Version);
            Assert.Equal(provider.CurrentView.Id, view.ViewId);
            Assert.Equal(3, view.GetMemberRangesByPartition(Silo).Length);
        }
        finally
        {
            // Disposing both registered services must not dispose their shared provider twice.
            await serviceProvider.DisposeAsync();
        }

        void RegisterProvider() => services.AddKeyedSingleton<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>(
            DirectoryMembershipSnapshot.ServiceId,
            (_, _) => CreateProvider(membership, DirectoryMembershipSnapshot.ServiceId, partitions: 3, epoch: 0));
    }

    [Fact]
    public async Task DirectoryRejectsAnEpochItsMembershipWireContractCannotRepresent()
    {
        using var membership = new MembershipSource();
        await using var provider = CreateProvider(membership, DirectoryMembershipSnapshot.ServiceId, partitions: 1, epoch: 1);

        var error = Assert.Throws<ArgumentException>(() => new DirectoryMembershipService(
            provider, null!, NullLogger<DirectoryMembershipService>.Instance));

        Assert.Contains("membership-version wire contract", error.Message);
    }

    [Fact]
    public void DirectoryRejectsAProviderWithoutTheMembershipDerivedWireMapping()
    {
        var provider = Substitute.For<IClusterServiceViewProvider<ClusterServiceViewId, MembershipBasedClusterServiceView>>();

        var error = Assert.Throws<ArgumentException>(() => new DirectoryMembershipService(
            provider, null!, NullLogger<DirectoryMembershipService>.Instance));

        Assert.Contains("membership-derived view provider", error.Message);
    }

    [Fact]
    public async Task DirectoryProjectionFailureReachesRefreshAndSubscribers()
    {
        using var membership = new MembershipSource();
        await using var provider = CreateProvider(membership, DirectoryMembershipSnapshot.ServiceId, 1, 0);
        var factory = Substitute.For<IInternalGrainFactory>();
        var failure = new InvalidOperationException("Partition reference construction failed.");
        factory.GetSystemTarget<IGrainDirectoryPartition>(Arg.Any<GrainId>()).Returns(_ => throw failure);
        await using var directory = new DirectoryMembershipService(provider, factory, NullLogger<DirectoryMembershipService>.Instance);
        await using var updates = directory.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await updates.MoveNextAsync());
        var nextView = updates.MoveNextAsync().AsTask();
        var refresh = directory.RefreshViewAsync(new(1), TestContext.Current.CancellationToken).AsTask();

        membership.Publish(Snapshot(1));

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => refresh));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => nextView));
        Assert.Equal(MembershipVersion.MinValue, directory.CurrentView.Version);
    }

    [Fact]
    public async Task ServiceViewsCarryConcreteConfigurationWithoutAUniversalTopology()
    {
        var oldView = new ConfiguredView(7, null, new(8, "old"));
        var newView = new ConfiguredView(11, oldView.Id, new(16, "new"));
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IClusterServiceViewProvider<int, ConfiguredView>>(
            "configured-service", new ConfiguredViewProvider(newView));
        using var serviceProvider = services.BuildServiceProvider();

        var current = await serviceProvider.GetRequiredKeyedService<IClusterServiceViewProvider<int, ConfiguredView>>("configured-service")
            .RefreshAtLeastAsync(11, TestContext.Current.CancellationToken);

        Assert.Equal(new ServiceSettings(16, "new"), current.Configuration);
        Assert.True(current.TryGetPredecessor(out var predecessor));
        Assert.Equal(oldView.Id, predecessor);
    }

    private static MembershipBasedClusterServiceViewProvider CreateProvider(
        IClusterMembershipService membership, string serviceId, int partitions, long epoch) =>
        new(membership, new(serviceId, partitions, "test-ring"), Boundaries, NullLogger.Instance, providerEpoch: epoch);

    private static uint[] Boundaries(SiloAddress silo, int count) =>
        Enumerable.Range(0, count).Select(index => (uint)(silo.Generation * 100 + index)).ToArray();

    private static ClusterMembershipSnapshot Snapshot(long version) =>
        new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(Silo, new(Silo, SiloStatus.Active, "silo")), new(version));

    private sealed record ServiceSettings(int Concurrency, string Metadata);

    private readonly record struct ConfiguredView(int Id, int? Previous, ServiceSettings Configuration) : IClusterServiceView<int>
    {
        public bool TryGetPredecessor(out int predecessor)
        {
            predecessor = Previous.GetValueOrDefault();
            return Previous.HasValue;
        }
    }

    private sealed class ConfiguredViewProvider(ConfiguredView current) : IClusterServiceViewProvider<int, ConfiguredView>
    {
        public IAsyncEnumerable<ConfiguredView> ViewUpdates => throw new NotSupportedException();

        public bool TryGetCurrentView(out ConfiguredView view)
        {
            view = current;
            return true;
        }

        public ValueTask<ConfiguredView> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(current);

        public ValueTask<ConfiguredView> RefreshAtLeastAsync(int minimumView, CancellationToken cancellationToken)
        {
            Assert.True(current.Id >= minimumView);
            return ValueTask.FromResult(current);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MembershipSource : IClusterMembershipService, IDisposable
    {
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> _updates;

        public MembershipSource() => _updates = new(
            ClusterMembershipSnapshot.Default,
            (old, proposed) => proposed.Version > old.Version,
            update => CurrentSnapshot = update);

        public ClusterMembershipSnapshot CurrentSnapshot { get; private set; } = ClusterMembershipSnapshot.Default;
        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => _updates;
        public int RefreshCalls { get; private set; }

        public void Publish(ClusterMembershipSnapshot snapshot) => _updates.Publish(snapshot);

        public async ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCalls++;
            if (CurrentSnapshot.Version >= minimumVersion)
            {
                return;
            }

            await foreach (var snapshot in _updates.WithCancellation(cancellationToken))
            {
                if (snapshot.Version >= minimumVersion)
                {
                    return;
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotSupportedException();
        public void Dispose() => _updates.Dispose();
    }
}
