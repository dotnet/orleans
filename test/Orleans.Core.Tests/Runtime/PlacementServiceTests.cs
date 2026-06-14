using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.TestingHost.Diagnostics;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime
{
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
        public void GetCompatibleSilos_FiltersInactiveSilos()
        {
            var localSilo = CreateSiloAddress(11111);
            var remoteSilo = CreateSiloAddress(11112);
            var inactiveSilo = CreateSiloAddress(11113);
            var target = CreateTarget(
                localSilo,
                activeSilos: [localSilo, remoteSilo],
                clusterManifest: CreateClusterManifest(1, 0, localSilo, remoteSilo, inactiveSilo));
            var placementTarget = CreatePlacementTarget();

            var compatibleSilos = target.GetCompatibleSilos(placementTarget);

            Assert.Equal(new[] { localSilo, remoteSilo }, compatibleSilos.OrderBy(static silo => silo));
        }

        [Fact]
        public void GetCompatibleSilos_FiltersLocalSiloWhenLocalStatusIsTerminating()
        {
            var localSilo = CreateSiloAddress(11111);
            var remoteSilo = CreateSiloAddress(11112);
            var target = CreateTarget(
                localSilo,
                activeSilos: [localSilo, remoteSilo],
                localSiloStatus: SiloStatus.ShuttingDown,
                assumeHomogeneousSilosForTesting: true);
            var placementTarget = CreatePlacementTarget();

            var compatibleSilos = target.GetCompatibleSilos(placementTarget);

            Assert.Equal(new[] { remoteSilo }, compatibleSilos);
        }

        [Fact]
        public void GetCompatibleSilosWithVersions_FiltersInactiveSilos()
        {
            var localSilo = CreateSiloAddress(11111);
            var remoteSilo = CreateSiloAddress(11112);
            var inactiveSilo = CreateSiloAddress(11113);
            var target = CreateTarget(
                localSilo,
                activeSilos: [localSilo, remoteSilo],
                clusterManifest: CreateClusterManifest(1, 0, localSilo, remoteSilo, inactiveSilo));
            var placementTarget = CreatePlacementTarget(interfaceVersion: 1);

            var compatibleSilosByVersion = target.GetCompatibleSilosWithVersions(placementTarget);

            var entry = Assert.Single(compatibleSilosByVersion);
            Assert.Equal(1, entry.Key);
            Assert.Equal(new[] { localSilo, remoteSilo }, entry.Value.OrderBy(static silo => silo));
        }

        private static PlacementService CreateTarget(
            SiloAddress localSilo = null,
            SiloAddress[] activeSilos = null,
            SiloStatus localSiloStatus = SiloStatus.Active,
            ClusterManifest clusterManifest = null,
            bool assumeHomogeneousSilosForTesting = false)
        {
            localSilo ??= CreateSiloAddress(11111);
            activeSilos ??= [localSilo];
            clusterManifest ??= CreateClusterManifest(1, 0, localSilo);

            var optionsMonitor = Substitute.For<IOptionsMonitor<SiloMessagingOptions>>();
            optionsMonitor.CurrentValue.Returns(new SiloMessagingOptions
            {
                AssumeHomogenousSilosForTesting = assumeHomogeneousSilosForTesting
            });

            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(localSilo);

            var siloStatusOracle = Substitute.For<ISiloStatusOracle>();
            siloStatusOracle.CurrentStatus.Returns(localSiloStatus);
            siloStatusOracle.GetActiveSilos().Returns(activeSilos);

            var clusterManifestProvider = new TestClusterManifestProvider(clusterManifest);
            var grainInterfaceVersions = new GrainVersionManifest(clusterManifestProvider);
            var versionSelectorManager = CreateCachedVersionSelectorManager(grainInterfaceVersions);
            var serviceProvider = new ServiceCollection().BuildServiceProvider();

            return new PlacementService(
                optionsMonitor,
                localSiloDetails,
                siloStatusOracle,
                NullLoggerFactory.Instance.CreateLogger<PlacementService>(),
                grainLocator: null!,
                grainInterfaceVersions,
                versionSelectorManager,
                directorResolver: null!,
                strategyResolver: null!,
                new PlacementFilterStrategyResolver(serviceProvider, new GrainPropertiesResolver(clusterManifestProvider)),
                new PlacementFilterDirectorResolver(serviceProvider));
        }

        private static PlacementTarget CreatePlacementTarget(ushort interfaceVersion = 0) => new(
            GrainId.Create(TestGrainType, "grain-1"),
            new Dictionary<string, object>(),
            interfaceVersion == 0 ? default : TestInterfaceType,
            interfaceVersion);

        private static SiloAddress CreateSiloAddress(int port)
        {
            return SiloAddress.New(IPAddress.Loopback, port, Interlocked.Increment(ref _siloGeneration));
        }

        private static CachedVersionSelectorManager CreateCachedVersionSelectorManager(GrainVersionManifest manifest)
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
                new CompatibilityDirectorManager(serviceProvider, options));
        }

        private static ClusterManifest CreateClusterManifest(long major, long minor, params SiloAddress[] silos)
        {
            var manifest = CreateGrainManifest();
            return new ClusterManifest(
                new MajorMinorVersion(major, minor),
                silos.ToImmutableDictionary(silo => silo, _ => manifest));
        }

        private static GrainManifest CreateGrainManifest()
        {
            var grains = ImmutableDictionary.CreateRange(
            [
                new KeyValuePair<GrainType, GrainProperties>(
                    TestGrainType,
                    new GrainProperties(CreatePropertyDictionary(
                    [
                        new KeyValuePair<string, string>(WellKnownGrainTypeProperties.TypeName, "Test"),
                        new KeyValuePair<string, string>(WellKnownGrainTypeProperties.FullTypeName, "UnitTests.Grains.Test"),
                        new KeyValuePair<string, string>($"{WellKnownGrainTypeProperties.ImplementedInterfacePrefix}0", TestInterfaceType.ToString())
                    ])))
            ]);
            var interfaces = ImmutableDictionary.CreateRange(
            [
                new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(
                    TestInterfaceType,
                    new GrainInterfaceProperties(CreatePropertyDictionary(
                    [
                        new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.TypeName, "ITest"),
                        new KeyValuePair<string, string>(WellKnownGrainInterfaceProperties.Version, "1")
                    ])))
            ]);

            return new GrainManifest(grains, interfaces);
        }

        private static ImmutableDictionary<string, string> CreatePropertyDictionary(params KeyValuePair<string, string>[] properties)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal, StringComparer.Ordinal);
            foreach (var property in properties)
            {
                builder.Add(property.Key, property.Value);
            }

            return builder.ToImmutable();
        }

        private static async Task StopAsync(PlacementService target, CancellationToken cancellationToken = default)
        {
            var lifecycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)target).Participate(lifecycle);
            await lifecycle.OnStart();
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

        private sealed class TestClusterManifestProvider(ClusterManifest current) : IClusterManifestProvider
        {
            public ClusterManifest Current { get; } = current;

            public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

            public GrainManifest LocalGrainManifest => Current.AllGrainManifests[0];

            private static async IAsyncEnumerable<ClusterManifest> GetUpdates()
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }
}
