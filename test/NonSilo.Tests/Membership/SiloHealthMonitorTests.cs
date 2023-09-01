using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NonSilo.Tests.Utilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
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
        private readonly IClusterMembershipService _membershipService;
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IOptionsMonitor<ClusterMembershipOptions> _optionsMonitor;
        private readonly Channel<ProbeResult> _probeResults;
        private readonly SiloHealthMonitor _monitor;
        private ClusterMembershipSnapshot _membershipSnapshot;

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

            _membershipService = Substitute.For<IClusterMembershipService>();

            _clusterMembershipOptions = new ClusterMembershipOptions();
            _optionsMonitor = Substitute.For<IOptionsMonitor<ClusterMembershipOptions>>();
            _optionsMonitor.CurrentValue.ReturnsForAnyArgs(info => _clusterMembershipOptions);

            _probeResults = Channel.CreateBounded<ProbeResult>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
            Task onProbeResult(SiloHealthMonitor mon, ProbeResult res) => _probeResults.Writer.WriteAsync(res).AsTask();

            _membershipSnapshot = Snapshot(
                1,
                Member(_localSilo, SiloStatus.Active),
                Member(Silo("127.0.0.200:100@100"), SiloStatus.Active));
            _membershipService.CurrentSnapshot.ReturnsForAnyArgs(info => _membershipSnapshot);

            _monitor = new SiloHealthMonitor(
                Silo("127.0.0.200:100@100"),
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
            _prober.ProbeIndirectly(default, default, default, default).ThrowsForAnyArgs(new InvalidOperationException("No"));

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
        public async Task SiloHealthMonitor_FailedProbe()
        {
            _clusterMembershipOptions.ProbeTimeout = TimeSpan.FromSeconds(2);

            _prober.Probe(default, default).ReturnsForAnyArgs(info => Task.Delay(TimeSpan.FromSeconds(3)));
            _prober.ProbeIndirectly(default, default, default, default).ThrowsForAnyArgs(new InvalidOperationException("No"));
            _monitor.Start();

            // Let a timer complete
            var timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            var probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(1, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            // Throw directly, instead of timing out the probe
            _prober.Probe(default, default).ThrowsForAnyArgs(new Exception("nope"));
            timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(2, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            await Shutdown();
        }

        [Fact]
        public async Task SiloHealthMonitor_Indirect_FailedProbe()
        {
            _clusterMembershipOptions.ProbeTimeout = TimeSpan.FromSeconds(2);
            _clusterMembershipOptions.EnableIndirectProbes = true;

            _prober.Probe(default, default).ThrowsForAnyArgs(info => new Exception("nonono!"));
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
            _membershipSnapshot = Snapshot(2, Member(_localSilo, SiloStatus.Active), Member(_monitor.SiloAddress, SiloStatus.Active), Member(otherSilo, SiloStatus.Joining));

            // There is only one other active silo (the target silo), so an indirect probe cannot be performed.
            probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(2, probeResult.FailedProbeCount);
            Assert.True(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            // Make the other silo active so that there is an intermediary to use for an indirect probe.
            _membershipSnapshot = Snapshot(3, Member(_localSilo, SiloStatus.Active), Member(_monitor.SiloAddress, SiloStatus.Active), Member(otherSilo, SiloStatus.Active));

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
            Assert.Equal(_monitor.SiloAddress, target);

            await Shutdown();
        }

        private static ClusterMembershipSnapshot Snapshot(long version, params ClusterMember[] members)
            => new ClusterMembershipSnapshot(
                ImmutableDictionary.CreateRange(
                    members.Select(m => new KeyValuePair<SiloAddress, ClusterMember>(m.SiloAddress, m))),
                new MembershipVersion(version));

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static ClusterMember Member(SiloAddress address, SiloStatus status) => new ClusterMember(address, status, address.ToString());
    }
}
