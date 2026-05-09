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
    /// <param name="journalFormatKey">The default state machine journal format key.</param>
    public VolatileJournalStorageProvider(string journalFormatKey) : this(journalFormatKey, journalFormatKeySelector: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileJournalStorageProvider"/> class.
    /// </summary>
    /// <param name="journalFormatKey">The default state machine journal format key.</param>
    /// <param name="journalFormatKeySelector">An optional selector for choosing the journal format key by grain type.</param>
    public VolatileJournalStorageProvider(string journalFormatKey, Func<GrainType, string>? journalFormatKeySelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalFormatKey);
        _journalFormatKey = journalFormatKey;
        _journalFormatKeySelector = journalFormatKeySelector;
    }

    public IJournalStorage Create(IGrainContext grainContext)
        => _storage.GetOrAdd(grainContext.GrainId, _ => new VolatileJournalStorage());

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

    public VolatileJournalStorage()
    {
    }

    public bool IsCompactionRequested => _segments.Count > 10;

    internal IReadOnlyList<byte[]> Segments => _segments;

    /// <inheritdoc/>
    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        consumer.Consume(GetSegments(_segments, cancellationToken));
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
        _segments.Add(segment.ToArray());
        return default;
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAsync(ReadOnlySequence<byte> snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Clear();
        _segments.Add(snapshot.ToArray());
        return default;
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Clear();
        return default;
    }
}
