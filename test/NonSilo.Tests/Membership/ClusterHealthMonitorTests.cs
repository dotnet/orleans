using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly IRemoteSiloProber prober;
        private readonly InMemoryMembershipTable membershipTable;
        private readonly MembershipTableManager manager;

        public ClusterHealthMonitorTests(ITestOutputHelper output)
        {
            MessagingStatisticsGroup.Init();
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

            this.prober = Substitute.For<IRemoteSiloProber>();
            this.membershipTable = new InMemoryMembershipTable(new TableVersion(1, "1"));
            this.manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                this.lifecycle);
            ((ILifecycleParticipant<ISiloLifecycle>)this.manager).Participate(this.lifecycle);
        }

        /// <summary>
        /// Tests basic operation of <see cref="ClusterHealthMonitor"/> and <see cref="SiloHealthMonitor"/>.
        /// </summary>
        [Fact]
        public async Task ClusterHealthMonitor_BasicScenario()
        {
            var probeCalls = new ConcurrentQueue<(SiloAddress, int)>();
            this.prober.Probe(default, default).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1)));
                return Task.CompletedTask;
            });

            var clusterMembershipOptions = new ClusterMembershipOptions();
            var monitor = new ClusterHealthMonitor(
                this.localSiloDetails,
                this.manager,
                this.loggerFactory.CreateLogger<ClusterHealthMonitor>(),
                Options.Create(clusterMembershipOptions),
                this.fatalErrorHandler,
                null,
                this.timerFactory);
            ((ILifecycleParticipant<ISiloLifecycle>)monitor).Participate(this.lifecycle);
            var testAccessor = (ClusterHealthMonitor.ITestAccessor)monitor;
            testAccessor.CreateMonitor = s => new SiloHealthMonitor(s, this.loggerFactory, this.prober);

            await this.lifecycle.OnStart();
            Assert.NotEmpty(this.timers);
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
            foreach (var entry in otherSilos)
            {
                var table = await this.membershipTable.ReadAll();
                Assert.True(await this.membershipTable.InsertRow(entry, table.Version.Next()));
            }

            await this.manager.Refresh();
            (TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion) timer = (default, default);
            while (!this.timerCalls.TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetResult(true);

            await Until(() => testAccessor.ObservedVersion > lastVersion);
            lastVersion = testAccessor.ObservedVersion;

            // No silos should be monitored by this silo until it becomes active.
            Assert.Empty(testAccessor.MonitoredSilos);

            await this.manager.UpdateStatus(SiloStatus.Active);

            await Until(() => testAccessor.ObservedVersion > lastVersion);
            lastVersion = testAccessor.ObservedVersion;

            // Now that this silo is active, it should be monitoring some fraction of the other active silos
            Assert.NotEmpty(testAccessor.MonitoredSilos);
            Assert.DoesNotContain(testAccessor.MonitoredSilos, s => s.Key.Equals(this.localSilo));
            Assert.Equal(clusterMembershipOptions.NumProbedSilos, testAccessor.MonitoredSilos.Count);
            Assert.All(testAccessor.MonitoredSilos, m => m.Key.Equals(m.Value.SiloAddress));
            Assert.Empty(probeCalls);

            // Check that those silos are actually being probed periodically
            while (!this.timerCalls.TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetResult(true);

            await Until(() => probeCalls.Count == clusterMembershipOptions.NumProbedSilos);
            Assert.Equal(clusterMembershipOptions.NumProbedSilos, probeCalls.Count);
            while (probeCalls.TryDequeue(out var call)) Assert.Contains(testAccessor.MonitoredSilos, k => k.Key.Equals(call.Item1));

            foreach (var siloMonitor in testAccessor.MonitoredSilos.Values)
            {
                Assert.Equal(0, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);
            }

            // Make the probes fail.
            this.prober.Probe(default, default).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1)));
                return Task.FromException(new Exception("no"));
            });

            // The above call to specify the probe behaviour also enqueued a value, so clear it here.
            while (probeCalls.TryDequeue(out _)) ;
            
            for (var expectedMissedProbes = 1; expectedMissedProbes <= clusterMembershipOptions.NumMissedProbesLimit; expectedMissedProbes++)
            {
                var now = DateTime.UtcNow;
                this.membershipTable.ClearCalls();

                while (!this.timerCalls.TryDequeue(out timer)) await Task.Delay(1);
                timer.Completion.TrySetResult(true);

                // Wait for probes to be fired
                await Until(() => probeCalls.Count == clusterMembershipOptions.NumProbedSilos);
                while (probeCalls.TryDequeue(out var call)) ;

                // Check that probes match the expected missed probes
                var table = await this.membershipTable.ReadAll();
                foreach (var siloMonitor in testAccessor.MonitoredSilos.Values)
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
                        Assert.Single(votes);
                    }
                }
            }

            // Make the probes succeed again.
            this.prober.Probe(default, default).ReturnsForAnyArgs(info =>
            {
                probeCalls.Enqueue((info.ArgAt<SiloAddress>(0), info.ArgAt<int>(1)));
                return Task.CompletedTask;
            });

            // The above call to specify the probe behaviour also enqueued a value, so clear it here.
            while (probeCalls.TryDequeue(out _)) ;

            while (!this.timerCalls.TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetResult(true);

            // Wait for probes to be fired
            await Until(() => probeCalls.Count == clusterMembershipOptions.NumProbedSilos);
            while (probeCalls.TryDequeue(out var call)) ;
            foreach (var siloMonitor in testAccessor.MonitoredSilos.Values)
            {
                Assert.Equal(0, ((SiloHealthMonitor.ITestAccessor)siloMonitor).MissedProbes);
            }

            await StopLifecycle();
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status) => new MembershipEntry { SiloAddress = address, Status = status };

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
