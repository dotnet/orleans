using System.Data.SqlClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using static System.String;

namespace Benchmarks.AdoNet.Streaming;

public class SqlServerMessageQueueingBenchmark() : MessageQueueingBenchmark(AdoNetInvariants.InvariantNameSqlServer, "OrleansStreamTest")
{
}

/// <summary>
/// This benchmark measures the performance of message queueing.
/// </summary>
[InProcess, WarmupCount(1), IterationCount(3)]
public abstract class MessageQueueingBenchmark(string invariant, string database)
{
    private readonly Consumer _consumer = new();
    private IRelationalStorage _storage = default!;
    private RelationalOrleansQueries _queries = default!;
    private byte[] _payload = [];
    private string[] _queueIds = default!;

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

    [GlobalSetup]
    public void GlobalSetup()
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

        SqlConnection.ClearAllPools();
    }

    [IterationSetup]
    public void IterationSetup() => _storage.ExecuteAsync("TRUNCATE TABLE OrleansStreamMessage").GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = 100)]
    public Task QueueStreamMessage_1() => QueueStreamMessage(100, 1);

    [Benchmark(OperationsPerInvoke = 100)]
    public Task QueueStreamMessage_10() => QueueStreamMessage(1000, 10);

    [Benchmark(OperationsPerInvoke = 100)]
    public Task QueueStreamMessage_100() => QueueStreamMessage(10000, 100);

    /// <summary>
    /// This highlights degradation from concurrency.
    /// </summary>
    private async Task QueueStreamMessage(int count, int concurrency)
    {
        var done = 0;
        var completed = new TaskCompletionSource();
        using var cancelled = new CancellationTokenSource();
        using var semaphore = new SemaphoreSlim(concurrency);

        // this deferred loop acts as a bounded pipeline of active tasks
        for (var i = 0; i < count; i++)
        {
            await semaphore.WaitAsync(cancelled.Token);

            Task.Run(async () =>
            {
                try
                {
                    var queueId = _queueIds[Random.Shared.Next(_queueIds.Length)];
                    var ack = await _queries.QueueStreamMessageAsync("ServiceId-0", "ProviderId-0", queueId, _payload, 1000);

                    _consumer.Consume(ack);

                    if (Interlocked.Increment(ref done) == count)
                    {
                        completed.SetResult();
                    }
                }
                catch (Exception ex)
                {
                    completed.SetException(ex);
                    cancelled.Cancel();
                }
                finally
                {
                    semaphore.Release();
                }
            }).Ignore();
        }

        await completed.Task;
    }
}
