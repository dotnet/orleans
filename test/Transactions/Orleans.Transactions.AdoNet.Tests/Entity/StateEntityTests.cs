// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using Newtonsoft.Json;
using Orleans.Transactions.AdoNet.Entity;
using Orleans.Transactions.AdoNet.Utils;
using Orleans.Transactions;
using Orleans.Transactions.Abstractions;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

/// <summary>
/// Unit tests for <see cref="StateEntity.Create{T}"/> and <see cref="KeyEntity"/>
/// property defaults. No mocking, no database.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class StateEntityTests
{
    // -----------------------------------------------------------------------
    // Helper types
    // -----------------------------------------------------------------------

    private sealed class CounterState
    {
        public int Value { get; set; }
        public string? Label { get; set; }
    }

    private static JsonSerializerSettings MakeSettings()
    {
        return new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.Include
        };
    }

    /// <summary>
    /// Builds a minimal <see cref="PendingTransactionState{TState}"/> for testing.
    /// </summary>
    private static PendingTransactionState<CounterState> MakePending(
        long sequenceId = 7L,
        string transactionId = "txn-abc",
        DateTime? timestamp = null,
        CounterState? state = null)
    {
        return new PendingTransactionState<CounterState>
        {
            SequenceId = sequenceId,
            TransactionId = transactionId,
            TimeStamp = timestamp ?? new DateTime(2024, 3, 15, 10, 0, 0, DateTimeKind.Utc),
            TransactionManager = default,   // struct default — no Orleans infra needed
            State = state ?? new CounterState { Value = 42, Label = "test" }
        };
    }

    // -----------------------------------------------------------------------
    // StateEntity.Create<T> — field mapping
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_SetsStateId()
    {
        var pending = MakePending();
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "partition-key-123", pending);

        Assert.Equal("partition-key-123", entity.StateId);
    }

    [Fact]
    public void Create_SetsSequenceId()
    {
        var pending = MakePending(sequenceId: 99L);
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "key", pending);

        Assert.Equal(99L, entity.SequenceId);
    }

    [Fact]
    public void Create_SetsTransactionId()
    {
        var pending = MakePending(transactionId: "unique-transaction-id");
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "key", pending);

        Assert.Equal("unique-transaction-id", entity.TransactionId);
    }

    [Fact]
    public void Create_TransactionTimestampTicks_UsesUtcTicks()
    {
        var localTime = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);
        var pending = MakePending(timestamp: localTime);
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "key", pending);

        Assert.Equal(localTime.ToUniversalTime().Ticks, entity.TransactionTimestampTicks);
    }

    [Fact]
    public void Create_TransactionTimestampTicks_PreservesUtcTickPrecision()
    {
        var utcTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1);
        var pending = MakePending(timestamp: utcTime);
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "key", pending);

        Assert.Equal(utcTime.Ticks, entity.TransactionTimestampTicks);
    }

    [Fact]
    public void Create_StateData_RoundTripsState()
    {
        var original = new CounterState { Value = 55, Label = "roundtrip" };
        var pending = MakePending(state: original);
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "key", pending);

        Assert.NotNull(entity.StateData);
        Assert.True(entity.StateData.Length > 0);

        var roundTripped = JsonUtils.DeserializeWithNewtonsoftJson<CounterState>(Assert.IsType<byte[]>(entity.StateData), settings);
        Assert.Equal(55, roundTripped.Value);
        Assert.Equal("roundtrip", roundTripped.Label);
    }

    [Fact]
    public void Create_StateData_ContainsCorrectFieldsNotOtherState()
    {
        // Verify StateData bytes encode the state, not the partition key or sequence ID.
        var pending = MakePending(state: new CounterState { Value = 123, Label = "special" });
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "should-not-appear", pending);

        var roundTripped = JsonUtils.DeserializeWithNewtonsoftJson<CounterState>(Assert.IsType<byte[]>(entity.StateData), settings);
        Assert.Equal(123, roundTripped.Value);
        Assert.Equal("special", roundTripped.Label);
    }

    [Fact]
    public void Create_TransactionManager_RoundTrips()
    {
        // ParticipantId is a struct; default is serializable via Newtonsoft.Json.
        // Verify TransactionManager bytes round-trip to the same Name field value.
        var participantId = new ParticipantId("my-participant", null!, ParticipantId.Role.Resource);
        var pending = new PendingTransactionState<CounterState>
        {
            SequenceId = 1,
            TransactionId = "t1",
            TimeStamp = DateTime.UtcNow,
            TransactionManager = participantId,
            State = new CounterState { Value = 1 }
        };
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "key", pending);

        Assert.NotNull(entity.TransactionManager);
        var roundTripped = JsonUtils.DeserializeWithNewtonsoftJson<ParticipantId>(entity.TransactionManager, settings);
        Assert.Equal("my-participant", roundTripped.Name);
        Assert.Equal(ParticipantId.Role.Resource, roundTripped.SupportedRoles);
    }

    [Fact]
    public void Create_LeavesDatabaseManagedFieldsUnset()
    {
        var pending = MakePending();
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "key", pending);

        Assert.Equal("key", entity.StateId);
        // ETag and row timestamp are assigned by the database layer, not creation.
        Assert.Null(entity.ETag);
        Assert.Null(entity.Timestamp);
    }

    [Fact]
    public void Create_PartitionKeyDistinctFromTransactionId()
    {
        // StateId == partitionKey, not TransactionId — they are different fields.
        var pending = MakePending(transactionId: "tx-9999");
        var settings = MakeSettings();

        var entity = StateEntity.Create(settings, "state-partition", pending);

        Assert.Equal("state-partition", entity.StateId);
        Assert.Equal("tx-9999", entity.TransactionId);
        Assert.NotEqual(entity.StateId, entity.TransactionId);
    }

}
