// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.Transactions.AdoNet.TransactionalState;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.Utils;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

/// <summary>
/// Unit tests for <see cref="TransactionalStateStorageOptions"/> default values and
/// <see cref="ExecuteSqlExtensions.InitExecuteSqlDic"/> SQL-generation logic.
/// All assertions are pure string comparisons — no database, no silo, no network.
/// </summary>
[TestCategory("BVT"), TestCategory("Transactions")]
public sealed class TransactionalStateStorageOptionsTests
{
    // -----------------------------------------------------------------------
    // Default property values
    // -----------------------------------------------------------------------

    [Fact]
    public void Defaults_Invariant()
    {
        var opts = new TransactionalStateStorageOptions();

        Assert.Equal(AdoNetInvariants.InvariantNameSqlServer, opts.Invariant);
        // Also verify the class-level constant matches
        Assert.Equal(TransactionalStateStorageOptions.DEFAULT_ADONET_INVARIANT, opts.Invariant);
    }

    [Fact]
    public void Defaults_StateEntityTableName()
    {
        var opts = new TransactionalStateStorageOptions();

        Assert.Equal("OrleansTransactionStateTable", opts.StateEntityTableName);
    }

    [Fact]
    public void Defaults_KeyEntityTableName()
    {
        var opts = new TransactionalStateStorageOptions();

        Assert.Equal("OrleansTransactionKeyTable", opts.KeyEntityTableName);
    }

    [Fact]
    public void Defaults_SqlParameterDot()
    {
        var opts = new TransactionalStateStorageOptions();

        // SQL Server / MySQL / PostgreSQL use '@'
        Assert.Equal("@", opts.SqlParameterDot);
        Assert.Equal(Constants.SqlParameterDot, opts.SqlParameterDot);
    }

    [Fact]
    public void Defaults_StateIdKeyMaxLength()
    {
        // Note: "Lenth" is an intentional typo in the property name — test must match exactly.
        var opts = new TransactionalStateStorageOptions();

        Assert.Equal(255, opts.StateIdKeyMaxLength);
    }

    [Fact]
    public void Defaults_InitStage()
    {
        var opts = new TransactionalStateStorageOptions();

        Assert.Equal(ServiceLifecycleStage.ApplicationServices, opts.InitStage);
        Assert.Equal(TransactionalStateStorageOptions.DEFAULT_INIT_STAGE, opts.InitStage);
    }

    [Fact]
    public void Defaults_ExecuteSqlDictionary_IsEmptyNotNull()
    {
        var opts = new TransactionalStateStorageOptions();

        Assert.NotNull(opts.ExecuteSqlDictionary);
        Assert.Empty(opts.ExecuteSqlDictionary);
    }

    // -----------------------------------------------------------------------
    // InitExecuteSqlDic() — SQL Server ('@') path: exact SQL string assertions
    // -----------------------------------------------------------------------

