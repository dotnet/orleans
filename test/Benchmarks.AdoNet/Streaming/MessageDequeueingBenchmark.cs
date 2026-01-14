using Microsoft.Data.SqlClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using static System.String;

namespace Benchmarks.AdoNet.Streaming;

public class SqlServerMessageDequeuingBenchmark() : MessageDequeuingBenchmark(AdoNetInvariants.InvariantNameSqlServer, "OrleansStreamTest")
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
    /// This highlights degradation from queue concurrency.
    /// </summary>
    [Params(1, 4, 8)]
    public int QueueCount { get; set; }

    /// <summary>
    /// This highlights degradation from payload size.
    /// </summary>
    [Params(10000)]
    public int PayloadSize { get; set; }

    /// <summary>
    /// This highlights variation according to batch size.
    /// </summary>
    [Params(1, 16, 32)]
    public int BatchSize { get; set; }

    /// <summary>
    /// This highlights variation from how full the table is.
    /// </summary>
    [Params(0, 0.5, 1)]
    public double FullnessRatio { get; set; }

    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        Async().GetAwaiter().GetResult();

        async Task Async()
        {
            // create an appropriate size payload
            _payload = new byte[PayloadSize];
            Array.Fill<byte>(_payload, 0xFF);

            // define the set queues
            _queueIds = Enumerable.Range(0, QueueCount).Select(i => $"QueueId-{i}").ToArray();

            // setup the test database
            var testing = await RelationalStorageForTesting.SetupInstance(invariant, database);
            if (IsNullOrEmpty(testing.CurrentConnectionString))
            {
                throw new InvalidOperationException($"Database '{database}' not initialized");
            }
            _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);
            _queries = await RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString);
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        Async().GetAwaiter().GetResult();

        async Task Async()
        {
            await _storage.ExecuteAsync("TRUNCATE TABLE OrleansStreamMessage");

            // generate test data to dequeue
            var count = (int)Math.Ceiling(OperationsPerInvoke * QueueCount * BatchSize * FullnessRatio);
            _acks = new AdoNetStreamMessageAck[count];
            await Parallel.ForAsync(0, count, async (i, ct) =>
            {
                // generate messages in round robin queue order to help simulate multiple agents
                var queueId = _queueIds[i % _queueIds.Length];
                var ack = await _queries.QueueStreamMessageAsync("ServiceId-0", "ProviderId-0", queueId, _payload, 1000);

                _acks[i] = ack;
            });
        }
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public async Task GetStreamMessages()
    {
        var count = OperationsPerInvoke * QueueCount;

        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = QueueCount }, async (i, ct) =>
        {
            // get a queue id in round robin order to help simulate multiple agents
            var queueId = _queueIds[i % _queueIds.Length];

            // get messages for the queue of the ack
            // the queue may or may not have data to dequeue depending on the fullness and batch size parameters
            // we dequeue regardless in order to measure overhead
            var messages = await _queries.GetStreamMessagesAsync("ServiceId-0", "ProviderId-0", queueId, BatchSize, 1, 1000, 1000, 1000, 1000);

            _consumer.Consume(messages);
        });
    }
}
