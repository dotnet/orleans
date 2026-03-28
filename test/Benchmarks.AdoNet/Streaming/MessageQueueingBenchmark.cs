using Microsoft.Data.SqlClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using static System.String;

namespace Benchmarks.AdoNet.Streaming;

public class SqlServerMessageQueueingBenchmark() : MessageQueueingBenchmark(AdoNetInvariants.InvariantNameSqlServer, "OrleansStreamTest")
{
    public override void GlobalSetup()
    {
        base.GlobalSetup();

        SqlConnection.ClearAllPools();
    }
}

/// <summary>
/// This benchmark measures the performance of message queueing.
/// </summary>
[WarmupCount(1), IterationCount(3), InvocationCount(1), MarkdownExporter]
public abstract class MessageQueueingBenchmark(string invariant, string database)
{
    private const int OperationsPerInvoke = 1000;

    private readonly Consumer _consumer = new();
    private IRelationalStorage _storage = default!;
    private RelationalOrleansQueries _queries = default!;
    private byte[] _payload = [];
    private string[] _queueIds = default!;

    /// <summary>
    /// This highlights degradation from database locking.
    /// </summary>
    [Params(1, 4, 8)]
    public int QueueCount { get; set; }

    /// <summary>
    /// This highlights degradation from payload size.
    /// </summary>
    [Params(1000, 10000, 100000)]
    public int PayloadSize { get; set; }

    /// <summary>
    /// This highlights degradation from concurrency.
    /// </summary>
    [Params(1, 4, 8)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        _payload = new byte[PayloadSize];
        Array.Fill<byte>(_payload, 0xFF);

        _queueIds = Enumerable.Range(0, QueueCount).Select(i => $"QueueId-{i}").ToArray();

        var testing = RelationalStorageForTesting.SetupInstance(invariant, database).GetAwaiter().GetResult();

        if (IsNullOrEmpty(testing.CurrentConnectionString))
        {
            throw new InvalidOperationException($"Database '{database}' not initialized");
        }

        _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);

        _queries = RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString).GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void IterationSetup() => _storage.ExecuteAsync("TRUNCATE TABLE OrleansStreamMessage").GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public Task QueueStreamMessage()
    {
        var count = OperationsPerInvoke * Concurrency;

        return Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = Concurrency }, async (i, ct) =>
        {
            var queueId = _queueIds[Random.Shared.Next(_queueIds.Length)];
            var ack = await _queries.QueueStreamMessageAsync("ServiceId-0", "ProviderId-0", queueId, _payload, 1000);

            _consumer.Consume(ack);
        });
    }
}
