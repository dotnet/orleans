using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using Orleans.Journaling;

namespace Orleans.Messaging.Tests.Support;

/// <summary>Stores journals in memory and records the bytes of every successful journal write.</summary>
public sealed class RecordingJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog
{
    private readonly ConcurrentDictionary<JournalId, ConcurrentQueue<byte[]>> _writes = new();
    private VolatileJournalStorageProvider? _inner;

    public void Configure(IOptions<JournaledStateManagerOptions> options) => _inner ??= new VolatileJournalStorageProvider(options);

    public IJournalStorage CreateStorage(JournalId journalId) =>
        new RecordingJournalStorage(_writes.GetOrAdd(journalId, static _ => new()), Inner.CreateStorage(journalId));

    public IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        JournalCatalogListOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Inner.ListAsync(options, cancellationToken);

    /// <summary>Gets the bytes of each successful write to a journal, in write order.</summary>
    public byte[][] GetWrites(JournalId journalId) => _writes.TryGetValue(journalId, out var writes) ? [.. writes] : [];

    /// <summary>
    /// Counts the occurrences of <paramref name="payload"/> across a journal's writes, as raw bytes in the Orleans binary
    /// format or as the base64 string which the JSON format writes for a byte array.
    /// </summary>
    public int CountPayloadOccurrences(JournalId journalId, byte[] payload)
    {
        var base64 = Encoding.UTF8.GetBytes(Convert.ToBase64String(payload));
        var count = 0;
        foreach (var write in GetWrites(journalId))
        {
            count += CountOccurrences(write, payload) + CountOccurrences(write, base64);
        }

        return count;
    }

    private static int CountOccurrences(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return 0;
        }

        var count = 0;
        for (var index = data.IndexOf(value); index >= 0; index = data.IndexOf(value))
        {
            count++;
            data = data[(index + value.Length)..];
        }

        return count;
    }

    private VolatileJournalStorageProvider Inner =>
        _inner ?? throw new InvalidOperationException("The recording journal storage provider has not been configured.");

    private sealed class RecordingJournalStorage(ConcurrentQueue<byte[]> writes, IJournalStorage inner) : IJournalStorage
    {
        public bool IsCompactionRequested => inner.IsCompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) =>
            inner.ReadAsync(consumer, cancellationToken);

        public ValueTask<bool> CreateIfNotExistsAsync(
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default) =>
            inner.CreateIfNotExistsAsync(metadata, cancellationToken);

        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.GetMetadataAsync(cancellationToken);

        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
            IReadOnlyDictionary<string, string>? set = null,
            IEnumerable<string>? remove = null,
            string? expectedETag = null,
            CancellationToken cancellationToken = default) =>
            inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

        public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            var bytes = value.ToArray();
            await inner.ReplaceAsync(value, cancellationToken).ConfigureAwait(false);
            writes.Enqueue(bytes);
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            var bytes = value.ToArray();
            await inner.AppendAsync(value, cancellationToken).ConfigureAwait(false);
            writes.Enqueue(bytes);
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
    }
}
