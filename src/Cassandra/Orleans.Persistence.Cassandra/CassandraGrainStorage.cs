using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Cassandra;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;

namespace Orleans.Persistence.Cassandra;

internal sealed class CassandraGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private readonly string _name;
    private readonly CassandraGrainStorageOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly IActivatorProvider _activatorProvider;
    private readonly IGrainStorageSerializer _serializer;
    private readonly ILogger<CassandraGrainStorage> _logger;
    private readonly IServiceProvider _services;
    private readonly Func<IStatement, Task<CassandraResult>>? _testExecutor;
    private readonly Action? _closePathEntered;
    private readonly object _operationLock = new();
    private ISession? _session;
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private bool _closing;
    private bool _ownsSession;
    private Task? _initializationCleanup;
    private Task? _closeCompletion;
    private PreparedStatement? _read;
    private PreparedStatement? _insert;
    private PreparedStatement? _update;
    private PreparedStatement? _clearWithEtag;
    private PreparedStatement? _deleteWithoutEtag;
    private PreparedStatement? _delete;

    public CassandraGrainStorage(
        string name,
        CassandraGrainStorageOptions options,
        IOptions<ClusterOptions> clusterOptions,
        IActivatorProvider activatorProvider,
        IGrainStorageSerializer serializer,
        ILogger<CassandraGrainStorage> logger,
        IServiceProvider services,
        Func<IStatement, Task<CassandraResult>>? testExecutor = null,
        Action? closePathEntered = null)
    {
        _name = name;
        _options = options;
        _clusterOptions = clusterOptions.Value;
        _activatorProvider = activatorProvider;
        _serializer = options.GrainStorageSerializer ?? serializer;
        _logger = logger;
        _services = services;
        _testExecutor = testExecutor;
        _closePathEntered = closePathEntered;
    }

    internal readonly record struct CassandraResult(
        bool Applied,
        bool? RecordExists,
        long? Version,
        byte[]? State);

    private string TableName => _options.TableName ?? "grain_state";
    private string QuotedTableName => CassandraIdentifier.Quote(TableName);
    private ISession Session => _session ?? throw new InvalidOperationException("Cassandra grain storage is not initialized.");

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(OptionFormattingUtilities.Name<CassandraGrainStorage>(_name), _options.InitStage, InitAsync, CloseAsync);

    private async Task InitAsync(CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        EnterOperation();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var creation = TrackInitializationTask((_options.CreateSessionAsync ?? CreateSessionFromConnectionString())(_services));
            ISession session;
            try
            {
                session = await WaitAsync(creation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (_options.OwnsSession)
                {
                    var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_operationLock)
                    {
                        _initializationCleanup = cleanup.Task;
                    }

                    _ = creation.ContinueWith(
                        static (task, state) =>
                        {
                            try
                            {
                                if (task.Status == TaskStatus.RanToCompletion)
                                {
                                    task.Result.Cluster.Dispose();
                                }
                                else
                                {
                                    _ = task.Exception;
                                }
                            }
                            finally
                            {
                                ((TaskCompletionSource)state!).TrySetResult();
                            }
                        },
                        cleanup,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                throw;
            }
            if (session is null) throw new InvalidOperationException("The Cassandra session provider returned null.");
            var owned = _options.OwnsSession;
            var ownedResource = owned ? new OwnedResource(session.Cluster) : null;
            Action<Task<CassandraResult>>? onExecuteCancellation = ownedResource is null ? null : ownedResource.Request;
            Action<Task<PreparedStatement>>? onPrepareCancellation = ownedResource is null ? null : ownedResource.Request;
            lock (_operationLock)
            {
                _initializationCleanup = ownedResource?.Completion;
            }
            try
            {
                if (_options.CreateTableIfNotExists)
                {
                    await SessionExecuteAsync(session, new SimpleStatement($"""
                        CREATE TABLE IF NOT EXISTS {QuotedTableName}
                        (
                            service_id text,
                            grain_id text,
                            state_name text,
                            grain_type text,
                            version bigint,
                            record_exists boolean,
                            state blob,
                            updated_at timestamp,
                            PRIMARY KEY ((service_id, grain_id), state_name)
                        )
                        """).SetConsistencyLevel(_options.ConsistencyLevel), cancellationToken, trackOperation: true, onCancellation: onExecuteCancellation).ConfigureAwait(false);
                }

                _read = await PrepareAsync(session, $"SELECT version, record_exists, state FROM {QuotedTableName} WHERE service_id = ? AND grain_id = ? AND state_name = ?", cancellationToken, onPrepareCancellation).ConfigureAwait(false);
                _insert = await PrepareAsync(session, $"INSERT INTO {QuotedTableName} (service_id, grain_id, state_name, grain_type, version, record_exists, state, updated_at) VALUES (?, ?, ?, ?, ?, true, ?, ?) IF NOT EXISTS", cancellationToken, onPrepareCancellation, true).ConfigureAwait(false);
                _update = await PrepareAsync(session, $"UPDATE {QuotedTableName} SET grain_type = ?, version = ?, record_exists = true, state = ?, updated_at = ? WHERE service_id = ? AND grain_id = ? AND state_name = ? IF version = ?", cancellationToken, onPrepareCancellation, true).ConfigureAwait(false);
                _clearWithEtag = await PrepareAsync(session, $"UPDATE {QuotedTableName} SET record_exists = false, state = null, version = ?, updated_at = ? WHERE service_id = ? AND grain_id = ? AND state_name = ? IF version = ?", cancellationToken, onPrepareCancellation).ConfigureAwait(false);
                _deleteWithoutEtag = await PrepareAsync(session, $"DELETE FROM {QuotedTableName} WHERE service_id = ? AND grain_id = ? AND state_name = ? IF record_exists = false", cancellationToken, onPrepareCancellation).ConfigureAwait(false);
                _delete = await PrepareAsync(session, $"DELETE FROM {QuotedTableName} WHERE service_id = ? AND grain_id = ? AND state_name = ? IF version = ?", cancellationToken, onPrepareCancellation, true).ConfigureAwait(false);

                bool publishSession;
                lock (_operationLock)
                {
                    publishSession = !_closing;
                    if (publishSession)
                    {
                        _session = owned ? session : new NonDisposingSession(session);
                        _ownsSession = owned;
                        _initializationCleanup = null;
                    }
                }

                if (!publishSession) ownedResource?.Request();
            }
            catch
            {
                ownedResource?.Request();
                throw;
            }

            _logger.LogDebug("Initialized Cassandra grain storage {ProviderName} for service {ServiceId} in {ElapsedMilliseconds}ms.", _name, _clusterOptions.ServiceId, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to initialize Cassandra grain storage {ProviderName}.", _name);
            throw;
        }
        finally
        {
            ExitOperation();
        }
    }

    private Func<IServiceProvider, Task<ISession>> CreateSessionFromConnectionString() =>
        _ => throw new InvalidOperationException("No Cassandra session configuration was provided.");

    private async Task<PreparedStatement> PrepareAsync(
        ISession session,
        string cql,
        CancellationToken cancellationToken,
        Action<Task<PreparedStatement>>? onCancellation = null,
        bool lwt = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = TrackInitializationTask(session.PrepareAsync(cql));
        return await WaitAsync(operation, cancellationToken, onCancellation).ConfigureAwait(false);
    }

    private async Task<CassandraResult> SessionExecuteAsync(
        ISession session,
        IStatement statement,
        CancellationToken cancellationToken,
        bool trackOperation = true,
        Action<Task<CassandraResult>>? onCancellation = null)
    {
        if (trackOperation) EnterOperation();

        Task<CassandraResult>? operation = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation = _testExecutor is null
                ? ExecuteAndConvertAsync(session, statement)
                : _testExecutor(statement);
            if (trackOperation)
            {
                _ = operation.ContinueWith(
                    static (task, state) =>
                    {
                        _ = task.Exception;
                        ((CassandraGrainStorage)state!).ExitOperation();
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return await WaitAsync(operation, cancellationToken, onCancellation).ConfigureAwait(false);
        }
        catch
        {
            if (trackOperation && operation is null)
            {
                ExitOperation();
            }

            throw;
        }
    }

    private void EnterOperation()
    {
        lock (_operationLock)
        {
            if (_closing) throw new InvalidOperationException("Cassandra grain storage is shutting down.");
            IncrementOperationCount();
        }
    }

    private void IncrementOperationCount()
    {
        if (_activeOperations++ == 0)
        {
            _operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ExitOperation()
    {
        lock (_operationLock)
        {
            if (--_activeOperations == 0)
            {
                _operationsDrained?.TrySetResult();
            }
        }
    }

    private Task BeginClose()
    {
        Task result;
        TaskCompletionSource? completionSource = null;
        Task? operationsDrained = null;
        lock (_operationLock)
        {
            _closing = true;
            if (_closeCompletion is null)
            {
                operationsDrained = _activeOperations == 0
                    ? Task.CompletedTask
                    : _operationsDrained!.Task;
                completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _closeCompletion = completionSource.Task;
            }

            result = _closeCompletion;
        }

        _closePathEntered?.Invoke();
        if (completionSource is not null)
        {
            _ = CompleteCloseAsync(operationsDrained!, completionSource);
        }

        return result;
    }

    private async Task CompleteCloseAsync(Task operationsDrained, TaskCompletionSource completionSource)
    {
        try
        {
            await operationsDrained.ConfigureAwait(false);
            Task? initializationCleanup;
            lock (_operationLock)
            {
                initializationCleanup = _initializationCleanup;
            }

            if (initializationCleanup is not null)
            {
                await initializationCleanup.ConfigureAwait(false);
            }

            var (session, ownsSession) = TakeSessionForDisposal();
            if (session is not null && ownsSession)
            {
                try
                {
                    await session.Cluster.ShutdownAsync().ConfigureAwait(false);
                }
                finally
                {
                    session.Cluster.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            completionSource.TrySetException(exception);
            return;
        }

        completionSource.TrySetResult();
    }

    private Task<T> TrackInitializationTask<T>(Task<T> operation)
    {
        lock (_operationLock)
        {
            IncrementOperationCount();
        }
        _ = operation.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((CassandraGrainStorage)state!).ExitOperation();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    private static async Task<CassandraResult> ExecuteAndConvertAsync(ISession session, IStatement statement)
    {
        var rows = await session.ExecuteAsync(statement).ConfigureAwait(false);
        var row = rows.FirstOrDefault();
        if (row is null) return default;

        bool applied = false;
        bool? recordExists = null;
        long? version = null;
        byte[]? state = null;
        foreach (var column in rows.Columns)
        {
            switch (column.Name)
            {
                case "[applied]":
                    applied = row.GetValue<bool>(column.Name);
                    break;
                case "record_exists":
                    recordExists = row.GetValue<bool>(column.Name);
                    break;
                case "version":
                    version = row.GetValue<long>(column.Name);
                    break;
                case "state":
                    state = row.IsNull(column.Name) ? null : row.GetValue<byte[]>(column.Name);
                    break;
            }
        }

        return new CassandraResult(applied, recordExists, version, state);
    }

    private static async Task<T> WaitAsync<T>(
        Task<T> operation,
        CancellationToken cancellationToken,
        Action<Task<T>>? onCancellation = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            onCancellation?.Invoke(operation);
            _ = operation.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private sealed class OwnedResource(ICluster cluster)
    {
        private int _disposeRequested;
        private int _disposed;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public void Request(Task? completion = null)
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;

            if (completion is { IsCompleted: false })
            {
                _ = completion.ContinueWith(
                    static (_, state) => ((OwnedResource)state!).Dispose(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                Dispose();
            }
        }

        private void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    cluster.Dispose();
                }
                finally
                {
                    _completion.TrySetResult();
                }
            }
        }
    }

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) =>
        ReadStateAsync(stateName, grainId, grainState, CancellationToken.None);

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateName);
        ArgumentNullException.ThrowIfNull(grainState);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await SessionExecuteAsync(Session, _read!.Bind(_clusterOptions.ServiceId, grainId.ToString(), stateName).SetConsistencyLevel(_options.ConsistencyLevel), cancellationToken).ConfigureAwait(false);
        if (result.RecordExists is not true)
        {
            grainState.RecordExists = false;
            grainState.State = CreateInstance<T>();
            grainState.ETag = result.Version is { } version ? FormatVersion(version) : null;
            return;
        }

        grainState.RecordExists = true;
        grainState.State = _serializer.Deserialize<T>(result.State!);
        grainState.ETag = FormatVersion(result.Version!.Value);
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) =>
        WriteStateAsync(stateName, grainId, grainState, CancellationToken.None);

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateName);
        ArgumentNullException.ThrowIfNull(grainState);
        cancellationToken.ThrowIfCancellationRequested();
        var expected = ParseEtag(grainState.ETag, nameof(WriteStateAsync));
        var grainType = grainId.Type.ToString();
        var payload = _serializer.Serialize(grainState.State).ToArray();
        var now = DateTimeOffset.UtcNow;
        var next = expected is null ? 1 : NextVersion(expected.Value, nameof(WriteStateAsync));
        var statement = expected is null
            ? _insert!.Bind(_clusterOptions.ServiceId, grainId.ToString(), stateName, grainType, 1L, payload, now)
            : _update!.Bind(grainType, next, payload, now, _clusterOptions.ServiceId, grainId.ToString(), stateName, expected.Value);
        var result = await SessionExecuteAsync(Session, statement.SetConsistencyLevel(_options.ConsistencyLevel).SetSerialConsistencyLevel(_options.SerialConsistencyLevel), cancellationToken).ConfigureAwait(false);
        if (!Applied(result))
        {
            throw Inconsistent(nameof(WriteStateAsync), stateName, grainId, grainState.ETag);
        }

        grainState.ETag = FormatVersion(next);
        grainState.RecordExists = true;
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) =>
        ClearStateAsync(stateName, grainId, grainState, CancellationToken.None);

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateName);
        ArgumentNullException.ThrowIfNull(grainState);
        cancellationToken.ThrowIfCancellationRequested();
        var expected = ParseEtag(grainState.ETag, nameof(ClearStateAsync));
        var next = expected is null || _options.DeleteStateOnClear ? 0 : NextVersion(expected.Value, nameof(ClearStateAsync));
        string? resultingEtag = expected is null ? null : _options.DeleteStateOnClear ? null : FormatVersion(next);
        if (expected is not null)
        {
            CassandraResult result;
            if (_options.DeleteStateOnClear)
            {
                result = await SessionExecuteAsync(Session, _delete!.Bind(_clusterOptions.ServiceId, grainId.ToString(), stateName, expected.Value).SetConsistencyLevel(_options.ConsistencyLevel).SetSerialConsistencyLevel(_options.SerialConsistencyLevel), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await SessionExecuteAsync(Session, _clearWithEtag!.Bind(next, DateTimeOffset.UtcNow, _clusterOptions.ServiceId, grainId.ToString(), stateName, expected.Value).SetConsistencyLevel(_options.ConsistencyLevel).SetSerialConsistencyLevel(_options.SerialConsistencyLevel), cancellationToken).ConfigureAwait(false);
            }
            if (!Applied(result))
            {
                throw Inconsistent(nameof(ClearStateAsync), stateName, grainId, grainState.ETag);
            }
        }
        else if (_options.DeleteStateOnClear)
        {
            var result = await SessionExecuteAsync(
                Session,
                _deleteWithoutEtag!.Bind(_clusterOptions.ServiceId, grainId.ToString(), stateName)
                    .SetConsistencyLevel(_options.ConsistencyLevel)
                    .SetSerialConsistencyLevel(_options.SerialConsistencyLevel),
                cancellationToken).ConfigureAwait(false);
            if (!Applied(result) && HasActiveRow(result))
            {
                throw Inconsistent(nameof(ClearStateAsync), stateName, grainId, grainState.ETag);
            }
        }
        else
        {
            // Without an ETag there is no safe mutation which can distinguish an active row from a stale clear.
            // Read-only evaluation preserves logical tombstones and cannot erase a concurrent write.
            var result = await SessionExecuteAsync(
                Session,
                _read!.Bind(_clusterOptions.ServiceId, grainId.ToString(), stateName)
                    .SetConsistencyLevel(_options.ConsistencyLevel),
                cancellationToken).ConfigureAwait(false);
            if (result.RecordExists is true)
            {
                throw Inconsistent(nameof(ClearStateAsync), stateName, grainId, grainState.ETag);
            }

            if (result.Version is { } version)
            {
                resultingEtag = FormatVersion(version);
            }
        }

        grainState.ETag = resultingEtag;
        grainState.RecordExists = false;
        grainState.State = CreateInstance<T>();
    }

    private static bool Applied(CassandraResult result) => result.Applied;
    private static bool HasActiveRow(CassandraResult result) => result.RecordExists is true;
    private static string FormatVersion(long version)
    {
        if (version < 0) throw new InconsistentStateException($"Invalid negative Cassandra version: {version}.", null, version.ToString(CultureInfo.InvariantCulture));
        return version.ToString(CultureInfo.InvariantCulture);
    }

    private static long NextVersion(long version, string operation)
    {
        if (version == long.MaxValue)
        {
            throw new InconsistentStateException($"Cassandra version cannot advance beyond {long.MaxValue} for {operation}.", null, FormatVersion(version));
        }

        return version + 1;
    }
    private static long? ParseEtag(string? etag, string operation)
    {
        if (string.IsNullOrWhiteSpace(etag)) return null;
        if (long.TryParse(etag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0) return result;
        throw new InconsistentStateException($"Invalid numeric ETag for {operation}: '{etag}'.", null, etag);
    }
    private InconsistentStateException Inconsistent(string operation, string stateName, GrainId grainId, string? etag) =>
        new($"Version conflict ({operation}): ServiceId={_clusterOptions.ServiceId} ProviderName={_name} StateName={stateName} GrainId={grainId} ETag={etag}.", null, etag);
    private T CreateInstance<T>() => _activatorProvider.GetActivator<T>().Create();

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        var close = BeginClose();
        cancellationToken.ThrowIfCancellationRequested();
        await close.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        BeginClose().GetAwaiter().GetResult();
    }

    private (ISession? Session, bool OwnsSession) TakeSessionForDisposal()
    {
        lock (_operationLock)
        {
            var session = _session;
            var ownsSession = _ownsSession;
            _session = null;
            _ownsSession = false;
            return (session, ownsSession);
        }
    }

    internal static bool ValidateIdentifier(string? value, string parameterName) => CassandraIdentifier.IsValid(value);

}

internal static class CassandraGrainStorageFactory
{
    public static CassandraGrainStorage Create(IServiceProvider services, string name)
    {
        var options = services.GetRequiredService<IOptionsMonitor<CassandraGrainStorageOptions>>().Get(name);
        if (options.CreateSessionAsync is null && !string.IsNullOrWhiteSpace(options.ConnectionString)) options.ConfigureClient(options.ConnectionString, options.Keyspace);
        return ActivatorUtilities.CreateInstance<CassandraGrainStorage>(services, name, options, services.GetProviderClusterOptions(name));
    }
}

internal sealed class NonDisposingSession(ISession inner) : ISession
{
    private readonly ICluster _cluster = new NonDisposingCluster(inner.Cluster);

    public ICluster Cluster => _cluster;
    public bool IsDisposed => inner.IsDisposed;
    public string Keyspace => inner.Keyspace;
    public string SessionName => inner.SessionName;
    public int BinaryProtocolVersion => inner.BinaryProtocolVersion;
    public UdtMappingDefinitions UserDefinedTypes => inner.UserDefinedTypes;

    public IAsyncResult BeginExecute(IStatement statement, AsyncCallback callback, object state) => inner.BeginExecute(statement, callback, state);
    public IAsyncResult BeginExecute(string query, ConsistencyLevel consistency, AsyncCallback callback, object state) => inner.BeginExecute(query, consistency, callback, state);
    public IAsyncResult BeginPrepare(string query, AsyncCallback callback, object state) => inner.BeginPrepare(query, callback, state);
    public void ChangeKeyspace(string keyspace) => inner.ChangeKeyspace(keyspace);
    public void CreateKeyspace(string keyspace, Dictionary<string, string> replication, bool durableWrites) => inner.CreateKeyspace(keyspace, replication, durableWrites);
    public void CreateKeyspaceIfNotExists(string keyspace, Dictionary<string, string> replication, bool durableWrites) => inner.CreateKeyspaceIfNotExists(keyspace, replication, durableWrites);
    public void DeleteKeyspace(string keyspace) => inner.DeleteKeyspace(keyspace);
    public void DeleteKeyspaceIfExists(string keyspace) => inner.DeleteKeyspaceIfExists(keyspace);
    public RowSet EndExecute(IAsyncResult ar) => inner.EndExecute(ar);
    public PreparedStatement EndPrepare(IAsyncResult ar) => inner.EndPrepare(ar);
    public RowSet Execute(string query) => inner.Execute(query);
    public RowSet Execute(IStatement statement) => inner.Execute(statement);
    public RowSet Execute(IStatement statement, string executionProfileName) => inner.Execute(statement, executionProfileName);
    public RowSet Execute(string query, string executionProfileName) => inner.Execute(query, executionProfileName);
    public RowSet Execute(string query, ConsistencyLevel consistency) => inner.Execute(query, consistency);
    public RowSet Execute(string query, int pageSize) => inner.Execute(query, pageSize);
    public Task<RowSet> ExecuteAsync(IStatement statement, string executionProfileName) => inner.ExecuteAsync(statement, executionProfileName);
    public Task<RowSet> ExecuteAsync(IStatement statement) => inner.ExecuteAsync(statement);
    public global::Cassandra.DataStax.Graph.GraphResultSet ExecuteGraph(global::Cassandra.DataStax.Graph.IGraphStatement statement) => inner.ExecuteGraph(statement);
    public global::Cassandra.DataStax.Graph.GraphResultSet ExecuteGraph(global::Cassandra.DataStax.Graph.IGraphStatement statement, string executionProfileName) => inner.ExecuteGraph(statement, executionProfileName);
    public Task<global::Cassandra.DataStax.Graph.GraphResultSet> ExecuteGraphAsync(global::Cassandra.DataStax.Graph.IGraphStatement statement) => inner.ExecuteGraphAsync(statement);
    public Task<global::Cassandra.DataStax.Graph.GraphResultSet> ExecuteGraphAsync(global::Cassandra.DataStax.Graph.IGraphStatement statement, string executionProfileName) => inner.ExecuteGraphAsync(statement, executionProfileName);
    public global::Cassandra.Metrics.IDriverMetrics GetMetrics() => inner.GetMetrics();
    public PreparedStatement Prepare(string query) => inner.Prepare(query);
    public PreparedStatement Prepare(string query, IDictionary<string, byte[]> customPayload) => inner.Prepare(query, customPayload);
    public PreparedStatement Prepare(string query, string executionProfileName) => inner.Prepare(query, executionProfileName);
    public PreparedStatement Prepare(string query, string executionProfileName, IDictionary<string, byte[]> customPayload) => inner.Prepare(query, executionProfileName, customPayload);
    public Task<PreparedStatement> PrepareAsync(string query, string executionProfileName, IDictionary<string, byte[]> customPayload) => inner.PrepareAsync(query, executionProfileName, customPayload);
    public Task<PreparedStatement> PrepareAsync(string query, string executionProfileName) => inner.PrepareAsync(query, executionProfileName);
    public Task<PreparedStatement> PrepareAsync(string query, IDictionary<string, byte[]> customPayload) => inner.PrepareAsync(query, customPayload);
    public Task<PreparedStatement> PrepareAsync(string query) => inner.PrepareAsync(query);
    public Task ShutdownAsync() => inner.ShutdownAsync();
#pragma warning disable CS0618
    public void WaitForSchemaAgreement(RowSet rowSet) => inner.WaitForSchemaAgreement(rowSet);
    public bool WaitForSchemaAgreement(System.Net.IPEndPoint host) => inner.WaitForSchemaAgreement(host);
#pragma warning restore CS0618

    public void Dispose()
    {
    }
}

internal sealed class NonDisposingCluster(ICluster inner) : ICluster
{
    public event Action<Host> HostAdded { add => inner.HostAdded += value; remove => inner.HostAdded -= value; }
    public event Action<Host> HostRemoved { add => inner.HostRemoved += value; remove => inner.HostRemoved -= value; }
    public ICollection<Host> AllHosts() => inner.AllHosts();
    public ISession Connect(string keyspace) => inner.Connect(keyspace);
    public ISession Connect() => inner.Connect();
    public Task<ISession> ConnectAsync(string keyspace) => inner.ConnectAsync(keyspace);
    public Task<ISession> ConnectAsync() => inner.ConnectAsync();
    public global::Cassandra.Configuration Configuration => inner.Configuration;
    public global::Cassandra.Metadata Metadata => inner.Metadata;
    public Host GetHost(System.Net.IPEndPoint address) => inner.GetHost(address);
    public ICollection<Host> GetReplicas(byte[] routingKey) => inner.GetReplicas(routingKey);
    public ICollection<Host> GetReplicas(string keyspace, byte[] routingKey) => inner.GetReplicas(keyspace, routingKey);
    public bool RefreshSchema(string keyspace, string table) => inner.RefreshSchema(keyspace, table);
    public Task<bool> RefreshSchemaAsync(string keyspace, string table) => inner.RefreshSchemaAsync(keyspace, table);
    public void Shutdown(int timeoutMillis) { }
    public Task ShutdownAsync(int timeoutMillis) => Task.CompletedTask;
    public void Dispose() { }
}
