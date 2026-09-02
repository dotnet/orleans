using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class ClusterServiceMembershipTests
{
    [Fact]
    public async Task Constructor_ExposesServiceAndProjectsUnderlyingCurrentSnapshot()
    {
        var underlyingSnapshot = CreateSnapshot(41, CreateSilo(1));
        await using var fixture = new ClusterServiceMembershipFixture(underlyingSnapshot);

        var view = fixture.Membership.CurrentView;

        Assert.Same(fixture.Service, fixture.Membership.ClusterMembershipService);
        Assert.Same(fixture.Configuration, view.Configuration);
        Assert.Equal(new MembershipVersion(41), view.ViewId.MembershipVersion);
        Assert.Same(underlyingSnapshot, view.ClusterMembershipSnapshot);
        Assert.Equal([CreateSilo(1)], view.Members);
        Assert.Equal(2, view.RangeOwners.Count);
    }

    [Fact]
    public async Task DirectoryMembershipService_ExplicitDefaultPreservesLegacyEmptyInitialView()
    {
        var underlyingSnapshot = CreateSnapshot(41, CreateSilo(1));
        var service = new TestClusterMembershipService(underlyingSnapshot);
        await using var membership = new DirectoryMembershipService(
            service,
            grainFactory: null!,
            NullLogger<DirectoryMembershipService>.Instance,
            partitionsPerSilo: 2,
            GetBoundaries);
        await service.EnumeratorStarted;
        await using var updates = membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await updates.MoveNextAsync());
        Assert.Same(membership.CurrentView, updates.Current);
        Assert.Same(service, membership.ClusterMembershipService);
        Assert.Same(underlyingSnapshot, service.CurrentSnapshot);
        Assert.NotSame(underlyingSnapshot, membership.CurrentView.ClusterMembershipSnapshot);
        Assert.Equal(MembershipVersion.MinValue, membership.CurrentView.Version);
        Assert.Empty(membership.CurrentView.ClusterMembershipSnapshot.Members);
        Assert.Empty(membership.CurrentView.Members);
        Assert.Empty(membership.CurrentView.RangeOwners);
        Assert.Equal(2, membership.PartitionsPerSilo);

        var nextUpdate = updates.MoveNextAsync().AsTask();
        service.Publish(underlyingSnapshot);
        Assert.True(await nextUpdate);
        Assert.Same(underlyingSnapshot, updates.Current.ClusterMembershipSnapshot);
        Assert.Equal(new MembershipVersion(41), updates.Current.Version);
        Assert.Equal([CreateSilo(1)], updates.Current.Members);
        Assert.Same(updates.Current, membership.CurrentView);
    }

    [Fact]
    public async Task DirectoryMembershipService_SuppressesDuplicateVersionWithDifferentTopology()
    {
        var service = new TestClusterMembershipService(ClusterMembershipSnapshot.Default);
        await using var membership = new DirectoryMembershipService(
            service,
            grainFactory: null!,
            NullLogger<DirectoryMembershipService>.Instance,
            partitionsPerSilo: 2,
            GetBoundaries);
        await service.EnumeratorStarted;
        await using var updates = membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var versions = new List<MembershipVersion>();

        Assert.True(await updates.MoveNextAsync());
        versions.Add(updates.Current.Version);

        var versionThreeSnapshot = CreateSnapshot(3, CreateSilo(1));
        var versionThreeUpdate = updates.MoveNextAsync().AsTask();
        service.Publish(versionThreeSnapshot);
        Assert.True(await versionThreeUpdate);
        var versionThree = updates.Current;
        versions.Add(versionThree.Version);

        var nextUpdate = updates.MoveNextAsync().AsTask();
        var duplicateVersionSnapshot = CreateSnapshot(3, CreateSilo(1), CreateSilo(2));
        service.Publish(duplicateVersionSnapshot);
        var versionFourSnapshot = CreateSnapshot(4, CreateSilo(4));
        service.Publish(versionFourSnapshot);
        Assert.True(await nextUpdate);
        versions.Add(updates.Current.Version);

        Assert.Equal([MembershipVersion.MinValue, new(3), new(4)], versions);
        Assert.Same(versionThreeSnapshot, versionThree.ClusterMembershipSnapshot);
        Assert.NotSame(duplicateVersionSnapshot, updates.Current.ClusterMembershipSnapshot);
        Assert.Same(versionFourSnapshot, updates.Current.ClusterMembershipSnapshot);
        Assert.Equal([CreateSilo(4)], updates.Current.Members);
        Assert.Same(updates.Current, membership.CurrentView);
    }

    [Fact]
    public async Task DirectoryMembershipService_NormalCompletionClosesSubscribers()
    {
        var service = new TestClusterMembershipService(ClusterMembershipSnapshot.Default);
        await using var membership = new DirectoryMembershipService(
            service,
            grainFactory: null!,
            NullLogger<DirectoryMembershipService>.Instance,
            partitionsPerSilo: 2,
            GetBoundaries);
        await service.EnumeratorStarted;
        await using var updates = membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await updates.MoveNextAsync());
        var initialView = updates.Current;
        var completion = updates.MoveNextAsync().AsTask();

        service.Complete();

        Assert.False(await completion);
        Assert.Same(initialView, membership.CurrentView);
        await using var lateSubscriber = membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await lateSubscriber.MoveNextAsync());
    }

    [Fact]
    public async Task MembershipUpdates_ProjectConfigurationBoundariesAndPublishInOrder()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        await using var updates = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var versions = new List<MembershipVersion>();

        Assert.True(await updates.MoveNextAsync());
        versions.Add(updates.Current.ViewId.MembershipVersion);

        var firstSnapshot = CreateSnapshot(2, CreateSilo(2));
        var first = await PublishAndReadNext(fixture, updates, firstSnapshot);
        versions.Add(first.ViewId.MembershipVersion);

        var secondSnapshot = CreateSnapshot(4, CreateSilo(2), CreateSilo(1));
        var second = await PublishAndReadNext(fixture, updates, secondSnapshot);
        versions.Add(second.ViewId.MembershipVersion);

        Assert.Equal([MembershipVersion.MinValue, new(2), new(4)], versions);
        Assert.Same(firstSnapshot, first.ClusterMembershipSnapshot);
        Assert.Same(secondSnapshot, second.ClusterMembershipSnapshot);
        Assert.Same(fixture.Configuration, second.Configuration);
        Assert.Equal(2, second.PartitionCount);
        Assert.Equal([CreateSilo(1), CreateSilo(2)], second.Members);
        Assert.Equal(4, second.RangeOwners.Count);
        Assert.Same(second, fixture.Membership.CurrentView);
    }

    [Fact]
    public async Task MembershipUpdates_SuppressDuplicateVersionWithDifferentTopology()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        await using var updates = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var versions = new List<MembershipVersion>();

        Assert.True(await updates.MoveNextAsync());
        versions.Add(updates.Current.ViewId.MembershipVersion);

        var versionThreeSnapshot = CreateSnapshot(3, CreateSilo(1));
        var versionThree = await PublishAndReadNext(fixture, updates, versionThreeSnapshot);
        versions.Add(versionThree.ViewId.MembershipVersion);

        var nextUpdate = updates.MoveNextAsync().AsTask();
        var duplicateVersionSnapshot = CreateSnapshot(3, CreateSilo(1), CreateSilo(2));
        fixture.Service.Publish(duplicateVersionSnapshot);
        var versionFourSnapshot = CreateSnapshot(4, CreateSilo(4));
        fixture.Service.Publish(versionFourSnapshot);
        Assert.True(await nextUpdate);
        versions.Add(updates.Current.ViewId.MembershipVersion);

        Assert.Equal([MembershipVersion.MinValue, new(3), new(4)], versions);
        Assert.Same(versionThreeSnapshot, versionThree.ClusterMembershipSnapshot);
        Assert.NotSame(duplicateVersionSnapshot, updates.Current.ClusterMembershipSnapshot);
        Assert.Same(versionFourSnapshot, updates.Current.ClusterMembershipSnapshot);
        Assert.Equal([CreateSilo(4)], updates.Current.Members);
        Assert.Same(updates.Current, fixture.Membership.CurrentView);
    }

    [Fact]
    public async Task RefreshViewAsync_ForwardsMinimumVersionAndExactCancellationTokenWithoutAwaitingRefresh()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        var current = await PublishAndObserve(fixture, CreateSnapshot(7, CreateSilo(1)));
        using var cancellation = new CancellationTokenSource();

        var result = await fixture.Membership.RefreshViewAsync(new(6), cancellation.Token);

        var call = Assert.Single(fixture.Service.RefreshCalls);
        Assert.Equal(new MembershipVersion(6), call.MinimumVersion);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Same(current, result);
        Assert.False(fixture.Service.RefreshCompletion.Task.IsCompleted);
    }

    [Fact]
    public async Task RefreshViewAsync_CurrentViewAtMinimumVersionCompletesAtConcreteBoundary()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        var current = await PublishAndObserve(fixture, CreateSnapshot(7, CreateSilo(1)));

        var refresh = fixture.Membership.RefreshViewAsync(new(7), CancellationToken.None).AsTask();

        Assert.True(refresh.IsCompleted);
        Assert.Same(current, await refresh);
        Assert.Equal(new MembershipVersion(7), Assert.Single(fixture.Service.RefreshCalls).MinimumVersion);
        Assert.False(fixture.Service.RefreshCompletion.Task.IsCompleted);
    }

    [Fact]
    public async Task RefreshViewAsync_WaitsThroughOlderViewAndCompletesAtMinimumVersion()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        await PublishAndObserve(fixture, CreateSnapshot(2, CreateSilo(1)));
        await using var observer = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await observer.MoveNextAsync());

        var refresh = fixture.Membership.RefreshViewAsync(new(4), CancellationToken.None).AsTask();
        Assert.False(refresh.IsCompleted);

        var versionThree = await PublishAndReadNext(fixture, observer, CreateSnapshot(3, CreateSilo(2)));
        Assert.Equal(new MembershipVersion(3), versionThree.ViewId.MembershipVersion);
        Assert.False(refresh.IsCompleted);

        var versionFour = await PublishAndReadNext(fixture, observer, CreateSnapshot(4, CreateSilo(3)));
        var result = await refresh;

        Assert.Same(versionFour, result);
        Assert.Equal(new MembershipVersion(4), result.ViewId.MembershipVersion);
        Assert.Equal([CreateSilo(3)], result.Members);
        Assert.Equal(new MembershipVersion(4), Assert.Single(fixture.Service.RefreshCalls).MinimumVersion);
    }

    [Fact]
    public async Task RefreshViewAsync_PreCanceledForwardsTokenAndLeavesPublicationOperational()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Membership.RefreshViewAsync(new(5), cancellation.Token).AsTask());

        var call = Assert.Single(fixture.Service.RefreshCalls);
        Assert.Equal(new MembershipVersion(5), call.MinimumVersion);
        Assert.Equal(cancellation.Token, call.CancellationToken);

        var published = await PublishAndObserve(fixture, CreateSnapshot(6, CreateSilo(2)));
        Assert.Same(published, fixture.Membership.CurrentView);
        Assert.Equal(new MembershipVersion(6), published.ViewId.MembershipVersion);
    }

    [Fact]
    public async Task DisposeAsync_StopsProjectionAndCompletesSubscribers()
    {
        var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        await using var updates = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await updates.MoveNextAsync());
        var subscriberCompletion = updates.MoveNextAsync().AsTask();
        var lastView = fixture.Membership.CurrentView;

        await fixture.DisposeMembershipAsync();

        Assert.False(await subscriberCompletion);
        fixture.Service.Publish(CreateSnapshot(9, CreateSilo(1)));
        Assert.Same(lastView, fixture.Membership.CurrentView);
        await using var lateSubscriber = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await lateSubscriber.MoveNextAsync());
    }

    [Fact]
    public async Task DisposeAsync_DuringRefreshCompletesWithLastPublishedView()
    {
        var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        var lastView = fixture.Membership.CurrentView;
        var refresh = fixture.Membership.RefreshViewAsync(new(5), CancellationToken.None).AsTask();
        Assert.False(refresh.IsCompleted);

        await fixture.DisposeMembershipAsync();
        var result = await refresh;

        Assert.Same(lastView, result);
        Assert.Equal(MembershipVersion.MinValue, result.ViewId.MembershipVersion);
        Assert.Equal(new MembershipVersion(5), Assert.Single(fixture.Service.RefreshCalls).MinimumVersion);
        Assert.False(fixture.Service.RefreshCompletion.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposeAsync_SecondCallThrowsObjectDisposedException()
    {
        var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;

        await fixture.Membership.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Membership.DisposeAsync().AsTask());
    }

    private static async Task<ClusterServiceTopology> PublishAndObserve(
        ClusterServiceMembershipFixture fixture,
        ClusterMembershipSnapshot snapshot)
    {
        await using var updates = fixture.Membership.ViewUpdates.GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        return await PublishAndReadNext(fixture, updates, snapshot);
    }

    private static async Task<ClusterServiceTopology> PublishAndReadNext(
        ClusterServiceMembershipFixture fixture,
        IAsyncEnumerator<ClusterServiceTopology> updates,
        ClusterMembershipSnapshot snapshot)
    {
        var nextUpdate = updates.MoveNextAsync().AsTask();
        fixture.Service.Publish(snapshot);
        Assert.True(await nextUpdate);
        return updates.Current;
    }

    private static ClusterMembershipSnapshot CreateSnapshot(long version, params SiloAddress[] activeSilos)
    {
        var members = activeSilos.ToImmutableDictionary(
            static address => address,
            static address => new ClusterMember(address, SiloStatus.Active, $"silo-{address.Endpoint.Port}"));
        return new(members, new(version));
    }

    private static SiloAddress CreateSilo(int index) =>
        SiloAddress.New(IPAddress.Loopback, 10_000 + index, generation: index);

    private static uint[] GetBoundaries(SiloAddress silo, int count) =>
        count == 1
            ? [unchecked((uint)silo.GetConsistentHashCode())]
            : silo.GetUniformHashCodes(count);

    private sealed class ClusterServiceMembershipFixture : IAsyncDisposable
    {
        private int _disposed;

        public ClusterServiceMembershipFixture(ClusterMembershipSnapshot? initialSnapshot = null)
        {
            Service = new(initialSnapshot ?? ClusterMembershipSnapshot.Default);
            Configuration = new(
                serviceId: "test-cluster-service",
                protocolVersion: 3,
                partitionsPerSilo: 2,
                assignmentStrategy: "uniform-hash-ring/v1");
            Membership = new(Service, Configuration, GetBoundaries, NullLogger.Instance);
        }

        public TestClusterMembershipService Service { get; }

        public ClusterServiceConfiguration Configuration { get; }

        public ClusterServiceMembership Membership { get; }

        public async ValueTask DisposeMembershipAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await Membership.DisposeAsync();
            }
        }

        public ValueTask DisposeAsync() => DisposeMembershipAsync();
    }

    private sealed class TestClusterMembershipService : IClusterMembershipService
    {
        private readonly Channel<ClusterMembershipSnapshot> _updates = Channel.CreateUnbounded<ClusterMembershipSnapshot>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly ConcurrentQueue<RefreshCall> _refreshCalls = new();
        private ClusterMembershipSnapshot _currentSnapshot;

        public TestClusterMembershipService(ClusterMembershipSnapshot initialSnapshot)
        {
            _currentSnapshot = initialSnapshot;
        }

        public ClusterMembershipSnapshot CurrentSnapshot => _currentSnapshot;

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => ReadUpdates();

        public Task EnumeratorStarted => EnumeratorStartedSource.Task;

        public TaskCompletionSource EnumeratorStartedSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RefreshCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RefreshCall[] RefreshCalls => _refreshCalls.ToArray();

        public void Publish(ClusterMembershipSnapshot snapshot)
        {
            Interlocked.Exchange(ref _currentSnapshot, snapshot);
            if (!_updates.Writer.TryWrite(snapshot))
            {
                throw new InvalidOperationException("The controlled membership update stream rejected an update.");
            }
        }

        public ValueTask Refresh(
            MembershipVersion minimumVersion = default,
            CancellationToken cancellationToken = default)
        {
            _refreshCalls.Enqueue(new(minimumVersion, cancellationToken));
            EnumeratorStartedSource.TrySetResult();
            return new(RefreshCompletion.Task);
        }

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        public void Complete()
        {
            if (!_updates.Writer.TryComplete())
            {
                throw new InvalidOperationException("The controlled membership update stream was already completed.");
            }
        }

        private async IAsyncEnumerable<ClusterMembershipSnapshot> ReadUpdates(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnumeratorStartedSource.TrySetResult();
            await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }
    }

    private readonly record struct RefreshCall(
        MembershipVersion MinimumVersion,
        CancellationToken CancellationToken);

    [Fact]
    public async Task Constructor_ExplicitInitialSnapshotOverridesDistinctUnderlyingCurrentSnapshot()
    {
        var underlyingSnapshot = CreateSnapshot(2, CreateSilo(1));
        var explicitInitialSnapshot = CreateSnapshot(8, CreateSilo(2), CreateSilo(3));
        var service = new TestClusterMembershipService(underlyingSnapshot);
        var configuration = new ClusterServiceConfiguration(
            serviceId: "explicit-initial-snapshot",
            protocolVersion: 3,
            partitionsPerSilo: 2,
            assignmentStrategy: "uniform-hash-ring/v1");
        await using var membership = new ClusterServiceMembership(
            service,
            configuration,
            GetBoundaries,
            NullLogger.Instance,
            explicitInitialSnapshot);
        await service.EnumeratorStarted;

        var view = membership.CurrentView;

        Assert.Same(underlyingSnapshot, service.CurrentSnapshot);
        Assert.NotSame(underlyingSnapshot, view.ClusterMembershipSnapshot);
        Assert.Same(explicitInitialSnapshot, view.ClusterMembershipSnapshot);
        Assert.Equal(new MembershipVersion(8), view.ViewId.MembershipVersion);
        Assert.Equal([CreateSilo(2), CreateSilo(3)], view.Members);
        Assert.Same(configuration, view.Configuration);
    }

    [Fact]
    public async Task MembershipUpdates_NormalCompletionClosesSubscribersAndPreservesLastCurrentView()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        await using var firstSubscriber = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var secondSubscriber = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await firstSubscriber.MoveNextAsync());
        Assert.True(await secondSubscriber.MoveNextAsync());

        var firstUpdate = firstSubscriber.MoveNextAsync().AsTask();
        var secondUpdate = secondSubscriber.MoveNextAsync().AsTask();
        var lastSnapshot = CreateSnapshot(6, CreateSilo(2), CreateSilo(4));
        fixture.Service.Publish(lastSnapshot);
        Assert.True(await firstUpdate);
        Assert.True(await secondUpdate);
        var lastView = firstSubscriber.Current;
        Assert.Same(lastView, secondSubscriber.Current);
        Assert.Same(lastSnapshot, lastView.ClusterMembershipSnapshot);

        var firstCompletion = firstSubscriber.MoveNextAsync().AsTask();
        var secondCompletion = secondSubscriber.MoveNextAsync().AsTask();
        fixture.Service.Complete();

        Assert.False(await firstCompletion);
        Assert.False(await secondCompletion);
        Assert.Same(lastView, fixture.Membership.CurrentView);
        Assert.Equal(new MembershipVersion(6), fixture.Membership.CurrentView.ViewId.MembershipVersion);
        Assert.Equal([CreateSilo(2), CreateSilo(4)], fixture.Membership.CurrentView.Members);
        await using var lateSubscriber = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await lateSubscriber.MoveNextAsync());
    }

    [Fact]
    public async Task MembershipUpdates_RegressiveUpdateBetweenNewerUpdatesIsNeverObserved()
    {
        await using var fixture = new ClusterServiceMembershipFixture();
        await fixture.Service.EnumeratorStarted;
        await using var updates = fixture.Membership.ViewUpdates.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var observedVersions = new List<MembershipVersion>();

        Assert.True(await updates.MoveNextAsync());
        observedVersions.Add(updates.Current.ViewId.MembershipVersion);

        var versionFour = await PublishAndReadNext(fixture, updates, CreateSnapshot(4, CreateSilo(4)));
        observedVersions.Add(versionFour.ViewId.MembershipVersion);
        Assert.Equal([CreateSilo(4)], versionFour.Members);

        var nextUpdate = updates.MoveNextAsync().AsTask();
        fixture.Service.Publish(CreateSnapshot(3, CreateSilo(3)));
        var versionFiveSnapshot = CreateSnapshot(5, CreateSilo(1), CreateSilo(5));
        fixture.Service.Publish(versionFiveSnapshot);
        Assert.True(await nextUpdate);
        observedVersions.Add(updates.Current.ViewId.MembershipVersion);

        Assert.Equal([MembershipVersion.MinValue, new(4), new(5)], observedVersions);
        Assert.Equal(new MembershipVersion(5), updates.Current.ViewId.MembershipVersion);
        Assert.Same(versionFiveSnapshot, updates.Current.ClusterMembershipSnapshot);
        Assert.Equal([CreateSilo(1), CreateSilo(5)], updates.Current.Members);
        Assert.Same(updates.Current, fixture.Membership.CurrentView);
    }
}
