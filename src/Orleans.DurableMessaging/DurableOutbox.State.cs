using Orleans.Journaling;

namespace Orleans.DurableMessaging;

internal sealed partial class DurableOutbox
{
    // The existing sequence stream supplies the feature's capture/ACK boundary for every journal writer.
    private sealed class SequenceState(DurableOutbox owner, IDurableValueCommandCodec<long> codec)
        : IDurableValue<long>, IStateMachine, IDurableValueCommandHandler<long>
    {
        private long _value;
        private bool _dirty;

        public long Value
        {
            get => _value;
            set
            {
                _value = value;
                _dirty = true;
            }
        }

        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, codec).Apply(entry.Reader, this);

        public void Reset(JournalStreamWriter writer)
        {
            _value = 0;
            _dirty = false;
            owner.ResetState();
        }

        public void OnRecoveryCompleted() => owner.OnRecoveryCompleted();

        public void WritePendingEntries(JournalStreamWriter writer)
        {
            owner.CaptureWrite();
            if (_dirty)
            {
                codec.WriteSet(_value, writer);
                _dirty = false;
            }
        }

        public void WriteSnapshot(JournalStreamWriter writer)
        {
            owner.CaptureWrite();
            codec.WriteSet(_value, writer);
            _dirty = false;
        }

        public void OnWriteCompleted() => owner.CompleteCapture();

        void IDurableValueCommandHandler<long>.ApplySet(long value) => _value = value;
    }
}
