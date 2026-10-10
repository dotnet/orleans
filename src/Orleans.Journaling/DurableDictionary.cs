using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Represents a dictionary whose mutations are recorded in a journal.
/// </summary>
/// <typeparam name="TKey">The type of keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of values in the dictionary.</typeparam>
public interface IDurableDictionary<TKey, TValue> : IDictionary<TKey, TValue> where TKey : notnull
{
}

[DebuggerTypeProxy(typeof(IDurableDictionaryDebugView<,>))]
[DebuggerDisplay("Count = {Count}")]
internal class DurableDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue>, IStateMachine, IDurableDictionaryCommandHandler<TKey, TValue>, IDisposable where TKey : notnull
{
    private readonly IDurableDictionaryCommandCodec<TKey, TValue> _codec;
    private readonly IDurableDictionaryValueLifecycle<TValue>? _valueLifecycle;
    private readonly Dictionary<TKey, TValue> _items = [];
    private JournalStreamWriter _writer;
    private bool _disposed;

    protected DurableDictionary(
        IDurableDictionaryCommandCodec<TKey, TValue> codec,
        IDurableDictionaryValueLifecycle<TValue>? valueLifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
        _valueLifecycle = valueLifecycle;
    }

    public DurableDictionary(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
        : this(
            JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TKey, TValue>>(serviceProvider, shared.JournalFormatKey),
            serviceProvider.GetService<IDurableDictionaryValueLifecycle<TValue>>())
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableDictionary(
        string key,
        IJournaledStateManager manager,
        IDurableDictionaryCommandCodec<TKey, TValue> codec,
        IDurableDictionaryValueLifecycle<TValue>? valueLifecycle = null) : this(codec, valueLifecycle)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    public TValue this[TKey key]
    {
        get => _items[key];

        set
        {
            SetValue(key, value);
        }
    }

    public int Count => _items.Count;

    public ICollection<TKey> Keys => _items.Keys;

    public ICollection<TValue> Values => _items.Values;

    public bool IsReadOnly => ((ICollection<KeyValuePair<TKey, TValue>>)_items).IsReadOnly;

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        ThrowIfDisposed();
        ApplyClear();
        _writer = writer;
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        // This state implementation appends journal entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
    }

    public void Clear()
    {
        _codec.WriteClear(GetWriter());
        ApplyClear();
    }

    public bool Contains(TKey key) => _items.ContainsKey(key);

    public bool Remove(TKey key)
    {
        if (!_items.ContainsKey(key))
        {
            return false;
        }

        WriteRemove(key);
        ApplyRemove(key);
        return true;
    }

    private void WriteRemove(TKey key)
    {
        _codec.WriteRemove(key, GetWriter());
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void WriteSet(TKey key, TValue value)
    {
        _codec.WriteSet(key, value, GetWriter());
    }

    private void SetValue(TKey key, TValue value)
    {
        ThrowIfDisposed();
        var owned = _valueLifecycle is { } lifecycle ? lifecycle.Retain(value) : value;
        try
        {
            WriteSet(key, owned);
        }
        catch
        {
            _valueLifecycle?.Release(owned);
            throw;
        }

        ApplySet(key, owned);
    }

    protected virtual void OnSet(TKey key, TValue value) { }

    private void ApplySet(TKey key, TValue value)
    {
        var replacing = _items.TryGetValue(key, out var previous);
        try
        {
            _items[key] = value;
        }
        catch
        {
            _valueLifecycle?.Release(value);
            throw;
        }

        if (replacing)
        {
            _valueLifecycle?.Release(previous!);
        }
        OnSet(key, value);
    }

    internal bool ApplyRemove(TKey key)
    {
        if (!_items.Remove(key, out var value))
        {
            return false;
        }

        _valueLifecycle?.Release(value);
        return true;
    }

    private void ApplyClear()
    {
        if (_valueLifecycle is { } lifecycle)
        {
            foreach (var value in _items.Values)
            {
                lifecycle.Release(value);
            }
        }

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

    protected virtual JournalStreamWriter GetWriter()
    {
        ThrowIfDisposed();
        Debug.Assert(_writer.IsInitialized);
        return _writer;
    }

    public void Add(TKey key, TValue value)
    {
        if (_items.ContainsKey(key))
        {
            ThrowDuplicateKey(key);
        }

        SetValue(key, value);
    }

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _items.TryGetValue(key, out value);
    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<TKey, TValue> item) => _items.Contains(item);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);
    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        if (!((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item))
        {
            return false;
        }

        WriteRemove(item.Key);
        ApplyRemove(item.Key);
        return true;
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => ((IEnumerable<KeyValuePair<TKey, TValue>>)_items).GetEnumerator();

    public void Dispose()
    {
        if (!_disposed && _valueLifecycle is not null)
        {
            _disposed = true;
            ApplyClear();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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
