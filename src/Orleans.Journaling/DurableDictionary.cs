using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableDictionary<K, V> : IDictionary<K, V> where K : notnull
{
}

[DebuggerTypeProxy(typeof(IDurableDictionaryDebugView<,>))]
[DebuggerDisplay("Count = {Count}")]
internal class DurableDictionary<K, V> : IDurableDictionary<K, V>, IDurableStateMachine where K : notnull
{
    private readonly ILogEntryCodec<DurableDictionaryEntry<K, V>> _entryCodec;
    private readonly Dictionary<K, V> _items = [];
    private IStateMachineLogWriter? _storage;

    protected DurableDictionary(ILogEntryCodec<DurableDictionaryEntry<K, V>> entryCodec)
    {
        _entryCodec = entryCodec;
    }

    public DurableDictionary([ServiceKey] string key, IStateMachineManager manager, ILogEntryCodec<DurableDictionaryEntry<K, V>> entryCodec) : this(entryCodec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    public V this[K key]
    {
        get => _items[key];

        set
        {
            ApplySet(key, value);
            AppendSet(key, value);
        }
    }

    public int Count => _items.Count;

    public ICollection<K> Keys => _items.Keys;

    public ICollection<V> Values => _items.Values;

    public bool IsReadOnly => ((ICollection<KeyValuePair<K, V>>)_items).IsReadOnly;

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
            case DictionarySetEntry<K, V>(var key, var value):
                ApplySet(key, value);
                break;
            case DictionaryRemoveEntry<K, V>(var key):
                ApplyRemove(key);
                break;
            case DictionaryClearEntry<K, V>:
                ApplyClear();
                break;
            case DictionarySnapshotEntry<K, V>(var items):
                _items.Clear();
                _items.EnsureCapacity(items.Count);
                foreach (var kv in items)
                {
                    ApplySet(kv.Key, kv.Value);
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
                new DictionarySnapshotEntry<K, V>(self._items.ToList()), bufferWriter);
        }, this);
    }

    public void Clear()
    {
        ApplyClear();
        GetStorage().AppendEntry(static (self, bufferWriter) =>
        {
            self._entryCodec.Write(new DictionaryClearEntry<K, V>(), bufferWriter);
        },
        this);
    }

    public bool Contains(K key) => _items.ContainsKey(key);

    public bool Remove(K key)
    {
        if (ApplyRemove(key))
        {
            AppendRemove(key);
            return true;
        }

        return false;
    }

    private void AppendRemove(K key)
    {
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, key) = state;
            self._entryCodec.Write(new DictionaryRemoveEntry<K, V>(key), bufferWriter);
        }, (this, key));
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void AppendSet(K key, V value)
    {
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, key, value) = state;
            self._entryCodec.Write(new DictionarySetEntry<K, V>(key, value), bufferWriter);
        },
        (this, key, value));
    }

    protected virtual void OnSet(K key, V value) { }

    private void ApplySet(K key, V value)
    {
        _items[key] = value;
        OnSet(key, value);
    }

    internal bool ApplyRemove(K key) => _items.Remove(key);
    private void ApplyClear() => _items.Clear();

    protected virtual IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
    public void Add(K key, V value)
    {
        _items.Add(key, value);
        OnSet(key, value);
        AppendSet(key, value);
    }

    public bool ContainsKey(K key) => _items.ContainsKey(key);
    public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value) => _items.TryGetValue(key, out value);
    public void Add(KeyValuePair<K, V> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<K, V> item) => _items.Contains(item);
    public void CopyTo(KeyValuePair<K, V>[] array, int arrayIndex) => ((ICollection<KeyValuePair<K, V>>)_items).CopyTo(array, arrayIndex);
    public bool Remove(KeyValuePair<K, V> item)
    {
        if (((ICollection<KeyValuePair<K, V>>)_items).Remove(item))
        {
            AppendRemove(item.Key);
            return true;
        }

        return false;
    }

    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => ((IEnumerable<KeyValuePair<K, V>>)_items).GetEnumerator();
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
