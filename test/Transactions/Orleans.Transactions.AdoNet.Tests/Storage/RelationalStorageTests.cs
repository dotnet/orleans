// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Storage;
using Orleans.Transactions.AdoNet.Storage;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class RelationalStorageTests
{
    [Fact]
    public async Task ExecuteTransactionAsync_NullOperations_ThrowsBeforeOpeningConnection()
    {
        var storage = (RelationalStorage)RuntimeHelpers.GetUninitializedObject(typeof(RelationalStorage));

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.ExecuteTransactionAsync(
                null!,
                currentETag: null,
                TestContext.Current.CancellationToken));

        Assert.Equal("multipleQuery", exception.ParamName);
    }

    [Fact]
    public void StaleETagMiss_IsReportedAsStorageConflict()
    {
        var exception = Assert.Throws<InconsistentStateException>(
            () => RelationalStorage.ValidateAffectedRows(
                operationIndex: 0,
                affectedRows: 0,
                currentETag: "expected-etag"));

        Assert.Equal("Unknown", exception.StoredEtag);
        Assert.Equal("expected-etag", exception.CurrentEtag);
        Assert.Null(exception.InnerException);
        Assert.Contains("operation 0", exception.Message);
    }

    [Fact]
    public async Task BeginTransactionAsync_UsesAsyncProvider()
    {
        var connection = new TrackingDbConnection();
        var transaction = await RelationalStorage.BeginTransactionAsync(
            connection,
            IsolationLevel.Serializable,
            isSynchronousAdoNetImplementation: false,
            TestContext.Current.CancellationToken);

        Assert.True(connection.AsyncBeginTransactionCalled);
        Assert.Equal(0, connection.SyncBeginTransactionThreadId);
        Assert.Same(connection.CreatedTransaction, transaction);
    }

    [Fact]
    public async Task BeginTransactionAsync_OffloadsSynchronousProvider()
    {
        var connection = new TrackingDbConnection();
        var cancellationToken = TestContext.Current.CancellationToken;
        var callerThreadId = 0;
        var transactionTask = Task.Factory.StartNew(
            () =>
            {
                callerThreadId = Environment.CurrentManagedThreadId;
                return RelationalStorage.BeginTransactionAsync(
                    connection,
                    IsolationLevel.ReadCommitted,
                    isSynchronousAdoNetImplementation: true,
                    cancellationToken);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var transaction = await transactionTask;

        Assert.False(connection.AsyncBeginTransactionCalled);
        Assert.NotEqual(callerThreadId, connection.SyncBeginTransactionThreadId);
        Assert.Same(connection.CreatedTransaction, transaction);
    }

    [Theory]
    [InlineData(AdoNetInvariants.InvariantNameSqlServer, 2601)]
    [InlineData(AdoNetInvariants.InvariantNameSqlServer, 2627)]
    [InlineData(AdoNetInvariants.InvariantNameMySql, 1062)]
    [InlineData(AdoNetInvariants.InvariantNameMySqlConnector, 1062)]
    [InlineData(AdoNetInvariants.InvariantNameOracleDatabase, 1)]
    public void InitialInsertUniqueViolation_IsRecognizedAsStorageConflict(
        string invariantName,
        int providerErrorNumber)
    {
        var providerException = new NumberedDbException(providerErrorNumber);

        Assert.True(RelationalStorage.IsUniqueConstraintViolation(invariantName, providerException));

        var exception = RelationalStorage.CreateTransactionConflict(
            "Initial insert conflicted.",
            currentETag: null,
            providerException);
        Assert.Equal("Unknown", exception.StoredEtag);
        Assert.Equal("null", exception.CurrentEtag);
        Assert.Same(providerException, exception.InnerException);
    }

    [Fact]
    public void PostgreSqlInitialInsertUniqueViolation_IsRecognizedAsStorageConflict()
    {
        var providerException = new SqlStateDbException("23505");

        Assert.True(RelationalStorage.IsUniqueConstraintViolation(
            AdoNetInvariants.InvariantNamePostgreSql,
            providerException));

        var exception = RelationalStorage.CreateTransactionConflict(
            "Initial insert conflicted.",
            currentETag: null,
            providerException);
        Assert.Same(providerException, exception.InnerException);
    }

    [Fact]
    public void UnrelatedDatabaseFailure_IsNotRecognizedAsStorageConflict()
    {
        var providerException = new NumberedDbException(1205);

        Assert.False(RelationalStorage.IsUniqueConstraintViolation(
            AdoNetInvariants.InvariantNameSqlServer,
            providerException));
    }

    [Fact]
    public async Task RollbackFailure_PreservesOriginalDatabaseFailure()
    {
        var originalException = new NumberedDbException(1205);
        var rollbackException = new InvalidOperationException("Transaction was already rolled back.");
        var transaction = new FailingRollbackTransaction(rollbackException);

        await RelationalStorage.RollbackPreservingOriginalExceptionAsync(
            transaction,
            originalException,
            TestContext.Current.CancellationToken);

        Assert.Same(
            rollbackException,
            originalException.Data[RelationalStorage.RollbackExceptionDataKey]);
    }

    private sealed class NumberedDbException(int number) : DbException("Provider failure")
    {
        public int Number { get; } = number;
    }

    private sealed class SqlStateDbException(string sqlState) : DbException("Provider failure")
    {
        public override string? SqlState { get; } = sqlState;
    }

    private sealed class TrackingDbConnection : DbConnection
    {
        public int SyncBeginTransactionThreadId { get; private set; }
        public bool AsyncBeginTransactionCalled { get; private set; }
        public DbTransaction? CreatedTransaction { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            SyncBeginTransactionThreadId = Environment.CurrentManagedThreadId;
            var transaction = new TrackingDbTransaction(this, isolationLevel);
            CreatedTransaction = transaction;
            return transaction;
        }

        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
            IsolationLevel isolationLevel,
            CancellationToken cancellationToken)
        {
            AsyncBeginTransactionCalled = true;
            var transaction = new TrackingDbTransaction(this, isolationLevel);
            CreatedTransaction = transaction;
            return ValueTask.FromResult<DbTransaction>(transaction);
        }

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class TrackingDbTransaction(DbConnection connection, IsolationLevel isolationLevel) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => isolationLevel;

        protected override DbConnection DbConnection => connection;

        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();
    }

    private sealed class FailingRollbackTransaction(Exception exception) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        public override void Commit() => throw new NotSupportedException();

        public override void Rollback() => throw exception;

        public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(exception);
    }
}
