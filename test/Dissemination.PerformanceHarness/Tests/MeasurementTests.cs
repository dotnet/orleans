using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Xunit;

namespace Orleans.Dissemination.PerformanceHarness;

public sealed class MeasurementTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void PairedRepetitionsReverseEveryRuntimePath(int count)
    {
        var forward = Enumerable.Range(0, count).Select(order => ScalingTests.ExecutionIndex(0, order, count)).ToArray();
        var reverse = Enumerable.Range(0, count).Select(order => ScalingTests.ExecutionIndex(1, order, count)).ToArray();
        var repeated = Enumerable.Range(0, count).Select(order => ScalingTests.ExecutionIndex(2, order, count)).ToArray();
        Assert.Equal(Enumerable.Range(0, count), forward);
        Assert.Equal(forward.Reverse(), reverse);
        Assert.Equal(forward, repeated);
    }

    [Fact]
    public void ScalingComparisonNormalizesEqualOfferedWorkAndReportsLatencyDistribution()
    {
        var environment = JsonSerializer.SerializeToElement(new { ProcessorCount = 4, SiloProcessorCount = 1, GCConserveMemory = 9 });
        var disabled = new ScalingSample(
            "CurrentDisabled", 100, 100, "stable", 0, 4, 400,
            new("candidate", "same-binary"), environment,
            39600, 800000, 4000000, 2000, 1000000, [40, 10, 30, 20]);
        var enabled = disabled with
        {
            RuntimePath = "CurrentEnabledSupported",
            TotalRpcs = 4000,
            SerializedBytesSent = 400000,
            AllocatedBytes = 2000000,
            CpuMilliseconds = 1000,
            ConvergenceMilliseconds = [30, 10, 20, 10],
        };

        var result = ScalingComparison.Create(JsonSerializer.Serialize(new[] { enabled, disabled }));

        Assert.Equal(0, result.IncompletePairs);
        var pair = Assert.Single(result.Pairs);
        Assert.Equal(100, pair.Size);
        Assert.Equal(400, pair.OfferedPublications);
        Assert.Equal(99, pair.Disabled.RpcsPerPublication);
        Assert.Equal(10, pair.Enabled.RpcsPerPublication);
        Assert.Equal(2000, pair.Disabled.SerializedBytesPerPublication);
        Assert.Equal(1000, pair.Enabled.SerializedBytesPerPublication);
        Assert.Equal(10000, pair.Disabled.AllocatedBytesPerPublication);
        Assert.Equal(5000, pair.Enabled.AllocatedBytesPerPublication);
        Assert.Equal(5, pair.Disabled.CpuMillisecondsPerPublication);
        Assert.Equal(2.5, pair.Enabled.CpuMillisecondsPerPublication);
        Assert.Equal(25, pair.Disabled.MedianConvergenceMilliseconds);
        Assert.Equal(15, pair.Enabled.MedianConvergenceMilliseconds);
        Assert.Equal(40, pair.Disabled.P95ConvergenceMilliseconds);
        Assert.Equal(30, pair.Enabled.P95ConvergenceMilliseconds);
        Assert.Contains("99.00 -> 10.00", result.ToMarkdown(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("workload")]
    [InlineData("environment")]
    [InlineData("silo-count")]
    public void ScalingComparisonRejectsIncomparableMeasurements(string mismatch)
    {
        var sample = new ScalingSample(
            "CurrentDisabled", 100, 100, "stable", 0, 3, 300,
            new("candidate", "same-binary"), JsonSerializer.SerializeToElement(new { ProcessorCount = 4 }),
            100, 1000, 10000, 100, 1000, [1, 2, 3]);
        var enabled = sample with { RuntimePath = "CurrentEnabledSupported" };
        enabled = mismatch switch
        {
            "binary" => enabled with { Runtime = new("different", "other-binary") },
            "workload" => enabled with { OfferedPublications = 301 },
            "environment" => enabled with { Environment = JsonSerializer.SerializeToElement(new { ProcessorCount = 1 }) },
            "silo-count" => enabled with { LiveSilos = 99 },
            _ => throw new InvalidOperationException(mismatch),
        };

        Assert.Throws<InvalidOperationException>(() => ScalingComparison.Create(JsonSerializer.Serialize(new[] { sample, enabled })));
    }

    [Fact]
    public void ScalingComparisonReportsPartialPairsExplicitly()
    {
        var sample = new ScalingSample(
            "CurrentDisabled", 8, 8, "stable", 0, 3, 24,
            new("candidate", "same-binary"), JsonSerializer.SerializeToElement(new { ProcessorCount = 4 }),
            100, 1000, 10000, 100, 1000, [1, 2, 3]);

        var result = ScalingComparison.Create(JsonSerializer.Serialize(new[] { sample }));

        Assert.Empty(result.Pairs);
        Assert.Equal(1, result.IncompletePairs);
    }

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
    public void MessageObservation_KeepsRequestOneWayAndBytesDistinct()
    {
        using var observation = new Observation();
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
        using var observation = new Observation();
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
        using var observation = new Observation();
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
