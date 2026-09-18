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

    public void Attach(DurableInboxExtension owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null || _recovered)
        {
            throw new InvalidOperationException("The inbox journal state must be attached once before recovery.");
        }

        _owner = owner;
    }

    public override bool IsWritePrepared => _owner?.IsWritePrepared ?? true;
    public override void ValidateWrite() => _owner?.ValidateWrite();
    public override void ValidateDelete() => _owner?.ValidateDelete();
    public override void OnDeleteStarted() => _owner?.OnDeleteStarted();
    public override void OnFaulted(Exception exception) => _owner?.OnFaulted(exception);
    public override void OnRecoveryCompleted()
    {
        _recovered = true;
        _owner?.OnRecoveryCompleted();
    }

    public override void Reset(JournalStreamWriter writer)
    {
        base.Reset(writer);
        _captured = false;
        _owner?.ResetState();
    }

    public override void AppendEntries(JournalStreamWriter writer)
    {
        base.AppendEntries(writer);
        _owner?.CaptureWrites();
        _captured = true;
    }

    public override void AppendSnapshot(JournalStreamWriter writer)
    {
        base.AppendSnapshot(writer);
        _owner?.CaptureWrites();
        _captured = true;
    }

    public override void OnWriteCompleted()
    {
        base.OnWriteCompleted();
        if (_captured)
        {
            _captured = false;
            _owner?.OnWriteCompleted();
        }
    }
}
