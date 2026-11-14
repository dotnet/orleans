using System.Net;
using Cassandra;
using Cassandra.DataStax.Graph;
using Cassandra.Metrics;

namespace Tester.Cassandra.Journaling;

internal enum FailureInjectionTiming
{
    BeforeExecution,
    AfterExecution
}

internal sealed class CassandraSessionFailureDecorator : ISession
{
    private readonly ISession _inner;
    private Func<IStatement, bool>? _predicate;
    private Exception? _failure;
    private FailureInjectionTiming _timing;
    private int _remainingMatches;
    private int _armed;
    private int _matchedStatements;
    private int _injectedFailures;

    public CassandraSessionFailureDecorator(ISession inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public int MatchedStatements => Volatile.Read(ref _matchedStatements);
    public int InjectedFailures => Volatile.Read(ref _injectedFailures);

    public void Arm(
        Func<IStatement, bool> predicate,
        FailureInjectionTiming timing,
        string message,
        int occurrence = 1)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(occurrence);
        if (Volatile.Read(ref _armed) != 0)
        {
            throw new InvalidOperationException("A Cassandra session failure is already armed.");
        }

        _predicate = predicate;
        _failure = new TimeoutException(message);
        _timing = timing;
        Volatile.Write(ref _remainingMatches, occurrence);
        Volatile.Write(ref _armed, 1);
    }

    private bool TryInjectFailure(IStatement statement, out Exception? failure, out FailureInjectionTiming timing)
    {
        failure = null;
        timing = default;
        if (Volatile.Read(ref _armed) == 0 || !_predicate!(statement))
        {
            return false;
        }

        Interlocked.Increment(ref _matchedStatements);
        if (Interlocked.Decrement(ref _remainingMatches) != 0 || Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return false;
        }

        Interlocked.Increment(ref _injectedFailures);
        failure = _failure!;
        timing = _timing;
        return true;
    }

    private Task<RowSet> ExecuteAsync(IStatement statement, Func<Task<RowSet>> execute)
    {
        if (!TryInjectFailure(statement, out var failure, out var timing))
        {
            return execute();
        }

        return timing == FailureInjectionTiming.BeforeExecution
            ? Task.FromException<RowSet>(failure!)
            : ExecuteThenFailAsync(execute, failure!);
    }

    private static async Task<RowSet> ExecuteThenFailAsync(Func<Task<RowSet>> execute, Exception failure)
    {
        await execute().ConfigureAwait(false);
        throw failure;
    }

    public int BinaryProtocolVersion => _inner.BinaryProtocolVersion;
    public ICluster Cluster => _inner.Cluster;
    public bool IsDisposed => _inner.IsDisposed;
    public string Keyspace => _inner.Keyspace;
    public UdtMappingDefinitions UserDefinedTypes => _inner.UserDefinedTypes;
    public string SessionName => _inner.SessionName;

    public IAsyncResult BeginExecute(IStatement statement, AsyncCallback callback, object state)
        => _inner.BeginExecute(statement, callback, state);

    public IAsyncResult BeginExecute(string cqlQuery, ConsistencyLevel consistency, AsyncCallback callback, object state)
        => _inner.BeginExecute(cqlQuery, consistency, callback, state);

    public IAsyncResult BeginPrepare(string cqlQuery, AsyncCallback callback, object state)
        => _inner.BeginPrepare(cqlQuery, callback, state);

    public void ChangeKeyspace(string keyspaceName) => _inner.ChangeKeyspace(keyspaceName);

    public void CreateKeyspace(string keyspaceName, Dictionary<string, string> replication, bool durableWrites)
        => _inner.CreateKeyspace(keyspaceName, replication, durableWrites);

    public void CreateKeyspaceIfNotExists(string keyspaceName, Dictionary<string, string> replication, bool durableWrites)
        => _inner.CreateKeyspaceIfNotExists(keyspaceName, replication, durableWrites);

    public void DeleteKeyspace(string keyspaceName) => _inner.DeleteKeyspace(keyspaceName);

    public void DeleteKeyspaceIfExists(string keyspaceName) => _inner.DeleteKeyspaceIfExists(keyspaceName);

    public RowSet EndExecute(IAsyncResult ar) => _inner.EndExecute(ar);

    public PreparedStatement EndPrepare(IAsyncResult ar) => _inner.EndPrepare(ar);

    public RowSet Execute(IStatement statement, string executionProfileName)
        => _inner.Execute(statement, executionProfileName);

    public RowSet Execute(IStatement statement) => _inner.Execute(statement);

    public RowSet Execute(string cqlQuery) => _inner.Execute(cqlQuery);

    public RowSet Execute(string cqlQuery, string executionProfileName)
        => _inner.Execute(cqlQuery, executionProfileName);

    public RowSet Execute(string cqlQuery, ConsistencyLevel consistency)
        => _inner.Execute(cqlQuery, consistency);

    public RowSet Execute(string cqlQuery, int pageSize) => _inner.Execute(cqlQuery, pageSize);

    public Task<RowSet> ExecuteAsync(IStatement statement)
        => ExecuteAsync(statement, () => _inner.ExecuteAsync(statement));

    public Task<RowSet> ExecuteAsync(IStatement statement, string executionProfileName)
        => ExecuteAsync(statement, () => _inner.ExecuteAsync(statement, executionProfileName));

    public PreparedStatement Prepare(string cqlQuery) => _inner.Prepare(cqlQuery);

    public PreparedStatement Prepare(string cqlQuery, IDictionary<string, byte[]> customPayload)
        => _inner.Prepare(cqlQuery, customPayload);

    public PreparedStatement Prepare(string cqlQuery, string keyspace)
        => _inner.Prepare(cqlQuery, keyspace);

    public PreparedStatement Prepare(string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload)
        => _inner.Prepare(cqlQuery, keyspace, customPayload);

    public Task<PreparedStatement> PrepareAsync(string cqlQuery) => _inner.PrepareAsync(cqlQuery);

    public Task<PreparedStatement> PrepareAsync(string cqlQuery, IDictionary<string, byte[]> customPayload)
        => _inner.PrepareAsync(cqlQuery, customPayload);

    public Task<PreparedStatement> PrepareAsync(string cqlQuery, string keyspace)
        => _inner.PrepareAsync(cqlQuery, keyspace);

    public Task<PreparedStatement> PrepareAsync(string cqlQuery, string keyspace, IDictionary<string, byte[]> customPayload)
        => _inner.PrepareAsync(cqlQuery, keyspace, customPayload);

    public IDriverMetrics GetMetrics() => _inner.GetMetrics();

    public GraphResultSet ExecuteGraph(IGraphStatement statement) => _inner.ExecuteGraph(statement);

    public Task<GraphResultSet> ExecuteGraphAsync(IGraphStatement statement)
        => _inner.ExecuteGraphAsync(statement);

    public GraphResultSet ExecuteGraph(IGraphStatement statement, string executionProfileName)
        => _inner.ExecuteGraph(statement, executionProfileName);

    public Task<GraphResultSet> ExecuteGraphAsync(IGraphStatement statement, string executionProfileName)
        => _inner.ExecuteGraphAsync(statement, executionProfileName);

    public Task ShutdownAsync() => _inner.ShutdownAsync();

#pragma warning disable CS0618 // ISession requires forwarding these obsolete members.
    public void WaitForSchemaAgreement(RowSet rs) => _inner.WaitForSchemaAgreement(rs);

    public bool WaitForSchemaAgreement(IPEndPoint forHost) => _inner.WaitForSchemaAgreement(forHost);
#pragma warning restore CS0618

    public void Dispose() => _inner.Dispose();
}
