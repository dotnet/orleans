using System.Buffers;
using System.Collections;
using Orleans.Serialization.Buffers;
using System.Diagnostics;

namespace Orleans.Journaling;

/// <summary>
/// Represents a log segment which has been sealed and is no longer mutable.
/// </summary>
public sealed class LogExtent(ArcBuffer buffer) : IDisposable
{
    private ArcBuffer _buffer = buffer;

    public LogExtent() : this(new())
    {
    }

    public bool IsEmpty => _buffer.Length == 0;

    internal EntryEnumerator Entries => EntryEnumerator.Create(this);

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

            var reader = Reader.Create(_current, null);
            _length = (int)reader.ReadVarUInt32();
            _current = _current.Slice(reader.Position);
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
                var id = reader.ReadVarUInt32();
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
