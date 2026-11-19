using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
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

    // Forgetting an identity requires a durable floor which excludes its send time.
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
            InboxCommand.Accept accept => accept.Message.Envelope.DeliverAt ?? DateTimeOffset.MinValue,
            InboxCommand.Retry retry => retry.State.NextAttemptAt,
            _ => null
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
        // Admission validated every message, and replay keys each entry by its own message ID.
        Debug.Assert(_messages.All(static pair => pair.Key == pair.Value.Message.Envelope.MessageId));
        Debug.Assert(_deadLetters.All(static pair => pair.Key == pair.Value.Message.Envelope.MessageId));

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
        Shared.InboxCodec.WriteSet(new InboxCommand.Snapshot(
            _messages.Values.ToArray(),
            _processed.Values.Where(entry => !IsAtOrBelowFloor(entry, floor)).ToArray(),
            _deadLetters.Values.ToArray(),
            floor,
            _stoppedCleanly), writer);
        _admissionFloor = floor;
        OnWriteCaptured(snapshot: true);
    }

    void IDurableValueCommandHandler<InboxCommand>.ApplySet(InboxCommand command) => Apply(command);

    private void Apply(InboxCommand command)
    {
        switch (command)
        {
            case InboxCommand.Accept { Message: var message }:
                _messages.Add(message.Envelope.MessageId, new InboxEntry { Message = message, State = new(), Sequence = ++_lastSequence });
                // A reused identity starts a new command, which replaces the earlier command's records.
                _processed.Remove(message.Envelope.MessageId);
                _deadLetters.Remove(message.Envelope.MessageId);
                ReportDepth(1);
                break;
            case InboxCommand.Retry { MessageId: var key, State: var state }:
                _messages[key].State = state;
                break;
            case InboxCommand.Complete { MessageId: var key, SentAt: var sentAt, DeadLetter: var deadLetter, Rejection: var rejection }:
                if (_messages.Remove(key)) ReportDepth(-1);

                _lastCompletionSequence = ++_lastSequence;
                _processed[key] = new() { MessageId = key, SentAt = sentAt, Rejection = rejection, Sequence = _lastSequence };
                if (deadLetter is not null) _deadLetters[key] = deadLetter;
                else _deadLetters.Remove(key);
                break;
            case InboxCommand.AdvanceAdmissionFloor { Floor: var floor }:
                AdvanceAdmissionFloor(floor);
                break;
            case InboxCommand.RemoveDeadLetters { Keys: var keys }:
                foreach (var key in keys) _deadLetters.Remove(key);
                break;
            case InboxCommand.CleanStop or InboxCommand.Resume:
                break;
            case InboxCommand.Snapshot snapshot:
                AdvanceAdmissionFloor(snapshot.AdmissionFloor);
                var previousCount = _messages.Count;
                _messages.Clear();
                _processed.Clear();
                _deadLetters.Clear();
                foreach (var restored in snapshot.Messages) _messages.Add(restored.Message.Envelope.MessageId, restored);

                ReportDepth(_messages.Count - previousCount);

                foreach (var processed in snapshot.Processed) _processed.Add(processed.MessageId, processed);
                foreach (var restored in snapshot.DeadLetters) _deadLetters.Add(restored.Message.Envelope.MessageId, restored);
                break;
        }

        _stoppedCleanly = command switch
        {
            InboxCommand.Snapshot snapshot => snapshot.StoppedCleanly,
            InboxCommand.CleanStop => true,
            _ => false
        };
    }
    private void AdvanceAdmissionFloor(DateTimeOffset floor)
    {
        if (floor < _admissionFloor) throw new InvalidOperationException("The inbox admission floor cannot move backward.");
        _admissionFloor = floor;
    }

}

