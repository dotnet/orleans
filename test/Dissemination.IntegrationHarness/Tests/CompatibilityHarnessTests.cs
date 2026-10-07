using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace Orleans.Dissemination.IntegrationHarness;

public sealed class CompatibilityHarnessTests
{
    [Fact]
    public void LoadStateComparisonRequiresExactInventoryAndValues()
    {
        var expected = new Dictionary<string, string>
        {
            ["silo@2"] = "current",
            ["peer@1"] = "peer-value",
        };
        var actual = new Dictionary<string, string>(expected);
        Assert.True(ProcessCluster.MatchesExpectedLoad(actual, expected));

        actual["silo@1"] = "retired";
        Assert.False(ProcessCluster.MatchesExpectedLoad(actual, expected));
        actual.Remove("silo@2");
        Assert.False(ProcessCluster.MatchesExpectedLoad(actual, expected));
        actual.Remove("silo@1");
        actual["silo@2"] = "older-value";
        Assert.False(ProcessCluster.MatchesExpectedLoad(actual, expected));
        actual["silo@2"] = "current";
        Assert.True(ProcessCluster.MatchesExpectedLoad(actual, expected));
    }

    [Fact]
    public async Task ConnectionDrain_WaitsForInitializationAndCloseBeforeReopening()
    {
        using var observation = new Observation();
        await using var context = CreateConnectionContext("initializing");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = observation.Connection(context, async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        observation.Partition();
        Assert.True(context.ConnectionClosed.IsCancellationRequested);
        var closeCalls = 0;
        var drain = observation.DrainConnections(
            static _ => true,
            connection =>
            {
                Assert.Same(context, connection);
                closeCalls++;
                return closed.Task;
            },
            TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, closeCalls);
            Assert.False(drain.IsCompleted);
            Assert.Throws<InvalidOperationException>(observation.ResumeConnections);
            await using var rejected = CreateConnectionContext("rejected");
            await observation.Connection(rejected, _ => throw new InvalidOperationException("A partitioned connection entered."));
            release.SetResult();
            await middleware;
            Assert.False(drain.IsCompleted);
            Assert.Throws<InvalidOperationException>(observation.ResumeConnections);
            closed.SetResult();
            await drain.WaitAsync(TestContext.Current.CancellationToken);
            observation.ResumeConnections();
            Assert.False(observation.Partitioned);
            var resumed = false;
            await using var reopened = CreateConnectionContext("reopened");
            await observation.Connection(reopened, _ =>
            {
                resumed = true;
                return Task.CompletedTask;
            });
            Assert.True(resumed);
        }
        finally
        {
            release.TrySetResult();
            closed.TrySetResult();
            await middleware;
            await drain;
        }
    }

    [Fact]
    public async Task ConnectionDrain_CapturesUnidentifiedPeersAndPreservesHealthyConnections()
    {
        using var observation = new Observation();
        await using var unknown = CreateConnectionContext("unknown");
        await using var healthy = CreateConnectionContext("healthy");
        healthy.Items["peer"] = "healthy";
        var releaseUnknown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHealthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unknownMiddleware = observation.Connection(unknown, _ => releaseUnknown.Task);
        var healthyMiddleware = observation.Connection(healthy, _ => releaseHealthy.Task);
        using var cancellation = new CancellationTokenSource();
        var closed = new List<string>();
        var drain = observation.DrainConnections(
            context => !context.Items.TryGetValue("peer", out var peer) || Equals(peer, "partitioned"),
            context =>
            {
                closed.Add(context.ConnectionId);
                return Task.CompletedTask;
            },
            cancellation.Token);
        try
        {
            Assert.Equal(new[] { "unknown" }, closed);
            Assert.True(unknown.ConnectionClosed.IsCancellationRequested);
            Assert.False(healthy.ConnectionClosed.IsCancellationRequested);
            Assert.False(healthyMiddleware.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
            await using var rejected = CreateConnectionContext("new-unknown");
            await observation.Connection(rejected, _ => throw new InvalidOperationException("An unidentified connection entered."));
            releaseUnknown.SetResult();
            await unknownMiddleware;
            observation.ResumeConnections();
            Assert.False(healthyMiddleware.IsCompleted);
        }
        finally
        {
            releaseUnknown.TrySetResult();
            releaseHealthy.TrySetResult();
            await Task.WhenAll(unknownMiddleware, healthyMiddleware);
        }
    }

    [Fact]
    public async Task ControlledCall_WaitsForRawCancellationAfterExplicitSignal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var call = new ControlledCall();
        var raw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken actualToken = default;
        call.Start("receiver", token =>
        {
            actualToken = token;
            return raw.Task;
        });
        using var registration = actualToken.UnsafeRegister(
            static state => ((TaskCompletionSource)state!).SetResult(), cancellationObserved);
        Assert.True(actualToken.CanBeCanceled);
        Assert.False(actualToken.IsCancellationRequested);
        Assert.False(call.Snapshot.CancellationRequested);
        Assert.False(call.Snapshot.RawTaskCompleted);
        var cancelling = call.Cancel(cancellationToken);
        await cancellationObserved.Task.WaitAsync(cancellationToken);
        Assert.True(actualToken.IsCancellationRequested);
        Assert.False(cancelling.IsCompleted);
        Assert.False(call.Snapshot.RawTaskCompleted);
        raw.SetCanceled(actualToken);
        await cancelling.WaitAsync(cancellationToken);
        Assert.True(call.Snapshot.CancellationRequested);
        Assert.True(call.Snapshot.RawTaskCompleted);
        Assert.Equal("Canceled", call.Snapshot.RawTaskStatus);
    }

