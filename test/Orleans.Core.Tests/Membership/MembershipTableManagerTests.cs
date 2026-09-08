using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for <see cref="MembershipTableManager"/>
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
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
            this.fatalErrorHandler.IsUnexpected(default!).ReturnsForAnyArgs(true);
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
            await this.BasicScenarioTest(
                membershipTable,
                gracefulShutdown: true,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_ExistingCluster()
        {
            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown, now),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now),
            };
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"), otherSilos);

            await this.BasicScenarioTest(
                membershipTable,
                gracefulShutdown: false,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        private async Task BasicScenarioTest(
            InMemoryMembershipTable membershipTable,
            bool gracefulShutdown,
            CancellationToken cancellationToken)
        {
            var timers = new List<DelegateAsyncTimer>();
            var timerCalls = new BlockingCollection<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>();

            var timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var timer = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var task = new TaskCompletionSource<bool>();
                            timerCalls.Add((overridePeriod, task));
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
                this.lifecycle,
                timeProvider: TimeProvider.System);

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
            var currentEnumerator = changes.GetAsyncEnumerator(cancellationToken);
            Assert.True(currentEnumerator.MoveNextAsync().Result);
            Assert.Equal(currentEnumerator.Current.Version, manager.MembershipTableSnapshot.Version);
            Assert.Empty(membershipTable.Calls);

            // All of these checks were performed before any lifecycle methods have a chance to run.
            // This is in order to verify that a service accessing membership in its constructor will
            // see the correct results regardless of initialization order.
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart(cancellationToken);

            var calls = membershipTable.Calls;
            Assert.NotEmpty(calls);
            Assert.True(calls.Count >= 2);
            Assert.Equal(nameof(IMembershipTable.InitializeMembershipTableAsync), calls[0].Method);
            Assert.Equal(nameof(IMembershipTable.ReadAllAsync), calls[1].Method);

            // During initialization, a first read from the table will be performed, transitioning
            // membership to a valid version.currentEnumerator = changes.GetAsyncEnumerator();
            currentEnumerator = changes.GetAsyncEnumerator(cancellationToken);
            Assert.True(currentEnumerator.MoveNextAsync().Result);
            var update1 = currentEnumerator.Current;

            // Transition to joining.
            this.membershipGossiper.ClearReceivedCalls();
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);
            await this.membershipGossiper.ReceivedWithAnyArgs().GossipToRemoteSilos(default!, default!, default!, default, default);
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            localSiloEntry = manager.MembershipTableSnapshot.Entries[this.localSilo];
            Assert.Equal(SiloStatus.Joining, localSiloEntry.Status);

            // An update should have been issued.
            currentEnumerator = changes.GetAsyncEnumerator(cancellationToken);
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
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.InsertRowAsync), StringComparison.Ordinal));
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAllAsync), StringComparison.Ordinal));

            {
                // Check that a timer is being requested and that after it expires a call to
                // refresh the membership table is made.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var (_, completion) = timerCalls.Take(cts.Token);
                membershipTable.ClearCalls();
                completion.TrySetResult(true);
                while (membershipTable.Calls.Count == 0) await Task.Delay(10, cancellationToken);
                Assert.Contains(membershipTable.Calls, c => c.Method.Equals(nameof(IMembershipTable.ReadAllAsync), StringComparison.Ordinal));
            }

            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!gracefulShutdown) shutdownCts.Cancel();
            Assert.Equal(0, timers.First().DisposedCounter);
            var stopped = this.lifecycle.OnStop(shutdownCts.Token);

            // Complete any timers that were waiting.
            while (timerCalls.TryTake(out var t))
            {
                t.Completion.TrySetResult(false);
            }

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
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;

            // The table includes a predecessor which is still marked as active
            // This can happen if a node restarts quickly.
            var predecessor = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, now);

            var otherSilos = new[]
            {
                predecessor,
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown, now),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now),
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
                this.lifecycle,
                timeProvider: TimeProvider.System);

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
            var membershipUpdates = manager.MembershipTableUpdates.GetAsyncEnumerator(cancellationToken);
            Assert.True(await membershipUpdates.MoveNextAsync());
            var firstSnapshot = membershipUpdates.Current;
            Assert.Equal(firstSnapshot.Version, manager.MembershipTableSnapshot.Version);
            Assert.Empty(membershipTable.Calls);

            // All of these checks were performed before any lifecycle methods have a chance to run.
            // This is in order to verify that a service accessing membership in its constructor will
            // see the correct results regardless of initialization order.
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart(cancellationToken);

            var calls = membershipTable.Calls;
            Assert.NotEmpty(calls);
            Assert.True(calls.Count >= 2);
            Assert.Equal(nameof(IMembershipTable.InitializeMembershipTableAsync), calls[0].Method);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAllAsync), StringComparison.Ordinal));

            // During initialization, the table is read and predecessor entries are declared dead
            // before the table snapshot is published to other components.
            Assert.True(await membershipUpdates.MoveNextAsync());
            var update1 = membershipUpdates.Current;

            // Transition to joining.
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);
            snapshot = manager.MembershipTableSnapshot;
            Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
            Assert.Equal(SiloStatus.Joining, snapshot.Entries[localSilo].Status);

            Assert.True(await membershipUpdates.MoveNextAsync());
            Assert.Equal(membershipUpdates.Current.Version, manager.MembershipTableSnapshot.Version);

            // The predecessor should have been marked dead during startup,
            // before the first snapshot was published.
            Assert.Equal(SiloStatus.Dead, update1.GetSiloStatus(predecessor.SiloAddress));
            var latest = membershipUpdates.Current;
            Assert.Equal(SiloStatus.Dead, latest.GetSiloStatus(predecessor.SiloAddress));

            var entry = Assert.Single(latest.Entries, e => e.Key.Equals(this.localSilo));
            Assert.Equal(this.localSilo, entry.Key);
            Assert.Equal(this.localSilo, entry.Value.SiloAddress);
            Assert.Equal(SiloStatus.Joining, entry.Value.Status);

            calls = membershipTable.Calls.Skip(2).ToList();
            Assert.NotEmpty(calls);
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.InsertRowAsync), StringComparison.Ordinal));
            Assert.Contains(calls, call => call.Method.Equals(nameof(IMembershipTable.ReadAllAsync), StringComparison.Ordinal));

            await this.lifecycle.OnStop(cancellationToken);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> behavior around silo startup when there is an
        /// existing cluster and this silo has already been superseded by a newer iteration.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Superseded()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;

            // The table includes a successor to this silo.
            var successor = Entry(Silo("127.0.0.1:100@200"), SiloStatus.Active, now);

            var otherSilos = new[]
            {
                successor,
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown, now),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now),
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
                this.lifecycle,
                timeProvider: TimeProvider.System);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart(cancellationToken);

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);

            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStop(cancellationToken);
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
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(this.localSilo, SiloStatus.Dead, now),
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.ShuttingDown, now),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Joining, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now),
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
                this.lifecycle,
                timeProvider: TimeProvider.System);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStart(cancellationToken);

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);

            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now)
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
                siloLifecycle: this.lifecycle,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);

            // Silo should kill itself during the joining phase
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);

            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            // Mark the silo as dead
            while (true)
            {
                var table = await membershipTable.ReadAllAsync(cancellationToken);
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(this.localSilo));
                var entry = row.Item1.WithStatus(SiloStatus.Dead);
                if (await membershipTable.UpdateRowAsync(entry, row.Item2, table.Version.Next(), cancellationToken)) break;
            }

            // Refresh silo status and check that it determines it's dead.
            await manager.Refresh(cancellationToken: cancellationToken);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task MembershipTableManager_DeclaredDead_DuringShutdown_DoesNotTerminate()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);
            await this.lifecycle.OnStop(cancellationToken);

            while (true)
            {
                var table = await membershipTable.ReadAllAsync(cancellationToken);
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(this.localSilo));
                if (await membershipTable.UpdateRowAsync(row.Item1.WithStatus(SiloStatus.Dead), row.Item2, table.Version.Next(), cancellationToken))
                {
                    break;
                }
            }

            await manager.Refresh(cancellationToken: cancellationToken);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        [Fact]
        public async Task MembershipTableManager_ExplicitDeadSnapshot_Terminates()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);

            var version = manager.MembershipTableSnapshot.Version;
            await manager.RefreshFromSnapshot(Snapshot(
                new MembershipVersion(version.Value - 1),
                Entry(this.localSilo, SiloStatus.Dead, DateTimeOffset.UtcNow)), cancellationToken);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            await manager.RefreshFromSnapshot(Snapshot(
                new MembershipVersion(version.Value + 1),
                Entry(this.localSilo, SiloStatus.Dead, DateTimeOffset.UtcNow)), cancellationToken);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);
            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task MembershipTableManager_DeadTransitionOutsideShutdown_Terminates()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);

            await manager.UpdateStatus(SiloStatus.Dead, cancellationToken);

            Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);
            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task MembershipTableManager_DeadTransitionDuringLaterStopStage_DoesNotTerminate()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            this.lifecycle.Subscribe(
                "CustomLaterStage",
                ServiceLifecycleStage.Active + 1,
                _ => Task.CompletedTask,
                ct => manager.UpdateStatus(SiloStatus.Dead, ct));
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);

            await this.lifecycle.OnStop(cancellationToken);

            Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        [Fact]
        public async Task MembershipTableManager_StopBeforeStart_DoesNotMarkLifecycleStopping()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await this.lifecycle.OnStop(cancellationToken);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Dead, cancellationToken);

            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);
            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task MembershipTableManager_MissingLocalEntry_OnlyNewerSnapshotAfterJoiningTerminates()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var otherSilo = Silo("127.0.0.1:200@100");
            var membershipTable = new InMemoryMembershipTable(
                new TableVersion(123, "123"),
                Entry(otherSilo, SiloStatus.Active, now));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            await manager.RefreshFromSnapshot(Snapshot(new MembershipVersion(1)), cancellationToken);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);
            var currentVersion = manager.MembershipTableSnapshot.Version;

            await manager.RefreshFromSnapshot(Snapshot(new MembershipVersion(currentVersion.Value - 1)), cancellationToken);
            await manager.RefreshFromSnapshot(Snapshot(
                currentVersion,
                Entry(otherSilo, SiloStatus.Active, now.AddMinutes(1))), cancellationToken);
            Assert.Equal(SiloStatus.Joining, manager.MembershipTableSnapshot.Entries[this.localSilo].Status);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            await using var membershipUpdates = manager.MembershipTableUpdates.GetAsyncEnumerator(cancellationToken);
            Assert.True(await membershipUpdates.MoveNextAsync());

            await manager.RefreshFromSnapshot(Snapshot(
                new MembershipVersion(currentVersion.Value + 1),
                Entry(otherSilo, SiloStatus.Active, now.AddMinutes(1))), cancellationToken);
            Assert.True(await membershipUpdates.MoveNextAsync());
            Assert.Equal(SiloStatus.Dead, membershipUpdates.Current.Entries[this.localSilo].Status);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.Entries[this.localSilo].Status);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task MembershipTableManager_MissingLocalEntry_WhenJoinSnapshotWasNotPublished_DoesNotTerminate()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);

            await manager.RefreshFromSnapshot(Snapshot(new MembershipVersion(125)), cancellationToken);
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
            Assert.DoesNotContain(this.localSilo, manager.MembershipTableSnapshot.Entries.Keys);

            await manager.RefreshFromSnapshot(Snapshot(new MembershipVersion(126)), cancellationToken);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task MembershipTableManager_DeclaredDeadThenPrunedBeforeRefresh_Terminates()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable(new TableVersion(123, "123"));
            var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Joining, cancellationToken);

            while (true)
            {
                var table = await membershipTable.ReadAllAsync(cancellationToken);
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(this.localSilo));
                if (await membershipTable.UpdateRowAsync(row.Item1.WithStatus(SiloStatus.Dead), row.Item2, table.Version.Next(), cancellationToken))
                {
                    break;
                }
            }

            await membershipTable.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
            var prunedTable = await membershipTable.ReadAllAsync(cancellationToken);
            Assert.DoesNotContain(prunedTable.Members, row => row.Item1.SiloAddress.Equals(this.localSilo));

            await manager.Refresh(cancellationToken: cancellationToken);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.Entries[this.localSilo].Status);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            await this.lifecycle.OnStop(cancellationToken);
        }

        /// <summary>
        /// Try to suspect another silo of failing but discover that this silo has failed.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_ButIAmKill()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
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
                siloLifecycle: this.lifecycle,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Active, cancellationToken);
            using var membershipEvents = new DiagnosticEventCollector(MembershipEvents.ListenerName);

            // Mark the silo as dead
            while (true)
            {
                var table = await membershipTable.ReadAllAsync(cancellationToken);
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(this.localSilo));
                var entry = row.Item1.WithStatus(SiloStatus.Dead);
                if (await membershipTable.UpdateRowAsync(entry, row.Item2, table.Version.Next(), cancellationToken)) break;
            }

            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            var victim = otherSilos.First().SiloAddress;
            var completion = WaitForSuspectOrKillCompletion(membershipEvents, victim, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            Assert.True((await completion).Success);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);
        }

        /// <summary>
        /// Try to suspect another silo of failing but discover that it is already dead.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_AlreadyDead()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now),
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
                siloLifecycle: this.lifecycle,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Active, cancellationToken);
            using var membershipEvents = new DiagnosticEventCollector(MembershipEvents.ListenerName);

            var victim = otherSilos.Last().SiloAddress;
            var completion = WaitForSuspectOrKillCompletion(membershipEvents, victim, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            Assert.True((await completion).Success);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(victim));
        }

        /// <summary>
        /// Declare a silo dead in a small, 2-silo cluster, requiring one vote ((2 + 1) / 2 = 1).
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_DeclareDead_SmallCluster()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Dead, now),
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
                siloLifecycle: this.lifecycle,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Active, cancellationToken);
            using var membershipEvents = new DiagnosticEventCollector(MembershipEvents.ListenerName);

            var victim = otherSilos.First().SiloAddress;
            var completion = WaitForSuspectOrKillCompletion(membershipEvents, victim, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            Assert.True((await completion).Success);
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
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTime.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:600@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:700@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:800@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:900@100"), SiloStatus.Dead, now),
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
                siloLifecycle: this.lifecycle,
                timeProvider: TimeProvider.System);

            // Rig the local clock.
            manager.GetDateTimeUtcNow = () => now;

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Active, cancellationToken);
            using var membershipEvents = new DiagnosticEventCollector(MembershipEvents.ListenerName);

            // Add some suspect times. The time difference between them is larger than the recency window (DeathVoteExpirationTimeout),
            // so only one of the votes will be considered fresh, even though both will be in the future from the perspective
            // of the local silo.
            var victim = otherSilos.First().SiloAddress;
            while (true)
            {
                var table = await membershipTable.ReadAllAsync(cancellationToken);
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
                if (await membershipTable.UpdateRowAsync(entry, row.Item2, table.Version.Next(), cancellationToken)) break;
            }

            // Check that:
            //   a) Adding our vote changes nothing, since our clock is too far behind
            //   b) The silo is not mistakenly declared dead, since the difference between the two votes is larger than DeathVoteExpirationTimeout.
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            var completion = WaitForSuspectOrKillCompletion(membershipEvents, victim, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            Assert.True((await completion).Success);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            // The victim should be alive as no second vote fell within the recency window of the latest vote.
            await manager.Refresh(cancellationToken: cancellationToken);
            Assert.Equal(SiloStatus.Active, manager.MembershipTableSnapshot.GetSiloStatus(victim));
        }

        /// <summary>
        /// Declare a silo dead in a larger cluster, requiring 2 votes (per configuration).
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_TrySuspectOrKill_DeclareDead_LargerCluster()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:300@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:400@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:500@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:600@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:700@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:800@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.1:900@100"), SiloStatus.Dead, now),
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
                siloLifecycle: this.lifecycle,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await manager.UpdateStatus(SiloStatus.Active, cancellationToken);
            using var membershipEvents = new DiagnosticEventCollector(MembershipEvents.ListenerName);

            // Multiple votes from the same node should not result in the node being declared dead.
            var victim = otherSilos.First().SiloAddress;
            var completions = WaitForSuspectOrKillCompletions(
                membershipEvents,
                victim,
                expectedCount: 3,
                cancellationToken: cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            Assert.All(await completions, completion => Assert.True(completion.Success));
            Assert.Equal(SiloStatus.Active, manager.MembershipTableSnapshot.GetSiloStatus(victim));

            // Manually remove our vote and add another silo's vote so we can be the one to kill the silo.
            while (true)
            {
                var table = await membershipTable.ReadAllAsync(cancellationToken);
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(victim));
                var entry = row.Item1.Copy();
                entry.SuspectTimes?.Clear();
                entry.AddSuspector(otherSilos[2].SiloAddress, DateTime.UtcNow);
                if (await membershipTable.UpdateRowAsync(entry, row.Item2, table.Version.Next(), cancellationToken)) break;
            }

            var completion = WaitForSuspectOrKillCompletion(membershipEvents, victim, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            Assert.True((await completion).Success);
            Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(victim));

            // One down, one to go. Now overshoot votes and kill ourselves instead (due to internal error).
            victim = otherSilos[1].SiloAddress;
            while (true)
            {
                var table = await membershipTable.ReadAllAsync(cancellationToken);
                var row = table.Members.Single(e => e.Item1.SiloAddress.Equals(victim));
                var entry = row.Item1.Copy();
                entry.SuspectTimes?.Clear();
                entry.AddSuspector(otherSilos[2].SiloAddress, DateTime.UtcNow);
                entry.AddSuspector(otherSilos[3].SiloAddress, DateTime.UtcNow);
                entry.AddSuspector(otherSilos[4].SiloAddress, DateTime.UtcNow);
                if (await membershipTable.UpdateRowAsync(entry, row.Item2, table.Version.Next(), cancellationToken)) break;
            }

            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            completion = WaitForSuspectOrKillCompletion(membershipEvents, victim, cancellationToken);
            await manager.TryToSuspectOrKill(victim, null, cancellationToken);
            Assert.False((await completion).Success);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            // We killed ourselves and should not have marked the other silo as dead.
            await manager.Refresh(cancellationToken: cancellationToken);
            Assert.Equal(SiloStatus.Active, manager.MembershipTableSnapshot.GetSiloStatus(victim));
        }

        /// <summary>
        /// Tests <see cref="MembershipTableManager"/> table refresh behavior.
        /// </summary>
        [Fact]
        public async Task MembershipTableManager_Refresh()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
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

            var now = DateTimeOffset.UtcNow;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, now)
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
                siloLifecycle: this.lifecycle,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);

            // Test that retries occur after an exception.
            (TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion) timer = (default, default!);
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(1, cancellationToken);
            var counter = 0;
            membershipTable.OnReadAll = () => { if (counter++ == 0) throw new Exception("no"); };
            timer.Completion.TrySetResult(true);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

            // A shorter delay should be provided after a transient failure.
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(10, cancellationToken);
            membershipTable.OnReadAll = null;
            Assert.True(timer.DelayOverride.HasValue);
            timer.Completion.TrySetResult(true);

            // The standard delay should be used thereafter.
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(10, cancellationToken);
            Assert.False(timer.DelayOverride.HasValue);
            timer.Completion.TrySetResult(true);

            // If for some reason the timer itself fails (or something else), the silo should crash
            while (!timerCalls.TryDequeue(out timer)) await Task.Delay(10, cancellationToken);
            timer.Completion.TrySetException(new Exception("no again"));
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);
            Assert.False(timerCalls.TryDequeue(out timer));
            await this.lifecycle.OnStop(cancellationToken);
        }

        [Fact]
        public async Task Refresh_CallerCancellation_DoesNotCancelSharedRefresh()
        {
            var readCompletion = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(readCompletion.Task);
            using var manager = this.CreateMembershipTableManager(membershipTable);
            using var cancellation = new CancellationTokenSource();
            var first = manager.Refresh(cancellationToken: cancellation.Token);
            var second = manager.Refresh(cancellationToken: TestContext.Current.CancellationToken);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(second.IsCompleted);
            Assert.False(readCompletion.Task.IsCompleted);
            Assert.Single(membershipTable.Inner.ReceivedCalls());

            readCompletion.SetResult(await new InMemoryMembershipTable(new TableVersion(1, "1")).ReadAllAsync(TestContext.Current.CancellationToken));
            await second;
            Assert.Equal(new MembershipVersion(1), manager.MembershipTableSnapshot.Version);
        }

        [Fact]
        public async Task Refresh_Disposal_CancelsSharedWaitWithoutApplyingLateRead()
        {
            var readCompletion = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(readCompletion.Task);
            using var manager = this.CreateMembershipTableManager(membershipTable);
            var refresh = manager.Refresh(cancellationToken: TestContext.Current.CancellationToken);

            manager.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(readCompletion.Task.IsCompleted);
            readCompletion.SetException(new InvalidOperationException("Late membership table failure"));
            Assert.Equal(MembershipVersion.MinValue, manager.MembershipTableSnapshot.Version);
        }

        [Fact]
        public async Task Refresh_MaintenanceStop_PreservesSharedAndSubsequentRefreshes()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var initial = await new InMemoryMembershipTable(new TableVersion(1, "1")).ReadAllAsync(cancellationToken);
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(initial);
            using var manager = this.CreateMembershipTableManager(membershipTable);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            var readCompletion = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
            membershipTable.ConfigureReadAll(readCompletion.Task);
            var refresh = manager.Refresh(cancellationToken: cancellationToken);

            await this.lifecycle.OnStop(cancellationToken);
            Assert.False(refresh.IsCompleted);
            readCompletion.SetResult(await new InMemoryMembershipTable(new TableVersion(2, "2")).ReadAllAsync(cancellationToken));
            await refresh;
            Assert.Equal(new MembershipVersion(2), manager.MembershipTableSnapshot.Version);

            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(new TableVersion(3, "3")).ReadAllAsync(cancellationToken));
            await manager.Refresh(cancellationToken: cancellationToken);
            Assert.Equal(new MembershipVersion(3), manager.MembershipTableSnapshot.Version);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        [Fact]
        public async Task PeriodicRefresh_ProviderFaultDuringStop_RemainsRecoverable()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(new TableVersion(1, "1")).ReadAllAsync(cancellationToken));
            var tick = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var manager = this.CreateMembershipTableManager(
                membershipTable, timerFactory: new DelegateAsyncTimerFactory((_, _) => new DelegateAsyncTimer(_ => tick.Task)));
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            var stopping = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            membershipTable.ConfigureReadAll(() =>
            {
                stopping.SetResult(this.lifecycle.OnStop(cancellationToken));
                return Task.FromException<MembershipTableData>(new InvalidOperationException("Provider failure during shutdown"));
            });

            tick.SetResult(true);
            var stop = await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            Assert.Equal(new MembershipVersion(1), manager.MembershipTableSnapshot.Version);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Refresh_MaintenanceStop_SettlesQueuedAndBackoffCleanupAcknowledgments(bool duringBackoff)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(new TableVersion(1, "1")).ReadAllAsync(cancellationToken));
            var clock = new BackoffTimeProvider();
            using var manager = this.CreateMembershipTableManager(
                membershipTable, clock, new DelegateAsyncTimerFactory((_, _) => new DelegateAsyncTimer(_ => Task.FromResult(false))));
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            var workerRead = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            membershipTable.ConfigureReadAll(() =>
            {
                workerStarted.TrySetResult();
                return workerRead.Task;
            });
            await manager.TryKill(Silo("127.0.0.1:200@100"), cancellationToken);
            await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var older = Silo("127.0.0.1:100@99");
            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(
                new TableVersion(2, "2"), Entry(older, SiloStatus.Active, DateTimeOffset.UtcNow)).ReadAllAsync(cancellationToken));
            using var events = new DiagnosticEventCollector(MembershipEvents.ListenerName);
            var acknowledged = events.WaitForEventAsync(
                nameof(MembershipEvents.SuspectOrKillRequestCompleted),
                evt => evt.Payload is MembershipEvents.SuspectOrKillRequestCompleted completed
                    && completed.ObserverSiloAddress.Equals(this.localSilo) && completed.SiloAddress.Equals(older),
                TimeSpan.FromSeconds(10), cancellationToken);

            var refresh = manager.Refresh(cancellationToken: cancellationToken);
            Assert.False(refresh.IsCompleted);
            if (duringBackoff)
            {
                workerRead.SetException(new InvalidOperationException("Worker failure before acknowledged cleanup"));
                await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            await this.lifecycle.OnStop(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await refresh.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var completed = Assert.IsType<MembershipEvents.SuspectOrKillRequestCompleted>((await acknowledged).Payload);
            Assert.False(completed.Success);
            Assert.IsAssignableFrom<OperationCanceledException>(completed.Exception);
            Assert.Equal(new MembershipVersion(2), manager.MembershipTableSnapshot.Version);
            if (!duringBackoff)
            {
                Assert.False(workerRead.Task.IsCompleted);
                workerRead.SetException(new InvalidOperationException("Late worker read failure"));
            }

            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(new TableVersion(3, "3")).ReadAllAsync(cancellationToken));
            await manager.Refresh(cancellationToken: cancellationToken);
            Assert.Equal(new MembershipVersion(3), manager.MembershipTableSnapshot.Version);
        }

        [Fact]
        public async Task Refresh_QueueOverflowSettlesCleanupAcknowledgment()
        {
            await QueueOverflowSettlesCleanupAcknowledgment(retryWrite: false);
        }

        [Fact]
        public async Task Refresh_RetryQueueOverflowSettlesCleanupAcknowledgment()
        {
            await QueueOverflowSettlesCleanupAcknowledgment(retryWrite: true);
        }

        private async Task QueueOverflowSettlesCleanupAcknowledgment(bool retryWrite)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(new TableVersion(1, "1")).ReadAllAsync(cancellationToken));
            var clock = new BackoffTimeProvider();
            using var manager = this.CreateMembershipTableManager(
                membershipTable, clock, new DelegateAsyncTimerFactory((_, _) => new DelegateAsyncTimer(_ => Task.FromResult(false))));
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            var workerRead = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            membershipTable.ConfigureReadAll(() =>
            {
                workerStarted.TrySetResult();
                return workerRead.Task;
            });
            await manager.TryKill(Silo("127.0.0.1:200@100"), cancellationToken);
            await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(
                new TableVersion(2, "2"), Entry(Silo("127.0.0.1:100@99"), SiloStatus.Active, DateTimeOffset.UtcNow)).ReadAllAsync(cancellationToken));

            var refresh = manager.Refresh(cancellationToken: cancellationToken);
            Assert.False(refresh.IsCompleted);
            var writes = retryWrite ? 99 : 100;
            for (var i = 0; i < writes; i++)
            {
                await manager.TryKill(Silo("127.0.0.1:300@100"), cancellationToken);
            }

            if (retryWrite)
            {
                Assert.False(refresh.IsCompleted);
                workerRead.SetException(new InvalidOperationException("Retry the request after filling its queue"));
                await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            await refresh.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(new MembershipVersion(2), manager.MembershipTableSnapshot.Version);
            await this.lifecycle.OnStop(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (!retryWrite)
            {
                Assert.False(workerRead.Task.IsCompleted);
                workerRead.SetException(new InvalidOperationException("Late worker read failure"));
            }
        }

        [Fact]
        public async Task UpdateLocalStatus_Cancellation_StopsPendingReadWithoutRetry()
        {
            var readCompletion = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(readCompletion.Task);
            using var manager = this.CreateMembershipTableManager(membershipTable);
            using var cancellation = new CancellationTokenSource();
            var update = ((IMembershipManager)manager).UpdateLocalStatus(SiloStatus.Joining, cancellation.Token);

            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => update.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Single(membershipTable.Inner.ReceivedCalls());
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
            Assert.Empty(this.membershipGossiper.ReceivedCalls());
            readCompletion.SetException(new InvalidOperationException("Late status read failure"));
        }

        [Fact]
        public async Task UpdateLocalStatus_Cancellation_LeavesProviderWriteRunning()
        {
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(await new InMemoryMembershipTable(new TableVersion(1, "1")).ReadAllAsync(TestContext.Current.CancellationToken));
            var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            membershipTable.ConfigureInsertRow(write.Task);
            using var manager = this.CreateMembershipTableManager(membershipTable);
            using var cancellation = new CancellationTokenSource();
            var update = ((IMembershipManager)manager).UpdateLocalStatus(SiloStatus.Joining, cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => update.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(write.Task.IsCompleted);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
            Assert.Empty(this.membershipGossiper.ReceivedCalls());
            Assert.Single(membershipTable.Inner.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IMembershipTable.InsertRow));
            write.SetException(new InvalidOperationException("Late status write failure"));
        }

        [Fact]
        public async Task UpdateIAmAlive_Cancellation_InterruptsProviderWait()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureUpdateIAmAlive(completion.Task);
            using var manager = this.CreateMembershipTableManager(membershipTable);
            using var cancellation = new CancellationTokenSource();
            var update = ((IMembershipManager)manager).UpdateIAmAlive(cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => update.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(completion.Task.IsCompleted);
            Assert.Single(membershipTable.Inner.ReceivedCalls());
            completion.SetResult();
        }

        [Fact]
        public async Task UpdateIAmAlive_ForwardsCallerTokenToNativeProvider()
        {
            var membershipTable = Substitute.For<IMembershipTable>();
            using var cancellation = new CancellationTokenSource();
            CancellationToken receivedToken = default;
            membershipTable.UpdateIAmAliveAsync(Arg.Any<MembershipEntry>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                receivedToken = call.ArgAt<CancellationToken>(1);
                return Task.CompletedTask;
            });
            using var manager = this.CreateMembershipTableManager(membershipTable);

            await manager.UpdateIAmAlive(cancellation.Token);

            Assert.Equal(cancellation.Token, receivedToken);
            var call = Assert.Single(membershipTable.ReceivedCalls());
            Assert.Equal(2, call.GetArguments().Length);
            Assert.Equal(this.localSilo, Assert.IsType<MembershipEntry>(call.GetArguments()[0]).SiloAddress);
        }

        [Fact]
        public async Task SuspectOrKill_PreCanceled_DoesNotQueueProviderWork()
        {
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            using var manager = this.CreateMembershipTableManager(membershipTable);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var membershipManager = (IMembershipManager)manager;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => membershipManager.TryKillSilo(this.localSilo, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => membershipManager.TrySuspectSilo(this.localSilo, null, cancellation.Token));

            Assert.Empty(membershipTable.Inner.ReceivedCalls());
        }

        [Fact]
        public async Task ProcessGossipSnapshot_CancellationInterruptsPendingRefresh()
        {
            var readCompletion = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var membershipTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            membershipTable.ConfigureReadAll(readCompletion.Task);
            using var manager = this.CreateMembershipTableManager(membershipTable);
            var refresh = manager.Refresh(cancellationToken: TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var gossip = ((IMembershipManager)manager).ProcessGossipSnapshot(
                Snapshot(new MembershipVersion(2)), cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => gossip.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(refresh.IsCompleted);
            Assert.Equal(MembershipVersion.MinValue, manager.MembershipTableSnapshot.Version);

            readCompletion.SetResult(await new InMemoryMembershipTable(new TableVersion(1, "1")).ReadAllAsync(TestContext.Current.CancellationToken));
            await refresh;
            Assert.Equal(new MembershipVersion(1), manager.MembershipTableSnapshot.Version);
        }

        [Theory]
        [InlineData(false, 3000)]
        [InlineData(true, 500)]
        public async Task UpdateStatus_Terminating_GossipDeadlinePreservesCommittedStatus(bool systemTargetProvider, int deadlineMilliseconds)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var clock = new FakeTimeProvider();
            IMembershipTable membershipTable = new InMemoryMembershipTable(
                new TableVersion(1, "1"), Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, DateTimeOffset.UtcNow));
            if (systemTargetProvider)
            {
                membershipTable = CreateSystemTargetBasedMembershipTable(membershipTable);
            }

            using var manager = this.CreateMembershipTableManager(membershipTable, clock);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await this.lifecycle.OnStop(cancellationToken);
            var gossipStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            MembershipTableSnapshot? gossipSnapshot = null;
            this.membershipGossiper.GossipToRemoteSilos(
                Arg.Any<List<SiloAddress>>(), Arg.Any<MembershipTableSnapshot>(), this.localSilo, SiloStatus.Dead, Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var token = call.ArgAt<CancellationToken>(4);
                    gossipSnapshot = call.ArgAt<MembershipTableSnapshot>(1);
                    gossipStarted.SetResult(token);
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                });

            var update = manager.UpdateStatus(SiloStatus.Dead, cancellationToken);
            var gossipToken = await gossipStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.NotNull(gossipSnapshot);
            Assert.Equal(SiloStatus.Dead, gossipSnapshot.Entries[this.localSilo].Status);
            Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
            Assert.True(gossipToken.CanBeCanceled);
            clock.Advance(TimeSpan.FromMilliseconds(deadlineMilliseconds - 1));
            Assert.False(gossipToken.IsCancellationRequested);
            Assert.False(update.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await update.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.True(gossipToken.IsCancellationRequested);
            Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        [Fact]
        public async Task UpdateStatus_Terminating_CallerCancellationPreservesCommittedStatus()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var manager = this.CreateMembershipTableManager(new InMemoryMembershipTable(new TableVersion(1, "1")));
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await this.lifecycle.OnStop(cancellationToken);
            var gossipStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.membershipGossiper.GossipToRemoteSilos(
                Arg.Any<List<SiloAddress>>(), Arg.Any<MembershipTableSnapshot>(), this.localSilo, SiloStatus.Dead, Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var token = call.ArgAt<CancellationToken>(4);
                    gossipStarted.SetResult(token);
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            using var cancellation = new CancellationTokenSource();
            var update = manager.UpdateStatus(SiloStatus.Dead, cancellation.Token);
            var gossipToken = await gossipStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => update.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(gossipToken.IsCancellationRequested);
            Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
        }

        [Fact]
        public async Task UpdateStatus_TerminatingSystemTarget_PreservesActualStateWhileWriteIsPending()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var clock = new FakeTimeProvider();
            var backingTable = new LegacyMembershipTable(Substitute.For<IMembershipTable>());
            backingTable.ConfigureReadAll(await new InMemoryMembershipTable(
                new TableVersion(1, "1"), Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, DateTimeOffset.UtcNow)).ReadAllAsync(cancellationToken));
            var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            backingTable.ConfigureInsertRow(write.Task);
            using var manager = this.CreateMembershipTableManager(CreateSystemTargetBasedMembershipTable(backingTable), clock);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);
            await this.lifecycle.OnStart(cancellationToken);
            await this.lifecycle.OnStop(cancellationToken);
            var gossipStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.membershipGossiper.GossipToRemoteSilos(
                Arg.Any<List<SiloAddress>>(), Arg.Any<MembershipTableSnapshot>(), this.localSilo, SiloStatus.Dead, Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var token = call.ArgAt<CancellationToken>(4);
                    gossipStarted.SetResult(token);
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            using var cleanup = new CancellationTokenSource();
            var update = manager.UpdateStatus(SiloStatus.Dead, cleanup.Token);
            Assert.Single(backingTable.Inner.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IMembershipTable.InsertRow));
            Assert.False(gossipStarted.Task.IsCompleted);

            clock.Advance(TimeSpan.FromMilliseconds(500));
            var gossipToken = await gossipStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.False(gossipToken.IsCancellationRequested);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await update.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Assert.True(gossipToken.IsCancellationRequested);
            Assert.False(write.Task.IsCompleted);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
            Assert.DoesNotContain(this.localSilo, manager.MembershipTableSnapshot.Entries.Keys);
            cleanup.Cancel();
            write.SetException(new InvalidOperationException("Late terminal status write failure"));
        }

        private SystemTargetBasedMembershipTable CreateSystemTargetBasedMembershipTable(IMembershipTable backingTable)
        {
            var primarySilo = Silo("127.0.0.1:200@100");
            var membershipTarget = Substitute.For<IMembershipTableSystemTarget>();
            membershipTarget.ReadAllAsync(Arg.Any<CancellationToken>())
                .Returns(call => backingTable.ReadAllAsync(call.ArgAt<CancellationToken>(0)));
            membershipTarget.InsertRowAsync(Arg.Any<MembershipEntry>(), Arg.Any<TableVersion>(), Arg.Any<CancellationToken>())
                .Returns(call => backingTable.InsertRowAsync(call.ArgAt<MembershipEntry>(0), call.ArgAt<TableVersion>(1), call.ArgAt<CancellationToken>(2)));
            membershipTarget.UpdateRowAsync(Arg.Any<MembershipEntry>(), Arg.Any<string>(), Arg.Any<TableVersion>(), Arg.Any<CancellationToken>())
                .Returns(call => backingTable.UpdateRowAsync(call.ArgAt<MembershipEntry>(0), call.ArgAt<string>(1), call.ArgAt<TableVersion>(2), call.ArgAt<CancellationToken>(3)));
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            grainFactory.GetSystemTarget<IMembershipTableSystemTarget>(Constants.SystemMembershipTableType, Arg.Any<SiloAddress>())
                .Returns(membershipTarget);
            var services = Substitute.For<IServiceProvider>();
            services.GetService(typeof(IOptions<DevelopmentClusterMembershipOptions>)).Returns(
                Options.Create(new DevelopmentClusterMembershipOptions { PrimarySiloEndpoint = primarySilo.Endpoint }));
            services.GetService(typeof(ILocalSiloDetails)).Returns(this.localSiloDetails);
            services.GetService(typeof(IInternalGrainFactory)).Returns(grainFactory);
            return new SystemTargetBasedMembershipTable(services, this.loggerFactory.CreateLogger<SystemTargetBasedMembershipTable>());
        }

        private sealed class BackoffTimeProvider : FakeTimeProvider
        {
            public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = base.CreateTimer(callback, state, dueTime, period);
                TimerCreated.TrySetResult();
                return timer;
            }
        }

        [Fact]
        public async Task MembershipTableManager_RequireFreshStartsNewReadWhileRefreshInFlight()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable();
            var manager = CreateMembershipTableManager(membershipTable);
            var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseFirstRead = new ManualResetEventSlim();
            var readCount = 0;
            membershipTable.OnReadAll = () =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    firstReadStarted.TrySetResult();
                    if (!releaseFirstRead.Wait(TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException("Timed out waiting to release the first membership-table read");
                    }
                }
            };

            var inFlightRefresh = Task.Run(
                () => manager.Refresh(cancellationToken: cancellationToken),
                cancellationToken);
            await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var causalRefresh = ((IMembershipManager)manager).Refresh(
                targetVersion: null,
                cancellationToken: CancellationToken.None,
                requireFresh: true);

            try
            {
                await causalRefresh.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                Assert.Equal(2, readCount);
            }
            finally
            {
                releaseFirstRead.Set();
                await inFlightRefresh;
            }
            Assert.Equal(2, readCount);
        }

        [Fact]
        public async Task MembershipTableManager_PreCancelledFreshRefreshDoesNotRead()
        {
            var membershipTable = new InMemoryMembershipTable();
            var manager = CreateMembershipTableManager(membershipTable);
            var readCount = 0;
            membershipTable.OnReadAll = () => Interlocked.Increment(ref readCount);
            var cancellation = new CancellationToken(canceled: true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.Refresh(
                    targetVersion: null,
                    cancellationToken: cancellation,
                    requireFresh: true));

            Assert.Equal(0, readCount);
        }

        [Fact]
        public async Task MembershipTableManager_ShutdownFreshRefreshDoesNotRead()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var membershipTable = new InMemoryMembershipTable();
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            using var manager = CreateMembershipTableManager(membershipTable, lifecycle);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);
            await lifecycle.OnStart(cancellationToken);
            await lifecycle.OnStop(cancellationToken);
            var readCount = 0;
            membershipTable.OnReadAll = () => Interlocked.Increment(ref readCount);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.Refresh(
                    targetVersion: null,
                    cancellationToken: CancellationToken.None,
                    requireFresh: true));

            Assert.Equal(0, readCount);
        }

        [Fact]
        public async Task MembershipTableManager_FreshTargetRefreshStopsOnShutdown()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var innerMembershipTable = new InMemoryMembershipTable();
            var membershipTable = new DelegatingMembershipTable(innerMembershipTable);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            using var manager = CreateMembershipTableManager(membershipTable, lifecycle);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);
            await lifecycle.OnStart(cancellationToken);

            var blockedRead = new TaskCompletionSource<MembershipTableData>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedReadStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedReadResult = await innerMembershipTable.ReadAll();
            var readCount = 0;
            membershipTable.ReadAllOverride = () =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    return innerMembershipTable.ReadAll();
                }

                blockedReadStarted.TrySetResult();
                return blockedRead.Task;
            };

            var refresh = manager.Refresh(
                targetVersion: new MembershipVersion(long.MaxValue),
                cancellationToken: CancellationToken.None,
                requireFresh: true);
            await blockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            try
            {
                await lifecycle.OnStop(cancellationToken);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            }
            finally
            {
                blockedRead.TrySetResult(blockedReadResult);
            }
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private MembershipTableManager CreateMembershipTableManager(
            IMembershipTable membershipTable,
            TimeProvider? timeProvider = null,
            IAsyncTimerFactory? timerFactory = null,
            SiloLifecycleSubject? lifecycle = null)
        {
            return new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: timerFactory ?? new AsyncTimerFactory(this.loggerFactory),
                siloLifecycle: lifecycle ?? this.lifecycle,
                timeProvider: timeProvider ?? TimeProvider.System);
        }

        private sealed class DelegatingMembershipTable(IMembershipTable inner) : IMembershipTable
        {
            public Func<Task<MembershipTableData>>? ReadAllOverride { get; set; }

            public Task InitializeMembershipTable(bool tryInitTableVersion) =>
                inner.InitializeMembershipTable(tryInitTableVersion);

            public Task DeleteMembershipTableEntries(string clusterId) =>
                inner.DeleteMembershipTableEntries(clusterId);

            public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) =>
                inner.CleanupDefunctSiloEntries(beforeDate);

            public Task<MembershipTableData> ReadRow(SiloAddress key) => inner.ReadRow(key);

            public Task<MembershipTableData> ReadAll() =>
                ReadAllOverride?.Invoke() ?? inner.ReadAll();

            public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) =>
                inner.InsertRow(entry, tableVersion);

            public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) =>
                inner.UpdateRow(entry, etag, tableVersion);

            public Task UpdateIAmAlive(MembershipEntry entry) => inner.UpdateIAmAlive(entry);
        }

        private static MembershipTableSnapshot Snapshot(MembershipVersion version, params MembershipEntry[] entries)
        {
            return new MembershipTableSnapshot(version, entries.ToImmutableDictionary(entry => entry.SiloAddress));
        }

        private async Task<MembershipEvents.SuspectOrKillRequestCompleted> WaitForSuspectOrKillCompletion(
            DiagnosticEventCollector membershipEvents,
            SiloAddress silo,
            CancellationToken cancellationToken)
        {
            var completions = await WaitForSuspectOrKillCompletions(
                membershipEvents,
                silo,
                expectedCount: 1,
                cancellationToken: cancellationToken);
            return completions[0];
        }

        private async Task<List<MembershipEvents.SuspectOrKillRequestCompleted>> WaitForSuspectOrKillCompletions(
            DiagnosticEventCollector membershipEvents,
            SiloAddress silo,
            int expectedCount,
            CancellationToken cancellationToken)
        {
            Assert.True(expectedCount > 0);

            membershipEvents.Clear();
            var completions = new List<MembershipEvents.SuspectOrKillRequestCompleted>(expectedCount);

            while (completions.Count < expectedCount)
            {
                var diagnosticEvent = await membershipEvents.WaitForEventAsync(
                    nameof(MembershipEvents.SuspectOrKillRequestCompleted),
                    evt => evt.Payload is MembershipEvents.SuspectOrKillRequestCompleted completed
                        && completed.ObserverSiloAddress.Equals(this.localSilo)
                        && completed.RequestType == MembershipEvents.SuspectOrKillRequestType.SuspectOrKill
                        && completed.SiloAddress.Equals(silo)
                        && !completions.Contains(completed),
                    TimeSpan.FromSeconds(40),
                    cancellationToken);

                completions.Add(Assert.IsType<MembershipEvents.SuspectOrKillRequestCompleted>(diagnosticEvent.Payload));
            }

            return completions;
        }

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTimeOffset iAmAliveTime)
        {
            return new MembershipEntry { SiloAddress = address, Status = status, IAmAliveTime = iAmAliveTime.UtcDateTime, StartTime = iAmAliveTime.UtcDateTime };
        }
    }
}
