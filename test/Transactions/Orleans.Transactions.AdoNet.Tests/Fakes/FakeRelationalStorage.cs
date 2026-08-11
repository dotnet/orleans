// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Data;
using System.Data.Common;
using Orleans.Transactions.AdoNet.Storage;

namespace Orleans.Transactions.AdoNet.Tests.Fakes;

/// <summary>
/// Hand-rolled stub for <see cref="IRelationalStorage"/>.
/// Controls <see cref="ReadAsync{TResult}"/> responses by SQL string and captures
/// <see cref="ExecuteTransactionAsync"/> call arguments for assertion.
/// </summary>
internal sealed class FakeRelationalStorage : IRelationalStorage
{
    // Set before a test to provide ReadAsync<T> rows.
    // Key: the exact SQL string passed; Value: the rows to return (must be cast-compatible with TResult).
    public Func<string, IEnumerable<object>>? ReadResponseFactory { get; set; }

    // All ExecuteTransactionAsync argument lists, in call order (outer = calls, inner = operations per call).
    public List<List<Tuple<string, Action<DbCommand>>>> TransactionCallLog { get; } = new();
    public Exception? TransactionException { get; set; }

    public Task<IEnumerable<TResult>> ReadAsync<TResult>(
        string query,
        Action<IDbCommand>? parameterProvider,
        Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
        CommandBehavior commandBehavior = CommandBehavior.Default,
        CancellationToken cancellationToken = default)
    {
        if (ReadResponseFactory is null)
        {
            return Task.FromResult(Enumerable.Empty<TResult>());
        }

        var raw = ReadResponseFactory(query).Cast<TResult>();
        return Task.FromResult(raw);
    }

    public Task<(IReadOnlyList<TFirst> First, IReadOnlyList<TSecond> Second)> ReadTransactionAsync<TFirst, TSecond>(
        string firstQuery,
        Action<IDbCommand>? firstParameterProvider,
        Func<IDataRecord, TFirst> firstSelector,
        string secondQuery,
        Action<IDbCommand>? secondParameterProvider,
        Func<IDataRecord, TSecond> secondSelector,
        CancellationToken cancellationToken = default)
    {
        var first = ReadResponseFactory?.Invoke(firstQuery).Cast<TFirst>().ToList() ?? [];
        var second = ReadResponseFactory?.Invoke(secondQuery).Cast<TSecond>().ToList() ?? [];
        return Task.FromResult<(IReadOnlyList<TFirst>, IReadOnlyList<TSecond>)>((first, second));
    }

    public Task<int> ExecuteAsync(
        string query,
        Action<IDbCommand>? parameterProvider,
        CommandBehavior commandBehavior = CommandBehavior.Default,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<int> ExecuteTransactionAsync(
        List<Tuple<string, Action<DbCommand>>> operations,
        CancellationToken cancellationToken = default)
    {
        TransactionCallLog.Add(new List<Tuple<string, Action<DbCommand>>>(operations));
        if (TransactionException is { } exception)
        {
            return Task.FromException<int>(exception);
        }

        return Task.FromResult(0);
    }

    public string InvariantName => "Fake";
    public string ConnectionString => string.Empty;
}
