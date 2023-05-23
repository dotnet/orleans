#nullable enable
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Orleans.Serialization.Buffers;

namespace Orleans.Runtime;

[GenerateSerializer, Immutable, Alias("MigrationCtx")]
internal sealed class MigrationContext : IDehydrationContext, IRehydrationContext, IDisposable, IEnumerable<string>, IBufferWriter<byte>
{
    private readonly object _lock = new ();

    [Id(0), Immutable]
    private readonly Dictionary<string, (int Offset, int Length)> _indices = new(StringComparer.Ordinal);

    [Id(1), Immutable]
    private PooledBuffer _buffer = new();

    public void Add(string key, ReadOnlySpan<byte> value)
    {
        lock (_lock)
        {
            _indices.Add(key, (_buffer.Length, value.Length));
            _buffer.Write(value);
        }
    }

    public void Add(string key, Action<object, IBufferWriter<byte>> valueWriter, object value)
    {
        lock (_lock)
        {
            var startOffset = _buffer.Length;
            valueWriter(value, this);
            var endOffset = _buffer.Length;
            _indices.Add(key, (startOffset, endOffset - startOffset));
        }
    }

    public IEnumerable<string> Keys => this;

    public void Dispose() => _buffer.Reset();

    public bool TryGetValue(string key, out ReadOnlySequence<byte> value)
    {
        if (_indices.TryGetValue(key, out var record))
        {
            value = _buffer.AsReadOnlySequence().Slice(record.Offset, record.Length);
            return true;
        }

        value = default;
        return false;
    }

    // Implemented on this class to prevent the need to repeatedly box & unbox _buffer.
    void IBufferWriter<byte>.Advance(int count) => _buffer.Advance(count);
    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint) => _buffer.GetMemory(sizeHint);
    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint) => _buffer.GetSpan(sizeHint);

    IEnumerator<string> IEnumerable<string>.GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    private sealed class Enumerator : IEnumerator<string>, IEnumerator
    {
        private Dictionary<string, (int Offset, int Length)>.KeyCollection.Enumerator _value;
        public Enumerator(MigrationContext context) => _value = context._indices.Keys.GetEnumerator();
        public string Current => _value.Current;
        object IEnumerator.Current => Current;
        public void Dispose() => _value.Dispose();
        public bool MoveNext() => _value.MoveNext();
        public void Reset()
        {
            var boxed = (IEnumerator)_value;
            boxed.Reset();
            _value = (Dictionary<string, (int Offset, int Length)>.KeyCollection.Enumerator)boxed;
        }
    }
}
