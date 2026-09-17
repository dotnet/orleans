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
    private readonly List<MembershipTableTestHandle> _handles = [];
    private readonly Dictionary<string, IMembershipTable> _clusters = new(StringComparer.Ordinal);
    private bool _initialized;
    private int _disposed;

    /// <summary>Creates a fixture from a synchronous factory accepting each requested cluster ID.</summary>
    public MembershipTableTestFixture(string providerName, Func<string, IMembershipTable> factory, string? serviceId = null)
        : this(providerName, Wrap(factory), serviceId) { }

    /// <summary>Creates a fixture from an asynchronous cluster-aware factory with explicit ownership.</summary>
    public MembershipTableTestFixture(string providerName,
        Func<string, CancellationToken, ValueTask<MembershipTableTestHandle>> factory, string? serviceId = null)
        : this(providerName, Wrap(factory), serviceId) { }

    /// <summary>Creates a fixture. All handles use the same service ID and backend, but the supplied cluster ID.</summary>
    public MembershipTableTestFixture(string providerName,
        Func<string, string, CancellationToken, ValueTask<MembershipTableTestHandle>> factory, string? serviceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized) return;
        try
        {
            First = (await CreateAdditionalHandleAsync(ClusterId, cancellationToken)).Table;
            Second = (await CreateAdditionalHandleAsync(ClusterId, cancellationToken)).Table;
            OtherCluster = (await CreateAdditionalHandleAsync(OtherClusterId, cancellationToken)).Table;
            foreach (var table in new[] { First, Second, OtherCluster })
                await table.InitializeMembershipTableAsync(true, cancellationToken);
            _initialized = true;
        }
        catch (Exception primary)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanup) { ClusteringTestKitDiagnostics.AttachCleanupFailure(primary, cleanup); }
            throw;
        }
    }

    /// <summary>Acquires another handle in one of this fixture's owned scopes. Initialization is explicit.</summary>
    public async ValueTask<MembershipTableTestHandle> CreateAdditionalHandleAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (clusterId != ClusterId && clusterId != OtherClusterId)
            throw new ArgumentException("Additional handles must belong to a fixture-owned cluster.", nameof(clusterId));
        cancellationToken.ThrowIfCancellationRequested();
        var handle = await _factory(ServiceId, clusterId, cancellationToken)
            ?? throw new InvalidOperationException("The membership handle factory returned null.");
        if (Volatile.Read(ref _disposed) != 0)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await handle.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            throw new ObjectDisposedException(nameof(MembershipTableTestFixture), "The factory completed after its fixture was disposed.");
        }
        var duplicate = _handles.Any(h => ReferenceEquals(h.Table, handle.Table));
        if (!_handles.Contains(handle)) _handles.Add(handle);
        _clusters.TryAdd(clusterId, handle.Table);
        ClusteringTestKitDiagnostics.Require(!duplicate,
            $"provider={ProviderName}; cluster={clusterId}; factory returned the same provider instance; independently construct each IMembershipTable");
        return handle;
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

    /// <summary>Deletes only owned clusters and disposes every acquired owner, with independent bounded cancellation.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var failures = new List<Exception>();
        foreach (var (cluster, table) in _clusters)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await table.DeleteMembershipTableEntriesAsync(cluster, timeout.Token).WaitAsync(timeout.Token); }
            catch (Exception exception) { failures.Add(new InvalidOperationException($"cleanup cluster={cluster}", exception)); }
        }

        foreach (var handle in _handles.AsEnumerable().Reverse())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await handle.DisposeAsync().AsTask().WaitAsync(timeout.Token); }
            catch (Exception exception) { failures.Add(exception); }
        }

        if (failures.Count > 0) throw new AggregateException("Membership fixture teardown failed.", failures);
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