    [Fact]
    public async Task ControlledCall_DoesNotTreatRawTimeoutAsCancellation()
    {
        using var call = new ControlledCall();
        var raw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        call.Start("receiver", _ => raw.Task);
        var cancelling = call.Cancel(TestContext.Current.CancellationToken);
        raw.SetException(new TimeoutException("Raw RPC expired without observing cancellation."));
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => cancelling);
        Assert.Equal("Raw RPC expired without observing cancellation.", exception.Message);
        Assert.True(call.Snapshot.CancellationRequested);
        Assert.True(call.Snapshot.RawTaskCompleted);
        Assert.Equal("Faulted", call.Snapshot.RawTaskStatus);
    }

    [Fact]
    public void StateComparison_ObservesNestedPublicReadonlyFieldChanges()
    {
        var original = new { Version = 7, EnvironmentStatistics = new ReadonlyStatistics(20) };
        var changed = new { Version = 7, EnvironmentStatistics = new ReadonlyStatistics(35) };
        // This is the previous comparison's blind spot: properties alone produce identical JSON.
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(changed));
        var before = StateComparison.Serialize(original);
        var after = StateComparison.Serialize(changed);
        Assert.NotEqual(before, after);
        using var beforeDocument = JsonDocument.Parse(before);
        using var afterDocument = JsonDocument.Parse(after);
        Assert.Equal(20, beforeDocument.RootElement.GetProperty("EnvironmentStatistics").GetProperty("RawCpuUsagePercentage").GetSingle());
        Assert.Equal(35, afterDocument.RootElement.GetProperty("EnvironmentStatistics").GetProperty("RawCpuUsagePercentage").GetSingle());
    }

    [Fact]
    public async Task InFlightCallGate_DrainsPriorCallsAndRejectsLaterCalls()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new InFlightCallGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = gate.Invoke(async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        try
        {
            await entered.Task.WaitAsync(cancellationToken);
            var drained = gate.BlockAndDrain(cancellationToken);
            Assert.False(drained.IsCompleted);
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.Invoke(
                () => throw new InvalidOperationException("A blocked call must never be invoked.")));
            Assert.Equal(InFlightCallGate.ClosedMessage, rejected.Message);
            Assert.Equal(new TreeGateSnapshot(true, 1, 1, 1), gate.Snapshot);
            release.SetResult();
            await prior.WaitAsync(cancellationToken);
            await drained.WaitAsync(cancellationToken);
            Assert.Equal(new TreeGateSnapshot(true, 0, 1, 1), gate.Snapshot);
            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.Invoke(
                () => throw new InvalidOperationException("A call must remain blocked after the drain.")));
            Assert.Equal(new TreeGateSnapshot(true, 0, 1, 2), gate.Snapshot);
            gate.Open();
            await gate.Invoke(() => Task.CompletedTask);
            Assert.Equal(new TreeGateSnapshot(false, 0, 2, 2), gate.Snapshot);
        }
        finally
        {
            release.TrySetResult();
            await prior.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public void DisseminationActivityCountsOnlyOutgoingWork()
    {
        using var observation = new Observation();
        using var meter = new Meter("Microsoft.Orleans.CompatibilityHarnessTest");
        var broadcasts = meter.CreateCounter<long>("orleans-dissemination-broadcast-sent");
        var repairs = meter.CreateCounter<long>("orleans-dissemination-anti-entropy-exchanges");
        broadcasts.Add(2);
        repairs.Add(3, new KeyValuePair<string, object?>("direction", "out"));
        repairs.Add(5, new KeyValuePair<string, object?>("direction", "in"));

        Assert.Equal(2, observation.BroadcastsSent);
        Assert.Equal(3, observation.OutgoingRepairs);
    }

    private sealed record TestPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

    private static DefaultConnectionContext CreateConnectionContext(string id)
    {
        var pipe = new Pipe();
        return new TestConnectionContext(id) { Transport = new TestPipe(pipe.Reader, pipe.Writer) };
    }

    // DefaultConnectionContext queues Cancel independently of Dispose. These tests own abort synchronously;
    // separate barriers continue to control middleware and transport completion.
    private sealed class TestConnectionContext : DefaultConnectionContext
    {
        private readonly CancellationTokenSource _closed = new();

        public TestConnectionContext(string id) : base(id) => ConnectionClosed = _closed.Token;

        public override void Abort(ConnectionAbortedException abortReason) => _closed.Cancel();

        public override ValueTask DisposeAsync()
        {
            _closed.Dispose();
            return base.DisposeAsync();
        }
    }

    private readonly struct ReadonlyStatistics(float rawCpuUsagePercentage)
    {
        public readonly float RawCpuUsagePercentage = rawCpuUsagePercentage;
    }
}
