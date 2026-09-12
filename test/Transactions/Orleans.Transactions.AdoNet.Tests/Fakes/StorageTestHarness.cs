// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Orleans.Hosting;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.TransactionalState;

namespace Orleans.Transactions.AdoNet.Tests.Fakes;

/// <summary>
/// Test helper that wires up a <see cref="TransactionalStateStorage{TState}"/> with a
/// <see cref="FakeRelationalStorage"/> without opening a database connection.
/// </summary>
internal static class StorageTestHarness
{
    /// <summary>
    /// Creates a storage instance + its fake storage back-end.
    /// The options (with pre-populated SQL dictionary) are also returned so tests can
    /// reference SQL strings via <c>options.ExecuteSqlDictionary[Constants.XxxSql]</c>.
    /// </summary>
    public static (TransactionalStateStorage<TState> storage,
                   FakeRelationalStorage fake,
                   TransactionalStateStorageOptions options)
        Create<TState>(string stateId = "test-state-id",
                       string? stateTable = null,
                       string? keyTable = null,
                       string? sqlDot = null)
        where TState : class, new()
    {
        var options = BuildOptions(stateTable, keyTable, sqlDot);
        var fake = new FakeRelationalStorage();
        var logger = NullLogger<TransactionalStateStorage<TState>>.Instance;
        var settings = new JsonSerializerSettings();

        var storage = (TransactionalStateStorage<TState>)RuntimeHelpers.GetUninitializedObject(
            typeof(TransactionalStateStorage<TState>));
        SetField(storage, "stateId", stateId);
        SetField(storage, "options", options);
        SetField(storage, "logger", logger);
        SetField(storage, "jsonSettings", settings);
        SetField<TransactionalStateStorage<TState>, IRelationalStorage>(storage, "storage", fake);

        return (storage, fake, options);
    }

    private static void SetField<TTarget, TValue>(TTarget target, string name, TValue value)
    {
        var field = typeof(TTarget).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found on {typeof(TTarget)}.");
        field.SetValue(target, value);
    }

    /// <summary>
    /// Builds and initialises a <see cref="TransactionalStateStorageOptions"/> instance
    /// with all SQL strings populated.
    /// </summary>
    internal static TransactionalStateStorageOptions BuildOptions(
        string? stateTable = null,
        string? keyTable   = null,
        string? sqlDot     = null)
    {
        var opts = new TransactionalStateStorageOptions();
        if (stateTable is not null) opts.StateEntityTableName = stateTable;
        if (keyTable   is not null) opts.KeyEntityTableName   = keyTable;
        if (sqlDot     is not null) opts.SqlParameterDot      = sqlDot;
        opts.InitExecuteSqlDic();
        return opts;
    }
}
