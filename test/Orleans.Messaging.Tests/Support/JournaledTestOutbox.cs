using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Orleans.Messaging.Tests.Support;

// Captures opaque handler output in the same journal as inbox effects. Dispatch belongs to the outbox layer.
internal sealed class JournaledTestOutbox(IDurableDictionary<HierarchicalKey, OutboxMessage> messages)
    : IOutbox, ILifecycleObserver, IEnumerable<KeyValuePair<HierarchicalKey, OutboxMessage>>
{
    private bool _stopped;
    private readonly TaskCompletionSource _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Stopping => _stopping.Task;
    public int SendCalls { get; private set; }
    public Exception? NextSendFailure { get; set; }
    public IDurableDictionary<HierarchicalKey, OutboxMessage> StoredMessages { get; } = messages;
    public int Count => StoredMessages.Count;
    public IEnumerable<OutboxMessage> Messages => StoredMessages.Values;
    public IEnumerator<KeyValuePair<HierarchicalKey, OutboxMessage>> GetEnumerator() => StoredMessages.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Send(OutboxMessage envelope)
    {
        SendCalls++;
        if (_stopped) throw new InvalidOperationException("The test outbox owner is stopped.");
        if (envelope.Envelope.MessageId.IsDefault || envelope.ReceiverId.IsDefault
            || envelope.Envelope.Payload is null)
        {
            throw new ArgumentException("An outgoing envelope requires identities and opaque payload bytes.", nameof(envelope));
        }
        if (TryGetMessage(envelope.Envelope.MessageId, out var existing))
        {
            if (existing.ReceiverId != envelope.ReceiverId
                || !string.Equals(TestApplicationProtocol.Subject(existing.Envelope),
                    TestApplicationProtocol.Subject(envelope.Envelope), StringComparison.Ordinal)
                || !existing.Envelope.Payload.AsSpan().SequenceEqual(envelope.Envelope.Payload))
            {
                throw new InvalidOperationException($"The outbox already contains a different envelope with message ID '{envelope.Envelope.MessageId}'.");
            }
            return;
        }
        if (NextSendFailure is { } failure)
        {
            NextSendFailure = null;
            throw failure;
        }
        StoredMessages.Add(envelope.Envelope.MessageId, envelope);
    }

    public bool TryGetMessage(HierarchicalKey messageId, [MaybeNullWhen(false)] out OutboxMessage envelope) =>
        StoredMessages.TryGetValue(messageId, out envelope);
    public Task OnStart(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnStop(CancellationToken cancellationToken)
    {
        _stopped = true;
        _stopping.TrySetResult();
        return Task.CompletedTask;
    }
}
