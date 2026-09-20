using System;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal class DeferredJournaledValue<T> : IDurableValue<T>, IStateMachine, IDurableValueCommandHandler<T>
{
    private readonly IDurableValueCommandCodec<T> _codec;
    private T? _value;

    public DeferredJournaledValue(IJournaledStateManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _codec = manager.GetRequiredCommandCodec<IDurableValueCommandCodec<T>>();
    }

    protected long MutationVersion { get; private set; }
    protected long CapturedVersion { get; private set; }
    protected long AcknowledgedVersion { get; private set; }
    protected bool HasPendingChanges => MutationVersion != CapturedVersion;
    public T? Value
    {
        get => _value;
        set
        {
            _value = value;
            MutationVersion++;
        }
    }

    public virtual void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    public virtual void Reset(JournalStreamWriter writer)
    {
        _value = default;
        MutationVersion = CapturedVersion = AcknowledgedVersion = 0;
    }

    public virtual void WritePendingEntries(JournalStreamWriter writer)
    {
        if (HasPendingChanges)
        {
            _codec.WriteSet(_value!, writer);
            CapturedVersion = MutationVersion;
        }
    }

    public virtual void WriteSnapshot(JournalStreamWriter writer)
    {
        _codec.WriteSet(_value!, writer);
        CapturedVersion = MutationVersion;
    }

    public virtual void OnWriteCompleted() => AcknowledgedVersion = CapturedVersion;
    public virtual void OnRecoveryCompleted() { }
    public virtual void ValidatePendingChanges() { }
    public virtual void ValidateWrite() { }
    public virtual void ValidateDelete() { }
    public virtual void OnDeleteStarted() { }
    public virtual void OnFaulted(Exception exception) { }
    void IDurableValueCommandHandler<T>.ApplySet(T value) => _value = value;
}
