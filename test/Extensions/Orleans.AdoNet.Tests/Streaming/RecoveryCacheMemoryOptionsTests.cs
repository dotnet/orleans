using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.AdoNet;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.Streams;
using TestExtensions;

namespace Tester.AdoNet.Streaming;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
public sealed class RecoveryCacheMemoryOptionsTests
{
    [Fact]
    public void AdoNet_EncodedCacheBudgetDefaultsTo64MiB()
    {
        var options = Options();

        Assert.Equal(64L * 1024 * 1024, options.MaxCacheSizeBytes);
        new AdoNetStreamOptionsValidator(options, "memory").ValidateConfiguration();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AdoNet_RejectsNonPositiveEncodedCacheBudget(long bytes)
    {
        var options = Options();
        options.MaxCacheSizeBytes = bytes;

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => new AdoNetStreamOptionsValidator(options, "memory").ValidateConfiguration());

        Assert.Contains(nameof(AdoNetStreamOptions.MaxCacheSizeBytes), exception.Message);
        Assert.Contains("memory", exception.Message);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(4L * 1024 * 1024 * 1024)]
    [InlineData(long.MaxValue)]
    public void AdoNet_AcceptsPositiveInt64EncodedCacheBudget(long bytes)
    {
        var options = Options();
        options.MaxCacheSizeBytes = bytes;

        new AdoNetStreamOptionsValidator(options, "memory").ValidateConfiguration();

        Assert.Equal(bytes, options.MaxCacheSizeBytes);
    }

    [Fact]
    public async Task AdoNet_PartitionsApplyConfiguredByteBudgetAndStageOneRead()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<AdoNetBatchContainer>>();
        var options = Options();
        options.MaxCacheSizeBytes = 128 * 1024;
        options.MaxMessagesPerRead = 8;
        var firstEvent = new string('a', 192 * 1024);
        var secondEvent = new string('b', 256 * 1024);
        var storage = new MemoryStorage();
        var receivers = new List<AdoNetQueueAdapterReceiver>();
        foreach (var queue in new[] { "first", "second" })
        {
            var stream = StreamId.Create("memory", queue);
            var payloads = new[]
            {
                AdoNetBatchContainer.ToMessagePayload(serializer, stream, [firstEvent], null),
                AdoNetBatchContainer.ToMessagePayload(serializer, stream, [secondEvent], null),
            };
            Assert.All(payloads, payload => Assert.True(payload.LongLength > options.MaxCacheSizeBytes));
            storage.Partitions.Add(queue, (stream, payloads));
            receivers.Add(new AdoNetQueueAdapterReceiver(
                "provider",
                queue,
                options,
                new ClusterOptions { ServiceId = "service" },
                new SimpleQueueCacheOptions { CacheSize = 8 },
                CreateQueries(storage),
                serializer,
                NullLogger<AdoNetQueueAdapterReceiver>.Instance));
        }

        try
        {
            foreach (var receiver in receivers)
            {
                var first = Assert.Single(await receiver.GetQueueMessagesAsync(8, CancellationToken.None));
                Assert.Equal(1, first.SequenceToken.SequenceNumber);
                AssertEvent(receiver, first, firstEvent);
                Assert.Equal(0, receiver.GetMaxAddCount());
                Assert.True(receiver.IsUnderPressure());
                Assert.Empty(await receiver.GetQueueMessagesAsync(8, CancellationToken.None));
            }

            Assert.Equal(2, storage.ReadRequests.Count);
            receivers[1].UpdateDeliveryProgress(new EventSequenceTokenV2(1), DateTime.UnixEpoch);
            Assert.Equal(8, receivers[1].GetMaxAddCount());
            var second = Assert.Single(await receivers[1].GetQueueMessagesAsync(8, CancellationToken.None));
            Assert.Equal(2, second.SequenceToken.SequenceNumber);
            Assert.Equal(StreamId.Create("memory", "second"), second.StreamId);
            AssertEvent(receivers[1], second, secondEvent);
            Assert.Equal(2, storage.ReadRequests.Count);
            Assert.True(receivers[0].IsUnderPressure());
            Assert.Empty(await receivers[0].GetQueueMessagesAsync(8, CancellationToken.None));

            receivers[1].UpdateDeliveryProgress(new EventSequenceTokenV2(2), DateTime.UnixEpoch);
            Assert.Empty(await receivers[1].GetQueueMessagesAsync(8, CancellationToken.None));
            Assert.Equal([0L, 2L], storage.ReadRequests
                .Where(request => request.Queue == "second")
                .Select(request => request.After));
            Assert.Equal([0L], storage.ReadRequests
                .Where(request => request.Queue == "first")
                .Select(request => request.After));
            Assert.All(storage.ReadRequests, request => Assert.Equal(8, request.Count));
        }
        finally
        {
            foreach (var receiver in receivers)
            {
                await receiver.Shutdown(Timeout.InfiniteTimeSpan);
            }
        }

