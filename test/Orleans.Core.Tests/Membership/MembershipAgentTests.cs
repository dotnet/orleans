using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NonSilo.Tests.Utilities;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for membership agent functionality including lifecycle stages, IAmAlive updates, and connectivity validation.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
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
            this.fatalErrorHandler.IsUnexpected(default!).ReturnsForAnyArgs(true);
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
            this.clusterMembershipOptions = Options.Create(new ClusterMembershipOptions() { MaxJoinAttemptTime = TimeSpan.FromSeconds(45) });
            this.manager = new MembershipTableManager(
                localSiloDetails: this.localSiloDetails,
                clusterMembershipOptions: Options.Create(new ClusterMembershipOptions()),
                membershipTable: membershipTable,
                fatalErrorHandler: this.fatalErrorHandler,
                gossiper: this.membershipGossiper,
                log: this.loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: new AsyncTimerFactory(this.loggerFactory),
                this.lifecycle,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)this.manager).Participate(this.lifecycle);

            this.optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            this.optionsMonitor.CurrentValue.ReturnsForAnyArgs(this.clusterMembershipOptions.Value);
            this.clusterHealthMonitor = new ClusterHealthMonitor(
                this.localSiloDetails,
                this.manager,
                this.loggerFactory.CreateLogger<ClusterHealthMonitor>(),
                optionsMonitor,
                this.fatalErrorHandler,
                null!,
                new ConnectionManager(
                    Options.Create(new ConnectionOptions()),
                    null!,
                    this.loggerFactory.CreateLogger<ConnectionManager>()),
                TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)this.clusterHealthMonitor).Participate(this.lifecycle);

            this.remoteSiloProber = Substitute.For<IRemoteSiloProber>();
            remoteSiloProber.Probe(default!, default, TestContext.Current.CancellationToken).ReturnsForAnyArgs(Task.CompletedTask);

            this.localSiloHealthMonitor = Substitute.For<ILocalSiloHealthMonitor>();
            this.localSiloHealthMonitor.GetLocalHealthStatus(default, default, default).ReturnsForAnyArgs(new LocalSiloHealthStatus(0, []));

            this.onProbeResult = (Func<SiloHealthMonitor, SiloHealthMonitor.ProbeResult, Task>)((siloHealthMonitor, probeResult) => Task.CompletedTask);

            this.agent = new MembershipAgent(
                this.manager,
                this.localSiloDetails,
                this.fatalErrorHandler,
                this.clusterMembershipOptions,
                this.loggerFactory.CreateLogger<MembershipAgent>(),
                this.timerFactory,
                this.remoteSiloProber,
                timeProvider: TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)this.agent).Participate(this.lifecycle);
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_GracefulShutdown()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
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
                ServiceLifecycleStage.ValidateInitialConnectivity,
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

            await this.lifecycle.OnStart(cancellationToken);
            Assert.Equal(SiloStatus.Created, levels[ServiceLifecycleStage.RuntimeInitialize + 1]);
            Assert.Equal(SiloStatus.Joining, levels[ServiceLifecycleStage.AfterRuntimeGrainServices + 1]);
            Assert.Equal(SiloStatus.Joining, levels[ServiceLifecycleStage.ValidateInitialConnectivity + 1]);
            Assert.Equal(SiloStatus.Active, levels[ServiceLifecycleStage.BecomeActive + 1]);

            await StopLifecycle(cancellationToken);

            Assert.Equal(SiloStatus.ShuttingDown, levels[ServiceLifecycleStage.GrainDeactivation]);
            Assert.Equal(SiloStatus.ShuttingDown, levels[ServiceLifecycleStage.ValidateInitialConnectivity - 1]);
            Assert.Equal(SiloStatus.ShuttingDown, levels[ServiceLifecycleStage.AfterRuntimeGrainServices - 1]);
            Assert.Equal(SiloStatus.Dead, levels[ServiceLifecycleStage.RuntimeInitialize - 1]);
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_UngracefulShutdown()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
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
                ServiceLifecycleStage.ValidateInitialConnectivity,
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

            await this.lifecycle.OnStart(cancellationToken);
            Assert.Equal(SiloStatus.Created, levels[ServiceLifecycleStage.RuntimeInitialize + 1]);
            Assert.Equal(SiloStatus.Joining, levels[ServiceLifecycleStage.AfterRuntimeGrainServices + 1]);
            Assert.Equal(SiloStatus.Joining, levels[ServiceLifecycleStage.ValidateInitialConnectivity + 1]);
            Assert.Equal(SiloStatus.Active, levels[ServiceLifecycleStage.BecomeActive + 1]);

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.Cancel();
            await StopLifecycle(cancellationToken, cancellation.Token);

            Assert.Equal(SiloStatus.Stopping, levels[ServiceLifecycleStage.GrainDeactivation]);
            Assert.Equal(SiloStatus.Stopping, levels[ServiceLifecycleStage.ValidateInitialConnectivity - 1]);
            Assert.Equal(SiloStatus.Stopping, levels[ServiceLifecycleStage.AfterRuntimeGrainServices - 1]);
            Assert.Equal(SiloStatus.Dead, levels[ServiceLifecycleStage.RuntimeInitialize - 1]);
        }

        [Fact]
        public async Task MembershipAgent_Shutdown_PublishesTerminalStatusesWithLiveTokens()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await this.lifecycle.OnStart(cancellationToken);
            var statuses = new List<SiloStatus>();
            this.membershipGossiper.GossipToRemoteSilos(
                    Arg.Any<List<SiloAddress>>(),
                    Arg.Any<MembershipTableSnapshot>(),
                    Arg.Any<SiloAddress>(),
                    Arg.Any<SiloStatus>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var token = call.ArgAt<CancellationToken>(4);
                    Assert.True(token.CanBeCanceled);
                    Assert.False(token.IsCancellationRequested);
                    statuses.Add(call.ArgAt<SiloStatus>(3));
                    return Task.CompletedTask;
                });
            using var shutdown = new CancellationTokenSource();
            shutdown.Cancel();

            await StopLifecycle(cancellationToken, shutdown.Token);

            Assert.Equal([SiloStatus.Stopping, SiloStatus.Dead], statuses);
            Assert.Equal(SiloStatus.Dead, this.manager.CurrentStatus);
        }

        [Fact]
        public async Task MembershipAgent_HeartbeatFaultDuringStop_RemainsRecoverable()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            var membershipManager = Substitute.For<IMembershipManager>();
            membershipManager.CurrentSnapshot.Returns(this.manager.MembershipTableSnapshot);
            membershipManager.LocalSiloStatus.Returns(SiloStatus.Active);
            var tick = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var agent = new MembershipAgent(
                membershipManager, this.localSiloDetails, this.fatalErrorHandler, this.clusterMembershipOptions,
                this.loggerFactory.CreateLogger<MembershipAgent>(),
                new DelegateAsyncTimerFactory((_, _) => new DelegateAsyncTimer(_ => tick.Task)),
                this.remoteSiloProber, TimeProvider.System);
            ((ILifecycleParticipant<ISiloLifecycle>)agent).Participate(lifecycle);
            await lifecycle.OnStart(cancellationToken);
            var stopping = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken heartbeatToken = default;
            membershipManager.UpdateIAmAlive(Arg.Any<CancellationToken>()).Returns(call =>
            {
                heartbeatToken = call.ArgAt<CancellationToken>(0);
                stopping.SetResult(lifecycle.OnStop(cancellationToken));
                return Task.FromException(new InvalidOperationException("Provider failure during shutdown"));
            });

            tick.SetResult(true);
            var stop = await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await stop.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Assert.True(heartbeatToken.IsCancellationRequested);
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }

        [Fact]
        public async Task MembershipAgent_ForcedStop_CompletesCanceledGracefulAttemptBeforeStopping()
        {
            var testToken = TestContext.Current.CancellationToken;
            var clock = new FakeTimeProvider();
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            var membershipManager = Substitute.For<IMembershipManager>();
            membershipManager.CurrentSnapshot.Returns(this.manager.MembershipTableSnapshot);
            membershipManager.LocalSiloStatus.Returns(SiloStatus.Active);
            var gracefulStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gracefulCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGraceful = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gracefulFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stoppingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var statuses = new ConcurrentQueue<SiloStatus>();
            var gracefulFinishedBeforeStopping = false;
            var stoppingTokenWasLive = false;
            CancellationToken stoppingToken = default;
            membershipManager.UpdateLocalStatus(Arg.Any<SiloStatus>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var status = call.ArgAt<SiloStatus>(0);
                    var token = call.ArgAt<CancellationToken>(1);
                    if (status is SiloStatus.ShuttingDown or SiloStatus.Stopping or SiloStatus.Dead)
                    {
                        statuses.Enqueue(status);
                    }

                    if (status == SiloStatus.ShuttingDown)
                    {
                        gracefulStarted.SetResult(token);
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            gracefulCanceled.SetResult();
                            await releaseGraceful.Task;
                            throw;
                        }
                        finally
                        {
                            gracefulFinished.SetResult();
                        }
                    }
                    else if (status == SiloStatus.Stopping)
                    {
                        gracefulFinishedBeforeStopping = gracefulFinished.Task.IsCompleted;
                        stoppingTokenWasLive = token.CanBeCanceled && !token.IsCancellationRequested;
                        stoppingToken = token;
                        stoppingStarted.SetResult();
                    }
                });
            using var agent = new MembershipAgent(
                membershipManager, this.localSiloDetails, this.fatalErrorHandler, this.clusterMembershipOptions,
                this.loggerFactory.CreateLogger<MembershipAgent>(),
                new DelegateAsyncTimerFactory((_, _) => new DelegateAsyncTimer(_ => Task.FromResult(false))),
                this.remoteSiloProber, clock);
            ((ILifecycleParticipant<ISiloLifecycle>)agent).Participate(lifecycle);
            await lifecycle.OnStart(testToken);
            using var shutdown = new CancellationTokenSource();
            var stopped = lifecycle.OnStop(shutdown.Token);
            try
            {
                var gracefulToken = await gracefulStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
                shutdown.Cancel();
                clock.Advance(ClusterMembershipOptions.ClusteringShutdownGracePeriod);
                await gracefulCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
                Assert.False(stoppingStarted.Task.IsCompleted);

                releaseGraceful.SetResult();
                await stopped.WaitAsync(TimeSpan.FromSeconds(10), testToken);

                Assert.True(gracefulFinishedBeforeStopping);
                Assert.True(stoppingTokenWasLive);
                Assert.NotEqual(gracefulToken, stoppingToken);
                Assert.Equal([SiloStatus.ShuttingDown, SiloStatus.Stopping, SiloStatus.Dead], statuses);
            }
            finally
            {
                releaseGraceful.TrySetResult();
                clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        [Fact]
        public async Task MembershipAgent_UpdateIAmAlive()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await this.lifecycle.OnStart(cancellationToken);
            await Until(() => this.timerCalls.ContainsKey("UpdateIAmAlive"), cancellationToken);

            var updateCounter = 0;
            var testAccessor = (MembershipAgent.ITestAccessor)this.agent;
            testAccessor.OnUpdateIAmAlive = () => ++updateCounter;

            (TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion) timer = (default, default!);
            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1, cancellationToken);
            timer.Completion.TrySetResult(true);
            await Until(() => updateCounter == 1, cancellationToken);

            testAccessor.OnUpdateIAmAlive = () => { ++updateCounter; throw new Exception("no"); };

            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1, cancellationToken);
            timer.Completion.TrySetResult(true);
            Assert.False(timer.DelayOverride.HasValue);
            await Until(() => updateCounter == 2, cancellationToken);

            testAccessor.OnUpdateIAmAlive = () => ++updateCounter;

            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1, cancellationToken);
            Assert.True(timer.DelayOverride.HasValue);
            timer.Completion.TrySetResult(true);
            await Until(() => updateCounter == 3, cancellationToken);
            Assert.Equal(3, updateCounter);

            // When something goes horribly awry (eg, the timer throws an exception), the silo should fault.
            this.fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
            while (!this.timerCalls["UpdateIAmAlive"].TryDequeue(out timer)) await Task.Delay(1, cancellationToken);
            timer.Completion.TrySetException(new Exception("no"));
            Assert.False(timer.DelayOverride.HasValue);
            await Until(() => this.fatalErrorHandler.ReceivedCalls().Any(), cancellationToken);
            this.fatalErrorHandler.ReceivedWithAnyArgs().OnFatalException(default, default, default);

            // Stop & cancel all timers.
            await StopLifecycle(cancellationToken);
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_ValidateInitialConnectivity_Success()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
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
                var table = await this.membershipTable.ReadAllAsync(cancellationToken);
                Assert.True(await this.membershipTable.InsertRowAsync(entry, table.Version.Next(), cancellationToken));
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
                manager,
                this.localSiloDetails,
                TimeProvider.System);
            var started = this.lifecycle.OnStart(cancellationToken);

            await Until(() => remoteSiloProber.ReceivedCalls().Count() >= otherSilos.Length, cancellationToken);


            await Until(() => started.IsCompleted, cancellationToken);
            await started;

            await StopLifecycle(cancellationToken);
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_ValidateInitialConnectivity_WaitsForStaleSilosToBeEvicted()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            this.clusterMembershipOptions.Value.NumMissedProbesLimit = 1;
            this.clusterMembershipOptions.Value.NumVotesForDeathDeclaration = 1;
            this.clusterMembershipOptions.Value.ProbeTimeout = TimeSpan.FromMilliseconds(20);
            this.remoteSiloProber.Probe(default!, default, cancellationToken).ReturnsForAnyArgs(Task.FromException(new Exception("no")));

            var staleSilo = Silo("127.0.0.200:100@100");
            var staleEntry = Entry(staleSilo, SiloStatus.Active, DateTime.UtcNow.Subtract(TimeSpan.FromHours(1)));
            var table = await this.membershipTable.ReadAllAsync(cancellationToken);
            Assert.True(await this.membershipTable.InsertRowAsync(staleEntry, table.Version.Next(), cancellationToken));

            var clusterHealthMonitorTestAccessor = (ClusterHealthMonitor.ITestAccessor)this.clusterHealthMonitor;
            clusterHealthMonitorTestAccessor.CreateMonitor = silo => new SiloHealthMonitor(
                silo,
                clusterHealthMonitorTestAccessor.OnProbeResult,
                this.optionsMonitor,
                this.loggerFactory,
                this.remoteSiloProber,
                this.timerFactory,
                this.localSiloHealthMonitor,
                manager,
                this.localSiloDetails,
                TimeProvider.System);

            var started = this.lifecycle.OnStart(cancellationToken);
            await Until(() => this.timerCalls.ContainsKey(nameof(SiloHealthMonitor)), cancellationToken);

            while (!started.IsCompleted)
            {
                if (this.timerCalls[nameof(SiloHealthMonitor)].TryDequeue(out var timer))
                {
                    timer.Completion.TrySetResult(true);
                }

                await Task.Delay(1, cancellationToken);
            }

            await started;

            table = await this.membershipTable.ReadAllAsync(cancellationToken);
            Assert.Equal(SiloStatus.Dead, table.Members.Single(member => member.Item1.SiloAddress.Equals(staleSilo)).Item1.Status);
            Assert.Equal(SiloStatus.Active, this.manager.CurrentStatus);

            await StopLifecycle(cancellationToken);
        }

        [Fact]
        public async Task MembershipAgent_LifecycleStages_ValidateInitialConnectivity_Failure()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
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
                var table = await this.membershipTable.ReadAllAsync(cancellationToken);
                Assert.True(await this.membershipTable.InsertRowAsync(entry, table.Version.Next(), cancellationToken));
            }

            this.remoteSiloProber.Probe(default!, default, cancellationToken).ReturnsForAnyArgs(Task.FromException(new Exception("no")));

            var dateTimeIndex = 0;
            var dateTimes = new DateTime[] { DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1) };
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
                manager,
                this.localSiloDetails,
                TimeProvider.System);
            var started = this.lifecycle.OnStart(cancellationToken);

            await Until(() => this.remoteSiloProber.ReceivedCalls().Count() >= otherSilos.Length, cancellationToken);
            await Until(() => started.IsCompleted, cancellationToken);

            // Startup should have faulted.
            Assert.True(started.IsFaulted);

            await StopLifecycle(cancellationToken);
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTime? iAmAliveTime = null)
        {
            var now = DateTime.UtcNow;
            var entryTime = iAmAliveTime ?? now;
            return new MembershipEntry { SiloAddress = address, Status = status, StartTime = entryTime, IAmAliveTime = entryTime };
        }

        private static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
        {
            var maxTimeout = 40_000;
            while (!condition() && (maxTimeout -= 10) >= 0) await Task.Delay(10, cancellationToken);
            Assert.True(maxTimeout > 0);
        }

        private async Task StopLifecycle(
            CancellationToken cancellationToken,
            CancellationToken? lifecycleCancellationToken = null)
        {
            var stopped = this.lifecycle.OnStop(lifecycleCancellationToken ?? cancellationToken);

            while (!stopped.IsCompleted)
            {
                foreach (var pair in this.timerCalls) while (pair.Value.TryDequeue(out var call)) call.Completion.TrySetResult(false);
                await Task.Delay(15, cancellationToken);
            }

            await stopped;
        }
    }
}
