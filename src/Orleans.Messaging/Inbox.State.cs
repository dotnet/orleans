using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling;

namespace Orleans.Messaging;

internal sealed partial class Inbox
{
    private readonly Dictionary<MessageId, InboxEntry> _messages = [];
    private readonly Dictionary<MessageId, InboxProcessedEntry> _processed = [];
    private readonly Dictionary<MessageId, InboxDeadLetter> _deadLetters = [];
    private JournalStreamWriter _writer;

    // Forgetting an identity requires a durable floor which excludes its latest observed send time.
    private DateTimeOffset _admissionFloor = DateTimeOffset.MinValue;
    private DateTimeOffset _capturedAdmissionFloor = DateTimeOffset.MinValue;
    private DateTimeOffset _durableAdmissionFloor = DateTimeOffset.MinValue;

    // Acceptances and completions are durable once a completed write captured their sequence number.
    private long _lastSequence;
    private long _capturedSequence;
    private long _durableSequence;
    private long _lastCompletionSequence;
    private bool _capturedSnapshot;

    // Whether the last inbox command in the journal records that an activation stopped without an interrupted attempt.
    private bool _stoppedCleanly;

    // Whether this activation finished its recovery bookkeeping, so its stop can be recorded as clean.
    private bool _recovered;

    // The earliest due time, on the Durable Jobs clock, of accepted or retrying messages staged since the last capture.
    private DateTimeOffset? _stagedWakeup;

    private bool IsDurable(InboxEntry entry) => entry.Sequence <= _durableSequence;

    // Whether every processed record which deduplication relies on is durable.
    private bool AreCompletionsDurable => _lastCompletionSequence <= _durableSequence;

    // The handler attempt in progress, whose staged changes the inbox discards if it fails.
    private HandlerExecution? _execution;

