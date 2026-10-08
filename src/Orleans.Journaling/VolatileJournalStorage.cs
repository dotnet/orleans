using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Orleans.Journaling.Json;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides shared in-memory journal storage instances identified by journal id.
/// </summary>
public sealed class VolatileJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog
{
    private const string ProviderName = "volatile";
    private readonly IOptions<JournaledStateManagerOptions>? _options;
    private readonly ConcurrentDictionary<string, VolatileJournalStorage.Store> _storage = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _storageKeys = new(StringComparer.Ordinal);
    private readonly object _catalogLock = new();
    private readonly JournalStorageTelemetry _telemetry;
    private readonly VolatileJournalStorageOptions _storageOptions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorageProvider"/> class using the default journal format.
    /// </summary>
    public VolatileJournalStorageProvider()
    {
        _telemetry = JournalStorageTelemetry.CreateForDirectConstruction();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorageProvider"/> class.
    /// </summary>
    /// <param name="options">The journaled state manager options.</param>
    public VolatileJournalStorageProvider(IOptions<JournaledStateManagerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _telemetry = JournalStorageTelemetry.CreateForDirectConstruction();
    }

    /// <summary>
    /// Initializes an in-memory provider using the configured Orleans metrics meter.
    /// </summary>
    /// <param name="options">The journaled state manager options.</param>
    /// <param name="instruments">The Orleans runtime metrics meter.</param>
    public VolatileJournalStorageProvider(IOptions<JournaledStateManagerOptions> options, OrleansInstruments instruments)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(instruments);
        _options = options;
        _telemetry = new JournalStorageTelemetry(instruments);
    }

