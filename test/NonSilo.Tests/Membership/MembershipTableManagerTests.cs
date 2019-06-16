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
using System.Collections.Generic;
using System.Threading;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for <see cref="MembershipTableManager"/>
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableManagerTests
    {
        private readonly ITestOutputHelper output;
        private readonly LoggerFactory loggerFactory;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly SiloAddress localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly IMembershipGossiper membershipGossiper;
        private readonly SiloLifecycleSubject lifecycle;

        public MembershipTableManagerTests(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });

            this.localSiloDetails = Substitute.For<ILocalSiloDetails>();
            this.localSilo = Silo("127.0.0.1:100@100");
            this.localSiloDetails.SiloAddress.Returns(this.localSilo);
            this.localSiloDetails.DnsHostName.Returns("MyServer11");
            this.localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            this.fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            this.membershipGossiper = Substitute.For<IMembershipGossiper>();
            this.lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup for a fresh cluster.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_FreshTable()
        {
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            await this.StartupTest(membershipTable, gracefulShutdown: true);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster()
        {
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            await this.StartupTest(membershipTable, gracefulShutdown: false);
        }

        private async Task StartupTest(InMemoryMembershipTable membershipTable, bool gracefulShutdown = true)
        {
            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);

            // Validate that the initial snapshot is valid and contains the local silo.
            var initialSnapshot = manager.MembershipTableSnapshot;
            Assert.NotNull(initialSnapshot);
            Assert.NotNull(initialSnapshot.Entries);
            Assert.NotNull(initialSnapshot.LocalSilo);
            Assert.Equal(SiloStatus.Created, initialSnapshot.LocalSilo.Status);
            Assert.Equal(this.localSiloDetails.Name, initialSnapshot.LocalSilo.SiloName);
            Assert.Equal(this.localSiloDetails.DnsHostName, initialSnapshot.LocalSilo.HostName);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);

            Assert.NotNull(manager.MembershipTableUpdates);
            var changes = manager.MembershipTableUpdates;
            Assert.Equal(changes.Value.Version, manager.MembershipTableSnapshot.Version);
            Assert.Empty(membershipTable.Calls);

            // All of these checks were performed before any lifecycle methods have a chance to run.
            // This is in order to verify that a service accessing membership in its constructor will
            // see the correct results regardless of initialization order.
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart();

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
            this.membershipGossiper.ClearReceivedCalls();
            await manager.UpdateStatus(SiloStatus.Joining);
            await this.membershipGossiper.ReceivedWithAnyArgs().GossipToRemoteSilos(default, default, default);
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            Assert.Equal(SiloStatus.Joining, manager.MembershipTableSnapshot.LocalSilo.Status);

            // An update should have been issued.
            Assert.True(update1.NextAsync().IsCompleted);
            Assert.NotEqual(update1.Value.Version, manager.MembershipTableSnapshot.Version);

            var update2 = update1.NextAsync().GetAwaiter().GetResult();
            Assert.Equal(update2.Value.Version, manager.MembershipTableSnapshot.Version);
            var entry = Assert.Single(update2.Value.Entries, e => e.Key.Equals(this.localSilo));
            Assert.Equal(this.localSilo, entry.Key);
            Assert.Equal(this.localSilo, entry.Value.SiloAddress);
            Assert.Equal(SiloStatus.Joining, entry.Value.Status);

            calls = membershipTable.Calls.Skip(2).ToList();
            Assert.NotEmpty(calls);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.InsertRow)));
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAll)));
            
            var cts = new CancellationTokenSource();
            if (!gracefulShutdown) cts.Cancel();
            await this.lifecycle.OnStop(cts.Token);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo has been restarted (there is an existing entry with an
        /// older generation).
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster_Restarted()
        {
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

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);

            // Validate that the initial snapshot is valid and contains the local silo.
            var snapshot = manager.MembershipTableSnapshot;
            Assert.NotNull(snapshot);
            Assert.NotNull(snapshot.Entries);
            Assert.NotNull(snapshot.LocalSilo);
            Assert.Equal(SiloStatus.Created, snapshot.LocalSilo.Status);
            Assert.Equal(this.localSiloDetails.Name, snapshot.LocalSilo.SiloName);
            Assert.Equal(this.localSiloDetails.DnsHostName, snapshot.LocalSilo.HostName);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);

            Assert.NotNull(manager.MembershipTableUpdates);
            var membershipUpdates = manager.MembershipTableUpdates;
            var firstSnapshot = membershipUpdates;
            Assert.Equal(firstSnapshot.Value.Version, manager.MembershipTableSnapshot.Version);
            Assert.Empty(membershipTable.Calls);

            // All of these checks were performed before any lifecycle methods have a chance to run.
            // This is in order to verify that a service accessing membership in its constructor will
            // see the correct results regardless of initialization order.
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart();

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

            var entry = Assert.Single(latest.Entries, e => e.Key.Equals(this.localSilo));
            Assert.Equal(this.localSilo, entry.Key);
            Assert.Equal(this.localSilo, entry.Value.SiloAddress);
            Assert.Equal(SiloStatus.Joining, entry.Value.Status);

            calls = membershipTable.Calls.Skip(2).ToList();
            Assert.NotEmpty(calls);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.InsertRow)));
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAll)));

            await this.lifecycle.OnStop();
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo has already been superseded by a newer iteration.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster_Superseded()
        {
            // The table includes a sucessor to this silo.
            var successor = Entry(Silo("127.0.0.1:100@200"), SiloStatus.Active);

            var otherSilos = new[]
            {
                successor,
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart();

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining);

            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStop();
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
            var otherSilos = new[]
            {
                Entry(this.localSilo, SiloStatus.Dead),
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart();

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining);

            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            var cts = new CancellationTokenSource();
            cts.Cancel();
            await this.lifecycle.OnStop(cts.Token);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo is declared dead some time after updating its status to joining.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Startup_ExistingCluster_DeclaredDead_AfterJoining()
        {
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active)
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining);

            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            // Mark the silo as dead
            while (true)
            {
                var table = await membershipTable.ReadAll();
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(this.localSilo));
                var entry = row.Item1.WithStatus(SiloStatus.Dead);
                if (await membershipTable.UpdateRow(entry, row.Item2, table.Version.Next())) break;
            }

            // Refresh silo status and check that it determines it's dead.
            await manager.Refresh();
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStop();
        }

        /// <summary>
        /// Try to suspect another silo of failing but discover that this silo has failed.
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_ButIAmKill()
        {
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();
            await manager.UpdateStatus(SiloStatus.Active);

            // Mark the silo as dead
            while (true)
            {
                var table = await membershipTable.ReadAll();
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(this.localSilo));
                var entry = row.Item1.WithStatus(SiloStatus.Dead);
                if (await membershipTable.UpdateRow(entry, row.Item2, table.Version.Next())) break;
            }

            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            await manager.TryToSuspectOrKill(otherSilos.First().SiloAddress);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);
        }

        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_AlreadyDead()
        {
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();
            await manager.UpdateStatus(SiloStatus.Active);

            var victim = otherSilos.Last().SiloAddress;
            await manager.TryToSuspectOrKill(victim);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(victim));
        }

        /// <summary>
        /// Declare a silo dead in a small, 2-silo cluster, requiring one vote ((2 + 1) / 2 = 1).
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_DeclareDead_SmallCluster()
        {
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();
            await manager.UpdateStatus(SiloStatus.Active);

            var victim = otherSilos.First().SiloAddress;
            await manager.TryToSuspectOrKill(victim);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(victim));
        }

        /// <summary>
        /// Declare a silo dead in a larger cluster, requiring 2 votes (per configuration).
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_DeclareDead_LargerCluster()
        {
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:600@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:700@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:800@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:900@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                loggerFactory: this.loggerFactory);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();
            await manager.UpdateStatus(SiloStatus.Active);

            // Try to declare an unknown silo dead
            await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.TryToSuspectOrKill(Silo("123.123.123.123:1@1")));

            // Multiple votes from the same node should not result in the node being declared dead.
            var victim = otherSilos.First().SiloAddress;
            await manager.TryToSuspectOrKill(victim);
            await manager.TryToSuspectOrKill(victim);
            await manager.TryToSuspectOrKill(victim);
            Assert.Equal(SiloStatus.Active, manager.MembershipTableSnapshot.GetSiloStatus(victim));

            // Manually remove our vote and add another silo's vote so we can be the one to kill the silo.
            while (true)
            {
                var table = await membershipTable.ReadAll();
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(victim));
                var entry = row.Item1.Copy();
                entry.SuspectTimes?.Clear();
                entry.AddSuspector(otherSilos[2].SiloAddress, DateTime.UtcNow);
                if (await membershipTable.UpdateRow(entry, row.Item2, table.Version.Next())) break;
            }

            await manager.TryToSuspectOrKill(victim);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(victim));

            // One down, one to go. Now overshoot votes and kill ourselves instead (due to internal error).
            victim = otherSilos[1].SiloAddress;
            while (true)
            {
                var table = await membershipTable.ReadAll();
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(victim));
                var entry = row.Item1.Copy();
                entry.SuspectTimes?.Clear();
                entry.AddSuspector(otherSilos[2].SiloAddress, DateTime.UtcNow);
                entry.AddSuspector(otherSilos[3].SiloAddress, DateTime.UtcNow);
                entry.AddSuspector(otherSilos[4].SiloAddress, DateTime.UtcNow);
                if (await membershipTable.UpdateRow(entry, row.Item2, table.Version.Next())) break;
            }

            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            await manager.TryToSuspectOrKill(victim);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            // We killed ourselves and should not have marked the other silo as dead.
            await manager.Refresh();
            Assert.Equal(SiloStatus.Active, manager.MembershipTableSnapshot.GetSiloStatus(victim));
        }

        //x Initial snapshots are valid
        // Table refresh:
        // * Does periodic refresh
        // * Quick retry after exception
        // * Emits change notification
        //x TrySuspectOrKill tests:
        //x * Notice I am dead during TrySuspectOrKill
        //x * KeyNotFound (bad silo address)
        //x * Already dead
        //x * Overshoot votes - fatal error
        //x * Declare dead: votes > config
        //x * Declare dead: votes > half cluster
        //x * Vote multiple times & don't declare dead
        //x Lifecycle tests:
        //x * Verify own memberhip table entry has correct properties
        //x * Cleans up old entries for same silo
        //x * Graceful shutdown
        //x *  Ungraceful shutdown
        // * Timer stalls?
        //x * Snapshot updated + change notification emitted after status update
        //x Fault on declared dead
        //x Gossips on updates

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status)
        {
            return new MembershipEntry { SiloAddress = address, Status = status };
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
