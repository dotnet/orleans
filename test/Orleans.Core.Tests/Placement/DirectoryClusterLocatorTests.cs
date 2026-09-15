using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.Placement;

[TestArea("Placement")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[Trait("Phase", "3")]
[Trait("FullyQualifiedName", "UnitTests.Placement.DirectoryClusterLocatorTests")]
public sealed class DirectoryClusterLocatorTests
{
    private static readonly GrainId GrainId = GrainId.Create("locator.test", "grain-1");
    private static readonly DateTimeOffset Start = new(2036, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task Locate_MissingEntry_PlacesAndAcquiresOwnership()
    {
        var fixture = CreateFixture(Topology(3, ("east", MetaclusterClusterState.Active)));

        var location = await fixture.Locator.Locate(
            GrainId,
            Context("east"),
            TestContext.Current.CancellationToken);
        var entry = await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterLocation("east", 1, 3, false), location);
        Assert.NotNull(entry);
        Assert.Equal(Start + Lease, entry.LeaseExpiration);
        Assert.Equal(1, entry.FencingToken);
    }

    [Fact]
    public async Task Locate_LiveActiveOwner_ReturnsOwnerWithoutPlacement()
    {
        var fixture = CreateFixture(Topology(
            4,
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Active)));
        var entry = await fixture.Directory.GetOrCreate(
            GrainId,
            "west",
            4,
            Lease,
            TestContext.Current.CancellationToken);

        var location = await fixture.Locator.Locate(
            GrainId,
            Context("east"),
            TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterLocation("west", entry.Version, entry.TopologyEpoch, true), location);
        Assert.Equal(entry, await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Locate_LiveDrainingOwner_FollowsRetentionPolicy()
    {
        var fixture = CreateFixture(Topology(
            5,
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Draining)));
        var entry = await fixture.Directory.GetOrCreate(
            GrainId,
            "west",
            4,
            Lease,
            TestContext.Current.CancellationToken);

        var location = await fixture.Locator.Locate(
            GrainId,
            Context("east"),
            TestContext.Current.CancellationToken);

        Assert.Equal("west", location.ClusterId);
        Assert.True(location.IsExistingOwner);
        Assert.Equal(entry.Version, location.Version);
    }

    [Fact]
    public async Task Locate_RemovedOwner_WaitsForLeaseExpiryBeforeRelocation()
    {
        var fixture = CreateFixture(Topology(
            6,
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Removed)));
        var original = await fixture.Directory.GetOrCreate(
            GrainId,
            "west",
            5,
            Lease,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Locator.Locate(
                GrainId,
                Context("east"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("remains leased", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'west'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(original, await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Locate_ExpiredOwner_ReacquiresWithHigherFence()
    {
        var fixture = CreateFixture(Topology(
            7,
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Active)));
        var expired = await fixture.Directory.GetOrCreate(
            GrainId,
            "west",
            6,
            Lease,
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(Lease);

        var location = await fixture.Locator.Locate(
            GrainId,
            Context("east"),
            TestContext.Current.CancellationToken);
        var current = await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken);

        Assert.Equal("east", location.ClusterId);
        Assert.False(location.IsExistingOwner);
        Assert.NotNull(current);
        Assert.True(current.FencingToken > expired.FencingToken);
        Assert.True(current.Version > expired.Version);
    }

    [Fact]
    public async Task Locate_ConcurrentAcquisition_UsesDirectoryWinner()
    {
        var fixture = CreateFixture(Topology(
            8,
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Active)));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var east = Task.Run(async () =>
        {
            await start.Task;
            return await fixture.Locator.Locate(GrainId, Context("east"));
        });
        var west = Task.Run(async () =>
        {
            await start.Task;
            return await fixture.Locator.Locate(GrainId, Context("west"));
        });

        start.SetResult();
        var locations = await Task.WhenAll(east, west);
        var current = await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken);

        Assert.NotNull(current);
        Assert.All(locations, location => Assert.Equal(current.ClusterId, location.ClusterId));
        Assert.All(locations, location => Assert.Equal(current.Version, location.Version));
        Assert.Contains(current.ClusterId, new[] { "east", "west" });
    }

    [Fact]
    public async Task Locate_TopologyChangesDuringMutation_RetriesWithNewEpoch()
    {
        var topology = new MutableTopologyProvider(Topology(10, ("east", MetaclusterClusterState.Active)));
        var directory = Substitute.For<IClusterDirectory>();
        directory.Lookup(GrainId, Arg.Any<CancellationToken>())
            .Returns((ClusterDirectoryEntry?)null);
        directory.GetOrCreate(GrainId, "east", 10, Lease, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                topology.Current = Topology(11, ("east", MetaclusterClusterState.Active));
                return new ClusterDirectoryEntry(GrainId, "east", 2, 11, 2, Start + Lease);
            });
        var locator = CreateLocator(directory, topology);

        var location = await locator.Locate(
            GrainId,
            Context("east"),
            TestContext.Current.CancellationToken);

        Assert.Equal(new ClusterLocation("east", 2, 11, false), location);
        Assert.Equal(11, topology.Current.Epoch);
        await directory.Received(1).GetOrCreate(GrainId, "east", 10, Lease, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Locate_LiveUnknownOwner_FailsWithoutMutatingDirectory()
    {
        var topology = new MutableTopologyProvider(Topology(
            12,
            ("east", MetaclusterClusterState.Active)));
        var directory = Substitute.For<IClusterDirectory>();
        var stale = new ClusterDirectoryEntry(GrainId, "unknown", 1, 11, 1, Start + Lease);
        directory.Lookup(GrainId, Arg.Any<CancellationToken>())
            .Returns(stale);
        var locator = CreateLocator(directory, topology);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => locator.Locate(
                GrainId,
                Context("east"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("unavailable cluster 'unknown'", exception.Message, StringComparison.Ordinal);
        await directory.Received(1).Lookup(GrainId, Arg.Any<CancellationToken>());
        await directory.DidNotReceiveWithAnyArgs().GetOrCreate(
            default,
            default!,
            default,
            default,
            TestContext.Current.CancellationToken);
        await directory.DidNotReceiveWithAnyArgs().TryMove(
            default,
            default,
            default!,
            default,
            default,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ValidateOwnership_CurrentEntry_ReturnsTrue()
    {
        var fixture = CreateFixture(Topology(13, ("east", MetaclusterClusterState.Active)));
        var original = await fixture.Directory.GetOrCreate(
            GrainId,
            "east",
            13,
            Lease,
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var validated = await fixture.Locator.ValidateLocalOwnership(
            GrainId,
            "east",
            TestContext.Current.CancellationToken);

        Assert.Equal(original.Version, validated.Version);
        Assert.Equal(original.FencingToken, validated.FencingToken);
        Assert.Equal(Start + TimeSpan.FromMinutes(5), validated.LeaseExpiration);
    }

    [Fact]
    public async Task ValidateOwnership_ExpiredVersionEpochFenceOrOwner_ReturnsFalse()
    {
        var fixture = CreateFixture(Topology(
            14,
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Active),
            ("removed", MetaclusterClusterState.Removed)));
        var stale = await fixture.Directory.GetOrCreate(
            GrainId,
            "east",
            13,
            Lease,
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(Lease);
        var moved = await fixture.Directory.TryMove(
            GrainId,
            stale.Version,
            "west",
            14,
            Lease,
            TestContext.Current.CancellationToken);
        Assert.NotNull(moved);

        var wrongOwner = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Locator.ValidateLocalOwnership(
                GrainId,
                "east",
                TestContext.Current.CancellationToken).AsTask());
        var removedOwner = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Locator.ValidateLocalOwnership(
                GrainId,
                "removed",
                TestContext.Current.CancellationToken).AsTask());
        fixture.Clock.Advance(Lease);
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Locator.ValidateLocalOwnership(
                GrainId,
                "west",
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("valid ownership lease", wrongOwner.Message, StringComparison.Ordinal);
        Assert.Contains("not an active member", removedOwner.Message, StringComparison.Ordinal);
        Assert.Contains("valid ownership lease", expired.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_NoActiveCluster_FailsDeterministically()
    {
        var fixture = CreateFixture(Topology(
            15,
            ("east", MetaclusterClusterState.Draining),
            ("west", MetaclusterClusterState.Removed)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Locator.Locate(
                GrainId,
                Context("east"),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("No active cluster", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'15'", exception.Message, StringComparison.Ordinal);
        Assert.Null(await fixture.Directory.Lookup(GrainId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Locate_CancellationBeforeOrDuringDirectoryCall_Propagates()
    {
        var topology = new MutableTopologyProvider(Topology(16, ("east", MetaclusterClusterState.Active)));
        var directory = new CancellationAwareDirectory();
        var locator = CreateLocator(directory, topology);
        using var before = new CancellationTokenSource();
        before.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locator.Locate(GrainId, Context("east"), before.Token).AsTask());

        using var during = new CancellationTokenSource();
        var pending = locator.Locate(GrainId, Context("east"), during.Token).AsTask();
        await directory.LookupStarted.Task;
        during.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, directory.LookupCalls);
        Assert.Equal(0, directory.MutationCalls);
    }

    private static LocatorFixture CreateFixture(MetaclusterTopology topology)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);
        var directory = new InMemoryClusterDirectory(clock);
        var provider = new MutableTopologyProvider(topology);
        return new(clock, directory, CreateLocator(directory, provider));
    }

    private static DirectoryClusterLocator CreateLocator(
        IClusterDirectory directory,
        IMetaclusterTopologyProvider topology)
    {
        var manifest = Substitute.For<IClusterManifestProvider>();
        manifest.LocalGrainManifest.Returns(new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty));
        var services = new ServiceCollection().BuildServiceProvider();
        return new DirectoryClusterLocator(
            directory,
            topology,
            new ClusterPlacementStrategyResolver(new GrainPropertiesResolver(manifest), services),
            new ClusterPlacementDirectorResolver(services),
            Options.Create(new MetaclusterOptions
            {
                Enabled = true,
                ClusterOwnershipLeaseDuration = Lease,
                ClusterOwnershipLeaseRenewalWindow = TimeSpan.FromMinutes(1)
            }));
    }

    private static ClusterLocationContext Context(string localClusterId)
        => new(
            "service",
            localClusterId,
            new GrainProperties(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)));

    private static MetaclusterTopology Topology(
        long epoch,
        params (string Id, MetaclusterClusterState State)[] clusters)
        => new(
            "service",
            epoch,
            clusters.ToImmutableDictionary(
                pair => pair.Id,
                pair => new MetaclusterCluster(pair.Id, pair.State, []),
                StringComparer.Ordinal));

    private sealed record LocatorFixture(
        Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock,
        InMemoryClusterDirectory Directory,
        DirectoryClusterLocator Locator);

    private sealed class MutableTopologyProvider(MetaclusterTopology current) : IMetaclusterTopologyProvider
    {
        public MetaclusterTopology Current { get; set; } = current;

        public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(Current);
        }

        public async IAsyncEnumerable<MetaclusterTopology> Watch(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Current;
            await Task.CompletedTask;
        }
    }

    private sealed class CancellationAwareDirectory : IClusterDirectory
    {
        public TaskCompletionSource LookupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LookupCalls { get; private set; }

        public int MutationCalls { get; private set; }

        public async ValueTask<ClusterDirectoryEntry?> Lookup(
            GrainId grainId,
            CancellationToken cancellationToken = default)
        {
            LookupCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            LookupStarted.TrySetResult();
            var completion = new TaskCompletionSource<ClusterDirectoryEntry?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<ClusterDirectoryEntry?>)state!).TrySetCanceled(),
                completion);
            return await completion.Task;
        }

        public ValueTask<ClusterDirectoryEntry> GetOrCreate(
            GrainId grainId,
            string proposedClusterId,
            long topologyEpoch,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            throw new InvalidOperationException("The mutation must not be reached.");
        }

        public ValueTask<ClusterDirectoryEntry?> TryRenew(
            GrainId grainId,
            long expectedVersion,
            string ownerClusterId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            throw new InvalidOperationException("The mutation must not be reached.");
        }

        public ValueTask<ClusterDirectoryEntry?> TryMove(
            GrainId grainId,
            long expectedVersion,
            string destinationClusterId,
            long topologyEpoch,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            throw new InvalidOperationException("The mutation must not be reached.");
        }
    }

    [Fact]
    public async Task Locate_ActiveDrainingRemovedExpiryAndReacquisition_PreservesSingleOwnerAndFence()
    {
        var topology = new MutableTopologyProvider(Topology(
            20,
            ("east", MetaclusterClusterState.Active),
            ("west", MetaclusterClusterState.Active)));
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);
        var directory = new InMemoryClusterDirectory(clock);
        var locator = CreateLocator(directory, topology);

        var acquiredLocation = await locator.Locate(
            GrainId,
            Context("east"),
            TestContext.Current.CancellationToken);
        var acquired = await directory.Lookup(GrainId, TestContext.Current.CancellationToken);
        Assert.NotNull(acquired);
        Assert.Equal(new ClusterLocation("east", acquired.Version, 20, false), acquiredLocation);

        clock.Advance(TimeSpan.FromMinutes(1));
        topology.Current = Topology(
            21,
            ("east", MetaclusterClusterState.Draining),
            ("west", MetaclusterClusterState.Active));
        var retainedLocation = await locator.Locate(
            GrainId,
            Context("east"),
            TestContext.Current.CancellationToken);
        var retained = await directory.Lookup(GrainId, TestContext.Current.CancellationToken);
        Assert.NotNull(retained);
        Assert.Equal("east", retainedLocation.ClusterId);
        Assert.True(retainedLocation.IsExistingOwner);
        Assert.Equal(acquired.Version, retained.Version);
        Assert.Equal(acquired.FencingToken, retained.FencingToken);
        Assert.Equal(Start + TimeSpan.FromMinutes(5), retained.LeaseExpiration);

        topology.Current = Topology(
            22,
            ("east", MetaclusterClusterState.Removed),
            ("west", MetaclusterClusterState.Active));
        var liveRemoval = await Assert.ThrowsAsync<InvalidOperationException>(
            () => locator.Locate(
                GrainId,
                Context("west"),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("remains leased", liveRemoval.Message, StringComparison.Ordinal);
        Assert.Equal(retained, await directory.Lookup(GrainId, TestContext.Current.CancellationToken));

        clock.Advance(TimeSpan.FromMinutes(4));
        var reacquiredLocation = await locator.Locate(
            GrainId,
            Context("west"),
            TestContext.Current.CancellationToken);
        var reacquired = await directory.Lookup(GrainId, TestContext.Current.CancellationToken);
        Assert.NotNull(reacquired);
        Assert.Equal(new ClusterLocation("west", reacquired.Version, 22, false), reacquiredLocation);
        Assert.Equal(acquired.Version + 1, reacquired.Version);
        Assert.Equal(acquired.FencingToken + 1, reacquired.FencingToken);
        Assert.Equal(Start + TimeSpan.FromMinutes(9), reacquired.LeaseExpiration);

        Assert.Null(await directory.TryRenew(
            GrainId,
            acquired.Version,
            "east",
            Lease,
            TestContext.Current.CancellationToken));
        Assert.Null(await directory.TryMove(
            GrainId,
            acquired.Version,
            "east",
            23,
            Lease,
            TestContext.Current.CancellationToken));
        Assert.Equal(reacquired, await directory.Lookup(GrainId, TestContext.Current.CancellationToken));
    }
}
