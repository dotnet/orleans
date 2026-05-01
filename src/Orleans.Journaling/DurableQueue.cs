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
internal sealed class DurableQueue<T> : IDurableQueue<T>, IDurableStateMachine, IDurableQueueOperationHandler<T>
{
    private readonly IDurableQueueOperationCodec<T> _codec;
    private readonly Queue<T> _items = new();
    private LogWriter _storage;

    public DurableQueue(
        [ServiceKey] string key,
        ILogManager manager,
        [FromKeyedServices(LogFormatServices.LogFormatKeyServiceKey)] string logFormatKey,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = LogFormatServices.GetRequiredKeyedService<IDurableQueueOperationCodecProvider>(serviceProvider, logFormatKey).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableQueue(string key, ILogManager manager, IDurableQueueOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
    }

    public int Count => _items.Count;

    object IDurableStateMachine.OperationCodec => _codec;

    void IDurableStateMachine.Reset(LogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(LogWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(LogWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
    }

    public void Clear()
    {
        _codec.WriteClear(GetStorage());
        ApplyClear();
    }

    public T Peek() => _items.Peek();
    public bool TryPeek([MaybeNullWhen(false)] out T item) => _items.TryPeek(out item);
    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public void Enqueue(T item)
    {
        _codec.WriteEnqueue(item, GetStorage());
        ApplyEnqueue(item);
    }

    public T Dequeue()
    {
        var result = _items.Peek();
        _codec.WriteDequeue(GetStorage());
        _ = ApplyDequeue();
        return result;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        if (!_items.TryPeek(out item))
        {
            return false;
        }

        _codec.WriteDequeue(GetStorage());
        _ = ApplyTryDequeue(out _);
        return true;
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected void ApplyEnqueue(T item) => _items.Enqueue(item);
    protected T ApplyDequeue() => _items.Dequeue();
    protected bool ApplyTryDequeue([MaybeNullWhen(false)] out T value) => _items.TryDequeue(out value);
    protected void ApplyClear() => _items.Clear();
    void IDurableQueueOperationHandler<T>.ApplyEnqueue(T item) => ApplyEnqueue(item);
    void IDurableQueueOperationHandler<T>.ApplyDequeue() => _ = ApplyDequeue();
    void IDurableQueueOperationHandler<T>.ApplyClear() => ApplyClear();
    void IDurableQueueOperationHandler<T>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableQueueOperationHandler<T>.ApplySnapshotItem(T item) => ApplyEnqueue(item);

    private LogWriter GetStorage()
    {
        Debug.Assert(_storage.IsInitialized);
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
