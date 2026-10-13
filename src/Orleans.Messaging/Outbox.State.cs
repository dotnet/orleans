using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling;

namespace Orleans.Messaging;

internal sealed partial class Outbox
{
    // Entries are durable once a completed write captured their sequence number.
    private long _lastSequence;
    private long _capturedSequence;
    private long _durableSequence;

    ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken) =>
        _lastSequence != _capturedSequence ? _pump.EnsureWakeupAsync(cancellationToken) : ValueTask.CompletedTask;

    private void OnWriteCaptured() => _capturedSequence = _lastSequence;

    void IStateMachine.OnWriteCompleted()
    {
        _durableSequence = _capturedSequence;
        _pump.Signal();
    }

    private OutboxCommand CreateSnapshot() => new()
    {
        Kind = OutboxCommandKind.Snapshot,
        Snapshot = new()
        {
            Messages = _messages.Values.ToArray(),
            DeadLetters = _deadLetters.Values.ToArray()
        }
    };

    private void Emit(OutboxCommand command)
    {
        Shared.OutboxCodec.WriteSet(command, _writer);
        Apply(command);
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _writer = writer;
        ResetState();
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, Shared.OutboxCodec).Apply(entry.Reader, this);

    void IStateMachine.OnRecoveryCompleted() => OnRecoveryCompleted();
    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => OnWriteCaptured();

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer)
    {
        Shared.OutboxCodec.WriteSet(CreateSnapshot(), writer);
        OnWriteCaptured();
    }

    void IDurableValueCommandHandler<OutboxCommand>.ApplySet(OutboxCommand command) => Apply(command);

    private void Apply(OutboxCommand command)
    {
        switch (command.Kind)
        {
            case OutboxCommandKind.Enqueue:
                var state = command.State ?? throw new InvalidOperationException("An enqueue command requires its delivery state.");
                var entry = new OutboxEntry { Message = command.Message, Reference = command.Reference, State = state, Sequence = ++_lastSequence };
                _messages.Add(entry.MessageId, entry);
                ReportDepth(1);
                break;
            case OutboxCommandKind.Delivery:
                if (command.State is { } retry) _messages[command.MessageId].State = retry;
                else if (_messages.Remove(command.MessageId)) ReportDepth(-1);
                if (command.DeadLetter is { } letter) _deadLetters[command.MessageId] = letter;
                if (command.RemovedDeadLetters is { } removed)
                {
                    foreach (var key in removed) _deadLetters.Remove(key);
                }
                break;
            case OutboxCommandKind.RemoveDeadLetters:
                foreach (var key in command.RemovedDeadLetters!) _deadLetters.Remove(key);
                break;
            case OutboxCommandKind.Snapshot:
                var snapshot = command.Snapshot ?? throw new InvalidOperationException("A snapshot command requires its state.");
                var previousCount = _messages.Count;
                _messages.Clear();
                _deadLetters.Clear();
                foreach (var restored in snapshot.Messages) _messages.Add(restored.MessageId, restored);
                foreach (var restored in snapshot.DeadLetters) _deadLetters.Add(restored.Message.Envelope.MessageId, restored);
                ReportDepth(_messages.Count - previousCount);
                break;
            default:
                throw new NotSupportedException($"Outbox command '{command.Kind}' is not supported.");
        }
    }

    internal bool RemoveDeadLetter(HierarchicalKey id)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (!_deadLetters.ContainsKey(id)) return false;
        Emit(new() { Kind = OutboxCommandKind.RemoveDeadLetters, RemovedDeadLetters = [id] });
        return true;
    }
}

internal enum OutboxCommandKind
{
    Enqueue = 1,
    Delivery = 2,
    RemoveDeadLetters = 3,
    Snapshot = 4
}

[GenerateSerializer, Alias("Orleans.Messaging.OutboxCommand")]
internal sealed class OutboxCommand
{
    [Id(0)] public OutboxCommandKind Kind { get; init; }
    [Id(1)] public OutboxMessage? Message { get; init; }
    [Id(2)] public HierarchicalKey MessageId { get; init; }
    [Id(3)] public OutboxMessageState? State { get; init; }
    [Id(4)] public OutboxDeadLetter? DeadLetter { get; init; }
    [Id(5)] public HierarchicalKey[]? RemovedDeadLetters { get; init; }
    [Id(6)] public OutboxSnapshot? Snapshot { get; init; }

    // An enqueue command sets this instead of Message for a message which an IOutboxMessageSource provides.
    [Id(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutboxMessageReference? Reference { get; init; }
}

/// <summary>Identifies a message which an <see cref="IOutboxMessageSource"/> provides.</summary>
[GenerateSerializer, Alias("Orleans.Messaging.OutboxMessageReference")]
internal readonly struct OutboxMessageReference
{
    [Id(0)] public required HierarchicalKey MessageId { get; init; }
}

[GenerateSerializer, Alias("Orleans.Messaging.OutboxSnapshot")]
internal sealed class OutboxSnapshot
{
    [Id(0)] public OutboxEntry[] Messages { get; init; } = [];
    [Id(1)] public OutboxDeadLetter[] DeadLetters { get; init; } = [];
}

[GenerateSerializer, Alias("Orleans.Messaging.OutboxEntry")]
internal sealed class OutboxEntry
{
    // Exactly one of Message and Reference is set. A reference entry journals no payload or receiver: its source provides the message.
    [Id(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutboxMessage? Message { get; init; }

    [Id(1)] public required OutboxMessageState State { get; set; }

    [Id(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutboxMessageReference? Reference { get; init; }

    [field: NonSerialized]
    [JsonIgnore] public long Sequence { get; init; }

    /// <summary>Gets the source which owns a reference entry's message, resolved when it is sent or first materialized after recovery.</summary>
    [field: NonSerialized]
    [JsonIgnore] public IOutboxMessageSource? Source { get; set; }

    [JsonIgnore]
    public HierarchicalKey MessageId => Message is { } message ? message.Envelope.MessageId
        : Reference is { } reference ? reference.MessageId
        : throw new InvalidOperationException("An outbox entry requires its message or message reference.");
}

[JsonSerializable(typeof(OutboxCommand))]
[JsonSerializable(typeof(OutboxSnapshot))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