        Assert.Equal(2, storage.Checkpoints["second"]);
        Assert.False(storage.Checkpoints.ContainsKey("first"));
    }

    private static AdoNetStreamOptions Options()
        => new()
        {
            Invariant = AdoNetInvariants.InvariantNameSqlServer,
            ConnectionString = "configured",
        };

    private static void AssertEvent(IQueueCache receiver, IBatchContainer notification, string expected)
    {
        using var cursor = receiver.GetCacheCursor(notification.StreamId, notification.SequenceToken);
        Assert.True(cursor.MoveNext());
        var batch = cursor.GetCurrent(out var exception);
        Assert.Null(exception);
        var item = Assert.Single(Assert.IsType<AdoNetBatchContainer>(batch).GetEvents<string>());
        Assert.Equal(expected, item.Item1);
        Assert.Equal(notification.SequenceToken.SequenceNumber, item.Item2.SequenceNumber);
    }

    private static RelationalOrleansQueries CreateQueries(IRelationalStorage storage)
    {
        var queries = typeof(DbStoredQueries)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(property => property.Name, property =>
                property.Name == nameof(DbStoredQueries.StreamSchemaVersionKey) ? "2" : property.Name);
        return new(storage, new DbStoredQueries(queries));
    }

    private sealed class MemoryStorage : IRelationalStorage
    {
        public Dictionary<string, (StreamId Stream, byte[][] Payloads)> Partitions { get; } = [];
        public Dictionary<string, long> Checkpoints { get; } = [];
        public List<(string Queue, long After, int Count)> ReadRequests { get; } = [];
        public string InvariantName => AdoNetInvariants.InvariantNameSqlServer;
        public string ConnectionString => "configured";

        public async Task<IEnumerable<TResult>> ReadAsync<TResult>(
            string query,
            Action<IDbCommand>? parameterProvider,
            Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = new SqlCommand();
            parameterProvider?.Invoke(command);
            var queue = (string)command.Parameters["QueueId"].Value;
            var partition = Partitions[queue];
            var rows = new List<Dictionary<string, object?>>();
            switch (query)
            {
                case nameof(DbStoredQueries.AcquireStreamPartitionKey):
                case nameof(DbStoredQueries.GetStreamPartitionBoundsKey):
                    rows.Add(new()
                    {
                        ["ServiceId"] = "service",
                        ["ProviderId"] = "provider",
                        ["QueueId"] = queue,
                        ["OwnerEpoch"] = 1L,
                        ["NextMessageId"] = (long)partition.Payloads.Length + 1,
                        ["Checkpoint"] = Checkpoints.GetValueOrDefault(queue),
                        ["EarliestMessageId"] = 1L,
                        ["TailMessageId"] = (long)partition.Payloads.Length,
                    });
                    break;
                case nameof(DbStoredQueries.ReadStreamMessagesKey):
                    var after = (long)command.Parameters["AfterMessageId"].Value;
                    var count = (int)command.Parameters["MaxCount"].Value;
                    ReadRequests.Add((queue, after, count));
                    for (var i = (int)after; i < Math.Min(partition.Payloads.Length, after + count); i++)
                    {
                        rows.Add(new()
                        {
                            ["ServiceId"] = "service",
                            ["ProviderId"] = "provider",
                            ["QueueId"] = queue,
                            ["MessageId"] = (long)i + 1,
                            ["StreamIdBytes"] = partition.Stream.FullKey.ToArray(),
                            ["StreamNamespaceLength"] = partition.Stream.Namespace.Length,
                            ["CreatedOn"] = DateTime.UnixEpoch,
                            ["Payload"] = partition.Payloads[i],
                        });
                    }

                    break;
                case nameof(DbStoredQueries.CleanupStreamMessagesKey):
                    rows.Add(new()
                    {
                        ["Ran"] = false,
                        ["DeletedCount"] = 0,
                        ["DeletedThroughMessageId"] = null,
                        ["HardDeletedCount"] = 0,
                        ["HardDeletedFromMessageId"] = null,
                        ["HardDeletedThroughMessageId"] = null,
                        ["Checkpoint"] = Checkpoints.GetValueOrDefault(queue),
                        ["EarliestMessageId"] = 1L,
                        ["TailMessageId"] = (long)partition.Payloads.Length,
                    });
                    break;
                case nameof(DbStoredQueries.AdvanceStreamCheckpointKey):
                    var checkpoint = (long)command.Parameters["Checkpoint"].Value;
                    Checkpoints[queue] = checkpoint;
                    rows.Add(new()
                    {
                        ["ServiceId"] = "service",
                        ["ProviderId"] = "provider",
                        ["QueueId"] = queue,
                        ["OwnerEpoch"] = 1L,
                        ["Checkpoint"] = checkpoint,
                        ["Updated"] = true,
                    });
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected query: {query}");
            }

            var result = new List<TResult>();
            if (rows.Count > 0)
            {
                using var table = new DataTable();
                foreach (var name in rows[0].Keys)
                {
                    table.Columns.Add(name, typeof(object));
                }

                foreach (var row in rows)
                {
                    table.Rows.Add(table.Columns.Cast<DataColumn>()
                        .Select(column => row[column.ColumnName] ?? DBNull.Value).ToArray());
                }

                using var reader = table.CreateDataReader();
                while (reader.Read())
                {
                    result.Add(await selector(reader, 0, cancellationToken));
                }
            }

            return result;
        }

        public Task<int> ExecuteAsync(
            string query,
            Action<IDbCommand>? parameterProvider,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"Unexpected command: {query}");
    }
}
