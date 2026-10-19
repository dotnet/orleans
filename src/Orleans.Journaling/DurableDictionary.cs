using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.Journaling;

/// <summary>
/// Represents a dictionary whose mutations are recorded in a journal.
/// </summary>
/// <typeparam name="TKey">The type of keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of values in the dictionary.</typeparam>
/// <remarks>
/// The dictionary takes ownership of the keys and values passed to it, and the journal records each mutation when it is made. To change a value, assign a new instance.
/// </remarks>
public interface IDurableDictionary<TKey, TValue> : IDictionary<TKey, TValue> where TKey : notnull
{
}

[DebuggerTypeProxy(typeof(IDurableDictionaryDebugView<,>))]
[DebuggerDisplay("Count = {Count}")]
internal class DurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue>, IStateMachine, IReportsStagedChanges, IDurableDictionaryCommandHandler<TKey, TValue>,
    IResyncSource<DictionaryChange<TKey, TValue>>, IJournaledStateHistory, IJournaledStateReplica where TKey : notnull
{
    private readonly IDurableDictionaryCommandCodec<TKey, TValue> _codec;
    private readonly Dictionary<TKey, TValue> _items = [];
    private readonly JournaledStateManager? _manager;
    // Null for state which a host owns, such as the manager's own bookkeeping, whose accesses always run directly.
    private readonly JournaledStateAccessTarget? _accessTarget;
    private JournalStreamWriter _writer;
    private bool _hasStagedEntries;

    protected DurableDictionary(IDurableDictionaryCommandCodec<TKey, TValue> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    public DurableDictionary(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
        : this(key, manager, JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TKey, TValue>>(serviceProvider, shared.JournalFormatKey), routed: true)
    {
    }

    internal DurableDictionary(string key, IJournaledStateManager manager, IDurableDictionaryCommandCodec<TKey, TValue> codec)
        : this(key, manager, codec, routed: true)
    {
    }

    private DurableDictionary(string key, IJournaledStateManager manager, IDurableDictionaryCommandCodec<TKey, TValue> codec, bool routed) : this(codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _manager = manager as JournaledStateManager;
        _accessTarget = routed ? new(key, manager, this) : null;
        manager.RegisterStateMachine(key, this);
    }

    /// <summary>
    /// Creates a dictionary which a host owns, such as the durable task runtime's bookkeeping, whose accesses always run
    /// directly: no host's context records them, whichever code accesses it.
    /// </summary>
    /// <param name="key">The state's name.</param>
    /// <param name="services">The activation's services, which supply its journaled state manager and the journal format's codec.</param>
    internal static DurableDictionary<TKey, TValue> CreateHostState(string key, IServiceProvider services) =>
        new(
            key,
            services.GetRequiredService<IJournaledStateManager>(),
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TKey, TValue>>(
                services,
                services.GetRequiredService<JournaledStateManagerShared>().JournalFormatKey),
            routed: false);

    public TValue this[TKey key]
    {
        get => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
            ? context.Read(_accessTarget, JournaledStateOperation.DictionaryGet, key, (Self: this, Key: key), static state => state.Self._items[state.Key])
            : _items[key];

        set
        {
            if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
            {
                context.Write(_accessTarget, JournaledStateOperation.DictionarySet, (key, value), (Self: this, Key: key, Value: value), static state => state.Self.SetCore(state.Key, state.Value));
                return;
            }

            SetCore(key, value);
        }
    }

    public int Count => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.ReadWhole(_accessTarget, JournaledStateOperation.DictionaryCount, this, static self => self._items.Count)
        : _items.Count;

    public ICollection<TKey> Keys => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.ReadWhole(_accessTarget, JournaledStateOperation.DictionaryKeys, this, static self => self._items.Keys.ToArray())
        : _items.Keys;

    public ICollection<TValue> Values => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.ReadWhole(_accessTarget, JournaledStateOperation.DictionaryValues, this, static self => self._items.Values.ToArray())
        : _items.Values;

    public bool IsReadOnly => ((ICollection<KeyValuePair<TKey, TValue>>)_items).IsReadOnly;

    private void SetCore(TKey key, TValue value)
    {
        WriteSet(key, value);
        ApplySet(key, value);
        PublishSet(key, value);
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        ApplyClear();
        _writer = writer;
        _hasStagedEntries = false;
        DurableStateObservation.PublishDurableResync<DurableDictionary<TKey, TValue>, DictionaryChange<TKey, TValue>>(_manager, this);
    }

    void IStateMachine.OnRecoveryCompleted() => DurableStateObservation.PublishDurableResync<DurableDictionary<TKey, TValue>, DictionaryChange<TKey, TValue>>(_manager, this);

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        // This state implementation appends journal entries as the data structure is modified, so there is no need to perform separate writing here.
        // The write being captured contains every staged entry.
        _hasStagedEntries = false;
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
        _hasStagedEntries = false;
    }

    public void Clear()
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.DictionaryClear, this, static self => self.ClearCore());
            return;
        }

        ClearCore();
    }

    private void ClearCore()
    {
        _codec.WriteClear(GetWriter());
        _hasStagedEntries = true;
        ApplyClear();
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishStaged(new DictionaryChange<TKey, TValue> { Kind = DictionaryChangeKind.Cleared });
        }
    }

    public bool Contains(TKey key) => ContainsKey(key);

    public bool Remove(TKey key) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.DictionaryRemove, key, (Self: this, Key: key), static state => state.Self.RemoveCore(state.Key))
        : RemoveCore(key);

    private bool RemoveCore(TKey key)
    {
        if (!_items.ContainsKey(key))
        {
            return false;
        }

        WriteRemove(key);
        ApplyRemove(key);
        PublishRemove(key);
        return true;
    }

    private void WriteRemove(TKey key)
    {
        _codec.WriteRemove(key, GetWriter());
        _hasStagedEntries = true;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void WriteSet(TKey key, TValue value)
    {
        _codec.WriteSet(key, value, GetWriter());
        _hasStagedEntries = true;
    }

    protected virtual void OnSet(TKey key, TValue value) { }

    private void ApplySet(TKey key, TValue value)
    {
        OnChanged();
        _items[key] = value;
        OnSet(key, value);
    }

    internal bool ApplyRemove(TKey key)
    {
        if (!_items.Remove(key))
        {
            return false;
        }

        OnChanged();
        return true;
    }

    private void ApplyClear()
    {
        OnChanged();
        _items.Clear();
    }

    void IDurableDictionaryCommandHandler<TKey, TValue>.ApplySet(TKey key, TValue value) => ApplySet(key, value);
    void IDurableDictionaryCommandHandler<TKey, TValue>.ApplyRemove(TKey key) => ApplyRemove(key);
    void IDurableDictionaryCommandHandler<TKey, TValue>.ApplyClear() => ApplyClear();
    void IDurableDictionaryCommandHandler<TKey, TValue>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
    }

    // State which a host owns, such as the manager's bookkeeping or the durable task runtime's, has no target by
    // construction: no context reads it, so no stamp records its changes.
    private void OnChanged() => _accessTarget?.OnChanged();

    protected virtual JournalStreamWriter GetWriter()
    {
        Debug.Assert(_writer.IsInitialized);
        return _writer;
    }

    public void Add(TKey key, TValue value)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.DictionaryAdd, (key, value), (Self: this, Key: key, Value: value), static state => state.Self.AddCore(state.Key, state.Value));
            return;
        }

        AddCore(key, value);
    }

    private void AddCore(TKey key, TValue value)
    {
        if (_items.ContainsKey(key))
        {
            ThrowDuplicateKey(key);
        }

        WriteSet(key, value);
        ApplySet(key, value);
        PublishSet(key, value);
    }

    public bool ContainsKey(TKey key) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.DictionaryContainsKey, key, (Self: this, Key: key), static state => state.Self._items.ContainsKey(state.Key))
        : _items.ContainsKey(key);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            (var found, value) = context.Read(
                _accessTarget,
                JournaledStateOperation.DictionaryTryGetValue,
                key,
                (Self: this, Key: key),
                static state => state.Self._items.TryGetValue(state.Key, out var value) ? (true, value) : (false, default(TValue)!));
            return found;
        }

        return _items.TryGetValue(key, out value);
    }

    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public bool Contains(KeyValuePair<TKey, TValue> item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.DictionaryContainsPair, (item.Key, item.Value), (Self: this, Item: item), static state => ((ICollection<KeyValuePair<TKey, TValue>>)state.Self._items).Contains(state.Item))
        : ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            ReadSnapshot(context).CopyTo(array, arrayIndex);
            return;
        }

        ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);
    }

    public bool Remove(KeyValuePair<TKey, TValue> item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.DictionaryRemovePair, (item.Key, item.Value), (Self: this, Item: item), static state => state.Self.RemoveCore(state.Item))
        : RemoveCore(item);

    private bool RemoveCore(KeyValuePair<TKey, TValue> item)
    {
        if (!((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item))
        {
            return false;
        }

        WriteRemove(item.Key);
        _ = ApplyRemove(item.Key);
        PublishRemove(item.Key);
        return true;
    }

    // A context records the entries which the code enumerates, so a replay enumerates the same entries.
    private KeyValuePair<TKey, TValue>[] ReadSnapshot(JournaledStateContext context) =>
        context.ReadWhole(_accessTarget!, JournaledStateOperation.DictionarySnapshot, this, static self => self._items.ToArray());

    internal IAsyncEnumerable<DictionaryChange<TKey, TValue>> WatchAsync(DurableWatchMode mode, int capacity, CancellationToken cancellationToken) =>
        DurableStateObservation.WatchChanges<DurableDictionary<TKey, TValue>, DictionaryChange<TKey, TValue>>(_manager, this, mode, capacity, cancellationToken);

    bool IResyncSource<DictionaryChange<TKey, TValue>>.HasStagedEntries => _hasStagedEntries;

    DictionaryChange<TKey, TValue> IResyncSource<DictionaryChange<TKey, TValue>>.CreateResync() =>
        new() { Kind = DictionaryChangeKind.Resync, Snapshot = _items.ToArray() };

    private void PublishSet(TKey key, TValue value)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishStaged(new DictionaryChange<TKey, TValue> { Kind = DictionaryChangeKind.Set, Key = key, Value = value });
        }
    }

    private void PublishRemove(TKey key)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishStaged(new DictionaryChange<TKey, TValue> { Kind = DictionaryChangeKind.Removed, Key = key });
        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? ((IEnumerable<KeyValuePair<TKey, TValue>>)ReadSnapshot(context)).GetEnumerator()
        : ((IEnumerable<KeyValuePair<TKey, TValue>>)_items).GetEnumerator();

    // The collection journals each change as it makes it.
    void IJournaledStateHistory.StagePendingChanges() { }

    IJournaledStateReplica IJournaledStateHistory.CreateReplica() => new DurableDictionary<TKey, TValue>(_codec);

    void IJournaledStateReplica.Evaluate(JournaledStateOperation operation, ReadOnlyMemory<byte> argument, Serializer serializer, IBufferWriter<byte> result)
    {
        switch (operation)
        {
            case JournaledStateOperation.DictionaryGet:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items[JournaledStateReplicaEncoding.ReadArgument<TKey>(serializer, argument)], result);
                break;
            case JournaledStateOperation.DictionaryTryGetValue:
                JournaledStateReplicaEncoding.WriteResult(
                    serializer,
                    _items.TryGetValue(JournaledStateReplicaEncoding.ReadArgument<TKey>(serializer, argument), out var value) ? (true, value) : (false, default(TValue)!),
                    result);
                break;
            case JournaledStateOperation.DictionaryContainsKey:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items.ContainsKey(JournaledStateReplicaEncoding.ReadArgument<TKey>(serializer, argument)), result);
                break;
            case JournaledStateOperation.DictionaryContainsPair:
                JournaledStateReplicaEncoding.WriteResult(
                    serializer,
                    ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(JournaledStateReplicaEncoding.ReadArgument<KeyValuePair<TKey, TValue>>(serializer, argument)),
                    result);
                break;
            case JournaledStateOperation.DictionaryCount:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items.Count, result);
                break;
            default:
                throw JournaledStateReplicaEncoding.NotPositional(operation, nameof(DurableDictionary<TKey, TValue>));
        }
    }

    [DoesNotReturn]
    private static void ThrowDuplicateKey(TKey key) => throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
}

[DebuggerDisplay("{Value}", Name = "[{Key}]")]
internal readonly struct DebugViewDictionaryItem<TKey, TValue>
{
    public DebugViewDictionaryItem(TKey key, TValue value)
    {
        Key = key;
        Value = value;
    }

    public DebugViewDictionaryItem(KeyValuePair<TKey, TValue> keyValue)
    {
        Key = keyValue.Key;
        Value = keyValue.Value;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    public TKey Key { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    public TValue Value { get; }
}

internal sealed class IDurableDictionaryDebugView<TKey, TValue> where TKey : notnull
{
    private readonly IDurableDictionary<TKey, TValue> _dict;

    public IDurableDictionaryDebugView(IDurableDictionary<TKey, TValue> dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        _dict = dictionary;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public DebugViewDictionaryItem<TKey, TValue>[] Items
    {
        get
        {
            var keyValuePairs = new KeyValuePair<TKey, TValue>[_dict.Count];
            _dict.CopyTo(keyValuePairs, 0);
            var items = new DebugViewDictionaryItem<TKey, TValue>[keyValuePairs.Length];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new DebugViewDictionaryItem<TKey, TValue>(keyValuePairs[i]);
            }
            return items;
        }
    }
}
