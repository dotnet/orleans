// Ignore Spelling: Locator

using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Hosting;
using TestExtensions;
using Xunit;

namespace UnitTests.Directory
{
    /// <summary>
    /// Tests for the CachedGrainLocator, which is Orleans' primary mechanism for locating grain activations across the cluster.
    /// The locator maintains a local cache of grain locations to minimize directory lookups and improve performance.
    /// It handles registration, lookup, and cleanup of grain activations while maintaining consistency with the distributed directory.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Directory")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("GrainDirectory")]
    public class CachedGrainLocatorTests
    {
        private readonly LoggerFactory loggerFactory;
        private readonly SiloLifecycleSubject lifecycle;
        private readonly IOptions<GrainDirectoryOptions> grainDirectoryOptions;
        private readonly IGrainDirectory grainDirectory;
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly MockClusterMembershipService mockMembershipService;
        private readonly CachedGrainLocator grainLocator;

        public CachedGrainLocatorTests(ITestOutputHelper output)
        {
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(output) });
            this.lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());

            this.grainDirectory = Substitute.For<IGrainDirectory>();
            this.grainDirectory
                .Register(Arg.Any<GrainAddress>(), Arg.Any<GrainAddress?>(), Arg.Any<CancellationToken>())
                .Returns(call => this.grainDirectory.Register(
                    call.ArgAt<GrainAddress>(0),
                    call.ArgAt<GrainAddress?>(1)));
            this.grainDirectory
                .Unregister(Arg.Any<GrainAddress>(), Arg.Any<CancellationToken>())
                .Returns(call => this.grainDirectory.Unregister(call.ArgAt<GrainAddress>(0)));
            var services = new ServiceCollection()
                .AddGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (sp, name) => this.grainDirectory)
                .BuildServiceProvider();

            this.grainDirectoryResolver = new GrainDirectoryResolver(
                services,
                new GrainPropertiesResolver(new NoOpClusterManifestProvider(TestContext.Current.CancellationToken)),
                Array.Empty<IGrainDirectoryResolver>());
            this.mockMembershipService = new MockClusterMembershipService();

            grainDirectoryOptions = Options.Create(new GrainDirectoryOptions());
            this.grainLocator = new CachedGrainLocator(
                services,
                this.grainDirectoryResolver, 
                this.mockMembershipService.Target,
                CreateDirectoryInstruments(),
                grainDirectoryOptions);

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

        /// <summary>
        /// Tests basic registration of a grain activation when no prior registration exists.
        /// Verifies that the activation is registered in the directory and cached locally.
        /// </summary>
        [Fact]
        public async Task RegisterWhenNoOtherEntryExists()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var silo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(silo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expected = GenerateGrainAddress(silo);

            ConfigureLegacyRegister(expected, previousAddress: null, expected);

            var actual = await this.grainLocator.Register(expected, previousAddress: null, cancellationToken);
            Assert.Equal(expected, actual);
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), expected, null));

            // Now should be in cache
            Assert.True(this.grainLocator.TryLookupInCache(expected.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expected, result);
        }

        [Fact]
        public async Task StopDoesNotDisposeRegisteredCustomCache()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var cache = new DisposableGrainDirectoryCache();
            var grainDirectory = Substitute.For<IGrainDirectory>();
            var services = new ServiceCollection()
                .AddSingleton<IGrainDirectoryCache>(cache)
                .AddGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, (sp, name) => grainDirectory)
                .BuildServiceProvider();
            var grainDirectoryResolver = new GrainDirectoryResolver(
                services,
                new GrainPropertiesResolver(new NoOpClusterManifestProvider(cancellationToken)),
                Array.Empty<IGrainDirectoryResolver>());
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            var membershipService = new MockClusterMembershipService();
            var grainLocator = new CachedGrainLocator(
                services,
                grainDirectoryResolver,
                membershipService.Target,
                CreateDirectoryInstruments(),
                Options.Create(new GrainDirectoryOptions()));

            grainLocator.Participate(lifecycle);

            await lifecycle.OnStart(cancellationToken);
            await lifecycle.OnStop(cancellationToken);

            Assert.False(cache.Disposed);
            Assert.False(cache.AsyncDisposed);
        }

        [Fact]
        public async Task LocalGrainDirectoryStopDoesNotDisposeRegisteredCustomCache()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var cache = new DisposableGrainDirectoryCache();
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 123);
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.GatewayAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("localhost");
            localSiloDetails.Name.Returns("TestSilo");
            localSiloDetails.ClusterId.Returns("TestCluster");
            var siloStatusOracle = Substitute.For<ISiloStatusOracle>();
            var membershipService = new MockClusterMembershipService();
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            var services = new ServiceCollection()
                .AddSingleton<IGrainDirectoryCache>(cache)
                .BuildServiceProvider();
            Factory<LocalGrainDirectoryPartition> partitionFactory = () => new LocalGrainDirectoryPartition(
                membershipService.Target,
                Options.Create(new GrainDirectoryOptions()),
                this.loggerFactory);
            var systemTargetShared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: localSiloDetails,
                loggerFactory: this.loggerFactory,
                schedulingOptions: Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activations: new ActivationDirectory(CreateCatalogInstruments()),
                schedulerInstruments: CreateSchedulerInstruments(),
                grainInstruments: CreateGrainInstruments(),
                messagingInstruments: CreateMessagingInstruments(),
                messagingProcessingInstruments: CreateMessagingProcessingInstruments());
            var localGrainDirectory = new LocalGrainDirectory(
                serviceProvider: services,
                siloDetails: localSiloDetails,
                siloStatusOracle: siloStatusOracle,
                clusterMembershipService: membershipService.Target,
                grainFactory: grainFactory,
                grainDirectoryPartitionFactory: partitionFactory,
                developmentClusterMembershipOptions: Options.Create(new DevelopmentClusterMembershipOptions()),
                grainDirectoryOptions: Options.Create(new GrainDirectoryOptions { CachingStrategy = GrainDirectoryOptions.CachingStrategyType.Custom }),
                loggerFactory: this.loggerFactory,
                directoryInstruments: CreateDirectoryInstruments(),
                systemTargetShared: systemTargetShared);

            await localGrainDirectory.StopAsync().WaitAsync(cancellationToken);

            Assert.False(cache.Disposed);
            Assert.False(cache.AsyncDisposed);
            Assert.Equal(1, cache.ClearCount);
        }

        [Fact]
        public async Task LocalGrainDirectoryAppliesNewerMembershipBeforeRegisterForwarding()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var localSilo = GenerateSiloAddress();
            var remoteSilo = GenerateSiloAddress();
            var membershipService = new MockClusterMembershipService(new()
            {
                [localSilo] = (SiloStatus.Active, "local")
            });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.GatewayAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("localhost");
            localSiloDetails.Name.Returns("TestSilo");
            localSiloDetails.ClusterId.Returns("TestCluster");
            var siloStatusOracle = Substitute.For<ISiloStatusOracle>();
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            var remoteDirectory = Substitute.For<IRemoteGrainDirectory>();
            grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceType, remoteSilo).Returns(remoteDirectory);
            var services = new ServiceCollection().BuildServiceProvider();
            Factory<LocalGrainDirectoryPartition> partitionFactory = () => new LocalGrainDirectoryPartition(
                membershipService.Target,
                Options.Create(new GrainDirectoryOptions()),
                this.loggerFactory);
            var systemTargetShared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: localSiloDetails,
                loggerFactory: this.loggerFactory,
                schedulingOptions: Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activations: new ActivationDirectory(CreateCatalogInstruments()),
                schedulerInstruments: CreateSchedulerInstruments(),
                grainInstruments: CreateGrainInstruments(),
                messagingInstruments: CreateMessagingInstruments(),
                messagingProcessingInstruments: CreateMessagingProcessingInstruments());
            var localGrainDirectory = new LocalGrainDirectory(
                serviceProvider: services,
                siloDetails: localSiloDetails,
                siloStatusOracle: siloStatusOracle,
                clusterMembershipService: membershipService.Target,
                grainFactory: grainFactory,
                grainDirectoryPartitionFactory: partitionFactory,
                developmentClusterMembershipOptions: Options.Create(new DevelopmentClusterMembershipOptions()),
                grainDirectoryOptions: Options.Create(new GrainDirectoryOptions()),
                loggerFactory: this.loggerFactory,
                directoryInstruments: CreateDirectoryInstruments(),
                systemTargetShared: systemTargetShared)
            {
                Running = true
            };

            membershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remote");
            var address = GenerateGrainAddressOwnedBy(remoteSilo, localSilo, remoteSilo, membershipService.CurrentVersion);
            remoteDirectory.RegisterAsync(address, null, 1).Returns(Task.FromResult(new AddressAndTag(address, 1)));

            try
            {
                var result = await localGrainDirectory.RegisterAsync(address, hopCount: 0).WaitAsync(cancellationToken);

                Assert.Equal(address, result.Address);
                Assert.Equal(0, membershipService.RefreshCallCount);
                await remoteDirectory.Received(1).RegisterAsync(address, null, 1).WaitAsync(cancellationToken);
                Assert.Null(localGrainDirectory.GetLocalDirectoryData(address.GrainId).Address);
            }
            finally
            {
                await localGrainDirectory.StopAsync().WaitAsync(cancellationToken);
            }
        }

        [Theory]
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        [InlineData(SiloStatus.Dead)]
        public async Task LocalGrainDirectoryAppliesNewerMembershipBeforeLookupForwarding(SiloStatus status)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var localSilo = GenerateSiloAddress();
            var remoteSilo = GenerateSiloAddress();
            var membershipService = new MockClusterMembershipService(new()
            {
                [localSilo] = (SiloStatus.Active, "local"),
                [remoteSilo] = (SiloStatus.Active, "remote")
            });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.GatewayAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("localhost");
            localSiloDetails.Name.Returns("TestSilo");
            localSiloDetails.ClusterId.Returns("TestCluster");
            var siloStatusOracle = Substitute.For<ISiloStatusOracle>();
            siloStatusOracle.IsFunctionalDirectory(Arg.Any<SiloAddress>()).Returns(
                call => membershipService.Target.CurrentSnapshot.GetSiloStatus(call.Arg<SiloAddress>()) == SiloStatus.Active);
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            var remoteDirectory = Substitute.For<IRemoteGrainDirectory>();
            grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceType, remoteSilo).Returns(remoteDirectory);
            var services = new ServiceCollection()
                .AddSingleton<GrainDirectoryResolver>(serviceProvider => new(
                    serviceProvider,
                    new GrainPropertiesResolver(new NoOpClusterManifestProvider(cancellationToken)),
                    Array.Empty<IGrainDirectoryResolver>()))
                .BuildServiceProvider();
            Factory<LocalGrainDirectoryPartition> partitionFactory = () => new LocalGrainDirectoryPartition(
                membershipService.Target,
                Options.Create(new GrainDirectoryOptions()),
                this.loggerFactory);
            var systemTargetShared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: localSiloDetails,
                loggerFactory: this.loggerFactory,
                schedulingOptions: Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activations: new ActivationDirectory(CreateCatalogInstruments()),
                schedulerInstruments: CreateSchedulerInstruments(),
                grainInstruments: CreateGrainInstruments(),
                messagingInstruments: CreateMessagingInstruments(),
                messagingProcessingInstruments: CreateMessagingProcessingInstruments());
            var localGrainDirectory = new LocalGrainDirectory(
                serviceProvider: services,
                siloDetails: localSiloDetails,
                siloStatusOracle: siloStatusOracle,
                clusterMembershipService: membershipService.Target,
                grainFactory: grainFactory,
                grainDirectoryPartitionFactory: partitionFactory,
                developmentClusterMembershipOptions: Options.Create(new DevelopmentClusterMembershipOptions()),
                grainDirectoryOptions: Options.Create(new GrainDirectoryOptions()),
                loggerFactory: this.loggerFactory,
                directoryInstruments: CreateDirectoryInstruments(),
                systemTargetShared: systemTargetShared)
            {
                Running = true
            };
            var address = GenerateGrainAddressOwnedBy(remoteSilo, localSilo, remoteSilo, membershipService.CurrentVersion);
            remoteDirectory.RegisterAsync(address, null, 1).Returns(Task.FromResult(new AddressAndTag(address, 1)));

            try
            {
                await localGrainDirectory.RegisterAsync(address, hopCount: 0).WaitAsync(cancellationToken);
                membershipService.UpdateSiloStatus(remoteSilo, status, "remote");

                var result = await localGrainDirectory.LookupAsync(address.GrainId).WaitAsync(cancellationToken);

                Assert.Null(result.Address);
                Assert.Equal(0, membershipService.RefreshCallCount);
                await remoteDirectory.DidNotReceive().LookupAsync(address.GrainId, Arg.Any<int>()).WaitAsync(cancellationToken);
            }
            finally
            {
                await localGrainDirectory.StopAsync().WaitAsync(cancellationToken);
            }
        }

        [Fact]
        public async Task LocalGrainDirectoryTryLocalLookupFindsLocalPartitionEntryOnCacheMiss()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var localSilo = GenerateSiloAddress();
            var membershipService = new MockClusterMembershipService(new()
            {
                [localSilo] = (SiloStatus.Active, "local")
            });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.GatewayAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("localhost");
            localSiloDetails.Name.Returns("TestSilo");
            localSiloDetails.ClusterId.Returns("TestCluster");
            var siloStatusOracle = Substitute.For<ISiloStatusOracle>();
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            var services = new ServiceCollection().BuildServiceProvider();
            Factory<LocalGrainDirectoryPartition> partitionFactory = () => new LocalGrainDirectoryPartition(
                membershipService.Target,
                Options.Create(new GrainDirectoryOptions()),
                this.loggerFactory);
            var systemTargetShared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: localSiloDetails,
                loggerFactory: this.loggerFactory,
                schedulingOptions: Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activations: new ActivationDirectory(CreateCatalogInstruments()),
                schedulerInstruments: CreateSchedulerInstruments(),
                grainInstruments: CreateGrainInstruments(),
                messagingInstruments: CreateMessagingInstruments(),
                messagingProcessingInstruments: CreateMessagingProcessingInstruments());
            var localGrainDirectory = new LocalGrainDirectory(
                serviceProvider: services,
                siloDetails: localSiloDetails,
                siloStatusOracle: siloStatusOracle,
                clusterMembershipService: membershipService.Target,
                grainFactory: grainFactory,
                grainDirectoryPartitionFactory: partitionFactory,
                developmentClusterMembershipOptions: Options.Create(new DevelopmentClusterMembershipOptions()),
                grainDirectoryOptions: Options.Create(new GrainDirectoryOptions()),
                loggerFactory: this.loggerFactory,
                directoryInstruments: CreateDirectoryInstruments(),
                systemTargetShared: systemTargetShared)
            {
                Running = true
            };

            var address = GenerateGrainAddress(localSilo, membershipService.CurrentVersion);

            try
            {
                var registered = await localGrainDirectory.RegisterAsync(address, hopCount: 0).WaitAsync(cancellationToken);
                Assert.Equal(address, registered.Address);

                localGrainDirectory.InvalidateCacheEntry(address.GrainId);
                Assert.False(localGrainDirectory.TryCachedLookup(address.GrainId, out _));

                Assert.True(localGrainDirectory.TryLocalLookup(address.GrainId, out var result));
                Assert.Equal(address, result);
            }
            finally
            {
                await localGrainDirectory.StopAsync().WaitAsync(cancellationToken);
            }
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExists()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();
            var otherSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var otherAddr = GenerateGrainAddress(otherSilo);

            ConfigureLegacyRegister(otherAddr, previousAddress: null, expectedAddr);

            var actual = await this.grainLocator.Register(otherAddr, previousAddress: null, cancellationToken);
            Assert.Equal(expectedAddr, actual);
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), otherAddr, null));

            // Now should be in cache
            Assert.True(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expectedAddr, result);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExistsAndPreviousAddressMatches()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var silo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(silo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var existing = GenerateGrainAddress(silo);
            var replacement = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                SiloAddress = GenerateSiloAddress(),
                GrainId = existing.GrainId,
                MembershipVersion = existing.MembershipVersion
            };

            ConfigureLegacyRegister(replacement, previousAddress: null, existing);

            var actual = await this.grainLocator.Register(replacement, previousAddress: null, cancellationToken);
            Assert.Equal(existing, actual);
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), replacement, null));
            this.grainDirectory.ClearReceivedCalls();

            ConfigureLegacyRegister(replacement, previousAddress: existing, replacement);
            actual = await this.grainLocator.Register(replacement, previousAddress: existing, cancellationToken);
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), replacement, existing));
            Assert.Equal(replacement, actual);

            // Now should be in cache
            Assert.True(this.grainLocator.TryLookupInCache(replacement.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(replacement, result);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExistsAndPreviousAddressDoesNotMatch()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var silo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(silo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var existing = GenerateGrainAddress(silo);
            var nonMatching = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                SiloAddress = GenerateSiloAddress(),
                GrainId = existing.GrainId,
                MembershipVersion = existing.MembershipVersion
            };
            var replacement = new GrainAddress
            {
                ActivationId = ActivationId.NewId(),
                SiloAddress = GenerateSiloAddress(),
                GrainId = existing.GrainId,
                MembershipVersion = existing.MembershipVersion
            };

            ConfigureLegacyRegister(replacement, previousAddress: null, existing);

            var actual = await this.grainLocator.Register(replacement, previousAddress: null, cancellationToken);
            Assert.Equal(existing, actual);
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), replacement, null));
            this.grainDirectory.ClearReceivedCalls();

            ConfigureLegacyRegister(replacement, previousAddress: nonMatching, existing);
            actual = await this.grainLocator.Register(replacement, previousAddress: nonMatching, cancellationToken);
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), replacement, nonMatching));
            Assert.Equal(existing, actual);

            // Cache should contain original address
            Assert.True(this.grainLocator.TryLookupInCache(replacement.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(existing, result);
        }

        [Fact]
        public async Task RegisterWhenOtherEntryExistsButSiloIsDead()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            ConfigureLegacyRegister(expectedAddr, previousAddress: null, outdatedAddr);
            ConfigureLegacyRegister(expectedAddr, previousAddress: outdatedAddr, expectedAddr);

            var actual = await this.grainLocator.Register(expectedAddr, previousAddress: null, cancellationToken);
            Assert.Equal(expectedAddr, actual);
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), expectedAddr, null));
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Register), expectedAddr, outdatedAddr));
            Assert.Equal(0, GetLegacyCallCount(nameof(IGrainDirectory.Unregister), outdatedAddr));

            // Now should be in cache
            Assert.True(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out var result));
            Assert.NotNull(result);
            Assert.Equal(expectedAddr, result);

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task LookupPopulateTheCache()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var grainAddress = GenerateGrainAddress(expectedSilo);

            ConfigureLegacyLookup(grainAddress.GrainId, grainAddress);

            // Cache should be empty
            Assert.False(this.grainLocator.TryLookupInCache(grainAddress.GrainId, out _));

            // Do a remote lookup
            var result = await this.grainLocator.Lookup(grainAddress.GrainId).AsTask().WaitAsync(cancellationToken);
            Assert.NotNull(result);
            Assert.Equal(grainAddress, result);

            // Now cache should be populated
            Assert.True(this.grainLocator.TryLookupInCache(grainAddress.GrainId, out var cachedValue));
            Assert.NotNull(cachedValue);
            Assert.Equal(grainAddress, cachedValue);
        }

        [Fact]
        public async Task LookupWhenEntryExistsButSiloIsDead()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            ConfigureLegacyLookup(outdatedAddr.GrainId, outdatedAddr);

            var actual = await this.grainLocator.Lookup(outdatedAddr.GrainId).AsTask().WaitAsync(cancellationToken);
            Assert.Null(actual);

            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Lookup), outdatedAddr.GrainId));
            Assert.Equal(1, GetLegacyCallCount(nameof(IGrainDirectory.Unregister), outdatedAddr));
            Assert.False(this.grainLocator.TryLookupInCache(outdatedAddr.GrainId, out _));

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task LocalLookupWhenEntryExistsButSiloIsDead()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            ConfigureLegacyLookup(outdatedAddr.GrainId, outdatedAddr);
            Assert.False(this.grainLocator.TryLookupInCache(outdatedAddr.GrainId, out _));

            // Local lookup should never call the directory
            Assert.Equal(0, GetLegacyCallCount(nameof(IGrainDirectory.Lookup), outdatedAddr.GrainId));
            Assert.Equal(0, GetLegacyCallCount(nameof(IGrainDirectory.Unregister), outdatedAddr));

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Theory]
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        public async Task LocalLookupWhenCachedEntrySiloIsTerminatingButNotDead(SiloStatus status)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var silo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(silo, SiloStatus.Active, "silo");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var address = GenerateGrainAddress(silo);
            ConfigureLegacyRegister(address, previousAddress: null, address);

            await this.grainLocator.Register(address, previousAddress: null, cancellationToken);
            Assert.True(this.grainLocator.TryLookupInCache(address.GrainId, out var cached));
            Assert.Equal(address, cached);

            this.mockMembershipService.UpdateSiloStatus(silo, status, "silo");
            await WaitUntilClusterChangePropagated(cancellationToken);

            Assert.True(this.grainLocator.TryLookupInCache(address.GrainId, out cached));
            Assert.Equal(address, cached);
            Assert.Equal(0, GetLegacyUnregisterSilosCallCount(_ => true));

            await this.lifecycle.OnStop(cancellationToken);
        }

        /// <summary>
        /// Tests that the locator properly cleans up cached entries when a silo dies.
        /// This is critical for preventing requests from being sent to dead silos.
        /// </summary>
        [Fact]
        public async Task CleanupWhenSiloIsDead()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();
            var outdatedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Active, "old");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var outdatedAddr = GenerateGrainAddress(outdatedSilo);

            // Register two entries
            ConfigureLegacyRegister(expectedAddr, previousAddress: null, expectedAddr);
            ConfigureLegacyRegister(outdatedAddr, previousAddress: null, outdatedAddr);

            await this.grainLocator.Register(expectedAddr, previousAddress: null, cancellationToken);
            await this.grainLocator.Register(outdatedAddr, previousAddress: null, cancellationToken);

            // Simulate a dead silo
            this.mockMembershipService.UpdateSiloStatus(outdatedAddr.SiloAddress!, SiloStatus.Dead, "old");

            // Wait a bit for the update to be processed
            await WaitUntilClusterChangePropagated(cancellationToken);

            // Cleanup function from grain directory should have been called
            Assert.Equal(1, GetLegacyUnregisterSilosCallCount(list => list.Count == 1 && list.Contains(outdatedAddr.SiloAddress!)));

            // Cache should have been cleaned
            Assert.False(this.grainLocator.TryLookupInCache(outdatedAddr.GrainId, out var unused1));
            Assert.True(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out var unused2));

            var result = await this.grainLocator.Lookup(expectedAddr.GrainId).AsTask().WaitAsync(cancellationToken);
            Assert.NotNull(result);
            Assert.Equal(expectedAddr, result);

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task CleanupWhenSiloIsDeadOnlyProcessesIncrementalChanges()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();
            var outdatedSilo = GenerateSiloAddress();

            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Active, "old");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            this.mockMembershipService.UpdateSiloStatus(outdatedSilo, SiloStatus.Dead, "old");
            await WaitUntilClusterChangePropagated(cancellationToken);

            Assert.Equal(1, GetLegacyUnregisterSilosCallCount(list => list.Count == 1 && list.Contains(outdatedSilo)));

            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp2");
            await WaitUntilClusterChangePropagated(cancellationToken);

            Assert.Equal(1, GetLegacyUnregisterSilosCallCount(list => list.Count == 1 && list.Contains(outdatedSilo)));

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task UpdateCacheStampsCurrentMembershipVersion()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await this.lifecycle.OnStart(cancellationToken);

            var grainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateGuidKey(Guid.NewGuid()));
            var silo = GenerateSiloAddress();

            this.grainLocator.UpdateCache(grainId, silo);

            Assert.True(this.grainLocator.TryLookupInCache(grainId, out var cached));
            Assert.Equal(silo, cached.SiloAddress);
            Assert.Equal(this.mockMembershipService.CurrentVersion, cached.MembershipVersion);

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task UnregisterCallDirectoryAndCleanCache()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expectedAddr = GenerateGrainAddress(expectedSilo);

            ConfigureLegacyRegister(expectedAddr, previousAddress: null, expectedAddr);

            // Register to populate cache
            await this.grainLocator.Register(expectedAddr, previousAddress: null, cancellationToken);

            // Unregister and check if cache was cleaned
            await this.grainLocator.Unregister(expectedAddr, UnregistrationCause.Force).WaitAsync(cancellationToken);
            Assert.False(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));
        }

        [Fact]
        public async Task UnregisterRemovesFromCacheFirst()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expectedAddr = GenerateGrainAddress(expectedSilo);

            // Do not complete until the test is cancelled.
            var unregisterCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(() => unregisterCompletion.TrySetCanceled(cancellationToken));
            ConfigureLegacyUnregister(expectedAddr, unregisterCompletion.Task);

            ConfigureLegacyRegister(expectedAddr, previousAddress: null, expectedAddr);

            // Register to populate cache
            await this.grainLocator.Register(expectedAddr, previousAddress: null, cancellationToken);

            // Unregister and check if cache was cleaned
            var unregisterTask = this.grainLocator.Unregister(expectedAddr, UnregistrationCause.Force);
            Assert.False(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));

            unregisterCompletion.TrySetResult();
            await unregisterTask.WaitAsync(cancellationToken);
        }

        [Fact]
        public async Task UnregisterRacesWithLookupSameId()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expectedAddr = GenerateGrainAddress(expectedSilo);

            var unregisterCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(() => unregisterCompletion.TrySetCanceled(cancellationToken));
            ConfigureLegacyUnregister(expectedAddr, unregisterCompletion.Task);

            ConfigureLegacyRegister(expectedAddr, previousAddress: null, expectedAddr);

            // Register to populate cache
            await this.grainLocator.Register(expectedAddr, previousAddress: null, cancellationToken);

            // Unregister and check if cache was cleaned
            Task t = this.grainLocator.Unregister(expectedAddr, UnregistrationCause.Force);
            Assert.False(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));

            // Add back to cache simulating a race from lookup
            await this.grainLocator.Register(expectedAddr, previousAddress: null, cancellationToken);
            Assert.True(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));

            // Ensure when Unregister finishes if the race occured on the same id that it was removed
            unregisterCompletion.TrySetResult();
            await t.WaitAsync(cancellationToken);
            Assert.False(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));
        }

        [Fact]
        public async Task UnregisterRacesWithLookupDifferentId()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var expectedSilo = GenerateSiloAddress();
            var secondSilo = GenerateSiloAddress();

            // Setup membership service
            this.mockMembershipService.UpdateSiloStatus(expectedSilo, SiloStatus.Active, "exp");
            this.mockMembershipService.UpdateSiloStatus(secondSilo, SiloStatus.Active, "exp");
            await this.lifecycle.OnStart(cancellationToken);
            await WaitUntilClusterChangePropagated(cancellationToken);

            var expectedAddr = GenerateGrainAddress(expectedSilo);
            var secondAddr = GenerateGrainAddress(secondSilo);

            var unregisterCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(() => unregisterCompletion.TrySetCanceled(cancellationToken));
            ConfigureLegacyUnregister(expectedAddr, unregisterCompletion.Task);

            ConfigureLegacyRegister(expectedAddr, previousAddress: null, expectedAddr);
            ConfigureLegacyRegister(secondAddr, previousAddress: null, secondAddr);

            // Register to populate cache
            await this.grainLocator.Register(expectedAddr, previousAddress: null, cancellationToken);

            // Unregister and check if cache was cleaned
            Task t = this.grainLocator.Unregister(expectedAddr, UnregistrationCause.Force);
            Assert.False(this.grainLocator.TryLookupInCache(expectedAddr.GrainId, out _));

            // Add back to cache simulating a race from lookup
            await this.grainLocator.Register(secondAddr, previousAddress: null, cancellationToken);
            Assert.True(this.grainLocator.TryLookupInCache(secondAddr.GrainId, out _));

            // Ensure when Unregister finishes if the race occured on the same id that it was removed
            unregisterCompletion.TrySetResult();
            await t.WaitAsync(cancellationToken);
            Assert.True(this.grainLocator.TryLookupInCache(secondAddr.GrainId, out _));
        }

        private GrainAddress GenerateGrainAddress(SiloAddress? siloAddress = null, MembershipVersion? membershipVersion = null)
        {
            return new GrainAddress
            {
                GrainId = GrainId.Create(GrainType.Create("test"), GrainIdKeyExtensions.CreateGuidKey(Guid.NewGuid())),
                ActivationId = ActivationId.NewId(),
                SiloAddress = siloAddress ?? GenerateSiloAddress(),
                MembershipVersion = membershipVersion ?? this.mockMembershipService.CurrentVersion,
            };
        }

        private GrainAddress GenerateGrainAddressOwnedBy(SiloAddress owner, SiloAddress localSilo, SiloAddress remoteSilo, MembershipVersion membershipVersion)
        {
            for (var i = 0; i < 1000; i++)
            {
                var candidate = GenerateGrainAddress(localSilo, membershipVersion);
                if (CalculateDirectoryOwner(candidate.GrainId, localSilo, remoteSilo).Equals(owner))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException($"Unable to generate a grain id owned by {owner}.");
        }

        private static SiloAddress CalculateDirectoryOwner(GrainId grainId, params SiloAddress[] silos)
        {
            Array.Sort(silos, static (left, right) =>
            {
                var hashComparison = left.GetConsistentHashCode().CompareTo(right.GetConsistentHashCode());
                return hashComparison != 0 ? hashComparison : left.CompareTo(right);
            });

            var hash = unchecked((int)grainId.GetUniformHashCode());
            for (var i = silos.Length - 1; i >= 0; --i)
            {
                if (silos[i].GetConsistentHashCode() <= hash)
                {
                    return silos[i];
                }
            }

            return silos[^1];
        }

        private static SchedulerInstruments CreateSchedulerInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<SchedulerInstruments>();
            return services.BuildServiceProvider().GetRequiredService<SchedulerInstruments>();
        }

        private static CatalogInstruments CreateCatalogInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<CatalogInstruments>();
            return services.BuildServiceProvider().GetRequiredService<CatalogInstruments>();
        }

        private static DirectoryInstruments CreateDirectoryInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<DirectoryInstruments>();
            return services.BuildServiceProvider().GetRequiredService<DirectoryInstruments>();
        }

        private static GrainInstruments CreateGrainInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<GrainInstruments>();
            return services.BuildServiceProvider().GetRequiredService<GrainInstruments>();
        }

        private static MessagingInstruments CreateMessagingInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<MessagingInstruments>();
            return services.BuildServiceProvider().GetRequiredService<MessagingInstruments>();
        }

        private static MessagingProcessingInstruments CreateMessagingProcessingInstruments()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<MessagingProcessingInstruments>();
            return services.BuildServiceProvider().GetRequiredService<MessagingProcessingInstruments>();
        }

        private int generation = 0;
        private SiloAddress GenerateSiloAddress() => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), ++generation);

        private void ConfigureLegacyLookup(GrainId grainId, GrainAddress result) =>
            this.grainDirectory.Lookup(grainId).Returns(result);

        private void ConfigureLegacyRegister(GrainAddress address, GrainAddress? previousAddress, GrainAddress result) =>
            this.grainDirectory.Register(address, previousAddress).Returns(result);

        private void ConfigureLegacyUnregister(GrainAddress address, Task result) =>
            this.grainDirectory.Unregister(address).Returns(result);

        private int GetLegacyCallCount(string methodName, params object?[] arguments) =>
            this.grainDirectory.ReceivedCalls().Count(
                call => call.GetMethodInfo().Name == methodName && call.GetArguments().SequenceEqual(arguments));

        private int GetLegacyUnregisterSilosCallCount(Func<List<SiloAddress>, bool> predicate) =>
            this.grainDirectory.ReceivedCalls().Count(
                call => call.GetMethodInfo().Name == nameof(IGrainDirectory.UnregisterSilos)
                    && call.GetArguments() is [List<SiloAddress> siloAddresses]
                    && predicate(siloAddresses));

        private async Task WaitUntilClusterChangePropagated(CancellationToken cancellationToken)
        {
            await Until(
                () => this.mockMembershipService.CurrentVersion == ((CachedGrainLocator.ITestAccessor)this.grainLocator).LastMembershipVersion,
                cancellationToken);
        }

        private static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
        {
            var maxTimeout = 40_000;
            while (!condition() && (maxTimeout -= 10) > 0)
            {
                await Task.Delay(10, cancellationToken);
            }

            Assert.True(maxTimeout > 0);
        }

        private class NoOpClusterManifestProvider : IClusterManifestProvider
        {
            private readonly CancellationToken cancellationToken;

            public NoOpClusterManifestProvider(CancellationToken cancellationToken)
            {
                this.cancellationToken = cancellationToken;
            }

            public ClusterManifest Current => new ClusterManifest(
                MajorMinorVersion.Zero,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty);

            public IAsyncEnumerable<ClusterManifest> Updates => this.GetUpdates();

            public GrainManifest LocalGrainManifest { get; } = new GrainManifest(ImmutableDictionary<GrainType, GrainProperties>.Empty, ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);

            private async IAsyncEnumerable<ClusterManifest> GetUpdates()
            {
                yield return this.Current;
                await Task.Delay(100, this.cancellationToken);
                yield break;
            }
        }

        private sealed class DisposableGrainDirectoryCache : IGrainDirectoryCache, IDisposable, IAsyncDisposable
        {
            private readonly Dictionary<GrainId, (GrainAddress Address, int Version)> entries = new();

            public bool Disposed { get; private set; }

            public bool AsyncDisposed { get; private set; }

            public int ClearCount { get; private set; }

            public IEnumerable<(GrainAddress ActivationAddress, int Version)> KeyValues => this.entries.Values.Select(entry => (entry.Address, entry.Version));

            public void AddOrUpdate(GrainAddress value, int version) => this.entries[value.GrainId] = (value, version);

            public bool Remove(GrainId key) => this.entries.Remove(key);

            public bool Remove(GrainAddress key) => this.entries.Remove(key.GrainId);

            public void Clear()
            {
                ++this.ClearCount;
                this.entries.Clear();
            }

            public bool LookUp(GrainId key, out GrainAddress result, out int version)
            {
                if (this.entries.TryGetValue(key, out var entry))
                {
                    result = entry.Address;
                    version = entry.Version;
                    return true;
                }

                result = default!;
                version = default;
                return false;
            }

            public void Dispose() => this.Disposed = true;

            public ValueTask DisposeAsync()
            {
                this.AsyncDisposed = true;
                return default;
            }
        }
    }
}
