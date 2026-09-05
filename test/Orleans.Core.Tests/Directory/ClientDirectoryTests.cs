using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;
using UnitTests.Directory;
using Xunit;

namespace NonSilo.Tests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("GrainDirectory")]
    public class ClientDirectoryTests
    {
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly SiloAddress _localSilo;
        private readonly IOptions<SiloMessagingOptions> _messagingOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly SiloLifecycleSubject _lifecycle;
        private readonly List<DelegateAsyncTimer> _timers;
        private readonly Channel<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)> _timerCalls;
        private readonly DelegateAsyncTimerFactory _timerFactory;
        private readonly MockClusterMembershipService _clusterMembershipService;
        private readonly IInternalGrainFactory _grainFactory;
        private readonly ClientDirectory _directory;
        private readonly ClientDirectory.TestAccessor _testAccessor;
        private readonly IConnectedClientCollection _connectedClientCollection;
        private readonly ConcurrentDictionary<SiloAddress, IRemoteClientDirectory> _remoteDirectories = new ConcurrentDictionary<SiloAddress, IRemoteClientDirectory>();
        private long _expectedConnectedClientsVersion;

        public ClientDirectoryTests()
        {
            _connectedClientCollection = Substitute.For<IConnectedClientCollection>();
            _connectedClientCollection.GetConnectedClientIds().ReturnsForAnyArgs(_ => new List<GrainId>());

            _localSiloDetails = Substitute.For<ILocalSiloDetails>();
            _localSilo = Silo("127.0.0.1:100@100");
            _localSiloDetails.SiloAddress.Returns(_localSilo);
            _localSiloDetails.DnsHostName.Returns("MyServer11");
            _localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            _messagingOptions = Options.Create(new SiloMessagingOptions());
            _loggerFactory = NullLoggerFactory.Instance;
            _lifecycle = new SiloLifecycleSubject(_loggerFactory.CreateLogger<SiloLifecycleSubject>());
            _timers = new List<DelegateAsyncTimer>();
            _timerCalls = Channel.CreateUnbounded<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>();
            _timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var t = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var task = new TaskCompletionSource<bool>();
                            _timerCalls.Writer.TryWrite((overridePeriod, task));
                            return task.Task;
                        });
                    _timers.Add(t);
                    return t;
                });

            _clusterMembershipService = new MockClusterMembershipService();
            _clusterMembershipService.UpdateSiloStatus(_localSilo, SiloStatus.Active, "local-silo");

            _grainFactory = Substitute.For<IInternalGrainFactory>();
            _grainFactory.GetSystemTarget<IRemoteClientDirectory>(default, default!)
                .ReturnsForAnyArgs(info => _remoteDirectories.GetOrAdd(info.ArgAt<SiloAddress>(1), k => Substitute.For<IRemoteClientDirectory>()));
            var systemTargetShared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: _localSiloDetails,
                loggerFactory: _loggerFactory,
                schedulingOptions: Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activations: new ActivationDirectory(CreateCatalogInstruments()),
                schedulerInstruments: CreateSchedulerInstruments(),
                grainInstruments: CreateGrainInstruments(),
                messagingInstruments: CreateMessagingInstruments(),
                messagingProcessingInstruments: CreateMessagingProcessingInstruments());

            _directory = new ClientDirectory(
                grainFactory: _grainFactory,
                siloDetails: _localSiloDetails,
                messagingOptions: _messagingOptions,
                loggerFactory: _loggerFactory,
                clusterMembershipService: _clusterMembershipService,
                timerFactory: _timerFactory,
                connectedClients: _connectedClientCollection,
                timeProvider: TimeProvider.System,
                shared: systemTargetShared);
            _testAccessor = new ClientDirectory.TestAccessor(_directory);

            // Disable automatic publishing to simplify testing.
            _testAccessor.SchedulePublishUpdate = () => { };
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

        /// <summary>
        /// Tests for the basic functionality of <see cref="ClientDirectory.TryLocalLookup(GrainId, out List{ActivationAddress})>)"/>
        /// </summary>
        [Fact]
        public void TryLocalLookupTests()
        {
            // Unknown clients don't exist locally.
            var fakeClientId = Client("mr. snrub");
            Assert.False(_directory.TryLocalLookup(fakeClientId, out var lookupResult));
            Assert.Null(lookupResult);

            var hostedClientId = HostedClient.CreateHostedClientGrainId(_localSilo).GrainId;
            Assert.True(_directory.TryLocalLookup(hostedClientId, out lookupResult));
            Assert.NotNull(lookupResult);
            var singleResult = Assert.Single(lookupResult);
            Assert.Equal(Gateway.GetClientActivationAddress(hostedClientId, _localSilo), singleResult);

            // Add the client and check that it's added successfully.
            var clientsVersion = SetLocalClients(new List<GrainId> { fakeClientId });
            Assert.True(_directory.TryLocalLookup(fakeClientId, out lookupResult));
            Assert.NotNull(lookupResult);
            singleResult = Assert.Single(lookupResult);
            Assert.Equal(Gateway.GetClientActivationAddress(fakeClientId, _localSilo), singleResult);
            Assert.Equal(clientsVersion, _testAccessor.ObservedConnectedClientsVersion);

            // Remove the client and check that it's no longer found.
            clientsVersion = SetLocalClients(new List<GrainId>(0));
            Assert.False(_directory.TryLocalLookup(fakeClientId, out lookupResult));
            Assert.Null(lookupResult);
            Assert.Equal(clientsVersion, _testAccessor.ObservedConnectedClientsVersion);

            // Add a new silo and ensure that its hosted client is immediately visible.
            var remoteSilo = Silo("127.0.0.1:222@100");
            var hostedClientId2 = HostedClient.CreateHostedClientGrainId(remoteSilo).GrainId;
            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            Assert.True(_directory.TryLocalLookup(hostedClientId2, out lookupResult));
            Assert.NotNull(lookupResult);
            Assert.Equal(Gateway.GetClientActivationAddress(hostedClientId2, remoteSilo), Assert.Single(lookupResult));
        }

        /// <summary>
        /// Tests for the basic functionality of <see cref="ClientDirectory.Lookup(GrainId)"/>
        /// </summary>
        [Fact]
        public async Task LocalLookupTests()
        {
            // Unknown clients don't exist locally
            var fakeClientId = Client("mr. snrub");
            var lookupResult = await _directory.Lookup(fakeClientId);
            Assert.Empty(lookupResult);

            var hostedClientId = HostedClient.CreateHostedClientGrainId(_localSilo).GrainId;
            lookupResult = await _directory.Lookup(hostedClientId);
            Assert.NotNull(lookupResult);
            var singleResult = Assert.Single(lookupResult);
            Assert.Equal(Gateway.GetClientActivationAddress(hostedClientId, _localSilo), singleResult);

            // Add the client and check that it's added successfully
            var clientsVersion = SetLocalClients(new List<GrainId> { fakeClientId });
            lookupResult = await _directory.Lookup(fakeClientId);
            Assert.NotNull(lookupResult);
            singleResult = Assert.Single(lookupResult);
            Assert.Equal(Gateway.GetClientActivationAddress(fakeClientId, _localSilo), singleResult);
            Assert.Equal(clientsVersion, _testAccessor.ObservedConnectedClientsVersion);

            // Remove the client and check that it's no longer found
            clientsVersion = SetLocalClients(new List<GrainId>(0));
            lookupResult = await _directory.Lookup(fakeClientId);
            Assert.Empty(lookupResult);
            Assert.Equal(clientsVersion, _testAccessor.ObservedConnectedClientsVersion);
        }

        /// <summary>
        /// Tests that <see cref="ClientDirectory.Lookup(GrainId)"/> will successfully reach out to a remote silo to perform lookups of client routes
        /// whent hey are not available locally. Additionally, that any other returned routes are stored locally so that subsequent lookups are not
        /// needed.
        /// </summary>
        [Fact]
        public async Task RemoteLookupSuccessTests()
        {
            var remoteClientId = Client("remote1");
            var remoteClientId2 = Client("remote2");
            var remoteSilo = Silo("127.0.0.1:222@100");

            // Verify that a silo will ask a remote silo 
            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            var remoteDirectory = _remoteDirectories.GetOrAdd(remoteSilo, Substitute.For<IRemoteClientDirectory>());
            remoteDirectory.GetClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(info =>
            {
                var versionVector = info.ArgAt<ImmutableDictionary<SiloAddress, long>>(0);
                Assert.NotNull(versionVector);
                Assert.True(versionVector.TryGetValue(_localSilo, out var localSiloVersion));
                Assert.Equal(2, localSiloVersion);

                Assert.True(versionVector.TryGetValue(remoteSilo, out var remoteSiloVersion));
                Assert.Equal(0, remoteSiloVersion);

                var result = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId>, long)>();
                result[remoteSilo] = (ImmutableHashSet.CreateRange(new[] { remoteClientId, remoteClientId2 }), 2);
                return Task.FromResult(result.ToImmutable());
            });

            var resultTask = _directory.Lookup(remoteClientId);
            var result = Assert.Single(await resultTask);
            Assert.Equal(Gateway.GetClientActivationAddress(remoteClientId, remoteSilo), result);

            // In finding the first client, the silo should have learned about the other client
            resultTask = _directory.Lookup(remoteClientId2);
            result = Assert.Single(await resultTask);
            Assert.Equal(Gateway.GetClientActivationAddress(remoteClientId2, remoteSilo), result);

            // The remote silo should not have been queried a second time.
            _ = remoteDirectory.Received(1).GetClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                Arg.Any<CancellationToken>());

            // Signal that the remote silo is shutting down. Both clients should disappear along with it.
            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.ShuttingDown, "remoteSilo");
            resultTask = _directory.Lookup(remoteClientId);
            Assert.Empty(await resultTask);
            resultTask = _directory.Lookup(remoteClientId2);
            Assert.Empty(await resultTask);

            // Since there are no other directories, no additional remote calls should have been made.
            _ = remoteDirectory.Received(1).GetClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// Tests that <see cref="ClientDirectory.Lookup(GrainId)"/> will continue despite failure reaching out to a remote silo.
        /// </summary>
        [Fact]
        public async Task RemoteLookupFailureTests()
        {
            var remoteClientId = Client("remote1");
            var remoteClientId2 = Client("remote2");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteSilo2 = Silo("127.0.0.1:333@100");

            var numTimesToThrow = new[] { 1 };
            IRemoteClientDirectory CreateRemoteDirectory()
            {
                var remoteDirectory = Substitute.For<IRemoteClientDirectory>();
                remoteDirectory.GetClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(info =>
                {
                    if (numTimesToThrow[0]-- > 0)
                    {
                        throw new TimeoutException("Unable");
                    }

                    var result = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId>, long)>();
                    result[remoteSilo] = (ImmutableHashSet.CreateRange(new[] { remoteClientId, remoteClientId2 }), 2);
                    return Task.FromResult(result.ToImmutable());
                });

                return remoteDirectory;
            }

            _remoteDirectories.GetOrAdd(remoteSilo, CreateRemoteDirectory());
            _remoteDirectories.GetOrAdd(remoteSilo2, CreateRemoteDirectory());

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            _clusterMembershipService.UpdateSiloStatus(remoteSilo2, SiloStatus.Active, "remoteSilo2");

            // Verify that a silo will ask a remote silo even after a failure
            var resultTask = _directory.Lookup(remoteClientId);
            var result = Assert.Single(await resultTask);
            Assert.Equal(Gateway.GetClientActivationAddress(remoteClientId, remoteSilo), result);

            // The silo should have made two calls: one failure and one successful call.
            // Each call should have landed on a different silo.
            foreach (var remoteDirectory in _remoteDirectories.Values)
            {
                _ = remoteDirectory.Received(1).GetClientRoutes(
                    Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                    Arg.Any<CancellationToken>());
            }
        }

        [Fact]
        public async Task InvalidationRefreshesAllCachedGateways()
        {
            var clientId = Client("dropped");
            var observerId = ObserverGrainId.Create(ClientGrainId.Create("dropped")).GrainId;
            var remoteSilos = Enumerable.Range(1, 6).Select(index => Silo($"127.0.0.1:{200 + index}@100")).ToArray();
            var refreshed = new HashSet<SiloAddress>();
            foreach (var silo in remoteSilos)
            {
                var remote = await AddRemoteClient(silo, clientId);
                remote.GetClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(info =>
                {
                    var versions = info.ArgAt<ImmutableDictionary<SiloAddress, long>>(0);
                    Assert.Equal(refreshed.Add(silo) ? 2 : 3, versions[silo]);
                    return Task.FromResult(CreateRoutes(silo, 3));
                });
            }

            Assert.True(_directory.TryLocalLookup(clientId, out var cached));
            Assert.Equal(remoteSilos.Length, cached.Count);
            var locator = new ClientGrainLocator(_localSiloDetails, _directory);
            locator.InvalidateCache(new GrainAddress { GrainId = observerId, SiloAddress = _localSilo });
            Assert.False(locator.TryLookupInCache(observerId, out _));

            Assert.Null(await locator.Lookup(observerId));
            Assert.Equal(remoteSilos.OrderBy(silo => silo.ToString()), refreshed.OrderBy(silo => silo.ToString()));

            // The newer owner versions also protect against delayed gossip of the dropped routes.
            foreach (var silo in remoteSilos)
            {
                await _directory.OnUpdateClientRoutes(CreateRoutes(silo, 2, clientId), TestContext.Current.CancellationToken);
            }

            Assert.False(locator.TryLookupInCache(observerId, out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InvalidationRevalidatesReconnectedGateway(bool sameVersion)
        {
            var clientId = Client("reconnected");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remote = await AddRemoteClient(remoteSilo, clientId);
            remote.GetClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(info =>
            {
                var versions = info.ArgAt<ImmutableDictionary<SiloAddress, long>>(0);
                Assert.Equal(2, versions[remoteSilo]);
                return Task.FromResult(sameVersion
                    ? ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty
                    : CreateRoutes(remoteSilo, 4, clientId));
            });

            var locator = new ClientGrainLocator(_localSiloDetails, _directory);
            locator.InvalidateCache(clientId);
            Assert.False(locator.TryLookupInCache(clientId, out _));

            var address = await locator.Lookup(clientId);
            Assert.Equal(Gateway.GetClientActivationAddress(clientId, remoteSilo), address);
            Assert.True(locator.TryLookupInCache(clientId, out var cached));
            Assert.Equal(address, cached);
            _ = remote.Received(1).GetClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RefreshRetainsConcurrentInvalidation()
        {
            var clientId = Client("reconnected");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remote = await AddRemoteClient(remoteSilo, clientId);
            var firstResponse = new TaskCompletionSource<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            remote.GetClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(_ =>
                Interlocked.Increment(ref calls) == 1 ? firstResponse.Task : Task.FromResult(CreateRoutes(remoteSilo, 4, clientId)));

            _directory.InvalidateCache(clientId);
            var lookup = _directory.Lookup(clientId).AsTask();
            Assert.Equal(1, calls);
            Assert.False(lookup.IsCompleted);

            _directory.InvalidateCache(clientId);
            firstResponse.SetResult(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty);
            var address = Assert.Single(await lookup.WaitAsync(TestContext.Current.CancellationToken));

            Assert.Equal(Gateway.GetClientActivationAddress(clientId, remoteSilo), address);
            _ = remote.Received(2).GetClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task InvalidatedGatewayRefreshFailureRemainsRetryable()
        {
            var clientId = Client("remote");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remote = await AddRemoteClient(remoteSilo, clientId);
            var failure = new TimeoutException("Client directory owner did not respond.");
            var respond = false;
            remote.GetClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(_ =>
                respond ? Task.FromResult(CreateRoutes(remoteSilo, 4, clientId)) : throw failure);

            _directory.InvalidateCache(clientId);
            Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => _directory.Lookup(clientId).AsTask()));
            Assert.False(_directory.TryLocalLookup(clientId, out _));

            respond = true;
            Assert.Equal(Gateway.GetClientActivationAddress(clientId, remoteSilo), Assert.Single(await _directory.Lookup(clientId)));
        }

        [Fact]
        public async Task InvalidationKeepsLocallyConnectedClientAvailable()
        {
            var clientId = Client("multiple-gateways");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remote = await AddRemoteClient(remoteSilo, clientId);
            remote.GetClientRoutes(default!, Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(Task.FromResult(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty));
            SetLocalClients([clientId]);

            var locator = new ClientGrainLocator(_localSiloDetails, _directory);
            locator.InvalidateCache(clientId);
            Assert.Equal(Gateway.GetClientActivationAddress(clientId, _localSilo), await locator.Lookup(clientId));
            _ = remote.DidNotReceive().GetClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                Arg.Any<CancellationToken>());

            SetLocalClients([]);
            Assert.Equal(Gateway.GetClientActivationAddress(clientId, remoteSilo), await locator.Lookup(clientId));
            _ = remote.Received(1).GetClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task InvalidationRemovesTerminatedGateway()
        {
            var clientId = Client("remote");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remote = await AddRemoteClient(remoteSilo, clientId);
            _directory.InvalidateCache(clientId);

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Dead, "remoteSilo");
            Assert.Empty(await _directory.Lookup(clientId));
            _ = remote.DidNotReceive().GetClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, long>>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task PublishChangesSuccessTests()
        {
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
            _testAccessor.SchedulePublishUpdate = () => _testAccessor.PublishUpdates().GetAwaiter().GetResult();
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method

            var remoteClientId = Client("remote1");
            var remoteClientId2 = Client("remote2");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteSilo2 = Silo("127.0.0.1:333@100");

            var totalUpdateCalls = new[] { 0 };
            var calledSilos = new List<SiloAddress>();
            var publishedUpdates = new List<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>>();
            SiloAddress GetOtherRemoteSilo(SiloAddress silo) => silo.Equals(remoteSilo) ? remoteSilo2 : remoteSilo;
            IRemoteClientDirectory CreateRemoteDirectory(SiloAddress silo)
            {
                var otherRemoteSilo = GetOtherRemoteSilo(silo);

                var remoteDirectory = Substitute.For<IRemoteClientDirectory>();
                remoteDirectory.GetClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(info =>
                {
                    var result = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId>, long)>();
                    result[silo] = (ImmutableHashSet.CreateRange(new[] { remoteClientId }), 2);
                    return Task.FromResult(result.ToImmutable());
                });

                remoteDirectory.OnUpdateClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(info =>
                {
                    calledSilos.Add(silo);
                    ++totalUpdateCalls[0];
                    var update = info.ArgAt<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>>(0);
                    publishedUpdates.Add(update);
                    return Task.CompletedTask;
                });

                return remoteDirectory;
            }

            _remoteDirectories.GetOrAdd(remoteSilo, CreateRemoteDirectory);
            _remoteDirectories.GetOrAdd(remoteSilo2, CreateRemoteDirectory);

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            _clusterMembershipService.UpdateSiloStatus(remoteSilo2, SiloStatus.Active, "remoteSilo2");

            var builder = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId>, long)>();
            builder[remoteSilo] = (ImmutableHashSet.CreateRange(new[] { remoteClientId, remoteClientId2 }), 3);
            builder[remoteSilo2] = (ImmutableHashSet.CreateRange(new[] { remoteClientId, remoteClientId2 }), 3);
            await _directory.OnUpdateClientRoutes(
                builder.ToImmutable(),
                TestContext.Current.CancellationToken);
            Assert.Equal(1, totalUpdateCalls[0]);

            var successor = Assert.Single(calledSilos);
            var initialUpdate = Assert.Single(publishedUpdates);
            Assert.DoesNotContain(successor, initialUpdate);
            Assert.True(initialUpdate.TryGetValue(GetOtherRemoteSilo(successor), out var remoteUpdate));
            Assert.Contains(remoteClientId2, remoteUpdate.ConnectedClients);

            SetLocalClients(new List<GrainId> { remoteClientId, remoteClientId2 });
            await _directory.OnUpdateClientRoutes(
                ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, totalUpdateCalls[0]);
            Assert.All(calledSilos, calledSilo => Assert.Equal(successor, calledSilo));
            var followUpUpdate = publishedUpdates[1];
            Assert.Single(followUpUpdate);
            Assert.True(followUpUpdate.TryGetValue(_localSilo, out var localUpdate));
            Assert.Equal(3, localUpdate.ConnectedClients.Count);
        }

        [Fact]
        public async Task ScheduledPublicationRepublishesChangesObservedInFlight()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteDirectory = _remoteDirectories.GetOrAdd(remoteSilo, Substitute.For<IRemoteClientDirectory>());
            var firstPublicationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPublicationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstPublicationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var publicationCount = 0;
            var firstClient = Client("local1");
            var secondClient = Client("local2");
            ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>? followUpUpdate = null;
            remoteDirectory.OnUpdateClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(info =>
            {
                var update = info.ArgAt<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>>(0);
                return Interlocked.Increment(ref publicationCount) switch
                {
                    1 => SignalAndReturn(firstPublicationStarted, firstPublicationRelease.Task),
                    2 => CaptureFollowUpAndReturn(secondPublicationStarted, update),
                    _ => throw new InvalidOperationException("Unexpected publication"),
                };
            });

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            _testAccessor.SchedulePublishUpdate = _testAccessor.SchedulePublishUpdates;
            SetLocalClients([firstClient]);
            Assert.True(_directory.TryLocalLookup(firstClient, out _));

            _testAccessor.SchedulePublishUpdates();
            await firstPublicationStarted.Task.WaitAsync(cancellationToken);

            SetLocalClients([firstClient, secondClient]);
            await _directory.OnUpdateClientRoutes(
                ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty,
                TestContext.Current.CancellationToken);

            firstPublicationRelease.TrySetResult(true);
            await secondPublicationStarted.Task.WaitAsync(cancellationToken);
            await _testAccessor.DrainScheduler().WaitAsync(cancellationToken);

            Assert.NotNull(followUpUpdate);
            Assert.True(followUpUpdate.TryGetValue(_localSilo, out var localUpdate));
            Assert.Contains(secondClient, localUpdate.ConnectedClients);
            _ = remoteDirectory.Received(2).OnUpdateClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(),
                Arg.Any<CancellationToken>());

            static Task SignalAndReturn(TaskCompletionSource<bool> started, Task task)
            {
                started.TrySetResult(true);
                return task;
            }

            Task CaptureFollowUpAndReturn(
                TaskCompletionSource<bool> started,
                ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update)
            {
                followUpUpdate = update;
                started.TrySetResult(true);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task ScheduledPublicationIgnoresDuplicateInFlightTrigger()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteDirectory = _remoteDirectories.GetOrAdd(remoteSilo, Substitute.For<IRemoteClientDirectory>());
            var publicationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var publicationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            remoteDirectory.OnUpdateClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(_ =>
            {
                publicationStarted.TrySetResult(true);
                return publicationRelease.Task;
            });

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            _testAccessor.SchedulePublishUpdate = _testAccessor.SchedulePublishUpdates;
            var localClient = Client("local");
            SetLocalClients([localClient]);
            Assert.True(_directory.TryLocalLookup(localClient, out _));

            _testAccessor.SchedulePublishUpdates();
            await publicationStarted.Task.WaitAsync(cancellationToken);
            _testAccessor.SchedulePublishUpdates();

            publicationRelease.TrySetResult(true);
            await _testAccessor.DrainScheduler().WaitAsync(cancellationToken);
            await _testAccessor.DrainScheduler().WaitAsync(cancellationToken);

            _ = remoteDirectory.Received(1).OnUpdateClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ScheduledPublicationFailureRepublishesInFlightChangesOnce()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteDirectory = _remoteDirectories.GetOrAdd(remoteSilo, Substitute.For<IRemoteClientDirectory>());
            var firstPublicationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPublicationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstPublicationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var publicationCount = 0;
            remoteDirectory.OnUpdateClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(_ =>
            {
                return Interlocked.Increment(ref publicationCount) switch
                {
                    1 => SignalAndReturn(firstPublicationStarted, firstPublicationRelease.Task),
                    2 => SignalAndReturn(secondPublicationStarted, Task.CompletedTask),
                    _ => throw new InvalidOperationException("Unexpected publication"),
                };
            });

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            _testAccessor.SchedulePublishUpdate = _testAccessor.SchedulePublishUpdates;
            var localClient = Client("local");

            _testAccessor.SchedulePublishUpdates();
            await firstPublicationStarted.Task.WaitAsync(cancellationToken);

            SetLocalClients([localClient]);
            await _directory.OnUpdateClientRoutes(
                ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty,
                TestContext.Current.CancellationToken);

            firstPublicationRelease.TrySetException(new TimeoutException("Unable"));
            await secondPublicationStarted.Task.WaitAsync(cancellationToken);
            await _testAccessor.DrainScheduler().WaitAsync(cancellationToken);
            _ = remoteDirectory.Received(2).OnUpdateClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(),
                Arg.Any<CancellationToken>());

            static Task SignalAndReturn(TaskCompletionSource<bool> started, Task task)
            {
                started.TrySetResult(true);
                return task;
            }
        }

        [Fact]
        public async Task ScheduledPublicationFailureWaitsForNextTrigger()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteDirectory = _remoteDirectories.GetOrAdd(remoteSilo, Substitute.For<IRemoteClientDirectory>());
            remoteDirectory.OnUpdateClientRoutes(default!, Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(_ => throw new TimeoutException("Unable"));

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            _testAccessor.SchedulePublishUpdate = _testAccessor.SchedulePublishUpdates;

            _testAccessor.SchedulePublishUpdates();
            await _testAccessor.DrainScheduler().WaitAsync(cancellationToken);
            await _testAccessor.DrainScheduler().WaitAsync(cancellationToken);

            _ = remoteDirectory.Received(1).OnUpdateClientRoutes(
                Arg.Any<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task QuiescenceTracksPublicationRegisteredBeforeRpcStarts()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteDirectory = _remoteDirectories.GetOrAdd(remoteSilo, Substitute.For<IRemoteClientDirectory>());
            var publicationRegistered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var publicationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _testAccessor.OnPublishRegistered = async () =>
            {
                publicationRegistered.TrySetResult(true);
                await publicationRelease.Task;
            };

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            var localClient = Client("local");
            SetLocalClients([localClient]);
            Assert.True(_directory.TryLocalLookup(localClient, out _));

            try
            {
                _testAccessor.SchedulePublishUpdates();
                await publicationRegistered.Task.WaitAsync(cancellationToken);

                var quiesce = _testAccessor.Quiesce(cancellationToken);
                publicationRelease.TrySetResult(true);
                await quiesce;

                _ = remoteDirectory.DidNotReceive().OnUpdateClientRoutes(
                    Arg.Any<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(),
                    Arg.Any<CancellationToken>());
            }
            finally
            {
                publicationRelease.TrySetResult(true);
            }
        }

        [Fact]
        public async Task PublishingStopsBeforeMembershipShutdownBegins()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteDirectory = _remoteDirectories.GetOrAdd(remoteSilo, Substitute.For<IRemoteClientDirectory>());
            var publicationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var publicationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            remoteDirectory.OnUpdateClientRoutes(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(_ =>
            {
                publicationStarted.TrySetResult(true);
                return publicationRelease.Task;
            });

            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.Active, "remoteSilo");
            _testAccessor.SchedulePublishUpdate = _testAccessor.SchedulePublishUpdates;
            ((ILifecycleParticipant<ISiloLifecycle>)_directory).Participate(_lifecycle);

            var membershipShutdownStarted = false;
            var publicationStoppedBeforeMembershipShutdown = false;
            _lifecycle.Subscribe(
                "MembershipShutdownObserver",
                ServiceLifecycleStage.BecomeActive,
                static _ => Task.CompletedTask,
                _ =>
                {
                    membershipShutdownStarted = true;
                    publicationStoppedBeforeMembershipShutdown = _testAccessor.PublishTasksCompleted;
                    return Task.CompletedTask;
                });

            var lifecycleStopped = false;
            try
            {
                var localClient = Client("local");
                SetLocalClients([localClient]);
                Assert.True(_directory.TryLocalLookup(localClient, out _));

                await _lifecycle.OnStart(cancellationToken);
                await publicationStarted.Task.WaitAsync(cancellationToken);

                var quiescing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = _testAccessor.StoppingToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                    quiescing);
                var lifecycleStop = _lifecycle.OnStop(cancellationToken);
                await quiescing.Task.WaitAsync(cancellationToken);
                await _testAccessor.DrainScheduler().WaitAsync(cancellationToken);

                Assert.False(lifecycleStop.IsCompleted);
                Assert.False(membershipShutdownStarted);

                publicationRelease.TrySetResult(true);
                await lifecycleStop;
                lifecycleStopped = true;

                Assert.True(
                    publicationStoppedBeforeMembershipShutdown,
                    "Client route publication remained active when the membership shutdown stage began.");
                Assert.True(_testAccessor.PublishTasksCompleted);

                var update = ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty.Add(
                    remoteSilo,
                    (ImmutableHashSet.Create(Client("remote")), 2));
                await _directory.OnUpdateClientRoutes(update, TestContext.Current.CancellationToken);
                _testAccessor.SchedulePublishUpdates();

                _ = remoteDirectory.Received(1).OnUpdateClientRoutes(
                    Arg.Any<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(),
                    Arg.Any<CancellationToken>());
            }
            finally
            {
                publicationRelease.TrySetResult(true);
                if (!lifecycleStopped)
                {
                    await _lifecycle.OnStop(CancellationToken.None);
                }
            }
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static GrainId Client(string id) => ClientGrainId.Create(id).GrainId;

        private async Task<IRemoteClientDirectory> AddRemoteClient(SiloAddress silo, GrainId clientId)
        {
            _clusterMembershipService.UpdateSiloStatus(silo, SiloStatus.Active, silo.ToString());
            await _directory.OnUpdateClientRoutes(CreateRoutes(silo, 2, clientId), TestContext.Current.CancellationToken);
            return _remoteDirectories[silo];
        }

        private static ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)> CreateRoutes(
            SiloAddress silo, long version, params GrainId[] clients)
            => ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty.Add(
                silo, (clients.ToImmutableHashSet().Add(HostedClient.CreateHostedClientGrainId(silo).GrainId), version));

        private long SetLocalClients(List<GrainId> clients)
        {
            var clientCollectionVersion = ++_expectedConnectedClientsVersion;
            _connectedClientCollection.GetConnectedClientIds().ReturnsForAnyArgs(_ => clients);
            _connectedClientCollection.Version.ReturnsForAnyArgs(_ => clientCollectionVersion);
            return clientCollectionVersion;
        }
    }
}
