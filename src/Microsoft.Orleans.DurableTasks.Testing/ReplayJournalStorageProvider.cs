using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Testing;

/// <summary>
/// The replay silo's journal storage: in memory, seeded with each replayed history's journal, which it reads back in the
/// format which recorded it.
/// </summary>
/// <remarks>
/// A replay reads the history's journal, and the writes which it derives, such as version decisions, stay in memory until
/// the replayer removes the journal. Once a write replaces or extends a history's journal, the storage reports the silo's
/// write format, in which the journal manager writes. Other journals, such as Durable Jobs shards, use in-memory journal
/// storage.
/// </remarks>
internal sealed class ReplayJournalStorageProvider(IOptions<JournaledStateManagerOptions> options)
    : IJournalStorageProvider, IJournalStorageCatalog
{
    private readonly ConcurrentDictionary<string, Journal> _journals = new(StringComparer.Ordinal);
    private readonly VolatileJournalStorageProvider _other = new(options);
    private readonly string _writeFormatKey = options.Value.JournalFormatKey;

    /// <summary>Stores <paramref name="history"/>'s journal under its grain's journal.</summary>
    public void Seed(DurableTaskHistory history)
    {
        var journal = new Journal(_writeFormatKey);
        journal.Seed(history.JournalFormat, history.Journal.ToArray());
        _journals[JournalId.FromGrainId(history.GrainId).Value] = journal;
    }

    /// <summary>Removes the journal of the grain which <paramref name="history"/> holds.</summary>
    public void Remove(DurableTaskHistory history) => _journals.TryRemove(JournalId.FromGrainId(history.GrainId).Value, out _);

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return _journals.TryGetValue(journalId.Value, out var journal) ? journal : _other.CreateStorage(journalId);
    }

    public IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        JournalCatalogListOptions? options = null,
        CancellationToken cancellationToken = default) => _other.ListAsync(options, cancellationToken);

    private sealed class Journal(string writeFormatKey) : IJournalStorage
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _segments = [];
        private string? _formatKey;
        private bool _exists;

        public bool IsCompactionRequested => false;

        public void Seed(string formatKey, byte[] journal)
        {
            lock (_lock)
            {
                _segments.Clear();
                _segments.Add(journal);
                _formatKey = formatKey;
                _exists = true;
            }
        }

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            byte[][] segments;
            IJournalMetadata metadata;
            lock (_lock)
            {
                segments = [.. _segments];
                metadata = _exists ? new JournalMetadata(_formatKey) : JournalMetadata.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();
            consumer.Read(segments.Select(static segment => (ReadOnlyMemory<byte>)segment), metadata, complete: true);
            return default;
        }

        public ValueTask<bool> CreateIfNotExistsAsync(
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_exists)
                {
                    return new(false);
                }

                _exists = true;
                _formatKey = writeFormatKey;
                return new(true);
            }
        }

        public ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return new(_exists ? new JournalMetadata(_formatKey) : null);
            }
        }

        public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
            IReadOnlyDictionary<string, string>? set = null,
            IEnumerable<string>? remove = null,
            string? expectedETag = null,
            CancellationToken cancellationToken = default) => GetMetadataAsync(cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _segments.Clear();
                _segments.Add(value.ToArray());
                _formatKey = writeFormatKey;
                _exists = true;
            }

            return default;
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _segments.Add(value.ToArray());
                _formatKey ??= writeFormatKey;
                _exists = true;
            }

            return default;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _segments.Clear();
                _formatKey = null;
                _exists = false;
            }

            return default;
        }
    }
}
