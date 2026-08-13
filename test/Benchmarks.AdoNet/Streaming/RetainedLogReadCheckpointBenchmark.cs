using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Data.SqlClient;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using UnitTests.General;
using static System.String;

namespace Benchmarks.AdoNet.Streaming;

public class SqlServerRetainedLogReadCheckpointBenchmark() : RetainedLogReadCheckpointBenchmark(AdoNetInvariants.InvariantNameSqlServer, "OrleansStreamTest")
{
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        SqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Measures exclusive ordered reads and epoch-fenced checkpoint updates across partitions.
/// </summary>
[WarmupCount(1), IterationCount(3), InvocationCount(1), MarkdownExporter]
public abstract class RetainedLogReadCheckpointBenchmark(string invariant, string database)
{
    private const int OperationsPerInvoke = 1_000;

    private readonly Consumer _consumer = new();
    private IRelationalStorage _storage = default!;
    private RelationalOrleansQueries _queries = default!;
    private byte[] _payload = [];
    private byte[] _streamIdBytes = [];
    private string[] _partitionIds = [];
    private long[] _ownerEpochs = [];

    [Params(1, 8)]
    public int PartitionCount { get; set; }

    [Params(1, 32, 256)]
    public int BatchSize { get; set; }

    [Params(1_000)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        _payload = new byte[PayloadSize];
        new Random(42).NextBytes(_payload);
        _streamIdBytes = "benchmarkstream-0"u8.ToArray();
        _partitionIds = Enumerable.Range(0, PartitionCount).Select(i => $"QueueId-{i}").ToArray();
        _ownerEpochs = new long[PartitionCount];

        var testing = RelationalStorageForTesting.SetupInstance(invariant, database).GetAwaiter().GetResult();
        if (IsNullOrEmpty(testing.CurrentConnectionString))
        {
            throw new InvalidOperationException($"Database '{database}' not initialized");
        }

        _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);
        _queries = RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString).GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _storage.ExecuteAsync(
            "TRUNCATE TABLE OrleansStreamMessage; TRUNCATE TABLE OrleansStreamPartition").GetAwaiter().GetResult();

        for (var i = 0; i < OperationsPerInvoke * PartitionCount; i++)
        {
            _queries.AppendStreamMessageAsync(
                "ServiceId-0",
                "ProviderId-0",
                _partitionIds[i % PartitionCount],
                _streamIdBytes,
                9,
                _payload).GetAwaiter().GetResult();
        }

        for (var i = 0; i < PartitionCount; i++)
        {
            _ownerEpochs[i] = _queries.AcquireStreamPartitionAsync(
                "ServiceId-0",
                "ProviderId-0",
                _partitionIds[i],
                startFromNow: false).GetAwaiter().GetResult().OwnerEpoch;
        }
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    [BenchmarkCategory("OrderedRead")]
    public async Task ReadExclusiveOrderedBatches()
    {
        await Parallel.ForAsync(0, OperationsPerInvoke, async (i, cancellationToken) =>
        {
            var messages = await _queries.ReadStreamMessagesAsync(
                "ServiceId-0",
                "ProviderId-0",
                _partitionIds[i % PartitionCount],
                afterMessageId: 0,
                BatchSize);
            _consumer.Consume(messages);
        });
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    [BenchmarkCategory("Checkpoint")]
    public async Task AdvanceEpochFencedCheckpoints()
    {
        await Parallel.ForAsync(0, OperationsPerInvoke, async (i, cancellationToken) =>
        {
            var partition = i % PartitionCount;
            var checkpoint = (i / PartitionCount) + 1L;
            var result = await _queries.AdvanceStreamCheckpointAsync(
                "ServiceId-0",
                "ProviderId-0",
                _partitionIds[partition],
                _ownerEpochs[partition],
                checkpoint);
            if (result is not null)
            {
                _consumer.Consume(result);
            }
        });
    }
}

public class SqlServerRetainedLogCleanupBenchmark() : RetainedLogCleanupBenchmark(AdoNetInvariants.InvariantNameSqlServer, "OrleansStreamTest")
{
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        SqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Measures bounded cleanup sweeps while varying partition count, batch size, and eligible-row density.
/// </summary>
[WarmupCount(1), IterationCount(3), InvocationCount(1), MarkdownExporter]
public abstract class RetainedLogCleanupBenchmark(string invariant, string database)
{
    private const int MessagesPerPartition = 1_000;

