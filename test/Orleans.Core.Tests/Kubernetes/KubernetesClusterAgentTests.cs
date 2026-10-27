using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Hosting.Kubernetes;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Kubernetes;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Hosting")]
public class KubernetesClusterAgentTests
{
    private static readonly SiloAddress LocalSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1000), 1);
    private static readonly SiloAddress OtherSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 2000), 1);

    [Fact]
    public async Task ChosenSiloKeepsMonitoringAcrossMembershipUpdates()
    {
        await using var fixture = new Fixture();
        foreach (var status in new[] { SiloStatus.Joining, SiloStatus.Active, SiloStatus.Dead })
        {
            await fixture.Publish((LocalSilo, SiloStatus.Active), (OtherSilo, status));
            Assert.True(fixture.Accessor.IsMonitoringEnabled);
            Assert.Equal(status == SiloStatus.Joining ? 1 : 0, fixture.Accessor.PauseMonitoringSemaphore.CurrentCount);
            if (status == SiloStatus.Joining)
            {
                Assert.True(await fixture.Accessor.PauseMonitoringSemaphore.WaitAsync(0, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task ChosenSiloDeletesPodWhenPeerDiesAfterBecomingActive()
    {
        await using var fixture = new Fixture(deleteDefunctPods: true);
        await fixture.Publish((LocalSilo, SiloStatus.Active));
        await fixture.Publish((LocalSilo, SiloStatus.Active), (OtherSilo, SiloStatus.Joining));
        await fixture.Publish((LocalSilo, SiloStatus.Active), (OtherSilo, SiloStatus.Active));
        Assert.Empty(fixture.DeletedPods);

        await fixture.Publish((LocalSilo, SiloStatus.Active), (OtherSilo, SiloStatus.Dead));

        Assert.Equal("/api/v1/namespaces/test/pods/other", Assert.Single(fixture.DeletedPods));
        Assert.True(fixture.Accessor.IsMonitoringEnabled);
    }

    [Fact]
    public async Task MonitoringPausesWhenDeselectedAndResumesWhenChosen()
    {
        await using var fixture = new Fixture(OtherSilo, maxAgents: 1);
        await fixture.Publish((OtherSilo, SiloStatus.Active));
        Assert.True(fixture.Accessor.IsMonitoringEnabled);
        Assert.True(await fixture.Accessor.PauseMonitoringSemaphore.WaitAsync(0, TestContext.Current.CancellationToken));

        await fixture.Publish((LocalSilo, SiloStatus.Active), (OtherSilo, SiloStatus.Active));
        Assert.False(fixture.Accessor.IsMonitoringEnabled);
        Assert.Equal(0, fixture.Accessor.PauseMonitoringSemaphore.CurrentCount);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var resumed = fixture.Accessor.PauseMonitoringSemaphore.WaitAsync(cancellation.Token);
        try
        {
            await fixture.Publish((LocalSilo, SiloStatus.Active), (OtherSilo, SiloStatus.Active));
            Assert.False(fixture.Accessor.IsMonitoringEnabled);
            Assert.False(resumed.IsCompleted);

            await fixture.Publish((LocalSilo, SiloStatus.Dead), (OtherSilo, SiloStatus.Active));
            await resumed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(fixture.Accessor.IsMonitoringEnabled);
            Assert.Equal(0, fixture.Accessor.PauseMonitoringSemaphore.CurrentCount);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    [Fact]
    public async Task RepeatedElectionsCoalescePendingWakeUpSignals()
    {
        await using var fixture = new Fixture(OtherSilo, maxAgents: 1);
        await fixture.Publish((OtherSilo, SiloStatus.Active));
        for (var i = 0; i < 3; i++)
        {
            var preferredSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1000 + i), 1);
            await fixture.Publish((preferredSilo, SiloStatus.Active), (OtherSilo, SiloStatus.Active));
            Assert.False(fixture.Accessor.IsMonitoringEnabled);
            Assert.Equal(1, fixture.Accessor.PauseMonitoringSemaphore.CurrentCount);

            await fixture.Publish((preferredSilo, SiloStatus.Dead), (OtherSilo, SiloStatus.Active));
            Assert.True(fixture.Accessor.IsMonitoringEnabled);
            Assert.Equal(1, fixture.Accessor.PauseMonitoringSemaphore.CurrentCount);
        }

        Assert.True(await fixture.Accessor.PauseMonitoringSemaphore.WaitAsync(0, TestContext.Current.CancellationToken));
        Assert.False(await fixture.Accessor.PauseMonitoringSemaphore.WaitAsync(0, TestContext.Current.CancellationToken));
        var nextPreferredSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 1003), 1);
        await fixture.Publish((nextPreferredSilo, SiloStatus.Active), (OtherSilo, SiloStatus.Active));
        await fixture.Publish((nextPreferredSilo, SiloStatus.Dead), (OtherSilo, SiloStatus.Active));
        Assert.True(fixture.Accessor.IsMonitoringEnabled);
        Assert.True(await fixture.Accessor.PauseMonitoringSemaphore.WaitAsync(0, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, SiloStatus.Active, false)]
    [InlineData(1, SiloStatus.Active, false)]
    [InlineData(2, SiloStatus.Active, true)]
    [InlineData(2, SiloStatus.Joining, false)]
    [InlineData(2, SiloStatus.ShuttingDown, false)]
    [InlineData(2, SiloStatus.Dead, false)]
    public async Task OnlyChosenActiveSilosMonitor(int maxAgents, SiloStatus localStatus, bool expected)
    {
        await using var fixture = new Fixture(OtherSilo, maxAgents);
        await fixture.Publish((OtherSilo, localStatus), (LocalSilo, SiloStatus.Active));
        Assert.Equal(expected, fixture.Accessor.IsMonitoringEnabled);
        Assert.Equal(expected ? 1 : 0, fixture.Accessor.PauseMonitoringSemaphore.CurrentCount);
    }

    [Fact]
    public async Task ShutdownWakesPausedPodMonitor()
    {
        await using var fixture = new Fixture();
        var monitor = fixture.Accessor.MonitorKubernetesPods();
        Assert.False(monitor.IsCompleted);

        await fixture.Agent.OnStop(TestContext.Current.CancellationToken);
        await monitor.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(fixture.Accessor.IsMonitoringEnabled);
        Assert.Equal(0, fixture.Accessor.PauseMonitoringSemaphore.CurrentCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Channel<(ClusterMembershipSnapshot Snapshot, TaskCompletionSource Processed)> _updates =
            Channel.CreateUnbounded<(ClusterMembershipSnapshot, TaskCompletionSource)>();
        private readonly Task _monitor;
        private readonly IClusterMembershipService _membership = Substitute.For<IClusterMembershipService>();
        private readonly PodDeletionHandler _handler = new();
        private long _version;

        public KubernetesClusterAgent Agent { get; }
        public KubernetesClusterAgent.TestAccessor Accessor { get; }
        public List<string> DeletedPods => _handler.DeletedPods;

        public Fixture(SiloAddress? localSilo = null, int maxAgents = 2, bool deleteDefunctPods = false)
        {
            _membership.CurrentSnapshot.Returns(Snapshot());
            _membership.MembershipUpdates.Returns(GetMembershipUpdates());
            var localDetails = Substitute.For<ILocalSiloDetails>();
            localDetails.SiloAddress.Returns(localSilo ?? LocalSilo);
            var options = Substitute.For<IOptionsMonitor<KubernetesHostingOptions>>();
            options.CurrentValue.Returns(new KubernetesHostingOptions
            {
                GetClientConfiguration = () => new KubernetesClientConfiguration { Host = "http://localhost" },
                MaxAgents = maxAgents,
                DeleteDefunctSiloPods = deleteDefunctPods,
                Namespace = "test",
                PodName = "local",
            });
            Agent = new KubernetesClusterAgent(
                _membership,
                NullLogger<KubernetesClusterAgent>.Instance,
                options,
                Options.Create(new ClusterOptions()),
                localDetails,
                [_handler]);
            Accessor = new KubernetesClusterAgent.TestAccessor(Agent);
            _monitor = Accessor.MonitorOrleansClustering();
        }

        public async Task Publish(params (SiloAddress Silo, SiloStatus Status)[] members)
        {
            var snapshot = Snapshot(members);
            _membership.CurrentSnapshot.Returns(snapshot);
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_updates.Writer.TryWrite((snapshot, processed)));
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        private ClusterMembershipSnapshot Snapshot(params (SiloAddress Silo, SiloStatus Status)[] members) =>
            new(members.ToImmutableDictionary(
                member => member.Silo,
                member => new ClusterMember(member.Silo, member.Status, member.Silo.Equals(LocalSilo) ? "local" : "other")),
                new MembershipVersion(++_version));

        private async IAsyncEnumerable<ClusterMembershipSnapshot> GetMembershipUpdates(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var (snapshot, processed) in _updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return snapshot;
                processed.SetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Agent.OnStop(TestContext.Current.CancellationToken);
            await _monitor.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            _handler.Dispose();
        }
    }

    private sealed class PodDeletionHandler : DelegatingHandler
    {
        public List<string> DeletedPods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            DeletedPods.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
