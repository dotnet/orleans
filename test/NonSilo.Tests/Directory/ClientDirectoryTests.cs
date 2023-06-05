using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
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
using UnitTests.Directory;
using Xunit;

namespace NonSilo.Tests.Directory
{
    [TestCategory("BVT"), TestCategory("Directory")]
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
            _grainFactory.GetSystemTarget<IRemoteClientDirectory>(default, default)
                .ReturnsForAnyArgs(info => _remoteDirectories.GetOrAdd(info.ArgAt<SiloAddress>(1), k => Substitute.For<IRemoteClientDirectory>()));

            _directory = new ClientDirectory(
                grainFactory: _grainFactory,
                siloDetails: _localSiloDetails,
                messagingOptions: _messagingOptions,
                loggerFactory: _loggerFactory,
                clusterMembershipService: _clusterMembershipService,
                timerFactory: _timerFactory,
                connectedClients: _connectedClientCollection);
            _testAccessor = new ClientDirectory.TestAccessor(_directory);

            // Disable automatic publishing to simplify testing.
            _testAccessor.SchedulePublishUpdate = () => { };
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
            remoteDirectory.GetClientRoutes(default).ReturnsForAnyArgs(info =>
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
            _ = remoteDirectory.Received(1).GetClientRoutes(Arg.Any<ImmutableDictionary<SiloAddress, long>>());

            // Signal that the remote silo is shutting down. Both clients should disappear along with it.
            _clusterMembershipService.UpdateSiloStatus(remoteSilo, SiloStatus.ShuttingDown, "remoteSilo");
            resultTask = _directory.Lookup(remoteClientId);
            Assert.Empty(await resultTask);
            resultTask = _directory.Lookup(remoteClientId2);
            Assert.Empty(await resultTask);

            // Since there are no other directories, no additional remote calls should have been made.
            _ = remoteDirectory.Received(1).GetClientRoutes(Arg.Any<ImmutableDictionary<SiloAddress, long>>());
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
                remoteDirectory.GetClientRoutes(default).ReturnsForAnyArgs(info =>
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
                _ = remoteDirectory.Received(1).GetClientRoutes(Arg.Any<ImmutableDictionary<SiloAddress, long>>());
            }
        }

        [Fact]
        public async Task PublishChangesSuccessTests()
        {
            _testAccessor.SchedulePublishUpdate = () => _testAccessor.PublishUpdates().GetAwaiter().GetResult();

            var remoteClientId = Client("remote1");
            var remoteClientId2 = Client("remote2");
            var remoteSilo = Silo("127.0.0.1:222@100");
            var remoteSilo2 = Silo("127.0.0.1:333@100");

            var totalUpdateCalls = new[] { 0 };
            var calledSilos = new List<SiloAddress>();
            SiloAddress GetOtherRemoteSilo(SiloAddress silo) => silo.Equals(remoteSilo) ? remoteSilo2 : remoteSilo;
            IRemoteClientDirectory CreateRemoteDirectory(SiloAddress silo)
            {
                var otherRemoteSilo = GetOtherRemoteSilo(silo);

                var remoteDirectory = Substitute.For<IRemoteClientDirectory>();
                remoteDirectory.GetClientRoutes(default).ReturnsForAnyArgs(info =>
                {
                    var result = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId>, long)>();
                    result[silo] = (ImmutableHashSet.CreateRange(new[] { remoteClientId }), 2);
                    return Task.FromResult(result.ToImmutable());
                });

                remoteDirectory.OnUpdateClientRoutes(default).ReturnsForAnyArgs(info =>
                {
                    calledSilos.Add(silo);
                    var callNumber = ++totalUpdateCalls[0];
                    var update = info.ArgAt<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>>(0);

                    if (callNumber == 1)
                    {
                        Assert.True(update.TryGetValue(otherRemoteSilo, out var siloUpdate));
                        Assert.Contains(remoteClientId2, siloUpdate.ConnectedClients);
                    }
                    else if (callNumber == 2)
                    {
                        // There should only be one silo in this update since the other remote silo is dead and this silo already has its own latest state.
                        Assert.Single(update);
                        Assert.True(update.TryGetValue(_localSilo, out var siloUpdate));
                        Assert.Equal(3, siloUpdate.ConnectedClients.Count);
                    }
                    else
                    {
                        throw new InvalidOperationException("Unexpected call");
                    }

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
            await _directory.OnUpdateClientRoutes(builder.ToImmutable());
            Assert.Equal(1, totalUpdateCalls[0]);

            var oldSuccessor = calledSilos.Last();
            _clusterMembershipService.UpdateSiloStatus(oldSuccessor, SiloStatus.Dead, "blah");
            var newSuccessor = GetOtherRemoteSilo(oldSuccessor);
            totalUpdateCalls[0] = 0;

            // Add clients locally and see that they are propagated to the new successor.
            SetLocalClients(new List<GrainId> { remoteClientId, remoteClientId2 });
            builder = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId>, long)>();
            builder[oldSuccessor] = (ImmutableHashSet.CreateRange(new[] { remoteClientId2 }), 4);
            await _directory.OnUpdateClientRoutes(builder.ToImmutable());
            Assert.Equal(1, totalUpdateCalls[0]);
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static GrainId Client(string id) => ClientGrainId.Create(id).GrainId;

        private long SetLocalClients(List<GrainId> clients)
        {
            var clientCollectionVersion = ++_expectedConnectedClientsVersion;
            _connectedClientCollection.GetConnectedClientIds().ReturnsForAnyArgs(_ => clients);
            _connectedClientCollection.Version.ReturnsForAnyArgs(_ => clientCollectionVersion);
            return clientCollectionVersion;
        }
    }
}
