using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace Orleans.Dissemination.IntegrationHarness;

public sealed class MeasurementTests
{
    [Fact]
    public async Task ConnectionDrain_WaitsForInitializationAndCloseBeforeReopening()
    {
        using var observation = new Observation(captureProvenance: false);
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
        using var observation = new Observation(captureProvenance: false);
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
    public void MessageObservation_KeepsRequestOneWayAndBytesDistinct()
    {
        using var observation = new Observation(captureProvenance: false);
        using var meter = new Meter("Microsoft.Orleans.HarnessMeasurementTest");
        var histogram = meter.CreateHistogram<int>("orleans-messaging-sent-messages-size");
        histogram.Record(97, new KeyValuePair<string, object?>("MessageDirection", "Request"));
        histogram.Record(31, new KeyValuePair<string, object?>("MessageDirection", "OneWay"));
        histogram.Record(17, new KeyValuePair<string, object?>("MessageDirection", "Response"));
        var metrics = observation.Metrics();
        Assert.Equal(new MetricValue(1, 97), metrics["orleans-messaging-sent-messages-size|MessageDirection=Request"]);
        Assert.Equal(new MetricValue(1, 31), metrics["orleans-messaging-sent-messages-size|MessageDirection=OneWay"]);
        Assert.Equal(new MetricValue(1, 17), metrics["orleans-messaging-sent-messages-size|MessageDirection=Response"]);
    }

    [Fact]
    public async Task TransportObservation_CountsConsumedAndSubmittedBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var observation = new Observation(captureProvenance: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var context = new DefaultConnectionContext("instrumentation-test")
        {
            Transport = new TestPipe(input.Reader, output.Writer),
        };
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await observation.Connection(context, async connection =>
        {
            await input.Writer.WriteAsync(payload, cancellationToken);
            var received = await connection.Transport.Input.ReadAsync(cancellationToken);
            connection.Transport.Input.AdvanceTo(received.Buffer.End);
            await connection.Transport.Output.WriteAsync(payload, cancellationToken);
        });
        Assert.Equal(payload.Length, observation.BytesRead);
        Assert.Equal(payload.Length, observation.BytesWritten);
        await input.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();
        await output.Writer.CompleteAsync();
    }

    [Fact]
    public async Task SocketObservation_ReportsActualLoopbackTransfer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var observation = new Observation(captureProvenance: false);
        using var client = new TcpClient();
        var accepted = listener.AcceptTcpClientAsync(cancellationToken);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
        using var server = await accepted;
        await WaitFor(() => observation.SocketSnapshot.Samples > 0, cancellationToken);
        var before = observation.SocketSnapshot;
        var payload = new byte[4096];
        await client.GetStream().WriteAsync(payload, cancellationToken);
        await server.GetStream().ReadExactlyAsync(payload, cancellationToken);
        await WaitFor(() =>
        {
            var snapshot = observation.SocketSnapshot;
            return snapshot.Sent - before.Sent >= payload.Length && snapshot.Received - before.Received >= payload.Length;
        }, cancellationToken);
    }

    private static async Task WaitFor(Func<bool> predicate, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), "The OS socket EventCounters did not report the measured transfer.");
            await Task.Delay(50, cancellationToken);
        }
    }

    private sealed record TestPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

    private static DefaultConnectionContext CreateConnectionContext(string id)
    {
        var pipe = new Pipe();
        return new(id) { Transport = new TestPipe(pipe.Reader, pipe.Writer) };
    }

    private readonly struct ReadonlyStatistics(float rawCpuUsagePercentage)
    {
        public readonly float RawCpuUsagePercentage = rawCpuUsagePercentage;
    }
}
