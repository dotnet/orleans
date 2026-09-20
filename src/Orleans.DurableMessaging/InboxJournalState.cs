using System;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal sealed class InboxJournalState(IJournaledStateManager manager)
    : DeferredJournaledDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope>(manager)
{
    private DurableInboxExtension? _owner;
    private bool _recovered;
    private bool _captured;
    private DurableInboxExtension Owner => _owner ?? throw new InvalidOperationException("Durable inbox runtime must be attached before journal recovery. Select messaging activation setup using IDurableMessagingGrain or DurableGrain.");

    public void Attach(DurableInboxExtension owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null || _recovered)
        {
            throw new InvalidOperationException("The inbox journal state must be attached once before recovery.");
        }

        _owner = owner;
    }

    public override void ValidatePendingChanges() => Owner.ValidatePendingChanges();
    public override void ValidateWrite() => Owner.ValidateWrite();
    public override void ValidateDelete() => Owner.ValidateDelete();
    public override void OnDeleteStarted() => Owner.OnDeleteStarted();
    public override void OnFaulted(Exception exception) => _owner?.OnFaulted(exception);
    public override void OnRecoveryCompleted()
    {
        _recovered = true;
        Owner.OnRecoveryCompleted();
    }

    public override void Reset(JournalStreamWriter writer)
    {
        base.Reset(writer);
        _captured = false;
        _owner?.ResetState();
    }

    public override void WritePendingEntries(JournalStreamWriter writer)
    {
        base.WritePendingEntries(writer);
        Owner.CaptureWrites();
        _captured = true;
    }

    public override void WriteSnapshot(JournalStreamWriter writer)
    {
        base.WriteSnapshot(writer);
        Owner.CaptureWrites();
        _captured = true;
    }

    public override void OnWriteCompleted()
    {
        base.OnWriteCompleted();
        if (_captured)
        {
            _captured = false;
            Owner.OnWriteCompleted();
        }
    }
}
