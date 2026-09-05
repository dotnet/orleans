using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Npgsql;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using static System.String;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/> against SQL Server.
/// </summary>
[TestCategory("SqlServer"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("SqlServer")]
[TestSuite("Functional")]
public class SqlServerAdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : AdoNetQueueAdapterTests(AdoNetInvariants.InvariantNameSqlServer, fixture)
{
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/> against MySQL.
/// </summary>
[TestCategory("MySql"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("MySql")]
[TestSuite("Functional")]
public class MySqlAdoNetQueueAdapterTests : AdoNetQueueAdapterTests
{
    public MySqlAdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : base(AdoNetInvariants.InvariantNameMySql, fixture)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/> against PostgreSQL.
/// </summary>
[TestCategory("PostgreSql"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("PostgreSql")]
[TestSuite("Functional")]
public class PostgreSqlAdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : AdoNetQueueAdapterTests(AdoNetInvariants.InvariantNamePostgreSql, fixture)
{
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("Functional")]
[TestArea("Streaming")]
public abstract class AdoNetQueueAdapterTests(string invariant, TestEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly TestEnvironmentFixture _fixture = fixture;
    private RelationalStorageForTesting _testing = null!;
    private IRelationalStorage _storage = null!;
    private RelationalOrleansQueries _queries = null!;

    private const string TestDatabaseName = "OrleansStreamTest";

    public async ValueTask InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(
            invariant,
            TestDatabaseName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.SkipWhen(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
        _queries = await RelationalOrleansQueries.CreateInstance(invariant, _testing.CurrentConnectionString);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> constructs with the expected state.
    /// </summary>
    [Fact]
    public void AdoNetQueueAdapter_Constructs()
    {
        // arrange
        var name = "MyProviderId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = invariant,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var mapper = new AdoNetStreamQueueMapper(new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions { TotalQueueCount = 8 }, "MyQueue"));
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var serviceProvider = _fixture.Services;

        // act
        var adapter = new AdoNetQueueAdapter(name, streamOptions, clusterOptions, cacheOptions, mapper, _queries, serializer, logger, serviceProvider);

        // assert
        Assert.Equal(name, adapter.Name);
        Assert.False(adapter.IsRewindable);
        Assert.Equal(StreamProviderDirection.ReadWrite, adapter.Direction);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> can enqueue messages.
    /// </summary>
    [Fact]
    public async Task AdoNetQueueAdapter_EnqueuesMessages()
    {
        // arrange
        var serviceId = "MyServiceId";
        var clusterOptions = new ClusterOptions
        {
            ServiceId = serviceId
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var providerId = "MyProviderId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = invariant,
            ConnectionString = _storage.ConnectionString
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var hashOptions = new HashRingStreamQueueMapperOptions { TotalQueueCount = 8 };
        var hashMapper = new HashRingBasedStreamQueueMapper(hashOptions, "MyQueue");
        var adoNetMapper = new AdoNetStreamQueueMapper(hashMapper);
        var adoNetQueueId = adoNetMapper.GetAdoNetQueueId(streamId);
        var adapter = new AdoNetQueueAdapter(providerId, streamOptions, clusterOptions, cacheOptions, adoNetMapper, _queries, serializer, logger, _fixture.Services);
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };

        // act - enqueue (via adapter) some messages
        var beforeEnqueued = DateTime.UtcNow.AddSeconds(-1);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(1) }, null!, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(2) }, null!, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(3) }, null!, context);
        var afterEnqueued = DateTime.UtcNow.AddSeconds(1);

        // assert - stored messages are as expected
        var stored = (await _queries.ReadStreamMessagesAsync(
            serviceId,
            providerId,
            adoNetQueueId,
            afterMessageId: 0,
            maxCount: 100,
            TestContext.Current.CancellationToken))
            .OrderBy(static message => message.MessageId)
            .ToList();
        Assert.Equal(3, stored.Count);
        for (var i = 0; i < stored.Count; i++)
        {
            var item = stored[i];

            Assert.Equal(serviceId, item.ServiceId);
            Assert.Equal(providerId, item.ProviderId);
            Assert.Equal(adoNetQueueId, item.QueueId);
            Assert.NotEqual(0, item.MessageId);
            Assert.Equal(streamId.FullKey.ToArray(), item.StreamIdBytes);
            Assert.Equal(streamId.Namespace.Length, item.StreamNamespaceLength);
            Assert.Equal(streamId, item.StreamId);
            Assert.True(item.CreatedOn >= beforeEnqueued);
            Assert.True(item.CreatedOn <= afterEnqueued);

            var serializedContainer = serializer.Deserialize(item.Payload);
            Assert.NotNull(serializedContainer);
            Assert.NotNull(serializedContainer.RequestContext);
            Assert.Equal(streamId, serializedContainer.StreamId);
            Assert.Null(serializedContainer.SequenceToken);
            Assert.Equal(new[] { new TestModel(i + 1) }, serializedContainer.Events);
            Assert.Single(serializedContainer.RequestContext);
            Assert.Equal("MyValue", serializedContainer.RequestContext["MyKey"]);
            Assert.Equal(0, serializedContainer.Dequeued);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquisitionCancellation_BlockedCommandSettlesBeforeReplacement(bool cancelRead)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = $"service-{Guid.NewGuid():N}";
        var providerId = $"provider-{Guid.NewGuid():N}";
        var queueId = QueueId.GetQueueId($"queue-{Guid.NewGuid():N}", 0, 0);
        var partitionId = queueId.ToString();
        var initial = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, partitionId, false, cancellationToken);
        var streamId = StreamId.Create("acquisition", Guid.NewGuid());
        var appended = await _queries.AppendStreamMessageAsync(
            serviceId, providerId, partitionId, streamId.FullKey.ToArray(), streamId.Namespace.Length, [1]);
        QueueAdapterReceiverRegistry<AdoNetQueueAdapterReceiver> registry = null!;
        registry = new(_ =>
        {
            var receiver = new AdoNetQueueAdapterReceiver(
                providerId,
                partitionId,
                new AdoNetStreamOptions { StartFromNow = false },
                new ClusterOptions { ServiceId = serviceId },
                new SimpleQueueCacheOptions(),
                _queries,
                _fixture.Serializer.GetSerializer<AdoNetBatchContainer>(),
                NullLogger<AdoNetQueueAdapterReceiver>.Instance);
            receiver.OnShutdown = stopped => registry.Remove(queueId, stopped);
            return receiver;
        });
        var first = registry.GetOrCreate(queueId);

        await using var lockConnection = CreateConnection();
        await lockConnection.OpenAsync(cancellationToken);
        await using var transaction = await lockConnection.BeginTransactionAsync(cancellationToken);
        await using (var command = lockConnection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE OrleansStreamPartition SET ModifiedOn = @ModifiedOn
                WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId
                """;
            AddParameter(command, "ServiceId", serviceId);
            AddParameter(command, "ProviderId", providerId);
            AddParameter(command, "QueueId", partitionId);
            AddParameter(command, "ModifiedOn", DateTime.UtcNow.AddDays(-1));
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        await using var ownerCommand = lockConnection.CreateCommand();
        ownerCommand.Transaction = transaction;
        ownerCommand.CommandText = invariant switch
        {
            AdoNetInvariants.InvariantNameSqlServer => "SELECT @@SPID",
            AdoNetInvariants.InvariantNameMySql => "SELECT CONNECTION_ID()",
            AdoNetInvariants.InvariantNamePostgreSql => "SELECT pg_backend_pid()",
            _ => throw new NotSupportedException(invariant),
        };
        var lockOwner = Convert.ToInt64(await ownerCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await using var observerConnection = CreateConnection();
        await observerConnection.OpenAsync(cancellationToken);
        await using var observer = observerConnection.CreateCommand();
        observer.CommandText = GetBlockedAcquisitionQuery(observerConnection);
        AddParameter(observer, "LockOwner", lockOwner);
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lockReleased = false;
        Task<IList<IBatchContainer>> read = null!;
        try
        {
            var blocked = WaitForBlockedAcquisitions(1);
            read = first.GetQueueMessagesAsync(1, readCancellation.Token);
            await blocked;
            Assert.False(read.IsCompleted);
            Assert.Same(first, registry.GetOrCreate(queueId));

            if (cancelRead)
            {
                readCancellation.Cancel();
            }

            var shutdown = first.Shutdown(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            await WaitForBlockedAcquisitions(0);

            await transaction.RollbackAsync(cancellationToken);
            lockReleased = true;
            var afterCancellation = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, partitionId, cancellationToken);
            Assert.Equal(initial.OwnerEpoch, afterCancellation!.OwnerEpoch);
            Assert.Equal(initial.Checkpoint, afterCancellation.Checkpoint);

            var replacement = registry.GetOrCreate(queueId);
            Assert.NotSame(first, replacement);
            try
            {
                await replacement.Initialize(TimeSpan.FromSeconds(20));
                var acquired = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, partitionId, cancellationToken);
                Assert.Equal(initial.OwnerEpoch + 1, acquired!.OwnerEpoch);
                var advanced = await _queries.AdvanceStreamCheckpointAsync(
                    serviceId, providerId, partitionId, acquired.OwnerEpoch, appended.MessageId, cancellationToken);
                Assert.True(advanced!.Updated);
                Assert.Equal(appended.MessageId, advanced.Checkpoint);
                Assert.Equal(acquired.OwnerEpoch, advanced.OwnerEpoch);
                var final = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, partitionId, cancellationToken);
                Assert.Equal(acquired.OwnerEpoch, final!.OwnerEpoch);
                Assert.Equal(appended.MessageId, final.Checkpoint);
            }
            finally
            {
                await replacement.Shutdown(TimeSpan.FromSeconds(20));
            }
        }
        finally
        {
            readCancellation.Cancel();
            if (!lockReleased)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            await first.Shutdown(TimeSpan.FromSeconds(20));
            if (read is not null)
            {
                await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        Task WaitForBlockedAcquisitions(int expected)
            => TestingUtils.WaitUntilAsync(
                async (lastTry, token) =>
                {
                    if (expected > 0 && read is { IsCompleted: true })
                    {
                        await read;
                        Assert.Fail("Acquisition completed before the held partition lock was released.");
                    }

                    var actual = Convert.ToInt32(await observer.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                    if (lastTry)
                    {
                        Assert.True(actual == expected,
                            $"{invariant} partition {serviceId}/{providerId}/{partitionId}: expected {expected} acquisitions blocked by session {lockOwner}, observed {actual}.");
                    }

                    return actual == expected;
                },
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMilliseconds(50),
                cancellationToken,
                predicateExpression: $"{invariant} acquisition lock wait count for session {lockOwner} becomes {expected}");
    }

    private DbConnection CreateConnection()
        => invariant switch
        {
            AdoNetInvariants.InvariantNameSqlServer => new SqlConnection(_storage.ConnectionString),
            AdoNetInvariants.InvariantNameMySql => new MySqlConnection(_storage.ConnectionString),
            AdoNetInvariants.InvariantNamePostgreSql => new NpgsqlConnection(_storage.ConnectionString),
            _ => throw new NotSupportedException(invariant),
        };

    private string GetBlockedAcquisitionQuery(DbConnection connection)
        => invariant switch
        {
            AdoNetInvariants.InvariantNameSqlServer =>
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @LockOwner",
            AdoNetInvariants.InvariantNamePostgreSql =>
                "SELECT COUNT(*) FROM pg_locks WHERE NOT granted AND @LockOwner = ANY(pg_blocking_pids(pid))",
            AdoNetInvariants.InvariantNameMySql when connection.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) =>
                """
                SELECT COUNT(*) FROM information_schema.INNODB_LOCK_WAITS AS W
                INNER JOIN information_schema.INNODB_TRX AS B ON B.trx_id = W.blocking_trx_id
                WHERE B.trx_mysql_thread_id = @LockOwner
                """,
            AdoNetInvariants.InvariantNameMySql =>
                """
                SELECT COUNT(*) FROM performance_schema.data_lock_waits AS W
                INNER JOIN performance_schema.threads AS B ON B.THREAD_ID = W.BLOCKING_THREAD_ID
                WHERE B.PROCESSLIST_ID = @LockOwner
                """,
            _ => throw new NotSupportedException(invariant),
        };

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);
}
