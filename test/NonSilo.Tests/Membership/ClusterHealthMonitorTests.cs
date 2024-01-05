using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
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

        private async Task ClusterHealthMonitor_BasicScenario_Runner(bool enableIndirectProbes, int? numVotesForDeathDeclaration = default)
        {
            var clusterMembershipOptions = new ClusterMembershipOptions
            {
                EnableIndirectProbes = enableIndirectProbes,
            };

            if (numVotesForDeathDeclaration.HasValue)
            {
                clusterMembershipOptions.NumVotesForDeathDeclaration = numVotesForDeathDeclaration.Value;
            }

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

            var membershipService = Substitute.For<IClusterMembershipService>();
            membershipService.CurrentSnapshot.ReturnsForAnyArgs(info => manager.MembershipTableSnapshot.CreateClusterMembershipSnapshot());
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
                membershipService,
                this.localSiloDetails);

            await this.lifecycle.OnStart();
            Assert.Empty(testAccessor.MonitoredSilos);

            var otherSilos = new[]
            {
                Entry(Silo("127.0.0.200:100@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:200@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:300@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:400@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:500@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:600@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:700@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:800@100"), SiloStatus.Active),
                Entry(Silo("127.0.0.200:900@100"), SiloStatus.Active)
            };

            var lastVersion = testAccessor.ObservedVersion;

            // Add the new silos
            var table = await this.membershipTable.ReadAll();
            foreach (var entry in otherSilos)
            {
                table = await this.membershipTable.ReadAll();
                Assert.True(await this.membershipTable.InsertRow(entry, table.Version.Next()));
            }

            await manager.Refresh();

            await Until(() => testAccessor.ObservedVersion > lastVersion);
            lastVersion = testAccessor.ObservedVersion;

            // No silos should be monitored by this silo until it becomes active.
            Assert.Empty(testAccessor.MonitoredSilos);

            await manager.UpdateStatus(SiloStatus.Active);

            await Until(() => testAccessor.ObservedVersion > lastVersion);
            lastVersion = testAccessor.ObservedVersion;

            // Now that this silo is active, it should be monitoring some fraction of the other active silos
            await Until(() => testAccessor.MonitoredSilos.Count > 0);
            Assert.NotEmpty(this.timers);
            Assert.DoesNotContain(testAccessor.MonitoredSilos, s => s.Key.Equals(this.localSilo));
            Assert.Equal(clusterMembershipOptions.NumProbedSilos, testAccessor.MonitoredSilos.Count);
            Assert.All(testAccessor.MonitoredSilos, m => m.Key.Equals(m.Value.SiloAddress));
            Assert.Empty(probeCalls);

            // Check that those silos are actually being probed periodically
            await UntilEqual(clusterMembershipOptions.NumProbedSilos, () =>
            {
                if (this.timerCalls.TryDequeue(out var timer))
                {
                    timer.Completion.TrySetResult(true);
                }

                return probeCalls.Count;
            });
            Assert.Equal(clusterMembershipOptions.NumProbedSilos, probeCalls.Count);
            while (probeCalls.TryDequeue(out var call)) Assert.Contains(testAccessor.MonitoredSilos, k => k.Key.Equals(call.Item1));

            var monitoredSilos = testAccessor.MonitoredSilos.Values.ToList();
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
                var now = DateTime.UtcNow;
                this.membershipTable.ClearCalls();

                // Wait for probes to be fired
                await UntilEqual(clusterMembershipOptions.NumProbedSilos, () =>
                {
                    if (this.timerCalls.TryDequeue(out var timer))
                    {
                        timer.Completion.TrySetResult(true);
                    }

                    return probeCalls.Count;
                });

                while (probeCalls.TryDequeue(out var call)) ;

                // Check that probes match the expected missed probes
                table = await this.membershipTable.ReadAll();
                foreach (var siloMonitor in monitoredSilos)
                {
                    Assert.Equal(expectedMissedProbes, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);

                    var entry = table.Members.Single(m => m.Item1.SiloAddress.Equals(siloMonitor.SiloAddress)).Item1;
                    var votes = entry.GetFreshVotes(now, clusterMembershipOptions.DeathVoteExpirationTimeout);
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
                Assert.Equal(clusterMembershipOptions.NumProbedSilos, table.Members.Count(m => m.Item1.Status == SiloStatus.Dead));

                // There is no more to test here, since all of the monitored silos have been killed.
                return;
            }

            await manager.Refresh();

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
            this.output.WriteLine($"Firing probes for silos: {string.Join(", ", testAccessor.MonitoredSilos.Keys)}");
            var probesReceived = new HashSet<SiloAddress>();
            await UntilEqual(testAccessor.MonitoredSilos.Count, () =>
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

            foreach (var siloMonitor in testAccessor.MonitoredSilos.Values)
            {
                this.output.WriteLine($"Checking missed probes on {siloMonitor.SiloAddress}: {((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes}");
                Assert.Equal(0, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);
            }

            await StopLifecycle();
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status) => new MembershipEntry { SiloAddress = address, Status = status };

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
    }
}
