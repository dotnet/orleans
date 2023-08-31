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
            loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });

            localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSilo = SiloAddress.FromParsableString("127.0.0.1:100@100");
            localSiloDetails.SiloAddress.Returns(localSilo);
            localSiloDetails.DnsHostName.Returns("MyServer11");
            localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            fatalErrorHandler.IsUnexpected(default).ReturnsForAnyArgs(true);
            membershipGossiper = Substitute.For<IMembershipGossiper>();
            lifecycle = new SiloLifecycleSubject(loggerFactory.CreateLogger<SiloLifecycleSubject>());
            timers = new List<DelegateAsyncTimer>();
            timerCalls = new ConcurrentDictionary<string, ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>>();
            timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var t = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var queue = timerCalls.GetOrAdd(name, n => new ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>());
                            var task = new TaskCompletionSource<bool>();
                            queue.Enqueue((overridePeriod, task));
                            return task.Task;
                        });
                    timers.Add(t);
                    return t;
                });

            membershipTable = new InMemoryMembershipTable(new TableVersion(1, "1"));
            clusterMembershipOptions = Options.Create(new ClusterMembershipOptions());
            manager = new MembershipTableManager(
                localSiloDetails: localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: new AsyncTimerFactory(loggerFactory),
                lifecycle);
            ((ILifecycleParticipant<ISiloLifecycle>)manager).Participate(lifecycle);

            optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            optionsMonitor.CurrentValue.ReturnsForAnyArgs(clusterMembershipOptions.Value);
            clusterHealthMonitor = new ClusterHealthMonitor(
                localSiloDetails,
                manager,
                loggerFactory.CreateLogger<ClusterHealthMonitor>(),
                optionsMonitor,
                fatalErrorHandler,
                null);
            ((ILifecycleParticipant<ISiloLifecycle>)clusterHealthMonitor).Participate(lifecycle);

            remoteSiloProber = Substitute.For<IRemoteSiloProber>();
            remoteSiloProber.Probe(default, default).ReturnsForAnyArgs(Task.CompletedTask);

            localSiloHealthMonitor = Substitute.For<ILocalSiloHealthMonitor>();
            localSiloHealthMonitor.GetLocalHealthDegradationScore(default).ReturnsForAnyArgs(0);

            onProbeResult = (Func<SiloHealthMonitor, SiloHealthMonitor.ProbeResult, Task>)((siloHealthMonitor, probeResult) => Task.CompletedTask);

            agent = new MembershipAgent(
                manager,
                localSiloDetails,
                fatalErrorHandler,
                clusterMembershipOptions,
                loggerFactory.CreateLogger<MembershipAgent>(),
                timerFactory,
                remoteSiloProber);
            ((ILifecycleParticipant<ISiloLifecycle>)agent).Participate(lifecycle);

            membershipService = Substitute.For<IClusterMembershipService>();
            membershipService.CurrentSnapshot.ReturnsForAnyArgs(info => manager.MembershipTableSnapshot.CreateClusterMembershipSnapshot());
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_GracefulShutdown()
        {
            var levels = new ConcurrentDictionary<int, SiloStatus>();
            Func<CancellationToken, Task> Callback(int level) => ct =>
            {
                levels[level] = manager.CurrentStatus;
                return Task.CompletedTask;
            };
            Task NoOp(CancellationToken ct) => Task.CompletedTask;
            foreach (var l in new[] {
                ServiceLifecycleStage.RuntimeInitialize,
                ServiceLifecycleStage.AfterRuntimeGrainServices,
                ServiceLifecycleStage.BecomeActive})
            {
                // After start
                lifecycle.Subscribe(
                "x",
                l + 1,
                Callback(l + 1),
                NoOp);

                // After stop
                lifecycle.Subscribe(
                "x",
                l - 1,
                NoOp,
                Callback(l - 1));
            }

            await lifecycle.OnStart();
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
                levels[level] = manager.CurrentStatus;
                return Task.CompletedTask;
            };
            Task NoOp(CancellationToken ct) => Task.CompletedTask;
            foreach (var l in new[] {
                ServiceLifecycleStage.RuntimeInitialize,
                ServiceLifecycleStage.AfterRuntimeGrainServices,
                ServiceLifecycleStage.BecomeActive})
            {
                // After start
                lifecycle.Subscribe(
                "x",
                l + 1,
                Callback(l + 1),
                NoOp);

                // After stop
                lifecycle.Subscribe(
                "x",
                l - 1,
                NoOp,
                Callback(l - 1));
            }

            await lifecycle.OnStart();
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
            await lifecycle.OnStart();
            await Until(() => timerCalls.ContainsKey("UpdateIAmAlive"));

            var updateCounter = 0;
            var testAccessor = (MembershipAgent.ITestAccessor)agent;
            testAccessor.OnUpdateIAmAlive = () => ++updateCounter;

            (TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion) timer = (default, default);
            while (!timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetResult(true);
            await Until(() => updateCounter == 1);

            testAccessor.OnUpdateIAmAlive = () => { ++updateCounter; throw new Exception("no"); };

            while (!timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetResult(true);
            Assert.False(timer.DelayOverride.HasValue);
            await Until(() => updateCounter == 2);

            testAccessor.OnUpdateIAmAlive = () => ++updateCounter;

            while (!timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            Assert.True(timer.DelayOverride.HasValue);
            timer.Completion.TrySetResult(true);
            await Until(() => updateCounter == 3);
            Assert.Equal(3, updateCounter);

            // When something goes horribly awry (eg, the timer throws an exception), the silo should fault.
            fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            while (!timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1);
            timer.Completion.TrySetException(new Exception("no"));
            Assert.False(timer.DelayOverride.HasValue);
            await Until(() => fatalErrorHandler.ReceivedCalls().Any());
            fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

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
                var table = await membershipTable.ReadAll();
                Assert.True(await membershipTable.InsertRow(entry, table.Version.Next()));
            }

            Task onProbeResult(SiloHealthMonitor siloHealthMonitor, SiloHealthMonitor.ProbeResult probeResult) => Task.CompletedTask;

            var clusterHealthMonitorTestAccessor = (ClusterHealthMonitor.ITestAccessor)clusterHealthMonitor;
            clusterHealthMonitorTestAccessor.CreateMonitor = silo => new SiloHealthMonitor(
                silo,
                onProbeResult,
                optionsMonitor,
                loggerFactory,
                remoteSiloProber,
                timerFactory,
                localSiloHealthMonitor,
                membershipService,
                localSiloDetails);
            var started = lifecycle.OnStart();

            await Until(() => remoteSiloProber.ReceivedCalls().Count() < otherSilos.Length);


            await Until(() => started.IsCompleted);
            await started;

            await StopLifecycle();
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_ValidateInitialConnectivity_Failure()
        {
            timerFactory.CreateDelegate = (period, name) => new DelegateAsyncTimer(_ => Task.FromResult(false));

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
                var table = await membershipTable.ReadAll();
                Assert.True(await membershipTable.InsertRow(entry, table.Version.Next()));
            }

            remoteSiloProber.Probe(default, default).ReturnsForAnyArgs(Task.FromException(new Exception("no")));

            var dateTimeIndex = 0;
            var dateTimes = new DateTime[] { DateTime.UtcNow, DateTime.UtcNow.AddMinutes(8) };
            var membershipAgentTestAccessor = ((MembershipAgent.ITestAccessor)agent).GetDateTime = () => dateTimes[dateTimeIndex++];

            var clusterHealthMonitorTestAccessor = (ClusterHealthMonitor.ITestAccessor)clusterHealthMonitor;
            clusterHealthMonitorTestAccessor.CreateMonitor = silo => new SiloHealthMonitor(
                silo,
                onProbeResult,
                optionsMonitor,
                loggerFactory,
                remoteSiloProber,
                timerFactory,
                localSiloHealthMonitor,
                membershipService,
                localSiloDetails);
            var started = lifecycle.OnStart();

            await Until(() => remoteSiloProber.ReceivedCalls().Count() < otherSilos.Length);
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
            var stopped = lifecycle.OnStop(cancellation);

            while (!stopped.IsCompleted)
            {
                foreach (var pair in timerCalls) while (pair.Value.TryDequeue(out var call)) call.Completion.TrySetResult(false);
                await Task.Delay(15);
            }

            await stopped;
        }
    }
}
