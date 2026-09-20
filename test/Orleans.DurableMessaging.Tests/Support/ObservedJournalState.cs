using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Orleans.Journaling;

namespace Orleans.DurableMessaging.Tests.Support;

internal abstract class ObservedJournalState(IStateMachine state) : IStateMachine
{
    private bool _initialized;
    protected IStateMachine State { get; } = state;
    public Action? Initializing { get; set; }
    public Action? Resetting { get; set; }
    public Action? Recovered { get; set; }
    public Action? Capturing { get; set; }
    public Action? Written { get; set; }
    public Action? ValidateWriting { get; set; }
    public Action? ValidateDeleting { get; set; }
    public Action<Exception>? Faulted { get; set; }
    public virtual void ValidatePendingChanges() => State.ValidatePendingChanges();
    public virtual void ValidateWrite() { State.ValidateWrite(); ValidateWriting?.Invoke(); }
    public virtual void ValidateDelete() { State.ValidateDelete(); ValidateDeleting?.Invoke(); }
    public virtual void OnDeleteStarted() => State.OnDeleteStarted();
    public virtual void OnFaulted(Exception exception) { State.OnFaulted(exception); Faulted?.Invoke(exception); }
    public virtual void ReplayEntry(JournalEntry entry, JournalReplayContext context) => State.ReplayEntry(entry, context);
    public virtual void Reset(JournalStreamWriter writer)
    {
        State.Reset(writer);
        Resetting?.Invoke();
        if (!_initialized)
        {
            _initialized = true;
            Initializing?.Invoke();
        }
    }
    public virtual void OnRecoveryCompleted() { State.OnRecoveryCompleted(); Recovered?.Invoke(); }
    public virtual void WritePendingEntries(JournalStreamWriter writer) { Capturing?.Invoke(); State.WritePendingEntries(writer); }
    public virtual void WriteSnapshot(JournalStreamWriter writer) { Capturing?.Invoke(); State.WriteSnapshot(writer); }
    public virtual void OnWriteCompleted() { State.OnWriteCompleted(); Written?.Invoke(); }
}

internal class ObservedJournalDictionary<TKey, TValue> : ObservedJournalState, IDurableDictionary<TKey, TValue> where TKey : notnull
{
    private IDurableDictionary<TKey, TValue> Items => (IDurableDictionary<TKey, TValue>)State;
    public ObservedJournalDictionary(IJournaledStateManager manager, bool deferred = false)
        : base(deferred ? ReceiverTestServices.CreateDeferredDictionary<TKey, TValue>(manager) : ReceiverTestServices.CreateStandardDictionary<TKey, TValue>(manager)) { }
    public TValue this[TKey key] { get => Items[key]; set => Items[key] = value; }
    public int Count => Items.Count;
    public ICollection<TKey> Keys => Items.Keys;
    public ICollection<TValue> Values => Items.Values;
    public bool IsReadOnly => false;
    public void Add(TKey key, TValue value) => Items.Add(key, value);
    public bool Remove(TKey key) => Items.Remove(key);
    public void Clear() => Items.Clear();
    public bool ContainsKey(TKey key) => Items.ContainsKey(key);
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => Items.TryGetValue(key, out value);
    public void Add(KeyValuePair<TKey, TValue> item) => Items.Add(item);
    public bool Contains(KeyValuePair<TKey, TValue> item) => Items.Contains(item);
    public bool Remove(KeyValuePair<TKey, TValue> item) => Items.Remove(item);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index) => Items.CopyTo(array, index);
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class ObservedJournalValue<T> : ObservedJournalState, IDurableValue<T>
{
    public ObservedJournalValue(IJournaledStateManager manager)
        : base(ReceiverTestServices.CreateDeferredValue<T>(manager)) { }
    public T? Value { get => ((IDurableValue<T>)State).Value; set => ((IDurableValue<T>)State).Value = value; }
}
