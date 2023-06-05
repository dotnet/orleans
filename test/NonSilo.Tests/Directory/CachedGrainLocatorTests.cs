using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
    public class CachedGrainLocatorTests
    {
        private readonly LoggerFactory loggerFactory;
        private readonly SiloLifecycleSubject lifecycle;

        private readonly IGrainDirectory grainDirectory;
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly MockClusterMembershipService mockMembershipService;
        private readonly CachedGrainLocator grainLocator;

        public CachedGrainLocatorTests(ITestOutputHelper output)
        {
            loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(output) });
            lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());

            grainDirectory = Substitute.For<IGrainDirectory>();
            var services = new ServiceCollection()
                .AddSingleton(typeof(IKeyedServiceCollection<,>), typeof(KeyedServiceCollection<,>))
                .AddSingletonKeyedService(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (sp, name) => grainDirectory)
                .BuildServiceProvider();

            grainDirectoryResolver = new GrainDirectoryResolver(
                services,
                new GrainPropertiesResolver(new NoOpClusterManifestProvider()),
                Array.Empty<IGrainDirectoryResolver>());
            mockMembershipService = new MockClusterMembershipService();

            grainLocator = new CachedGrainLocator(
                grainDirectoryResolver, 
                mockMembershipService.Target);

            grainLocator.Participate(lifecycle);
        }

        // TODO
        //[Fact]
        //public void ConvertActivationAddressToGrainAddress()
        //{
        //    var expected = GenerateActivationAddress();
        //    var grainAddress = expected.ToGrainAddress();
        //    Assert.Equal(expected, grainAddress.ToActivationAddress());
        //}

        [Fact]
        public async Task RegisterWhenNoOtherEntryExists()
        {
            var silo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(silo, SiloStatus.Active, "exp");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expected = GenerateGrainAddress(silo);

            grainDirectory.Register(expected).Returns(expected);

            var actual = await grainLocator.Register(expected);
            Assert.Equal(expected, actual);
            await grainDirectory.Received(1).Register(expected);

            // Now should be in cache
            Assert.True(grainLocator.TryLookupInCache(expected.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expected, result);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExists()
        {
            var expectedSilo = GenerateSiloAddress();
            var otherSilo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var otherAddr = GenerateGrainAddress(otherSilo);

            grainDirectory.Register(otherAddr).Returns(expectedAddr);

            var actual = await grainLocator.Register(otherAddr);
            Assert.Equal(expectedAddr, actual);
            await grainDirectory.Received(1).Register(otherAddr);

            // Now should be in cache
            Assert.True(grainLocator.TryLookupInCache(expectedAddr.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expectedAddr, result);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExistsButSiloIsDead()
        {
            var expectedSilo = GenerateSiloAddress();
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            // First returns the outdated entry, then the new one
            grainDirectory.Register(expectedAddr).Returns(outdatedAddr, expectedAddr);

            var actual = await grainLocator.Register(expectedAddr);
            Assert.Equal(expectedAddr, actual);
            await grainDirectory.Received(2).Register(expectedAddr);
            await grainDirectory.Received(1).Unregister(outdatedAddr);

            // Now should be in cache
            Assert.True(grainLocator.TryLookupInCache(expectedAddr.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expectedAddr, result);

            await lifecycle.OnStop();
        }

        [Fact]
        public async Task LookupPopulateTheCache()
        {
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var grainAddress = GenerateGrainAddress(expectedSilo);

            grainDirectory.Lookup(grainAddress.GrainId).Returns(grainAddress);

            // Cache should be empty
            Assert.False(grainLocator.TryLookupInCache(grainAddress.GrainId, out _));

            // Do a remote lookup
            var result = await grainLocator.Lookup(grainAddress.GrainId);
            Assert.NotNull(result);
            Assert.Equal(grainAddress, result);

            // Now cache should be populated
            Assert.True(grainLocator.TryLookupInCache(grainAddress.GrainId, out var cachedValue));
            Assert.NotNull(cachedValue);
            Assert.Equal(grainAddress, cachedValue);
        }

        [Fact]
        public async Task LookupWhenEntryExistsButSiloIsDead()
        {
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            grainDirectory.Lookup(outdatedAddr.GrainId).Returns(outdatedAddr);

            var actual = await grainLocator.Lookup(outdatedAddr.GrainId);
            Assert.Null(actual);

            await grainDirectory.Received(1).Lookup(outdatedAddr.GrainId);
            await grainDirectory.Received(1).Unregister(outdatedAddr);
            Assert.False(grainLocator.TryLookupInCache(outdatedAddr.GrainId, out _));

            await lifecycle.OnStop();
        }

        [Fact]
        public async Task LocalLookupWhenEntryExistsButSiloIsDead()
        {
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            grainDirectory.Lookup(outdatedAddr.GrainId).Returns(outdatedAddr);
            Assert.False(grainLocator.TryLookupInCache(outdatedAddr.GrainId, out _));

            // Local lookup should never call the directory
            await grainDirectory.DidNotReceive().Lookup(outdatedAddr.GrainId);
            await grainDirectory.DidNotReceive().Unregister(outdatedAddr);

            await lifecycle.OnStop();
        }

        [Fact]
        public async Task CleanupWhenSiloIsDead()
        {
            var expectedSilo = GenerateSiloAddress();
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Active, "old");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            // Register two entries
            grainDirectory.Register(expectedAddr).Returns(expectedAddr);
            grainDirectory.Register(outdatedAddr).Returns(outdatedAddr);

            await grainLocator.Register(expectedAddr);
            await grainLocator.Register(outdatedAddr);

            // Simulate a dead silo
            mockMembershipService.UpdateSiloStatus(outdatedAddr.SiloAddress, SiloStatus.Dead, "old");

            // Wait a bit for the update to be processed
            await WaitUntilClusterChangePropagated();

            // Cleanup function from grain directory should have been called
            await grainDirectory
                .Received(1)
                .UnregisterSilos(Arg.Is<List<SiloAddress>>(list => list.Count == 1 && list.Contains(outdatedAddr.SiloAddress)));

            // Cache should have been cleaned
            Assert.False(grainLocator.TryLookupInCache(outdatedAddr.GrainId, out var unused1));
            Assert.True(grainLocator.TryLookupInCache(expectedAddr.GrainId, out var unused2));

            var result = await grainLocator.Lookup(expectedAddr.GrainId);
            Assert.NotNull(result);
            Assert.Equal(expectedAddr, result);

            await lifecycle.OnStop();
        }

        [Fact]
        public async Task UnregisterCallDirectoryAndCleanCache()
        {
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);

            grainDirectory.Register(expectedAddr).Returns(expectedAddr);

            // Register to populate cache
            await grainLocator.Register(expectedAddr);

            // Unregister and check if cache was cleaned
            await grainLocator.Unregister(expectedAddr, UnregistrationCause.Force);
            Assert.False(grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));
        }

        private GrainAddress GenerateGrainAddress(SiloAddress siloAddress = null)
        {
            return new GrainAddress
            {
                GrainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateGuidKey(Guid.NewGuid())),
                ActivationId = ActivationId.NewId(),
                SiloAddress = siloAddress ?? GenerateSiloAddress(),
                MembershipVersion = mockMembershipService.CurrentVersion,
            };
        }

        private int generation = 0;
        private SiloAddress GenerateSiloAddress() => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), ++generation);

        private async Task WaitUntilClusterChangePropagated()
        {
            await Until(() => mockMembershipService.CurrentVersion == ((CachedGrainLocator.ITestAccessor)grainLocator).LastMembershipVersion);
        }

        private static async Task Until(Func<bool> condition)
        {
            var maxTimeout = 40_000;
            while (!condition() && (maxTimeout -= 10) > 0) await Task.Delay(10);
            Assert.True(maxTimeout > 0);
        }

        private class NoOpClusterManifestProvider : IClusterManifestProvider
        {
            public ClusterManifest Current => new ClusterManifest(
                MajorMinorVersion.Zero,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty,
                ImmutableArray.Create(new GrainManifest(ImmutableDictionary<GrainType, GrainProperties>.Empty, ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty)));

            public IAsyncEnumerable<ClusterManifest> Updates => GetUpdates();

            public GrainManifest LocalGrainManifest { get; } = new GrainManifest(ImmutableDictionary<GrainType, GrainProperties>.Empty, ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

            private async IAsyncEnumerable<ClusterManifest> GetUpdates()
            {
                yield return Current;
                await Task.Delay(100);
                yield break;
            }
        }
    }
}
