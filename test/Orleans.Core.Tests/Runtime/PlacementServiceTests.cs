using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Placement")]
    [TestCategory("BVT"), TestCategory("Placement")]
    public class PlacementServiceTests
    {
        private static readonly GrainType TestGrainType = GrainType.Create("test");
        private static readonly GrainInterfaceType TestInterfaceType = GrainInterfaceType.Create("test.interface");
        private static int _siloGeneration;

        [Fact]
        public async Task LifecycleStop_CompletesWorkerTasks()
        {
            var target = CreateTarget();
            var testAccessor = GetTestAccessor(target);
            using var collector = new DiagnosticEventCollector(PlacementServiceEvents.ListenerName);

            await StopAsync(target);

            Assert.All(testAccessor.WorkerTasks, task => Assert.True(task.IsCompleted));
            await AssertWorkerStopEventsAsync(target, collector);
        }

        [Fact]
        public async Task LifecycleStop_WithCanceledToken_CompletesWorkerTasks()
        {
            var target = CreateTarget();
            var testAccessor = GetTestAccessor(target);
            using var collector = new DiagnosticEventCollector(PlacementServiceEvents.ListenerName);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await StopAsync(target, cts.Token);

            Assert.All(testAccessor.WorkerTasks, task => Assert.True(task.IsCompleted));
            await AssertWorkerStopEventsAsync(target, collector);
        }

        [Fact]
        public async Task AddressMessage_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
            };

            await StopAsync(target);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => target.AddressMessage(message));
        }

        [Fact]
        public async Task GetOrPlaceActivationAsync_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
                InterfaceType = GrainInterfaceType.Create("test.interface"),
                InterfaceVersion = 1,
            };

            await StopAsync(target);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => GetTestAccessor(target).GetOrPlaceActivationAsync(message));
        }

        [Fact]
        public async Task GetCompatibleSilos_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var placementTarget = new PlacementTarget(GrainId.Create("test", "grain-1"), new Dictionary<string, object>(), default, 0);

            await StopAsync(target);

            Assert.Throws<SiloUnavailableException>(() => target.GetCompatibleSilos(placementTarget));
        }

        [Fact]
        public async Task GetCompatibleSilosWithVersions_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var placementTarget = new PlacementTarget(
                GrainId.Create("test", "grain-1"),
                new Dictionary<string, object>(),
                GrainInterfaceType.Create("test.interface"),
                1);

            await StopAsync(target);

            Assert.Throws<SiloUnavailableException>(() => target.GetCompatibleSilosWithVersions(placementTarget));
        }

        [Fact]
        public async Task GetCompatibleSilos_WithoutFilters_UsesCachedResult()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos);
            var placementTarget = CreatePlacementTarget();

            var first = fixture.Target.GetCompatibleSilos(placementTarget);
            var second = fixture.Target.GetCompatibleSilos(placementTarget);

            Assert.Same(first, second);
            Assert.True(silos.ToHashSet().SetEquals(first));

            await StopAsync(fixture.Target);
        }

        [Fact]
        public async Task GetCompatibleSilos_WithoutInterfaceVersion_ReusesCacheAcrossInterfaces()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos);
            var firstTarget = CreatePlacementTarget(interfaceType: GrainInterfaceType.Create("test.interface.one"));
            var secondTarget = CreatePlacementTarget(interfaceType: GrainInterfaceType.Create("test.interface.two"));

            var first = fixture.Target.GetCompatibleSilos(firstTarget);
            var second = fixture.Target.GetCompatibleSilos(secondTarget);

            Assert.Same(first, second);

            await StopAsync(fixture.Target);
        }

        [Fact]
        public async Task GetCompatibleSilos_LocalSiloShuttingDown_ExcludesLocalSiloFromCachedManifest()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(
                activeSilos: silos,
                manifestSilos: [silos[1]],
                localSiloStatus: SiloStatus.ShuttingDown);

            var result = fixture.Target.GetCompatibleSilos(CreatePlacementTarget());

            Assert.Equal(new[] { silos[1] }, result);

            await StopAsync(fixture.Target);
        }

        [Fact]
        public async Task GetCompatibleSilosWithVersions_LocalSiloShuttingDown_ExcludesLocalSiloFromCachedManifest()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(
                activeSilos: silos,
                manifestSilos: [silos[1]],
                localSiloStatus: SiloStatus.ShuttingDown,
                interfaceVersion: 1);
            var target = new PlacementTarget(
                GrainId.Create(TestGrainType, "grain-1"),
                new Dictionary<string, object>(),
                TestInterfaceType,
                1);

            var result = fixture.Target.GetCompatibleSilosWithVersions(target);

            Assert.Equal(new[] { silos[1] }, result[1]);

            await StopAsync(fixture.Target);
        }

        [Fact]
        public async Task GetCompatibleSilos_MembershipChange_InvalidatesCachedResult()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos);
            var placementTarget = CreatePlacementTarget();

            var first = fixture.Target.GetCompatibleSilos(placementTarget);

            fixture.ClusterManifestProvider.SetCurrent(CreateClusterManifest(new[] { silos[1] }, version: new MajorMinorVersion(1, 0)));
            fixture.SetActiveSilos(silos[1]);
            var second = fixture.Target.GetCompatibleSilos(placementTarget);

            Assert.NotSame(first, second);
            Assert.Equal(new[] { silos[1] }, second);

            await StopAsync(fixture.Target);
        }

        [Fact]
        public async Task GetCompatibleSilos_ManifestUpdate_InvalidatesCachedResult()
        {
            var silos = CreateSilos(2);
            var manifestProvider = new TestClusterManifestProvider(CreateClusterManifest(new[] { silos[0] }));
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: new[] { silos[0] }, manifestProvider: manifestProvider);
            var placementTarget = CreatePlacementTarget();
            var first = fixture.Target.GetCompatibleSilos(placementTarget);
            Assert.Equal(new[] { silos[0] }, first);

            manifestProvider.Publish(CreateClusterManifest(new[] { silos[1] }, version: new MajorMinorVersion(1, 0)));
            fixture.SetActiveSilos(silos[1]);

            var second = fixture.Target.GetCompatibleSilos(placementTarget);

            Assert.NotSame(first, second);
            Assert.Equal(new[] { silos[1] }, second);

            await StopAsync(fixture.Target);
        }

        [Fact]
        public async Task GetCompatibleSilos_WithFilters_RunsFiltersPerRequest()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos, useFilter: true);

            var first = fixture.Target.GetCompatibleSilos(CreatePlacementTarget(new Dictionary<string, object> { ["target-silo"] = silos[0] }));
            var second = fixture.Target.GetCompatibleSilos(CreatePlacementTarget(new Dictionary<string, object> { ["target-silo"] = silos[1] }));

            Assert.Equal(new[] { silos[0] }, first);
            Assert.Equal(new[] { silos[1] }, second);
            Assert.Equal(2, fixture.FilterDirector!.CallCount);

            await StopAsync(fixture.Target);
        }

        private static PlacementService CreateTarget()
        {
            return new PlacementServiceFixture().Target;
        }

        private static PlacementTarget CreatePlacementTarget(
            Dictionary<string, object>? requestContextData = null,
            GrainInterfaceType? interfaceType = null) =>
            new(
                GrainId.Create(TestGrainType, "grain-1"),
                requestContextData ?? new Dictionary<string, object>(),
                interfaceType ?? TestInterfaceType,
                0);

        private static PlacementService CreateTarget(
            IOptionsMonitor<SiloMessagingOptions> optionsMonitor,
            ILocalSiloDetails localSiloDetails,
            ISiloStatusOracle siloStatusOracle,
            TestClusterManifestProvider clusterManifestProvider,
            IServiceProvider serviceProvider,
            CachedVersionSelectorManager versionSelectorManager)
        {
            var grainVersionManifest = new GrainVersionManifest(clusterManifestProvider);
            var filterStrategyResolver = new PlacementFilterStrategyResolver(serviceProvider, new GrainPropertiesResolver(clusterManifestProvider));
            var placementFilterDirectorResolver = new PlacementFilterDirectorResolver(serviceProvider);

            return new PlacementService(
                optionsMonitor,
                localSiloDetails,
                siloStatusOracle,
                NullLoggerFactory.Instance.CreateLogger<PlacementService>(),
                grainLocator: null!,
                grainInterfaceVersions: grainVersionManifest,
                versionSelectorManager,
                directorResolver: null!,
                strategyResolver: null!,
                filterStrategyResolver,
                placementFilterDirectorResolver);
        }

        private static SiloAddress[] CreateSilos(int count)
        {
            var result = new SiloAddress[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = SiloAddress.New(IPAddress.Loopback, 11111, Interlocked.Increment(ref _siloGeneration));
            }

            return result;
        }

        private static ClusterMembershipSnapshot CreateMembershipSnapshot(
            long version,
            IReadOnlyDictionary<SiloAddress, SiloStatus> statuses)
        {
            var members = statuses.ToImmutableDictionary(
                static entry => entry.Key,
                static entry => new ClusterMember(entry.Key, entry.Value, entry.Key.ToString()));
            return new ClusterMembershipSnapshot(members, new MembershipVersion(version));
        }

        private static ClusterManifest CreateClusterManifest(
            SiloAddress[] silos,
            bool useFilter = false,
            MajorMinorVersion? version = null,
            ushort interfaceVersion = 0)
        {
            var manifest = CreateGrainManifest(useFilter, interfaceVersion);
            var manifests = silos.ToImmutableDictionary(silo => silo, _ => manifest);
            return new ClusterManifest(version ?? MajorMinorVersion.Zero, manifests);
        }

        private static GrainManifest CreateGrainManifest(bool useFilter, ushort interfaceVersion = 0)
        {
            var grainProperties = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
            if (useFilter)
            {
                var filterName = typeof(TestPlacementFilterStrategy).Name;
                grainProperties = grainProperties
                    .Add(WellKnownGrainTypeProperties.PlacementFilter, filterName)
                    .Add($"{WellKnownGrainTypeProperties.PlacementFilter}.{filterName}.order", "0");
            }

            return new GrainManifest(
                ImmutableDictionary.CreateRange(new[] { new KeyValuePair<GrainType, GrainProperties>(TestGrainType, new GrainProperties(grainProperties)) }),
                ImmutableDictionary.CreateRange(new[]
                {
                    new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(
                        TestInterfaceType,
                        new GrainInterfaceProperties(
                            ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)
                           .Add(WellKnownGrainInterfaceProperties.Version, interfaceVersion.ToString())))
                }));
        }

        private static CachedVersionSelectorManager CreateCachedVersionSelectorManager(
            GrainVersionManifest manifest,
            IClusterMembershipService membership)
        {
            var services = new ServiceCollection();
            services.AddOptions<GrainVersioningOptions>();
            services.AddKeyedSingleton<VersionSelectorStrategy, AllCompatibleVersions>(nameof(AllCompatibleVersions));
            services.AddKeyedSingleton<CompatibilityStrategy, BackwardCompatible>(nameof(BackwardCompatible));
            services.AddKeyedSingleton<IVersionSelector, AllCompatibleVersionsSelector>(typeof(AllCompatibleVersions));
            services.AddKeyedSingleton<ICompatibilityDirector, BackwardCompatilityDirector>(typeof(BackwardCompatible));
            var serviceProvider = services.BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<GrainVersioningOptions>>();

            return new CachedVersionSelectorManager(
                manifest,
                new VersionSelectorManager(serviceProvider, options),
                new CompatibilityDirectorManager(serviceProvider, options),
                membership);
        }

        private static ServiceProvider CreateServiceProvider(TestPlacementFilterDirector? filterDirector = null)
        {
            IServiceCollection services = new ServiceCollection();
            if (filterDirector is not null)
            {
                services.Add(ServiceDescriptor.DescribeKeyed(
                    typeof(PlacementFilterStrategy),
                    typeof(TestPlacementFilterStrategy).Name,
                    typeof(TestPlacementFilterStrategy),
                    ServiceLifetime.Transient));
                services.AddKeyedSingleton<IPlacementFilterDirector>(typeof(TestPlacementFilterStrategy), filterDirector);
            }

            return services.BuildServiceProvider();
        }

        private static async Task<SiloLifecycleSubject> StartAsync(PlacementService target)
        {
            var lifecycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)target).Participate(lifecycle);
            await lifecycle.OnStart();
            return lifecycle;
        }

        private static async Task StopAsync(PlacementService target, CancellationToken cancellationToken = default)
        {
            var lifecycle = await StartAsync(target);
            await lifecycle.OnStop(cancellationToken);
        }

        private static PlacementService.ITestAccessor GetTestAccessor(PlacementService target) => target;

        private static async Task AssertWorkerStopEventsAsync(PlacementService target, DiagnosticEventCollector collector)
        {
            var workerCount = GetTestAccessor(target).WorkerTasks.Length;
            var stoppedEvents = new List<PlacementServiceEvents.WorkerStopped>(workerCount);

            while (stoppedEvents.Count < workerCount)
            {
                var diagnosticEvent = await collector.WaitForEventAsync(
                    nameof(PlacementServiceEvents.WorkerStopped),
                    evt => evt.Payload is PlacementServiceEvents.WorkerStopped stopped
                        && stopped.SiloAddress == target.LocalSilo
                        && stoppedEvents.All(existing => existing.WorkerIndex != stopped.WorkerIndex),
                    TimeSpan.FromSeconds(10));

                stoppedEvents.Add(Assert.IsType<PlacementServiceEvents.WorkerStopped>(diagnosticEvent.Payload));
            }

            Assert.Equal(workerCount, stoppedEvents.Count);
        }

        private sealed class PlacementServiceFixture
        {
            private Dictionary<SiloAddress, SiloStatus> _siloStatuses = new();
            private long _membershipVersion;

            public PlacementServiceFixture(
                SiloAddress[]? activeSilos = null,
                SiloAddress[]? manifestSilos = null,
                TestClusterManifestProvider? manifestProvider = null,
                bool useFilter = false,
                SiloStatus localSiloStatus = SiloStatus.Active,
                ushort interfaceVersion = 0)
            {
                activeSilos ??= CreateSilos(1);
                manifestSilos ??= activeSilos;
                SetActiveSilos(activeSilos);
                _siloStatuses[activeSilos[0]] = localSiloStatus;

                ClusterManifestProvider = manifestProvider ?? new TestClusterManifestProvider(CreateClusterManifest(manifestSilos, useFilter, interfaceVersion: interfaceVersion));
                _membershipVersion = ClusterManifestProvider.Current.Version.Major;
                ClusterMembershipService = new TestClusterMembershipService(CreateMembershipSnapshot(_membershipVersion, _siloStatuses));
                VersionSelectorManager = CreateCachedVersionSelectorManager(new GrainVersionManifest(ClusterManifestProvider), ClusterMembershipService);
                FilterDirector = useFilter ? new TestPlacementFilterDirector() : null;
                ServiceProvider = CreateServiceProvider(FilterDirector);

                var optionsMonitor = Substitute.For<IOptionsMonitor<SiloMessagingOptions>>();
                optionsMonitor.CurrentValue.Returns(new SiloMessagingOptions());

                var localSiloDetails = Substitute.For<ILocalSiloDetails>();
                localSiloDetails.SiloAddress.Returns(activeSilos[0]);

                SiloStatusOracle = Substitute.For<ISiloStatusOracle>();
                SiloStatusOracle.CurrentStatus.Returns(localSiloStatus);
                SiloStatusOracle.GetActiveSilos().Returns(_ => _siloStatuses
                    .Where(static entry => entry.Value == SiloStatus.Active)
                    .Select(static entry => entry.Key)
                    .ToArray());
                SiloStatusOracle.GetApproximateSiloStatuses(onlyActive: true).Returns(_ => _siloStatuses
                    .Where(static entry => entry.Value == SiloStatus.Active)
                    .ToDictionary());
                Target = CreateTarget(optionsMonitor, localSiloDetails, SiloStatusOracle, ClusterManifestProvider, ServiceProvider, VersionSelectorManager);
            }

            public PlacementService Target { get; }

            public ISiloStatusOracle SiloStatusOracle { get; }

            public TestClusterManifestProvider ClusterManifestProvider { get; }

            public TestClusterMembershipService ClusterMembershipService { get; }

            public ServiceProvider ServiceProvider { get; }

            public CachedVersionSelectorManager VersionSelectorManager { get; }

            public TestPlacementFilterDirector? FilterDirector { get; }

            public void SetActiveSilos(params SiloAddress[] silos)
            {
                _siloStatuses = silos.ToDictionary(silo => silo, _ => SiloStatus.Active);
                if (ClusterMembershipService is not null)
                {
                    ClusterMembershipService.Update(CreateMembershipSnapshot(++_membershipVersion, _siloStatuses));
                }
            }
        }

        private sealed class TestClusterManifestProvider : IClusterManifestProvider
        {
            private readonly Channel<ClusterManifest> _updates = Channel.CreateUnbounded<ClusterManifest>();
            private ClusterManifest _current;

            public TestClusterManifestProvider(ClusterManifest current)
            {
                _current = current;
                LocalGrainManifest = current.AllGrainManifests.FirstOrDefault() ?? CreateGrainManifest(useFilter: false);
            }

            public ClusterManifest Current => Volatile.Read(ref _current);

            public IAsyncEnumerable<ClusterManifest> Updates => ReadUpdates();

            public GrainManifest LocalGrainManifest { get; }

            public void Publish(ClusterManifest manifest)
            {
                SetCurrent(manifest);
                Assert.True(_updates.Writer.TryWrite(manifest));
            }

            public void SetCurrent(ClusterManifest manifest) => Volatile.Write(ref _current, manifest);

            private async IAsyncEnumerable<ClusterManifest> ReadUpdates([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return Current;

                await foreach (var manifest in _updates.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return manifest;
                }
            }
        }

        public sealed class TestClusterMembershipService(ClusterMembershipSnapshot current) : IClusterMembershipService
        {
            public ClusterMembershipSnapshot CurrentSnapshot { get; private set; } = current;

            public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => GetUpdates();

            public void Update(ClusterMembershipSnapshot snapshot) => CurrentSnapshot = snapshot;

            public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => default;

            public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

            private async IAsyncEnumerable<ClusterMembershipSnapshot> GetUpdates()
            {
                yield return CurrentSnapshot;
                await Task.CompletedTask;
            }
        }

        private sealed class TestPlacementFilterStrategy : PlacementFilterStrategy
        {
            public TestPlacementFilterStrategy()
                : base(0)
            {
            }
        }

        private sealed class TestPlacementFilterDirector : IPlacementFilterDirector
        {
            public int CallCount { get; private set; }

            public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
            {
                CallCount++;
                if (target.RequestContextData.TryGetValue("target-silo", out var value) && value is SiloAddress requestedSilo)
                {
                    return silos.Where(silo => silo.Equals(requestedSilo));
                }

                return silos;
            }
        }
    }
}
