// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Orleans.Hosting;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.TransactionalState;
using Orleans.Transactions.AdoNet.Utils;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

[TestSuite("Functional")]
[TestProvider("SqlServer")]
[TestArea("Transactions")]
public sealed class SqlServerTransactionalStateStorageTests
{
    [Fact]
    public async Task Load_RacingWithStore_ReturnsAConsistentSnapshot()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var stateId = Guid.NewGuid().ToString("N");
        var (seed, _) = database.CreateStorage(stateId);
        var initial = await seed.Load();
        var initialETag = await seed.Store(initial.ETag, new(), null, null, null);

        var (writer, _) = database.CreateStorage(stateId);
        var writerSnapshot = await writer.Load();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await start.Task;
                var (reader, _) = database.CreateStorage(stateId);
                return await reader.Load();
            })
            .ToArray();

        async Task<string> StoreAfterStart()
        {
            await start.Task;
            return await writer.Store(
                writerSnapshot.ETag,
                new(),
                [CreatePendingState(1)],
                commitUpTo: 1,
                abortAfter: null);
        }

        var storeTask = StoreAfterStart();
        start.SetResult(true);
        await Task.WhenAll(readers.Select(task => (Task)task).Append(storeTask));

        var newETag = await storeTask;
        foreach (var snapshot in await Task.WhenAll(readers))
        {
            if (snapshot.ETag == initialETag)
            {
                Assert.Equal(0, snapshot.CommittedSequenceId);
                Assert.Equal(0, snapshot.CommittedState.Value);
                Assert.Empty(snapshot.PendingStates);
            }
            else
            {
                Assert.Equal(newETag, snapshot.ETag);
                Assert.Equal(1, snapshot.CommittedSequenceId);
                Assert.Equal(1, snapshot.CommittedState.Value);
                Assert.Empty(snapshot.PendingStates);
            }
        }
    }

    [Fact]
    public async Task ConcurrentWriters_RejectTheStaleETag()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var stateId = Guid.NewGuid().ToString("N");
        var (seed, _) = database.CreateStorage(stateId);
        var initial = await seed.Load();
        var initialETag = await seed.Store(initial.ETag, new(), null, null, null);
        var pendingETag = await seed.Store(initialETag, new(), [CreatePendingState(1)], null, null);

        var (firstWriter, _) = database.CreateStorage(stateId);
        var (secondWriter, _) = database.CreateStorage(stateId);
        var firstSnapshot = await firstWriter.Load();
        var secondSnapshot = await secondWriter.Load();
        Assert.Equal(1, Assert.Single(firstSnapshot.PendingStates).State.Value);

        var winningETag = await firstWriter.Store(
            firstSnapshot.ETag,
            new(),
            [CreatePendingState(2)],
            commitUpTo: 1,
            abortAfter: null);
        var conflict = await Assert.ThrowsAsync<InconsistentStateException>(
            () => secondWriter.Store(secondSnapshot.ETag, new(), null, null, null));

        Assert.Equal(pendingETag, conflict.CurrentEtag);
        var (reader, _) = database.CreateStorage(stateId);
        var persisted = await reader.Load();
        Assert.Equal(winningETag, persisted.ETag);
        Assert.Equal(1, persisted.CommittedSequenceId);
        Assert.Equal(2, persisted.CommittedState.Value);
    }

    [Fact]
    public async Task Store_FailureAfterKeyUpdate_RollsBackTheTransaction()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var stateId = Guid.NewGuid().ToString("N");
        var (storage, options) = database.CreateStorage(stateId);
        var initial = await storage.Load();
        options.ExecuteSqlDictionary[Constants.AddStateSql] =
            "INSERT INTO OrleansTransactionsMissingStateTable (StateId) VALUES (@StateId)";

        var exception = await Assert.ThrowsAsync<SqlException>(
            () => storage.Store(
                initial.ETag,
                new(),
                [CreatePendingState(1)],
                commitUpTo: 1,
                abortAfter: null));

        Assert.Equal(208, exception.Number);
        var (reader, _) = database.CreateStorage(stateId);
        var afterFailure = await reader.Load();
        Assert.Null(afterFailure.ETag);
        Assert.Equal(0, afterFailure.CommittedSequenceId);
        Assert.Equal(0, afterFailure.CommittedState.Value);
    }

    private sealed class SimpleState
    {
        public int Value { get; set; }
    }

    private static PendingTransactionState<SimpleState> CreatePendingState(int value) =>
        new()
        {
            SequenceId = 1,
            TransactionId = Guid.NewGuid().ToString("N"),
            TimeStamp = DateTime.UtcNow,
            TransactionManager = default,
            State = new SimpleState { Value = value }
        };

    private sealed class SqlServerDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _stateTable;
        private readonly string _keyTable;

        private SqlServerDatabase(string connectionString, string stateTable, string keyTable)
        {
            _connectionString = connectionString;
            _stateTable = stateTable;
            _keyTable = keyTable;
        }

        public static async Task<SqlServerDatabase> CreateAsync()
        {
            var connectionString = TestDefaultConfiguration.MsSqlConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw Xunit.Sdk.SkipException.ForSkip("SQL Server connection string is not specified.");
            }

            var suffix = Guid.NewGuid().ToString("N");
            var database = new SqlServerDatabase(
                connectionString,
                $"OrleansTransactionStateTable_{suffix}",
                $"OrleansTransactionKeyTable_{suffix}");
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "SQLServer-Transactions.sql");
            var script = File.ReadAllText(scriptPath)
                .Replace("OrleansTransactionKeyTable", database._keyTable, StringComparison.Ordinal)
                .Replace("OrleansTransactionStateTable", database._stateTable, StringComparison.Ordinal)
                .Replace("IX_State_Seq", $"IX_State_Seq_{suffix}", StringComparison.Ordinal);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            return database;
        }

        public (TransactionalStateStorage<SimpleState> Storage, TransactionalStateStorageOptions Options)
            CreateStorage(string stateId)
        {
            var options = new TransactionalStateStorageOptions
            {
                ConnectionString = _connectionString,
                Invariant = AdoNetInvariants.InvariantNameSqlServer,
                StateEntityTableName = _stateTable,
                KeyEntityTableName = _keyTable
            };
            var storage = new TransactionalStateStorage<SimpleState>(
                stateId,
                new JsonSerializerSettings(),
                options,
                NullLogger<TransactionalStateStorage<SimpleState>>.Instance);
            return (storage, options);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE [{_stateTable}]; DROP TABLE [{_keyTable}];";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}