    private readonly Consumer _consumer = new();
    private IRelationalStorage _storage = default!;
    private RelationalOrleansQueries _queries = default!;
    private byte[] _payload = [];
    private byte[] _streamIdBytes = [];
    private string[] _partitionIds = [];

    [Params(1, 8)]
    public int PartitionCount { get; set; }

    [Params(16, 256)]
    public int CleanupBatchSize { get; set; }

    [Params(0.0, 0.5, 1.0)]
    public double CleanupImpactRatio { get; set; }

    [Params(1_000)]
    public int PayloadSize { get; set; }

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
    public void IterationSetup()
    {
        _storage.ExecuteAsync(
            "TRUNCATE TABLE OrleansStreamMessage; TRUNCATE TABLE OrleansStreamPartition").GetAwaiter().GetResult();

        for (var partition = 0; partition < PartitionCount; partition++)
        {
            for (var message = 0; message < MessagesPerPartition; message++)
            {
                _queries.AppendStreamMessageAsync(
                    "ServiceId-0",
                    "ProviderId-0",
                    _partitionIds[partition],
                    _streamIdBytes,
                    9,
                    _payload).GetAwaiter().GetResult();
            }

            var state = _queries.AcquireStreamPartitionAsync(
                "ServiceId-0",
                "ProviderId-0",
                _partitionIds[partition],
                startFromNow: false).GetAwaiter().GetResult();
            _queries.AdvanceStreamCheckpointAsync(
                "ServiceId-0",
                "ProviderId-0",
                _partitionIds[partition],
                state.OwnerEpoch,
                MessagesPerPartition).GetAwaiter().GetResult();
        }

        var eligiblePerPartition = (int)(MessagesPerPartition * CleanupImpactRatio);
        _storage.ExecuteAsync(
            $"""
            WITH Ranked AS
            (
                SELECT
                    ServiceId,
                    ProviderId,
                    QueueId,
                    MessageId,
                    ROW_NUMBER() OVER (PARTITION BY QueueId ORDER BY MessageId) AS RowNumber
                FROM OrleansStreamMessage
            )
            UPDATE Message
            SET CreatedOn = DATEADD(DAY, -2, SYSUTCDATETIME())
            FROM OrleansStreamMessage AS Message
            INNER JOIN Ranked
                ON Ranked.ServiceId = Message.ServiceId
                AND Ranked.ProviderId = Message.ProviderId
                AND Ranked.QueueId = Message.QueueId
                AND Ranked.MessageId = Message.MessageId
            WHERE Ranked.RowNumber <= {eligiblePerPartition};

            UPDATE OrleansStreamPartition SET CleanupOn = DATEADD(SECOND, -1, SYSUTCDATETIME());
            """).GetAwaiter().GetResult();
    }

    [Benchmark]
    [BenchmarkCategory("Cleanup", "CleanupImpact")]
    public async Task CleanupRetainedPartitions()
    {
        var results = await Task.WhenAll(_partitionIds.Select(partitionId =>
            _queries.CleanupStreamMessagesAsync(
                "ServiceId-0",
                "ProviderId-0",
                partitionId,
                retentionPeriodSeconds: 86_400,
                maximumRetentionPeriodSeconds: null,
                cleanupIntervalSeconds: 60,
                CleanupBatchSize)));
        _consumer.Consume(results);
    }
}
