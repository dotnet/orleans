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
    private readonly Dictionary<HierarchicalKey, InboxEntry> _messages = [];
    private readonly Dictionary<HierarchicalKey, DateTimeOffset> _processed = [];
    private readonly Dictionary<HierarchicalKey, InboxDeadLetter> _deadLetters = [];
    private JournalStreamWriter _writer;

    // Pending messages with a delivery time, which do not count toward MaxCapacity.
    private int _scheduledCount;

    // Entries are durable once a completed write captured their sequence number.
    private long _lastSequence;
    private long _capturedSequence;
    private long _durableSequence;

    private bool IsDurable(InboxEntry entry) => entry.Sequence <= _durableSequence;

    ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken) =>
        _lastSequence != _capturedSequence ? _pump.EnsureWakeupAsync(cancellationToken) : ValueTask.CompletedTask;

    private void OnWriteCaptured() => _capturedSequence = _lastSequence;

    void IStateMachine.OnWriteCompleted()
    {
        _durableSequence = _capturedSequence;
        _pump.Signal();
    }

    private void Emit(InboxCommand command)
    {
        Shared.InboxCodec.WriteSet(command, _writer);
        Apply(command);
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        ReportDepth(-_messages.Count);
        _writer = writer;
        _messages.Clear();
        _processed.Clear();
        _deadLetters.Clear();
        _scheduledCount = 0;
        _lastSequence = _capturedSequence = _durableSequence = 0;
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, Shared.InboxCodec).Apply(entry.Reader, this);

    void IStateMachine.OnRecoveryCompleted()
    {
        foreach (var (key, entry) in _messages)
        {
            EnvelopeValidation.Validate(entry.Message);
            if (key != entry.Message.Envelope.MessageId)
            {
                throw new InvalidOperationException("The recovered inbox key does not match its command identity.");
            }
        }

        foreach (var (key, letter) in _deadLetters)
        {
            EnvelopeValidation.Validate(letter.Message);
            if (key != letter.Message.Envelope.MessageId)
            {
                throw new InvalidOperationException("The recovered inbox dead-letter key does not match its command identity.");
            }
        }

        _capturedSequence = _durableSequence = _lastSequence;
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => OnWriteCaptured();

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        // Snapshots compact deduplication history, so retained history grows with the journal.
        var now = TimeProvider.GetUtcNow();
        foreach (var (key, processedAt) in _processed)
        {
            if (MessagingTime.IsExpired(now, processedAt, Options.DeduplicationWindow)) _processed.Remove(key);
        }

        Shared.InboxCodec.WriteSet(new()
        {
            Kind = InboxCommandKind.Snapshot,
            Snapshot = new()
            {
                Messages = _messages.Values.ToArray(),
                Processed = _processed.Select(static pair => new InboxProcessedEntry { MessageId = pair.Key, ProcessedAt = pair.Value }).ToArray(),
                DeadLetters = _deadLetters.Values.ToArray()
            }
        }, writer);
        OnWriteCaptured();
    }

    void IDurableValueCommandHandler<InboxCommand>.ApplySet(InboxCommand command) => Apply(command);

    private void Apply(InboxCommand command)
    {
        switch (command.Kind)
        {
            case InboxCommandKind.Accept:
                var message = command.Message ?? throw new InvalidOperationException("An accept command requires its message.");
                _messages.Add(message.Envelope.MessageId, new InboxEntry { Message = message, State = new(), Sequence = ++_lastSequence });
                _processed.Remove(message.Envelope.MessageId);
                if (message.Envelope.DeliverAt is not null) _scheduledCount++;
                ReportDepth(1);
                break;
            case InboxCommandKind.Retry:
                _messages[command.MessageId].State = command.State ?? throw new InvalidOperationException("A retry command requires its state.");
                break;
            case InboxCommandKind.Complete:
                if (_messages.Remove(command.MessageId, out var completed))
                {
                    if (completed.Message.Envelope.DeliverAt is not null) _scheduledCount--;
                    ReportDepth(-1);
                }

                _processed[command.MessageId] = command.ProcessedAt;
                if (command.DeadLetter is { } letter) _deadLetters[command.MessageId] = letter;
                break;
            case InboxCommandKind.RemoveDeadLetters:
                foreach (var key in command.RemovedKeys!) _deadLetters.Remove(key);
                break;
            case InboxCommandKind.Snapshot:
                var snapshot = command.Snapshot ?? throw new InvalidOperationException("A snapshot command requires its state.");
                var previousCount = _messages.Count;
                _messages.Clear();
                _processed.Clear();
                _deadLetters.Clear();
                _scheduledCount = 0;
                foreach (var restored in snapshot.Messages)
                {
                    _messages.Add(restored.Message.Envelope.MessageId, restored);
                    if (restored.Message.Envelope.DeliverAt is not null) _scheduledCount++;
                }

                ReportDepth(_messages.Count - previousCount);

                foreach (var processed in snapshot.Processed) _processed.Add(processed.MessageId, processed.ProcessedAt);
                foreach (var restored in snapshot.DeadLetters) _deadLetters.Add(restored.Message.Envelope.MessageId, restored);
                break;
            default:
                throw new NotSupportedException($"Inbox command '{command.Kind}' is not supported.");
        }
    }
}

internal enum InboxCommandKind
{
    Accept = 1,
    Retry = 2,
    Complete = 3,
    RemoveDeadLetters = 4,
    Snapshot = 6
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxCommand")]
internal sealed class InboxCommand
{
    [Id(0)] public InboxCommandKind Kind { get; init; }
    [Id(1)] public InboxMessage? Message { get; init; }
    [Id(2)] public HierarchicalKey MessageId { get; init; }
    [Id(3)] public InboxMessageState? State { get; init; }
    [Id(4)] public DateTimeOffset ProcessedAt { get; init; }
    [Id(5)] public InboxDeadLetter? DeadLetter { get; init; }
    [Id(6)] public HierarchicalKey[]? RemovedKeys { get; init; }
    [Id(7)] public InboxSnapshot? Snapshot { get; init; }
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxSnapshot")]
internal sealed class InboxSnapshot
{
    [Id(0)] public InboxEntry[] Messages { get; init; } = [];
    [Id(1)] public InboxProcessedEntry[] Processed { get; init; } = [];
    [Id(2)] public InboxDeadLetter[] DeadLetters { get; init; } = [];
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxProcessedEntry")]
internal sealed class InboxProcessedEntry
{
    [Id(0)] public HierarchicalKey MessageId { get; init; }
    [Id(1)] public DateTimeOffset ProcessedAt { get; init; }
}

[GenerateSerializer, Alias("Orleans.Messaging.InboxEntry")]
internal sealed class InboxEntry
{
    [Id(0)] public required InboxMessage Message { get; init; }
    [Id(1)] public required InboxMessageState State { get; set; }
    [field: NonSerialized]
    [JsonIgnore] public long Sequence { get; init; }
}

[JsonSerializable(typeof(InboxCommand))]
[JsonSerializable(typeof(InboxSnapshot))]
internal sealed partial class InboxJsonContext : JsonSerializerContext;
