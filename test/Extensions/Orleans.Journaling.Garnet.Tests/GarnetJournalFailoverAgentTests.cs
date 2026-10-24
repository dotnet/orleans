using System.Collections.Immutable;
using System.Net;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests which partitions the failover agent assigns to active silos, by membership's verdict on their hosts.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalFailoverAgentTests
{
    private static readonly SiloAddress Active = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
    private static readonly SiloAddress Dead = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11112), 1);
    private static readonly SiloAddress Unknown = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11113), 1);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agent_AssignsPartitionsOfDeadUnknownAndNonSiloHosts_ToActiveSilos()
    {
        var store = new InMemoryRangeMetaStore();
        var hosts = new[] { Active.ToParsableString(), Dead.ToParsableString(), "placeholder", Unknown.ToParsableString() };
        var hostings = hosts.Select((host, index) => KeyValuePair.Create(new PartitionId($"p{index}"), new PartitionHosting(host, 3, $"p{index}", "endpoint:1"))).ToArray();
        await store.TryWriteAsync(RangeMap.CreateUniform(16, hostings), 0, CancellationToken);
        var membership = new FakeMembership(Snapshot(1));
        await using var agent = new GarnetJournalFailoverAgent(new GarnetJournalHostingCoordinator(store), membership, new FakeRegistry(Active, Unknown), TimeSpan.FromSeconds(1), logger: null);

        // The dead silo's partition and the placeholder's move at once, at the next epoch and without an endpoint. A
        // silo unknown to membership may have just joined, so its partition stays until membership advances.
        Assert.Equal(2, await agent.AssignAsync(CancellationToken));
        var map = (await store.ReadAsync(CancellationToken))!.Map;
        Assert.Equal(new PartitionHosting(Active.ToParsableString(), 3, "p0", "endpoint:1"), map.Partitions[new("p0")]);
        Assert.Equal(new PartitionHosting(Active.ToParsableString(), 4, "p1"), map.Partitions[new("p1")]);
        Assert.Equal(new PartitionHosting(Active.ToParsableString(), 4, "p2"), map.Partitions[new("p2")]);
        Assert.Equal(Unknown.ToParsableString(), map.Partitions[new("p3")].Host);

        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        membership.Current = Snapshot(2);
        Assert.Equal(1, await agent.AssignAsync(CancellationToken));
        Assert.Equal(new PartitionHosting(Active.ToParsableString(), 4, "p3"), (await store.ReadAsync(CancellationToken))!.Map.Partitions[new("p3")]);
    }

    [Fact]
    public async Task Agent_AssignsPartitionsOfUnknownHosts_AfterFreshMembershipReads_WithoutAVersionChange()
    {
        // The cluster was redeployed with a new membership table: the map's host is in no snapshot, and the stable
        // cluster's membership version does not advance.
        var store = new InMemoryRangeMetaStore();
        await store.TryWriteAsync(RangeMap.CreateUniform(16, [KeyValuePair.Create(new PartitionId("p0"), new PartitionHosting(Unknown.ToParsableString(), 3, "p0", "endpoint:1"))]), 0, CancellationToken);
        var membership = new FakeMembership(Snapshot(1));
        await using var agent = new GarnetJournalFailoverAgent(new GarnetJournalHostingCoordinator(store), membership, new FakeRegistry(Active, Unknown), TimeSpan.FromSeconds(1), logger: null);

        // The first pass finds the host unknown; a refresh may join a table read which started earlier, so the second
        // refresh is the first certain to read the table since.
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        Assert.Equal(0, membership.Refreshes);
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        Assert.Equal(1, membership.Refreshes);
        Assert.Equal(1, await agent.AssignAsync(CancellationToken));
        Assert.Equal(2, membership.Refreshes);
        Assert.Equal(new PartitionHosting(Active.ToParsableString(), 4, "p0"), (await store.ReadAsync(CancellationToken))!.Map.Partitions[new("p0")]);

        // With no unknown host left, passes read no table.
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        Assert.Equal(2, membership.Refreshes);
    }

    [Fact]
    public async Task Agent_KeepsPartitionsOfHostsWhichMembershipLearnsOf()
    {
        // A silo which has just joined is missing from this agent's snapshot, and present after the table read.
        var store = new InMemoryRangeMetaStore();
        await store.TryWriteAsync(RangeMap.CreateUniform(16, [KeyValuePair.Create(new PartitionId("p0"), new PartitionHosting(Unknown.ToParsableString(), 3, "p0", "endpoint:1"))]), 0, CancellationToken);
        var membership = new FakeMembership(Snapshot(1));
        await using var agent = new GarnetJournalFailoverAgent(new GarnetJournalHostingCoordinator(store), membership, new FakeRegistry(Active, Unknown), TimeSpan.FromSeconds(1), logger: null);
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        membership.OnRefresh = () => membership.Current = new(
            Snapshot(1).Members.Add(Unknown, new ClusterMember(Unknown, SiloStatus.Active, "joined")),
            new MembershipVersion(1));
        for (var pass = 0; pass < 4; pass++)
        {
            Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        }

        Assert.Equal(new PartitionHosting(Unknown.ToParsableString(), 3, "p0", "endpoint:1"), (await store.ReadAsync(CancellationToken))!.Map.Partitions[new("p0")]);
    }

    [Fact]
    public async Task Agent_AssignsOnlyToSilosRegisteredUnderTheirAddresses_AndKeepsExplicitHostsPartitions()
    {
        // A mixed cluster: one silo hosts under its address, another under an explicit HostId and so is registered
        // only under that identifier. The explicit silo has fewer partitions, so it would win any unrestricted choice.
        var store = new InMemoryRangeMetaStore();
        var explicitSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11114), 1);
        var hostings = new[]
        {
            KeyValuePair.Create(new PartitionId("p0"), new PartitionHosting(Dead.ToParsableString(), 3, "p0", "endpoint:1")),
            KeyValuePair.Create(new PartitionId("p1"), new PartitionHosting("silo-b", 2, "p1", "endpoint:2")),
            KeyValuePair.Create(new PartitionId("p2"), new PartitionHosting(explicitSilo.ToParsableString(), 5, "p2")),
            KeyValuePair.Create(new PartitionId("p3"), new PartitionHosting(Active.ToParsableString(), 1, "p3", "endpoint:3")),
            KeyValuePair.Create(new PartitionId("p4"), new PartitionHosting(Active.ToParsableString(), 1, "p4", "endpoint:4")),
        };
        await store.TryWriteAsync(RangeMap.CreateUniform(16, hostings), 0, CancellationToken);
        var membership = new FakeMembership(new(
            Snapshot(1).Members.Add(explicitSilo, new ClusterMember(explicitSilo, SiloStatus.Active, "explicit")),
            new MembershipVersion(1)));
        var registry = new FakeRegistry(Active, Dead);
        registry.Hosts.Add("silo-b");
        await using var agent = new GarnetJournalFailoverAgent(new GarnetJournalHostingCoordinator(store), membership, registry, TimeSpan.FromSeconds(1), logger: null);

        // The dead silo's partition, and the partition stranded on the explicit silo's address, which that silo does not
        // serve, move to the silo registered under its address. The explicit host keeps its partition.
        Assert.Equal(2, await agent.AssignAsync(CancellationToken));
        var map = (await store.ReadAsync(CancellationToken))!.Map;
        Assert.Equal(new PartitionHosting(Active.ToParsableString(), 4, "p0"), map.Partitions[new("p0")]);
        Assert.Equal(new PartitionHosting("silo-b", 2, "p1", "endpoint:2"), map.Partitions[new("p1")]);
        Assert.Equal(new PartitionHosting(Active.ToParsableString(), 6, "p2"), map.Partitions[new("p2")]);
        Assert.DoesNotContain(map.Partitions.Values, hosting => hosting.Host == explicitSilo.ToParsableString());

        // The dead silo's registration is dropped, and nothing moves again.
        Assert.DoesNotContain(Dead.ToParsableString(), registry.Hosts);
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
    }

    [Fact]
    public async Task Agent_BalancesOnlyOntoSilosRegisteredUnderTheirAddresses()
    {
        // An active silo which serves an explicit HostId is not registered under its address, so a partition placed on
        // that address would have no host; balancing leaves the partitions where they are until it registers.
        var store = new InMemoryRangeMetaStore();
        var other = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11115), 1);
        var hostings = Enumerable.Range(0, 4)
            .Select(index => KeyValuePair.Create(new PartitionId($"p{index}"), new PartitionHosting(Active.ToParsableString(), 1, $"p{index}", $"endpoint:{index}")))
            .ToArray();
        await store.TryWriteAsync(RangeMap.CreateUniform(16, hostings), 0, CancellationToken);
        var membership = new FakeMembership(new(
            Snapshot(1).Members.Add(other, new ClusterMember(other, SiloStatus.Active, "other")),
            new MembershipVersion(1)));
        var registry = new FakeRegistry(Active);
        await using var agent = new GarnetJournalFailoverAgent(new GarnetJournalHostingCoordinator(store), membership, registry, TimeSpan.FromSeconds(1), logger: null, balancingInterval: TimeSpan.Zero);

        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        Assert.All((await store.ReadAsync(CancellationToken))!.Map.Partitions.Values, hosting => Assert.Equal(Active.ToParsableString(), hosting.Host));

        registry.Hosts.Add(other.ToParsableString());
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        Assert.Single((await store.ReadAsync(CancellationToken))!.Map.Partitions.Values, hosting => hosting.Host == other.ToParsableString());
    }

    [Fact]
    public async Task Agent_BalancesAutomaticHosts_WhileExplicitHostsServeTheirPartitions()
    {
        var store = new InMemoryRangeMetaStore();
        var other = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11115), 1);
        var explicitPartition = new PartitionId("explicit");
        var explicitHosting = new PartitionHosting("explicit-host", 3, "explicit");
        var hostings = Enumerable.Range(0, 4)
            .Select(index => KeyValuePair.Create(new PartitionId($"p{index}"), new PartitionHosting(Active.ToParsableString(), 1, $"p{index}", $"endpoint:{index}")))
            .Append(KeyValuePair.Create(explicitPartition, explicitHosting));
        await store.TryWriteAsync(RangeMap.CreateUniform(16, hostings), 0, CancellationToken);
        var membership = new FakeMembership(new(
            Snapshot(1).Members.Add(other, new ClusterMember(other, SiloStatus.Active, "other")),
            new MembershipVersion(1)));
        var registry = new FakeRegistry(Active, other);
        registry.Hosts.Add(explicitHosting.Host);
        await using var agent = new GarnetJournalFailoverAgent(new GarnetJournalHostingCoordinator(store), membership, registry, TimeSpan.FromSeconds(1), logger: null, balancingInterval: TimeSpan.Zero);

        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        var view = (await store.ReadAsync(CancellationToken))!;
        Assert.All(view.Map.Partitions, entry => Assert.Equal(entry.Key == explicitPartition ? explicitHosting.Host : Active.ToParsableString(), entry.Value.Host));

        explicitHosting = explicitHosting with { Endpoint = "explicit:1" };
        Assert.True((await store.TryWriteAsync(view.Map with { Partitions = view.Map.Partitions.SetItem(explicitPartition, explicitHosting) }, view.Version, CancellationToken)).Written);
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        var after = (await store.ReadAsync(CancellationToken))!;
        Assert.Equal(explicitHosting, after.Map.Partitions[explicitPartition]);
        Assert.Equal(3, after.Map.Partitions.Values.Count(hosting => hosting.Host == Active.ToParsableString()));
        var moved = Assert.Single(after.Map.Partitions.Values, hosting => hosting.Host == other.ToParsableString());
        Assert.Equal(2, moved.Epoch);
        Assert.Null(moved.Endpoint);

        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
        Assert.Equal(after, await store.ReadAsync(CancellationToken));
    }

    [Fact]
    public async Task Agent_AssignsNothingWithoutActiveSilos()
    {
        var store = new InMemoryRangeMetaStore();
        await store.TryWriteAsync(RangeMap.CreateUniform(16, [KeyValuePair.Create(new PartitionId("p0"), new PartitionHosting(Dead.ToParsableString(), 0, "p0"))]), 0, CancellationToken);
        var membership = new FakeMembership(new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(Dead, new ClusterMember(Dead, SiloStatus.Dead, "dead")),
            new MembershipVersion(1)));
        await using var agent = new GarnetJournalFailoverAgent(new GarnetJournalHostingCoordinator(store), membership, new FakeRegistry(Active, Unknown), TimeSpan.FromSeconds(1), logger: null);
        Assert.Equal(0, await agent.AssignAsync(CancellationToken));
    }

    private static ClusterMembershipSnapshot Snapshot(long version) => new(
        ImmutableDictionary<SiloAddress, ClusterMember>.Empty
            .Add(Active, new ClusterMember(Active, SiloStatus.Active, "active"))
            .Add(Dead, new ClusterMember(Dead, SiloStatus.Dead, "dead")),
        new MembershipVersion(version));

    private sealed class FakeRegistry(params SiloAddress[] silos) : IGarnetJournalHostRegistry
    {
        public HashSet<string> Hosts { get; } = [.. silos.Select(silo => silo.ToParsableString())];

        public Task<IReadOnlySet<string>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(Hosts));

        public Task UnregisterAsync(string host, CancellationToken cancellationToken)
        {
            Hosts.Remove(host);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeMembership(ClusterMembershipSnapshot current) : IClusterMembershipService
    {
        public ClusterMembershipSnapshot Current { get; set; } = current;

        public int Refreshes { get; private set; }

        public Action? OnRefresh { get; set; }

        public ClusterMembershipSnapshot CurrentSnapshot => Current;

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => throw new NotSupportedException();

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            OnRefresh?.Invoke();
            return ValueTask.CompletedTask;
        }

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotSupportedException();
    }
}
