using System.Collections;
using System.Data;
using Orleans.Persistence.AdoNet.Storage;

namespace UnitTests.StorageTests.Relational.Fakes;

internal sealed class ScriptedRelationalStorage(
    string invariantName = "Scripted.Provider",
    string connectionString = "scripted") :
    IRelationalStorage,
    Orleans.Clustering.AdoNet.Storage.IRelationalStorage,
    Orleans.Reminders.AdoNet.Storage.IRelationalStorage,
    Orleans.Streaming.AdoNet.Storage.IRelationalStorage,
    Orleans.GrainDirectory.AdoNet.Storage.IRelationalStorage
{
    private readonly Queue<ExpectedCall> _expectedCalls = [];
    private readonly List<RecordedStorageCall> _calls = [];

    public string InvariantName { get; } = invariantName;

    public string ConnectionString { get; } = connectionString;

    public IReadOnlyList<RecordedStorageCall> Calls => _calls;

    public ScriptedRelationalStorage ExpectRead(string query, params DataTable[] resultSets)
    {
        _expectedCalls.Enqueue(new(ExpectedCallKind.Read, query, resultSets, 0, null));
        return this;
    }

    public ScriptedRelationalStorage ExpectReadException(string query, Exception exception)
    {
        _expectedCalls.Enqueue(new(ExpectedCallKind.Read, query, [], 0, exception));
        return this;
    }

    public ScriptedRelationalStorage ExpectExecute(string query, int affectedRows)
    {
        _expectedCalls.Enqueue(new(ExpectedCallKind.Execute, query, [], affectedRows, null));
        return this;
    }

    public ScriptedRelationalStorage ExpectExecuteException(string query, Exception exception)
    {
        _expectedCalls.Enqueue(new(ExpectedCallKind.Execute, query, [], 0, exception));
        return this;
    }

    public void VerifyComplete()
    {
        if (_expectedCalls.TryPeek(out var expected))
        {
            throw new InvalidOperationException(
                $"Missing scripted {expected.Kind} call for query '{expected.Query}'.");
        }
    }

    public async Task<IEnumerable<TResult>> ReadAsync<TResult>(
        string query,
        Action<IDbCommand>? parameterProvider,
        Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
        CommandBehavior commandBehavior = CommandBehavior.Default,
        CancellationToken cancellationToken = default)
    {
        var expected = TakeExpected(ExpectedCallKind.Read, query);
        var command = RecordCall(ExpectedCallKind.Read, query, parameterProvider, commandBehavior, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (expected.Exception is not null)
        {
            throw expected.Exception;
        }

        if (expected.ResultSets.Length == 0)
        {
            return [];
        }

        var results = new List<TResult>();
        using var reader = new DataTableReader(expected.ResultSets);
        var resultSet = 0;
        do
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await selector(reader, resultSet, cancellationToken));
            }

            resultSet++;
        }
        while (reader.NextResult());

        _ = command;
        return results;
    }

    public Task<int> ExecuteAsync(
        string query,
        Action<IDbCommand>? parameterProvider,
        CommandBehavior commandBehavior = CommandBehavior.Default,
        CancellationToken cancellationToken = default)
    {
        var expected = TakeExpected(ExpectedCallKind.Execute, query);
        _ = RecordCall(ExpectedCallKind.Execute, query, parameterProvider, commandBehavior, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return expected.Exception is not null
            ? Task.FromException<int>(expected.Exception)
            : Task.FromResult(expected.AffectedRows);
    }

    private ExpectedCall TakeExpected(ExpectedCallKind kind, string query)
    {
        if (!_expectedCalls.TryDequeue(out var expected))
        {
            throw new InvalidOperationException($"Unexpected {kind} call for query '{query}'.");
        }

        if (expected.Kind != kind || !string.Equals(expected.Query, query, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected {expected.Kind} call for query '{expected.Query}', but received {kind} call for query '{query}'.");
        }

        return expected;
    }

    private RecordingDbCommand RecordCall(
        ExpectedCallKind kind,
        string query,
        Action<IDbCommand>? parameterProvider,
        CommandBehavior commandBehavior,
        CancellationToken cancellationToken)
    {
        var command = new RecordingDbCommand { CommandText = query };
        var call = new RecordedStorageCall(kind, query, commandBehavior, cancellationToken, command);
        _calls.Add(call);
        parameterProvider?.Invoke(command);
        return command;
    }

    private sealed record ExpectedCall(
        ExpectedCallKind Kind,
        string Query,
        DataTable[] ResultSets,
        int AffectedRows,
        Exception? Exception);
}

internal enum ExpectedCallKind
{
    Read,
    Execute,
}

internal sealed record RecordedStorageCall(
    ExpectedCallKind Kind,
    string Query,
    CommandBehavior CommandBehavior,
    CancellationToken CancellationToken,
    RecordingDbCommand Command);

internal sealed class RecordingDbCommand : IDbCommand
{
    private readonly RecordingDbParameterCollection _parameters = new();

    [AllowNull]
    public string CommandText { get; set; } = string.Empty;

    public int CommandTimeout { get; set; }

    public CommandType CommandType { get; set; } = CommandType.Text;

    public IDbConnection? Connection { get; set; }

    public IDataParameterCollection Parameters => _parameters;

    public IDbTransaction? Transaction { get; set; }

    public UpdateRowSource UpdatedRowSource { get; set; }

    public void Cancel() => throw new NotSupportedException();

    public IDbDataParameter CreateParameter() => new RecordingDbParameter();

    public void Dispose()
    {
    }

    public int ExecuteNonQuery() => throw new NotSupportedException();

    public IDataReader ExecuteReader() => throw new NotSupportedException();

    public IDataReader ExecuteReader(CommandBehavior behavior) => throw new NotSupportedException();

    public object? ExecuteScalar() => throw new NotSupportedException();

    public void Prepare() => throw new NotSupportedException();
}

internal sealed class RecordingDbParameter : IDbDataParameter
{
    public DbType DbType { get; set; }

    public ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public bool IsNullable => true;

    [AllowNull]
    public string ParameterName { get; set; } = string.Empty;

    [AllowNull]
    public string SourceColumn { get; set; } = string.Empty;

    public DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    public object? Value { get; set; }

    public byte Precision { get; set; }

    public byte Scale { get; set; }

    public int Size { get; set; }
}

internal sealed class RecordingDbParameterCollection : IDataParameterCollection
{
    private readonly List<object?> _items = [];

    [AllowNull]
    public object this[string parameterName]
    {
        get => Find(parameterName)!;
        set
        {
            var index = IndexOf(parameterName);
            if (index < 0)
            {
                Add(value);
            }
            else
            {
                this[index] = value;
            }
        }
    }

    public object? this[int index]
    {
        get => _items[index];
        set
        {
            EnsureParameter(value);
            _items[index] = value;
        }
    }

    public bool IsFixedSize => false;

    public bool IsReadOnly => false;

    public int Count => _items.Count;

    public bool IsSynchronized => false;

    public object SyncRoot => ((ICollection)_items).SyncRoot;

    public int Add(object? value)
    {
        EnsureParameter(value);
        _items.Add(value);
        return _items.Count - 1;
    }

    public void Clear() => _items.Clear();

    public bool Contains(string parameterName) => IndexOf(parameterName) >= 0;

    public bool Contains(object? value) => _items.Contains(value);

    public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public IEnumerator GetEnumerator() => _items.GetEnumerator();

    public int IndexOf(string parameterName) =>
        _items.FindIndex(item =>
            item is IDataParameter parameter
            && string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));

    public int IndexOf(object? value) => _items.IndexOf(value);

    public void Insert(int index, object? value)
    {
        EnsureParameter(value);
        _items.Insert(index, value);
    }

    public void Remove(object? value) => _items.Remove(value);

    public void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
        {
            _items.RemoveAt(index);
        }
    }

    public void RemoveAt(int index) => _items.RemoveAt(index);

    private object? Find(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0 ? _items[index] : null;
    }

    private static void EnsureParameter(object? value)
    {
        if (value is not IDbDataParameter)
        {
            throw new ArgumentException("Only IDbDataParameter instances can be added.", nameof(value));
        }
    }
}
