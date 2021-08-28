using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
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
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(output) });
            this.lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());

            this.grainDirectory = Substitute.For<IGrainDirectory>();
            var services = new ServiceCollection()
                .AddSingleton(typeof(IKeyedServiceCollection<,>), typeof(KeyedServiceCollection<,>))
                .AddSingletonKeyedService(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (sp, name) => this.grainDirectory)
                .BuildServiceProvider();

            this.grainDirectoryResolver = new GrainDirectoryResolver(
                services,
                new GrainPropertiesResolver(new NoOpClusterManifestProvider()),
                Array.Empty<IGrainDirectoryResolver>());
            this.mockMembershipService = new MockClusterMembershipService();

            this.grainLocator = new CachedGrainLocator(
                this.grainDirectoryResolver, 
                this.mockMembershipService.Target);

            this.grainLocator.Participate(this.lifecycle);
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
            this.mockMembershipService.UpdateSiloStatus(silo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expected = GenerateGrainAddress(silo);

            this.grainDirectory.Register(expected).Returns(expected);

            var actual = await this.grainLocator.Register(expected.ToActivationAddress());
            Assert.Equal(expected.ToActivationAddress(), actual);
            await this.grainDirectory.Received(1).Register(expected);

            // Now should be in cache
            Assert.True(this.grainLocator.TryLookupInCache(expected.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expected.ToActivationAddress(), result);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExists()
        {
            var expectedSilo = GenerateSiloAddress();
            var otherSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var otherAddr = GenerateGrainAddress(otherSilo);

            this.grainDirectory.Register(otherAddr).Returns(expectedAddr);

            var actual = await this.grainLocator.Register(otherAddr.ToActivationAddress());
            Assert.Equal(expectedAddr.ToActivationAddress(), actual);
            await this.grainDirectory.Received(1).Register(otherAddr);

            // Now should be in cache
            Assert.True(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expectedAddr.ToActivationAddress(), result);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExistsButSiloIsDead()
        {
            var expectedSilo = GenerateSiloAddress();
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            // First returns the outdated entry, then the new one
            this.grainDirectory.Register(expectedAddr).Returns(outdatedAddr, expectedAddr);

            var actual = await this.grainLocator.Register(expectedAddr.ToActivationAddress());
            Assert.Equal(expectedAddr.ToActivationAddress(), actual);
            await this.grainDirectory.Received(2).Register(expectedAddr);
            await this.grainDirectory.Received(1).Unregister(outdatedAddr);

            // Now should be in cache
            Assert.True(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expectedAddr.ToActivationAddress(), result);

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task LookupPopulateTheCache()
        {
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var grainAddress = GenerateGrainAddress(expectedSilo);

            this.grainDirectory.Lookup(grainAddress.GrainId).Returns(grainAddress);

            // Cache should be empty
            Assert.False(this.grainLocator.TryLookupInCache(grainAddress.GrainId, out _));

            // Do a remote lookup
            var result = await this.grainLocator.Lookup(grainAddress.GrainId);
            Assert.NotNull(result);
            Assert.Equal(grainAddress.ToActivationAddress(), result);

            // Now cache should be populated
            Assert.True(this.grainLocator.TryLookupInCache(grainAddress.GrainId, out var cachedValue));
            Assert.NotNull(cachedValue);
            Assert.Equal(grainAddress.ToActivationAddress(), cachedValue);
        }

        [Fact]
        public async Task LookupWhenEntryExistsButSiloIsDead()
        {
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            this.grainDirectory.Lookup(outdatedAddr.GrainId).Returns(outdatedAddr);

            var actual = await this.grainLocator.Lookup(outdatedAddr.GrainId);
            Assert.Null(actual);

            await this.grainDirectory.Received(1).Lookup(outdatedAddr.GrainId);
            await this.grainDirectory.Received(1).Unregister(outdatedAddr);
            Assert.False(this.grainLocator.TryLookupInCache(outdatedAddr.GrainId, out _));

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task LocalLookupWhenEntryExistsButSiloIsDead()
        {
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            this.grainDirectory.Lookup(outdatedAddr.GrainId).Returns(outdatedAddr);
            Assert.False(this.grainLocator.TryLookupInCache(outdatedAddr.GrainId, out _));

            // Local lookup should never call the directory
            await this.grainDirectory.DidNotReceive().Lookup(outdatedAddr.GrainId);
            await this.grainDirectory.DidNotReceive().Unregister(outdatedAddr);

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task CleanupWhenSiloIsDead()
        {
            var expectedSilo = GenerateSiloAddress();
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Active, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            // Register two entries
            this.grainDirectory.Register(expectedAddr).Returns(expectedAddr);
            this.grainDirectory.Register(outdatedAddr).Returns(outdatedAddr);

            await this.grainLocator.Register(expectedAddr.ToActivationAddress());
            await this.grainLocator.Register(outdatedAddr.ToActivationAddress());

            // Simulate a dead silo
            this.mockMembershipService.UpdateSiloStatus(outdatedAddr.SiloAddress, SiloStatus.Dead, "old");

            // Wait a bit for the update to be processed
            await WaitUntilClusterChangePropagated();

            // Cleanup function from grain directory should have been called
            await this.grainDirectory
                .Received(1)
                .UnregisterSilos(Arg.Is<List<SiloAddress>>(list => list.Count == 1 && list.Contains(outdatedAddr.SiloAddress)));

            // Cache should have been cleaned
            Assert.False(this.grainLocator.TryLookupInCache(outdatedAddr.GrainId, out var unused1));
            Assert.True(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out var unused2));

            var result = await this.grainLocator.Lookup(expectedAddr.GrainId);
            Assert.NotNull(result);
            Assert.Equal(expectedAddr.ToActivationAddress(), result);

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task UnregisterCallDirectoryAndCleanCache()
        {
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            var expectedAddr = GenerateGrainAddress(expectedSilo);

            this.grainDirectory.Register(expectedAddr).Returns(expectedAddr);

            // Register to populate cache
            await this.grainLocator.Register(expectedAddr.ToActivationAddress());

            // Unregister and check if cache was cleaned
            await this.grainLocator.Unregister(expectedAddr.ToActivationAddress(), UnregistrationCause.Force);
            Assert.False(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));
        }

        private GrainAddress GenerateGrainAddress(SiloAddress siloAddress = null)
        {
            return new GrainAddress
            {
                GrainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateGuidKey(Guid.NewGuid())),
                ActivationId = ActivationId.NewId().Key.ToString("N"),
                SiloAddress = siloAddress ?? GenerateSiloAddress(),
                MembershipVersion = this.mockMembershipService.CurrentVersion,
            };
        }

        private int generation = 0;
        private SiloAddress GenerateSiloAddress() => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), ++generation);

        private async Task WaitUntilClusterChangePropagated()
        {
            await Until(() => this.mockMembershipService.CurrentVersion == ((CachedGrainLocator.ITestAccessor)this.grainLocator).LastMembershipVersion);
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

            public IAsyncEnumerable<ClusterManifest> Updates => this.GetUpdates();

            public GrainManifest LocalGrainManifest { get; } = new GrainManifest(ImmutableDictionary<GrainType, GrainProperties>.Empty, ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

            private async IAsyncEnumerable<ClusterManifest> GetUpdates()
            {
                yield return this.Current;
                await Task.Delay(100);
                yield break;
            }
        }
    }
}
