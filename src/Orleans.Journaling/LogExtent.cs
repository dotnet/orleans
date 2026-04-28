using System.Buffers;
using System.Collections;
using System.Diagnostics;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Represents a log segment which has been sealed and is no longer mutable.
/// </summary>
public sealed class LogExtent(ArcBuffer buffer) : IDisposable
{
    private ArcBuffer _buffer = buffer;
    private readonly IReadOnlyList<Entry>? _entries;

    public LogExtent() : this(default(ArcBuffer))
    {
    }

    /// <summary>
    /// Initializes a log extent from decoded entries.
    /// </summary>
    /// <param name="entries">The decoded log entries.</param>
    public LogExtent(IReadOnlyList<Entry> entries) : this(default(ArcBuffer))
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
    }

    public bool IsEmpty => _entries is not null ? _entries.Count == 0 : _buffer.Length == 0;

    internal IEnumerable<Entry> Entries => _entries is not null ? _entries : EntryEnumerator.Create(this);

    public void Dispose() => _buffer.Dispose();

    public readonly record struct Entry(StateMachineId StreamId, ReadOnlySequence<byte> Payload);

    internal struct EntryEnumerator : IEnumerable<Entry>, IEnumerator<Entry>, IDisposable
    {
        private LogExtent _logExtent;
        private ReadOnlySequence<byte> _current;
        private int _length;

        private EntryEnumerator(LogExtent logExtent)
        {
            _logExtent = logExtent;
            _current = logExtent._buffer.AsReadOnlySequence();
            _length = -2;
        }

        public readonly EntryEnumerator GetEnumerator() => this;

        public static EntryEnumerator Create(LogExtent logSegment) => new(logSegment);

        public bool MoveNext()
        {
            if (_length == -1)
            {
                ThrowEnumerationNotStartedOrEnded();
            }

            if (_length >= 0)
            {
                // Advance the cursor.
                _current = _current.Slice(_length);
            }

            if (_current.Length == 0)
            {
                _length = -1;
                return false;
            }

            var reader = new SequenceReader<byte>(_current);
            if (!reader.TryReadLittleEndian(out _length))
            {
                throw new InvalidOperationException("Malformed binary log extent: missing entry length.");
            }

            if (_length < 0 || _length > reader.Remaining)
            {
                throw new InvalidOperationException("Malformed binary log extent: entry length exceeds remaining extent bytes.");
            }

            _current = _current.Slice(sizeof(int));
            return true;
        }

        public readonly Entry Current
        {
            get
            {
                if (_length < 0)
                {
                    ThrowEnumerationNotStartedOrEnded();
                }

                var slice = _current.Slice(0, _length);
                var reader = Reader.Create(slice, null);
                var id = reader.ReadVarUInt64();
                return new(new(id), slice.Slice(reader.Position));
            }
        }

        private readonly void ThrowEnumerationNotStartedOrEnded()
        {
            Debug.Assert(_length is (-1) or (-2));
            throw new InvalidOperationException(_length == -2 ? "Enumeration has not started." : "Enumeration has completed.");
        }

        readonly object? IEnumerator.Current => Current;

        public void Reset() => this = new(_logExtent);

        public void Dispose() => _length = -1;

        readonly IEnumerator<Entry> IEnumerable<Entry>.GetEnumerator() => GetEnumerator();
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
