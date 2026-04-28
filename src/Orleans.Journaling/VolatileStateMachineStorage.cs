using Orleans.Serialization.Buffers;
using System.Collections.Concurrent;

namespace Orleans.Journaling;

public sealed class VolatileStateMachineStorageProvider : IStateMachineStorageProvider
{
    private readonly IStateMachineLogExtentCodec _codec;
    private readonly ConcurrentDictionary<GrainId, VolatileStateMachineStorage> _storage = new();

    public VolatileStateMachineStorageProvider() : this(BinaryLogExtentCodec.Instance)
    {
    }

    public VolatileStateMachineStorageProvider(IStateMachineLogExtentCodec codec)
    {
        _codec = codec;
    }

    public IStateMachineStorage Create(IGrainContext grainContext) => _storage.GetOrAdd(grainContext.GrainId, _ => new VolatileStateMachineStorage(_codec));
}

/// <summary>
/// An in-memory, volatile implementation of <see cref="IStateMachineStorage"/> for non-durable use cases, such as development and testing.
/// </summary>
public sealed class VolatileStateMachineStorage : IStateMachineStorage
{
    private readonly List<byte[]> _segments = [];
    private readonly IStateMachineLogExtentCodec _codec;

    public VolatileStateMachineStorage() : this(BinaryLogExtentCodec.Instance)
    {
    }

    public VolatileStateMachineStorage(IStateMachineLogExtentCodec codec)
    {
        _codec = codec;
    }

    public bool IsCompactionRequested => _segments.Count > 10;

    internal IReadOnlyList<byte[]> Segments => _segments;

    /// <inheritdoc/>
    public async ValueTask ReadAsync(IStateMachineLogEntryConsumer consumer, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        using var buffer = new ArcBufferWriter();
        foreach (var segment in _segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Write(segment);
            _codec.Read(buffer.ConsumeSlice(segment.Length), consumer);
        }
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(LogExtentBuilder segment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Add(Encode(segment));
        return default;
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAsync(LogExtentBuilder snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Clear();
        _segments.Add(Encode(snapshot));
        return default;
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Clear();
        return default;
    }

    private byte[] Encode(LogExtentBuilder segment)
    {
        using var stream = _codec.EncodeToStream(segment);
        using var output = stream.CanSeek ? new MemoryStream((int)stream.Length) : new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}

