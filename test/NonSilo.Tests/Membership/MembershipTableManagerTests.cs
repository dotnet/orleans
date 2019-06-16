using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Xunit;
using NSubstitute;
using Orleans.Runtime;
using System;
using Orleans;
using Xunit.Abstractions;
using System.Linq;
using TestExtensions;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for <see cref="MembershipTableManager"/>
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableManagerTests
    {
        private readonly ITestOutputHelper output;
        public MembershipTableManagerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup for a fresh cluster.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_FreshTable()
        {
            var loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = Silo("127.0.0.1:100@1");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            var membershipTable = new InMemoryMembershipTable();

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();

            await StartupTest(loggerFactory, localSiloDetails, localSilo, membershipTable, fatalErrorHandler, membershipGossiper);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster()
        {
            var loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = Silo("127.0.0.1:100@1");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();

            await StartupTest(loggerFactory, localSiloDetails, localSilo, membershipTable, fatalErrorHandler, membershipGossiper);
        }

        private static async Task StartupTest(
            LoggerFactory loggerFactory,
            ILocalSiloDetails localSiloDetails,
            SiloAddress localSilo,
            InMemoryMembershipTable membershipTable,
            IFatalErrorHandler fatalErrorHandler,
            IMembershipGossiper membershipGossiper)
        {
            var lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());

            var manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: loggerFactory);

            // Validate that the initial snapshot is valid and contains the local silo.
            var initialSnapshot = manager.MembershipTableSnapshot;
            Assert.NotNull(initialSnapshot);
            Assert.NotNull(initialSnapshot.Entries);
            Assert.NotNull(initialSnapshot.LocalSilo);
            Assert.Equal(SiloStatus.Created, initialSnapshot.LocalSilo.Status);
            Assert.Equal(localSiloDetails.Name, initialSnapshot.LocalSilo.SiloName);
            Assert.Equal(localSiloDetails.DnsHostName, initialSnapshot.LocalSilo.HostName);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);

            Assert.NotNull(manager.MembershipTableUpdates);
            var changes = manager.MembershipTableUpdates;
            Assert.Equal(changes.Value.Version, manager.MembershipTableSnapshot.Version);
            Assert.Empty(membershipTable.Calls);

            // All of these checks were performed before any lifecycle methods have a chance to run.
            // This is in order to verify that a service accessing membership in its constructor will
            // see the correct results regardless of initialization order.
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);

            await lifecycle.OnStart();

            var calls = membershipTable.Calls;
            Assert.NotEmpty(calls);
            Assert.True(calls.Count >= 2);
            Assert.Equal(nameof(IMembershipTable.InitializeMembershipTable), calls[0].Method);
            Assert.Equal(nameof(IMembershipTable.ReadAll), calls[1].Method);

            // During initialization, a first read from the table will be performed, transitioning
            // membership to a valid version.
            Assert.True(changes.NextAsync().IsCompleted);
            var update1 = changes.NextAsync().GetAwaiter().GetResult();

            // Transition to joining.
            membershipGossiper.ClearReceivedCalls();
            await manager.UpdateStatus(SiloStatus.Joining);
            await membershipGossiper.ReceivedWithAnyArgs().GossipToRemoteSilos(default, default, default);
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            Assert.Equal(SiloStatus.Joining, manager.MembershipTableSnapshot.LocalSilo.Status);

            // An update should have been issued.
            Assert.True(update1.NextAsync().IsCompleted);
            Assert.NotEqual(update1.Value.Version, manager.MembershipTableSnapshot.Version);

            var update2 = update1.NextAsync().GetAwaiter().GetResult();
            Assert.Equal(update2.Value.Version, manager.MembershipTableSnapshot.Version);
            var entry = Assert.Single(update2.Value.Entries, e => e.Key.Equals(localSilo));
            Assert.Equal(localSilo, entry.Key);
            Assert.Equal(localSilo, entry.Value.SiloAddress);
            Assert.Equal(SiloStatus.Joining, entry.Value.Status);

            calls = membershipTable.Calls.Skip(2).ToList();
            Assert.NotEmpty(calls);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.InsertRow)));
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAll)));

            await lifecycle.OnStop();
            fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo has been restarted (there is an existing entry with an
        /// older generation).
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster_Restarted()
        {
            var loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = Silo("127.0.0.1:100@2");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            // The table includes a predecessor which is still marked as active
            // This can happen if a node restarts quickly.
            var predecessor = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active);

            var otherSilos = new[]
            {
                predecessor,
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();

            var lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());

            var manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: loggerFactory);

            // Validate that the initial snapshot is valid and contains the local silo.
            var snapshot = manager.MembershipTableSnapshot;
            Assert.NotNull(snapshot);
            Assert.NotNull(snapshot.Entries);
            Assert.NotNull(snapshot.LocalSilo);
            Assert.Equal(SiloStatus.Created, snapshot.LocalSilo.Status);
            Assert.Equal(localSiloDetails.Name, snapshot.LocalSilo.SiloName);
            Assert.Equal(localSiloDetails.DnsHostName, snapshot.LocalSilo.HostName);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);

            Assert.NotNull(manager.MembershipTableUpdates);
            var membershipUpdates = manager.MembershipTableUpdates;
            var firstSnapshot = membershipUpdates;
            Assert.Equal(firstSnapshot.Value.Version, manager.MembershipTableSnapshot.Version);
            Assert.Empty(membershipTable.Calls);

            // All of these checks were performed before any lifecycle methods have a chance to run.
            // This is in order to verify that a service accessing membership in its constructor will
            // see the correct results regardless of initialization order.
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);

            await lifecycle.OnStart();

            var calls = membershipTable.Calls;
            Assert.NotEmpty(calls);
            Assert.True(calls.Count >= 2);
            Assert.Equal(nameof(IMembershipTable.InitializeMembershipTable), calls[0].Method);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAll)));

            // Verify that the silo marked its predecessor as dead.
            Assert.True(firstSnapshot.NextAsync().IsCompleted);

            // During initialization, a first read from the table will be performed, transitioning
            // membership to a valid version.
            Assert.True(firstSnapshot.NextAsync().IsCompleted);
            var update1 = firstSnapshot.NextAsync().GetAwaiter().GetResult();

            // Transition to joining.
            await manager.UpdateStatus(SiloStatus.Joining);
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            Assert.Equal(SiloStatus.Joining, manager.MembershipTableSnapshot.LocalSilo.Status);

            var updates = ToList(membershipUpdates);
            var latest = updates.Last();

            Assert.Equal(updates.Last().Version, manager.MembershipTableSnapshot.Version);

            // The predecessor should have been marked dead during startup.
            Assert.Equal(SiloStatus.Active, update1.Value.GetSiloStatus(predecessor.SiloAddress));
            Assert.Equal(SiloStatus.Dead, latest.GetSiloStatus(predecessor.SiloAddress));

            var entry = Assert.Single(latest.Entries, e => e.Key.Equals(localSilo));
            Assert.Equal(localSilo, entry.Key);
            Assert.Equal(localSilo, entry.Value.SiloAddress);
            Assert.Equal(SiloStatus.Joining, entry.Value.Status);

            calls = membershipTable.Calls.Skip(2).ToList();
            Assert.NotEmpty(calls);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.InsertRow)));
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAll)));

            await lifecycle.OnStop();
            fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo has already been superseded by a newer iteration.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster_Superseded()
        {
            var loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = Silo("127.0.0.1:100@2");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            // The table includes a sucessor to this silo.
            var successor = Entry(Silo("127.0.0.1:100@3"), SiloStatus.Active);

            var otherSilos = new[]
            {
                successor,
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();

            var lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());

            var manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: loggerFactory);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);

            await lifecycle.OnStart();

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining);

            fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await lifecycle.OnStop();
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo has already been declared dead.
        /// Note that this should never happen in the way tested here - the silo should not be known
        /// to other silos before it starts up. Still, the case is covered by the manager.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster_AlreadyDeclaredDead()
        {
            var loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = Silo("127.0.0.1:100@2");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            var otherSilos = new[]
            {
                Entry(localSilo, SiloStatus.Dead),
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();

            var lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());

            var manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: loggerFactory);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);

            await lifecycle.OnStart();

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining);

            fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await lifecycle.OnStop();
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo is declared dead some time after updating its status to joining.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster_DeclaredDead_AfterJoining()
        {
            var loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            var localSilo = Silo("127.0.0.1:100@2");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active)
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();

            var lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());

            var manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: loggerFactory);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);

            await lifecycle.OnStart();

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining);

            fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            // Mark the silo as dead
            while (true)
            {
                var table = await membershipTable.ReadAll();
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(localSilo));
                var entry = row.Item1.WithStatus(SiloStatus.Dead);
                if (await membershipTable.UpdateRow(entry, row.Item2, table.Version.Next())) break;
            }

            // Refresh silo status and check that it determines it's dead.
            await manager.Refresh();
            fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await lifecycle.OnStop();
        }

        //x Initial snapshots are valid
        // Table refresh:
        // * Does periodic refresh
        // * Quick retry after exception
        // * Emits change notification
        // TrySuspectOrKill tests:
        //x Lifecycle tests:
        //x * Verify own memberhip table entry has correct properties
        //x * Cleans up old entries for same silo
        // * Graceful & ungraceful shutdown
        // * Timer stalls?
        //x * Snapshot updated + change notification emitted after status update
        // Fault on missing entry during refresh
        //x Fault on declared dead
        //x Gossips on updates

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status)
        {
            return new MembershipEntry { SiloAddress = address, Status = status };
        }

        private static MembershipTableData Table(params MembershipEntry[] entries)
        {
            var entryList = entries.Select(e => Tuple.Create(e, "test")).ToList();
            return new MembershipTableData(entryList, new TableVersion(12, "test"));
        }

        private static List<T> ToList<T>(ChangeFeedEntry<T> entry)
        {
            var result = new List<T>();
            do
            {
                var next = entry.NextAsync();
                if (entry.HasValue) result.Add(entry.Value);
                if (!next.IsCompleted) break;
                entry = next.GetAwaiter().GetResult();
            }
            while (true);

            return result;
        }
    }
}
