using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class PullingAgentHostResolverTests
{
    private const string ProviderName = "host-resolver";
    private static readonly QueueId Queue = QueueId.GetQueueId("resolver", 0, 1);
    private static readonly GrainType AgentType = GrainType.Create("host-resolver-agent");
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    [InlineData("rejected", false)]
    [InlineData("rejected", true)]
    [InlineData("timeout", false)]
    [InlineData("timeout", true)]
    [InlineData("canceled", false)]
    [InlineData("canceled", true)]
    public async Task FilterEligibleSilos_MixedHealthyFailingAndIneligibleHosts_ReturnsHealthyHostsAndLogsFailure(
        string failureKind, bool synchronousFailure)
    {
        var setup = new Setup();
        var healthy = setup.AddSilo();
        var failing = setup.AddSilo();
        var ineligible = setup.AddSilo();
        var otherHealthy = setup.AddSilo();
        Exception failure = failureKind switch
        {
            "unavailable" => new SiloUnavailableException("Host became unavailable."),
            "rejected" => (OrleansMessageRejectionException)Activator.CreateInstance(
                typeof(OrleansMessageRejectionException),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null, args: ["Probe rejected."], culture: null)!,
            "timeout" => new TimeoutException("Probe timed out."),
            "canceled" => new OperationCanceledException("Remote probe canceled."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };
        setup.Runtime(failing).Probe = (_, _, _) => synchronousFailure ? throw failure : Task.FromException<bool>(failure);
        setup.Runtime(ineligible).Probe = (_, _, _) => Task.FromResult(false);

        var result = await setup.Resolver.FilterEligibleSilos(
            ProviderName, Queue, AgentType, [healthy, failing, ineligible, otherHealthy, healthy],
            TestContext.Current.CancellationToken);

        Assert.Equal([healthy, otherHealthy], result);
        foreach (var silo in new[] { healthy, failing, ineligible, otherHealthy })
        {
            Assert.Equal((ProviderName, (QueueId?)Queue, TestContext.Current.CancellationToken),
                Assert.Single(setup.Runtime(silo).Calls));
        }

        var log = Assert.Single(setup.Logger.Entries);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Same(failure, log.Exception);
        var properties = log.Properties;
        Assert.Equal(ProviderName, properties["ProviderName"]);
        Assert.Equal(Queue, properties["QueueId"]);
        Assert.Equal(failing, properties["Silo"]);
    }

    [Fact]
    public async Task FilterEligibleSilos_UnexpectedFailure_Propagates()
    {
        var setup = new Setup();
        var failing = setup.AddSilo();
        var healthy = setup.AddSilo();
        var failure = new InvalidOperationException("Invalid runtime configuration.");
        setup.Runtime(failing).Probe = (_, _, _) => Task.FromException<bool>(failure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Resolver.FilterEligibleSilos(
            ProviderName, Queue, AgentType, [failing, healthy], TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Empty(setup.Logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterEligibleSilos_AlreadyCanceledWithNoCandidates_PropagatesCancellation(bool filteredCandidate)
    {
        var setup = new Setup();
        var candidates = filteredCandidate ? new[] { setup.AddSilo(status: SiloStatus.Dead) } : [];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Resolver.FilterEligibleSilos(
            ProviderName, Queue, AgentType, candidates, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(setup.GrainFactory.Calls);
        Assert.Empty(setup.Logger.Entries);
    }

    [Fact]
    public void GetEligibleSilos_AlreadyCanceled_PropagatesCancellationBeforeManifestLookup()
    {
        var setup = new Setup();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = setup.Resolver.GetEligibleSilos(ProviderName, Queue, AgentType, default, cancellation.Token);
        });

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(setup.GrainFactory.Calls);
    }

    [Fact]
    public async Task FilterEligibleSilos_CanceledWithOutstandingCalls_CompletesWithoutWaitingForRemoteResponses()
    {
        var setup = new Setup();
        var first = setup.AddSilo();
        var second = setup.AddSilo();
        using var cancellation = new CancellationTokenSource();
        var firstResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Runtime(first).Probe = (_, _, _) => firstResponse.Task;
        setup.Runtime(second).Probe = (_, _, _) => secondResponse.Task;
        var resolving = setup.Resolver.FilterEligibleSilos(
            ProviderName, Queue, AgentType, [first, second], cancellation.Token);

        try
        {
            Assert.Equal((ProviderName, (QueueId?)Queue, cancellation.Token), Assert.Single(setup.Runtime(first).Calls));
            Assert.Equal((ProviderName, (QueueId?)Queue, cancellation.Token), Assert.Single(setup.Runtime(second).Calls));
            Assert.False(resolving.IsCompleted);
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                resolving.WaitAsync(PhaseTimeout, TestContext.Current.CancellationToken));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.False(firstResponse.Task.IsCompleted);
            Assert.False(secondResponse.Task.IsCompleted);
            Assert.Empty(setup.Logger.Entries);
        }
        finally
        {
            firstResponse.TrySetResult(true);
            secondResponse.TrySetResult(true);
        }
    }

    [Fact]
    public async Task FilterEligibleSilos_CanceledDuringFanout_DoesNotProbeRemainingCandidatesOrLogRemoteFailure()
    {
        var setup = new Setup();
        var first = setup.AddSilo();
        var second = setup.AddSilo();
        using var cancellation = new CancellationTokenSource();
        setup.Runtime(first).Probe = (_, _, _) =>
        {
            cancellation.Cancel();
            throw new TimeoutException("Transport failed while caller canceled.");
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Resolver.FilterEligibleSilos(
            ProviderName, Queue, AgentType, [first, second], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(setup.Runtime(second).Calls);
        Assert.Empty(setup.Logger.Entries);
    }

    [Fact]
    public async Task FilterEligibleSilos_MetadataAndMembershipFiltering_ProbesOnlyDistinctCompatibleActiveProviderHosts()
    {
        var setup = new Setup();
        var healthy = setup.AddSilo();
        var missingMember = setup.AddSilo(includeMember: false);
        var missingManifest = setup.AddSilo(includeManifest: false);
        var missingGrain = setup.AddSilo(includeGrain: false);
        var differentProvider = setup.AddSilo(providerName: ProviderName + "-other");
        var caseDifferentProvider = setup.AddSilo(providerName: ProviderName.ToUpperInvariant());
        var incompatible = setup.AddSilo();
        var inactive = new[] { SiloStatus.None, SiloStatus.Created, SiloStatus.Joining, SiloStatus.ShuttingDown, SiloStatus.Stopping, SiloStatus.Dead }
            .Select(status => setup.AddSilo(status: status)).ToArray();
        SiloAddress[] candidates = [healthy, missingMember, missingManifest, missingGrain, differentProvider, caseDifferentProvider, .. inactive, healthy];

        var result = await setup.Resolver.FilterEligibleSilos(
            ProviderName, null, AgentType, candidates, TestContext.Current.CancellationToken);

        Assert.Equal([healthy], result);
        Assert.Equal((ProviderName, (QueueId?)null, TestContext.Current.CancellationToken),
            Assert.Single(setup.Runtime(healthy).Calls));
        Assert.Equal((PullingAgentRuntime.TargetType, healthy), Assert.Single(setup.GrainFactory.Calls));
        foreach (var silo in candidates.Where(silo => silo != healthy).Append(incompatible))
        {
            Assert.Empty(setup.Runtime(silo).Calls);
        }

        Assert.Empty(setup.Logger.Entries);
    }

    [Fact]
    public async Task FilterEligibleSilos_NoCandidates_ReturnsEmptyWithoutProbing()
    {
        var setup = new Setup();

        var result = await setup.Resolver.FilterEligibleSilos(
            ProviderName, Queue, AgentType, [], TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Empty(setup.GrainFactory.Calls);
        Assert.Empty(setup.Logger.Entries);
    }

    private sealed class Setup
    {
        private readonly IClusterManifestProvider _manifests = Substitute.For<IClusterManifestProvider>();
        private readonly IClusterMembershipService _membership = Substitute.For<IClusterMembershipService>();
        private readonly Dictionary<SiloAddress, TestRuntime> _runtimes = [];
        private ImmutableDictionary<SiloAddress, GrainManifest> _silos = ImmutableDictionary<SiloAddress, GrainManifest>.Empty;
        private ImmutableDictionary<SiloAddress, ClusterMember> _members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty;

        public TestGrainFactory GrainFactory { get; } = new();
        public TestLogger Logger { get; } = new();
        public PullingAgentHostResolver Resolver { get; }

        public Setup()
        {
            _manifests.Current.Returns(_ => new ClusterManifest(MajorMinorVersion.Zero, _silos));
            _membership.CurrentSnapshot.Returns(_ => new ClusterMembershipSnapshot(_members, new MembershipVersion(1)));
            Resolver = new(_manifests, null!, _membership, GrainFactory, Logger);
        }

        public TestRuntime Runtime(SiloAddress silo) => _runtimes[silo];

        public SiloAddress AddSilo(
            SiloStatus status = SiloStatus.Active,
            bool includeMember = true,
            bool includeManifest = true,
            bool includeGrain = true,
            string providerName = ProviderName)
        {
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 10000 + _runtimes.Count), 1);
            var runtime = new TestRuntime();
            _runtimes.Add(silo, runtime);
            GrainFactory.Runtimes.Add(silo, runtime);
            if (includeMember)
            {
                _members = _members.Add(silo, new ClusterMember(silo, status, silo.ToString()));
            }

            if (includeManifest)
            {
                var grains = ImmutableDictionary<GrainType, GrainProperties>.Empty;
                if (includeGrain)
                {
                    grains = grains.Add(AgentType, new GrainProperties(ImmutableDictionary<string, string>.Empty
                        .WithComparers(StringComparer.Ordinal)
                        .Add(PullingAgentPlacementDirector.ProviderPropertyPrefix + providerName, "true")));
                }

                _silos = _silos.Add(silo, new GrainManifest(grains, ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty));
            }

            return silo;
        }
    }

    private sealed class TestGrainFactory : IInternalGrainFactory
    {
        public Dictionary<SiloAddress, TestRuntime> Runtimes { get; } = [];
        public List<(GrainType GrainType, SiloAddress Silo)> Calls { get; } = [];

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination)
            where TGrainInterface : ISystemTarget
        {
            Assert.Equal(PullingAgentRuntime.TargetType, grainType);
            Assert.Equal(typeof(IPullingAgentRuntime), typeof(TGrainInterface));
            Calls.Add((grainType, destination));
            return (TGrainInterface)(ISystemTarget)Runtimes[destination];
        }

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId) where TGrainInterface : ISystemTarget => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj) where TGrainObserverInterface : IAddressable => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public TGrainInterface Cast<TGrainInterface>(IAddressable grain) => throw new NotSupportedException();
        public object Cast(IAddressable grain, Type interfaceType) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    private sealed class TestRuntime : IPullingAgentRuntime
    {
        public List<(string ProviderName, QueueId? QueueId, CancellationToken CancellationToken)> Calls { get; } = [];
        public Func<string, QueueId?, CancellationToken, Task<bool>> Probe { get; set; } = (_, _, _) => Task.FromResult(true);

        public Task<bool> IsEligible(string providerName, QueueId? queueId, CancellationToken cancellationToken)
        {
            Calls.Add((providerName, queueId, cancellationToken));
            return Probe(providerName, queueId, cancellationToken);
        }

        public Task<bool> CanRetire(string providerName, QueueId queueId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Host eligibility resolution must not check retirement.");
    }

    private sealed class TestLogger : ILogger<PullingAgentHostResolver>
    {
        public List<(LogLevel Level, Exception? Exception, Dictionary<string, object?> Properties)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            Entries.Add((logLevel, exception, properties));
        }
    }
}