/// <summary>A change to the inbox's journaled state.</summary>
/// <remarks>
/// Case identifiers follow declaration order and are part of the journal format. In JSON, each case leads with its
/// <c>kind</c>, which <see cref="InboxCommandClassifier"/> reads to select the case.
/// </remarks>
[GenerateSerializer]
[JsonUnion(TypeClassifier = typeof(InboxCommandClassifier))]
internal union InboxCommand(
    InboxCommand.Accept,
    InboxCommand.Retry,
    InboxCommand.Complete,
    InboxCommand.RemoveDeadLetters,
    InboxCommand.Snapshot,
    InboxCommand.AdvanceAdmissionFloor,
    InboxCommand.CleanStop,
    InboxCommand.Resume)
{
    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.Accept")]
    internal sealed class Accept(InboxMessage message)
    {
        [Id(0)] public InboxMessage Message { get; } = message;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "accept";
    }

    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.Retry")]
    internal sealed class Retry(MessageId messageId, InboxMessageState state)
    {
        [Id(0)] public MessageId MessageId { get; } = messageId;
        [Id(1)] public InboxMessageState State { get; } = state;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "retry";
    }

    /// <summary>Records a message's processed outcome, replacing any earlier record and dead letter of it.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.Complete")]
    internal sealed class Complete(MessageId messageId, DateTimeOffset sentAt, InboxDeadLetter? deadLetter, InboxRejection? rejection)
    {
        [Id(0)] public MessageId MessageId { get; } = messageId;
        [Id(1)] public DateTimeOffset SentAt { get; } = sentAt;
        [Id(2)] public InboxDeadLetter? DeadLetter { get; } = deadLetter;
        [Id(3)] public InboxRejection? Rejection { get; } = rejection;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "complete";
    }

    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.RemoveDeadLetters")]
    internal sealed class RemoveDeadLetters(MessageId[] keys)
    {
        [Id(0)] public MessageId[] Keys { get; } = keys;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "removeDeadLetters";
    }

    /// <summary>Replaces the inbox's state.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.Snapshot")]
    internal sealed class Snapshot(InboxEntry[] messages, InboxProcessedEntry[] processed, InboxDeadLetter[] deadLetters, DateTimeOffset admissionFloor, bool stoppedCleanly)
    {
        [Id(0)] public InboxEntry[] Messages { get; } = messages;
        [Id(1)] public InboxProcessedEntry[] Processed { get; } = processed;
        [Id(2)] public InboxDeadLetter[] DeadLetters { get; } = deadLetters;
        [Id(3)] public DateTimeOffset AdmissionFloor { get; } = admissionFloor;
        [Id(4)] public bool StoppedCleanly { get; } = stoppedCleanly;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "snapshot";
    }

    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.AdvanceAdmissionFloor")]
    internal sealed class AdvanceAdmissionFloor(DateTimeOffset floor)
    {
        [Id(0)] public DateTimeOffset Floor { get; } = floor;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "advanceAdmissionFloor";
    }

    /// <summary>Records that an activation stopped with no handler attempt interrupted, so recovery charges none.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.CleanStop")]
    internal sealed class CleanStop
    {
        public static CleanStop Instance { get; } = new();

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "cleanStop";
    }

    /// <summary>Records that a recovered activation consumed a clean stop, so a later unclean stop is charged.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.InboxCommand.Resume")]
    internal sealed class Resume
    {
        public static Resume Instance { get; } = new();

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "resume";
    }
}

/// <summary>Selects an <see cref="InboxCommand"/> case from the <c>kind</c> which each JSON case leads with.</summary>
internal sealed class InboxCommandClassifier : JsonTypeClassifierFactory
{
    public override bool CanClassify(JsonTypeClassifierContext context) => context.DeclaringType == typeof(InboxCommand);

    public override JsonTypeClassifier CreateJsonClassifier(JsonTypeClassifierContext context, JsonSerializerOptions options) => Classify;

    private static Type? Classify(ref Utf8JsonReader reader)
    {
        var lookahead = reader;
        if (lookahead.TokenType != JsonTokenType.StartObject) return null;
        while (lookahead.Read() && lookahead.TokenType == JsonTokenType.PropertyName)
        {
            var isKind = lookahead.ValueTextEquals("kind"u8);
            lookahead.Read();
            if (isKind)
            {
                return lookahead.GetString() switch
                {
                    "accept" => typeof(InboxCommand.Accept),
                    "retry" => typeof(InboxCommand.Retry),
                    "complete" => typeof(InboxCommand.Complete),
                    "removeDeadLetters" => typeof(InboxCommand.RemoveDeadLetters),
                    "snapshot" => typeof(InboxCommand.Snapshot),
                    "advanceAdmissionFloor" => typeof(InboxCommand.AdvanceAdmissionFloor),
                    "cleanStop" => typeof(InboxCommand.CleanStop),
                    "resume" => typeof(InboxCommand.Resume),
                    _ => null
                };
            }

            lookahead.Skip();
        }

        return null;
    }
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
    [Id(0)] public required InboxMessage Message { get; init; }
    [Id(1)] public required InboxMessageState State { get; set; }
    [field: NonSerialized]
    [JsonIgnore] public long Sequence { get; set; }
}

[JsonSerializable(typeof(InboxCommand))]
internal sealed partial class InboxJsonContext : JsonSerializerContext;