    /// <summary>
    /// Initializes an in-memory provider with configurable snapshot thresholds.
    /// </summary>
    /// <param name="options">The journaled state manager options.</param>
    /// <param name="storageOptions">The provider's snapshot thresholds.</param>
    /// <param name="instruments">The optional Orleans runtime metrics meter.</param>
    public VolatileJournalStorageProvider(
        IOptions<JournaledStateManagerOptions> options,
        IOptions<VolatileJournalStorageOptions> storageOptions,
        OrleansInstruments? instruments) : this(options)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        var configuration = storageOptions.Value;
        configuration.Validate();
        _storageOptions = new()
        {
            MaxAppendsBeforeSnapshot = configuration.MaxAppendsBeforeSnapshot,
            MaxBytesBeforeSnapshot = configuration.MaxBytesBeforeSnapshot
        };
        if (instruments is not null)
        {
            _telemetry = new JournalStorageTelemetry(instruments);
        }
    }

    /// <inheritdoc/>
    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var journalFormatKey = GetJournalFormatKey();
        if (!_storage.TryGetValue(journalId.Value, out var store))
        {
            lock (_catalogLock)
            {
                if (!_storage.TryGetValue(journalId.Value, out store))
                {
                    store = new VolatileJournalStorage.Store(journalId.Value);
                    _storageKeys.Add(journalId.Value);
                    _storage[journalId.Value] = store;
                }
            }
        }

        return new VolatileJournalStorage(store, journalFormatKey, _storageOptions);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        JournalCatalogListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = new JournalCatalogRange(options);
        if (range.IsEmpty)
        {
            yield break;
        }

        string[] keys;
        lock (_catalogLock)
        {
            if (_storageKeys.Count == 0)
            {
                keys = [];
            }
            else
            {
                var lower = range.LowerBound ?? _storageKeys.Min!;
                var upper = range.UpperBound ?? _storageKeys.Max!;
                keys = string.CompareOrdinal(lower, upper) <= 0
                    ? _storageKeys.GetViewBetween(lower, upper).ToArray()
                    : [];
            }
        }

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!range.Contains(key))
            {
                continue;
            }

            var store = _storage[key];
            IJournalMetadata? metadata = null;
            lock (store.SyncRoot)
            {
                if (!store.Exists)
                {
                    continue;
                }

                if (range.IncludeMetadata)
                {
                    metadata = store.GetMetadata();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _telemetry.OnCatalogEntry(ProviderName);
            yield return new JournalCatalogEntry(new JournalId(key), metadata);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string GetJournalFormatKey()
        => JournalFormatServices.ValidateJournalFormatKey(_options?.Value.JournalFormatKey ?? JsonLinesJournalFormat.JournalFormatKey);
}

/// <summary>
/// An in-memory, volatile implementation of <see cref="IJournalStorage"/> for non-durable use cases, such as development and testing.
/// </summary>
public sealed class VolatileJournalStorage : IJournalStorage, IRetainedJournalStorage
{
    private readonly Store _store;
    private readonly int _maxAppendsBeforeSnapshot;
    private readonly long _maxBytesBeforeSnapshot;
    private string? _configuredJournalFormatKey;

    /// <summary>
    /// Initializes a new isolated in-memory journal storage instance using the default journal format.
    /// </summary>
    public VolatileJournalStorage() : this(new Store(CreateVolatileStorageId()), journalFormatKey: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorage"/> class.
    /// </summary>
    /// <param name="journalFormatKey">The journal format key to stamp on writes.</param>
    public VolatileJournalStorage(string? journalFormatKey) : this(new Store(CreateVolatileStorageId()), journalFormatKey)
    {
    }

    /// <summary>
    /// Initializes an isolated in-memory journal with configurable snapshot thresholds.
    /// </summary>
    /// <param name="journalFormatKey">The journal format key to stamp on writes.</param>
    /// <param name="options">The snapshot thresholds.</param>
    public VolatileJournalStorage(string? journalFormatKey, VolatileJournalStorageOptions options)
        : this(new Store(CreateVolatileStorageId()), journalFormatKey, options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    internal VolatileJournalStorage(Store store, string? journalFormatKey, VolatileJournalStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new();
        options.Validate();
        _store = store;
        _maxAppendsBeforeSnapshot = options.MaxAppendsBeforeSnapshot;
        _maxBytesBeforeSnapshot = options.MaxBytesBeforeSnapshot;
        SetConfiguredJournalFormatKey(journalFormatKey);
    }

    /// <inheritdoc/>
    public bool IsCompactionRequested
    {
        get
        {
            lock (_store.SyncRoot)
            {
                return _store.AppendCount >= _maxAppendsBeforeSnapshot || _store.AppendedBytes >= _maxBytesBeforeSnapshot;
            }
        }
    }

    // Diagnostic snapshots never expose pooled memory beyond a pinned lifetime.
    internal IReadOnlyList<byte[]> Segments
    {
        get
        {
            lock (_store.SyncRoot)
            {
                return _store.Segments.Select(static segment => segment.Length == 0 ? [] : segment.ToArray()).ToArray();
            }
        }
    }

    internal (int Segments, int ReaderReferences, long CopiedBytes, long SharedBytes, long RetainedCapacity, int RetainedPages) MemoryStatistics
    {
        get
        {
            lock (_store.SyncRoot)
            {
                var pages = new HashSet<ArcBufferPage>();
                foreach (var segment in _store.Segments) AddPages(segment, pages);
                foreach (var snapshot in _store.ReadSnapshots)
                {
                    foreach (var segment in snapshot) AddPages(segment, pages);
                }

                if (_store.CopyWriter is { } writer)
                {
                    using var tail = writer.PeekSlice(0);
                    pages.Add(tail.First);
                }

                return (_store.Segments.Count, _store.ReadSnapshots.Sum(static snapshot => snapshot.Count(static segment => segment.Length > 0)),
                    _store.CopiedBytes, _store.SharedBytes, pages.Sum(static page => (long)page.Array.Length), pages.Count);
            }
        }
    }

    private static void AddPages(ArcBuffer segment, HashSet<ArcBufferPage> pages)
    {
        if (segment.Length == 0) return;
        var remaining = segment.Length;
        var offset = segment.Offset;
        for (var page = segment.First; remaining > 0; page = page.Next!)
        {
            pages.Add(page);
            remaining -= page.Length - offset;
            offset = 0;
        }
    }

    internal string? StoredJournalFormatKey
    {
        get
        {
            lock (_store.SyncRoot)
            {
                return _store.StoredJournalFormatKey;
            }
        }

        set
        {
            lock (_store.SyncRoot)
            {
                _store.StoredJournalFormatKey = value;
            }
        }
    }

    internal void SetConfiguredJournalFormatKey(string? journalFormatKey)
    {
        _configuredJournalFormatKey = journalFormatKey;
    }

    /// <inheritdoc/>
    public ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = JournalMetadata.CopyProperties(metadata);
        lock (_store.SyncRoot)
        {
            if (_store.Exists)
            {
                return new(false);
            }

            _store.Create(values);
            return new(true);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            return new(_store.Exists ? _store.GetMetadata() : null);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<string>? remove = null,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var setValues = JournalMetadata.CopyProperties(set);
        var removeValues = CopyRemove(remove, setValues);
        lock (_store.SyncRoot)
        {
            if (!_store.Exists || expectedETag is not null && !string.Equals(expectedETag, _store.ETag, StringComparison.Ordinal))
            {
                return new((IJournalMetadata?)null);
            }

            _store.ApplyMetadataUpdate(setValues, removeValues);
            return new(_store.GetMetadata());
        }
    }

    /// <inheritdoc/>
    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        cancellationToken.ThrowIfCancellationRequested();

        ArcBuffer[] segments;
        IJournalMetadata metadata;
        lock (_store.SyncRoot)
        {
            metadata = _store.Exists ? _store.GetMetadata() : JournalMetadata.Empty;
            segments = new ArcBuffer[_store.Segments.Count];
            try
            {
                for (var i = 0; i < segments.Length; i++)
                {
                    var segment = _store.Segments[i];
                    if (segment.Length > 0) segments[i] = segment.Slice(0);
                }
                _store.ReadSnapshots.Add(segments);
            }
            catch
            {
                ReleaseSegments(segments);
                throw;
            }
        }

        try
        {
            consumer.Read(GetSegments(segments, cancellationToken), metadata, complete: true);
            return default;
        }
        finally
        {
            lock (_store.SyncRoot)
            {
                _store.ReadSnapshots.Remove(segments);
                if (_store.ReadSnapshots.Count == 0) _store.ReadSnapshots.TrimExcess();
                ReleaseSegments(segments);
            }
        }
    }

    private static IEnumerable<ReadOnlyMemory<byte>> GetSegments(ArcBuffer[] segments, CancellationToken cancellationToken)
    {
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Length == 0) continue;
            foreach (var memory in segment.MemorySegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return memory;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(ReadOnlySequence<byte> segment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            _store.Segments.EnsureCapacity(_store.Segments.Count + 1);
            var retained = _store.Copy(segment);
            Publish(retained);
            _store.CopiedBytes += segment.Length;
        }

        return default;
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAsync(ReadOnlySequence<byte> snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            var replacement = new List<ArcBuffer>(1);
            // Build before retiring the old journal: a failed copy must not alter committed storage.
            using var writer = new ArcBufferWriter();
            writer.Write(snapshot);
            var retained = snapshot.IsEmpty ? default : writer.PeekSlice(writer.Length);
            Publish(retained, replacement);
            _store.CopiedBytes += snapshot.Length;
        }

        return default;
    }

    ValueTask IRetainedJournalStorage.AppendRetainedAsync(ArcBuffer value, CancellationToken cancellationToken)
        => WriteRetained(value, replace: false, cancellationToken);

    ValueTask IRetainedJournalStorage.ReplaceRetainedAsync(ArcBuffer value, CancellationToken cancellationToken)
        => WriteRetained(value, replace: true, cancellationToken);

    private ValueTask WriteRetained(ArcBuffer value, bool replace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            var replacement = replace ? new List<ArcBuffer>(1) : null;
            if (!replace) _store.Segments.EnsureCapacity(_store.Segments.Count + 1);
            var retained = value.Length == 0 ? default : value.Slice(0);
            Publish(retained, replacement);
            _store.SharedBytes += value.Length;
        }

        return default;
    }

    // Called under the store lock, with an independently owned reference. Takes ownership of it.
    private void Publish(ArcBuffer retained, List<ArcBuffer>? replacement = null)
    {
        if (replacement is not null)
        {
            _store.ReleaseContents();
            _store.Segments = replacement;
            _store.AppendedBytes = 0;
            _store.AppendCount = 0;
        }
        else
        {
            _store.AppendedBytes += retained.Length;
            _store.AppendCount++;
        }

        _store.Exists = true;
        _store.StoredJournalFormatKey = _configuredJournalFormatKey;
        _store.Segments.Add(retained);
        _store.RefreshETag();
    }

    private static void ReleaseSegments(IEnumerable<ArcBuffer> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.Length > 0) segment.Dispose();
        }
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            _store.Delete();
        }

        return default;
    }

    private static string CreateVolatileStorageId() => $"volatile/{Guid.NewGuid():N}";

    internal sealed class Store(string storageId)
    {
        public object SyncRoot { get; } = new();

        // Every nonempty entry owns exactly one pin, independent of the writer and readers.
        public List<ArcBuffer> Segments { get; set; } = [];
        public HashSet<ArcBuffer[]> ReadSnapshots { get; } = [];
        public ArcBufferWriter? CopyWriter { get; private set; }
        public long CopiedBytes { get; set; }
        public long SharedBytes { get; set; }

        // Storage outlives individual handles/managers. Return its pins if the whole shared store
        // becomes unreachable, without requiring a new public disposal contract for handles.
        ~Store() => ReleaseContents();

        public ArcBuffer Copy(ReadOnlySequence<byte> input)
        {
            if (input.IsEmpty) return default;
            var length = checked((int)input.Length);
            var writer = CopyWriter ??= new ArcBufferWriter();
            try
            {
                writer.Write(input);
                return writer.ConsumeSlice(length);
            }
            catch
            {
                writer.Truncate(0);
                throw;
            }
        }

        public void ReleaseContents()
        {
            ReleaseSegments(Segments);
            Segments.Clear();
            Segments.Capacity = 0;
            CopyWriter?.Dispose();
            CopyWriter = null;
        }

        public long AppendCount { get; set; }

        public long AppendedBytes { get; set; }

        public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

        public string? StoredJournalFormatKey { get; set; }

        public bool Exists { get; set; }

        public long Version { get; private set; }

        public string? ETag { get; private set; }

        public void Create(IReadOnlyDictionary<string, string>? properties)
        {
            Exists = true;
            ReleaseContents();
            AppendCount = 0;
            AppendedBytes = 0;
            Properties.Clear();
            StoredJournalFormatKey = null;
            if (properties is not null)
            {
                foreach (var (key, value) in properties)
                {
                    Properties.Add(key, value);
                }
            }

            RefreshETag();
        }

        public void Delete()
        {
            Exists = false;
            ReleaseContents();
            AppendCount = 0;
            AppendedBytes = 0;
            Properties.Clear();
            StoredJournalFormatKey = null;
            ETag = null;
            Version++;
        }

        public IJournalMetadata GetMetadata() => new JournalMetadata(StoredJournalFormatKey, ETag, Properties);

        public bool ApplyMetadataUpdate(IReadOnlyDictionary<string, string> set, IReadOnlySet<string> remove)
        {
            var changed = false;
            foreach (var propertyName in remove)
            {
                changed |= Properties.Remove(propertyName);
            }

            foreach (var (propertyName, value) in set)
            {
                if (!Properties.TryGetValue(propertyName, out var currentValue)
                    || !string.Equals(currentValue, value, StringComparison.Ordinal))
                {
                    Properties[propertyName] = value;
                    changed = true;
                }
            }

            if (changed)
            {
                RefreshETag();
            }

            return changed;
        }

        public string RefreshETag()
        {
            Exists = true;
            ETag = (++Version).ToString("D", CultureInfo.InvariantCulture);
            return ETag;
        }

        public override string ToString() => storageId;
    }

    private static IReadOnlySet<string> CopyRemove(IEnumerable<string>? remove, IReadOnlyDictionary<string, string> set)
    {
        if (remove is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in remove)
        {
            JournalMetadata.ValidateCallerPropertyName(key);
            if (set.ContainsKey(key))
            {
                throw new ArgumentException($"Journal metadata property '{key}' cannot be both set and removed.", nameof(remove));
            }

            result.Add(key);
        }

        return result;
    }
}
