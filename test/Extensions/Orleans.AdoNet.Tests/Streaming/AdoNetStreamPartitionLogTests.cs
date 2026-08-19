using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySql.Data.MySqlClient;
using Npgsql;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using Orleans.Streaming.AdoNet.Storage;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests the immutable partition-log storage layer via <see cref="RelationalOrleansQueries"/> against Sql Server.
/// </summary>
[TestCategory("SqlServer"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("SqlServer")]
[TestSuite("Functional")]
public class SqlServerAdoNetStreamPartitionLogTests() : AdoNetStreamPartitionLogTests(AdoNetInvariants.InvariantNameSqlServer)
{
    /// <summary>
    /// Concurrent appends to the same partition must be serialized by the partition-row lock, and a
    /// rolled-back append must not permanently burn its allocated message identifier.
    /// </summary>
    [SkippableFact]
    public Task AppendStreamMessage_ConcurrentAppends_AreSerializedAndRollbackDoesNotBurnIds() =>
        VerifySqlServerConcurrentAppendsAreSerializedAndRollbackDoesNotBurnIds();

    /// <summary>
    /// A reader must never observe a message allocated by a transaction that has not yet committed.
    /// </summary>
    [SkippableFact]
    public Task ReadStreamMessages_ExcludesUncommittedInFlightAppend() =>
        VerifySqlServerReadExcludesUncommittedInFlightAppend();

    [SkippableFact]
    public Task AppendStreamMessage_DifferentPartitionsDoNotShareTheAllocationLock() =>
        VerifySqlServerPartitionsAreIndependent();
}

/// <summary>
/// Tests the immutable partition-log storage layer via <see cref="RelationalOrleansQueries"/> against MySQL.
/// </summary>
[TestCategory("MySql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("MySql")]
[TestSuite("Functional")]
public class MySqlAdoNetStreamPartitionLogTests : AdoNetStreamPartitionLogTests
{
    public MySqlAdoNetStreamPartitionLogTests() : base(AdoNetInvariants.InvariantNameMySql)
    {
        MySqlConnection.ClearAllPools();
    }

    [SkippableFact]
    public Task AppendStreamMessage_RollbackRestoresAllocation() => VerifyMySqlRollbackRestoresAllocation();
}

/// <summary>
/// Tests the immutable partition-log storage layer via <see cref="RelationalOrleansQueries"/> against PostgreSQL.
/// </summary>
[TestCategory("PostgreSql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("PostgreSql")]
[TestSuite("Functional")]
public class PostgreSqlAdoNetStreamPartitionLogTests : AdoNetStreamPartitionLogTests
{
    public PostgreSqlAdoNetStreamPartitionLogTests() : base(AdoNetInvariants.InvariantNamePostgreSql)
    {
        NpgsqlConnection.ClearAllPools();
    }

    [SkippableFact]
    public Task AppendStreamMessage_RollbackRestoresAllocation() => VerifyPostgreSqlRollbackRestoresAllocation();
}

/// <summary>
/// Tests the immutable partition-log storage layer via <see cref="RelationalOrleansQueries"/>: transactional
/// append with rollback-safe allocation, exclusive ordered reads, epoch-fenced monotonic checkpoints,
/// bounded retention/cleanup with hard-ceiling diagnostics, partition independence, and explicit schema
/// version/query-key enforcement.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("Functional")]
[TestArea("Streaming")]
public abstract class AdoNetStreamPartitionLogTests(string invariant) : IAsyncLifetime
{
    private const string TestDatabaseName = "OrleansStreamTest";

    private IRelationalStorage _storage = null!;
    private RelationalOrleansQueries _queries = null!;

    public async Task InitializeAsync()
    {
        var testing = await RelationalStorageForTesting.SetupInstance(invariant, TestDatabaseName);
        Skip.If(IsNullOrEmpty(testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);
        _queries = await RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    #region Helpers

    // A large range keeps identifiers effectively unique across the partition-independence tests,
    // which (unlike the single-partition tests) need two distinct queue ids within the same test.
    private static string RandomServiceId(int max = 1_000_000) => $"ServiceId{Random.Shared.Next(max)}";

    private static string RandomProviderId(int max = 1_000_000) => $"ProviderId{Random.Shared.Next(max)}";

    private static string RandomQueueId(int max = 1_000_000) => $"QueueId{Random.Shared.Next(max)}";

    private static byte[] RandomPayload(int size = 128)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    private static (byte[] StreamIdBytes, int StreamNamespaceLength) RandomStreamKey()
    {
        var streamId = StreamId.Create($"ns-{Guid.NewGuid():N}", Guid.NewGuid());
        return (streamId.FullKey.ToArray(), streamId.Namespace.Length);
    }

    private Task<AdoNetStreamMessageAck> AppendAsync(string serviceId, string providerId, string queueId, byte[]? payload = null)
    {
        var (streamIdBytes, nsLength) = RandomStreamKey();
        return _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, nsLength, payload ?? RandomPayload());
    }

    private Task AgePartitionMessagesAsync(string serviceId, string providerId, string queueId) =>
        _storage.ExecuteAsync(
            """
            UPDATE OrleansStreamMessage
            SET CreatedOn = @CreatedOn
            WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId
            """,
            command =>
            {
                AddParameter(command, "CreatedOn", DateTime.UtcNow.AddDays(-2));
                AddParameter(command, "ServiceId", serviceId);
                AddParameter(command, "ProviderId", providerId);
                AddParameter(command, "QueueId", queueId);
            });

    private Task MakeCleanupDueAsync(string serviceId, string providerId, string queueId) =>
        _storage.ExecuteAsync(
            """
            UPDATE OrleansStreamPartition
            SET CleanupOn = @CleanupOn
            WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId
            """,
            command =>
            {
                AddParameter(command, "CleanupOn", DateTime.UtcNow.AddSeconds(-1));
                AddParameter(command, "ServiceId", serviceId);
                AddParameter(command, "ProviderId", providerId);
                AddParameter(command, "QueueId", queueId);
            });

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    #endregion Helpers

    #region Append: sequential allocation and partition independence

    [SkippableFact]
    public async Task AppendStreamMessage_AllocatesSequentialMessageIdsPerPartition()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var first = await AppendAsync(serviceId, providerId, queueId);
        var second = await AppendAsync(serviceId, providerId, queueId);
        var third = await AppendAsync(serviceId, providerId, queueId);

        Assert.Equal(1, first.MessageId);
        Assert.Equal(2, second.MessageId);
        Assert.Equal(3, third.MessageId);
    }

    [SkippableFact]
    public async Task AppendStreamMessage_ConcurrentAppendsAreGapFree()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        const int count = 32;

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => AppendAsync(serviceId, providerId, queueId)));

        Assert.Equal(Enumerable.Range(1, count).Select(static value => (long)value), results.Select(static result => result.MessageId).Order());
    }

    [SkippableFact]
    public async Task AppendStreamMessage_PartitionsAreIndependent()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueIdA = RandomQueueId();
        var queueIdB = RandomQueueId();

        await AppendAsync(serviceId, providerId, queueIdA);
        var secondA = await AppendAsync(serviceId, providerId, queueIdA);
        var firstB = await AppendAsync(serviceId, providerId, queueIdB);

        Assert.Equal(2, secondA.MessageId);
        Assert.Equal(1, firstB.MessageId);

        var boundsA = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, queueIdA);
        var boundsB = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, queueIdB);

        Assert.Equal(2, boundsA!.TailMessageId);
        Assert.Equal(1, boundsB!.TailMessageId);
        Assert.Equal(1, boundsA.EarliestMessageId);
        Assert.Equal(1, boundsB.EarliestMessageId);
    }

    [SkippableFact]
    public async Task AppendStreamMessage_ValidatesStreamNamespaceBoundary()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var (streamIdBytes, _) = RandomStreamKey();

        // An empty stream key (no bytes at all) is never a valid canonical StreamId.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, [], 0, payload));

        // A negative boundary cannot separate namespace from key.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, -1, payload));

        // A boundary consuming the entire key would leave an empty stream key, which is invalid.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, streamIdBytes.Length, payload));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, null!, 0, payload));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, 0, null!));

        // Valid boundaries must NOT throw: a zero-length namespace (entire key, no namespace) and a
        // namespace that consumes every byte except the last (a single-byte key) are both legal, and
        // each still allocates the next sequential message identifier as normal.
        var zeroNamespaceAck = await _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, 0, payload);
        Assert.Equal(1, zeroNamespaceAck.MessageId);
        var maxNamespaceAck = await _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, streamIdBytes.Length - 1, payload);
        Assert.Equal(2, maxNamespaceAck.MessageId);
    }

    [SkippableFact]
    public async Task AppendStreamMessage_PersistsCanonicalStreamIdFullKeyAndNamespaceBoundary()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var streamId = StreamId.Create("orders", Guid.NewGuid());
        var payload = RandomPayload();

        var ack = await _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamId.FullKey.ToArray(), streamId.Namespace.Length, payload);
        var messages = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 10);
        var stored = Assert.Single(messages);

        Assert.Equal(ack.MessageId, stored.MessageId);
        Assert.True(streamId.FullKey.Span.SequenceEqual(stored.StreamIdBytes));
        Assert.Equal(streamId.Namespace.Length, stored.StreamNamespaceLength);
        Assert.Equal(streamId, stored.StreamId);
        Assert.True(streamId.Namespace.Span.SequenceEqual(stored.StreamId.Namespace.Span));
        Assert.True(streamId.Key.Span.SequenceEqual(stored.StreamId.Key.Span));
        Assert.Equal(payload, stored.Payload);
    }

    #endregion Append

    #region Acquire: ownership, epoch, and checkpoint initialization

    [SkippableFact]
    public async Task AcquireStreamPartition_InitializesCheckpointBeforeEarliestWhenNotStartingFromNow()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await AppendAsync(serviceId, providerId, queueId);
        await AppendAsync(serviceId, providerId, queueId);

        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);

        Assert.Equal(1, state.OwnerEpoch);
        Assert.Equal(0, state.Checkpoint); // one before the earliest retained message (id 1)
        Assert.Equal(1, state.EarliestMessageId);
        Assert.Equal(2, state.TailMessageId);
    }

    [SkippableFact]
    public async Task AcquireStreamPartition_InitializesCheckpointAtTailWhenStartingFromNow()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await AppendAsync(serviceId, providerId, queueId);
        var last = await AppendAsync(serviceId, providerId, queueId);

        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: true);

        Assert.Equal(last.MessageId, state.Checkpoint);
        Assert.Equal(last.MessageId, state.TailMessageId);
    }

    [SkippableFact]
    public async Task AcquireStreamPartition_OnNeverAppendedPartitionHasNoBounds()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);

        Assert.Equal(1, state.OwnerEpoch);
        Assert.Equal(0, state.Checkpoint);
        Assert.Null(state.EarliestMessageId);
        Assert.Null(state.TailMessageId);
    }

    [SkippableFact]
    public async Task AcquireStreamPartition_ReacquisitionIncrementsOwnerEpochAndPreservesCheckpoint()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await AppendAsync(serviceId, providerId, queueId);

        var first = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);
        await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, first.OwnerEpoch, 1);

        var second = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);

        Assert.Equal(first.OwnerEpoch + 1, second.OwnerEpoch);
        Assert.Equal(1, second.Checkpoint); // preserved from the prior owner, not re-initialized
    }

    [SkippableFact]
    public async Task AcquireStreamPartition_OwnerEpochAndCheckpointArePerPartition()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueIdA = RandomQueueId();
        var queueIdB = RandomQueueId();

        await AppendAsync(serviceId, providerId, queueIdA);
        await AppendAsync(serviceId, providerId, queueIdB);

        var stateA1 = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueIdA, startFromNow: false);
        await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueIdA, startFromNow: false);
        var stateB1 = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueIdB, startFromNow: false);

        var boundsA = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, queueIdA);
        var boundsB = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, queueIdB);

        Assert.Equal(stateA1.OwnerEpoch + 1, boundsA!.OwnerEpoch);
        Assert.Equal(stateB1.OwnerEpoch, boundsB!.OwnerEpoch);
    }

    #endregion Acquire

    #region Read: exclusive ordered ranges

    [SkippableFact]
    public async Task ReadStreamMessages_ReturnsExclusiveOrderedRangeRespectingMaxCount()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var acks = new List<AdoNetStreamMessageAck>();
        for (var i = 0; i < 5; i++)
        {
            acks.Add(await AppendAsync(serviceId, providerId, queueId));
        }

        var page = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: acks[1].MessageId, maxCount: 2);

        Assert.Equal([acks[2].MessageId, acks[3].MessageId], page.Select(m => m.MessageId));
        Assert.Equal(page.OrderBy(m => m.MessageId).Select(m => m.MessageId), page.Select(m => m.MessageId));
    }

    [SkippableFact]
    public async Task ReadStreamMessages_EmptyWhenAfterMessageIdIsAtOrBeyondTail()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var only = await AppendAsync(serviceId, providerId, queueId);

        var atTail = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: only.MessageId, maxCount: 10);
        var beyondTail = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: only.MessageId + 100, maxCount: 10);

        Assert.Empty(atTail);
        Assert.Empty(beyondTail);
    }

    [SkippableFact]
    public async Task ReadStreamMessages_ValidatesArgumentBounds()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: -1, maxCount: 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: -5));

        // Valid boundaries must NOT throw: afterMessageId = 0 (read from the very start) and
        // maxCount = 1 (the smallest legal page size) are both legal and return the expected message.
        var appended = await AppendAsync(serviceId, providerId, queueId);
        var page = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 1);
        var onlyMessage = Assert.Single(page);
        Assert.Equal(appended.MessageId, onlyMessage.MessageId);
    }

    #endregion Read

    #region Checkpoint: monotonicity, non-regression, and epoch fencing

    [SkippableFact]
    public async Task AdvanceStreamCheckpoint_AdvancesMonotonicallyAndRejectsRegression()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await AppendAsync(serviceId, providerId, queueId);
        await AppendAsync(serviceId, providerId, queueId);
        await AppendAsync(serviceId, providerId, queueId);

        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);

        var advanceTo2 = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, 2);
        Assert.NotNull(advanceTo2);
        Assert.True(advanceTo2!.Updated);
        Assert.Equal(2, advanceTo2.Checkpoint);

        var regressTo1 = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, 1);
        Assert.NotNull(regressTo1);
        Assert.False(regressTo1!.Updated);
        Assert.Equal(2, regressTo1.Checkpoint); // unchanged by the rejected regression

        var sameValue = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, 2);
        Assert.NotNull(sameValue);
        Assert.False(sameValue!.Updated); // strictly-forward only: re-affirming the same value is not an advance
        Assert.Equal(2, sameValue.Checkpoint);

        var advanceTo3 = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, 3);
        Assert.True(advanceTo3!.Updated);
        Assert.Equal(3, advanceTo3.Checkpoint);
    }

    [SkippableFact]
    public async Task AdvanceStreamCheckpoint_RejectsCheckpointAtOrBeyondTail()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var only = await AppendAsync(serviceId, providerId, queueId);
        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);

        // The checkpoint marks the last fully-processed message; it cannot reach or exceed the
        // not-yet-allocated next message identifier.
        var result = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, only.MessageId + 1);

        Assert.NotNull(result);
        Assert.False(result!.Updated);
    }

    [SkippableFact]
    public async Task AdvanceStreamCheckpoint_FencesStaleOwnerEpochAfterReacquisition()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await AppendAsync(serviceId, providerId, queueId);
        await AppendAsync(serviceId, providerId, queueId);

        var firstOwner = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);
        var secondOwner = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: false);
        Assert.True(secondOwner.OwnerEpoch > firstOwner.OwnerEpoch);

        // The former owner's epoch must be fenced: its checkpoint attempt must not apply.
        var staleAttempt = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, firstOwner.OwnerEpoch, 2);
        Assert.NotNull(staleAttempt);
        Assert.False(staleAttempt!.Updated);

        // The current owner's epoch must still be able to advance the checkpoint.
        var currentAttempt = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, secondOwner.OwnerEpoch, 2);
        Assert.NotNull(currentAttempt);
        Assert.True(currentAttempt!.Updated);
        Assert.Equal(2, currentAttempt.Checkpoint);
    }

    [SkippableFact]
    public async Task AdvanceStreamCheckpoint_ReturnsNullForUnknownPartition()
    {
        var result = await _queries.AdvanceStreamCheckpointAsync(RandomServiceId(), RandomProviderId(), RandomQueueId(), ownerEpoch: 1, checkpoint: 1);

        Assert.Null(result);
    }

    [SkippableFact]
    public async Task AdvanceStreamCheckpoint_ValidatesArgumentBounds()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, ownerEpoch: 0, checkpoint: 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, ownerEpoch: -1, checkpoint: 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, ownerEpoch: 1, checkpoint: -1));

        // Valid boundaries must NOT throw: ownerEpoch = 1 (the smallest legal epoch) and
        // checkpoint = 0 (the smallest legal checkpoint) are both legal argument values, even
        // though there is no matching partition row here (which is reported via a null result,
        // not an exception).
        var result = await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, ownerEpoch: 1, checkpoint: 0);
        Assert.Null(result);
    }

    #endregion Checkpoint

    #region Bounds: partition state reporting

    [SkippableFact]
    public async Task GetStreamPartitionBounds_ReflectsCurrentState()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        Assert.Null(await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, queueId));

        await AppendAsync(serviceId, providerId, queueId);
        var second = await AppendAsync(serviceId, providerId, queueId);
        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: true);
        await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, second.MessageId);

        var bounds = await _queries.GetStreamPartitionBoundsAsync(serviceId, providerId, queueId);

        Assert.NotNull(bounds);
        Assert.Equal(state.OwnerEpoch, bounds!.OwnerEpoch);
        Assert.Equal(second.MessageId, bounds.Checkpoint);
        Assert.Equal(1, bounds.EarliestMessageId);
        Assert.Equal(second.MessageId, bounds.TailMessageId);
    }

    #endregion Bounds

    #region Cleanup: retention, hard ceiling diagnostics, batching, and throttling

    [SkippableFact]
    public async Task CleanupStreamMessages_RemovesCheckpointedMessagesAfterRetentionElapses()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var acks = new List<AdoNetStreamMessageAck>();
        for (var i = 0; i < 3; i++)
        {
            acks.Add(await AppendAsync(serviceId, providerId, queueId));
        }

        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: true);
        await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, acks[^1].MessageId);

        await AgePartitionMessagesAsync(serviceId, providerId, queueId);

        var result = await _queries.CleanupStreamMessagesAsync(
            serviceId, providerId, queueId,
            retentionPeriodSeconds: 1,
            maximumRetentionPeriodSeconds: null,
            cleanupIntervalSeconds: 60,
            cleanupBatchSize: 100);

        Assert.True(result.Ran);
        Assert.Equal(3, result.DeletedCount);
        Assert.Equal(acks[^1].MessageId, result.DeletedThroughMessageId);
        Assert.Equal(0, result.HardDeletedCount);
        Assert.Null(result.HardDeletedFromMessageId);
        Assert.Null(result.HardDeletedThroughMessageId);
        Assert.Null(result.EarliestMessageId);
        Assert.Null(result.TailMessageId);

        var remaining = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 100);
        Assert.Empty(remaining);
    }

    [SkippableFact]
    public async Task CleanupStreamMessages_AppliesHardCeilingAndReportsDiagnostics()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var acks = new List<AdoNetStreamMessageAck>();
        for (var i = 0; i < 3; i++)
        {
            acks.Add(await AppendAsync(serviceId, providerId, queueId));
        }

        // No checkpoint is ever established, so these messages are ahead of the (null) checkpoint;
        // only the hard retention ceiling can force their removal, and that removal must be
        // reported distinctly from a normal, checkpoint-driven cleanup.
        await AgePartitionMessagesAsync(serviceId, providerId, queueId);

        var result = await _queries.CleanupStreamMessagesAsync(
            serviceId, providerId, queueId,
            retentionPeriodSeconds: 60,
            maximumRetentionPeriodSeconds: 120,
            cleanupIntervalSeconds: 60,
            cleanupBatchSize: 100);

        Assert.True(result.Ran);
        Assert.Equal(3, result.DeletedCount);
        Assert.Equal(3, result.HardDeletedCount);
        Assert.Equal(acks[0].MessageId, result.HardDeletedFromMessageId);
        Assert.Equal(acks[^1].MessageId, result.HardDeletedThroughMessageId);
        Assert.Null(result.Checkpoint);
    }

    [SkippableFact]
    public async Task CleanupStreamMessages_RespectsBatchSizeAcrossMultipleRuns()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var acks = new List<AdoNetStreamMessageAck>();
        for (var i = 0; i < 5; i++)
        {
            acks.Add(await AppendAsync(serviceId, providerId, queueId));
        }

        var state = await _queries.AcquireStreamPartitionAsync(serviceId, providerId, queueId, startFromNow: true);
        await _queries.AdvanceStreamCheckpointAsync(serviceId, providerId, queueId, state.OwnerEpoch, acks[^1].MessageId);

        await AgePartitionMessagesAsync(serviceId, providerId, queueId);

        var first = await _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, 1, null, cleanupIntervalSeconds: 1, cleanupBatchSize: 2);
        Assert.True(first.Ran);
        Assert.Equal(2, first.DeletedCount);

        await MakeCleanupDueAsync(serviceId, providerId, queueId);

        var second = await _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, 1, null, cleanupIntervalSeconds: 1, cleanupBatchSize: 2);
        Assert.True(second.Ran);
        Assert.Equal(2, second.DeletedCount);

        await MakeCleanupDueAsync(serviceId, providerId, queueId);

        var third = await _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, 1, null, cleanupIntervalSeconds: 1, cleanupBatchSize: 2);
        Assert.True(third.Ran);
        Assert.Equal(1, third.DeletedCount); // the final, partial batch

        var remaining = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 100);
        Assert.Empty(remaining);
    }

    [SkippableFact]
    public async Task CleanupStreamMessages_ThrottlesRepeatedRunsWithinInterval()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        await AppendAsync(serviceId, providerId, queueId);

        var first = await _queries.CleanupStreamMessagesAsync(
            serviceId, providerId, queueId,
            retentionPeriodSeconds: 1, maximumRetentionPeriodSeconds: null, cleanupIntervalSeconds: 60, cleanupBatchSize: 100);
        Assert.True(first.Ran);

        // Immediately repeating the call must be throttled by CleanupInterval and not run again.
        var second = await _queries.CleanupStreamMessagesAsync(
            serviceId, providerId, queueId,
            retentionPeriodSeconds: 1, maximumRetentionPeriodSeconds: null, cleanupIntervalSeconds: 60, cleanupBatchSize: 100);
        Assert.False(second.Ran);
        Assert.Equal(0, second.DeletedCount);
    }

    [SkippableFact]
    public async Task CleanupStreamMessages_ValidatesArgumentBounds()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, retentionPeriodSeconds: 0, maximumRetentionPeriodSeconds: null, cleanupIntervalSeconds: 1, cleanupBatchSize: 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, retentionPeriodSeconds: 1, maximumRetentionPeriodSeconds: null, cleanupIntervalSeconds: 0, cleanupBatchSize: 1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, retentionPeriodSeconds: 1, maximumRetentionPeriodSeconds: null, cleanupIntervalSeconds: 1, cleanupBatchSize: 0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, retentionPeriodSeconds: 10, maximumRetentionPeriodSeconds: 5, cleanupIntervalSeconds: 1, cleanupBatchSize: 1));

        // Valid boundaries must NOT throw: retentionPeriodSeconds/cleanupIntervalSeconds/cleanupBatchSize
        // of exactly 1 (the smallest legal values), and a maximumRetentionPeriodSeconds exactly equal to
        // retentionPeriodSeconds (the ceiling may legitimately coincide with the normal retention period).
        await AppendAsync(serviceId, providerId, queueId);
        var result = await _queries.CleanupStreamMessagesAsync(serviceId, providerId, queueId, retentionPeriodSeconds: 1, maximumRetentionPeriodSeconds: 1, cleanupIntervalSeconds: 1, cleanupBatchSize: 1);
        Assert.True(result.Ran);
    }

    #endregion Cleanup

    #region Explicit schema mismatch

    /// <summary>
    /// An old or partially-migrated schema that is missing partition-log query keys must fail
    /// explicitly and name the missing keys, rather than fail lazily or silently on first use.
    /// </summary>
    [SkippableFact]
    public async Task CreateInstance_MissingPartitionLogQueryKeys_FailsExplicitly()
    {
        await _storage.ExecuteAsync(
            "DELETE FROM OrleansQuery WHERE QueryKey IN ('AppendStreamMessageKey', 'StreamSchemaVersionKey')",
            command => { });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RelationalOrleansQueries.CreateInstance(invariant, _storage.ConnectionString));

        Assert.Contains("AppendStreamMessageKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("StreamSchemaVersionKey", exception.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task CreateInstance_MixedLegacyAndPartitionLogQueryKeys_FailsExplicitly()
    {
        await _storage.ExecuteAsync(
            "INSERT INTO OrleansQuery (QueryKey, QueryText) VALUES ('QueueStreamMessageKey', 'legacy')",
            command => { });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RelationalOrleansQueries.CreateInstance(invariant, _storage.ConnectionString));

        Assert.Contains("QueueStreamMessageKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no in-place migration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion Explicit schema mismatch

    #region SQL Server: raw-connection concurrency and rollback semantics

    /// <summary>
    /// Holds an append transaction open on one connection, proves a concurrent append on the same
    /// partition cannot proceed while it is in-flight (serialization/ordering), then rolls the first
    /// transaction back and proves the allocated identifier was not burned: the next successful
    /// append reuses it and no message row was left behind.
    /// </summary>
    protected async Task VerifySqlServerConcurrentAppendsAreSerializedAndRollbackDoesNotBurnIds()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var (streamIdBytes, nsLength) = RandomStreamKey();
        var payload = RandomPayload();

        await using var firstConnection = new SqlConnection(_storage.ConnectionString);
        await firstConnection.OpenAsync();
        await using var firstTransaction = (SqlTransaction)await firstConnection.BeginTransactionAsync();
        await using var firstCommand = CreateAppendCommand(firstConnection, firstTransaction, serviceId, providerId, queueId, streamIdBytes, nsLength, payload);
        var firstMessageId = await ReadMessageId(firstCommand);
        Assert.Equal(1, firstMessageId);

        // A concurrent append on the same partition must not be able to proceed while the first
        // transaction still holds the partition-row lock.
        await using (var secondConnection = new SqlConnection(_storage.ConnectionString))
        {
            await secondConnection.OpenAsync();
            await using var secondCommand = secondConnection.CreateCommand();
            secondCommand.CommandType = CommandType.Text;
            secondCommand.CommandText = "SET LOCK_TIMEOUT 0; EXECUTE AppendStreamMessage @ServiceId, @ProviderId, @QueueId, @StreamIdBytes, @StreamNamespaceLength, @Payload;";
            secondCommand.Parameters.AddWithValue("ServiceId", serviceId);
            secondCommand.Parameters.AddWithValue("ProviderId", providerId);
            secondCommand.Parameters.AddWithValue("QueueId", queueId);
            secondCommand.Parameters.AddWithValue("StreamIdBytes", streamIdBytes);
            secondCommand.Parameters.AddWithValue("StreamNamespaceLength", nsLength);
            secondCommand.Parameters.AddWithValue("Payload", payload);

            var exception = await Assert.ThrowsAsync<SqlException>(() => secondCommand.ExecuteReaderAsync());
            Assert.Equal(51000, exception.Number);
            Assert.Contains("initialization lock", exception.Message, StringComparison.Ordinal);
        }

        // Rolling back must not burn the allocated identifier: it is reused by the next append,
        // and no message row for it was left behind.
        await firstTransaction.RollbackAsync();

        var afterRollback = await _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, nsLength, payload);
        Assert.Equal(1, afterRollback.MessageId);

        var rows = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 100);
        var stored = Assert.Single(rows);
        Assert.Equal(1, stored.MessageId);
    }

    /// <summary>
    /// While an append transaction is in-flight and uncommitted, a reader must observe only
    /// previously-committed messages; once the transaction commits, the message becomes visible.
    /// </summary>
    protected async Task VerifySqlServerReadExcludesUncommittedInFlightAppend()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();

        var committed = await AppendAsync(serviceId, providerId, queueId);

        var (streamIdBytes, nsLength) = RandomStreamKey();
        var payload = RandomPayload();

        await using var connection = new SqlConnection(_storage.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = CreateAppendCommand(connection, transaction, serviceId, providerId, queueId, streamIdBytes, nsLength, payload);
        var inFlightMessageId = await ReadMessageId(command);
        Assert.Equal(committed.MessageId + 1, inFlightMessageId);

        var duringAppend = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 100);
        Assert.Equal([committed.MessageId], duringAppend.Select(m => m.MessageId));

        await transaction.CommitAsync();

        var afterCommit = await _queries.ReadStreamMessagesAsync(serviceId, providerId, queueId, afterMessageId: 0, maxCount: 100);
        Assert.Equal([committed.MessageId, inFlightMessageId], afterCommit.Select(m => m.MessageId));
    }

    protected async Task VerifySqlServerPartitionsAreIndependent()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var firstQueueId = RandomQueueId();
        var secondQueueId = RandomQueueId();
        var (streamIdBytes, nsLength) = RandomStreamKey();
        var payload = RandomPayload();

        await using var connection = new SqlConnection(_storage.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = CreateAppendCommand(connection, transaction, serviceId, providerId, firstQueueId, streamIdBytes, nsLength, payload);
        Assert.Equal(1, await ReadMessageId(command));

        var independent = await _queries.AppendStreamMessageAsync(serviceId, providerId, secondQueueId, streamIdBytes, nsLength, payload)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, independent.MessageId);

        await transaction.RollbackAsync();
    }

    protected async Task VerifyMySqlRollbackRestoresAllocation()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var (streamIdBytes, nsLength) = RandomStreamKey();
        var payload = RandomPayload();

        await using var connection = new MySqlConnection(_storage.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "CALL AppendStreamMessage(@ServiceId, @ProviderId, @QueueId, @StreamIdBytes, @StreamNamespaceLength, @Payload, FALSE)";
            command.Parameters.AddWithValue("ServiceId", serviceId);
            command.Parameters.AddWithValue("ProviderId", providerId);
            command.Parameters.AddWithValue("QueueId", queueId);
            command.Parameters.AddWithValue("StreamIdBytes", streamIdBytes);
            command.Parameters.AddWithValue("StreamNamespaceLength", nsLength);
            command.Parameters.AddWithValue("Payload", payload);
            Assert.Equal(1, await ReadMessageId(command));
        }

        await transaction.RollbackAsync();

        var afterRollback = await _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, nsLength, payload);
        Assert.Equal(1, afterRollback.MessageId);
    }

    protected async Task VerifyPostgreSqlRollbackRestoresAllocation()
    {
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var (streamIdBytes, nsLength) = RandomStreamKey();
        var payload = RandomPayload();

        await using var connection = new NpgsqlConnection(_storage.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT * FROM AppendStreamMessage(@ServiceId, @ProviderId, @QueueId, @StreamIdBytes, @StreamNamespaceLength, @Payload)";
            command.Parameters.AddWithValue("ServiceId", serviceId);
            command.Parameters.AddWithValue("ProviderId", providerId);
            command.Parameters.AddWithValue("QueueId", queueId);
            command.Parameters.AddWithValue("StreamIdBytes", streamIdBytes);
            command.Parameters.AddWithValue("StreamNamespaceLength", nsLength);
            command.Parameters.AddWithValue("Payload", payload);
            Assert.Equal(1, await ReadMessageId(command));
        }

        await transaction.RollbackAsync();

        var afterRollback = await _queries.AppendStreamMessageAsync(serviceId, providerId, queueId, streamIdBytes, nsLength, payload);
        Assert.Equal(1, afterRollback.MessageId);
    }

    private static SqlCommand CreateAppendCommand(
        SqlConnection connection,
        SqlTransaction? transaction,
        string serviceId,
        string providerId,
        string queueId,
        byte[] streamIdBytes,
        int streamNamespaceLength,
        byte[] payload)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "AppendStreamMessage";
        command.Parameters.AddWithValue("ServiceId", serviceId);
        command.Parameters.AddWithValue("ProviderId", providerId);
        command.Parameters.AddWithValue("QueueId", queueId);
        command.Parameters.AddWithValue("StreamIdBytes", streamIdBytes);
        command.Parameters.AddWithValue("StreamNamespaceLength", streamNamespaceLength);
        command.Parameters.AddWithValue("Payload", payload);
        return command;
    }

    private static async Task<long> ReadMessageId(DbCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetInt64(reader.GetOrdinal(nameof(AdoNetStreamMessage.MessageId)));
    }

    #endregion SQL Server
}
