using System;
using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed partial class DurableOutbox
{
    [Flags]
    private enum StateSlot
    {
        Messages = 1,
        MessageStates = 2,
        DeadLetters = 4,
        JobId = 8,
        Job = 16,
        CompletedJobId = 32,
        JobSequence = 64,
        All = Messages | MessageStates | DeadLetters | JobId | Job | CompletedJobId | JobSequence
    }

    private interface IOutboxState : IStateMachine
    {
        bool HasChanges { get; }
    }

    private sealed class DictionaryState<TValue>(DurableOutbox owner, StateSlot slot)
        : DeferredJournaledDictionary<Guid, TValue>(owner._stateManager), IOutboxState
    {
        public bool HasChanges => HasPendingChanges;
        public override bool IsWritePrepared => owner.IsWritePrepared;
        public override void ValidateWrite() => owner.ValidateWrite();
        public override void ValidateDelete() => owner.ValidateDelete();
        public override void OnDeleteStarted() => owner.OnDeleteStarted();

        public override void WritePendingEntries(JournalStreamWriter writer)
        {
            owner.CaptureWrite(snapshot: false);
            base.WritePendingEntries(writer);
            owner.OnStateCaptured(slot);
        }

        public override void WriteSnapshot(JournalStreamWriter writer)
        {
            owner.CaptureWrite(snapshot: true);
            base.WriteSnapshot(writer);
            owner.OnStateCaptured(slot);
        }

        public override void OnWriteCompleted()
        {
            base.OnWriteCompleted();
            owner.OnStateWriteCompleted(slot);
        }

        public override void Reset(JournalStreamWriter writer)
        {
            base.Reset(writer);
            owner.OnStateReset(slot);
        }

        public override void OnRecoveryCompleted()
        {
            base.OnRecoveryCompleted();
            owner.OnStateRecoveryCompleted(slot);
        }

        public override void OnFaulted(Exception exception)
        {
            owner.OnFaulted(exception);
            base.OnFaulted(exception);
        }
    }

    private sealed class ValueState<TValue>(DurableOutbox owner, StateSlot slot)
        : DeferredJournaledValue<TValue>(owner._stateManager), IOutboxState
    {
        public bool HasChanges => HasPendingChanges;
        public override bool IsWritePrepared => owner.IsWritePrepared;
        public override void ValidateWrite() => owner.ValidateWrite();
        public override void ValidateDelete() => owner.ValidateDelete();
        public override void OnDeleteStarted() => owner.OnDeleteStarted();

        public override void WritePendingEntries(JournalStreamWriter writer)
        {
            owner.CaptureWrite(snapshot: false);
            base.WritePendingEntries(writer);
            owner.OnStateCaptured(slot);
        }

        public override void WriteSnapshot(JournalStreamWriter writer)
        {
            owner.CaptureWrite(snapshot: true);
            base.WriteSnapshot(writer);
            owner.OnStateCaptured(slot);
        }

        public override void OnWriteCompleted()
        {
            base.OnWriteCompleted();
            owner.OnStateWriteCompleted(slot);
        }

        public override void Reset(JournalStreamWriter writer)
        {
            base.Reset(writer);
            owner.OnStateReset(slot);
        }

        public override void OnRecoveryCompleted()
        {
            base.OnRecoveryCompleted();
            owner.OnStateRecoveryCompleted(slot);
        }

        public override void OnFaulted(Exception exception)
        {
            owner.OnFaulted(exception);
            base.OnFaulted(exception);
        }
    }
}
