using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
[TestCategory("BVT"), TestCategory("GrainDirectory")]
public class GrainDirectoryHandoffManagerTests
{
    [Theory]
    [InlineData(SiloStatus.Active, true)]
    [InlineData(SiloStatus.ShuttingDown, true)]
    [InlineData(SiloStatus.Stopping, true)]
    [InlineData(SiloStatus.Dead, false)]
    public void IsTransferableRegistration_UsesSnapshotStatus(SiloStatus status, bool expected)
    {
        var silo = CreateSiloAddress(1);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(silo, status, "silo"), version: 2);

        Assert.Equal(expected, GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    [Fact]
    public void IsTransferableRegistration_AllowsUnknownSiloWithoutNewerMembershipVersion()
    {
        var silo = CreateSiloAddress(1);
        var unrelatedSilo = CreateSiloAddress(1, port: 11112);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(unrelatedSilo, SiloStatus.Active, "other"), version: 2);

        Assert.True(GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    [Fact]
    public void IsTransferableRegistration_RejectsUnknownSiloWithOlderMembershipVersion()
    {
        var silo = CreateSiloAddress(1);
        var unrelatedSilo = CreateSiloAddress(1, port: 11112);
        var address = CreateGrainAddress(silo, membershipVersion: 1);
        var snapshot = CreateSnapshot(new ClusterMember(unrelatedSilo, SiloStatus.Active, "other"), version: 2);

        Assert.False(GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    [Fact]
    public void IsTransferableRegistration_RejectsSiloReplacedBySuccessor()
    {
        var silo = CreateSiloAddress(1);
        var successor = CreateSiloAddress(2);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(successor, SiloStatus.Active, "silo"), version: 2);

        Assert.False(GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    private static ClusterMembershipSnapshot CreateSnapshot(ClusterMember member, long version)
        => new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member.SiloAddress, member), new MembershipVersion(version));

    private static GrainAddress CreateGrainAddress(SiloAddress siloAddress, long membershipVersion)
        => new()
        {
            GrainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N")),
            ActivationId = ActivationId.NewId(),
            SiloAddress = siloAddress,
            MembershipVersion = new MembershipVersion(membershipVersion)
        };

    private static SiloAddress CreateSiloAddress(int generation, int port = 11111)
        => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);

    [Fact]
    public async Task AcceptExistingRegistrations_FiltersDeadStaleAndNullSiloUntransferableRegistrationsBeforeForwarding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var transferable = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "transferable");
        var dead = CreateGrainAddress(CreateTestGrainId("dead"), fixture.DeadSilo, fixture.RoutingMembershipVersion);
        var stale = CreateGrainAddress(CreateTestGrainId("stale"), fixture.ReplacedSilo, fixture.RoutingMembershipVersion);
        var nullSilo = CreateGrainAddress(CreateTestGrainId("null-silo"), null, fixture.RoutingMembershipVersion);
        var registrations = new List<GrainAddress> { transferable, dead, stale, nullSilo };
        var registrationEntered = new TaskCompletionSource<GrainAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionSentinelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionSentinel = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "completion-sentinel");

