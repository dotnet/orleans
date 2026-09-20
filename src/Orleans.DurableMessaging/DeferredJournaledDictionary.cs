using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal class DeferredJournaledDictionary<TKey, TValue> : IDurableDictionary<TKey, TValue>, IStateMachine, IDurableDictionaryCommandHandler<TKey, TValue>
    where TKey : notnull
{
    private readonly IDurableDictionaryCommandCodec<TKey, TValue> _codec;
    private readonly Dictionary<TKey, TValue> _items = [];
    private readonly List<Command> _pending = [];

    public DeferredJournaledDictionary(IJournaledStateManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _codec = manager.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<TKey, TValue>>();
    }

    protected long MutationVersion { get; private set; }
    protected long CapturedVersion { get; private set; }
    protected long AcknowledgedVersion { get; private set; }
    protected bool HasPendingChanges => MutationVersion != CapturedVersion;
    public int Count => _items.Count;
    public ICollection<TKey> Keys => _items.Keys;
    public ICollection<TValue> Values => _items.Values;
    public bool IsReadOnly => false;

    public TValue this[TKey key]
    {
        get => _items[key];
        set
        {
            _items[key] = value;
            Record(new(CommandKind.Set, key, value));
        }
    }

    public void Add(TKey key, TValue value)
    {
        _items.Add(key, value);
        Record(new(CommandKind.Set, key, value));
    }

    public bool Remove(TKey key)
    {
        if (!_items.Remove(key))
        {
            return false;
        }

        Record(new(CommandKind.Remove, key, default!));
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        Record(new(CommandKind.Clear, default!, default!));
    }

    private void Record(Command command)
    {
        _pending.Add(command);
        MutationVersion++;
    }

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _items.TryGetValue(key, out value);
    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);
    public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public virtual void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public virtual void Reset(JournalStreamWriter writer)
    {
        _items.Clear();
        _pending.Clear();
        MutationVersion = CapturedVersion = AcknowledgedVersion = 0;
    }

    public virtual void WritePendingEntries(JournalStreamWriter writer)
    {
        foreach (var command in _pending)
        {
            switch (command.Kind)
            {
                case CommandKind.Set:
                    _codec.WriteSet(command.Key, command.Value, writer);
                    break;
                case CommandKind.Remove:
                    _codec.WriteRemove(command.Key, writer);
                    break;
                case CommandKind.Clear:
                    _codec.WriteClear(writer);
                    break;
            }
        }

        CapturedVersion = MutationVersion;
        _pending.Clear();
    }

    public virtual void WriteSnapshot(JournalStreamWriter writer)
    {
        _codec.WriteSnapshot(_items, writer);
        CapturedVersion = MutationVersion;
        _pending.Clear();
    }

    public virtual void OnWriteCompleted() => AcknowledgedVersion = CapturedVersion;
    public virtual void OnRecoveryCompleted() { }
    public virtual void ValidatePendingChanges() { }
    public virtual void ValidateWrite() { }
    public virtual void ValidateDelete() { }
    public virtual void OnDeleteStarted() { }
    public virtual void OnFaulted(Exception exception) { }

    void IDurableDictionaryCommandHandler<TKey, TValue>.ApplySet(TKey key, TValue value) => _items[key] = value;
    void IDurableDictionaryCommandHandler<TKey, TValue>.ApplyRemove(TKey key) => _items.Remove(key);
    void IDurableDictionaryCommandHandler<TKey, TValue>.ApplyClear() => _items.Clear();
    void IDurableDictionaryCommandHandler<TKey, TValue>.Reset(int capacityHint)
    {
        _items.Clear();
        _items.EnsureCapacity(capacityHint);
    }

    private enum CommandKind : byte { Set, Remove, Clear }
    private readonly record struct Command(CommandKind Kind, TKey Key, TValue Value);
}
