using Orleans.Serialization.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

public sealed class VolatileStateMachineStorageProvider : IStateMachineStorageProvider
{
    private readonly ConcurrentDictionary<GrainId, VolatileStateMachineStorage> _storage = new();
    public IStateMachineStorage Create(IGrainContext grainContext) => _storage.GetOrAdd(grainContext.GrainId, _ => new VolatileStateMachineStorage());
}

/// <summary>
/// An in-memory, volatile implementation of <see cref="IStateMachineStorage"/> for non-durable use cases, such as development and testing.
/// </summary>
public sealed class VolatileStateMachineStorage : IStateMachineStorage
{
    private readonly List<byte[]> _segments = [];

    public bool IsCompactionRequested => _segments.Count > 10;

    /// <inheritdoc/>
    public async IAsyncEnumerable<LogExtent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        using var buffer = new ArcBufferWriter();
        foreach (var segment in _segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Write(segment);
            yield return new LogExtent(buffer.ConsumeSlice(segment.Length));
        }
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(LogExtentBuilder segment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Add(segment.ToArray());
        return default;
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAsync(LogExtentBuilder snapshot, CancellationToken cancellationToken)
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

