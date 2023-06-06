#nullable enable
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Runtime;

[GenerateSerializer, Immutable, Alias("MigrationCtx"), SerializationCallbacks(typeof(SerializationHooks))]
internal sealed class MigrationContext : IDehydrationContext, IRehydrationContext, IDisposable, IEnumerable<string>, IBufferWriter<byte>
{
    [NonSerialized]
    private readonly object _lock = new();

    [NonSerialized]
    internal SerializerSessionPool _sessionPool;

    [OrleansConstructor]
    public MigrationContext(SerializerSessionPool sessionPool)
    {
        _sessionPool = sessionPool;
    }

    [Id(0), Immutable]
    private readonly Dictionary<string, (int Offset, int Length)> _indices = new(StringComparer.Ordinal);

    [Id(1), Immutable]
    private PooledBuffer _buffer = new();

    public void AddBytes(string key, ReadOnlySpan<byte> value)
    {
        lock (_lock)
        {
            _indices.Add(key, (_buffer.Length, value.Length));
            _buffer.Write(value);
        }
    }

    public void AddBytes<T>(string key, Action<T, IBufferWriter<byte>> valueWriter, T value)
    {
        lock (_lock)
        {
            var startOffset = _buffer.Length;
            valueWriter(value, this);
            var endOffset = _buffer.Length;
            _indices.Add(key, (startOffset, endOffset - startOffset));
        }
    }

    public bool TryAddValue<T>(string key, T? value)
    {
        if (_sessionPool.CodecProvider.TryGetCodec<T>() is { } codec)
        {
            lock (_lock)
            {
                ref var indexValue = ref CollectionsMarshal.GetValueRefOrAddDefault(_indices, key, out var exists);
                if (!exists)
                {
                    var startOffset = _buffer.Length;

                    using var session = _sessionPool.GetSession();
                    var writer = Writer.Create(this, session);
                    codec.WriteField(ref writer, 0, typeof(T), value!);
                    writer.Commit();

                    var endOffset = _buffer.Length;
                    indexValue = (Offset: startOffset, Length: endOffset - startOffset);
                    return true;
                }
            }
        }

        return false;
    }

    public IEnumerable<string> Keys => this;

    public void Dispose() => _buffer.Reset();

    public bool TryGetBytes(string key, out ReadOnlySequence<byte> value)
    {
        if (_indices.TryGetValue(key, out var record))
        {
            value = _buffer.AsReadOnlySequence().Slice(record.Offset, record.Length);
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetValue<T>(string key, out T? value)
    {
        if (_indices.TryGetValue(key, out var record) && _sessionPool.CodecProvider.TryGetCodec<T>() is { } codec)
        {
            using var session = _sessionPool.GetSession();
            var source = _buffer.Slice(record.Offset, record.Length);
            var reader = Reader.Create(source, session);
            var field = reader.ReadFieldHeader();
            value = codec.ReadValue(ref reader, field);
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

    internal sealed class SerializationHooks
    {
        private readonly SerializerSessionPool _serializerSessionPool;

        public SerializationHooks(SerializerSessionPool serializerSessionPool)
        {
            _serializerSessionPool = serializerSessionPool;
        }

        public void OnDeserializing(MigrationContext context)
        {
            context._sessionPool = _serializerSessionPool;
        }
    }
}
