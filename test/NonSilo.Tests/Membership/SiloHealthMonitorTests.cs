using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
        private ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IOptionsMonitor<ClusterMembershipOptions> _optionsMonitor;
        private readonly Channel<ProbeResult> _probeResults;
        private readonly SiloHealthMonitor _monitor;
        private ClusterMembershipSnapshot _membershipSnapshot;

        public SiloHealthMonitorTests(ITestOutputHelper output)
        {
            MessagingStatisticsGroup.Init();
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
            Func<SiloHealthMonitor, ProbeResult, Task> onProbeResult = (mon, res) => _probeResults.Writer.WriteAsync(res).AsTask();

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

        /// <summary>
        /// An intermediary only learns that an unresponsive target has failed once its own probe of the target times out, so it must be
        /// given less time than this silo waits for its response. Otherwise, the intermediary's response races this silo's own timeout,
        /// the probe usually completes as unknown, and an unresponsive (rather than crashed) silo is not declared dead.
        /// </summary>
        [Fact]
        public async Task SiloHealthMonitor_Indirect_UnresponsiveTargetFailsProbe()
        {
            _clusterMembershipOptions.ProbeTimeout = TimeSpan.FromMilliseconds(500);
            _clusterMembershipOptions.EnableIndirectProbes = true;

            var intermediary = Silo("127.0.0.1:1234@1234");
            _membershipSnapshot = Snapshot(2, Member(_localSilo, SiloStatus.Active), Member(_monitor.SiloAddress, SiloStatus.Active), Member(intermediary, SiloStatus.Active));

            // The target never responds: direct probes fail, and the intermediary reports failure once the timeout it was given elapses.
            _prober.Probe(default, default).ThrowsForAnyArgs(info => new TimeoutException("No response"));
            _prober.ProbeIndirectly(default, default, default, default).ReturnsForAnyArgs(async info =>
            {
                var targetProbeTimeout = info.ArgAt<TimeSpan>(2);
                await Task.Delay(targetProbeTimeout);
                return new IndirectProbeResponse
                {
                    FailureMessage = "Requested probe timeout exceeded",
                    IntermediaryHealthScore = 0,
                    ProbeResponseTime = targetProbeTimeout,
                    Succeeded = false
                };
            });
            _monitor.Start();

            // The first two probes are direct.
            for (var expectedFailedProbes = 1; expectedFailedProbes <= 2; expectedFailedProbes++)
            {
                var directTimerCall = await _timerCalls.Reader.ReadAsync();
                directTimerCall.Completion.TrySetResult(true);

                var directProbeResult = await _probeResults.Reader.ReadAsync();
                Assert.Equal(ProbeResultStatus.Failed, directProbeResult.Status);
                Assert.Equal(expectedFailedProbes, directProbeResult.FailedProbeCount);
                Assert.True(directProbeResult.IsDirectProbe);
            }

            // The third probe is performed via the intermediary, whose failure response must arrive before this silo stops waiting.
            _prober.ClearReceivedCalls();
            var timerCall = await _timerCalls.Reader.ReadAsync();
            timerCall.Completion.TrySetResult(true);

            var probeResult = await _probeResults.Reader.ReadAsync();
            Assert.Equal(ProbeResultStatus.Failed, probeResult.Status);
            Assert.Equal(3, probeResult.FailedProbeCount);
            Assert.False(probeResult.IsDirectProbe);
            Assert.Equal(0, probeResult.IntermediaryHealthDegradationScore);

            // This silo waits 2 * ProbeTimeout for an indirect probe, reserving one ProbeTimeout for the intermediary's response.
            var args = _prober.ReceivedCalls().Single().GetArguments();
            Assert.Equal(intermediary, Assert.IsType<SiloAddress>(args[0]));
            Assert.Equal(_monitor.SiloAddress, Assert.IsType<SiloAddress>(args[1]));
            Assert.Equal(_clusterMembershipOptions.ProbeTimeout, Assert.IsType<TimeSpan>(args[2]));

            await Shutdown();
        }

        [Theory]
        [InlineData(10, 0, 5)]
        [InlineData(15, 1, 10)]
        [InlineData(20, 2, 15)]
        public void SiloHealthMonitor_IndirectProbeTargetTimeout_ReservesTimeForResponse(int timeoutSeconds, int localDegradationScore, int expectedSeconds)
        {
            var targetProbeTimeout = SiloHealthMonitor.CalculateIndirectProbeTargetTimeout(TimeSpan.FromSeconds(timeoutSeconds), localDegradationScore);

            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), targetProbeTimeout);
        }

        [Fact]
        public void SiloHealthMonitor_IndirectProbeTargetTimeout_RejectsInvalidArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SiloHealthMonitor.CalculateIndirectProbeTargetTimeout(TimeSpan.Zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SiloHealthMonitor.CalculateIndirectProbeTargetTimeout(TimeSpan.FromSeconds(10), -1));
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
