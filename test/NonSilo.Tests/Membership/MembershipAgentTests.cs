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
    public class MembershipAgentTests
    {
        private readonly ITestOutputHelper output;
        private readonly LoggerFactory loggerFactory;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly SiloAddress localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly IMembershipGossiper membershipGossiper;
        private readonly SiloLifecycleSubject lifecycle;
        private readonly List<DelegateAsyncTimer> timers;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>> timerCalls;
        private readonly DelegateAsyncTimerFactory timerFactory;
        private readonly InMemoryMembershipTable membershipTable;
        private readonly IOptions<ClusterMembershipOptions> clusterMembershipOptions;
        private readonly MembershipTableManager manager;
        private readonly ClusterHealthMonitor clusterHealthMonitor;
        private readonly IRemoteSiloProber remoteSiloProber;
        private readonly Func<SiloHealthMonitor, SiloHealthMonitor.ProbeResult, Task> onProbeResult;
        private readonly MembershipAgent agent;
        private readonly ILocalSiloHealthMonitor localSiloHealthMonitor;
        private readonly IOptionsMonitor<ClusterMembershipOptions> optionsMonitor;
        private readonly IClusterMembershipService membershipService;

        public MembershipAgentTests(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });

            this.localSiloDetails = Substitute.For<ILocalSiloDetails>();
            this.localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
            this.localSiloDetails.SiloAddress.Returns(this.localSilo);
            this.localSiloDetails.DnsHostName.Returns("MyServer11");
            this.localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            this.fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            this.fatalErrorHandler.IsUnexpected(default).ReturnsForAnyArgs(true);
            this.membershipGossiper = Substitute.For<IMembershipGossiper>();
            this.lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            this.timers = new List<DelegateAsyncTimer>();
            this.timerCalls = new ConcurrentDictionary<string, ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>>();
            this.timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var t = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var queue = this.timerCalls.GetOrAdd(name, n => new ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>());
                            var task = new TaskCompletionSource<bool>();
                            queue.Enqueue((overridePeriod, task));
                            return task.Task;
                        });
                    this.timers.Add(t);
                    return t;
                });

            this.membershipTable = new InMemoryMembershipTable(new TableVersion(1, "1"));
            this.clusterMembershipOptions = Options.Create(new ClusterMembershipOptions());
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

            this.optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            this.optionsMonitor.CurrentValue.ReturnsForAnyArgs(this.clusterMembershipOptions.Value);
            this.clusterHealthMonitor = new ClusterHealthMonitor(
                this.localSiloDetails,
                this.manager,
                this.loggerFactory.CreateLogger<ClusterHealthMonitor>(),
                optionsMonitor,
                this.fatalErrorHandler,
                null);
            ((ILifecycleParticipant<ISiloLifecycle>)this.clusterHealthMonitor).Participate(this.lifecycle);

            this.remoteSiloProber = Substitute.For<IRemoteSiloProber>();
            remoteSiloProber.Probe(default, default).ReturnsForAnyArgs(Task.CompletedTask);

            this.localSiloHealthMonitor = Substitute.For<ILocalSiloHealthMonitor>();
            this.localSiloHealthMonitor.GetLocalHealthDegradationScore(default).ReturnsForAnyArgs(0);

            this.onProbeResult = (Func<SiloHealthMonitor, SiloHealthMonitor.ProbeResult, Task>)((siloHealthMonitor, probeResult) => Task.CompletedTask);

            this.agent = new MembershipAgent(
                this.manager,
                this.localSiloDetails,
                this.fatalErrorHandler,
                this.clusterMembershipOptions,
                this.loggerFactory.CreateLogger<MembershipAgent>(),
                this.timerFactory,
                this.remoteSiloProber);
            ((ILifecycleParticipant<ISiloLifecycle>)this.agent).Participate(this.lifecycle);

            this.membershipService = Substitute.For<IClusterMembershipService>();
            this.membershipService.CurrentSnapshot.ReturnsForAnyArgs(info => this.manager.MembershipTableSnapshot.CreateClusterMembershipSnapshot());
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_GracefulShutdown()
        {
            var levels = new ConcurrentDictionary<int, SiloStatus>();
            Func<CancellationToken, Task> Callback(int level) => ct =>
            {
                levels[level] = this.manager.CurrentStatus;
                return Task.CompletedTask;
            };
            Task NoOp(CancellationToken ct) => Task.CompletedTask;
            foreach (var l in new[] {
                ServiceLifecycleStage.RuntimeInitialize,
                ServiceLifecycleStage.AfterRuntimeGrainServices,
                ServiceLifecycleStage.BecomeActive})
            {
                // After start
                this.lifecycle.Subscribe(
                "x",
                l + 1,
                Callback(l + 1),
                NoOp);

                // After stop
                this.lifecycle.Subscribe(
                "x",
                l - 1,
                NoOp,
                Callback(l - 1));
            }

            await this.lifecycle.OnStart();
            Assert.Equal(SiloStatus.Created, levels[ServiceLifecycleStage.RuntimeInitialize + 1]);
            Assert.Equal(SiloStatus.Joining, levels[ServiceLifecycleStage.AfterRuntimeGrainServices + 1]);
            Assert.Equal(SiloStatus.Active, levels[ServiceLifecycleStage.BecomeActive + 1]);

            await StopLifecycle();

            Assert.Equal(SiloStatus.ShuttingDown, levels[ServiceLifecycleStage.BecomeActive - 1]);
            Assert.Equal(SiloStatus.ShuttingDown, levels[ServiceLifecycleStage.AfterRuntimeGrainServices - 1]);
            Assert.Equal(SiloStatus.Dead, levels[ServiceLifecycleStage.RuntimeInitialize - 1]);
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_UngracefulShutdown()
        {
            var levels = new ConcurrentDictionary<int, SiloStatus>();
            Func<CancellationToken, Task> Callback(int level) => ct =>
            {
                levels[level] = this.manager.CurrentStatus;
                return Task.CompletedTask;
            };
            Task NoOp(CancellationToken ct) => Task.CompletedTask;
            foreach (var l in new[] {
                ServiceLifecycleStage.RuntimeInitialize,
                ServiceLifecycleStage.AfterRuntimeGrainServices,
                ServiceLifecycleStage.BecomeActive})
            {
                // After start
                this.lifecycle.Subscribe(
                "x",
                l + 1,
                Callback(l + 1),
                NoOp);

                // After stop
                this.lifecycle.Subscribe(
                "x",
                l - 1,
                NoOp,
                Callback(l - 1));
            }

            await this.lifecycle.OnStart();
            Assert.Equal(SiloStatus.Created, levels[ServiceLifecycleStage.RuntimeInitialize + 1]);
            Assert.Equal(SiloStatus.Joining, levels[ServiceLifecycleStage.AfterRuntimeGrainServices + 1]);
            Assert.Equal(SiloStatus.Active, levels[ServiceLifecycleStage.BecomeActive + 1]);

            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await StopLifecycle(cancellation.Token);

            Assert.Equal(SiloStatus.Stopping, levels[ServiceLifecycleStage.BecomeActive - 1]);
            Assert.Equal(SiloStatus.Stopping, levels[ServiceLifecycleStage.AfterRuntimeGrainServices - 1]);
            Assert.Equal(SiloStatus.Dead, levels[ServiceLifecycleStage.RuntimeInitialize - 1]);
        }

        [Fact]
        public async Task MembershipAgent_UpdateIAmAlive()
        {
            await this.lifecycle.OnStart();
            await Until(() => this.timerCalls.ContainsKey("UpdateIAmAlive"));

            var updateCounter = 0;
            var testAccessor = (MembershipAgent.ITestAccessor)this.agent;
            testAccessor.OnUpdateIAmAlive = () => ++updateCounter;

            (TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion) timer = (default, default);
            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetResult(true);
            await Until(() => updateCounter == 1);

            testAccessor.OnUpdateIAmAlive = () => { ++updateCounter; throw new Exception("no"); };

            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetResult(true);
            Assert.False(timer.DelayOverride.HasValue);
            await Until(() => updateCounter == 2);

            testAccessor.OnUpdateIAmAlive = () => ++updateCounter;

            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            Assert.True(timer.DelayOverride.HasValue);
            timer.Completion.TrySetResult(true);
            await Until(() => updateCounter == 3);
            Assert.Equal(3, updateCounter);

            // When something goes horribly awry (eg, the timer throws an exception), the silo should fault.
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetException(new Exception("no"));
            Assert.False(timer.DelayOverride.HasValue);
            await Until(() => this.fatalErrorHandler.ReceivedCalls().Any());
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            // Stop & cancel all timers.
            await StopLifecycle();
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_ValidateInitialConnectivity_Success()
        {
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

            // Add the new silos
            foreach (var entry in otherSilos)
            {
                var table = await this.membershipTable.ReadAll();
                Assert.True(await this.membershipTable.InsertRow(entry, table.Version.Next()));
            }

            Task onProbeResult(SiloHealthMonitor siloHealthMonitor, SiloHealthMonitor.ProbeResult probeResult) => Task.CompletedTask;

            var clusterHealthMonitorTestAccessor = (ClusterHealthMonitor.ITestAccessor)this.clusterHealthMonitor;
            clusterHealthMonitorTestAccessor.CreateMonitor = silo => new SiloHealthMonitor(
                silo,
                onProbeResult,
                this.optionsMonitor,
                this.loggerFactory,
                remoteSiloProber,
                this.timerFactory,
                this.localSiloHealthMonitor,
                this.membershipService,
                this.localSiloDetails);
            var started = this.lifecycle.OnStart();

            await Until(() => remoteSiloProber.ReceivedCalls().Count() < otherSilos.Length);


            await Until(() => started.IsCompleted);
            await started;

            await StopLifecycle();
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_ValidateInitialConnectivity_Failure()
        {
            this.timerFactory.CreateDelegate = (period, name) => new DelegateAsyncTimer(_ => Task.FromResult(false));

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

            // Add the new silos
            foreach (var entry in otherSilos)
            {
                var table = await this.membershipTable.ReadAll();
                Assert.True(await this.membershipTable.InsertRow(entry, table.Version.Next()));
            }

            this.remoteSiloProber.Probe(default, default).ReturnsForAnyArgs(Task.FromException(new Exception("no")));

            var dateTimeIndex = 0;
            var dateTimes = new DateTime[] { DateTime.UtcNow, DateTime.UtcNow.AddMinutes(8) };
            var membershipAgentTestAccessor = ((MembershipAgent.ITestAccessor)this.agent).GetDateTime = () => dateTimes[dateTimeIndex++];

            var clusterHealthMonitorTestAccessor = (ClusterHealthMonitor.ITestAccessor)this.clusterHealthMonitor;
            clusterHealthMonitorTestAccessor.CreateMonitor = silo => new SiloHealthMonitor(
                silo,
                this.onProbeResult,
                this.optionsMonitor,
                this.loggerFactory,
                this.remoteSiloProber,
                this.timerFactory,
                this.localSiloHealthMonitor,
                this.membershipService,
                this.localSiloDetails);
            var started = this.lifecycle.OnStart();

            await Until(() => this.remoteSiloProber.ReceivedCalls().Count() < otherSilos.Length);
            await Until(() => started.IsCompleted);

            // Startup should have faulted.
            Assert.True(started.IsFaulted);

            await StopLifecycle();
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status) => new MembershipEntry { SiloAddress = address, Status = status, StartTime = DateTime.UtcNow, IAmAliveTime = DateTime.UtcNow };

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
                foreach (var pair in this.timerCalls) while (pair.Value.TryDequeue(out var call)) call.Completion.TrySetResult(false);
                await Task.Delay(15);
            }

            await stopped;
        }
    }
}
