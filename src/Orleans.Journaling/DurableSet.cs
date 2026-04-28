using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableSet<T> : ISet<T>, IReadOnlyCollection<T>, IReadOnlySet<T>
{
    new int Count { get; }
    new bool Contains(T item);
    new bool Add(T item);
    new bool IsProperSubsetOf(IEnumerable<T> other);
    new bool IsProperSupersetOf(IEnumerable<T> other);
    new bool IsSubsetOf(IEnumerable<T> other);
    new bool IsSupersetOf(IEnumerable<T> other);
    new bool Overlaps(IEnumerable<T> other);
    new bool SetEquals(IEnumerable<T> other);
}

[DebuggerTypeProxy(typeof(IDurableCollectionDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableSet<T> : IDurableSet<T>, IDurableStateMachine, IDurableSetLogEntryConsumer<T>
{
    private readonly IDurableSetCodec<T> _codec;
    private readonly HashSet<T> _items = [];
    private IStateMachineLogWriter? _storage;

    public DurableSet([ServiceKey] string key, IStateMachineManager manager, IDurableSetCodecProvider codecProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codecProvider.GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableSet(string key, IStateMachineManager manager, IDurableSetCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
    }

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        snapshotWriter.AppendEntry(WriteSnapshotToBufferWriter, this);
    }

    private static void WriteSnapshotToBufferWriter(DurableSet<T> self, IBufferWriter<byte> bufferWriter)
    {
        self._codec.WriteSnapshot(self._items, self._items.Count, bufferWriter);
    }

    public void Clear()
    {
        ApplyClear();
        GetStorage().AppendEntry(static (self, bufferWriter) =>
        {
            self._codec.WriteClear(bufferWriter);
        },
        this);
    }

    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public bool Add(T item)
    {
        if (ApplyAdd(item))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (self, item) = state;
                self._codec.WriteAdd(item!, bufferWriter);
            },
            (this, item));
            return true;
        }

        return false;
    }

    public bool Remove(T item)
    {
        if (ApplyRemove(item))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (self, item) = state;
                self._codec.WriteRemove(item!, bufferWriter);
            },
            (this, item));
            return true;
        }

        return false;
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected bool ApplyAdd(T item) => _items.Add(item);
    protected bool ApplyRemove(T item) => _items.Remove(item);
    protected void ApplyClear() => _items.Clear();
    void IDurableSetLogEntryConsumer<T>.ApplyAdd(T item) => ApplyAdd(item);
    void IDurableSetLogEntryConsumer<T>.ApplyRemove(T item) => ApplyRemove(item);
    void IDurableSetLogEntryConsumer<T>.ApplyClear() => ApplyClear();
    void IDurableSetLogEntryConsumer<T>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableSetLogEntryConsumer<T>.ApplySnapshotItem(T item) => ApplyAdd(item);

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
    public void ExceptWith(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            Remove(item);
        }
    }

    public bool IsProperSubsetOf(IEnumerable<T> other) => _items.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => _items.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => _items.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => _items.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => _items.Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => _items.SetEquals(other);
    void ICollection<T>.Add(T item) => Add(item);

    public void IntersectWith(IEnumerable<T> other)
    {
        var initialCount = Count;
        _items.IntersectWith(other);
        if (Count != initialCount)
        {
            GetStorage().AppendEntry(WriteSnapshotToBufferWriter, this);
        }
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        var initialCount = Count;
        _items.SymmetricExceptWith(other);
        if (Count != initialCount)
        {
            GetStorage().AppendEntry(WriteSnapshotToBufferWriter, this);
        }
    }

    public void UnionWith(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            Add(item);
        }
    }
}
