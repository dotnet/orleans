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

internal sealed partial class Outbox
{
    // Entries are durable once a completed write captured their sequence number.
    private long _lastSequence;
    private long _capturedSequence;
    private long _durableSequence;

    ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken) =>
        _lastSequence != _capturedSequence
            ? _pump.EnsureWakeupAsync(Shared.JobTimeProvider.GetUtcNow(), cancellationToken)
            : ValueTask.CompletedTask;

    private void OnWriteCaptured() => _capturedSequence = _lastSequence;

    void IStateMachine.OnWriteCompleted()
    {
        _durableSequence = _capturedSequence;
        _pump.Signal();
    }

    private OutboxCommand CreateSnapshot() => new OutboxCommand.Snapshot(_messages.Values.ToArray(), _deadLetters.Values.ToArray());

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
        switch (command)
        {
            case OutboxCommand.Enqueue { Message: var message, State: var state }:
                Add(new OutboxEntry { Message = message, State = state, Sequence = ++_lastSequence });
                break;
            case OutboxCommand.EnqueueReference { Reference: var reference, State: var state }:
                Add(new OutboxEntry { Reference = reference, State = state, Sequence = ++_lastSequence });
                break;
            case OutboxCommand.Retry { MessageId: var key, State: var state }:
                _messages[key].State = state;
                break;
            case OutboxCommand.Retire { MessageId: var key, DeadLetter: var deadLetter }:
                if (_messages.Remove(key)) ReportDepth(-1);
                if (deadLetter is not null) _deadLetters[key] = deadLetter;
                break;
            case OutboxCommand.RemoveDeadLetters { Keys: var keys }:
                foreach (var key in keys) _deadLetters.Remove(key);
                break;
            case OutboxCommand.Snapshot snapshot:
                var previousCount = _messages.Count;
                _messages.Clear();
                _deadLetters.Clear();
                foreach (var restored in snapshot.Messages) _messages.Add(restored.MessageId, restored);
                foreach (var restored in snapshot.DeadLetters) _deadLetters.Add(restored.Message.Envelope.MessageId, restored);
                ReportDepth(_messages.Count - previousCount);
                break;
        }
    }

    private void Add(OutboxEntry entry)
    {
        // Sending stamps every entry with the send time which bounds its retries.
        Debug.Assert(entry.State.SentAt is not null);
        _messages.Add(entry.MessageId, entry);
        ReportDepth(1);
    }

    internal bool RemoveDeadLetter(MessageId id)
    {
        _pump.ShutdownToken.ThrowIfCancellationRequested();
        if (!_deadLetters.ContainsKey(id)) return false;
        Emit(new OutboxCommand.RemoveDeadLetters([id]));
        return true;
    }
}

/// <summary>A change to the outbox's journaled state.</summary>
/// <remarks>
/// Case identifiers follow declaration order and are part of the journal format. In JSON, each case leads with its
/// <c>kind</c>, which <see cref="OutboxCommandClassifier"/> reads to select the case.
/// </remarks>
[GenerateSerializer]
[JsonUnion(TypeClassifier = typeof(OutboxCommandClassifier))]
internal union OutboxCommand(
    OutboxCommand.Enqueue,
    OutboxCommand.EnqueueReference,
    OutboxCommand.Retry,
    OutboxCommand.Retire,
    OutboxCommand.RemoveDeadLetters,
    OutboxCommand.Snapshot)
{
    /// <summary>Stages a message which the outbox journals in full.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.OutboxCommand.Enqueue")]
    internal sealed class Enqueue(OutboxMessage message, OutboxMessageState state)
    {
        [Id(0)] public OutboxMessage Message { get; } = message;
        [Id(1)] public OutboxMessageState State { get; } = state;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "enqueue";
    }

    /// <summary>Stages a message which an <see cref="IOutboxMessageSource"/> provides, journaling only its identity.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.OutboxCommand.EnqueueReference")]
    internal sealed class EnqueueReference(OutboxMessageReference reference, OutboxMessageState state)
    {
        [Id(0)] public OutboxMessageReference Reference { get; } = reference;
        [Id(1)] public OutboxMessageState State { get; } = state;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "enqueueReference";
    }

    [GenerateSerializer, Alias("Orleans.Messaging.OutboxCommand.Retry")]
    internal sealed class Retry(MessageId messageId, OutboxMessageState state)
    {
        [Id(0)] public MessageId MessageId { get; } = messageId;
        [Id(1)] public OutboxMessageState State { get; } = state;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "retry";
    }

    /// <summary>Removes a pending message, recording its dead letter when the outbox dead-lettered it.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.OutboxCommand.Retire")]
    internal sealed class Retire(MessageId messageId, OutboxDeadLetterRecord? deadLetter = null)
    {
        [Id(0)] public MessageId MessageId { get; } = messageId;
        [Id(1)] public OutboxDeadLetterRecord? DeadLetter { get; } = deadLetter;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "retire";
    }

    [GenerateSerializer, Alias("Orleans.Messaging.OutboxCommand.RemoveDeadLetters")]
    internal sealed class RemoveDeadLetters(MessageId[] keys)
    {
        [Id(0)] public MessageId[] Keys { get; } = keys;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "removeDeadLetters";
    }

    /// <summary>Replaces the outbox's state.</summary>
    [GenerateSerializer, Alias("Orleans.Messaging.OutboxCommand.Snapshot")]
    internal sealed class Snapshot(OutboxEntry[] messages, OutboxDeadLetterRecord[] deadLetters)
    {
        [Id(0)] public OutboxEntry[] Messages { get; } = messages;
        [Id(1)] public OutboxDeadLetterRecord[] DeadLetters { get; } = deadLetters;

        [JsonPropertyName("kind"), JsonPropertyOrder(-1)]
        public string Kind => "snapshot";
    }
}

/// <summary>Selects an <see cref="OutboxCommand"/> case from the <c>kind</c> which each JSON case leads with.</summary>
internal sealed class OutboxCommandClassifier : JsonTypeClassifierFactory
{
    public override bool CanClassify(JsonTypeClassifierContext context) => context.DeclaringType == typeof(OutboxCommand);

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
                    "enqueue" => typeof(OutboxCommand.Enqueue),
                    "enqueueReference" => typeof(OutboxCommand.EnqueueReference),
                    "retry" => typeof(OutboxCommand.Retry),
                    "retire" => typeof(OutboxCommand.Retire),
                    "removeDeadLetters" => typeof(OutboxCommand.RemoveDeadLetters),
                    "snapshot" => typeof(OutboxCommand.Snapshot),
                    _ => null
                };
            }

            lookahead.Skip();
        }

        return null;
    }
}

/// <summary>Identifies a message which an <see cref="IOutboxMessageSource"/> provides.</summary>
[GenerateSerializer, Alias("Orleans.Messaging.OutboxMessageReference")]
internal readonly struct OutboxMessageReference
{
    [Id(0)] public required MessageId MessageId { get; init; }
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

    /// <summary>
    /// Gets the source which owns a reference entry's message, resolved when it is sent or first materialized after
    /// recovery; <see langword="null"/> for an ordinary entry or a recovered reference which no source provides.
    /// </summary>
    [field: NonSerialized]
    [JsonIgnore] public IOutboxMessageSource? Source { get; set; }

    [JsonIgnore]
    public MessageId MessageId => Message is { } message ? message.Envelope.MessageId
        : Reference is { } reference ? reference.MessageId
        : throw new InvalidOperationException("An outbox entry requires its message or message reference.");
}

[JsonSerializable(typeof(OutboxCommand))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
