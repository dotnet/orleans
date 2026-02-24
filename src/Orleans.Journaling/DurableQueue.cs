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
internal sealed class DurableQueue<T> : IDurableQueue<T>, IDurableStateMachine
{
    private readonly ILogEntryCodec<DurableQueueEntry<T>> _entryCodec;
    private readonly Queue<T> _items = new();
    private IStateMachineLogWriter? _storage;

    public DurableQueue([ServiceKey] string key, IStateMachineManager manager, IDurableQueueCodecProvider codecProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _entryCodec = codecProvider.GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableQueue(string key, IStateMachineManager manager, ILogEntryCodec<DurableQueueEntry<T>> entryCodec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _entryCodec = entryCodec;
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
        var entry = _entryCodec.Read(logEntry);
        switch (entry)
        {
            case QueueEnqueueEntry<T>(var item):
                ApplyEnqueue(item);
                break;
            case QueueDequeueEntry<T>:
                _ = ApplyDequeue();
                break;
            case QueueClearEntry<T>:
                ApplyClear();
                break;
            case QueueSnapshotEntry<T>(var items):
                ApplyClear();
                _items.EnsureCapacity(items.Count);
                foreach (var item in items)
                {
                    ApplyEnqueue(item);
                }

                break;
        }
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        snapshotWriter.AppendEntry(static (self, bufferWriter) =>
        {
            self._entryCodec.Write(
                new QueueSnapshotEntry<T>(self._items.ToList()), bufferWriter);
        }, this);
    }

    public void Clear()
    {
        ApplyClear();
        GetStorage().AppendEntry(static (self, bufferWriter) =>
        {
            self._entryCodec.Write(new QueueClearEntry<T>(), bufferWriter);
        },
        this);
    }

    public T Peek() => _items.Peek();
    public bool TryPeek([MaybeNullWhen(false)] out T item) => _items.TryPeek(out item);
    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public void Enqueue(T item)
    {
        ApplyEnqueue(item);
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, value) = state;
            self._entryCodec.Write(new QueueEnqueueEntry<T>(value!), bufferWriter);
        },
        (this, item));
    }

    public T Dequeue()
    {
        var result = ApplyDequeue();
        GetStorage().AppendEntry(static (self, bufferWriter) =>
        {
            self._entryCodec.Write(new QueueDequeueEntry<T>(), bufferWriter);
        }, this);
        return result;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        if (ApplyTryDequeue(out item))
        {
            GetStorage().AppendEntry(static (self, bufferWriter) =>
            {
                self._entryCodec.Write(new QueueDequeueEntry<T>(), bufferWriter);
            }, this);
            return true;
        }

        return false;
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected void ApplyEnqueue(T item) => _items.Enqueue(item);
    protected T ApplyDequeue() => _items.Dequeue();
    protected bool ApplyTryDequeue([MaybeNullWhen(false)] out T value) => _items.TryDequeue(out value);
    protected void ApplyClear() => _items.Clear();

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