    private static TransactionalStateStorageOptions DefaultSqlServerOptions()
    {
        var opts = new TransactionalStateStorageOptions(); // Invariant = SQL Server, dot = "@"
        opts.InitExecuteSqlDic();
        return opts;
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_PopulatesExactlyEightKeys()
    {
        var opts = DefaultSqlServerOptions();

        Assert.Equal(8, opts.ExecuteSqlDictionary.Count);

        // All 8 expected constant keys are present
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.QueryKeySql));
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.AddKeySql));
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.UpdateKeySql));
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.DelKeySql));
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.QueryStateSql));
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.AddStateSql));
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.UpdateStateSql));
        Assert.True(opts.ExecuteSqlDictionary.ContainsKey(Constants.DelStateSql));
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_QueryKeySql()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.QueryKeySql];

        Assert.Equal(
            "SELECT StateId,CommittedSequenceId,Metadata,ETag FROM OrleansTransactionKeyTable WHERE StateId=@StateId;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_AddKeySql()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.AddKeySql];

        Assert.Equal(
            "INSERT INTO OrleansTransactionKeyTable (StateId,CommittedSequenceId,Metadata,Timestamp,ETag) VALUES (@StateId,@CommittedSequenceId,@Metadata,@Timestamp,@ETag);",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_UpdateKeySql()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.UpdateKeySql];

        Assert.Equal(
            "UPDATE OrleansTransactionKeyTable SET CommittedSequenceId=@CommittedSequenceId,Metadata=@Metadata,Timestamp=@Timestamp,ETag=@ETag WHERE StateId=@StateId AND ETag=@PreviousETag;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_DelKeySql()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.DelKeySql];

        Assert.Equal(
            "DELETE FROM OrleansTransactionKeyTable WHERE StateId=@StateId AND ETag=@ETag;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_QueryStateSql_OrdersBySequenceId()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.QueryStateSql];

        Assert.Equal(
            "SELECT StateId,SequenceId,TransactionId,TransactionTimestamp,TransactionManager,StateData,ETag FROM OrleansTransactionStateTable WHERE StateId=@StateId ORDER BY SequenceId ASC;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_AddStateSql()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.AddStateSql];

        Assert.Equal(
            "INSERT INTO OrleansTransactionStateTable (StateId,SequenceId,TransactionId,TransactionTimestamp,TransactionManager,StateData,ETag,Timestamp) VALUES (@StateId,@SequenceId,@TransactionId,@TransactionTimestamp,@TransactionManager,@StateData,@ETag,@Timestamp);",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_UpdateStateSql()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.UpdateStateSql];

        Assert.Equal(
            "UPDATE OrleansTransactionStateTable SET TransactionId=@TransactionId,TransactionTimestamp=@TransactionTimestamp,TransactionManager=@TransactionManager,StateData=@StateData,Timestamp=@Timestamp,ETag=@ETag WHERE StateId=@StateId AND SequenceId=@SequenceId;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_SqlServer_DelStateSql()
    {
        var opts = DefaultSqlServerOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.DelStateSql];

        Assert.Equal(
            "DELETE FROM OrleansTransactionStateTable WHERE StateId=@StateId AND SequenceId=@SequenceId AND ETag=@ETag;",
            sql);
    }

    // -----------------------------------------------------------------------
    // InitExecuteSqlDic() — Oracle (':') path
    // -----------------------------------------------------------------------

    private static TransactionalStateStorageOptions OracleOptions()
    {
        var opts = new TransactionalStateStorageOptions
        {
            SqlParameterDot = Constants.OracleParameterDot   // ":"
        };
        opts.InitExecuteSqlDic();
        return opts;
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_AllSqlsUseColonNotAt()
    {
        var opts = OracleOptions();

        foreach (var kvp in opts.ExecuteSqlDictionary)
        {
            Assert.False(kvp.Value.Contains("@"),
                $"Key '{kvp.Key}' contains '@' but Oracle should use ':'. SQL: {kvp.Value}");
            Assert.Contains(":", kvp.Value);
        }
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_QueryKeySql()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.QueryKeySql];

        Assert.Equal(
            "SELECT StateId,CommittedSequenceId,Metadata,ETag FROM OrleansTransactionKeyTable WHERE StateId=:StateId;",
            sql);
        Assert.DoesNotContain("@", sql);
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_AddKeySql()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.AddKeySql];

        Assert.Equal(
            "INSERT INTO OrleansTransactionKeyTable (StateId,CommittedSequenceId,Metadata,Timestamp,ETag) VALUES (:StateId,:CommittedSequenceId,:Metadata,:Timestamp,:ETag);",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_UpdateKeySql()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.UpdateKeySql];

        Assert.Equal(
            "UPDATE OrleansTransactionKeyTable SET CommittedSequenceId=:CommittedSequenceId,Metadata=:Metadata,Timestamp=:Timestamp,ETag=:ETag WHERE StateId=:StateId AND ETag=:PreviousETag;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_DelKeySql()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.DelKeySql];

        Assert.Equal(
            "DELETE FROM OrleansTransactionKeyTable WHERE StateId=:StateId AND ETag=:ETag;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_AddStateSql()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.AddStateSql];

        Assert.Equal(
            "INSERT INTO OrleansTransactionStateTable (StateId,SequenceId,TransactionId,TransactionTimestamp,TransactionManager,StateData,ETag,Timestamp) VALUES (:StateId,:SequenceId,:TransactionId,:TransactionTimestamp,:TransactionManager,:StateData,:ETag,:Timestamp);",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_UpdateStateSql()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.UpdateStateSql];

        Assert.Equal(
            "UPDATE OrleansTransactionStateTable SET TransactionId=:TransactionId,TransactionTimestamp=:TransactionTimestamp,TransactionManager=:TransactionManager,StateData=:StateData,Timestamp=:Timestamp,ETag=:ETag WHERE StateId=:StateId AND SequenceId=:SequenceId;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_DelStateSql()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.DelStateSql];

        Assert.Equal(
            "DELETE FROM OrleansTransactionStateTable WHERE StateId=:StateId AND SequenceId=:SequenceId AND ETag=:ETag;",
            sql);
    }

    [Fact]
    public void InitExecuteSqlDic_Oracle_QueryStateSql_OrdersBySequenceId()
    {
        var opts = OracleOptions();
        var sql = opts.ExecuteSqlDictionary[Constants.QueryStateSql];

        Assert.Equal(
            "SELECT StateId,SequenceId,TransactionId,TransactionTimestamp,TransactionManager,StateData,ETag FROM OrleansTransactionStateTable WHERE StateId=:StateId ORDER BY SequenceId ASC;",
            sql);
        Assert.DoesNotContain("@", sql);
    }

    // -----------------------------------------------------------------------
    // Custom table names
    // -----------------------------------------------------------------------

    [Fact]
    public void InitExecuteSqlDic_CustomStateTable_UsedInAllStateSqls()
    {
        var opts = new TransactionalStateStorageOptions
        {
            StateEntityTableName = "MyCustomStateTable"
        };
        opts.InitExecuteSqlDic();

        // All three state-table SQL entries should reference the custom table name
        Assert.Contains("MyCustomStateTable", opts.ExecuteSqlDictionary[Constants.QueryStateSql]);
        Assert.Contains("MyCustomStateTable", opts.ExecuteSqlDictionary[Constants.AddStateSql]);
        Assert.Contains("MyCustomStateTable", opts.ExecuteSqlDictionary[Constants.UpdateStateSql]);
        Assert.Contains("MyCustomStateTable", opts.ExecuteSqlDictionary[Constants.DelStateSql]);

        // Default state table must NOT appear
        Assert.DoesNotContain("OrleansTransactionStateTable", opts.ExecuteSqlDictionary[Constants.QueryStateSql]);
    }

    [Fact]
    public void InitExecuteSqlDic_CustomKeyTable_UsedInAllKeySqls()
    {
        var opts = new TransactionalStateStorageOptions
        {
            KeyEntityTableName = "MyCustomKeyTable"
        };
        opts.InitExecuteSqlDic();

        Assert.Contains("MyCustomKeyTable", opts.ExecuteSqlDictionary[Constants.QueryKeySql]);
        Assert.Contains("MyCustomKeyTable", opts.ExecuteSqlDictionary[Constants.AddKeySql]);
        Assert.Contains("MyCustomKeyTable", opts.ExecuteSqlDictionary[Constants.UpdateKeySql]);
        Assert.Contains("MyCustomKeyTable", opts.ExecuteSqlDictionary[Constants.DelKeySql]);

        // Default key table must NOT appear
        Assert.DoesNotContain("OrleansTransactionKeyTable", opts.ExecuteSqlDictionary[Constants.QueryKeySql]);
    }

    [Fact]
    public void InitExecuteSqlDic_CustomTables_DoNotCrossContaminate()
    {
        // Custom state table should not appear in key SQL entries and vice versa.
        var opts = new TransactionalStateStorageOptions
        {
            StateEntityTableName = "StateT",
            KeyEntityTableName = "KeyT"
        };
        opts.InitExecuteSqlDic();

        // Key sqls reference KeyT, not StateT
        Assert.DoesNotContain("StateT", opts.ExecuteSqlDictionary[Constants.QueryKeySql]);
        Assert.DoesNotContain("StateT", opts.ExecuteSqlDictionary[Constants.AddKeySql]);

        // State sqls reference StateT, not KeyT
        Assert.DoesNotContain("KeyT", opts.ExecuteSqlDictionary[Constants.QueryStateSql]);
        Assert.DoesNotContain("KeyT", opts.ExecuteSqlDictionary[Constants.AddStateSql]);
    }

    // -----------------------------------------------------------------------
    // Idempotence / double-init guard
    // -----------------------------------------------------------------------

    [Fact]
    public void InitExecuteSqlDic_CalledTwice_ThrowsArgumentException()
    {
        // Dictionary.Add throws ArgumentException on duplicate key.
        // Calling InitExecuteSqlDic twice on the same instance must throw.
        var opts = new TransactionalStateStorageOptions();
        opts.InitExecuteSqlDic();

        Assert.Throws<ArgumentException>(() => opts.InitExecuteSqlDic());
    }

}
