using System.Runtime.ExceptionServices;

namespace Orleans.Clustering.TestKit;

/// <summary>A separately constructed membership provider and its optional resource owner.</summary>
public sealed class MembershipTableTestHandle : IAsyncDisposable
{
    private readonly Func<ValueTask>? _dispose;
    private int _disposed;

    /// <summary>Creates a handle. The supplied disposer, if any, owns all resources for this handle.</summary>
    public MembershipTableTestHandle(IMembershipTable table, Func<ValueTask>? disposeAsync = null)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        _dispose = disposeAsync;
    }

    /// <summary>Gets the independently constructed provider.</summary>
    public IMembershipTable Table { get; }

    /// <summary>Disposes owned resources at most once. The table itself is not implicitly disposed.</summary>
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 && _dispose is not null ? _dispose() : ValueTask.CompletedTask;
}

/// <summary>Owns isolated cluster scopes, independent handles, initialization, and bounded teardown.</summary>
public sealed class MembershipTableTestFixture : IAsyncDisposable
{
    private readonly Func<string, string, CancellationToken, ValueTask<MembershipTableTestHandle>> _factory;
    private readonly Func<string, CancellationToken, ValueTask<bool>> _isDeleted;
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly List<MembershipTableTestHandle> _handles = [];
    private readonly Dictionary<string, IMembershipTable> _clusters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedClusters = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly List<Exception> _operationFailures = [];
    private bool _initialized;
    private int _disposed;
    private Task? _disposalTask;

    /// <summary>Creates a fixture from a synchronous factory accepting each requested cluster ID.</summary>
    public MembershipTableTestFixture(string providerName, Func<string, IMembershipTable> factory,
        Func<string, CancellationToken, ValueTask<bool>> isDeletedAsync, string? serviceId = null)
        : this(providerName, Wrap(factory), isDeletedAsync, serviceId) { }

    /// <summary>Creates a fixture from an asynchronous cluster-aware factory with explicit ownership.</summary>
    public MembershipTableTestFixture(string providerName,
        Func<string, CancellationToken, ValueTask<MembershipTableTestHandle>> factory,
        Func<string, CancellationToken, ValueTask<bool>> isDeletedAsync, string? serviceId = null)
        : this(providerName, Wrap(factory), isDeletedAsync, serviceId) { }