        fixture.RegistrationHandler = async address =>
        {
            if (address.Equals(transferable))
            {
                registrationEntered.TrySetResult(address);
                await releaseRegistration.Task.WaitAsync(cancellationToken);
            }
            else if (address.Equals(completionSentinel))
            {
                completionSentinelEntered.TrySetResult();
            }
            else
            {
                throw new InvalidOperationException($"Unexpected registration: {address}");
            }

            return new AddressAndTag(address, 1);
        };

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);

        try
        {
            Assert.Equal(transferable, await registrationEntered.Task.WaitAsync(cancellationToken));
            Assert.Collection(
                fixture.ForwardedRegistrations.ToArray(),
                forwarded => Assert.Equal(transferable, forwarded));

            releaseRegistration.TrySetResult();
            var sentinelRegistrations = new List<GrainAddress> { completionSentinel };
            fixture.HandoffManager.AcceptExistingRegistrations(sentinelRegistrations);
            await completionSentinelEntered.Task.WaitAsync(cancellationToken);
            await fixture.DrainSchedulerAsync(cancellationToken);

            Assert.Empty(registrations);
            Assert.Empty(sentinelRegistrations);
            Assert.Equal(new[] { transferable, completionSentinel }, fixture.ForwardedRegistrations.ToArray());
            Assert.Equal(
                new[] { fixture.AcceptedSourceSilo, fixture.AcceptedSourceSilo },
                fixture.RemoteDirectoryResolutions.ToArray());
            fixture.SiloStatusOracle.DidNotReceive().GetApproximateSiloStatus(Arg.Any<SiloAddress>());
            Assert.Equal(0, fixture.CatalogResolutionCount);
            Assert.Equal(0, fixture.CatalogDeletionCount);
        }
        finally
        {
            releaseRegistration.TrySetResult();
        }
    }

    [Fact]
    public async Task AcceptExistingRegistrations_WhenInputIsEmpty_ProducesNoRegistrationOrDeletionWork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var registrations = new List<GrainAddress>();

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);
        await fixture.DrainSchedulerAsync(cancellationToken);

        Assert.Empty(registrations);
        Assert.Empty(fixture.ForwardedRegistrations);
        Assert.Empty(fixture.RemoteDirectoryResolutions);
        Assert.Equal(0, fixture.CatalogResolutionCount);
        Assert.Equal(0, fixture.CatalogDeletionCount);
    }

    [Fact]
    public async Task AcceptExistingRegistrations_WhenDirectoryIsStopped_ProducesNoRegistrationOrDeletionWork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var registration = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "stopped");
        var registrations = new List<GrainAddress> { registration };
        fixture.LocalDirectory.Running = false;

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);
        await fixture.DrainSchedulerAsync(cancellationToken);

        Assert.Collection(registrations, actual => Assert.Equal(registration, actual));
        Assert.Empty(fixture.ForwardedRegistrations);
        Assert.Empty(fixture.RemoteDirectoryResolutions);
        Assert.Equal(0, fixture.CatalogResolutionCount);
        Assert.Equal(0, fixture.CatalogDeletionCount);
    }

    private static ClusterMembershipSnapshot CreateSnapshot(long version, params ClusterMember[] members)
    {
        var builder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        foreach (var member in members)
        {
            builder.Add(member.SiloAddress, member);
        }

        return new ClusterMembershipSnapshot(builder.ToImmutable(), new MembershipVersion(version));
    }

    private static GrainAddress CreateGrainAddress(GrainId grainId, SiloAddress? siloAddress, MembershipVersion membershipVersion)
        => new()
        {
            GrainId = grainId,
            ActivationId = ActivationId.NewId(),
            SiloAddress = siloAddress,
            MembershipVersion = membershipVersion
        };

    private static GrainId CreateTestGrainId(string key) => GrainId.Create("handoff-test", key);

    private static GrainId CreateGrainIdOwnedBy(string key, SiloAddress owner, IReadOnlyList<SiloAddress> silos)
    {
        for (var i = 0; i < 10_000; i++)
        {
            var candidate = CreateTestGrainId($"{key}-{i}");
            if (CalculateDirectoryOwner(candidate, silos).Equals(owner))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Unable to create a grain id owned by {owner}.");
    }

    private static SiloAddress CalculateDirectoryOwner(GrainId grainId, IReadOnlyList<SiloAddress> silos)
    {
        var sorted = new SiloAddress[silos.Count];
        for (var i = 0; i < silos.Count; i++)
        {
            sorted[i] = silos[i];
        }

        Array.Sort(sorted, static (left, right) =>
        {
            var hashComparison = left.GetConsistentHashCode().CompareTo(right.GetConsistentHashCode());
            return hashComparison != 0 ? hashComparison : left.CompareTo(right);
        });

        var hash = unchecked((int)grainId.GetUniformHashCode());
        for (var i = sorted.Length - 1; i >= 0; i--)
        {
            if (sorted[i].GetConsistentHashCode() <= hash)
            {
                return sorted[i];
            }
        }

        return sorted[^1];
    }

    private sealed class HandoffManagerFixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly IRemoteGrainDirectory remoteDirectory;
        private readonly ICatalog catalog;
        private int catalogResolutionCount;
        private int catalogDeletionCount;

        private HandoffManagerFixture(
            ServiceProvider services,
            LocalGrainDirectory localDirectory,
            TestClusterMembershipService membershipService,
            ISiloStatusOracle siloStatusOracle,
            IInternalGrainFactory grainFactory,
            IRemoteGrainDirectory remoteDirectory,
            ICatalog catalog,
            SiloAddress acceptedSourceSilo,
            SiloAddress deadSilo,
            SiloAddress replacedSilo,
            MembershipVersion routingMembershipVersion,
            SiloAddress[] routingSilos)
        {
            this.services = services;
            this.remoteDirectory = remoteDirectory;
            this.catalog = catalog;
            LocalDirectory = localDirectory;
            MembershipService = membershipService;
            GrainFactory = grainFactory;
            SiloStatusOracle = siloStatusOracle;
            AcceptedSourceSilo = acceptedSourceSilo;
            DeadSilo = deadSilo;
            ReplacedSilo = replacedSilo;
            RoutingMembershipVersion = routingMembershipVersion;
            RoutingSilos = routingSilos;

            siloStatusOracle.GetApproximateSiloStatus(Arg.Any<SiloAddress>())
                .Returns(call => MembershipService.CurrentSnapshot.GetSiloStatus(call.Arg<SiloAddress>()));
            grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceType, Arg.Any<SiloAddress>())
                .Returns(call =>
                {
                    RemoteDirectoryResolutions.Enqueue(call.ArgAt<SiloAddress>(1));
                    return this.remoteDirectory;
                });
            grainFactory.GetSystemTarget<ICatalog>(Constants.CatalogType, Arg.Any<SiloAddress>())
                .Returns(_ =>
                {
                    Interlocked.Increment(ref catalogResolutionCount);
                    return this.catalog;
                });
            this.remoteDirectory.AcceptSplitPartition(Arg.Any<List<GrainAddress>>()).Returns(Task.CompletedTask);
            this.remoteDirectory.RegisterAsync(
                    Arg.Any<GrainAddress>(),
                    Arg.Any<GrainAddress?>(),
                    Arg.Any<int>())
                .Returns(call =>
                {
                    var address = call.ArgAt<GrainAddress>(0);
                    ForwardedRegistrations.Enqueue(address);
                    return RegistrationHandler(address);
                });
            this.catalog.DeleteActivations(
                    Arg.Any<List<GrainAddress>>(),
                    Arg.Any<DeactivationReasonCode>(),
                    Arg.Any<string>())
                .Returns(call =>
                {
                    Interlocked.Increment(ref catalogDeletionCount);
                    return CatalogDeletionHandler(
                        call.ArgAt<List<GrainAddress>>(0),
                        call.ArgAt<DeactivationReasonCode>(1),
                        call.ArgAt<string>(2));
                });
        }

        public LocalGrainDirectory LocalDirectory { get; }

        public GrainDirectoryHandoffManager HandoffManager => LocalDirectory.HandoffManager;

        public TestClusterMembershipService MembershipService { get; }

        public IInternalGrainFactory GrainFactory { get; }

        public ISiloStatusOracle SiloStatusOracle { get; }

        public SiloAddress AcceptedSourceSilo { get; }

        public SiloAddress DeadSilo { get; }

        public SiloAddress ReplacedSilo { get; }

        public MembershipVersion RoutingMembershipVersion { get; }

        public SiloAddress[] RoutingSilos { get; }

        public ConcurrentQueue<GrainAddress> ForwardedRegistrations { get; } = new();

        public ConcurrentQueue<SiloAddress> RemoteDirectoryResolutions { get; } = new();

        public int CatalogResolutionCount => Volatile.Read(ref catalogResolutionCount);

        public int CatalogDeletionCount => Volatile.Read(ref catalogDeletionCount);

        public Func<GrainAddress, Task<AddressAndTag>> RegistrationHandler { get; set; }
            = static address => Task.FromResult(new AddressAndTag(address, 1));

        public Func<List<GrainAddress>, DeactivationReasonCode, string, Task> CatalogDeletionHandler { get; set; }
            = static (_, _, _) => Task.CompletedTask;

        public static async Task<HandoffManagerFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var localSilo = CreateSiloAddress(1, 11111);
            var acceptedSourceSilo = CreateSiloAddress(1, 11112);
            var deadSilo = CreateSiloAddress(1, 11113);
            var replacedSilo = CreateSiloAddress(1, 11114);
            var replacementSilo = CreateSiloAddress(2, 11114);
            var routingSnapshot = CreateSnapshot(
                version: 1,
                new ClusterMember(localSilo, SiloStatus.Active, "local"),
                new ClusterMember(acceptedSourceSilo, SiloStatus.Active, "accepted"),
                new ClusterMember(replacementSilo, SiloStatus.Active, "replacement"));
            var filteringSnapshot = CreateSnapshot(
                version: 2,
                new ClusterMember(localSilo, SiloStatus.Active, "local"),
                new ClusterMember(acceptedSourceSilo, SiloStatus.Active, "accepted"),
                new ClusterMember(deadSilo, SiloStatus.Dead, "dead"),
                new ClusterMember(replacementSilo, SiloStatus.Active, "replacement"));
            var membershipService = new TestClusterMembershipService(routingSnapshot);
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.GatewayAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("localhost");
            localSiloDetails.Name.Returns("TestSilo");
            localSiloDetails.ClusterId.Returns("TestCluster");
            var siloStatusOracle = Substitute.For<ISiloStatusOracle>();
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            var remoteDirectory = Substitute.For<IRemoteGrainDirectory>();
            var catalog = Substitute.For<ICatalog>();
            var services = new ServiceCollection()
                .AddMetrics()
                .AddSingleton<OrleansInstruments>()
                .AddSingleton<SchedulerInstruments>()
                .AddSingleton<CatalogInstruments>()
                .AddSingleton<DirectoryInstruments>()
                .AddSingleton<GrainInstruments>()
                .AddSingleton<MessagingInstruments>()
                .AddSingleton<MessagingProcessingInstruments>()
                .BuildServiceProvider();
            Factory<LocalGrainDirectoryPartition> partitionFactory = () => new LocalGrainDirectoryPartition(
                membershipService,
                Options.Create(new GrainDirectoryOptions()),
                NullLoggerFactory.Instance);
            var systemTargetShared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: localSiloDetails,
                loggerFactory: NullLoggerFactory.Instance,
                schedulingOptions: Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activations: new ActivationDirectory(services.GetRequiredService<CatalogInstruments>()),
                schedulerInstruments: services.GetRequiredService<SchedulerInstruments>(),
                grainInstruments: services.GetRequiredService<GrainInstruments>(),
                messagingInstruments: services.GetRequiredService<MessagingInstruments>(),
                messagingProcessingInstruments: services.GetRequiredService<MessagingProcessingInstruments>());
            var localDirectory = new LocalGrainDirectory(
                serviceProvider: services,
                siloDetails: localSiloDetails,
                siloStatusOracle: siloStatusOracle,
                clusterMembershipService: membershipService,
                grainFactory: grainFactory,
                grainDirectoryPartitionFactory: partitionFactory,
                developmentClusterMembershipOptions: Options.Create(new DevelopmentClusterMembershipOptions()),
                grainDirectoryOptions: Options.Create(new GrainDirectoryOptions()),
                loggerFactory: NullLoggerFactory.Instance,
                directoryInstruments: services.GetRequiredService<DirectoryInstruments>(),
                systemTargetShared: systemTargetShared)
            {
                Running = true
            };
            var fixture = new HandoffManagerFixture(
                services,
                localDirectory,
                membershipService,
                siloStatusOracle,
                grainFactory,
                remoteDirectory,
                catalog,
                acceptedSourceSilo,
                deadSilo,
                replacedSilo,
                routingSnapshot.Version,
                [localSilo, acceptedSourceSilo, replacementSilo]);

            var bootstrapGrainId = CreateGrainIdOwnedBy("bootstrap", localSilo, fixture.RoutingSilos);
            var bootstrapAddress = CreateGrainAddress(bootstrapGrainId, localSilo, routingSnapshot.Version);
            await localDirectory.RegisterAsync(bootstrapAddress, previousAddress: null, hopCount: 0).WaitAsync(cancellationToken);
            await fixture.DrainSchedulerAsync(cancellationToken);
            membershipService.CurrentSnapshot = filteringSnapshot;
            fixture.ForwardedRegistrations.Clear();
            fixture.RemoteDirectoryResolutions.Clear();
            fixture.SiloStatusOracle.ClearReceivedCalls();
            return fixture;
        }

        public GrainAddress CreateRemoteOwnedAddress(SiloAddress activationSilo, string key)
        {
            var grainId = CreateGrainIdOwnedBy(key, AcceptedSourceSilo, RoutingSilos);
            return CreateGrainAddress(grainId, activationSilo, RoutingMembershipVersion);
        }

        public async Task DrainSchedulerAsync(CancellationToken cancellationToken)
        {
            var reachedBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrierTask = LocalDirectory.RemoteGrainDirectory.WorkItemGroup.QueueTask(
                () =>
                {
                    reachedBarrier.TrySetResult();
                    return Task.CompletedTask;
                },
                LocalDirectory.RemoteGrainDirectory);
            await reachedBarrier.Task.WaitAsync(cancellationToken);
            await barrierTask.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await LocalDirectory.StopAsync();
            services.Dispose();
        }
    }

    private sealed class TestClusterMembershipService(ClusterMembershipSnapshot currentSnapshot) : IClusterMembershipService
    {
        public ClusterMembershipSnapshot CurrentSnapshot { get; set; } = currentSnapshot;

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates
            => throw new NotSupportedException("The fixture applies membership snapshots explicitly.");

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AcceptExistingRegistrations_WhenRegistrationSucceeds_RemovesPendingRegistrationWithoutDeletingWinner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var registration = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "success");
        var completionSentinel = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "success-completion-sentinel");
        var registrations = new List<GrainAddress> { registration };
        var sentinelRegistrations = new List<GrainAddress> { completionSentinel };
        var registrationEntered = new TaskCompletionSource<GrainAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionSentinelEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationAttemptCount = 0;
        var sentinelAttemptCount = 0;

        fixture.RegistrationHandler = async address =>
        {
            if (address.Equals(registration))
            {
                Interlocked.Increment(ref registrationAttemptCount);
                registrationEntered.TrySetResult(address);
                await releaseRegistration.Task.WaitAsync(cancellationToken);
                return new AddressAndTag(address, 1);
            }

            if (address.Equals(completionSentinel))
            {
                Interlocked.Increment(ref sentinelAttemptCount);
                completionSentinelEntered.TrySetResult(registrations.Count == 0);
                return new AddressAndTag(address, 1);
            }

            throw new InvalidOperationException($"Unexpected registration: {address}");
        };

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);

        try
        {
            Assert.Equal(registration, await registrationEntered.Task.WaitAsync(cancellationToken));
            Assert.Equal(1, Volatile.Read(ref registrationAttemptCount));
            Assert.Collection(
                fixture.ForwardedRegistrations.ToArray(),
                actual => Assert.Equal(registration, actual));

            releaseRegistration.TrySetResult();
            fixture.HandoffManager.AcceptExistingRegistrations(sentinelRegistrations);

            Assert.True(await completionSentinelEntered.Task.WaitAsync(cancellationToken));
            await fixture.DrainSchedulerAsync(cancellationToken);

            Assert.Empty(registrations);
            Assert.Empty(sentinelRegistrations);
            Assert.Equal(1, Volatile.Read(ref registrationAttemptCount));
            Assert.Equal(1, Volatile.Read(ref sentinelAttemptCount));
            Assert.Equal(new[] { registration, completionSentinel }, fixture.ForwardedRegistrations.ToArray());
            Assert.Equal(0, fixture.CatalogResolutionCount);
            Assert.Equal(0, fixture.CatalogDeletionCount);
        }
        finally
        {
            releaseRegistration.TrySetResult();
        }
    }

    [Fact]
    public async Task AcceptExistingRegistrations_WhenBatchPartiallyCompletes_RetainsOnlyFailureUntilRetrySucceeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var successful = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "partial-success");
        var initiallyFailed = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "partial-failure");
        var later = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "partial-later");
        var postRetrySentinel = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "partial-post-retry");
        var registrations = new List<GrainAddress> { successful, initiallyFailed };
        var laterRegistrations = new List<GrainAddress> { later };
        var sentinelRegistrations = new List<GrainAddress> { postRetrySentinel };
        var firstFailureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeFirstFailure = new TaskCompletionSource<AddressAndTag>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRetryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successfulAttemptCount = 0;
        var failedAttemptCount = 0;
        var laterAttemptCount = 0;
        var sentinelAttemptCount = 0;

        fixture.RegistrationHandler = async address =>
        {
            if (address.Equals(successful))
            {
                Interlocked.Increment(ref successfulAttemptCount);
                return new AddressAndTag(address, 1);
            }

            if (address.Equals(initiallyFailed))
            {
                var attempt = Interlocked.Increment(ref failedAttemptCount);
                if (attempt == 1)
                {
                    firstFailureEntered.TrySetResult();
                    return await completeFirstFailure.Task.WaitAsync(cancellationToken);
                }

                if (attempt == 2)
                {
                    retryEntered.TrySetResult();
                    return new AddressAndTag(address, 1);
                }

                throw new InvalidOperationException($"Unexpected attempt {attempt} for {address}.");
            }

            if (address.Equals(later))
            {
                Interlocked.Increment(ref laterAttemptCount);
                laterEntered.TrySetResult();
                await releaseLater.Task.WaitAsync(cancellationToken);
                return new AddressAndTag(address, 1);
            }

            if (address.Equals(postRetrySentinel))
            {
                Interlocked.Increment(ref sentinelAttemptCount);
                postRetryEntered.TrySetResult();
                return new AddressAndTag(address, 1);
            }

            throw new InvalidOperationException($"Unexpected registration: {address}");
        };

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);

        try
        {
            await firstFailureEntered.Task.WaitAsync(cancellationToken);
            Assert.Equal(
                new[] { successful, initiallyFailed },
                fixture.ForwardedRegistrations.ToArray());
            fixture.HandoffManager.AcceptExistingRegistrations(laterRegistrations);
            completeFirstFailure.TrySetException(new InvalidOperationException("Expected first-attempt failure."));

            await laterEntered.Task.WaitAsync(cancellationToken);

            Assert.Collection(registrations, actual => Assert.Equal(initiallyFailed, actual));
            Assert.Equal(1, Volatile.Read(ref successfulAttemptCount));
            Assert.Equal(1, Volatile.Read(ref failedAttemptCount));
            Assert.Equal(1, Volatile.Read(ref laterAttemptCount));

            releaseLater.TrySetResult();
            await retryEntered.Task.WaitAsync(cancellationToken);
            Assert.Equal(
                new[] { successful, initiallyFailed, later, initiallyFailed },
                fixture.ForwardedRegistrations.ToArray());
            fixture.HandoffManager.AcceptExistingRegistrations(sentinelRegistrations);
            await postRetryEntered.Task.WaitAsync(cancellationToken);
            await fixture.DrainSchedulerAsync(cancellationToken);

            Assert.Empty(registrations);
            Assert.Empty(laterRegistrations);
            Assert.Empty(sentinelRegistrations);
            Assert.Equal(1, Volatile.Read(ref successfulAttemptCount));
            Assert.Equal(2, Volatile.Read(ref failedAttemptCount));
            Assert.Equal(1, Volatile.Read(ref laterAttemptCount));
            Assert.Equal(1, Volatile.Read(ref sentinelAttemptCount));
            Assert.Equal(
                new[] { successful, initiallyFailed, later, initiallyFailed, postRetrySentinel },
                fixture.ForwardedRegistrations.ToArray());
            Assert.Equal(0, fixture.CatalogResolutionCount);
            Assert.Equal(0, fixture.CatalogDeletionCount);
        }
        finally
        {
            completeFirstFailure.TrySetException(new InvalidOperationException("Test cleanup."));
            releaseLater.TrySetResult();
        }
    }

    [Fact]
    public async Task ExecutePendingOperations_WhenHeadRegistrationFails_RunsLaterOperationBeforeRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var head = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "fairness-head");
        var later = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "fairness-later");
        var postRetrySentinel = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "fairness-post-retry");
        var headRegistrations = new List<GrainAddress> { head };
        var laterRegistrations = new List<GrainAddress> { later };
        var sentinelRegistrations = new List<GrainAddress> { postRetrySentinel };
        var events = new ConcurrentQueue<string>();
        var headFirstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeHeadFirstAttempt = new TaskCompletionSource<AddressAndTag>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headRetryEntered = new TaskCompletionSource<(bool LaterCompleted, bool HeadPending)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var postRetryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headAttemptCount = 0;
        var laterAttemptCount = 0;
        var sentinelAttemptCount = 0;

        fixture.RegistrationHandler = async address =>
        {
            if (address.Equals(head))
            {
                var attempt = Interlocked.Increment(ref headAttemptCount);
                if (attempt == 1)
                {
                    events.Enqueue("head:first");
                    headFirstEntered.TrySetResult();
                    return await completeHeadFirstAttempt.Task.WaitAsync(cancellationToken);
                }

                if (attempt == 2)
                {
                    events.Enqueue("head:retry");
                    headRetryEntered.TrySetResult((
                        laterRegistrations.Count == 0,
                        headRegistrations.Count == 1 && headRegistrations[0].Equals(head)));
                    fixture.HandoffManager.AcceptExistingRegistrations(sentinelRegistrations);
                    return new AddressAndTag(address, 1);
                }

                throw new InvalidOperationException($"Unexpected attempt {attempt} for {address}.");
            }

            if (address.Equals(later))
            {
                Interlocked.Increment(ref laterAttemptCount);
                events.Enqueue("later:started");
                laterEntered.TrySetResult();
                await releaseLater.Task.WaitAsync(cancellationToken);
                events.Enqueue("later:completed");
                return new AddressAndTag(address, 1);
            }

            if (address.Equals(postRetrySentinel))
            {
                Interlocked.Increment(ref sentinelAttemptCount);
                events.Enqueue("post-retry");
                postRetryEntered.TrySetResult();
                return new AddressAndTag(address, 1);
            }

            throw new InvalidOperationException($"Unexpected registration: {address}");
        };

        fixture.HandoffManager.AcceptExistingRegistrations(headRegistrations);

        try
        {
            await headFirstEntered.Task.WaitAsync(cancellationToken);
            fixture.HandoffManager.AcceptExistingRegistrations(laterRegistrations);
            completeHeadFirstAttempt.TrySetException(new InvalidOperationException("Expected head failure."));

            await laterEntered.Task.WaitAsync(cancellationToken);

            Assert.Collection(headRegistrations, actual => Assert.Equal(head, actual));
            Assert.Equal(new[] { "head:first", "later:started" }, events.ToArray());
            Assert.Equal(1, Volatile.Read(ref headAttemptCount));
            Assert.Equal(1, Volatile.Read(ref laterAttemptCount));
            Assert.Equal(0, Volatile.Read(ref sentinelAttemptCount));

            releaseLater.TrySetResult();
            Assert.Equal((true, true), await headRetryEntered.Task.WaitAsync(cancellationToken));
            await postRetryEntered.Task.WaitAsync(cancellationToken);
            await fixture.DrainSchedulerAsync(cancellationToken);

            Assert.Equal(
                new[] { "head:first", "later:started", "later:completed", "head:retry", "post-retry" },
                events.ToArray());
            Assert.Empty(headRegistrations);
            Assert.Empty(laterRegistrations);
            Assert.Empty(sentinelRegistrations);
            Assert.Equal(2, Volatile.Read(ref headAttemptCount));
            Assert.Equal(1, Volatile.Read(ref laterAttemptCount));
            Assert.Equal(1, Volatile.Read(ref sentinelAttemptCount));
            Assert.Equal(0, fixture.CatalogResolutionCount);
            Assert.Equal(0, fixture.CatalogDeletionCount);
        }
        finally
        {
            completeHeadFirstAttempt.TrySetException(new InvalidOperationException("Test cleanup."));
            releaseLater.TrySetResult();
        }
    }

    [Fact]
    public async Task DestroyDuplicateActivations_GroupsLosersBySiloAndDeletesThemWithDuplicateReasonWithoutDeletingWinners()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var sourceSiloA = fixture.AcceptedSourceSilo;
        var sourceSiloB = fixture.RoutingSilos[2];
        var loserA1 = fixture.CreateRemoteOwnedAddress(sourceSiloA, "duplicate-a-1");
        var loserA2 = fixture.CreateRemoteOwnedAddress(sourceSiloA, "duplicate-a-2");
        var loserB = fixture.CreateRemoteOwnedAddress(sourceSiloB, "duplicate-b");
        var retained = fixture.CreateRemoteOwnedAddress(sourceSiloA, "duplicate-retained");
        var winnerA1 = CreateGrainAddress(loserA1.GrainId, fixture.RoutingSilos[0], fixture.RoutingMembershipVersion);
        var winnerA2 = CreateGrainAddress(loserA2.GrainId, fixture.RoutingSilos[0], fixture.RoutingMembershipVersion);
        var winnerB = CreateGrainAddress(loserB.GrainId, fixture.RoutingSilos[0], fixture.RoutingMembershipVersion);
        var completionSentinel = fixture.CreateRemoteOwnedAddress(sourceSiloA, "duplicate-completion-sentinel");
        var registrations = new List<GrainAddress> { loserA1, loserA2, loserB, retained };
        var sentinelRegistrations = new List<GrainAddress> { completionSentinel };
        var catalogA = Substitute.For<ICatalog>();
        var catalogB = Substitute.For<ICatalog>();
        var catalogResolutions = new ConcurrentQueue<SiloAddress>();
        var catalogAEntered = new TaskCompletionSource<(GrainAddress[] Activations, DeactivationReasonCode Code, string Text)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogBEntered = new TaskCompletionSource<(GrainAddress[] Activations, DeactivationReasonCode Code, string Text)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCatalogA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCatalogB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinelEntered = new TaskCompletionSource<(int CatalogACompleted, int CatalogBCompleted, bool RegistrationsEmpty)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var catalogAInvocationCount = 0;
        var catalogBInvocationCount = 0;
        var catalogACompletionCount = 0;
        var catalogBCompletionCount = 0;
        var sentinelAttemptCount = 0;

        fixture.GrainFactory.GetSystemTarget<ICatalog>(Constants.CatalogType, Arg.Any<SiloAddress>())
            .Returns(call =>
            {
                var siloAddress = call.ArgAt<SiloAddress>(1);
                catalogResolutions.Enqueue(siloAddress);
                return siloAddress.Equals(sourceSiloA) ? catalogA
                    : siloAddress.Equals(sourceSiloB) ? catalogB
                    : throw new InvalidOperationException($"Unexpected catalog resolution for {siloAddress}.");
            });
        catalogA.DeleteActivations(
                Arg.Any<List<GrainAddress>>(),
                Arg.Any<DeactivationReasonCode>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Interlocked.Increment(ref catalogAInvocationCount);
                catalogAEntered.TrySetResult((
                    call.ArgAt<List<GrainAddress>>(0).ToArray(),
                    call.ArgAt<DeactivationReasonCode>(1),
                    call.ArgAt<string>(2)));
                await releaseCatalogA.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref catalogACompletionCount);
            });
        catalogB.DeleteActivations(
                Arg.Any<List<GrainAddress>>(),
                Arg.Any<DeactivationReasonCode>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Interlocked.Increment(ref catalogBInvocationCount);
                catalogBEntered.TrySetResult((
                    call.ArgAt<List<GrainAddress>>(0).ToArray(),
                    call.ArgAt<DeactivationReasonCode>(1),
                    call.ArgAt<string>(2)));
                await releaseCatalogB.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref catalogBCompletionCount);
            });
        fixture.RegistrationHandler = address =>
        {
            if (address.Equals(loserA1))
            {
                return Task.FromResult(new AddressAndTag(winnerA1, 1));
            }

            if (address.Equals(loserA2))
            {
                return Task.FromResult(new AddressAndTag(winnerA2, 1));
            }

            if (address.Equals(loserB))
            {
                return Task.FromResult(new AddressAndTag(winnerB, 1));
            }

            if (address.Equals(retained))
            {
                return Task.FromResult(new AddressAndTag(retained, 1));
            }

            if (address.Equals(completionSentinel))
            {
                Interlocked.Increment(ref sentinelAttemptCount);
                sentinelEntered.TrySetResult((
                    Volatile.Read(ref catalogACompletionCount),
                    Volatile.Read(ref catalogBCompletionCount),
                    registrations.Count == 0));
                return Task.FromResult(new AddressAndTag(address, 1));
            }

            throw new InvalidOperationException($"Unexpected registration: {address}");
        };

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);

        try
        {
            var firstCatalogTask = await Task.WhenAny(catalogAEntered.Task, catalogBEntered.Task).WaitAsync(cancellationToken);
            if (ReferenceEquals(firstCatalogTask, catalogAEntered.Task))
            {
                releaseCatalogA.TrySetResult();
                await catalogBEntered.Task.WaitAsync(cancellationToken);
            }
            else
            {
                releaseCatalogB.TrySetResult();
                await catalogAEntered.Task.WaitAsync(cancellationToken);
            }

            fixture.HandoffManager.AcceptExistingRegistrations(sentinelRegistrations);
            if (ReferenceEquals(firstCatalogTask, catalogAEntered.Task))
            {
                releaseCatalogB.TrySetResult();
            }
            else
            {
                releaseCatalogA.TrySetResult();
            }

            Assert.Equal((1, 1, true), await sentinelEntered.Task.WaitAsync(cancellationToken));
            await fixture.DrainSchedulerAsync(cancellationToken);

            var catalogACall = await catalogAEntered.Task.WaitAsync(cancellationToken);
            var catalogBCall = await catalogBEntered.Task.WaitAsync(cancellationToken);
            Assert.Equal(2, catalogACall.Activations.Length);
            Assert.Contains(loserA1, catalogACall.Activations);
            Assert.Contains(loserA2, catalogACall.Activations);
            Assert.DoesNotContain(loserB, catalogACall.Activations);
            Assert.Single(catalogBCall.Activations, actual => actual.Equals(loserB));
            Assert.DoesNotContain(loserA1, catalogBCall.Activations);
            Assert.DoesNotContain(loserA2, catalogBCall.Activations);
            Assert.Equal(DeactivationReasonCode.DuplicateActivation, catalogACall.Code);
            Assert.Equal(DeactivationReasonCode.DuplicateActivation, catalogBCall.Code);
            Assert.Equal("This grain has been activated elsewhere", catalogACall.Text);
            Assert.Equal("This grain has been activated elsewhere", catalogBCall.Text);

            var deletedActivations = catalogACall.Activations.Concat(catalogBCall.Activations).ToArray();
            Assert.DoesNotContain(winnerA1, deletedActivations);
            Assert.DoesNotContain(winnerA2, deletedActivations);
            Assert.DoesNotContain(winnerB, deletedActivations);
            Assert.DoesNotContain(retained, deletedActivations);
            Assert.Equal(3, deletedActivations.Length);
            Assert.Empty(registrations);
            Assert.Empty(sentinelRegistrations);
            Assert.Equal(1, Volatile.Read(ref catalogAInvocationCount));
            Assert.Equal(1, Volatile.Read(ref catalogBInvocationCount));
            Assert.Equal(1, Volatile.Read(ref catalogACompletionCount));
            Assert.Equal(1, Volatile.Read(ref catalogBCompletionCount));
            Assert.Equal(1, Volatile.Read(ref sentinelAttemptCount));
            Assert.Equal(2, catalogResolutions.Count);
            Assert.Contains(sourceSiloA, catalogResolutions);
            Assert.Contains(sourceSiloB, catalogResolutions);
            Assert.DoesNotContain(fixture.RoutingSilos[0], catalogResolutions);
        }
        finally
        {
            releaseCatalogA.TrySetResult();
            releaseCatalogB.TrySetResult();
        }
    }

    [Fact]
    public async Task DestroyDuplicateActivations_WhenSomeSilosAreNotActive_SkipsThemAndContinuesWithActiveGroups()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var deadSourceSilo = fixture.RoutingSilos[0];
        var activeSourceSilo = fixture.AcceptedSourceSilo;
        var stoppingSourceSilo = fixture.RoutingSilos[2];
        var deadLoser = fixture.CreateRemoteOwnedAddress(deadSourceSilo, "status-dead");
        var stoppingLoser = fixture.CreateRemoteOwnedAddress(stoppingSourceSilo, "status-stopping");
        var activeLoser = fixture.CreateRemoteOwnedAddress(activeSourceSilo, "status-active");
        var deadWinner = CreateGrainAddress(deadLoser.GrainId, activeSourceSilo, fixture.RoutingMembershipVersion);
        var stoppingWinner = CreateGrainAddress(stoppingLoser.GrainId, activeSourceSilo, fixture.RoutingMembershipVersion);
        var activeWinner = CreateGrainAddress(activeLoser.GrainId, deadSourceSilo, fixture.RoutingMembershipVersion);
        var postDeletionSentinel = fixture.CreateRemoteOwnedAddress(activeSourceSilo, "status-post-deletion-sentinel");
        var registrations = new List<GrainAddress> { activeLoser, stoppingLoser, deadLoser };
        var sentinelRegistrations = new List<GrainAddress> { postDeletionSentinel };
        var deadCatalog = Substitute.For<ICatalog>();
        var stoppingCatalog = Substitute.For<ICatalog>();
        var activeCatalog = Substitute.For<ICatalog>();
        var catalogResolutions = new ConcurrentQueue<SiloAddress>();
        var allRegistrationsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRegistrations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCatalogEntered = new TaskCompletionSource<(GrainAddress[] Activations, DeactivationReasonCode Code, string Text)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActiveCatalog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinelEntered = new TaskCompletionSource<(int ActiveDeletionCompleted, bool RegistrationsEmpty)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationAttemptCount = 0;
        var deadDeletionCount = 0;
        var stoppingDeletionCount = 0;
        var activeDeletionCount = 0;
        var activeDeletionCompletionCount = 0;
        var sentinelAttemptCount = 0;

        fixture.MembershipService.CurrentSnapshot = CreateSnapshot(
            version: 3,
            new ClusterMember(deadSourceSilo, SiloStatus.Active, "dead-source"),
            new ClusterMember(stoppingSourceSilo, SiloStatus.Active, "stopping-source"),
            new ClusterMember(activeSourceSilo, SiloStatus.Active, "active-source"));
        fixture.GrainFactory.GetSystemTarget<ICatalog>(Constants.CatalogType, Arg.Any<SiloAddress>())
            .Returns(call =>
            {
                var siloAddress = call.ArgAt<SiloAddress>(1);
                catalogResolutions.Enqueue(siloAddress);
                return siloAddress.Equals(deadSourceSilo) ? deadCatalog
                    : siloAddress.Equals(stoppingSourceSilo) ? stoppingCatalog
                    : siloAddress.Equals(activeSourceSilo) ? activeCatalog
                    : throw new InvalidOperationException($"Unexpected catalog resolution for {siloAddress}.");
            });
        deadCatalog.DeleteActivations(
                Arg.Any<List<GrainAddress>>(),
                Arg.Any<DeactivationReasonCode>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref deadDeletionCount);
                return Task.CompletedTask;
            });
        stoppingCatalog.DeleteActivations(
                Arg.Any<List<GrainAddress>>(),
                Arg.Any<DeactivationReasonCode>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref stoppingDeletionCount);
                return Task.CompletedTask;
            });
        activeCatalog.DeleteActivations(
                Arg.Any<List<GrainAddress>>(),
                Arg.Any<DeactivationReasonCode>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                Interlocked.Increment(ref activeDeletionCount);
                activeCatalogEntered.TrySetResult((
                    call.ArgAt<List<GrainAddress>>(0).ToArray(),
                    call.ArgAt<DeactivationReasonCode>(1),
                    call.ArgAt<string>(2)));
                await releaseActiveCatalog.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref activeDeletionCompletionCount);
            });
        fixture.RegistrationHandler = async address =>
        {
            if (address.Equals(postDeletionSentinel))
            {
                Interlocked.Increment(ref sentinelAttemptCount);
                sentinelEntered.TrySetResult((
                    Volatile.Read(ref activeDeletionCompletionCount),
                    registrations.Count == 0));
                return new AddressAndTag(address, 1);
            }

            var winner = address.Equals(deadLoser) ? deadWinner
                : address.Equals(stoppingLoser) ? stoppingWinner
                : address.Equals(activeLoser) ? activeWinner
                : throw new InvalidOperationException($"Unexpected registration: {address}");
            if (Interlocked.Increment(ref registrationAttemptCount) == 3)
            {
                allRegistrationsEntered.TrySetResult();
            }

            await releaseRegistrations.Task.WaitAsync(cancellationToken);
            return new AddressAndTag(winner, 1);
        };

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);

        try
        {
            await allRegistrationsEntered.Task.WaitAsync(cancellationToken);
            fixture.MembershipService.CurrentSnapshot = CreateSnapshot(
                version: 4,
                new ClusterMember(deadSourceSilo, SiloStatus.Dead, "dead-source"),
                new ClusterMember(stoppingSourceSilo, SiloStatus.Stopping, "stopping-source"),
                new ClusterMember(activeSourceSilo, SiloStatus.Active, "active-source"));
            releaseRegistrations.TrySetResult();

            var activeCatalogCall = await activeCatalogEntered.Task.WaitAsync(cancellationToken);
            Assert.Equal(new[] { activeSourceSilo }, catalogResolutions.ToArray());
            Assert.Equal(0, Volatile.Read(ref deadDeletionCount));
            Assert.Equal(0, Volatile.Read(ref stoppingDeletionCount));
            Assert.Empty(registrations);

            fixture.HandoffManager.AcceptExistingRegistrations(sentinelRegistrations);
            releaseActiveCatalog.TrySetResult();

            Assert.Equal((1, true), await sentinelEntered.Task.WaitAsync(cancellationToken));
            await fixture.DrainSchedulerAsync(cancellationToken);

            Assert.Single(activeCatalogCall.Activations, actual => actual.Equals(activeLoser));
            Assert.Equal(DeactivationReasonCode.DuplicateActivation, activeCatalogCall.Code);
            Assert.Equal("This grain has been activated elsewhere", activeCatalogCall.Text);
            Assert.DoesNotContain(deadWinner, activeCatalogCall.Activations);
            Assert.DoesNotContain(stoppingWinner, activeCatalogCall.Activations);
            Assert.DoesNotContain(activeWinner, activeCatalogCall.Activations);
            Assert.Empty(registrations);
            Assert.Empty(sentinelRegistrations);
            Assert.Equal(3, Volatile.Read(ref registrationAttemptCount));
            Assert.Equal(0, Volatile.Read(ref deadDeletionCount));
            Assert.Equal(0, Volatile.Read(ref stoppingDeletionCount));
            Assert.Equal(1, Volatile.Read(ref activeDeletionCount));
            Assert.Equal(1, Volatile.Read(ref activeDeletionCompletionCount));
            Assert.Equal(1, Volatile.Read(ref sentinelAttemptCount));
            Assert.Equal(new[] { activeSourceSilo }, catalogResolutions.ToArray());
        }
        finally
        {
            releaseRegistrations.TrySetResult();
            releaseActiveCatalog.TrySetResult();
        }
    }

    [Fact]
    public async Task AcceptExistingRegistrations_WhenRegistrationReturnsNoWinner_DeletesSubmittedActivationAsDuplicate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var duplicate = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "null-winner");
        var registrations = new List<GrainAddress> { duplicate };
        var deletionEntered = new TaskCompletionSource<(GrainAddress[] Activations, DeactivationReasonCode Code, string Text)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RegistrationHandler = _ => Task.FromResult(new AddressAndTag(null, 1));
        fixture.CatalogDeletionHandler = (activations, code, text) =>
        {
            deletionEntered.TrySetResult((activations.ToArray(), code, text));
            return Task.CompletedTask;
        };

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);

        var deletion = await deletionEntered.Task.WaitAsync(cancellationToken);
        await fixture.DrainSchedulerAsync(cancellationToken);

        Assert.Single(deletion.Activations, actual => actual.Equals(duplicate));
        Assert.Equal(DeactivationReasonCode.DuplicateActivation, deletion.Code);
        Assert.Equal("This grain has been activated elsewhere", deletion.Text);
        Assert.Empty(registrations);
        Assert.Equal(new[] { duplicate }, fixture.ForwardedRegistrations.ToArray());
        Assert.Equal(1, fixture.CatalogResolutionCount);
        Assert.Equal(1, fixture.CatalogDeletionCount);
    }

    [Fact]
    public async Task DestroyDuplicateActivations_WhenDeletionFails_RetriesSameGroupAfterLaterOperationProgresses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await HandoffManagerFixture.CreateAsync(cancellationToken);
        var duplicate = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "deletion-retry");
        var winner = CreateGrainAddress(duplicate.GrainId, fixture.RoutingSilos[0], fixture.RoutingMembershipVersion);
        var later = fixture.CreateRemoteOwnedAddress(fixture.AcceptedSourceSilo, "deletion-retry-later");
        var registrations = new List<GrainAddress> { duplicate };
        var laterRegistrations = new List<GrainAddress> { later };
        var events = new ConcurrentQueue<string>();
        var firstDeletionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeFirstDeletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDeletionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletionCalls = new ConcurrentQueue<(GrainAddress[] Activations, DeactivationReasonCode Code, string Text)>();
        var deletionAttemptCount = 0;
        var registrationAttemptCount = 0;
        var laterAttemptCount = 0;

        fixture.RegistrationHandler = address =>
        {
            if (address.Equals(duplicate))
            {
                Interlocked.Increment(ref registrationAttemptCount);
                return Task.FromResult(new AddressAndTag(winner, 1));
            }

            if (address.Equals(later))
            {
                Interlocked.Increment(ref laterAttemptCount);
                events.Enqueue("later");
                laterEntered.TrySetResult();
                return Task.FromResult(new AddressAndTag(address, 1));
            }

            throw new InvalidOperationException($"Unexpected registration: {address}");
        };
        fixture.CatalogDeletionHandler = async (activations, code, text) =>
        {
            deletionCalls.Enqueue((activations.ToArray(), code, text));
            var attempt = Interlocked.Increment(ref deletionAttemptCount);
            if (attempt == 1)
            {
                events.Enqueue("delete:first");
                firstDeletionEntered.TrySetResult();
                await completeFirstDeletion.Task.WaitAsync(cancellationToken);
                return;
            }

            if (attempt == 2)
            {
                events.Enqueue("delete:retry");
                retryDeletionEntered.TrySetResult();
                return;
            }

            throw new InvalidOperationException($"Unexpected deletion attempt {attempt}.");
        };

        fixture.HandoffManager.AcceptExistingRegistrations(registrations);

        try
        {
            await firstDeletionEntered.Task.WaitAsync(cancellationToken);
            fixture.HandoffManager.AcceptExistingRegistrations(laterRegistrations);
            completeFirstDeletion.TrySetException(new InvalidOperationException("Expected first deletion failure."));

            await laterEntered.Task.WaitAsync(cancellationToken);
            await retryDeletionEntered.Task.WaitAsync(cancellationToken);
            await fixture.DrainSchedulerAsync(cancellationToken);

            Assert.Equal(new[] { "delete:first", "later", "delete:retry" }, events.ToArray());
            Assert.Equal(2, Volatile.Read(ref deletionAttemptCount));
            Assert.Equal(1, Volatile.Read(ref registrationAttemptCount));
            Assert.Equal(1, Volatile.Read(ref laterAttemptCount));
            Assert.Empty(registrations);
            Assert.Empty(laterRegistrations);
            Assert.Equal(2, fixture.CatalogResolutionCount);
            Assert.Equal(2, fixture.CatalogDeletionCount);

            Assert.Collection(
                deletionCalls.ToArray(),
                first => AssertDuplicateDeletion(first, duplicate),
                retry => AssertDuplicateDeletion(retry, duplicate));
        }
        finally
        {
            completeFirstDeletion.TrySetException(new InvalidOperationException("Test cleanup."));
        }
    }

    private static void AssertDuplicateDeletion(
        (GrainAddress[] Activations, DeactivationReasonCode Code, string Text) deletion,
        GrainAddress expectedDuplicate)
    {
        Assert.Single(deletion.Activations, actual => actual.Equals(expectedDuplicate));
        Assert.Equal(DeactivationReasonCode.DuplicateActivation, deletion.Code);
        Assert.Equal("This grain has been activated elsewhere", deletion.Text);
    }
}
