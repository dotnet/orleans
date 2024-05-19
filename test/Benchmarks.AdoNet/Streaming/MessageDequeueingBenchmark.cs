using System.Data.SqlClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using static System.String;

namespace Benchmarks.AdoNet.Streaming;

public class SqlServerMessageDequeuingBenchmark() : MessageDequeuingBenchmark(AdoNetInvariants.InvariantNameSqlServer, "OrleansStreamTest")
{
}

/// <summary>
/// This benchmark measures the performance of message queueing.
/// </summary>
[InProcess, WarmupCount(1), IterationCount(3), InvocationCount(1)]
public abstract class MessageDequeuingBenchmark(string invariant, string database)
{
    private const int OperationsPerInvoke = 1000;

    private readonly Consumer _consumer = new();
    private IRelationalStorage _storage = default!;
    private RelationalOrleansQueries _queries = default!;
    private byte[] _payload = [];
    private string[] _queueIds = default!;
    private AdoNetStreamMessageAck[] _acks = [];

    /// <summary>
    /// This highlights degradation from database locking.
    /// </summary>
    [Params(1, 10, 100)]
    public int QueueCount { get; set; }

    /// <summary>
    /// This highlights degradation from payload size.
    /// </summary>
    [Params(1000, 10000, 100000)]
    public int PayloadSize { get; set; }

    /// <summary>
    /// This highlights degradation from concurrency.
    /// </summary>
    [Params(1, 10, 100)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        Async().GetAwaiter().GetResult();

        async Task Async()
        {
            _payload = new byte[PayloadSize];
            Array.Fill<byte>(_payload, 0xFF);

            _queueIds = Enumerable.Range(0, QueueCount).Select(i => $"QueueId-{i}").ToArray();

            var testing = await RelationalStorageForTesting.SetupInstance(invariant, database);

            if (IsNullOrEmpty(testing.CurrentConnectionString))
            {
                throw new InvalidOperationException($"Database '{database}' not initialized");
            }

            _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);

            _queries = await RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString);

            SqlConnection.ClearAllPools();
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        Async().GetAwaiter().GetResult();

        async Task Async()
        {
            await _storage.ExecuteAsync("TRUNCATE TABLE OrleansStreamMessage");

            var count = OperationsPerInvoke * Concurrency;
            _acks = new AdoNetStreamMessageAck[count];

            await Parallel.ForAsync(0, count, async (i, ct) =>
            {
                var queueId = _queueIds[Random.Shared.Next(_queueIds.Length)];
                var ack = await _queries.QueueStreamMessageAsync("ServiceId-0", "ProviderId-0", queueId, _payload, 1000);
                _acks[i] = ack;
            });
        }
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public async Task GetStreamMessages()
    {
        await Parallel.ForEachAsync(_acks, new ParallelOptions { MaxDegreeOfParallelism = Concurrency }, async (ack, ct) =>
        {
            var message = await _queries.GetStreamMessagesAsync("ServiceId-0", "ProviderId-0", ack.QueueId, 1, 1, 1000, 1000, 1000, 1000);

            _consumer.Consume(message);
        });
    }
}