    /// <summary>Creates a fixture. All handles use the same service ID and backend, but the supplied cluster ID.</summary>
    /// <param name="providerName">The non-secret provider label used in diagnostics.</param>
    /// <param name="factory">Creates an independently owned handle over the requested scope's current backend.</param>
    /// <param name="isDeletedAsync">
    /// Read-only native probe of the specified cluster's original backing state. Returns true when its membership
    /// data is deleted or its owner has terminally invalidated the store; returns false while seeded data remains.
    /// The probe runs before owner disposal, must preserve backend identity, and must propagate infrastructure failures.
    /// Persistent providers can inspect the same scope through independent backend access; terminal providers inspect
    /// native invalidation. The suite checks the probe against populated data before invoking deletion.
    /// </param>
    /// <param name="serviceId">The shared service ID.</param>
    public MembershipTableTestFixture(string providerName,
        Func<string, string, CancellationToken, ValueTask<MembershipTableTestHandle>> factory,
        Func<string, CancellationToken, ValueTask<bool>> isDeletedAsync, string? serviceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _isDeleted = isDeletedAsync ?? throw new ArgumentNullException(nameof(isDeletedAsync));
        ProviderName = providerName;
        ServiceId = serviceId ?? "clustering-testkit";
        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceId);
        var suffix = Guid.NewGuid().ToString("N");
        ClusterId = $"ctk-a-{suffix}";
        OtherClusterId = $"{ClusterId}-other";
    }

    /// <summary>Gets the diagnostic provider label. Do not put credentials in this value.</summary>
    public string ProviderName { get; }
    /// <summary>Gets the common service ID.</summary>
    public string ServiceId { get; }
    /// <summary>Gets the first isolated cluster ID.</summary>
    public string ClusterId { get; }
    /// <summary>Gets the second isolated cluster ID.</summary>
    public string OtherClusterId { get; }
    /// <summary>Gets the first provider handle after initialization.</summary>
    public IMembershipTable First { get; private set; } = null!;
    /// <summary>Gets a separately constructed provider for the first cluster.</summary>
    public IMembershipTable Second { get; private set; } = null!;
    /// <summary>Gets the provider for the second cluster.</summary>
    public IMembershipTable OtherCluster { get; private set; } = null!;

    /// <summary>Constructs and initializes the three required handles. Partial failures clean up acquired resources.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (_endedClusters.Count > 0)
                    throw new InvalidOperationException("A deleted history requires a new fixture and owner.");
            }
            try { await InitializeCoreAsync(cancellationToken); }
            catch (Exception primary)
            {
                try { await DisposeAsync(cancellationToken.IsCancellationRequested ? TimeSpan.Zero : TimeSpan.FromSeconds(30)); }
                catch (Exception cleanup) { ClusteringTestKitDiagnostics.AttachCleanupFailure(primary, cleanup); }
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask InitializeCoreAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource pending;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_endedClusters.Count > 0)
                throw new InvalidOperationException("A deleted history requires a new fixture and owner.");
            if (_initialized) return;
            pending = BeginOperation();
        }
        try
        {
            First = (await CreateAdditionalHandleAsync(ClusterId, cancellationToken)).Table;
            Second = (await CreateAdditionalHandleAsync(ClusterId, cancellationToken)).Table;
            OtherCluster = (await CreateAdditionalHandleAsync(OtherClusterId, cancellationToken)).Table;
            foreach (var table in new[] { First, Second, OtherCluster })
            {
                var operation = InitializeTableAsync(table);
                try { await operation.WaitAsync(cancellationToken); }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    _ = ObserveLateCleanupFailureAsync(operation, exception);
                    throw;
                }
            }
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                _initialized = true;
            }
        }
        finally
        {
            EndOperation(pending);
        }
    }

    private async Task InitializeTableAsync(IMembershipTable table)
    {
        TaskCompletionSource pending;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            pending = BeginOperation();
        }
        try { await table.InitializeMembershipTableAsync(true, CancellationToken.None); }
        catch (Exception exception)
        {
            lock (_lifecycleLock) _operationFailures.Add(exception);
            throw;
        }
        finally { EndOperation(pending); }
    }

    /// <summary>Acquires another handle in one of this fixture's owned scopes. Initialization is explicit.</summary>
    public async ValueTask<MembershipTableTestHandle> CreateAdditionalHandleAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource pending;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (clusterId != ClusterId && clusterId != OtherClusterId)
                throw new ArgumentException("Additional handles must belong to a fixture-owned cluster.", nameof(clusterId));
            if (_endedClusters.Contains(clusterId))
                throw new InvalidOperationException("A deleted history requires a new fixture and owner.");
            cancellationToken.ThrowIfCancellationRequested();
            pending = BeginOperation();
        }
        var operation = CreateAdditionalHandleCoreAsync(clusterId, pending);
        try { return await operation.WaitAsync(cancellationToken); }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveLateCleanupFailureAsync(operation, exception);
            throw;
        }
    }

    private async Task<MembershipTableTestHandle> CreateAdditionalHandleCoreAsync(string clusterId, TaskCompletionSource pending)
    {
        try
        {
            MembershipTableTestHandle handle;
            try
            {
                handle = await _factory(ServiceId, clusterId, CancellationToken.None)
                    ?? throw new InvalidOperationException("The membership handle factory returned null.");
            }
            catch (Exception exception)
            {
                lock (_lifecycleLock) _operationFailures.Add(exception);
                throw;
            }
            bool disposed;
            lock (_lifecycleLock)
            {
                disposed = _disposed != 0;
                if (!disposed && !_endedClusters.Contains(clusterId))
                {
                    var duplicate = _handles.Any(h => ReferenceEquals(h.Table, handle.Table));
                    if (!_handles.Contains(handle)) _handles.Add(handle);
                    _clusters.TryAdd(clusterId, handle.Table);
                    ClusteringTestKitDiagnostics.Require(!duplicate,
                        $"provider={ProviderName}; cluster={clusterId}; factory returned the same provider instance; independently construct each IMembershipTable");
                    return handle;
                }
            }

            try { await handle.DisposeAsync(); }
            catch (Exception exception)
            {
                lock (_lifecycleLock) _operationFailures.Add(exception);
                throw;
            }
            if (disposed)
                throw new ObjectDisposedException(nameof(MembershipTableTestFixture), "The factory completed after its fixture was disposed.");
            throw new InvalidOperationException("The factory completed after its cluster history ended.");
        }
        finally
        {
            EndOperation(pending);
        }
    }

    private TaskCompletionSource BeginOperation()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingOperations.Add(pending.Task);
        return pending;
    }

    private void EndOperation(TaskCompletionSource pending)
    {
        lock (_lifecycleLock)
        {
            _pendingOperations.Remove(pending.Task);
            pending.SetResult();
        }
    }

    internal async Task AssertHistoryPresentAsync(string clusterId, CancellationToken cancellationToken)
    {
        ClusteringTestKitDiagnostics.Require(!await ObserveDeletionAsync(clusterId, cancellationToken),
            $"deletion probe reported deleted populated history: cluster={clusterId}");
    }

    internal async Task<bool> DeleteClusterAsync(IMembershipTable table, string clusterId, bool allowRetained, CancellationToken cancellationToken)
    {
        await AssertHistoryPresentAsync(clusterId, cancellationToken);
        // A failed request can have committed. Retire these handles until native evidence proves retention.
        lock (_lifecycleLock) _endedClusters.Add(clusterId);
        var rejected = false;
        try
        {
            await table.DeleteMembershipTableEntriesAsync(clusterId, cancellationToken);
        }
        catch (ArgumentException exception) when (allowRetained && exception.ParamName == nameof(clusterId))
        {
            rejected = true;
        }

        var deleted = await ObserveDeletionAsync(clusterId, cancellationToken);
        ClusteringTestKitDiagnostics.Require(!rejected || !deleted,
            $"rejected foreign deletion changed its target scope: cluster={clusterId}");
        ClusteringTestKitDiagnostics.Require(allowRetained || deleted,
            $"deletion left populated history: cluster={clusterId}");
        return deleted;
    }

    private async ValueTask<bool> ObserveDeletionAsync(string clusterId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleLock) _endedClusters.Add(clusterId);
        var deleted = await _isDeleted(clusterId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!deleted)
        {
            lock (_lifecycleLock) _endedClusters.Remove(clusterId);
        }
        return deleted;
    }

    /// <summary>Runs a complete isolated case and preserves its primary failure if teardown also fails.</summary>
    public async Task RunAsync(Func<MembershipTableTestFixture, CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        Exception? primary = null;
        try
        {
            await InitializeAsync(cancellationToken);
            await action(this, cancellationToken);
        }
        catch (Exception exception) { primary = exception; }
        try { await DisposeAsync(); }
        catch (Exception cleanup)
        {
            if (primary is null) primary = cleanup;
            else ClusteringTestKitDiagnostics.AttachCleanupFailure(primary, cleanup);
        }

        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>Waits up to 30 seconds for cleanup. Owners remain alive until their cleanup operations complete.</summary>
    public ValueTask DisposeAsync() => DisposeAsync(TimeSpan.FromSeconds(30));

    internal async ValueTask DisposeAsync(TimeSpan waitTimeout)
    {
        Task completion;
        TaskCompletionSource? started = null;
        Task[] pending = [];
        lock (_lifecycleLock)
        {
            if (_disposalTask is null)
            {
                _disposed = 1;
                pending = _pendingOperations.ToArray();
                started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposalTask = started.Task;
            }
            completion = _disposalTask;
        }

        if (started is not null) _ = CompleteDisposalAsync(pending, started);
        try
        {
            await completion.WaitAsync(waitTimeout);
        }
        catch (TimeoutException exception) when (!completion.IsCompleted)
        {
            exception.Data[ClusteringTestKitDiagnostics.CleanupCompletionKey] = completion;
            _ = ObserveLateCleanupFailureAsync(completion, exception);
            throw;
        }
    }

    private async Task CompleteDisposalAsync(Task[] pending, TaskCompletionSource completion)
    {
        await Task.WhenAll(pending);
        KeyValuePair<string, IMembershipTable>[] clusters;
        MembershipTableTestHandle[] handles;
        lock (_lifecycleLock)
        {
            clusters = _clusters.Where(pair => !_endedClusters.Contains(pair.Key)).ToArray();
            handles = _handles.ToArray();
        }
        List<Exception> failures;
        lock (_lifecycleLock) failures = [.. _operationFailures];
        foreach (var (cluster, table) in clusters)
        {
            // A non-cancellable dispatch retains actual completion for tokenless compatibility providers.
            try { await table.DeleteMembershipTableEntriesAsync(cluster, CancellationToken.None); }
            catch (Exception exception) { failures.Add(new InvalidOperationException($"cleanup cluster={cluster}", exception)); }
        }

        foreach (var handle in handles.AsEnumerable().Reverse())
        {
            try { await handle.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
        }

        if (failures.Count > 0) completion.SetException(new AggregateException("Membership fixture teardown failed.", failures));
        else completion.SetResult();
    }

    private static async Task ObserveLateCleanupFailureAsync(Task completion, Exception timeout)
    {
        try { await completion; }
        catch (Exception failure) { ClusteringTestKitDiagnostics.AttachCleanupFailure(timeout, failure); }
    }

    private static Func<string, string, CancellationToken, ValueTask<MembershipTableTestHandle>> Wrap(Func<string, IMembershipTable> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return (_, cluster, _) => ValueTask.FromResult(new MembershipTableTestHandle(factory(cluster)));
    }

    private static Func<string, string, CancellationToken, ValueTask<MembershipTableTestHandle>> Wrap(
        Func<string, CancellationToken, ValueTask<MembershipTableTestHandle>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return (_, cluster, token) => factory(cluster, token);
    }
}
