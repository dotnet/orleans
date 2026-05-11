using System.Buffers;
using System.Collections.Concurrent;
using Orleans.Journaling.Json;

namespace Orleans.Journaling;

public sealed class VolatileJournalStorageProvider : IJournalStorageProvider, IJournalFormatKeyProvider
{
    private readonly string _journalFormatKey;
    private readonly Func<GrainType, string>? _journalFormatKeySelector;
    private readonly ConcurrentDictionary<GrainId, VolatileJournalStorage> _storage = new();

    public VolatileJournalStorageProvider() : this(JsonJournalExtensions.JournalFormatKey)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorageProvider"/> class.
    /// </summary>
    /// <param name="journalFormatKey">The default state journal format key.</param>
    public VolatileJournalStorageProvider(string journalFormatKey) : this(journalFormatKey, journalFormatKeySelector: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorageProvider"/> class.
    /// </summary>
    /// <param name="journalFormatKey">The default state journal format key.</param>
    /// <param name="journalFormatKeySelector">An optional selector for choosing the journal format key by grain type.</param>
    public VolatileJournalStorageProvider(string journalFormatKey, Func<GrainType, string>? journalFormatKeySelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalFormatKey);
        _journalFormatKey = journalFormatKey;
        _journalFormatKeySelector = journalFormatKeySelector;
    }

    public IJournalStorage Create(IGrainContext grainContext)
    {
        var journalFormatKey = GetJournalFormatKey(grainContext);
        var storage = _storage.GetOrAdd(grainContext.GrainId, _ => new VolatileJournalStorage(journalFormatKey));
        storage.SetConfiguredJournalFormatKey(journalFormatKey);
        return storage;
    }

    public string GetJournalFormatKey(IGrainContext grainContext)
        => GetJournalFormatKey(grainContext.GrainId.Type);

    private string GetJournalFormatKey(GrainType grainType)
    {
        var result = _journalFormatKeySelector?.Invoke(grainType) ?? _journalFormatKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        return result;
    }
}
/// <summary>
/// An in-memory, volatile implementation of <see cref="IJournalStorage"/> for non-durable use cases, such as development and testing.
/// </summary>
public sealed class VolatileJournalStorage : IJournalStorage
{
    private readonly List<byte[]> _segments = [];
    private string? _configuredJournalFormatKey;
    private string? _storedJournalFormatKey;

    public VolatileJournalStorage()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorage"/> class.
    /// </summary>
    /// <param name="journalFormatKey">The journal format key to stamp on writes.</param>
    public VolatileJournalStorage(string? journalFormatKey)
    {
        SetConfiguredJournalFormatKey(journalFormatKey);
    }

    public bool IsCompactionRequested => _segments.Count > 10;

    internal IReadOnlyList<byte[]> Segments => _segments;

    internal string? StoredJournalFormatKey
    {
        get => _storedJournalFormatKey;
        set => _storedJournalFormatKey = value;
    }

    internal void SetConfiguredJournalFormatKey(string? journalFormatKey)
    {
        _configuredJournalFormatKey = journalFormatKey;
    }

    /// <inheritdoc/>
    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        var metadata = _storedJournalFormatKey is null
            ? JournalFileMetadata.Empty
            : new JournalFileMetadata(_storedJournalFormatKey);
        consumer.Consume(GetSegments(_segments, cancellationToken), metadata);
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
        _storedJournalFormatKey = _configuredJournalFormatKey;
        _segments.Add(segment.ToArray());
        return default;
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAsync(ReadOnlySequence<byte> snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _storedJournalFormatKey = _configuredJournalFormatKey;
        _segments.Clear();
        _segments.Add(snapshot.ToArray());
        return default;
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Clear();
        _storedJournalFormatKey = null;
        return default;
    }
}
