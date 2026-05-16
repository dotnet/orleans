using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Orleans.Journaling.Json;

namespace Orleans.Journaling;

public sealed class VolatileJournalStorageProvider : IJournalStorageProvider
{
    private readonly IOptions<JournaledStateManagerOptions>? _options;
    private readonly ConcurrentDictionary<JournalId, VolatileJournalStorage> _storage = new();

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
        var storage = _storage.GetOrAdd(journalId, _ => new VolatileJournalStorage(journalFormatKey));
        storage.SetConfiguredJournalFormatKey(journalFormatKey);
        return storage;
    }

    private string GetJournalFormatKey()
        => JournalFormatServices.ValidateJournalFormatKey(_options?.Value.JournalFormatKey ?? JsonJournalExtensions.JournalFormatKey);
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
        => _storedJournalFormatKey;

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
        consumer.Read(GetSegments(_segments, cancellationToken), metadata, complete: true);
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
