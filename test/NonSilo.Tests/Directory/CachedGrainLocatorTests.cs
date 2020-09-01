using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Orleans;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;
using TestExtensions;
using UnitTests.SchedulerTests;
using UnitTests.TesterInternal;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
    public class CachedGrainLocatorTests : IDisposable
    {
        private readonly LoggerFactory loggerFactory;
        private readonly SiloLifecycleSubject lifecycle;

        private readonly IGrainDirectory grainDirectory;
        private readonly IGrainDirectoryResolver grainDirectoryResolver;
        private readonly ILocalGrainDirectory localGrainDirectory;
        private readonly MockClusterMembershipService mockMembershipService;
        private readonly UnitTestSchedulingContext rootContext;
        private readonly OrleansTaskScheduler taskScheduler;
        private readonly CachedGrainLocator grainLocator;

        public CachedGrainLocatorTests(ITestOutputHelper output)
        {
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(output) });
            this.lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());

            this.grainDirectory = Substitute.For<IGrainDirectory>();
            this.grainDirectoryResolver = Substitute.For<IGrainDirectoryResolver>();
            this.grainDirectoryResolver.Resolve(Arg.Any<GrainId>()).Returns(this.grainDirectory);
            this.grainDirectoryResolver.Directories.Returns(new[] { this.grainDirectory });
            this.localGrainDirectory = Substitute.For<ILocalGrainDirectory>();
            this.mockMembershipService = new MockClusterMembershipService();
            this.rootContext = new UnitTestSchedulingContext();
            this.taskScheduler = TestInternalHelper.InitializeSchedulerForTesting(this.rootContext, this.loggerFactory);

            this.grainLocator = new CachedGrainLocator(
                this.grainDirectoryResolver, 
                new DhtGrainLocator(this.localGrainDirectory, this.taskScheduler, this.rootContext),
                this.mockMembershipService.Target);

            this.grainLocator.Participate(this.lifecycle);
        }

        [Fact]
        public void ConvertActivationAddressToGrainAddress()
        {
            var expected = GenerateActivationAddress();
            var grainAddress = expected.ToGrainAddress();
            Assert.Equal(expected, grainAddress.ToActivationAddress());
        }

        [Fact]
        public async Task RegisterWhenNoOtherEntryExists()
        {
            var expected = GenerateActivationAddress();
            var grainAddress = expected.ToGrainAddress();

            this.grainDirectory.Register(grainAddress).Returns(grainAddress);

            var actual = await this.grainLocator.Register(expected);
            Assert.Equal(expected, actual);
            await this.grainDirectory.Received(1).Register(grainAddress);

            // Now should be in cache
            Assert.True(this.grainLocator.TryLocalLookup(expected.Grain, out var results));
            Assert.Single(results);
            Assert.Equal(expected, results[0]);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExists()
        {
            var expectedAddr = GenerateActivationAddress();
            var expectedGrainAddr = expectedAddr.ToGrainAddress();
            var otherAddr = GenerateActivationAddress();
            var otherGrainAddr = otherAddr.ToGrainAddress();

            this.grainDirectory.Register(otherGrainAddr).Returns(expectedGrainAddr);

            var actual = await this.grainLocator.Register(otherAddr);
            Assert.Equal(expectedAddr, actual);
            await this.grainDirectory.Received(1).Register(otherGrainAddr);

            // Now should be in cache
            Assert.True(this.grainLocator.TryLocalLookup(expectedAddr.Grain, out var results));
            Assert.Single(results);
            Assert.Equal(expectedAddr, results[0]);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExistsButSiloIsDead()
        {
            var expectedAddr = GenerateActivationAddress();
            var expectedGrainAddr = expectedAddr.ToGrainAddress();
            var outdatedAddr = GenerateActivationAddress();
            var outdatedGrainAddr = outdatedAddr.ToGrainAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedAddr.Silo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(outdatedAddr.Silo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            // First returns the outdated entry, then the new one
            this.grainDirectory.Register(expectedGrainAddr).Returns(outdatedGrainAddr, expectedGrainAddr);

            var actual = await this.grainLocator.Register(expectedAddr);
            Assert.Equal(expectedAddr, actual);
            await this.grainDirectory.Received(2).Register(expectedGrainAddr);
            await this.grainDirectory.Received(1).Unregister(outdatedGrainAddr);

            // Now should be in cache
            Assert.True(this.grainLocator.TryLocalLookup(expectedAddr.Grain, out var results));
            Assert.Single(results);
            Assert.Equal(expectedAddr, results[0]);

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task LookupPopulateTheCache()
        {
            var expected = GenerateActivationAddress();
            var grainAddress = expected.ToGrainAddress();

            this.grainDirectory.Lookup(grainAddress.GrainId).Returns(grainAddress);

            // Cache should be empty
            Assert.False(this.grainLocator.TryLocalLookup(expected.Grain, out _));

            // Do a remote lookup
            var results = await this.grainLocator.Lookup(expected.Grain);
            Assert.Single(results);
            Assert.Equal(expected, results[0]);

            // Now cache should be populated
            Assert.True(this.grainLocator.TryLocalLookup(expected.Grain, out var cachedValues));
            Assert.Single(cachedValues);
            Assert.Equal(expected, cachedValues[0]);
        }

        [Fact]
        public async Task LookupWhenEntryExistsButSiloIsDead()
        {
            var outdatedAddr = GenerateActivationAddress();
            var outdatedGrainAddr = outdatedAddr.ToGrainAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(outdatedAddr.Silo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            this.grainDirectory.Lookup(outdatedGrainAddr.GrainId).Returns(outdatedGrainAddr);

            var actual = await this.grainLocator.Lookup(outdatedAddr.Grain);
            Assert.Empty(actual);

            await this.grainDirectory.Received(1).Lookup(outdatedGrainAddr.GrainId);
            await this.grainDirectory.Received(1).Unregister(outdatedGrainAddr);
            Assert.False(this.grainLocator.TryLocalLookup(outdatedAddr.Grain, out _));

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task LocalLookupWhenEntryExistsButSiloIsDead()
        {
            var outdatedAddr = GenerateActivationAddress();
            var outdatedGrainAddr = outdatedAddr.ToGrainAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(outdatedAddr.Silo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            this.grainDirectory.Lookup(outdatedGrainAddr.GrainId).Returns(outdatedGrainAddr);
            Assert.False(this.grainLocator.TryLocalLookup(outdatedAddr.Grain, out _));

            // Local lookup should never call the directory
            await this.grainDirectory.DidNotReceive().Lookup(outdatedGrainAddr.GrainId);
            await this.grainDirectory.DidNotReceive().Unregister(outdatedGrainAddr);

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task CleanupWhenSiloIsDead()
        {
            var expectedAddr = GenerateActivationAddress();
            var expectedGrainAddr = expectedAddr.ToGrainAddress();
            var outdatedAddr = GenerateActivationAddress();
            var outdatedGrainAddr = outdatedAddr.ToGrainAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedAddr.Silo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(outdatedAddr.Silo, SiloStatus.Active, "old");
            await this.lifecycle.OnStart();
            await WaitUntilClusterChangePropagated();

            // Register two entries
            this.grainDirectory.Register(expectedGrainAddr).Returns(expectedGrainAddr);
            this.grainDirectory.Register(outdatedGrainAddr).Returns(outdatedGrainAddr);

            await this.grainLocator.Register(expectedAddr);
            await this.grainLocator.Register(outdatedAddr);

            // Simulate a dead silo
            this.mockMembershipService.UpdateSiloStatus(outdatedAddr.Silo, SiloStatus.Dead, "old");

            // Wait a bit for the update to be processed
            await WaitUntilClusterChangePropagated();

            // Cleanup function from grain directory should have been called
            await this.grainDirectory
                .Received(1)
                .UnregisterSilos(Arg.Is<List<string>>(list => list.Count == 1 && list.Contains(outdatedGrainAddr.SiloAddress)));

            // Cache should have been cleaned
            Assert.False(this.grainLocator.TryLocalLookup(outdatedAddr.Grain, out var unused1));
            Assert.True(this.grainLocator.TryLocalLookup(expectedAddr.Grain, out var unused2));

            var results = await this.grainLocator.Lookup(expectedAddr.Grain);
            Assert.Single(results);
            Assert.Equal(expectedAddr, results[0]);

            await this.lifecycle.OnStop();
        }

        [Fact]
        public async Task UnregisterCallDirectoryAndCleanCache()
        {
            var expectedAddr = GenerateActivationAddress();
            var expectedGrainAddr = expectedAddr.ToGrainAddress();

            this.grainDirectory.Register(expectedGrainAddr).Returns(expectedGrainAddr);

            // Register to populate cache
            await this.grainLocator.Register(expectedAddr);

            // Unregister and check if cache was cleaned
            await this.grainLocator.Unregister(expectedAddr, UnregistrationCause.Force);
            Assert.False(this.grainLocator.TryLocalLookup(expectedAddr.Grain, out _));
        }


        private int generation = 0;
        private ActivationAddress GenerateActivationAddress()
        {
            var grainId = GrainId.GetGrainIdForTesting(Guid.NewGuid());
            var siloAddr = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), ++generation);

            return ActivationAddress.NewActivationAddress(siloAddr, grainId);
        }

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

        public void Dispose()
        {
            if (this.taskScheduler != null)
            {
                this.taskScheduler.Stop();
            }
        }
    }
}
