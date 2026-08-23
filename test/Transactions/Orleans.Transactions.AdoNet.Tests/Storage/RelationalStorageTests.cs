// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
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
            () => storage.ExecuteTransactionAsync(null!, currentETag: null));

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
            CancellationToken.None);

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
