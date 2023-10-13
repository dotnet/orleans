using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Xunit;
using NSubstitute;
using Orleans.Runtime;
using Orleans;
using Xunit.Abstractions;
using TestExtensions;
using System.Collections.Concurrent;
using NonSilo.Tests.Utilities;

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
            this.fatalErrorHandler.IsUnexpected(default).ReturnsForAnyArgs(true);
            this.membershipGossiper = Substitute.For<IMembershipGossiper>();
            this.lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup for a fresh cluster.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_NewCluster()
        {
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            await this.BasicScenarioTest(membershipTable, gracefulShutdown: true);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_ExistingCluster()
        {
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            await this.BasicScenarioTest(membershipTable, gracefulShutdown: false);
        }

        private async Task BasicScenarioTest(InMemoryMembershipTable membershipTable, bool gracefulShutdown = true)
        {
            var timers = new List<DelegateAsyncTimer>();
            var timerCalls = new ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>();
            var timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var timer = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var task = new TaskCompletionSource<bool>();
                            timerCalls.Enqueue((overridePeriod, task));
                            return task.Task;
                        });
                    timers.Add(timer);
                    return timer;
                });

            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: timerFactory,
                this.lifecycle);

            // Validate that the initial snapshot is valid and contains the local silo.
            var initialSnapshot = manager.MembershipTableSnapshot;
            Assert.NotNull(initialSnapshot);
            Assert.NotNull(initialSnapshot.Entries);
            var localSiloEntry = initialSnapshot.Entries[this.localSilo];
            Assert.Equal(SiloStatus.Created, localSiloEntry.Status);
            Assert.Equal(this.localSiloDetails.Name, localSiloEntry.SiloName);
            Assert.Equal(this.localSiloDetails.DnsHostName, localSiloEntry.HostName);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);

            Assert.NotNull(manager.MembershipTableUpdates);
            var changes = manager.MembershipTableUpdates;
            var currentEnumerator = changes.GetAsyncEnumerator();
            Assert.True(currentEnumerator.MoveNextAsync().Result);
            Assert.Equal(currentEnumerator.Current.Version, manager.MembershipTableSnapshot.Version);
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
            // membership to a valid version.currentEnumerator = changes.GetAsyncEnumerator();
            currentEnumerator = changes.GetAsyncEnumerator();
            Assert.True(currentEnumerator.MoveNextAsync().Result);
            var update1 = currentEnumerator.Current;

            // Transition to joining.
            this.membershipGossiper.ClearReceivedCalls();
            await manager.UpdateStatus(SiloStatus.Joining);
            await this.membershipGossiper.ReceivedWithAnyArgs().GossipToRemoteSilos(default, default, default, default);
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            localSiloEntry = manager.MembershipTableSnapshot.Entries[this.localSilo];
            Assert.Equal(SiloStatus.Joining, localSiloEntry.Status);

            // An update should have been issued.
            currentEnumerator = changes.GetAsyncEnumerator();
            Assert.True(currentEnumerator.MoveNextAsync().Result);
            Assert.NotEqual(update1.Version, manager.MembershipTableSnapshot.Version);

            var update2 = currentEnumerator.Current;
            Assert.Equal(update2.Version, manager.MembershipTableSnapshot.Version);
            var entry = Assert.Single(update2.Entries, e => e.Key.Equals(this.localSilo));
            Assert.Equal(this.localSilo, entry.Key);
            Assert.Equal(this.localSilo, entry.Value.SiloAddress);
            Assert.Equal(SiloStatus.Joining, entry.Value.Status);

            calls = membershipTable.Calls.Skip(2).ToList();
            Assert.NotEmpty(calls);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.InsertRow)));
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAll)));

            {
                // Check that a timer is being requested and that after it expires a call to
                // refresh the membership table is made.
                Assert.True(timerCalls.TryDequeue(out var timer));
                membershipTable.ClearCalls();
                timer.Completion.TrySetResult(true);
                while (membershipTable.Calls.Count == 0) await Task.Delay(10);
                Assert.Contains(membershipTable.Calls, c => c.Method.Equals(nameof(IMembershipTable.ReadAll)));
            }

            var cts = new CancellationTokenSource();
            if (!gracefulShutdown) cts.Cancel();
            Assert.Equal(0, timers.First().DisposedCounter);
            var stopped = this.lifecycle.OnStop(cts.Token);

            // Complete any timers that were waiting.
            while (timerCalls.TryDequeue(out var t)) t.Completion.TrySetResult(false);

            await stopped;
            Assert.Equal(1, timers.First().DisposedCounter);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo has been restarted (there is an existing entry with an
        /// older generation).
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Restarted()
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                this.lifecycle);

            // Validate that the initial snapshot is valid and contains the local silo.
            var snapshot = manager.MembershipTableSnapshot;
            Assert.NotNull(snapshot);
            Assert.NotNull(snapshot.Entries);
            var localSiloEntry = snapshot.Entries[this.localSilo];
            Assert.Equal(SiloStatus.Created, localSiloEntry.Status);
            Assert.Equal(this.localSiloDetails.Name, localSiloEntry.SiloName);
            Assert.Equal(this.localSiloDetails.DnsHostName, localSiloEntry.HostName);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);

            Assert.NotNull(manager.MembershipTableUpdates);
            var membershipUpdates = manager.MembershipTableUpdates.GetAsyncEnumerator();
            Assert.True(membershipUpdates.MoveNextAsync().Result);
            var firstSnapshot = membershipUpdates.Current;
            Assert.Equal(firstSnapshot.Version, manager.MembershipTableSnapshot.Version);
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
            
            // During initialization, a first read from the table will be performed, transitioning
            // membership to a valid version.Assert.True(membershipUpdates.MoveNextAsync().Result);
            Assert.True(membershipUpdates.MoveNextAsync().Result);
            var update1 = membershipUpdates.Current;

            // Transition to joining.
            await manager.UpdateStatus(SiloStatus.Joining);
            snapshot = manager.MembershipTableSnapshot;
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            Assert.Equal(SiloStatus.Joining, snapshot.Entries[localSilo].Status);

            Assert.True(membershipUpdates.MoveNextAsync().Result);
            Assert.True(membershipUpdates.MoveNextAsync().Result);
            Assert.Equal(membershipUpdates.Current.Version, manager.MembershipTableSnapshot.Version);

            // The predecessor should have been marked dead during startup.
            Assert.Equal(SiloStatus.Active, update1.GetSiloStatus(predecessor.SiloAddress));
            var latest = membershipUpdates.Current;
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
        public async Task MembershipTableManager_Superseded()
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                this.lifecycle);

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
        public async Task MembershipTableManager_AlreadyDeclaredDead()
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                this.lifecycle);

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
        /// Tests <see cref="MembershipTableManager"/> behavior when there is an
        /// existing cluster and this silo is declared dead some time after updating its status to joining.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_DeclaredDead_AfterJoining()
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                siloLifecycle: this.lifecycle);
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                siloLifecycle: this.lifecycle);
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

        /// <summary>
        /// Try to suspect another silo of failing but discover that it is already dead.
        /// </summary>
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                siloLifecycle: this.lifecycle);
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                siloLifecycle: this.lifecycle);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();
            await manager.UpdateStatus(SiloStatus.Active);

            var victim = otherSilos.First().SiloAddress;
            await manager.TryToSuspectOrKill(victim);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(victim));
        }

        /// <summary>
        /// Declare a silo dead in a larger cluster, requiring 2 votes (per configuration), but where our clock is several minutes out of sync with others in the cluster.
        /// The purpose is to check that logic is consistent across a cluster even if clocks are wildly out of sync.
        /// This is especially relevant when it comes to vote counting.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_ClocksNotSynchronized()
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

            var clusterMembershipOptions = new ClusterMembershipOptions();
            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(clusterMembershipOptions),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                siloLifecycle: this.lifecycle);

            // Rig the local clock.
            var now = DateTime.UtcNow;
            manager.GetDateTimeUtcNow = () => now;

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();
            await manager.UpdateStatus(SiloStatus.Active);

            // Add some suspect times. The time difference between them is larger than the recency window (DeathVoteExpirationTimeout),
            // so only one of the votes will be considered fresh, even though both will be in the future from the perspective
            // of the local silo.
            var victim = otherSilos.First().SiloAddress;
            while (true)
            {
                var table = await membershipTable.ReadAll();
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(victim));
                var entry = row.Item1.Copy();
                entry.SuspectTimes?.Clear();

                // Twice the recency window into the future (from the local silo's perspective). This will be the benchmark against which
                // other votes are compared.
                // If the logic for counting fresh votes is faulty, this plus the vote below should have resulted in an eviction, and
                // therefore the local silo will crash, declaring that there is a bug.
                entry.AddSuspector(otherSilos[2].SiloAddress, now.Add(clusterMembershipOptions.DeathVoteExpirationTimeout.Multiply(2)));

                // Half the recency window into the past (from the local silo's perspective) and therefore within the recency window from
                // the local silo's perspective.
                // If the logic for counting fresh votes is faulty, this plus the local silo's vote should be enough to evict the victim.
                entry.AddSuspector(otherSilos[4].SiloAddress, now.Subtract(clusterMembershipOptions.DeathVoteExpirationTimeout.Divide(2)));
                if (await membershipTable.UpdateRow(entry, row.Item2, table.Version.Next())) break;
            }

            // Check that:
            //   a) Adding our vote changes nothing, since our clock is too far behind
            //   b) The silo is not mistakenly declared dead, since the difference between the two votes is larger than DeathVoteExpirationTimeout.
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            await manager.TryToSuspectOrKill(victim);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            // The victim should be alive as no second vote fell within the recency window of the latest vote.
            await manager.Refresh();
            Assert.Equal(SiloStatus.Active, manager.MembershipTableSnapshot.GetSiloStatus(victim));
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
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                siloLifecycle: this.lifecycle);
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

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> table refresh behavior.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Refresh()
        {
            var timers = new List<DelegateAsyncTimer>();
            var timerCalls = new ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>();
            var timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var t = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var task = new TaskCompletionSource<bool>();
                            timerCalls.Enqueue((overridePeriod, task));
                            return task.Task;
                        });
                    timers.Add(t);
                    return t;
                });

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
                timerFactory: timerFactory,
                siloLifecycle: this.lifecycle);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart();
            
            // Test that retries occur after an exception.
            (TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion) timer = (default, default);
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(1);
            var counter = 0;
            membershipTable.OnReadAll = () => { if (counter++ == 0) throw new Exception("no"); };
            timer.Completion.TrySetResult(true);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            // A shorter delay should be provided after a transient failure.
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(10);
            membershipTable.OnReadAll = null;
            Assert.True(timer.DelayOverride.HasValue);
            timer.Completion.TrySetResult(true);

            // The standard delay should be used thereafter.
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(10);
            Assert.False(timer.DelayOverride.HasValue);
            timer.Completion.TrySetResult(true);

            // If for some reason the timer itself fails (or something else), the silo should crash
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(10);
            timer.Completion.TrySetException(new Exception("no again"));
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);
            Assert.False(timerCalls.TryDequeue(out timer));
            await this.lifecycle.OnStop();
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status)
        {
            return new MembershipEntry { SiloAddress = address, Status = status };
        }
    }
}
