using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NonSilo.Tests.Utilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using static Orleans.Runtime.MembershipService.SiloHealthMonitor;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class SiloHealthMonitorTests
    {
        private readonly ITestOutputHelper _output;
        private readonly LoggerFactory _loggerFactory;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly SiloAddress _localSilo;
        private readonly List<DelegateAsyncTimer> _timers;
        private readonly Channel<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)> _timerCalls;
        private readonly DelegateAsyncTimerFactory _timerFactory;
        private readonly ILocalSiloHealthMonitor _localSiloHealthMonitor;
        private readonly IRemoteSiloProber _prober;
        private readonly MembershipTableManager _membershipService;
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IOptionsMonitor<ClusterMembershipOptions> _optionsMonitor;
        private readonly Channel<ProbeResult> _probeResults;
        private readonly SiloHealthMonitor _monitor;
        private readonly SiloAddress _targetSilo;
        private readonly InMemoryMembershipTable _membershipTable;

        public SiloHealthMonitorTests(ITestOutputHelper output)
        {
            _output = output;
            _loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(_output) });

            _localSiloDetails = Substitute.For<ILocalSiloDetails>();
            _localSilo = Silo("127.0.0.1:100@100");
            _localSiloDetails.SiloAddress.Returns(_localSilo);
            _localSiloDetails.DnsHostName.Returns("MyServer11");
            _localSiloDetails.Name.Returns(Guid.NewGuid().ToString("N"));

            _timers = new List<DelegateAsyncTimer>();
            _timerCalls = Channel.CreateUnbounded<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>();
            _timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    var t = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var task = new TaskCompletionSource<bool>();
                            _timerCalls.Writer.TryWrite((overridePeriod, task));
                            return task.Task;
                        });
                    _timers.Add(t);
                    return t;
                });

            _localSiloHealthMonitor = Substitute.For<ILocalSiloHealthMonitor>();
            _localSiloHealthMonitor.GetLocalHealthDegradationScore(default).ReturnsForAnyArgs(0);

            _prober = Substitute.For<IRemoteSiloProber>();

            _clusterMembershipOptions = new ClusterMembershipOptions();
            _optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            _optionsMonitor.CurrentValue.ReturnsForAnyArgs(info => _clusterMembershipOptions);

            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var membershipGossiper = Substitute.For<IMembershipGossiper>();
            var lifecycle = new SiloLifecycleSubject(_loggerFactory.CreateLogger<SiloLifecycleSubject>());

            _targetSilo = Silo("127.0.0.200:100@100");
            _membershipTable = new(new TableVersion(0, "0"), Entry(_localSilo, SiloStatus.Active), Entry(_targetSilo, SiloStatus.Active));
            _membershipService = new MembershipTableManager(
                localSiloDetails: _localSiloDetails,
                clusterMembershipOptions: Options.Create(_clusterMembershipOptions),
                membershipTable: _membershipTable,
                fatalErrorHandler: fatalErrorHandler,
                gossiper: membershipGossiper,
                log: _loggerFactory.CreateLogger<MembershipTableManager>(),
                timerFactory: new AsyncTimerFactory(_loggerFactory),
                lifecycle);

            _probeResults = Channel.CreateBounded<ProbeResult>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
            Task onProbeResult(SiloHealthMonitor mon, ProbeResult res) => _probeResults.Writer.WriteAsync(res).AsTask();

            _monitor = new SiloHealthMonitor(
                _targetSilo,
                onProbeResult,
                _optionsMonitor,
                _loggerFactory,
                _prober,
                _timerFactory,
                _localSiloHealthMonitor,
                _membershipService,
                _localSiloDetails);
        }

        private async Task Shutdown()
        {
            var stopTask = _monitor.StopAsync(CancellationToken.None);

            Task.Run(async () =>
            {
                while (!stopTask.IsCompleted && await _timerCalls.Reader.WaitToReadAsync())
                {
                    while (_timerCalls.Reader.TryRead(out var timerCall))
                    {
                        timerCall.Completion.TrySetResult(false);
                    }
                }
            }).Ignore();

            await stopTask;
            _timerCalls.Writer.TryComplete();
        }

        [Fact]
        public async Task SiloHealthMonitor_SuccessfulProbe()
        {
            _prober.Probe(default, default).ReturnsForAnyArgs(Task.CompletedTask);
            _prober.ProbeIndirectly(default, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("No"));

            _monitor.Start();

            // Let a timer complete
            var timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            // Check the resulting probe result.
            var probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Succeeded, probeResult.Status);
            Assert.Equal(0, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            await Shutdown();
        }

        [Fact]
        public async Task SiloHealthMonitor_FailedProbe_Timeout()
        {
            _clusterMembershipOptions.ProbeTimeout = TimeSpan.FromSeconds(2);

            _prober.Probe(default, default, default).ReturnsForAnyArgs(info => Task.Delay(TimeSpan.FromSeconds(30)));
            _prober.ProbeIndirectly(default, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("No"));
            _monitor.Start();

            // Let a timer complete
            var timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            var probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(1, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            await Shutdown();
        }

        [Fact]
        public async Task SiloHealthMonitor_FailedProbe_Exception()
        {
            _clusterMembershipOptions.ProbeTimeout = TimeSpan.FromSeconds(2);

            _prober.Probe(default, default).ThrowsAsyncForAnyArgs(new Exception("nope"));
            _prober.ProbeIndirectly(default, default, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("No"));
            _monitor.Start();

            // Let a timer complete
            var timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            // Throw directly, instead of timing out the probe
            _prober.WhenForAnyArgs(s => s.Probe(default, default)).Throw(new Exception("nope"));
            timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            var probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(1, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            await Shutdown();
        }

        [Fact]
        public async Task SiloHealthMonitor_Indirect_FailedProbe()
        {
            _clusterMembershipOptions.ProbeTimeout = TimeSpan.FromSeconds(2);
            _clusterMembershipOptions.EnableIndirectProbes = true;

            _prober.Probe(default, default).ThrowsAsyncForAnyArgs(info => new Exception("nonono!"));
            _prober.ProbeIndirectly(default, default, default, default).ReturnsForAnyArgs(new IndirectProbeResponse
            {
                FailureMessage = "fail",
                IntermediaryHealthScore = 0,
                ProbeResponseTime = TimeSpan.FromSeconds(1),
                Succeeded = false
            });
            _monitor.Start();

            // Let a timer complete
            var timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            var probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(1, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            var otherSilo = Silo("127.0.0.1:1234@1234");
            await _membershipTable.InsertRow(Entry(_monitor.TargetSiloAddress, SiloStatus.Active), _membershipTable.Version.Next());
            await _membershipTable.InsertRow(Entry(otherSilo, SiloStatus.Joining), _membershipTable.Version.Next());
            await _membershipService.Refresh();

            // There is only one other active silo (the target silo), so an indirect probe cannot be performed.
            probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(2, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            // Make the other silo active so that there is an intermediary to use for an indirect probe.
            var etag = (await _membershipTable.ReadAll()).Members.Where(kv => kv.Item1.SiloAddress.Equals(otherSilo)).Single().Item2;
            await _membershipTable.UpdateRow(Entry(otherSilo, SiloStatus.Active, iAmAliveTime: DateTime.UtcNow), etag, _membershipTable.Version.Next());
            await _membershipService.Refresh();

            _prober.ClearReceivedCalls();
            timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            // Since there is another active silo, an indirect probe will be performed.
            probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(3, probeResult.FailedProbeCount);
            Assert.False(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            // Ensure that the correct intermediary was selected.
            var probeCall = _prober.ReceivedCalls().Single();
            var args = probeCall.GetArguments();
            var intermediary = Assert.IsType<SiloAddress>(args[0]);
            Assert.Equal(otherSilo, intermediary);

            // Ensure that negative results from unhealthy intermediaries are not considered.
            _prober.ProbeIndirectly(default, default, default, default).ReturnsForAnyArgs(new IndirectProbeResponse
            {
                FailureMessage = "fail",
                IntermediaryHealthScore = 1,
                ProbeResponseTime = TimeSpan.FromSeconds(1),
                Succeeded = false
            });

            _prober.ClearReceivedCalls();
            timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            // The number of failed probes should not be incremented, the status should be "unknown", and the health score should be 1.
            probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Unknown, probeResult.Status);
            Assert.Equal(3, probeResult.FailedProbeCount);
            Assert.False(probeResult.IsDirectProbe);
            Assert.Equal(1, probeResult.IntermediaryHealthDegradationScore);

            // Ensure that the correct intermediary was selected.
            probeCall = _prober.ReceivedCalls().Single();
            args = probeCall.GetArguments();
            intermediary = Assert.IsType<SiloAddress>(args[0]);
            Assert.Equal(otherSilo, intermediary);

            // After seeing that the chosen intermediary is unhealthy, a subsequent probe should be
            // performed directly (since there are no other silos to use as an intermediary).
            _prober.ClearReceivedCalls();
            timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(4, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            // Ensure that it was the target that was probed directly.
            probeCall = _prober.ReceivedCalls().Single();
            args = probeCall.GetArguments();
            var target = Assert.IsType<SiloAddress>(args[0]);
            Assert.Equal(_monitor.TargetSiloAddress, target);

            await Shutdown();
        }

        [Fact]
        public async Task SiloHealthMonitor_IndirectProbe_SkipsStaleSilo()
        {
            _clusterMembershipOptions.EnableIndirectProbes = true;
            _clusterMembershipOptions.ProbeTimeout = TimeSpan.FromSeconds(2);

            // Make direct probes fail.
            _prober.Probe(default, default).ThrowsAsyncForAnyArgs(new Exception("Direct probe failing."));

            // Start the monitor and trigger one timer cycle for a direct-probe attempt (which fails).
            _monitor.Start();
            var timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);
            var firstProbeResult = await _probeResults.Reader.ReadAsync();
            Assert.True(firstProbeResult.IsDirectProbe);
            Assert.Equal(ProbeResultStatus.Failed, firstProbeResult.Status);

            // Now add a 'stale' silo and a 'fresh' silo (both Active).
            // This occurs after the first failed direct probe, matching the approach used in the test above.
            var staleSilo = Silo("127.0.0.1:3333@3333");
            await _membershipTable.InsertRow(
                Entry(staleSilo, SiloStatus.Active, DateTime.UtcNow - TimeSpan.FromMinutes(30)),
                _membershipTable.Version.Next());
            _prober.ClearReceivedCalls();

            // Trigger another timer cycle which will now attempt an indirect probe.
            timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);
            var probeResult = await _probeResults.Reader.ReadAsync();

            // Verify that this time the probe is direct since it skipped the stale intermediary silos.
            Assert.True(probeResult.IsDirectProbe);
            var call = _prober.ReceivedCalls().LastOrDefault();
            var args = call?.GetArguments();
            var probedSilo = Assert.IsType<SiloAddress>(args?[0]);
            Assert.Equal(_targetSilo, probedSilo);
            Assert.Equal(_targetSilo, _monitor.TargetSiloAddress);

            await Shutdown();
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTimeOffset iAmAliveTime = default) => new()
        {
            SiloAddress = address,
            Status = status,
            StartTime = iAmAliveTime.UtcDateTime,
            IAmAliveTime = iAmAliveTime.UtcDateTime
        };
    }
}
