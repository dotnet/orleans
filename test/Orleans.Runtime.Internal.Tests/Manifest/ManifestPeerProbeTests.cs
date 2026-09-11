using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime.Metadata;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Manifest")]
public sealed class ManifestPeerProbeTests(ManifestPeerProbeTests.Fixture fixture) : IClassFixture<ManifestPeerProbeTests.Fixture>
{
    private static readonly GrainType TargetType = SystemTargetGrainId.CreateGrainType("manifest-probe-test");

    public sealed class Fixture : BaseInProcessTestClusterFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((_, silo) =>
            {
                silo.Services.AddSingleton<PeerTarget>();
                silo.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(services => services.GetRequiredService<PeerTarget>());
            });
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalCompletion_ReleasesSlotsAndSignalsPeerCancellation(bool waitForUpdate, bool cancelCaller)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = fixture.HostedCluster.Silos[0];
        var remote = fixture.HostedCluster.Silos[1];
        var localServices = local.ServiceProvider;
        Assert.False(localServices.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.WaitForCancellationAcknowledgement);
        var actualFactory = localServices.GetRequiredService<IInternalGrainFactory>();
        var proxy = actualFactory.GetSystemTarget<IClusterManifestSystemTarget>(TargetType, remote.SiloAddress);
        var factory = Substitute.For<IInternalGrainFactory>();
        factory.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, remote.SiloAddress).Returns(proxy);
        using var services = new ServiceCollection().AddSingleton(factory).BuildServiceProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var time = new FakeTimeProvider();
        await using var provider = new ClusterManifestProvider(
            localServices.GetRequiredService<ILocalSiloDetails>(),
            localServices.GetRequiredService<SiloManifestProvider>(),
            localServices.GetRequiredService<IClusterMembershipService>(),
            localServices.GetRequiredService<IFatalErrorHandler>(),
            NullLogger<ClusterManifestProvider>.Instance,
            services,
            time,
            Options.Create(new ClusterManifestOptions { EnableContentAddressedRetrieval = true }));
        var initialize = typeof(ClusterManifestProvider).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)initialize.Invoke(provider, [cancellationToken])!;
        var target = remote.ServiceProvider.GetRequiredService<PeerTarget>();
        var scenario = new ProbeScenario(waitForUpdate);
        target.Scenario = scenario;
        var initialSummaryRequests = target.SummaryRequests;
        var probes = Enumerable.Range(0, 3).Select(_ => ProbeAsync(provider, remote.SiloAddress, cancellation.Token)).ToArray();
        try
        {
            await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(initialSummaryRequests + 3, target.SummaryRequests);

            await ProbeAsync(provider, remote.SiloAddress, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(initialSummaryRequests + 3, target.SummaryRequests);
            Assert.All(probes, probe => Assert.False(probe.IsCompleted));
            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    Task.WhenAll(probes).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(1));
                await Task.WhenAll(probes).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            await scenario.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.False(scenario.Finished.Task.IsCompleted);

            target.Scenario = null;
            await ProbeAsync(provider, remote.SiloAddress, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(initialSummaryRequests + 4, target.SummaryRequests);
            Assert.False(scenario.Finished.Task.IsCompleted);

            scenario.Release();
            await scenario.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await ProbeAsync(provider, remote.SiloAddress, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(initialSummaryRequests + 5, target.SummaryRequests);
        }
        finally
        {
            cancellation.Cancel();
            scenario.Release();
            try
            {
                await Task.WhenAll(probes).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            await scenario.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            target.Scenario = null;
        }
    }

    private static Task ProbeAsync(ClusterManifestProvider provider, SiloAddress peer, CancellationToken cancellationToken) =>
        (Task)typeof(ClusterManifestProvider).GetMethod("ProbePeerForManifests", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider, [peer, new[] { peer }, new ConcurrentDictionary<ManifestHash, GrainManifest>(), cancellationToken])!;

    private sealed class ProbeScenario(bool waitForUpdate)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _canceled;
        private int _finished;

        public bool WaitForUpdate => waitForUpdate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                if (Interlocked.Increment(ref _canceled) == 3)
                {
                    CancellationObserved.TrySetResult();
                }
            });
            if (Interlocked.Increment(ref _entered) == 3)
            {
                Entered.TrySetResult();
            }

            // Observe cancellation while deliberately keeping the remote invocation alive.
            await _release.Task;
            if (Interlocked.Increment(ref _finished) == 3)
            {
                Finished.TrySetResult();
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class PeerTarget : SystemTarget, IClusterManifestSystemTarget, ILifecycleParticipant<ISiloLifecycle>
    {
        private ProbeScenario? _scenario;
        private int _summaryRequests;

        public PeerTarget(SystemTargetShared shared) : base(TargetType, shared)
        {
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public ProbeScenario? Scenario
        {
            get => Volatile.Read(ref _scenario);
            set => Volatile.Write(ref _scenario, value);
        }

        public int SummaryRequests => Volatile.Read(ref _summaryRequests);

        public async ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _summaryRequests);
            if (Scenario is { WaitForUpdate: false } scenario)
            {
                await scenario.WaitAsync(cancellationToken);
            }

            return new ClusterManifestHashSummary(MajorMinorVersion.MinValue, []);
        }

        public async ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(MajorMinorVersion previousVersion, CancellationToken cancellationToken = default)
        {
            if (Scenario is { WaitForUpdate: true } scenario)
            {
                await scenario.WaitAsync(cancellationToken);
            }

            return null;
        }

        public ValueTask<ClusterManifest> GetClusterManifest(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ManifestHash> GetSiloManifestHash(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GrainManifest?> GetSiloManifestByHash(ManifestHash hash, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Participate(ISiloLifecycle lifecycle)
        {
        }
    }
}