    // Every journal write prepares every state, so a write which makes a message or a work source's work pending ensures
    // its wake-up before it can become durable. After messaging fails, in-memory state may hold changes which must not
    // commit, such as a failed handler's, so every later write fails and recovery starts from the journal.
    ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken)
    {
        if (_pump.IsFailed)
        {
            return ValueTask.FromException(new InvalidOperationException(
                "Messaging failed for this activation, so its uncommitted changes are discarded by recovery from the journal."));
        }

        var dueTime = _stagedWakeup;
        if (GetWorkSourceDelay() is { } delay)
        {
            var workDueTime = MessagingTime.AddClamped(Shared.JobTimeProvider.GetUtcNow(), delay);
            if (dueTime is null || workDueTime < dueTime) dueTime = workDueTime;
        }

        return dueTime is { } due ? _pump.EnsureWakeupAsync(due, cancellationToken) : ValueTask.CompletedTask;
    }

    private void OnWriteCaptured(bool snapshot = false)
    {
        _capturedSequence = _lastSequence;
        _capturedAdmissionFloor = _admissionFloor;
        _capturedSnapshot = snapshot;
        _stagedWakeup = null;
    }

    void IStateMachine.OnWriteCompleted()
    {
        _durableSequence = _capturedSequence;
        _durableAdmissionFloor = _capturedAdmissionFloor;
        // Floor-only appends stay constant-time; snapshots reclaim history after their admission floor is durable.
        if (_capturedSnapshot) CompactProcessed();
        _pump.Signal();
    }

    private DateTimeOffset GetAdmissionFloor(DateTimeOffset now)
    {
        var nowTicks = now.UtcTicks;
        var candidate = new DateTimeOffset(Math.Max(0, nowTicks - Options.DeduplicationWindow.Ticks), TimeSpan.Zero);
        return candidate > _admissionFloor ? candidate : _admissionFloor;
    }

    private static bool IsAtOrBelowFloor(InboxProcessedEntry entry, DateTimeOffset floor) =>
        entry.SentAt <= floor;

    private void CompactProcessed()
    {
        foreach (var (key, entry) in _processed)
        {
            if (entry.Sequence <= _durableSequence && IsAtOrBelowFloor(entry, _durableAdmissionFloor)) _processed.Remove(key);
        }
    }

    private void Emit(InboxCommand command)
    {
        Shared.InboxCodec.WriteSet(command, _writer);
        Apply(command);
        var dueTime = command switch
        {
            { Kind: InboxCommandKind.Accept, Message: { } message } => message.Envelope.DeliverAt ?? DateTimeOffset.MinValue,
            { Kind: InboxCommandKind.Retry, State: { } state } => state.NextAttemptAt,
            { Kind: InboxCommandKind.ExtendSentAt, MessageId: var key } when _messages.TryGetValue(key, out var pending) => GetDueTime(pending),
            _ => (DateTimeOffset?)null
        };
        if (dueTime is { } due && (_stagedWakeup is null || due < _stagedWakeup)) _stagedWakeup = due;
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        ReportDepth(-_messages.Count);
        _writer = writer;
        _messages.Clear();
        _processed.Clear();
        _deadLetters.Clear();
        _admissionFloor = _capturedAdmissionFloor = _durableAdmissionFloor = DateTimeOffset.MinValue;
        _stagedWakeup = null;
        _capturedSnapshot = false;
        _stoppedCleanly = false;
        _lastSequence = _capturedSequence = _durableSequence = _lastCompletionSequence = 0;
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, Shared.InboxCodec).Apply(entry.Reader, this);

    void IStateMachine.OnRecoveryCompleted()
    {
        foreach (var (key, entry) in _messages)
        {
            EnvelopeValidation.Validate(entry.Message);
            ValidateRecoveredSentAt(entry.Message.SentAt);
            if (key != entry.Message.Envelope.MessageId)
            {
                throw new InvalidOperationException("The recovered inbox key does not match its command identity.");
            }
        }

        foreach (var (key, letter) in _deadLetters)
        {
            EnvelopeValidation.Validate(letter.Message);
            ValidateRecoveredSentAt(letter.Message.SentAt);
            if (key != letter.Message.Envelope.MessageId)
            {
                throw new InvalidOperationException("The recovered inbox dead-letter key does not match its command identity.");
            }
        }

        foreach (var entry in _processed.Values) ValidateRecoveredSentAt(entry.SentAt);

        _capturedSequence = _durableSequence = _lastSequence;
        _capturedAdmissionFloor = _durableAdmissionFloor = _admissionFloor;
        CompactProcessed();
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => OnWriteCaptured();

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        // The floor and compacted history commit atomically. Keep the in-memory records until acknowledgement, so
        // an interleaving delivery still deduplicates while the snapshot's admission floor is not yet durable.
        var floor = GetAdmissionFloor(Shared.JobTimeProvider.GetUtcNow());
        Shared.InboxCodec.WriteSet(new()
        {
            Kind = InboxCommandKind.Snapshot,
            Snapshot = new()
            {
                Messages = _messages.Values.ToArray(),
                Processed = _processed.Values.Where(entry => !IsAtOrBelowFloor(entry, floor)).ToArray(),
                DeadLetters = _deadLetters.Values.ToArray(),
                AdmissionFloor = floor,
                StoppedCleanly = _stoppedCleanly
            }
        }, writer);
        _admissionFloor = floor;
        OnWriteCaptured(snapshot: true);
    }

    void IDurableValueCommandHandler<InboxCommand>.ApplySet(InboxCommand command) => Apply(command);

    private void Apply(InboxCommand command)
    {
        if (command.Kind != InboxCommandKind.Snapshot) _stoppedCleanly = command.Kind == InboxCommandKind.CleanStop;
        switch (command.Kind)
        {
            case InboxCommandKind.Accept:
                var message = command.Message ?? throw new InvalidOperationException("An accept command requires its message.");
                _messages.Add(message.Envelope.MessageId, new InboxEntry { Message = message, State = new(), Sequence = ++_lastSequence });
                _processed.Remove(message.Envelope.MessageId);
                ReportDepth(1);
                break;
            case InboxCommandKind.Retry:
                _messages[command.MessageId].State = command.State ?? throw new InvalidOperationException("A retry command requires its state.");
                break;
            case InboxCommandKind.Complete:
                if (_messages.Remove(command.MessageId)) ReportDepth(-1);

                _lastCompletionSequence = ++_lastSequence;
                _processed[command.MessageId] = new()
                {
                    MessageId = command.MessageId,
                    SentAt = command.SentAt,
                    Rejection = command.Rejection,
                    Sequence = _lastSequence
                };
                if (command.DeadLetter is { } letter) _deadLetters[command.MessageId] = letter;
                break;
            case InboxCommandKind.AdvanceAdmissionFloor:
                AdvanceAdmissionFloor(command.AdmissionFloor);
                break;
            case InboxCommandKind.ExtendSentAt:
                // Pending entries are present in every snapshot; processed extensions use completion upserts.
                if (!_messages.TryGetValue(command.MessageId, out var pending))
                {
                    throw new InvalidOperationException("An ExtendSentAt command requires its pending entry.");
                }
                if (command.SentAt > pending.Message.SentAt)
                {
                    pending.Message = pending.Message with { SentAt = command.SentAt };
                    pending.Sequence = ++_lastSequence;
                }
                break;
            case InboxCommandKind.RemoveDeadLetters:
                foreach (var key in command.RemovedKeys!) _deadLetters.Remove(key);
                break;
            case InboxCommandKind.CleanStop:
            case InboxCommandKind.Resume:
                break;
            case InboxCommandKind.Snapshot:
                var snapshot = command.Snapshot ?? throw new InvalidOperationException("A snapshot command requires its state.");
                AdvanceAdmissionFloor(snapshot.AdmissionFloor);
                _stoppedCleanly = snapshot.StoppedCleanly;
                var previousCount = _messages.Count;
                _messages.Clear();
                _processed.Clear();
                _deadLetters.Clear();
                foreach (var restored in snapshot.Messages) _messages.Add(restored.Message.Envelope.MessageId, restored);

                ReportDepth(_messages.Count - previousCount);

                foreach (var processed in snapshot.Processed)
                {
                    _processed.Add(processed.MessageId, processed);
                }

                foreach (var restored in snapshot.DeadLetters) _deadLetters.Add(restored.Message.Envelope.MessageId, restored);
                break;
            default:
                throw new NotSupportedException($"Inbox command '{command.Kind}' is not supported.");
        }
    }

    private void AdvanceAdmissionFloor(DateTimeOffset floor)
    {
        if (floor < _admissionFloor) throw new InvalidOperationException("The inbox admission floor cannot move backward.");
        _admissionFloor = floor;
    }

    private static void ValidateRecoveredSentAt(DateTimeOffset sentAt)
    {
        // Admission requires a time above a floor which is at least MinValue, so default cannot be admitted state.
        if (sentAt == DateTimeOffset.MinValue) throw new InvalidOperationException("A recovered inbox entry requires its admitted send time.");
    }
}

