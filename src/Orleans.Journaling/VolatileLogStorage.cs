using System.Buffers;
using System.Collections.Concurrent;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

public sealed class VolatileLogStorageProvider : ILogStorageProvider, ILogFormatKeyProvider
{
    private readonly string _logFormatKey;
    private readonly Func<GrainType, string>? _logFormatKeySelector;
    private readonly ConcurrentDictionary<GrainId, VolatileLogStorage> _storage = new();

    public VolatileLogStorageProvider() : this(OrleansBinaryLogFormat.LogFormatKey)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileLogStorageProvider"/> class.
    /// </summary>
    /// <param name="logFormatKey">The default state machine log format key.</param>
    public VolatileLogStorageProvider(string logFormatKey) : this(logFormatKey, logFormatKeySelector: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileLogStorageProvider"/> class.
    /// </summary>
    /// <param name="logFormatKey">The default state machine log format key.</param>
    /// <param name="logFormatKeySelector">An optional selector for choosing the log format key by grain type.</param>
    public VolatileLogStorageProvider(string logFormatKey, Func<GrainType, string>? logFormatKeySelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFormatKey);
        _logFormatKey = logFormatKey;
        _logFormatKeySelector = logFormatKeySelector;
    }

    public ILogStorage Create(IGrainContext grainContext)
        => _storage.GetOrAdd(grainContext.GrainId, _ => new VolatileLogStorage());

    public string GetLogFormatKey(IGrainContext grainContext)
        => GetLogFormatKey(grainContext.GrainId.Type);

    private string GetLogFormatKey(GrainType grainType)
    {
        var result = _logFormatKeySelector?.Invoke(grainType) ?? _logFormatKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        return result;
    }
}

/// <summary>
/// An in-memory, volatile implementation of <see cref="ILogStorage"/> for non-durable use cases, such as development and testing.
/// </summary>
public sealed class VolatileLogStorage : ILogStorage
{
    private readonly List<byte[]> _segments = [];

    public VolatileLogStorage()
    {
    }

    public bool IsCompactionRequested => _segments.Count > 10;

    internal IReadOnlyList<byte[]> Segments => _segments;

    /// <inheritdoc/>
    public async ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var segment in _segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Write(segment);
            consume(new ArcBufferReader(buffer));
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

