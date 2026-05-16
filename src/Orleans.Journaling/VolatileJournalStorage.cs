using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Orleans.Journaling.Json;

namespace Orleans.Journaling;

public sealed class VolatileJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog
{
    private readonly IOptions<JournaledStateManagerOptions>? _options;
    private readonly ConcurrentDictionary<string, VolatileJournalStorage.Store> _storage = new(StringComparer.Ordinal);

    public VolatileJournalStorageProvider()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorageProvider"/> class.
    /// </summary>
    /// <param name="options">The journaled state manager options.</param>
    public VolatileJournalStorageProvider(IOptions<JournaledStateManagerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var journalFormatKey = GetJournalFormatKey();
        var store = _storage.GetOrAdd(journalId.Value, static key => new VolatileJournalStorage.Store(key));
        return new VolatileJournalStorage(store, journalFormatKey);
    }

    public IJournalStorage Create(JournalStorageId storageId)
    {
        ArgumentNullException.ThrowIfNull(storageId);
        var journalFormatKey = GetJournalFormatKey();
        var store = _storage.GetOrAdd(storageId.Value, static key => new VolatileJournalStorage.Store(key));
        return new VolatileJournalStorage(store, journalFormatKey);
    }

    public async IAsyncEnumerable<JournalStorageId> ListAsync(
        JournalStoragePrefix prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var storageIds = _storage
            .Where(pair => pair.Value.Exists && TryParseStorageId(pair.Key, out var storageId) && prefix.Matches(storageId))
            .Select(pair => JournalStorageId.Parse(pair.Key))
            .OrderBy(static storageId => storageId.Value, StringComparer.Ordinal)
            .ToArray();

        foreach (var storageId in storageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return storageId;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<JournalStorageCreateResult> CreateIfNotExistsAsync(
        JournalStorageId storageId,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageId);
        ValidateCallerProperties(properties);
        cancellationToken.ThrowIfCancellationRequested();

        var store = _storage.GetOrAdd(storageId.Value, static key => new VolatileJournalStorage.Store(key));
        lock (store.SyncRoot)
        {
            if (store.Exists)
            {
                var current = store.GetProperties();
                var status = InitialPropertiesMatch(current.Values, properties)
                    ? JournalStorageCreateStatus.AlreadyExists
                    : JournalStorageCreateStatus.Conflict;
                return new(new JournalStorageCreateResult(status, current));
            }

            store.Create(properties);
            return new(new JournalStorageCreateResult(JournalStorageCreateStatus.Created, store.GetProperties()));
        }
    }

    public ValueTask<JournalStorageProperties?> GetPropertiesAsync(JournalStorageId storageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_storage.TryGetValue(storageId.Value, out var store))
        {
            return new((JournalStorageProperties?)null);
        }

        lock (store.SyncRoot)
        {
            return new(store.Exists ? store.GetProperties() : null);
        }
    }

