using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableQueue<T> : IEnumerable<T>, IReadOnlyCollection<T>
{
    void Clear();
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    T Dequeue();
    void Enqueue(T item);
    T Peek();
    bool TryDequeue([MaybeNullWhen(false)] out T item);
    bool TryPeek([MaybeNullWhen(false)] out T item);
}

[DebuggerTypeProxy(typeof(DurableQueueDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableQueue<T> : IDurableQueue<T>, IDurableStateMachine, IDurableQueueLogEntryConsumer<T>
{
    private readonly IDurableQueueCodec<T> _codec;
    private readonly Queue<T> _items = new();
    private IStateMachineLogWriter? _storage;

    public DurableQueue([ServiceKey] string key, IStateMachineManager manager, IStateMachineStorage storage, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = StateMachineLogFormatServices.GetRequiredKeyedService<IDurableQueueCodecProvider>(serviceProvider, storage).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableQueue(string key, IStateMachineManager manager, IDurableQueueCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
    }

    public int Count => _items.Count;

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(StateMachineLogWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineLogWriter snapshotWriter)
    {
        using var entry = snapshotWriter.BeginEntry();
        _codec.WriteSnapshot(_items, entry.Writer);
        entry.Commit();
    }

    public void Clear()
    {
        using var entry = GetStorage().BeginEntry();
        _codec.WriteClear(entry.Writer);
        entry.Commit();

        ApplyClear();
    }

    public T Peek() => _items.Peek();
    public bool TryPeek([MaybeNullWhen(false)] out T item) => _items.TryPeek(out item);
    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public void Enqueue(T item)
    {
        using var entry = GetStorage().BeginEntry();
        _codec.WriteEnqueue(item, entry.Writer);
        entry.Commit();

        ApplyEnqueue(item);
    }

    public T Dequeue()
    {
        var result = _items.Peek();
        using var entry = GetStorage().BeginEntry();
        _codec.WriteDequeue(entry.Writer);
        entry.Commit();

        _ = ApplyDequeue();
        return result;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        if (!_items.TryPeek(out item))
        {
            return false;
        }

        using var entry = GetStorage().BeginEntry();
        _codec.WriteDequeue(entry.Writer);
        entry.Commit();

        _ = ApplyTryDequeue(out _);
        return true;
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected void ApplyEnqueue(T item) => _items.Enqueue(item);
    protected T ApplyDequeue() => _items.Dequeue();
    protected bool ApplyTryDequeue([MaybeNullWhen(false)] out T value) => _items.TryDequeue(out value);
    protected void ApplyClear() => _items.Clear();
    void IDurableQueueLogEntryConsumer<T>.ApplyEnqueue(T item) => ApplyEnqueue(item);
    void IDurableQueueLogEntryConsumer<T>.ApplyDequeue() => _ = ApplyDequeue();
    void IDurableQueueLogEntryConsumer<T>.ApplyClear() => ApplyClear();
    void IDurableQueueLogEntryConsumer<T>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableQueueLogEntryConsumer<T>.ApplySnapshotItem(T item) => ApplyEnqueue(item);

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
}

internal sealed class DurableQueueDebugView<T>
{
    private readonly DurableQueue<T> _queue;

    public DurableQueueDebugView(DurableQueue<T> queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        _queue = queue;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public T[] Items
    {
        get
        {
            return _queue.ToArray();
        }
    }
}
