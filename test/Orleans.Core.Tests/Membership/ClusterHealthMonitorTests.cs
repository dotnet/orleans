using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Messaging;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Messaging;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for Orleans' cluster health monitoring system, which is responsible for detecting failed silos
    /// and maintaining cluster membership consistency. The health monitor performs periodic probes of other silos
    /// and uses voting mechanisms to declare silos as dead, preventing split-brain scenarios in the distributed system.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class ClusterHealthMonitorTests
    {
        private readonly ITestOutputHelper output;
        private readonly LoggerFactory loggerFactory;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly SiloAddress localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly IMembershipGossiper membershipGossiper;
        private readonly SiloLifecycleSubject lifecycle;
        private readonly List<DelegateAsyncTimer> timers;
        private readonly ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)> timerCalls;
        private readonly DelegateAsyncTimerFactory timerFactory;
        private readonly ILocalSiloHealthMonitor localSiloHealthMonitor;
        private readonly InMemoryMembershipTable membershipTable;
        private readonly IRemoteSiloProber prober;
        private readonly ConnectionManager connectionManager;

        public ClusterHealthMonitorTests(ITestOutputHelper output)
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
            this.timers = new List<DelegateAsyncTimer>();
            this.timerCalls = new ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>();
            this.timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var t = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var task = new TaskCompletionSource<bool>();
                            this.timerCalls.Enqueue((overridePeriod, task));
                            return task.Task;
                        });
                    this.timers.Add(t);
                    return t;
                });

            this.localSiloHealthMonitor = Substitute.For<ILocalSiloHealthMonitor>();
            this.localSiloHealthMonitor.GetLocalHealthStatus(default, default, default).ReturnsForAnyArgs(new LocalSiloHealthStatus(0, []));

            this.prober = Substitute.For<IRemoteSiloProber>();
            this.membershipTable = new InMemoryMembershipTable(new TableVersion(1, "1"));
            this.connectionManager = new ConnectionManager(
                Options.Create(new ConnectionOptions()),
                null!,
                this.loggerFactory.CreateLogger<ConnectionManager>());
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_BasicScenario()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(
                enableIndirectProbes: true,
                numVotesForDeathDeclaration: 2,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests that when silos are stale, they are monitored by all other silos.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_MonitorAllStaleSilos()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(
                enableIndirectProbes: true,
                numVotesForDeathDeclaration: 2,
                otherSilosAreStale: true,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task ClusterHealthMonitor_JoiningSiloMonitorsStaleAndSuspectedEvictableSilos()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var clusterMembershipOptions = new ClusterMembershipOptions
            {
                NumProbedSilos = 3,
            };

            var testRig = CreateClusterHealthMonitorTestRig(clusterMembershipOptions);
            await this.lifecycle.OnStart(cancellationToken);

            var staleSilo = Silo("127.0.0.200:100@100");
            var freshSilo = Silo("127.0.0.200:200@100");
            var suspectedSilo = Silo("127.0.0.200:300@100");
            var shuttingDownSilo = Silo("127.0.0.200:500@100");
            var suspectedEntry = Entry(suspectedSilo, SiloStatus.Active, now);
            suspectedEntry.AddSuspector(Silo("127.0.0.200:400@100"), now.UtcDateTime);

            var otherSilos = new[]
            {
                Entry(staleSilo, SiloStatus.Active, now.Subtract(TimeSpan.FromHours(1))),
                Entry(freshSilo, SiloStatus.Active, now),
                suspectedEntry,
                Entry(shuttingDownSilo, SiloStatus.ShuttingDown, now.Subtract(TimeSpan.FromHours(1)))
            };

            var lastVersion = testRig.TestAccessor.ObservedVersion;
            foreach (var entry in otherSilos)
            {
                var table = await this.membershipTable.ReadAll();
                Assert.True(await this.membershipTable.InsertRow(entry, table.Version.Next()));
            }

            await testRig.Manager.Refresh(cancellationToken: cancellationToken);
            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion, cancellationToken);
            Assert.Empty(testRig.TestAccessor.MonitoredSilos);

            lastVersion = testRig.TestAccessor.ObservedVersion;
            await testRig.Manager.UpdateStatus(SiloStatus.Joining);
            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion, cancellationToken);
            await Until(() => testRig.TestAccessor.MonitoredSilos.Count == 3, cancellationToken);

            Assert.Contains(testRig.TestAccessor.MonitoredSilos, pair => pair.Key.Equals(staleSilo));
            Assert.Contains(testRig.TestAccessor.MonitoredSilos, pair => pair.Key.Equals(suspectedSilo));
            Assert.Contains(testRig.TestAccessor.MonitoredSilos, pair => pair.Key.Equals(shuttingDownSilo));
            Assert.DoesNotContain(testRig.TestAccessor.MonitoredSilos, pair => pair.Key.Equals(freshSilo));

            await StopLifecycle(cancellationToken);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with indirect probes disabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_NoIndirectProbes()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(
                enableIndirectProbes: false,
                numVotesForDeathDeclaration: 2,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with indirect probes disabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_ThreeVotesNeededToKill()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(
                enableIndirectProbes: true,
                numVotesForDeathDeclaration: 3,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with indirect probes disabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_OneVoteNeededToKill()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(
                enableIndirectProbes: false,
                numVotesForDeathDeclaration: 1,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_OneVoteNeededToKill()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(
                evictWhenMaxJoinAttemptTimeExceeded: true,
                numVotesForDeathDeclaration: 1,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_TwoVotesNeededToKill()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(
                evictWhenMaxJoinAttemptTimeExceeded: true,
                numVotesForDeathDeclaration: 2,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_ThreeVotesNeededToKill()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(
                evictWhenMaxJoinAttemptTimeExceeded: true,
                numVotesForDeathDeclaration: 3,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_Disabled()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(
                evictWhenMaxJoinAttemptTimeExceeded: false,
                numVotesForDeathDeclaration: 3,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests that when an active connection has recently received messages from the target silo,
        /// the vote to suspect/kill is suppressed even though probes are failing.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_ConnectionCanary_SuppressesVoteWhenConnectionActive()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var clusterMembershipOptions = new ClusterMembershipOptions
            {
                EnableIndirectProbes = false,
                NumProbedSilos = 1,
                NumVotesForDeathDeclaration = 1,
            };

            var canaryConnectionManager = new ConnectionManager(
                Options.Create(new ConnectionOptions()),
                null!,
                this.loggerFactory.CreateLogger<ConnectionManager>());

            var testRig = CreateClusterHealthMonitorTestRig(clusterMembershipOptions, canaryConnectionManager);

            // Set up probes to always fail.
            var probeCalls = new ConcurrentQueue<SiloAddress>();
            this.prober.Probe(default!, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue(info.ArgAt<SiloAddress>(0));
                return Task.FromException(new Exception("probe failed"));
            });

            await this.lifecycle.OnStart(cancellationToken);

            var targetSilo = Silo("127.0.0.200:100@100");
            await this.membershipTable.InsertRow(Entry(targetSilo, SiloStatus.Active, now), this.membershipTable.Version.Next());
            await testRig.Manager.Refresh(cancellationToken: cancellationToken);
            await testRig.Manager.UpdateStatus(SiloStatus.Active);
            await testRig.Manager.Refresh(cancellationToken: cancellationToken);

            await Until(() => testRig.TestAccessor.MonitoredSilos.Count > 0, cancellationToken);

            // Register a test connection and simulate recent message activity.
            var testConnection = CreateTestConnection(this.loggerFactory);
            canaryConnectionManager.OnConnected(targetSilo, testConnection);
            testConnection.SimulateMessageReceived();

            // Drive enough probe failures to normally trigger a vote.
            for (var i = 0; i < clusterMembershipOptions.NumMissedProbesLimit + 1; i++)
            {
                if (this.timerCalls.TryDequeue(out var timer))
                {
                    timer.Completion.TrySetResult(true);
                }

                // Keep re-stamping the canary so it stays fresh.
                testConnection.SimulateMessageReceived();
                await Task.Delay(50, cancellationToken);
            }

            await Until(() => probeCalls.Count >= clusterMembershipOptions.NumMissedProbesLimit, cancellationToken);
            await Task.Delay(100, cancellationToken);

            // The silo should NOT be dead because the canary detected active connection traffic.
            var table = await this.membershipTable.ReadAll();
            var entry = table.Members.SingleOrDefault(m => m.Item1.SiloAddress.Equals(targetSilo));
            Assert.NotNull(entry);
            Assert.NotEqual(SiloStatus.Dead, entry.Item1.Status);

            await StopLifecycle(cancellationToken);
        }

        /// <summary>
        /// Tests that when no connections exist to a target silo, the canary does not interfere
        /// and the silo is declared dead normally after probe failures.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_ConnectionCanary_AllowsVoteWhenNoConnection()
        {
            // With no connections in the connection manager, canary returns null -> vote proceeds.
            await ClusterHealthMonitor_BasicScenario_Runner(
                enableIndirectProbes: false,
                numVotesForDeathDeclaration: 1,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Tests that when <see cref="ClusterMembershipOptions.EnableConnectionLivenessCheck"/> is disabled,
        /// votes proceed normally even when an active connection exists.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_ConnectionCanary_DisabledByOption()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            var clusterMembershipOptions = new ClusterMembershipOptions
            {
                EnableIndirectProbes = false,
                NumProbedSilos = 1,
                NumVotesForDeathDeclaration = 1,
                EnableConnectionLivenessCheck = false,
            };

            var canaryConnectionManager = new ConnectionManager(
                Options.Create(new ConnectionOptions()),
                null!,
                this.loggerFactory.CreateLogger<ConnectionManager>());

            var testRig = CreateClusterHealthMonitorTestRig(clusterMembershipOptions, canaryConnectionManager);

            var probeCalls = new ConcurrentQueue<SiloAddress>();
            this.prober.Probe(default!, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue(info.ArgAt<SiloAddress>(0));
                return Task.FromException(new Exception("probe failed"));
            });

            await this.lifecycle.OnStart(cancellationToken);

            var targetSilo = Silo("127.0.0.200:100@100");
            await this.membershipTable.InsertRow(Entry(targetSilo, SiloStatus.Active, now), this.membershipTable.Version.Next());
            await testRig.Manager.Refresh(cancellationToken: cancellationToken);
            await testRig.Manager.UpdateStatus(SiloStatus.Active);
            await testRig.Manager.Refresh(cancellationToken: cancellationToken);

            await Until(() => testRig.TestAccessor.MonitoredSilos.Count > 0, cancellationToken);

            // Register a test connection and simulate recent message activity.
            var testConnection = CreateTestConnection(this.loggerFactory);
            canaryConnectionManager.OnConnected(targetSilo, testConnection);
            testConnection.SimulateMessageReceived();

            // Drive enough probe failures to trigger a vote.
            for (var i = 0; i < clusterMembershipOptions.NumMissedProbesLimit + 1; i++)
            {
                if (this.timerCalls.TryDequeue(out var timer))
                {
                    timer.Completion.TrySetResult(true);
                }

                testConnection.SimulateMessageReceived();
                await Task.Delay(50, cancellationToken);
            }

            await Until(async () =>
            {
                var snapshot = await this.membershipTable.ReadAll();
                return snapshot.Members.Any(m => m.Item1.SiloAddress.Equals(targetSilo) && m.Item1.Status == SiloStatus.Dead);
            }, cancellationToken);

            // Despite an active connection, the silo SHOULD be dead because the option is disabled.
            var table = await this.membershipTable.ReadAll();
            var entry = table.Members.SingleOrDefault(m => m.Item1.SiloAddress.Equals(targetSilo));
            Assert.NotNull(entry);
            Assert.Equal(SiloStatus.Dead, entry.Item1.Status);

            await StopLifecycle(cancellationToken);
        }

        private async Task ClusterHealthMonitor_BasicScenario_Runner(
            bool enableIndirectProbes,
            int? numVotesForDeathDeclaration,
            CancellationToken cancellationToken,
            bool otherSilosAreStale = false)
        {
            var now = DateTimeOffset.UtcNow;
            var clusterMembershipOptions = new ClusterMembershipOptions
            {
                EnableIndirectProbes = enableIndirectProbes,
                NumProbedSilos = 3,
            };

            if (numVotesForDeathDeclaration.HasValue)
            {
                clusterMembershipOptions.NumVotesForDeathDeclaration = numVotesForDeathDeclaration.Value;
            }

            var testRig = CreateClusterHealthMonitorTestRig(clusterMembershipOptions);
            using var membershipEvents = new DiagnosticEventCollector(MembershipEvents.ListenerName);
            var probeCalls = new ConcurrentQueue<(SiloAddress Target, int ProbeNumber, bool IsIndirect)>();
            this.prober.Probe(default!, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1), false));
                return Task.CompletedTask;
            });
            this.prober.ProbeIndirectly(default!, default!, default, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(1), info.ArgAt<int>(3), true));
                return Task.FromResult(new IndirectProbeResponse
                {
                    IntermediaryHealthScore = 0,
                    ProbeResponseTime = TimeSpan.FromMilliseconds(1),
                    Succeeded = true
                });
            });

            await this.lifecycle.OnStart(cancellationToken);
            Assert.Empty(testRig.TestAccessor.MonitoredSilos);

            var iAmAliveTime = otherSilosAreStale ? now.Subtract(TimeSpan.FromHours(1)) : now;
            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.200:100@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:200@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:300@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:400@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:500@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:600@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:700@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:800@100"), SiloStatus.Active, iAmAliveTime),
                Entry(Silo("127.0.0.200:900@100"), SiloStatus.Active, iAmAliveTime)
            };

            var lastVersion = testRig.TestAccessor.ObservedVersion;

            // Add the new silos
            var table = await this.membershipTable.ReadAll();
            foreach (var entry in otherSilos)
            {
                table = await this.membershipTable.ReadAll();
                Assert.True(await this.membershipTable.InsertRow(entry, table.Version.Next()));
            }

            await testRig.Manager.Refresh(cancellationToken: cancellationToken);

            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion, cancellationToken);
            lastVersion = testRig.TestAccessor.ObservedVersion;

            // No silos should be monitored by this silo until it becomes active.
            Assert.Empty(testRig.TestAccessor.MonitoredSilos);

            await testRig.Manager.UpdateStatus(SiloStatus.Active);

            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion, cancellationToken);
            lastVersion = testRig.TestAccessor.ObservedVersion;

            // Now that this silo is active, it should be monitoring some fraction of the other active silos
            await Until(() => testRig.TestAccessor.MonitoredSilos.Count > 0, cancellationToken);
            Assert.NotEmpty(this.timers);
            Assert.DoesNotContain(testRig.TestAccessor.MonitoredSilos, s => s.Key.Equals(this.localSilo));
            var expectedNumProbedSilos = otherSilosAreStale ? otherSilos.Length : clusterMembershipOptions.NumProbedSilos;
            Assert.Equal(expectedNumProbedSilos, testRig.TestAccessor.MonitoredSilos.Count);
            Assert.All(testRig.TestAccessor.MonitoredSilos, m => m.Key.Equals(m.Value.TargetSiloAddress));
            Assert.Empty(probeCalls);

            // Check that those silos are actually being probed periodically
            await UntilEqual(expectedNumProbedSilos, () =>
            {
                if (this.timerCalls.TryDequeue(out var timer))
                {
                    timer.Completion.TrySetResult(true);
                }

                return probeCalls.Count;
            }, cancellationToken);
            Assert.Equal(expectedNumProbedSilos, probeCalls.Count);
            while (probeCalls.TryDequeue(out var call)) Assert.Contains(testRig.TestAccessor.MonitoredSilos, k => k.Key.Equals(call.Item1));

            var monitoredSilos = testRig.TestAccessor.MonitoredSilos.Values.ToList();
            foreach (var siloMonitor in monitoredSilos)
            {
                Assert.Equal(0, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);
            }

            // Make the probes fail.
            this.prober.Probe(default!, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1), true));
                return Task.FromException(new Exception("no"));
            });
            this.prober.ProbeIndirectly(default!, default!, default, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(1), info.ArgAt<int>(3), true));
                return Task.FromResult(new IndirectProbeResponse
                {
                    FailureMessage = "We failed the probe on purpose, as a joke",
                    IntermediaryHealthScore = 0,
                    ProbeResponseTime = TimeSpan.FromSeconds(1),
                    Succeeded = false
                });
            });

            // The above call to specify the probe behaviour also enqueued a value, so clear it here.
            while (probeCalls.TryDequeue(out _)) ;

            for (var expectedMissedProbes = 1; expectedMissedProbes <= clusterMembershipOptions.NumMissedProbesLimit; expectedMissedProbes++)
            {
                this.membershipTable.ClearCalls();

                // Wait for probes to be fired
                await UntilEqual(expectedNumProbedSilos, () =>
                {
                    if (this.timerCalls.TryDequeue(out var timer))
                    {
                        timer.Completion.TrySetResult(true);
                    }

                    return probeCalls.Count;
                }, cancellationToken);

                while (probeCalls.TryDequeue(out var call)) ;

                if (expectedMissedProbes >= clusterMembershipOptions.NumMissedProbesLimit)
                {
                    var expectDead = (clusterMembershipOptions.NumVotesForDeathDeclaration <= 2 && enableIndirectProbes) || numVotesForDeathDeclaration == 1;
                    await WaitForMembershipSnapshot(membershipEvents, snapshot =>
                    {
                        return monitoredSilos.All(siloMonitor =>
                        {
                            if (!snapshot.Entries.TryGetValue(siloMonitor.TargetSiloAddress, out var entry))
                            {
                                return false;
                            }

                            var votes = entry.GetFreshVotes(now.UtcDateTime, clusterMembershipOptions.DeathVoteExpirationTimeout);
                            return votes.Any(vote => vote.Item1.Equals(localSiloDetails.SiloAddress)) && (!expectDead || entry.Status == SiloStatus.Dead);
                        });
                    }, cancellationToken);
                }

                // Check that probes match the expected missed probes
                table = await this.membershipTable.ReadAll();
                foreach (var siloMonitor in monitoredSilos)
                {
                    Assert.Equal(expectedMissedProbes, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);

                    var entry = table.Members.Single(m => m.Item1.SiloAddress.Equals(siloMonitor.TargetSiloAddress)).Item1;
                    var votes = entry.GetFreshVotes(now.UtcDateTime, clusterMembershipOptions.DeathVoteExpirationTimeout);
                    if (expectedMissedProbes < clusterMembershipOptions.NumMissedProbesLimit)
                    {
                        Assert.Empty(votes);
                    }
                    else
                    {
                        // After a certain number of failures, a vote should be added to the table.
                        Assert.Contains(votes, vote => vote.Item1.Equals(localSiloDetails.SiloAddress));
                        if (clusterMembershipOptions.NumVotesForDeathDeclaration <= 2 && enableIndirectProbes || numVotesForDeathDeclaration == 1)
                        {
                            Assert.Equal(SiloStatus.Dead, entry.Status);
                        }
                    }
                }
            }

            if (enableIndirectProbes && numVotesForDeathDeclaration <= 2 || numVotesForDeathDeclaration == 1)
            {
                table = await this.membershipTable.ReadAll();
                Assert.Equal(expectedNumProbedSilos, table.Members.Count(m => m.Item1.Status == SiloStatus.Dead));

                // There is no more to test here, since all of the monitored silos have been killed.
                return;
            }

            await testRig.Manager.Refresh(cancellationToken: cancellationToken);

            // Make the probes succeed again.
            this.prober.Probe(default!, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1), false));
                return Task.CompletedTask;
            });
            this.prober.ProbeIndirectly(default!, default!, default, default, cancellationToken).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(1), info.ArgAt<int>(3), true));
                return Task.FromResult(new IndirectProbeResponse
                {
                    IntermediaryHealthScore = 0,
                    ProbeResponseTime = TimeSpan.FromMilliseconds(1),
                    Succeeded = true
                });
            });

            // The above call to specify the probe behaviour also enqueued a value, so clear it here.
            while (probeCalls.TryDequeue(out _)) ;

            // Wait for probes to be fired
            this.output.WriteLine($"Firing probes for silos: {string.Join(", ", testRig.TestAccessor.MonitoredSilos.Keys)}");
            var probesReceived = new HashSet<SiloAddress>();
            await UntilEqual(testRig.TestAccessor.MonitoredSilos.Count, () =>
            {
                if (this.timerCalls.TryDequeue(out var timer))
                {
                    timer.Completion.TrySetResult(true);
                }

                while (probeCalls.TryDequeue(out var call))
                {
                    probesReceived.Add(call.Target);
                }

                return probesReceived.Count;
            }, cancellationToken);

            foreach (var siloMonitor in testRig.TestAccessor.MonitoredSilos.Values)
            {
                this.output.WriteLine($"Checking missed probes on {siloMonitor.TargetSiloAddress}: {((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes}");
                Assert.Equal(0, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);
            }

            await StopLifecycle(cancellationToken);
        }

        private async Task ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(
            bool evictWhenMaxJoinAttemptTimeExceeded,
            int? numVotesForDeathDeclaration,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var clusterMembershipOptions = new ClusterMembershipOptions
            {
                EvictWhenMaxJoinAttemptTimeExceeded = evictWhenMaxJoinAttemptTimeExceeded
            };

            if (numVotesForDeathDeclaration.HasValue)
            {
                clusterMembershipOptions.NumVotesForDeathDeclaration = numVotesForDeathDeclaration.Value;
            }

            var testRig = CreateClusterHealthMonitorTestRig(clusterMembershipOptions);
            using var membershipEvents = new DiagnosticEventCollector(MembershipEvents.ListenerName);

            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.200:100@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:200@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:300@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:400@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:500@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:600@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:700@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:800@100"), SiloStatus.Active, now),
                Entry(Silo("127.0.0.200:900@100"), SiloStatus.Active, now)
            };

            var joiningSilo = "127.0.0.200:111@100";
            var createdSilo = "127.0.0.200:112@100";

            // default MaxJoinAttemptTime is 5 minutes, setting it to 6 minutes ago will make sure they are flagged immediately
            var staleCreatedOrJoiningSilos = new[]
            {
                Entry(Silo(joiningSilo), SiloStatus.Joining, DateTime.UtcNow.AddMinutes(-6)),
                Entry(Silo(createdSilo), SiloStatus.Created, DateTime.UtcNow.AddMinutes(-6)),
            };

            otherSilos = [.. otherSilos, .. staleCreatedOrJoiningSilos];

            var lastVersion = testRig.TestAccessor.ObservedVersion;

            // Add the new silos
            var table = await this.membershipTable.ReadAll();
            foreach (var entry in otherSilos)
            {
                table = await this.membershipTable.ReadAll();
                Assert.True(await this.membershipTable.InsertRow(entry, table.Version.Next()));
            }

            table = await this.membershipTable.ReadAll();
            var joiningEntry = GetEntryFromTable(table, joiningSilo);
            var createdEntry = GetEntryFromTable(table, createdSilo);

            Assert.NotNull(joiningEntry);
            Assert.NotNull(createdEntry);

            Assert.Equal(expected: SiloStatus.Joining, actual: joiningEntry.Item1.Status);
            Assert.Equal(expected: SiloStatus.Created, actual: createdEntry.Item1.Status);

            // We are going to add numVotesForDeathDeclaration - 1 votes to the created or joining silos
            var totalRequiredVotes = clusterMembershipOptions.NumVotesForDeathDeclaration;

            var votesNeeded = totalRequiredVotes - 1;

            // the joining and created silos should not be declared dead until the required number of votes.
            while (votesNeeded > 0)
            {
                table = await this.membershipTable.ReadAll();
                joiningEntry = GetEntryFromTable(table, joiningSilo);
                Assert.NotNull(joiningEntry);
                joiningEntry.Item1.AddSuspector(otherSilos[0].SiloAddress, DateTime.UtcNow);
                Assert.True(await this.membershipTable.UpdateRow(joiningEntry.Item1, joiningEntry.Item2, table.Version.Next()));

                table = await this.membershipTable.ReadAll();
                createdEntry = GetEntryFromTable(table, createdSilo);
                Assert.NotNull(createdEntry);
                createdEntry.Item1.AddSuspector(otherSilos[0].SiloAddress, DateTime.UtcNow);
                Assert.True(await this.membershipTable.UpdateRow(createdEntry.Item1, createdEntry.Item2, table.Version.Next()));

                votesNeeded--;
            }

            table = await this.membershipTable.ReadAll();
            joiningEntry = GetEntryFromTable(table, joiningSilo);
            createdEntry = GetEntryFromTable(table, createdSilo);
            Assert.NotNull(joiningEntry);
            Assert.NotNull(createdEntry);

            // Suspect time will be null if numVotesForDeathDeclaration == 1
            if (totalRequiredVotes > 1 && evictWhenMaxJoinAttemptTimeExceeded)
            {
                var initialJoiningSuspectTimes = joiningEntry.Item1.SuspectTimes;
                var initialCreatedSuspectTimes = createdEntry.Item1.SuspectTimes;
                Assert.NotNull(initialJoiningSuspectTimes);
                Assert.NotNull(initialCreatedSuspectTimes);
                Assert.Equal(totalRequiredVotes - 1, initialJoiningSuspectTimes.Count);
                Assert.Equal(totalRequiredVotes - 1, initialCreatedSuspectTimes.Count);
            }

            // now we start the lifecycle and let the local silo add the final vote.
            await this.lifecycle.OnStart(cancellationToken);

            await testRig.Manager.Refresh(cancellationToken: cancellationToken);

            if (evictWhenMaxJoinAttemptTimeExceeded)
            {
                await WaitForMembershipSnapshot(membershipEvents, snapshot =>
                {
                    return snapshot.Entries.TryGetValue(Silo(joiningSilo), out var joining)
                        && joining.Status == SiloStatus.Dead
                        && snapshot.Entries.TryGetValue(Silo(createdSilo), out var created)
                        && created.Status == SiloStatus.Dead;
                }, cancellationToken);
            }

            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion, cancellationToken);

            lastVersion = testRig.TestAccessor.ObservedVersion;

            table = await this.membershipTable.ReadAll();
            joiningEntry = GetEntryFromTable(table, joiningSilo);
            createdEntry = GetEntryFromTable(table, createdSilo);
            Assert.NotNull(joiningEntry);
            Assert.NotNull(createdEntry);

            var expectedVotes = totalRequiredVotes == 1
                ? 2
                : totalRequiredVotes;

            expectedVotes = evictWhenMaxJoinAttemptTimeExceeded
                ? totalRequiredVotes
                : totalRequiredVotes - 1;

            var joiningSuspectTimes = joiningEntry.Item1.SuspectTimes;
            var createdSuspectTimes = createdEntry.Item1.SuspectTimes;
            Assert.NotNull(joiningSuspectTimes);
            Assert.NotNull(createdSuspectTimes);
            Assert.True(expectedVotes <= joiningSuspectTimes.Count);
            Assert.True(expectedVotes <= createdSuspectTimes.Count);

            Assert.Equal(expected: evictWhenMaxJoinAttemptTimeExceeded ? SiloStatus.Dead : SiloStatus.Joining, actual: joiningEntry.Item1.Status);
            Assert.Equal(expected: evictWhenMaxJoinAttemptTimeExceeded ? SiloStatus.Dead : SiloStatus.Created, actual: createdEntry.Item1.Status);

            await StopLifecycle(cancellationToken);

            static Tuple<MembershipEntry, string>? GetEntryFromTable(MembershipTableData table, string siloAddress)
            {
                return table.Members.FirstOrDefault(entry => entry.Item1.SiloAddress.ToParsableString() == siloAddress);
            }
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTimeOffset startTime = default) => new MembershipEntry { SiloAddress = address, Status = status, StartTime = startTime.UtcDateTime, IAmAliveTime = startTime.UtcDateTime };

        private static async Task UntilEqual<T>(
            T expected,
            Func<T> getActual,
            CancellationToken cancellationToken)
        {
            var maxTimeout = 40_000;
            var equalityComparer = EqualityComparer<T>.Default;
            var actual = getActual();
            while (!equalityComparer.Equals(expected, actual) && (maxTimeout -= 10) > 0)
            {
                await Task.Delay(10, cancellationToken);
                actual = getActual();
            }

            Assert.Equal(expected, actual);
            Assert.True(maxTimeout > 0);
        }

        private static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
        {
            var maxTimeout = 40_000;
            while (!condition() && (maxTimeout -= 10) > 0) await Task.Delay(10, cancellationToken);
            Assert.True(maxTimeout > 0);
        }

        private static async Task Until(
            Func<Task<bool>> condition,
            CancellationToken cancellationToken)
        {
            var maxTimeout = 40_000;
            while (!await condition() && (maxTimeout -= 10) > 0)
            {
                await Task.Delay(10, cancellationToken);
            }

            Assert.True(maxTimeout > 0);
        }

        private static async Task<MembershipTableSnapshot> WaitForMembershipSnapshot(
            DiagnosticEventCollector membershipEvents,
            Func<MembershipTableSnapshot, bool> condition,
            CancellationToken cancellationToken)
        {
            var diagnosticEvent = await membershipEvents.WaitForEventAsync(
                nameof(MembershipEvents.ViewChanged),
                evt => evt.Payload is MembershipEvents.ViewChanged viewChanged && condition(viewChanged.Snapshot),
                TimeSpan.FromSeconds(40),
                cancellationToken);

            return Assert.IsType<MembershipEvents.ViewChanged>(diagnosticEvent.Payload).Snapshot;
        }

        private async Task StopLifecycle(CancellationToken cancellationToken)
        {
            var stopped = this.lifecycle.OnStop(cancellationToken);

            while (!stopped.IsCompleted)
            {
                while (this.timerCalls.TryDequeue(out var call)) call.Completion.TrySetResult(false);
                await Task.Delay(15, cancellationToken);
            }

            await stopped;
        }

        private class ClusterHealthMonitorTestRig(
            MembershipTableManager manager,
            IOptionsMonitor<ClusterMembershipOptions> optionsMonitor,
            ClusterHealthMonitor.ITestAccessor testAccessor)
        {
            public readonly MembershipTableManager Manager = manager;
            public readonly IOptionsMonitor<ClusterMembershipOptions> OptionsMonitor = optionsMonitor;
            public readonly ClusterHealthMonitor.ITestAccessor TestAccessor = testAccessor;
        }

        private ClusterHealthMonitorTestRig CreateClusterHealthMonitorTestRig(ClusterMembershipOptions clusterMembershipOptions)
        {
            return CreateClusterHealthMonitorTestRig(clusterMembershipOptions, this.connectionManager);
        }

        private ClusterHealthMonitorTestRig CreateClusterHealthMonitorTestRig(ClusterMembershipOptions clusterMembershipOptions, ConnectionManager connManager)
        {
            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(clusterMembershipOptions),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                this.lifecycle,
                timeProvider: TimeProvider.System);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            var optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            optionsMonitor.CurrentValue.ReturnsForAnyArgs(clusterMembershipOptions);

            var monitor = new ClusterHealthMonitor(
                this.localSiloDetails,
                manager,
                this.loggerFactory.CreateLogger<ClusterHealthMonitor>(),
                optionsMonitor,
                this.fatalErrorHandler,
                null!,
                connManager,
                TimeProvider.System);

            ((ILifecycleParticipant<ISiloLifecycle>)monitor).Participate(this.lifecycle);

            var testAccessor = (ClusterHealthMonitor.ITestAccessor)monitor;
            testAccessor.CreateMonitor = s => new SiloHealthMonitor(
                s,
                testAccessor.OnProbeResult,
                optionsMonitor,
                this.loggerFactory,
                this.prober,
                this.timerFactory,
                this.localSiloHealthMonitor,
                manager,
                this.localSiloDetails,
                TimeProvider.System);

            return new(
                manager: manager,
                optionsMonitor: optionsMonitor,
                testAccessor: testAccessor);
        }

        /// <summary>
        /// Creates a minimal test <see cref="Connection"/> suitable for registering with <see cref="ConnectionManager"/>.
        /// </summary>
        private TestConnection CreateTestConnection(ILoggerFactory loggerFactory)
        {
            var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
            var context = Substitute.For<ConnectionContext>();
            context.Features.Returns(features);
            ConnectionDelegate middleware = _ => Task.CompletedTask;
            var services = new ServiceCollection();
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<MessagingInstruments>();
            services.AddSingleton<MessagingProcessingInstruments>();
            var serviceProvider = services.BuildServiceProvider();
            var orleansInstruments = serviceProvider.GetRequiredService<OrleansInstruments>();
            var messagingInstruments = serviceProvider.GetRequiredService<MessagingInstruments>();
            var messagingTrace = new MessagingTrace(
                loggerFactory,
                messagingInstruments,
                serviceProvider.GetRequiredService<MessagingProcessingInstruments>());
            var shared = new ConnectionCommon(
                serviceProvider,
                null!,
                messagingTrace,
                orleansInstruments,
                messagingInstruments,
                loggerFactory.CreateLogger<Connection>(),
                new NoOpMessageStatisticsSink());
            return new TestConnection(context, middleware, shared);
        }

        [Fact]
        public void Connection_PartialReadMarksReceiveActivity()
        {
            var connection = CreateTestConnection(this.loggerFactory);

            Assert.False(connection.SimulateBytesReceived(availableBytes: 0, retainedBytes: 0));
            Assert.Null(connection.ElapsedSinceLastMessageReceived);

            Assert.True(connection.SimulateBytesReceived(availableBytes: 1, retainedBytes: 0));
            Assert.NotNull(connection.ElapsedSinceLastMessageReceived);

            Assert.False(connection.SimulateBytesReceived(availableBytes: 1, retainedBytes: 1));
        }

        private sealed class TestConnection(ConnectionContext context, ConnectionDelegate middleware, ConnectionCommon shared)
            : Connection(context, middleware, shared)
        {
            protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
            protected override IMessageCenter MessageCenter => null!;
            protected override bool PrepareMessageForSend(Message msg) => true;
            protected override void OnReceivedMessage(Message msg) { }
            protected override void RecordMessageReceive(Message msg, int numTotalBytes, int headerBytes) { }
            protected override void RecordMessageSend(Message msg, int numTotalBytes, int headerBytes) { }
            protected override void OnSendMessageFailure(Message message, string error) { }
            protected override void RetryMessage(Message msg, Exception? ex = null) { }
            public void SimulateMessageReceived() => MarkMessageReceived();
            public bool SimulateBytesReceived(long availableBytes, long retainedBytes) => MarkBytesReceived(availableBytes, retainedBytes);
        }

        [Fact]
        public async Task ClusterHealthMonitor_StaleJoinEvictionUsesInjectedTimeProvider()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var maxJoinAttemptTime = TimeSpan.FromMinutes(5);
            var timeProvider = new FakeTimeProvider(start);
            var updates = new ControlledMembershipUpdateStream();
            var manager = Substitute.For<IMembershipManager>();
            manager.CurrentSnapshot.Returns(new MembershipTableSnapshot(
                MembershipVersion.MinValue,
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty));
            manager.MembershipUpdates.Returns(updates);
            manager.TrySuspectSilo(default!, default, cancellationToken).ReturnsForAnyArgs(true);
            var options = new ClusterMembershipOptions
            {
                EvictWhenMaxJoinAttemptTimeExceeded = true,
                MaxJoinAttemptTime = maxJoinAttemptTime,
            };
            var optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            optionsMonitor.CurrentValue.Returns(options);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            using var services = new ServiceCollection().BuildServiceProvider();
            await using var monitor = new ClusterHealthMonitor(
                this.localSiloDetails,
                manager,
                this.loggerFactory.CreateLogger<ClusterHealthMonitor>(),
                optionsMonitor,
                this.fatalErrorHandler,
                services,
                this.connectionManager,
                timeProvider);
            ((ILifecycleParticipant<ISiloLifecycle>)monitor).Participate(lifecycle);
            var accessor = (ClusterHealthMonitor.ITestAccessor)monitor;
            var joiningSilo = Silo("127.0.0.200:111@100");
            var joiningEntry = Entry(joiningSilo, SiloStatus.Joining, start);

            await lifecycle.OnStart(cancellationToken);
            try
            {
                await updates.WaitForReadAsync(1, cancellationToken);
                timeProvider.Advance(maxJoinAttemptTime);
                updates.Publish(CreateSnapshot(1, joiningEntry));
                await updates.WaitForReadAsync(2, cancellationToken);

                Assert.Equal(new MembershipVersion(1), accessor.ObservedVersion);
                await manager.DidNotReceive().TrySuspectSilo(
                    Arg.Any<SiloAddress>(),
                    Arg.Any<SiloAddress?>(),
                    Arg.Any<CancellationToken>());

                timeProvider.Advance(TimeSpan.FromTicks(1));
                updates.Publish(CreateSnapshot(2, joiningEntry));
                await updates.WaitForReadAsync(3, cancellationToken);

                Assert.Equal(new MembershipVersion(2), accessor.ObservedVersion);
                await manager.Received(1).TrySuspectSilo(
                    joiningSilo,
                    null,
                    Arg.Is<CancellationToken>(token => token.CanBeCanceled && !token.IsCancellationRequested));
            }
            finally
            {
                var stopTask = lifecycle.OnStop(cancellationToken);
                updates.Complete();
                await stopTask;
                await updates.Completed.WaitAsync(cancellationToken);
            }

            static MembershipTableSnapshot CreateSnapshot(long version, MembershipEntry joiningEntry)
                => new(
                    new MembershipVersion(version),
                    ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(joiningEntry.SiloAddress, joiningEntry));
        }

        private sealed class ControlledMembershipUpdateStream : IAsyncEnumerable<MembershipTableSnapshot>
        {
            private readonly Channel<MembershipTableSnapshot> _updates = Channel.CreateUnbounded<MembershipTableSnapshot>();
            private readonly Dictionary<int, TaskCompletionSource> _readWaiters = [];
            private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _lock = new();
            private int _readCount;

            public Task Completed => _completed.Task;

            public void Publish(MembershipTableSnapshot snapshot) => Assert.True(_updates.Writer.TryWrite(snapshot));

            public void Complete() => _updates.Writer.TryComplete();

            public Task WaitForReadAsync(int readCount, CancellationToken cancellationToken)
            {
                lock (_lock)
                {
                    if (_readCount >= readCount)
                    {
                        return Task.CompletedTask;
                    }

                    if (!_readWaiters.TryGetValue(readCount, out var waiter))
                    {
                        waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _readWaiters.Add(readCount, waiter);
                    }

                    return waiter.Task.WaitAsync(cancellationToken);
                }
            }

            public IAsyncEnumerator<MembershipTableSnapshot> GetAsyncEnumerator(CancellationToken cancellationToken = default)
                => new Enumerator(this, _updates.Reader, cancellationToken);

            private void OnReadStarted()
            {
                TaskCompletionSource? waiter;
                lock (_lock)
                {
                    _readCount++;
                    _readWaiters.Remove(_readCount, out waiter);
                }

                waiter?.TrySetResult();
            }

            private sealed class Enumerator(
                ControlledMembershipUpdateStream owner,
                ChannelReader<MembershipTableSnapshot> reader,
                CancellationToken cancellationToken) : IAsyncEnumerator<MembershipTableSnapshot>
            {
                public MembershipTableSnapshot Current { get; private set; } = null!;

                public async ValueTask<bool> MoveNextAsync()
                {
                    owner.OnReadStarted();
                    while (await reader.WaitToReadAsync(cancellationToken))
                    {
                        if (reader.TryRead(out var item))
                        {
                            Current = item;
                            return true;
                        }
                    }

                    return false;
                }

                public ValueTask DisposeAsync()
                {
                    owner._completed.TrySetResult();
                    return ValueTask.CompletedTask;
                }
            }
        }
    }
}
