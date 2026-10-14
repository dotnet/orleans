using System.Buffers;
using System.Diagnostics;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Measures append latency and throughput on an embedded partition on local disk.
/// </summary>
[TestCategory("Performance")]
public sealed class GarnetJournalLatencyTests(GarnetPartitionFixture fixture) : IClassFixture<GarnetPartitionFixture>
{
    [Theory]
    [InlineData(1, 2000, 128)]
    [InlineData(16, 1000, 128)]
    [InlineData(64, 500, 128)]
    public async Task MeasureAppendLatency(int writers, int appendsPerWriter, int payloadSize)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = new ReadOnlySequence<byte>(new byte[payloadSize]);
        var storages = Enumerable.Range(0, writers)
            .Select(index => fixture.Provider.CreateStorage(JournalId.Create("latency", Guid.NewGuid().ToString("N"))))
            .ToArray();
        foreach (var storage in storages)
        {
            await storage.AppendAsync(payload, cancellationToken);
        }

        var latencies = new double[writers * appendsPerWriter];
        var total = Stopwatch.StartNew();
        await Task.WhenAll(storages.Select((storage, writer) => Task.Run(async () =>
        {
            for (var i = 0; i < appendsPerWriter; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await storage.AppendAsync(payload, cancellationToken);
                latencies[writer * appendsPerWriter + i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }, cancellationToken)));
        total.Stop();

        Array.Sort(latencies);
        var message =
            $"writers={writers} appends={latencies.Length} payload={payloadSize}B: "
            + $"p50={Percentile(latencies, 0.50):F3}ms p99={Percentile(latencies, 0.99):F3}ms p99.9={Percentile(latencies, 0.999):F3}ms "
            + $"max={latencies[^1]:F3}ms throughput={latencies.Length / total.Elapsed.TotalSeconds:F0}/s";
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        TestContext.Current.SendDiagnosticMessage(message);
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percentile * sorted.Length) - 1)];
}
