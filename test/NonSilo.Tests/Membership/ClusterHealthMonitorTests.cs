using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
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
            this.localSiloHealthMonitor.GetLocalHealthDegradationScore(default).ReturnsForAnyArgs(0);

            this.prober = Substitute.For<IRemoteSiloProber>();
            this.membershipTable = new InMemoryMembershipTable(new TableVersion(1, "1"));
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_BasicScenario()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(enableIndirectProbes: true, numVotesForDeathDeclaration: 2);
        }

        /// <summary>
        /// Tests that when silos are stale, they are monitored by all other silos.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_MonitorAllStaleSilos()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(enableIndirectProbes: true, numVotesForDeathDeclaration: 2, otherSilosAreStale: true);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with indirect probes disabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_NoIndirectProbes()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(enableIndirectProbes: false, numVotesForDeathDeclaration: 2);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with indirect probes disabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_ThreeVotesNeededToKill()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(enableIndirectProbes: true, numVotesForDeathDeclaration: 3);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with indirect probes disabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_OneVoteNeededToKill()
        {
            await ClusterHealthMonitor_BasicScenario_Runner(enableIndirectProbes: false, numVotesForDeathDeclaration: 1);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_OneVoteNeededToKill()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(evictWhenMaxJoinAttemptTimeExceeded: true, numVotesForDeathDeclaration: 1);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_TwoVotesNeededToKill()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(evictWhenMaxJoinAttemptTimeExceeded: true, numVotesForDeathDeclaration: 2);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_ThreeVotesNeededToKill()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(evictWhenMaxJoinAttemptTimeExceeded: true, numVotesForDeathDeclaration: 3);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>, but with EvictWhenMaxJoinAttemptTimeExceeded enabled.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_SilosWithStaleCreatedOrJoiningState_Disabled()
        {
            await ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(evictWhenMaxJoinAttemptTimeExceeded: false, numVotesForDeathDeclaration: 3);
        }

        private async Task ClusterHealthMonitor_BasicScenario_Runner(bool enableIndirectProbes, int? numVotesForDeathDeclaration = default, bool otherSilosAreStale = false)
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
            var probeCalls = new ConcurrentQueue<(SiloAddress Target, int ProbeNumber, bool IsIndirect)>();
            this.prober.Probe(default, default).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1), false));
                return Task.CompletedTask;
            });
            this.prober.ProbeIndirectly(default, default, default, default).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(1), info.ArgAt<int>(3), true));
                return Task.FromResult(new IndirectProbeResponse
                {
                    IntermediaryHealthScore = 0,
                    ProbeResponseTime = TimeSpan.FromMilliseconds(1),
                    Succeeded = true
                });
            });

            await this.lifecycle.OnStart();
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

            await testRig.Manager.Refresh();

            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion);
            lastVersion = testRig.TestAccessor.ObservedVersion;

            // No silos should be monitored by this silo until it becomes active.
            Assert.Empty(testRig.TestAccessor.MonitoredSilos);

            await testRig.Manager.UpdateStatus(SiloStatus.Active);

            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion);
            lastVersion = testRig.TestAccessor.ObservedVersion;

            // Now that this silo is active, it should be monitoring some fraction of the other active silos
            await Until(() => testRig.TestAccessor.MonitoredSilos.Count > 0);
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
            });
            Assert.Equal(expectedNumProbedSilos, probeCalls.Count);
            while (probeCalls.TryDequeue(out var call)) Assert.Contains(testRig.TestAccessor.MonitoredSilos, k => k.Key.Equals(call.Item1));

            var monitoredSilos = testRig.TestAccessor.MonitoredSilos.Values.ToList();
            foreach (var siloMonitor in monitoredSilos)
            {
                Assert.Equal(0, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);
            }

            // Make the probes fail.
            this.prober.Probe(default, default).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1), true));
                return Task.FromException(new Exception("no"));
            });
            this.prober.ProbeIndirectly(default, default, default, default).ReturnsForAnyArgs(info =>
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
                });

                while (probeCalls.TryDequeue(out var call)) ;

                testRig.Manager.TestingSuspectOrKillIdle.WaitOne(TimeSpan.FromSeconds(45));
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

            await testRig.Manager.Refresh();

            // Make the probes succeed again.
            this.prober.Probe(default, default).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1), false));
                return Task.CompletedTask;
            });
            this.prober.ProbeIndirectly(default, default, default, default).ReturnsForAnyArgs(info =>
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
            });

            foreach (var siloMonitor in testRig.TestAccessor.MonitoredSilos.Values)
            {
                this.output.WriteLine($"Checking missed probes on {siloMonitor.TargetSiloAddress}: {((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes}");
                Assert.Equal(0, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);
            }

            await StopLifecycle();
        }

        private async Task ClusterHealthMonitor_StaleJoinOrCreatedSilos_Runner(bool evictWhenMaxJoinAttemptTimeExceeded = true, int? numVotesForDeathDeclaration = default)
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
                joiningEntry.Item1.AddSuspector(otherSilos[0].SiloAddress, DateTime.UtcNow);
                Assert.True(await this.membershipTable.UpdateRow(joiningEntry.Item1, joiningEntry.Item2, table.Version.Next()));

                table = await this.membershipTable.ReadAll();
                createdEntry = GetEntryFromTable(table, createdSilo);
                createdEntry.Item1.AddSuspector(otherSilos[0].SiloAddress, DateTime.UtcNow);
                Assert.True(await this.membershipTable.UpdateRow(createdEntry.Item1, createdEntry.Item2, table.Version.Next()));

                votesNeeded--;
            }

            table = await this.membershipTable.ReadAll();
            joiningEntry = GetEntryFromTable(table, joiningSilo);
            createdEntry = GetEntryFromTable(table, createdSilo);

            // Suspect time will be null if numVotesForDeathDeclaration == 1
            if (totalRequiredVotes > 1 && evictWhenMaxJoinAttemptTimeExceeded)
            {
                Assert.Equal(totalRequiredVotes - 1, joiningEntry.Item1.SuspectTimes.Count);
                Assert.Equal(totalRequiredVotes - 1, createdEntry.Item1.SuspectTimes.Count);
            }

            // now we start the lifecycle and let the local silo add the final vote.
            await this.lifecycle.OnStart();

            await testRig.Manager.Refresh();

            testRig.Manager.TestingSuspectOrKillIdle.WaitOne(TimeSpan.FromSeconds(45));
            await Until(() => testRig.TestAccessor.ObservedVersion > lastVersion);
            
            lastVersion = testRig.TestAccessor.ObservedVersion;

            table = await this.membershipTable.ReadAll();
            joiningEntry = GetEntryFromTable(table, joiningSilo);
            createdEntry = GetEntryFromTable(table, createdSilo);

            var expectedVotes = totalRequiredVotes == 1
                ? 2
                : totalRequiredVotes;

            expectedVotes = evictWhenMaxJoinAttemptTimeExceeded
                ? totalRequiredVotes
                : totalRequiredVotes - 1;

            Assert.True(expectedVotes <= joiningEntry.Item1.SuspectTimes.Count);
            Assert.True(expectedVotes <= createdEntry.Item1.SuspectTimes.Count);

            Assert.Equal(expected: evictWhenMaxJoinAttemptTimeExceeded ? SiloStatus.Dead : SiloStatus.Joining, actual: joiningEntry.Item1.Status);
            Assert.Equal(expected: evictWhenMaxJoinAttemptTimeExceeded ? SiloStatus.Dead : SiloStatus.Created, actual: createdEntry.Item1.Status);

            await StopLifecycle();

            static Tuple<MembershipEntry, string> GetEntryFromTable(MembershipTableData table, string siloAddress)
            {
                return table.Members.FirstOrDefault(entry => entry.Item1.SiloAddress.ToParsableString() == siloAddress);
            }
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTimeOffset startTime = default) => new MembershipEntry { SiloAddress = address, Status = status, StartTime = startTime.UtcDateTime, IAmAliveTime = startTime.UtcDateTime };

        private static async Task UntilEqual<T>(T expected, Func<T> getActual)
        {
            var maxTimeout = 40_000;
            var equalityComparer = EqualityComparer<T>.Default;
            var actual = getActual();
            while (!equalityComparer.Equals(expected, actual) && (maxTimeout -= 10) > 0)
            {
                await Task.Delay(10);
                actual = getActual();
            }

            Assert.Equal(expected, actual);
            Assert.True(maxTimeout > 0);
        }

        private static async Task Until(Func<bool> condition)
        {
            var maxTimeout = 40_000;
            while (!condition() && (maxTimeout -= 10) > 0) await Task.Delay(10);
            Assert.True(maxTimeout > 0);
        }

        private async Task StopLifecycle(CancellationToken cancellation = default)
        {
            var stopped = this.lifecycle.OnStop(cancellation);

            while (!stopped.IsCompleted)
            {
                while (this.timerCalls.TryDequeue(out var call)) call.Completion.TrySetResult(false);
                await Task.Delay(15);
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
            var manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(clusterMembershipOptions),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                this.lifecycle);

            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(this.lifecycle);

            var optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            optionsMonitor.CurrentValue.ReturnsForAnyArgs(clusterMembershipOptions);

            var monitor = new ClusterHealthMonitor(
                this.localSiloDetails,
                manager,
                this.loggerFactory.CreateLogger<ClusterHealthMonitor>(),
                optionsMonitor,
                this.fatalErrorHandler,
                null);

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
                this.localSiloDetails);

            return new(
                manager: manager,
                optionsMonitor: optionsMonitor,
                testAccessor: testAccessor);
        }
    }
}