    public ValueTask<JournalStoragePropertiesUpdateResult> UpdatePropertiesAsync(
        JournalStorageId storageId,
        JournalStoragePropertiesUpdate update,
        string? expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageId);
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_storage.TryGetValue(storageId.Value, out var store))
        {
            return new(new JournalStoragePropertiesUpdateResult(JournalStoragePropertiesUpdateStatus.NotFound, properties: null));
        }

        lock (store.SyncRoot)
        {
            if (!store.Exists)
            {
                return new(new JournalStoragePropertiesUpdateResult(JournalStoragePropertiesUpdateStatus.NotFound, properties: null));
            }

            var current = store.GetProperties();
            if (expectedETag is not null && !string.Equals(expectedETag, current.ETag, StringComparison.Ordinal))
            {
                return new(new JournalStoragePropertiesUpdateResult(JournalStoragePropertiesUpdateStatus.ETagMismatch, current));
            }

            if (!store.ApplyPropertiesUpdate(update))
            {
                return new(new JournalStoragePropertiesUpdateResult(JournalStoragePropertiesUpdateStatus.NoChange, current));
            }

            return new(new JournalStoragePropertiesUpdateResult(JournalStoragePropertiesUpdateStatus.Updated, store.GetProperties()));
        }
    }

    private string GetJournalFormatKey()
        => JournalFormatServices.ValidateJournalFormatKey(_options?.Value.JournalFormatKey ?? JsonJournalExtensions.JournalFormatKey);

    private static void ValidateCallerProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var (key, value) in properties)
        {
            JournalStorageProperties.ValidateCallerPropertyName(key);
            ArgumentNullException.ThrowIfNull(value);
        }
    }

    private static bool InitialPropertiesMatch(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return true;
        }

        foreach (var (key, value) in requested)
        {
            if (!current.TryGetValue(key, out var currentValue)
                || !string.Equals(currentValue, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseStorageId(string value, [NotNullWhen(true)] out JournalStorageId? storageId)
    {
        try
        {
            storageId = JournalStorageId.Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            storageId = null;
            return false;
        }
    }
}

/// <summary>
/// An in-memory, volatile implementation of <see cref="IJournalStorage"/> for non-durable use cases, such as development and testing.
/// </summary>
public sealed class VolatileJournalStorage : IJournalStorage
{
    private readonly Store _store;
    private string? _configuredJournalFormatKey;

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
    /// Initializes a new instance of the <see cref="VolatileJournalStorage"/> class.
    /// </summary>
    /// <param name="storageId">The journal storage id.</param>
    /// <param name="journalFormatKey">The journal format key to stamp on writes.</param>
    public VolatileJournalStorage(JournalStorageId storageId, string? journalFormatKey = null)
        : this(new Store((storageId ?? throw new ArgumentNullException(nameof(storageId))).Value), journalFormatKey)
    {
    }

    internal VolatileJournalStorage(Store store, string? journalFormatKey)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        SetConfiguredJournalFormatKey(journalFormatKey);
    }

    public bool IsCompactionRequested
    {
        get
        {
            lock (_store.SyncRoot)
            {
                return _store.Segments.Count > 10;
            }
        }
    }

    internal IReadOnlyList<byte[]> Segments => _store.Segments;

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
    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        byte[][] segments;
        IJournalFileMetadata metadata;
        lock (_store.SyncRoot)
        {
            metadata = _store.StoredJournalFormatKey is { } storedJournalFormatKey
                ? new JournalFileMetadata(storedJournalFormatKey)
                : JournalFileMetadata.Empty;
            segments = _store.Segments.ToArray();
        }

        consumer.Read(GetSegments(segments, cancellationToken), metadata, complete: true);
        return default;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> GetSegments(IEnumerable<byte[]> segments, CancellationToken cancellationToken)
    {
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return segment;
        }
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(ReadOnlySequence<byte> segment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            _store.Exists = true;
            _store.StoredJournalFormatKey = _configuredJournalFormatKey;
            _store.Segments.Add(segment.ToArray());
            _store.RefreshETag();
        }

        return default;
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAsync(ReadOnlySequence<byte> snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            _store.Exists = true;
            _store.StoredJournalFormatKey = _configuredJournalFormatKey;
            _store.Segments.Clear();
            _store.Segments.Add(snapshot.ToArray());
            _store.RefreshETag();
        }

        return default;
    }

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

        public List<byte[]> Segments { get; } = [];

        public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

        public string? StoredJournalFormatKey { get; set; }

        public bool Exists { get; set; }

        public long Version { get; private set; }

        public string? ETag { get; private set; }

        public void Create(IReadOnlyDictionary<string, string>? properties)
        {
            Exists = true;
            Segments.Clear();
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
            Segments.Clear();
            Properties.Clear();
            StoredJournalFormatKey = null;
            ETag = null;
            Version++;
        }

        public JournalStorageProperties GetProperties() => new(ETag, Properties);

        public bool ApplyPropertiesUpdate(JournalStoragePropertiesUpdate update)
        {
            var changed = false;
            foreach (var propertyName in update.Remove)
            {
                changed |= Properties.Remove(propertyName);
            }

            foreach (var (propertyName, value) in update.Set)
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
}
