using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Data.SqlClient;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using static System.String;

namespace Benchmarks.AdoNet.Streaming;

public class SqlServerRetainedLogAppendBenchmark() : RetainedLogAppendBenchmark(AdoNetInvariants.InvariantNameSqlServer, "OrleansStreamTest")
{
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        SqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Measures immutable log append throughput while varying payload size, concurrency, and partition contention.
/// A partition count of one is the lock-wait baseline for database-side wait telemetry.
/// </summary>
[WarmupCount(1), IterationCount(3), InvocationCount(1), MarkdownExporter]
public abstract class RetainedLogAppendBenchmark(string invariant, string database)
{
    private const int OperationsPerInvoke = 1_000;

    private readonly Consumer _consumer = new();
    private IRelationalStorage _storage = default!;
    private RelationalOrleansQueries _queries = default!;
    private byte[] _payload = [];
    private byte[] _streamIdBytes = [];
    private string[] _partitionIds = [];

    [Params(1, 8)]
    public int PartitionCount { get; set; }

    [Params(1_000, 100_000)]
    public int PayloadSize { get; set; }

    [Params(1, 8)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        _payload = new byte[PayloadSize];
        new Random(42).NextBytes(_payload);
        _streamIdBytes = "benchmarkstream-0"u8.ToArray();
        _partitionIds = Enumerable.Range(0, PartitionCount).Select(i => $"QueueId-{i}").ToArray();

        var testing = RelationalStorageForTesting.SetupInstance(invariant, database).GetAwaiter().GetResult();
        if (IsNullOrEmpty(testing.CurrentConnectionString))
        {
            throw new InvalidOperationException($"Database '{database}' not initialized");
        }

        _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);
        _queries = RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString).GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void IterationSetup() => _storage.ExecuteAsync(
        "TRUNCATE TABLE OrleansStreamMessage; TRUNCATE TABLE OrleansStreamPartition").GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    [BenchmarkCategory("Append", "LockWait")]
    public Task AppendWithPartitionContention() =>
        Parallel.ForAsync(
            0,
            OperationsPerInvoke,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
            async (i, cancellationToken) =>
            {
                var result = await _queries.AppendStreamMessageAsync(
                    "ServiceId-0",
                    "ProviderId-0",
                    _partitionIds[i % _partitionIds.Length],
                    _streamIdBytes,
                    9,
                    _payload);
                _consumer.Consume(result);
            });
}
