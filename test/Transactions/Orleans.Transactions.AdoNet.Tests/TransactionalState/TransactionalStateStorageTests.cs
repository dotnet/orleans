// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Data;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.Entity;
using Orleans.Transactions.AdoNet.Tests.Fakes;
using Orleans.Transactions.AdoNet.TransactionalState;
using Orleans.Transactions.AdoNet.Utils;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

/// <summary>
/// Unit tests for <see cref="TransactionalStateStorage{TState}"/> Load/Store behaviour,
/// <see cref="DbBatchOperation"/> batching guards, and state transitions.
/// All tests use <see cref="FakeRelationalStorage"/> — no real database required.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class TransactionalStateStorageTests
{
    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>Simple state class used throughout tests.</summary>
    private sealed class SimpleState
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    private static readonly JsonSerializerSettings s_jsonSettings = new();

    /// <summary>
    /// Serialise <paramref name="obj"/> to UTF-8 bytes using plain Newtonsoft.Json.
    /// Matches what <see cref="Utils.JsonUtils.SerializeWithNewtonsoftJson"/> does.
    /// </summary>
    private static byte[] Serialize(object? obj)
        => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj, s_jsonSettings));

    /// <summary>
    /// Wire the fake to return a single <see cref="KeyEntity"/> for the key query and
    /// optionally one or more <see cref="StateEntity"/> rows for the state query.
    /// </summary>
    private static void SetupFakeRows(
        FakeRelationalStorage fake,
        TransactionalStateStorageOptions opts,
        KeyEntity keyEntity,
        IEnumerable<StateEntity>? stateEntities = null)
    {
        var stateRows = stateEntities?.ToArray() ?? [];
        fake.ReadResponseFactory = sql =>
        {
            if (sql == opts.ExecuteSqlDictionary[Constants.QueryKeySql])
                return new object[] { keyEntity };
            if (sql == opts.ExecuteSqlDictionary[Constants.QueryStateSql])
                return stateRows.Cast<object>();
            return [];
        };
    }

    /// <summary>Returns a default empty metadata instance.</summary>
    private static TransactionalStateMetaData EmptyMeta() => new()
    {
        CommitRecords = new Dictionary<Guid, CommitRecord>(),
        TimeStamp     = DateTime.UtcNow
    };

    private static IReadOnlyList<KeyValuePair<long, StateEntity>> GetTrackedStates(
        TransactionalStateStorage<SimpleState> storage)
    {
        var field = typeof(TransactionalStateStorage<SimpleState>)
            .GetField("stateEntityList", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("'stateEntityList' field not found.");
        return Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<long, StateEntity>>>(field.GetValue(storage));
    }

    // -----------------------------------------------------------------------
    // Load() — fresh state (no rows in DB)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Load_FreshState_ReturnsDefaultResponse()
    {
        var (sut, _, _) = StorageTestHarness.Create<SimpleState>();
        // ReadResponseFactory left null => ReadAsync returns empty for both queries.

        var response = await sut.Load();

        Assert.Null(response.ETag);
        Assert.Equal(0L, response.CommittedSequenceId);
        Assert.Empty(response.PendingStates);
    }

    // -----------------------------------------------------------------------
    // Load() — existing committed state
    // -----------------------------------------------------------------------

    [Fact]
    public void DatabaseKeyRecord_MapsAllFields()
    {
        var (sut, _, _) = StorageTestHarness.Create<SimpleState>();
        var table = new DataTable();
        table.Columns.Add(nameof(KeyEntity.StateId), typeof(string));
        table.Columns.Add(nameof(KeyEntity.CommittedSequenceId), typeof(long));
        table.Columns.Add(nameof(KeyEntity.Metadata), typeof(byte[]));
        table.Columns.Add(nameof(KeyEntity.ETag), typeof(string));
        table.Rows.Add("key-1", 7L, new byte[] { 1, 2 }, "etag-7");

        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var entity = InvokePrivate<KeyEntity>(sut, "GetConvertKeyRecord", reader);

        Assert.Equal("key-1", entity.StateId);
        Assert.Equal(7L, entity.CommittedSequenceId);
        Assert.Equal(new byte[] { 1, 2 }, entity.Metadata);
        Assert.Equal("etag-7", entity.ETag);
    }

    [Fact]
    public void DatabaseStateRecord_MapsAllFieldsAndPreservesTimestampTicks()
    {
        var (sut, _, _) = StorageTestHarness.Create<SimpleState>();
        var timestamp = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1);
        var table = new DataTable();
        table.Columns.Add(nameof(StateEntity.StateId), typeof(string));
        table.Columns.Add(nameof(StateEntity.SequenceId), typeof(long));
        table.Columns.Add(nameof(StateEntity.TransactionId), typeof(string));
        table.Columns.Add(nameof(StateEntity.TransactionTimestampTicks), typeof(long));
        table.Columns.Add(nameof(StateEntity.TransactionManager), typeof(byte[]));
        table.Columns.Add(nameof(StateEntity.StateData), typeof(byte[]));
        table.Columns.Add(nameof(StateEntity.ETag), typeof(string));
        table.Rows.Add("state-1", 3L, "tx-3", timestamp.Ticks, new byte[] { 9 }, new byte[] { 8 }, "etag-3");

        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var entity = InvokePrivate<StateEntity>(sut, "GetConvertStateRecord", reader);

        Assert.Equal("state-1", entity.StateId);
        Assert.Equal(3L, entity.SequenceId);
        Assert.Equal("tx-3", entity.TransactionId);
        Assert.Equal(timestamp.Ticks, entity.TransactionTimestampTicks);
        Assert.Equal(new byte[] { 9 }, entity.TransactionManager);
        Assert.Equal(new byte[] { 8 }, entity.StateData);
        Assert.Equal("etag-3", entity.ETag);
    }

    [Fact]
    public async Task Load_KeyWithETag_CommittedSeqId1_ReturnsETag()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-1",
            CommittedSequenceId = 1,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateEntity = new StateEntity
        {
            StateId    = "test-state-id",
            SequenceId = 1,
            ETag       = "etag-1",
            StateData   = Serialize(new SimpleState { Name = "Alice", Value = 42 }),
            TransactionManager = null!,
        };
        SetupFakeRows(fake, opts, keyEntity, new[] { stateEntity });

        var response = await sut.Load();

        Assert.Equal("etag-1", response.ETag);
        Assert.Equal(1L, response.CommittedSequenceId);
    }

    [Fact]
    public async Task Load_KeyWithETag_CommittedSeqId1_DeserializesCommittedState()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var expected = new SimpleState { Name = "Alice", Value = 42 };
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-1",
            CommittedSequenceId = 1,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateEntity = new StateEntity
        {
            StateId    = "test-state-id",
            SequenceId = 1,
            ETag       = "etag-1",
            StateData   = Serialize(expected),
            TransactionManager = null!,
        };
        SetupFakeRows(fake, opts, keyEntity, new[] { stateEntity });

        var response = await sut.Load();

        Assert.NotNull(response.CommittedState);
        Assert.Equal("Alice", response.CommittedState.Name);
        Assert.Equal(42, response.CommittedState.Value);
    }

    [Fact]
    public async Task Load_CommittedSeqIdZero_ETagSet_ReturnsDefaultCommittedState()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-1",
            CommittedSequenceId = 0,  // No committed state yet, but key row exists
            Metadata            = Serialize(EmptyMeta()),
        };
        SetupFakeRows(fake, opts, keyEntity, stateEntities: null);

        var response = await sut.Load();

        Assert.Equal("etag-1", response.ETag);
        Assert.NotNull(response.CommittedState); // default new SimpleState()
        Assert.Equal(string.Empty, response.CommittedState.Name);
        Assert.Equal(0, response.CommittedState.Value);
    }

    [Fact]
    public async Task Load_CommittedSeqIdNotFoundInStates_ThrowsInvalidOperationException()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        // Key says committed = 5, but no state row with SequenceId=5.
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-1",
            CommittedSequenceId = 5,
            Metadata            = Serialize(EmptyMeta()),
        };
        SetupFakeRows(fake, opts, keyEntity, stateEntities: null); // no state rows

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Load());
        Assert.Contains("corrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Load() — pending states recovery
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Load_PrepareRecords_AboveCommitted_AreRecovered()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-1",
            CommittedSequenceId = 1,
            Metadata            = Serialize(EmptyMeta()),
        };
        var secondTimestamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var thirdTimestamp = secondTimestamp.AddMinutes(1);
        // SequenceId=1 is committed; SequenceId=2,3 are pending with non-null TM.
        var stateRows = new[]
        {
            new StateEntity
            {
                StateId    = "test-state-id", SequenceId = 1, ETag = "etag-1",
                StateData   = Serialize(new SimpleState()), TransactionManager = null!,
            },
            new StateEntity
            {
                StateId    = "test-state-id", SequenceId = 2, ETag = "etag-2",
                StateData   = Serialize(new SimpleState { Name = "B" }),
                TransactionManager = Serialize(new { Name = "mgr", SupportedRoles = 0 }),
                TransactionId = "tx-2",
                TransactionTimestampTicks = secondTimestamp.UtcTicks,
            },
            new StateEntity
            {
                StateId    = "test-state-id", SequenceId = 3, ETag = "etag-3",
                StateData   = Serialize(new SimpleState { Name = "C" }),
                TransactionManager = Serialize(new { Name = "mgr", SupportedRoles = 0 }),
                TransactionId = "tx-3",
                TransactionTimestampTicks = thirdTimestamp.UtcTicks,
            },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);

        var response = await sut.Load();

        // Two pending states (SequenceId 2 and 3) should be recovered.
        Assert.Equal(2, response.PendingStates.Count);
        Assert.Equal(2L, response.PendingStates[0].SequenceId);
        Assert.Equal(3L, response.PendingStates[1].SequenceId);
        Assert.Equal(secondTimestamp.UtcDateTime, response.PendingStates[0].TimeStamp);
        Assert.Equal(thirdTimestamp.UtcDateTime, response.PendingStates[1].TimeStamp);
        Assert.Equal("tx-2", response.PendingStates[0].TransactionId);
        Assert.Equal("B", response.PendingStates[0].State.Name);
        Assert.Equal("tx-3", response.PendingStates[1].TransactionId);
        Assert.Equal("C", response.PendingStates[1].State.Name);
    }

    [Fact]
    public async Task Load_PrepareRecord_NullTransactionManager_StopsRecovery()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-1",
            CommittedSequenceId = 1,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateRows = new[]
        {
            new StateEntity
            {
                StateId    = "test-state-id", SequenceId = 1, ETag = "etag-1",
                StateData   = Serialize(new SimpleState()), TransactionManager = null!,
            },
            new StateEntity
            {
                StateId    = "test-state-id", SequenceId = 2, ETag = "etag-2",
                StateData   = Serialize(new SimpleState()),
                TransactionManager = null!, // null → recovery stops here
                TransactionId = "tx-2",
            },
            new StateEntity
            {
                StateId    = "test-state-id", SequenceId = 3, ETag = "etag-3",
                StateData   = Serialize(new SimpleState()),
                TransactionManager = Serialize(new { Name = "mgr" }), // would be recovered if loop continued
                TransactionId = "tx-3",
            },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);

        var response = await sut.Load();

        // Recovery stops at SequenceId=2 (TM is null) so SequenceId=3 is NOT recovered.
        Assert.Empty(response.PendingStates);
    }

    // -----------------------------------------------------------------------
    // Store() — ETag mismatch
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Store_ETagMismatch_ThrowsArgumentException()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId = "test-state-id", ETag = "correct-etag", CommittedSequenceId = 0,
            Metadata = Serialize(EmptyMeta()),
        };
        SetupFakeRows(fake, opts, keyEntity);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        // "wrong-etag" does not match "correct-etag"
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Store("wrong-etag", EmptyMeta(), null, null, null));

        Assert.Contains("Etag does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Store_BothETagsNull_DoesNotThrow()
    {
        // When both the stored ETag and the expectedETag are null/empty, no mismatch.
        var (sut, _, _) = StorageTestHarness.Create<SimpleState>();
        await sut.Load(); // fresh state → keyEntity.ETag = null

        // expectedETag = null, keyEntity.ETag = null → no throw
        var result = await sut.Store(null, EmptyMeta(), null, null, null);
        Assert.NotNull(result);  // returns the new ETag
        Assert.True(Guid.TryParseExact(result, "N", out _));
    }

    [Fact]
    public async Task Store_WhitespaceExpectedETag_IsTreatedAsEmpty()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        await sut.Load();

        var result = await sut.Store("   ", EmptyMeta(), null, null, null);

        Assert.True(Guid.TryParseExact(result, "N", out _));
        Assert.Contains(
            fake.TransactionCallLog.SelectMany(call => call),
            operation => operation.Item1 == opts.ExecuteSqlDictionary[Constants.AddKeySql]);
    }

    // -----------------------------------------------------------------------
    // Store() — fresh first write (inserts key row)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Store_FreshState_NoExistingETag_InsertsKeyEntity()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        await sut.Load(); // fresh — no DB rows

        await sut.Store(null, EmptyMeta(), null, null, null);

        Assert.Single(fake.TransactionCallLog);
        var ops = fake.TransactionCallLog[0];
        var addKeySql = opts.ExecuteSqlDictionary[Constants.AddKeySql];
        Assert.Contains(ops, t => t.Item1 == addKeySql);
    }

    [Fact]
    public async Task Store_FreshState_ReturnsNonNullNonEmptyETag()
    {
        var (sut, _, _) = StorageTestHarness.Create<SimpleState>();
        await sut.Load();

        var eTag = await sut.Store(null, EmptyMeta(), null, null, null);

        Assert.NotNull(eTag);
        Assert.True(Guid.TryParseExact(eTag, "N", out _));
    }

    [Fact]
    public async Task Store_FreshState_OnlyKeyInsertInBatch_NoStateInserts()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        await sut.Load();

        await sut.Store(null, EmptyMeta(), null, null, null);

        var ops = fake.TransactionCallLog[0];
        var addStateSql = opts.ExecuteSqlDictionary[Constants.AddStateSql];
        Assert.DoesNotContain(ops, t => t.Item1 == addStateSql);
    }

    // -----------------------------------------------------------------------
    // Store() — abort cleanup (phase 1 of Store)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Store_AbortAfter1_DeletesSequences2And3()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateRows = new[]
        {
            new StateEntity { StateId = "test-state-id", SequenceId = 1, ETag = "e1",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
            new StateEntity { StateId = "test-state-id", SequenceId = 2, ETag = "e2",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
            new StateEntity { StateId = "test-state-id", SequenceId = 3, ETag = "e3",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        await sut.Store("etag-k", EmptyMeta(), null, null, abortAfter: 1);

        var delStateSql = opts.ExecuteSqlDictionary[Constants.DelStateSql];
        var ops = fake.TransactionCallLog.SelectMany(call => call).ToList();
        var deleteSqls = ops.Where(t => t.Item1 == delStateSql).ToList();

        // SequenceId=2 and SequenceId=3 must be deleted; SequenceId=1 must NOT be deleted.
        Assert.Equal(2, deleteSqls.Count);
        Assert.Equal([1L], GetTrackedStates(sut).Select(entry => entry.Key));
    }

    [Fact]
    public async Task Store_AbortAfterNull_NoStateDeletes()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateRows = new[]
        {
            new StateEntity { StateId = "test-state-id", SequenceId = 1, ETag = "e1",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        // abortAfter = null → skip cleanup phase
        await sut.Store("etag-k", EmptyMeta(), null, null, abortAfter: null);

        var delStateSql = opts.ExecuteSqlDictionary[Constants.DelStateSql];
        var ops = fake.TransactionCallLog.SelectMany(call => call).ToList();
        Assert.DoesNotContain(ops, t => t.Item1 == delStateSql);
    }

    [Fact]
    public async Task Store_AbortAfterHighestSequence_LeavesHighestState()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId = "test-state-id", ETag = "etag-k", CommittedSequenceId = 0,
            Metadata = Serialize(EmptyMeta())
        };
        var stateRows = new[]
        {
            new StateEntity { StateId = "test-state-id", SequenceId = 1, ETag = "e1", StateData = Serialize(new SimpleState()), TransactionManager = null! },
            new StateEntity { StateId = "test-state-id", SequenceId = 2, ETag = "e2", StateData = Serialize(new SimpleState()), TransactionManager = null! }
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        await sut.Store("etag-k", EmptyMeta(), null, null, abortAfter: 2);

        var deleteSql = opts.ExecuteSqlDictionary[Constants.DelStateSql];
        Assert.DoesNotContain(fake.TransactionCallLog.SelectMany(call => call), operation => operation.Item1 == deleteSql);
    }

    // -----------------------------------------------------------------------
    // Store() — prepare state rows (phase 2 of Store)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Store_NewPendingState_InsertsStateEntity()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        await sut.Load(); // fresh
        fake.TransactionCallLog.Clear();

        var statesToPrepare = new List<PendingTransactionState<SimpleState>>
        {
            new() { SequenceId = 1, TransactionId = "tx-1",
                    TimeStamp = DateTime.UtcNow, State = new SimpleState { Name = "X" } }
        };

        await sut.Store(null, EmptyMeta(), statesToPrepare, null, null);

        var addStateSql = opts.ExecuteSqlDictionary[Constants.AddStateSql];
        var ops = fake.TransactionCallLog.SelectMany(call => call).ToList();
        Assert.Contains(ops, t => t.Item1 == addStateSql);
        var tracked = Assert.Single(GetTrackedStates(sut));
        Assert.Equal(1L, tracked.Key);
        Assert.Equal("tx-1", tracked.Value.TransactionId);
        Assert.Equal("X", JsonUtils.DeserializeWithNewtonsoftJson<SimpleState>(Assert.IsType<byte[]>(tracked.Value.StateData), s_jsonSettings).Name);
    }

    [Fact]
    public async Task Store_PrepareBelowCommitPoint_IsSkipped()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        await sut.Load();
        fake.TransactionCallLog.Clear();

        var pending = new List<PendingTransactionState<SimpleState>>
        {
            new() { SequenceId = 1, TransactionId = "obsolete", TimeStamp = DateTime.UnixEpoch, State = new SimpleState { Name = "ignored" } }
        };
        await sut.Store(null, EmptyMeta(), pending, commitUpTo: 2, abortAfter: null);

        var addStateSql = opts.ExecuteSqlDictionary[Constants.AddStateSql];
        Assert.DoesNotContain(fake.TransactionCallLog.SelectMany(call => call), operation => operation.Item1 == addStateSql);
    }

    [Fact]
    public async Task Store_Exactly128PendingStates_UsesOneAtomicTransaction()
    {
        var (sut, fake, _) = StorageTestHarness.Create<SimpleState>();
        await sut.Load();
        fake.TransactionCallLog.Clear();

        var pending = Enumerable.Range(1, 128)
            .Select(sequenceId => new PendingTransactionState<SimpleState>
            {
                SequenceId = sequenceId,
                TransactionId = $"tx-{sequenceId}",
                TimeStamp = DateTime.UnixEpoch,
                State = new SimpleState { Value = sequenceId }
            })
            .ToList();

        await sut.Store(null, EmptyMeta(), pending, null, null);

        var transaction = Assert.Single(fake.TransactionCallLog);
        Assert.Equal(129, transaction.Count);
    }

    [Fact]
    public async Task Store_ExistingPendingState_UpdatesStateEntity()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        // SequenceId=1 already exists with ETag="e1"
        var stateRows = new[]
        {
            new StateEntity { StateId = "test-state-id", SequenceId = 1, ETag = "e1",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        var statesToPrepare = new List<PendingTransactionState<SimpleState>>
        {
            new() { SequenceId = 1, TransactionId = "tx-1",
                    TimeStamp = DateTime.UtcNow, State = new SimpleState { Name = "Updated" } }
        };
        await sut.Store("etag-k", EmptyMeta(), statesToPrepare, null, null);

        var updateStateSql = opts.ExecuteSqlDictionary[Constants.UpdateStateSql];
        var ops = fake.TransactionCallLog.SelectMany(call => call).ToList();
        Assert.Equal(opts.ExecuteSqlDictionary[Constants.UpdateKeySql], ops[0].Item1);
        Assert.Contains(ops, t => t.Item1 == updateStateSql);
        var tracked = Assert.Single(GetTrackedStates(sut));
        Assert.Equal("tx-1", tracked.Value.TransactionId);
        Assert.Equal("Updated", JsonUtils.DeserializeWithNewtonsoftJson<SimpleState>(Assert.IsType<byte[]>(tracked.Value.StateData), s_jsonSettings).Name);
    }

    [Fact]
    public async Task Store_ObsoleteStatesBelowCommitPoint_AreDeleted()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateRows = new[]
        {
            new StateEntity { StateId = "test-state-id", SequenceId = 1, ETag = "e1",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
            new StateEntity { StateId = "test-state-id", SequenceId = 2, ETag = "e2",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
            new StateEntity { StateId = "test-state-id", SequenceId = 3, ETag = "e3",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        // Commit up to SequenceId=3 → SequenceId 1 and 2 become obsolete.
        await sut.Store("etag-k", EmptyMeta(), null, commitUpTo: 3, abortAfter: null);

        var delStateSql = opts.ExecuteSqlDictionary[Constants.DelStateSql];
        var ops = fake.TransactionCallLog.SelectMany(call => call).ToList();
        var deletes = ops.Where(t => t.Item1 == delStateSql).ToList();
        Assert.Equal(2, deletes.Count); // SequenceId 1 and 2 deleted
        Assert.Equal([3L], GetTrackedStates(sut).Select(entry => entry.Key));
    }

    // -----------------------------------------------------------------------
    // Store() — key entity upsert (phase 3 of Store)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Store_ExistingKeyEntity_UsesUpdateSql()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        SetupFakeRows(fake, opts, keyEntity);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        await sut.Store("etag-k", EmptyMeta(), null, null, null);

        var updateKeySql = opts.ExecuteSqlDictionary[Constants.UpdateKeySql];
        var ops = fake.TransactionCallLog.SelectMany(call => call).ToList();
        var updateKey = Assert.Single(ops, operation => operation.Item1 == updateKeySql);
        using var command = new SqlCommand();
        updateKey.Item2(command);
        Assert.Equal("etag-k", command.Parameters[Constants.PreviousETag].Value);
        Assert.NotEqual("etag-k", command.Parameters[nameof(KeyEntity.ETag)].Value);
    }

    [Fact]
    public async Task Store_CommitUpTo_AdvancesCommittedSequenceId()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateRows = new[]
        {
            new StateEntity { StateId = "test-state-id", SequenceId = 1, ETag = "e1",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        // Commit up to 1
        var newETag = await sut.Store("etag-k", EmptyMeta(), null, commitUpTo: 1, abortAfter: null);
        Assert.NotNull(newETag);
        Assert.NotEmpty(newETag);

        var keyField = typeof(TransactionalStateStorage<SimpleState>).GetField("keyEntity", BindingFlags.Instance | BindingFlags.NonPublic);
        var storedKey = Assert.IsType<KeyEntity>(keyField?.GetValue(sut));
        Assert.Equal(1L, storedKey.CommittedSequenceId);
    }

    // -----------------------------------------------------------------------
    // DbBatchOperation guards — via Store()
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Store_EntityWithEmptyETag_ThrowsArgumentException()
    {
        // The guard `string.IsNullOrEmpty(entity.ETag)` inside DbBatchOperation.Add
        // is triggered via the abortAfter path when the loaded state entity has ETag="".
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",  // non-empty → Load doesn't treat as fresh
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        // State entity SequenceId=1 has an empty ETag — guard should fire during Delete.
        var stateRow = new StateEntity
        {
            StateId    = "test-state-id",
            SequenceId = 1,
            ETag       = "",              // <-- empty ETag triggers the guard
            StateData   = Serialize(new SimpleState()),
            TransactionManager = null!,
        };
        SetupFakeRows(fake, opts, keyEntity, new[] { stateRow });
        await sut.Load();

        // abortAfter=0 causes Store to delete stateRow (SequenceId=1 > 0) → empty ETag guard fires.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Store("etag-k", EmptyMeta(), null, null, abortAfter: 0));

        Assert.Contains("ETag can not be null or empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Store_StateIdMismatchInLoadedBatch_ThrowsArgumentException()
    {
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId = "test-state-id", ETag = "etag-k", CommittedSequenceId = 0,
            Metadata = Serialize(EmptyMeta())
        };
        var stateEntity = new StateEntity
        {
            StateId = "different-state-id", SequenceId = 1, ETag = "e1",
            StateData = Serialize(new SimpleState()), TransactionManager = null!
        };
        SetupFakeRows(fake, opts, keyEntity, new[] { stateEntity });
        await sut.Load();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Store("etag-k", EmptyMeta(), null, null, abortAfter: 0));

        Assert.Contains("stateId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // FindState() — indirect via Store()
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Store_PendingStateForMissingSequenceId_IsInsertedAtCorrectSortedPosition()
    {
        // Load with SequenceId=[1,5]; add new SequenceId=3 via statesToPrepare.
        // Verify addStateSql appears (insert, not update), and a second Store confirms
        // FindState works for SequenceId=3 (it now updates rather than inserts).
        var (sut, fake, opts) = StorageTestHarness.Create<SimpleState>();
        var keyEntity = new KeyEntity
        {
            StateId             = "test-state-id",
            ETag                = "etag-k",
            CommittedSequenceId = 0,
            Metadata            = Serialize(EmptyMeta()),
        };
        var stateRows = new[]
        {
            new StateEntity { StateId = "test-state-id", SequenceId = 1, ETag = "e1",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
            new StateEntity { StateId = "test-state-id", SequenceId = 5, ETag = "e5",
                StateData = Serialize(new SimpleState()), TransactionManager = null! },
        };
        SetupFakeRows(fake, opts, keyEntity, stateRows);
        await sut.Load();
        fake.TransactionCallLog.Clear();

        // First Store: insert SequenceId=3 (not present → addStateSql).
        var pending3 = new List<PendingTransactionState<SimpleState>>
        {
            new() { SequenceId = 3, TransactionId = "tx-3",
                    TimeStamp = DateTime.UtcNow, State = new SimpleState { Name = "Mid" } }
        };
        var firstETag = await sut.Store("etag-k", EmptyMeta(), pending3, null, null);

        var addStateSql    = opts.ExecuteSqlDictionary[Constants.AddStateSql];
        var updateStateSql = opts.ExecuteSqlDictionary[Constants.UpdateStateSql];
        var ops1 = fake.TransactionCallLog.SelectMany(c => c).ToList();
        Assert.Contains(ops1, t => t.Item1 == addStateSql);
        fake.TransactionCallLog.Clear();

        // Second Store: update SequenceId=3 (now present → updateStateSql).
        var secondETag = await sut.Store(firstETag, EmptyMeta(), pending3, null, null);
        var ops2 = fake.TransactionCallLog.SelectMany(c => c).ToList();
        var updateState = Assert.Single(ops2, operation => operation.Item1 == updateStateSql);
        using var command = new SqlCommand();
        updateState.Item2(command);
        Assert.Equal(secondETag, command.Parameters[nameof(StateEntity.ETag)].Value);
        Assert.Equal(
            pending3[0].TimeStamp.Ticks,
            Assert.IsType<long>(command.Parameters[nameof(StateEntity.TransactionTimestampTicks)].Value));
        Assert.DoesNotContain(ops2, t => t.Item1 == addStateSql);
        Assert.NotEqual(firstETag, secondETag);
    }

    [Fact]
    public async Task Store_AfterFailedTransaction_RequiresLoad()
    {
        var (sut, fake, _) = StorageTestHarness.Create<SimpleState>();
        await sut.Load();
        fake.TransactionException = new InvalidOperationException("Simulated transaction failure");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Store(null, EmptyMeta(), null, null, null));
        fake.TransactionException = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Store(null, EmptyMeta(), null, null, null));
        Assert.Contains("Load must complete successfully", exception.Message);
    }

    private static T InvokePrivate<T>(object instance, string methodName, IDataRecord record)
        where T : class
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(instance, new object[] { record }));
    }
}
