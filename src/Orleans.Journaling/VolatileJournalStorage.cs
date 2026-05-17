using System.Buffers;
using System.Collections.Concurrent;
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

    public async IAsyncEnumerable<JournalId> ListAsync(
        JournalId prefix = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<JournalId> journalIds = [];
        foreach (var (key, store) in _storage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseJournalId(key, out var journalId) || !prefix.IsPrefixOf(journalId))
            {
                continue;
            }

            lock (store.SyncRoot)
            {
                if (!store.Exists)
                {
                    continue;
                }
            }

            journalIds.Add(journalId);
        }

        journalIds.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));

        foreach (var journalId in journalIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return journalId;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string GetJournalFormatKey()
        => JournalFormatServices.ValidateJournalFormatKey(_options?.Value.JournalFormatKey ?? JsonJournalExtensions.JournalFormatKey);

    private static bool TryParseJournalId(string value, out JournalId journalId)
    {
        try
        {
            journalId = new JournalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            journalId = default;
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

    public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_store.SyncRoot)
        {
            return new(_store.Exists ? _store.GetMetadata() : null);
        }
    }

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

        byte[][] segments;
        IJournalMetadata metadata;
        lock (_store.SyncRoot)
        {
            metadata = _store.Exists ? _store.GetMetadata() : JournalMetadata.Empty;
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