internal enum InboxCommandKind
{
    Accept = 1,
    Retry = 2,
    Complete = 3,
    RemoveDeadLetters = 4,
    Snapshot = 6,
    AdvanceAdmissionFloor = 7,
    ExtendSentAt = 8,

    // An activation stopped with no handler attempt interrupted, so recovery charges no attempt.
    CleanStop = 9,

    // A recovered activation consumed a clean stop, so a later unclean stop is charged.
    Resume = 10
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxCommand")]
internal sealed class InboxCommand
{
    [Id(0)] public InboxCommandKind Kind { get; init; }
    [Id(1)] public InboxMessage? Message { get; init; }
    [Id(2)] public MessageId MessageId { get; init; }
    [Id(3)] public InboxMessageState? State { get; init; }
    [Id(5)] public InboxDeadLetter? DeadLetter { get; init; }
    [Id(6)] public MessageId[]? RemovedKeys { get; init; }
    [Id(7)] public InboxSnapshot? Snapshot { get; init; }
    [Id(8)] public InboxRejection? Rejection { get; init; }
    [Id(9)] public DateTimeOffset SentAt { get; init; }
    [Id(10)] public DateTimeOffset AdmissionFloor { get; init; }
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxSnapshot")]
internal sealed class InboxSnapshot
{
    [Id(0)] public InboxEntry[] Messages { get; init; } = [];
    [Id(1)] public InboxProcessedEntry[] Processed { get; init; } = [];
    [Id(2)] public InboxDeadLetter[] DeadLetters { get; init; } = [];
    [Id(3)] public DateTimeOffset AdmissionFloor { get; init; } = DateTimeOffset.MinValue;
    [Id(4)] public bool StoppedCleanly { get; init; }
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxProcessedEntry")]
internal sealed class InboxProcessedEntry
{
    [Id(0)] public MessageId MessageId { get; init; }
    [Id(2)] public InboxRejection? Rejection { get; init; }
    [Id(3)] public required DateTimeOffset SentAt { get; init; }
    [field: NonSerialized]
    [JsonIgnore] public long Sequence { get; init; }
}

/// <summary>The answer which an inbox sink gave when it rejected a message on delivery.</summary>
[GenerateSerializer, Alias("Orleans.Messaging.InboxRejection")]
internal sealed class InboxRejection
{
    [Id(0)] public required string Reason { get; init; }
    [Id(1)] public string? Code { get; init; }
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxEntry")]
internal sealed class InboxEntry
{
    [Id(0)] public required InboxMessage Message { get; set; }
    [Id(1)] public required InboxMessageState State { get; set; }
    [field: NonSerialized]
    [JsonIgnore] public long Sequence { get; set; }
}

[JsonSerializable(typeof(InboxCommand))]
[JsonSerializable(typeof(InboxSnapshot))]
internal sealed partial class InboxJsonContext : JsonSerializerContext;
